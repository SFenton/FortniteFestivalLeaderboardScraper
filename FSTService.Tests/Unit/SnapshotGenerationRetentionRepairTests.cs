using System.Diagnostics;
using FstSnapshotGenerationRetentionReport;
using FSTService.Persistence.Maintenance;
using FSTService.Tests.Helpers;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed partial class SnapshotGenerationRetentionPlannerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanonicalMigrationRefusesAnIncompatibleActiveOrStaleWorker(bool stale)
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        Execute("ALTER TABLE snapshot_generation_retention_cycles DROP CONSTRAINT ux_snapshot_generation_retention_cycle_trigger");
        Execute(stale
            ? "UPDATE service_worker_status SET instance_id='legacy-worker',status='offline',last_status_change_at=now()-interval '16 minutes'"
            : "UPDATE service_worker_status SET instance_id='legacy-worker',status='running'");
        var before = RetentionEvidenceSnapshot();
        var error = Assert.Throws<PostgresException>(() => Execute(SnapshotGenerationRetentionSchema.Sql));
        Assert.Equal("55000", error.SqlState);
        Assert.Equal(before, RetentionEvidenceSnapshot());
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM pg_constraint WHERE conname='ux_snapshot_generation_retention_cycle_trigger'"));
    }

    [Fact]
    public async Task OfflineMigrationPreservesTheLegacySameKindConflictTarget()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        await CreatePlanner().PlanAsync(CreateRequest());
        Execute("ALTER TABLE snapshot_generation_retention_cycles DROP CONSTRAINT ux_snapshot_generation_retention_cycle_trigger");
        Execute(SnapshotGenerationRetentionSchema.Sql);
        Execute("""
            INSERT INTO snapshot_generation_retention_cycles
            SELECT (jsonb_populate_record(NULL::snapshot_generation_retention_cycles,
                to_jsonb(cycle) || jsonb_build_object(
                    'cycle_id',nextval(pg_get_serial_sequence('snapshot_generation_retention_cycles','cycle_id'))))).*
            FROM snapshot_generation_retention_cycles cycle
            ON CONFLICT(trigger_scrape_id,trigger_publication_id,safe_point_kind) DO NOTHING;
            """);
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
    }

    [Fact]
    public async Task RepeatableReadFirstSnapshotFollowsEveryCanonicalAdmissionLock()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        Execute("CREATE TABLE offline_snapshot_order_probe(id INTEGER PRIMARY KEY)");
        var locksProved = false;
        var snapshotProved = false;
        var planner = CreatePlanner(oracle: new OfflineHookOracle(async (connection, transaction, ct) =>
        {
            Assert.True(locksProved);
            await using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "SHOW transaction_isolation";
            Assert.Equal("repeatable read", await query.ExecuteScalarAsync(ct));
            query.CommandText = "SELECT count(*) FROM offline_snapshot_order_probe WHERE id=1";
            Assert.Equal(1L, await query.ExecuteScalarAsync(ct));
            Execute("INSERT INTO offline_snapshot_order_probe VALUES(2)");
            query.CommandText = "SELECT count(*) FROM offline_snapshot_order_probe";
            Assert.Equal(1L, await query.ExecuteScalarAsync(ct));
            snapshotProved = true;
        }));
        planner.OfflineFenceAdmittedTestHook = async (connection, transaction, ct) =>
        {
            foreach (var name in new[] { "schema", "registration", "maintenance", "publication", "planner", "ddl" })
            {
                await using var query = connection.CreateCommand();
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT count(*) FROM pg_locks
                    WHERE pid=pg_backend_pid() AND locktype='advisory' AND granted
                      AND classid::BIGINT=((@key::BIGINT >> 32) & 4294967295)
                      AND objid::BIGINT=(@key::BIGINT & 4294967295)
                      AND objsubid=1 AND mode=@mode
                    """;
                query.Parameters.AddWithValue("key", OfflineTestLockKey(name));
                query.Parameters.AddWithValue("mode", name is "schema" or "publication" or "ddl"
                    ? "ShareLock" : "ExclusiveLock");
                Assert.True(await query.ExecuteScalarAsync(ct) is 1L,
                    $"Canonical {name} admission lock is not held by the fence backend.");
            }
            Execute("INSERT INTO offline_snapshot_order_probe VALUES(1)");
            locksProved = true;
        };
        var result = await planner.ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        Assert.True(snapshotProved);
        Assert.Equal(SnapshotGenerationRetentionPlanDisposition.Observed, result.Result.Disposition);
    }

    [Fact]
    public async Task OfflineThenWorkerUsesOneCanonicalCycleWithoutRewritingProvenance()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var planner = CreatePlanner();
        var offline = await planner.ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        var before = RetentionEvidenceSnapshot();
        var worker = await planner.PlanAsync(CreateRequest(
            broadcastScrapeId: null, backgroundQuiesced: false));
        Assert.Equal(offline.Cycle.CycleId, worker.CycleId);
        Assert.Equal(SnapshotGenerationRetentionPlanDisposition.Existing, worker.Disposition);
        Assert.Equal(before, RetentionEvidenceSnapshot());
        var canonical = await new SnapshotGenerationRetentionRepository(_fixture.DataSource)
            .GetCycleForTriggerAsync(CurrentScrapeId, CurrentPublicationId);
        Assert.Equal(SnapshotGenerationRetentionContract.OperatorOfflineSafePoint, canonical!.SafePointKind);
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
    }

    [Fact]
    public async Task CanonicalConstraintRejectsCrossKindLegacyInsert()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        await CreatePlanner().ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        var before = RetentionEvidenceSnapshot();
        var error = Assert.Throws<PostgresException>(() => DuplicateCurrentCycleAsWorker());
        Assert.Equal("23505", error.SqlState);
        Assert.Equal("ux_snapshot_generation_retention_cycle_trigger", error.ConstraintName);
        Assert.Equal(before, RetentionEvidenceSnapshot());
    }

    [Fact]
    public async Task UpgradeRefusesExistingCrossKindDuplicatesWithoutRewritingEvidence()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        await CreatePlanner().ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        Execute("ALTER TABLE snapshot_generation_retention_cycles DROP CONSTRAINT ux_snapshot_generation_retention_cycle_trigger");
        DuplicateCurrentCycleAsWorker();
        var before = RetentionEvidenceSnapshot();
        var error = Assert.Throws<PostgresException>(() => Execute(SnapshotGenerationRetentionSchema.Sql));
        Assert.Equal("55000", error.SqlState);
        Assert.Equal(before, RetentionEvidenceSnapshot());
    }

    [Fact]
    public async Task LegacyCycleAndDeferralConstraintsUpgradeIdempotently()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        await CreatePlanner().PlanAsync(CreateRequest());
        var before = RetentionEvidenceSnapshot();
        Execute("""
            ALTER TABLE snapshot_generation_retention_cycles
                DROP CONSTRAINT ux_snapshot_generation_retention_cycle_trigger;
            ALTER TABLE snapshot_generation_retention_cycles
                DROP CONSTRAINT ck_snapshot_generation_retention_cycle_safe_point;
            ALTER TABLE snapshot_generation_retention_cycles
                ADD CONSTRAINT ck_snapshot_generation_retention_cycle_safe_point
                CHECK(safe_point_kind='terminal_worker_post_publication');
            ALTER TABLE snapshot_generation_retention_deferrals
                DROP CONSTRAINT ck_snapshot_generation_retention_deferral_safe_point;
            ALTER TABLE snapshot_generation_retention_deferrals
                ADD CONSTRAINT ck_snapshot_generation_retention_deferral_safe_point
                CHECK(safe_point_kind='terminal_worker_post_publication');
            """);
        Execute(SnapshotGenerationRetentionSchema.Sql);
        var constraintOids = Scalar<string>("""
            SELECT string_agg(oid::TEXT,',' ORDER BY conname) FROM pg_constraint
            WHERE conname IN ('ux_snapshot_generation_retention_cycle_trigger',
                'ck_snapshot_generation_retention_cycle_safe_point',
                'ck_snapshot_generation_retention_deferral_safe_point')
            """);
        Execute(SnapshotGenerationRetentionSchema.Sql);
        Assert.Equal(constraintOids, Scalar<string>("""
            SELECT string_agg(oid::TEXT,',' ORDER BY conname) FROM pg_constraint
            WHERE conname IN ('ux_snapshot_generation_retention_cycle_trigger',
                'ck_snapshot_generation_retention_cycle_safe_point',
                'ck_snapshot_generation_retention_deferral_safe_point')
            """));
        Assert.Equal(before, RetentionEvidenceSnapshot());
        await new SnapshotGenerationRetentionRepository(_fixture.DataSource).RecordDeferralAsync(
            CreateRequest() with { SafePointKind = SnapshotGenerationRetentionContract.OperatorOfflineSafePoint },
            "test_busy", "fixture deferral", true, new { fixture = true });
        var deferrals = await new SnapshotGenerationRetentionRepository(_fixture.DataSource)
            .GetDeferralsAsync(CurrentPublicationId);
        Assert.Equal(SnapshotGenerationRetentionContract.OperatorOfflineSafePoint,
            Assert.Single(deferrals).SafePointKind);
    }

    [Theory]
    [InlineData("cycles", "ck_snapshot_generation_retention_cycle_safe_point")]
    [InlineData("deferrals", "ck_snapshot_generation_retention_deferral_safe_point")]
    public async Task SchemaAttestationRejectsDecorativeKindChecks(string table, string constraint)
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        if (table == "cycles")
            Execute("""
                ALTER TABLE snapshot_generation_retention_cycles DROP CONSTRAINT ck_snapshot_generation_retention_cycle_safe_point;
                ALTER TABLE snapshot_generation_retention_cycles ADD CONSTRAINT ck_snapshot_generation_retention_cycle_safe_point
                CHECK(safe_point_kind IN ('terminal_worker_post_publication','operator_offline_post_publication') OR TRUE);
                """);
        else
            Execute("""
                ALTER TABLE snapshot_generation_retention_deferrals DROP CONSTRAINT ck_snapshot_generation_retention_deferral_safe_point;
                ALTER TABLE snapshot_generation_retention_deferrals ADD CONSTRAINT ck_snapshot_generation_retention_deferral_safe_point
                CHECK(safe_point_kind IN ('terminal_worker_post_publication','operator_offline_post_publication') OR TRUE);
                """);
        Assert.False((await CaptureOfflineRuntimeIdentityAsync()).RequiredSchemaAccepted);
        Assert.NotEmpty(constraint);
    }

    [Fact]
    public async Task DeployedDisabledConfigurationCannotBeOverriddenByLocalPlannerEnablement()
    {
        SeedOfflineBaselineConfiguration(false, ("Solo_Guitar", 1307));
        var identity = await CaptureOfflineRuntimeIdentityAsync();
        Assert.False(identity.WorkerConfiguration!.ReportOnlyEnabled);
        var error = await Assert.ThrowsAsync<OfflineReportRefusal>(() =>
            CreatePlanner(enabled: true).ObserveCurrentOfflineAsync(new OfflineReportAttestation(
                new OfflineFixtureCode(), OfflineReportCommandTests.Assertions(identity))));
        Assert.Equal("report_only_configuration_disabled", error.Code);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
    }

    [Fact]
    public async Task MissingLegacyWorkerConfigurationRefusesOfflineReporting()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        Execute("UPDATE service_worker_status SET instance_id='legacy-worker-without-receipt'");
        var error = await Assert.ThrowsAsync<SnapshotGenerationRetentionOfflineRefusal>(() =>
            CreatePlanner().ObserveCurrentOfflineAsync(new OfflineTestAttestation()));
        Assert.Equal("report_only_configuration_missing", error.Code);
    }

    [Fact]
    public async Task WorkerConfigurationIsImmutableAndHashBoundToTheInstance()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var identity = await CaptureOfflineRuntimeIdentityAsync();
        Assert.True(identity.WorkerConfiguration!.IsAuthentic);
        Assert.Equal(2, identity.WorkerConfiguration.CanonicalCycleIdentityVersion);
        var error = Assert.Throws<PostgresException>(() => Execute(
            "UPDATE snapshot_generation_retention_worker_configuration SET report_only_enabled=FALSE"));
        Assert.Equal("55000", error.SqlState);
        var wrong = OfflineReportCommandTests.Assertions(identity) with { WorkerConfigurationSha256 = new('f', 64) };
        var refused = await Assert.ThrowsAsync<OfflineReportRefusal>(() =>
            CreatePlanner().ObserveCurrentOfflineAsync(new OfflineReportAttestation(new OfflineFixtureCode(), wrong)));
        Assert.Equal("worker_configuration_identity_mismatch", refused.Code);
    }

    [Fact]
    public async Task ObservationBudgetExceededRollsBackAndReportsPhaseTimings()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var planner = CreatePlanner(oracle: new BlockingOracle());
        planner.OfflineObservationBudget = TimeSpan.FromMilliseconds(250);
        var before = OfflineSourceSnapshot();
        var failure = await Assert.ThrowsAsync<SnapshotGenerationRetentionOfflineRefusal>(() =>
            planner.ObserveCurrentOfflineAsync(new OfflineTestAttestation()));
        Assert.Equal("observation_budget_exceeded", failure.Code);
        Assert.True(failure.ElapsedMilliseconds > 0);
        Assert.NotEmpty(failure.Timings);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
        Assert.Equal(before, OfflineSourceSnapshot());
        Assert.Equal(120, SnapshotGenerationRetentionPlanner.OfflineTotalTimeoutSeconds);
    }

    [Fact]
    public async Task PublicationConvoyEndsWhenTheBoundedFenceTransactionExpires()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var planner = CreatePlanner(oracle: new OfflineHookOracle(async (_, _, _) =>
        {
            entered.TrySetResult();
            await Task.Delay(2500);
        }));
        planner.OfflineFenceTransactionTimeoutSeconds = 1;
        var report = planner.ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var writer = await _fixture.DataSource.OpenConnectionAsync();
        await using var exclusive = writer.CreateCommand();
        exclusive.CommandTimeout = 5;
        exclusive.CommandText = "SELECT pg_advisory_xact_lock(@key)";
        exclusive.Parameters.AddWithValue("key", OfflineTestLockKey("publication"));
        var waitingWriter = exclusive.ExecuteNonQueryAsync();
        var deadline = Stopwatch.StartNew();
        while (Scalar<long>("SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND NOT granted AND mode='ExclusiveLock' AND database=(SELECT oid FROM pg_database WHERE datname=current_database())") == 0)
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(3));
            await Task.Delay(10);
        }
        await using var reader = await _fixture.DataSource.OpenConnectionAsync();
        await using var shared = reader.CreateCommand();
        shared.CommandTimeout = 5;
        shared.CommandText = "SELECT pg_advisory_xact_lock_shared(@key)";
        shared.Parameters.AddWithValue("key", OfflineTestLockKey("publication"));
        await shared.ExecuteNonQueryAsync();
        await waitingWriter;
        Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(4));
        await Assert.ThrowsAnyAsync<Exception>(() => report);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
    }

    [Fact]
    public async Task SchemaAdmissionIsDatabaseScopedAndSameDatabaseRefusalIsBounded()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        await using var blocker = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using var acquire = blocker.CreateCommand();
        acquire.Transaction = transaction;
        acquire.CommandText = "SELECT pg_advisory_xact_lock_shared(hashtextextended('fst.snapshot-generation-retention-schema',0))";
        await acquire.ExecuteNonQueryAsync();
        var stopwatch = Stopwatch.StartNew();
        var same = Assert.Throws<PostgresException>(() => Execute(SnapshotGenerationRetentionSchema.Sql));
        Assert.Equal("55P03", same.SqlState);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
        using var other = SharedPostgresContainer.CreateDatabase();
        await using var otherConnection = await other.OpenConnectionAsync();
        await using var command = otherConnection.CreateCommand();
        command.CommandText = SnapshotGenerationRetentionSchema.Sql;
        await command.ExecuteNonQueryAsync();
        Assert.NotEqual(_fixture.DataSource.ConnectionString, other.ConnectionString);
    }

    [Fact]
    public async Task CommittedCycleSurvivesVerifiedPostCommitDisposalFailure()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var planner = CreatePlanner();
        planner.OfflinePostCommitCleanupTestHook = () => throw new IOException("injected cleanup failure");
        var result = await planner.ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        Assert.Equal(SnapshotGenerationRetentionPlanDisposition.Observed, result.Result.Disposition);
        Assert.Contains("committed_cycle_cleanup_warning_verified", result.Warnings);
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
    }

    [Fact]
    public async Task CommittedCycleDataSourceDisposalFailureRequiresAuthoritativeConfirmation()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var result = await CreatePlanner().ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        await using var database = new OfflineReportDatabase(SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource));
        database.DisposeTestHook = () => throw new IOException("injected source disposal");
        var confirmed = await database.CompleteAndDisposeAsync(result);
        Assert.Contains("committed_cycle_cleanup_warning_verified", confirmed.Warnings);
    }

    [Fact]
    public async Task CleanupWarningCannotClaimSuccessWhileAnOwnedTransactionRemains()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var result = await CreatePlanner().ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pid,backend_start,xact_start FROM pg_stat_activity WHERE pid=pg_backend_pid()";
        SnapshotGenerationRetentionTransactionOwner owner;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            owner = new(reader.GetInt32(0), reader.GetDateTime(1), reader.GetDateTime(2));
        }
        var pending = result with { Completion = result.Completion! with { Owners = [owner] } };
        Assert.Null(await SnapshotGenerationRetentionPlanner.ConfirmCommittedAfterCleanupAsync(
            new FSTService.Persistence.PostgresUnpooledConnectionFactory(SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource)), pending));
        await transaction.CommitAsync();
        Assert.NotNull(await SnapshotGenerationRetentionPlanner.ConfirmCommittedAfterCleanupAsync(
            new FSTService.Persistence.PostgresUnpooledConnectionFactory(SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource)), pending));
        Assert.Null(await SnapshotGenerationRetentionPlanner.ConfirmCommittedAfterCleanupAsync(
            new FSTService.Persistence.PostgresUnpooledConnectionFactory(SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource)),
            result with { Cycle = result.Cycle with { CandidateIdentityHash = new('f', 64) } }));
    }

    private void DuplicateCurrentCycleAsWorker() => Execute("""
        INSERT INTO snapshot_generation_retention_cycles
        SELECT (jsonb_populate_record(NULL::snapshot_generation_retention_cycles,
            to_jsonb(cycle) || jsonb_build_object(
                'cycle_id',nextval(pg_get_serial_sequence('snapshot_generation_retention_cycles','cycle_id')),
                'safe_point_kind','terminal_worker_post_publication'))).*
        FROM snapshot_generation_retention_cycles cycle
        ORDER BY cycle_id DESC LIMIT 1
        ON CONFLICT(trigger_scrape_id,trigger_publication_id,safe_point_kind) DO NOTHING;
        """);
}
