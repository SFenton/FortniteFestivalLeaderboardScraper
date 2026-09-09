using System.Diagnostics;
using FSTService.Persistence.Maintenance;
using FSTService.Tests.Helpers;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed partial class SnapshotGenerationRetentionPlannerTests
{
    [Fact]
    public async Task OfflineReportUsesRealOracleAndPreservesEverySourceRow()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        RejectOfflineSourceMutations();
        var before = OfflineSourceSnapshot();
        var oracle = new TransactionRecordingOracle();
        var report = await CreatePlanner(oracle: oracle)
            .ObserveCurrentOfflineAsync(new OfflineTestAttestation());

        Assert.Equal(SnapshotGenerationRetentionPlanDisposition.Observed, report.Result.Disposition);
        Assert.Equal(SnapshotGenerationRetentionContract.OperatorOfflineSafePoint, report.Cycle.SafePointKind);
        Assert.Equal(3, report.Cycle.PlannerVersion);
        Assert.Equal(1, report.Cycle.ConfigVersion);
        Assert.True(report.Cycle.OracleAgreement);
        Assert.Equal(1, report.Cycle.CandidateCount);
        Assert.Equal(0, report.Cycle.BlockedCount);
        Assert.Equal("[]", report.Cycle.GlobalBlockersJson);
        Assert.Equal(report.Cycle.PlannerChildSetJson, report.Cycle.OracleChildSetJson);
        Assert.Equal(report.Cycle.PlannerLiveSetJson, report.Cycle.OracleLiveSetJson);
        Assert.Equal(report.Cycle.PlannerCandidateSetJson, report.Cycle.OracleCandidateSetJson);
        Assert.Equal(1, oracle.InvocationCount);
        Assert.Equal("repeatable read", oracle.IsolationLevel);
        Assert.Equal("off", oracle.ReadOnly);
        Assert.Equal("15s", oracle.StatementTimeout);
        Assert.Equal("2s", oracle.LockTimeout);
        Assert.Equal("20s", oracle.IdleTimeout);
        Assert.Equal(before, OfflineSourceSnapshot());
        Assert.Equal(2, ReadEvidence(report.Cycle.CycleId).Count);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retirement_jobs"));
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_deferrals"));
    }

    [Fact]
    public async Task OfflineReportRevalidatesAndReusesItsCurrentCycle()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var oracle = new TransactionRecordingOracle();
        var planner = CreatePlanner(oracle: oracle);
        var first = await planner.ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        var second = await planner.ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        Assert.Equal(first.Cycle.CycleId, second.Cycle.CycleId);
        Assert.Equal(SnapshotGenerationRetentionPlanDisposition.Existing, second.Result.Disposition);
        Assert.Equal(2, oracle.InvocationCount);
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
    }

    [Fact]
    public async Task OfflineReportRevalidatesWorkerCycleWithoutInventingAnotherSafePoint()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var planner = CreatePlanner();
        var worker = await planner.PlanAsync(CreateRequest());
        var offline = await planner.ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        Assert.Equal(worker.CycleId, offline.Cycle.CycleId);
        Assert.Equal(SnapshotGenerationRetentionContract.TerminalWorkerSafePoint, offline.Cycle.SafePointKind);
        Assert.Equal(SnapshotGenerationRetentionPlanDisposition.Existing, offline.Result.Disposition);
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
    }

    [Fact]
    public async Task OfflineReportRejectsCurrentCycleThatIsNotTheNewestCycle()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var first = await CreatePlanner()
            .ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        Execute("""
            INSERT INTO publication_generations (
                publication_id,scrape_id,status,created_at,ready_at,published_at)
            VALUES (
                9001,1307,'retired',now()-interval '1 day',
                now()-interval '23 hours',now()-interval '23 hours');
            INSERT INTO snapshot_generation_retention_cycles (
                trigger_scrape_id,trigger_publication_id,safe_point_kind,safe_point_at,
                planner_version,config_version,report_only,status,oracle_agreement,
                candidate_identity_hash,observation_hash,
                planner_child_set,planner_live_set,planner_candidate_set,
                oracle_child_set,oracle_live_set,oracle_candidate_set,
                candidate_count,protected_count,blocked_count,candidate_bytes,
                global_blockers,anomalies,error_message,created_at)
            SELECT
                1307,9001,safe_point_kind,safe_point_at,
                planner_version,config_version,report_only,status,oracle_agreement,
                candidate_identity_hash,observation_hash,
                planner_child_set,planner_live_set,planner_candidate_set,
                oracle_child_set,oracle_live_set,oracle_candidate_set,
                candidate_count,protected_count,blocked_count,candidate_bytes,
                global_blockers,anomalies,error_message,clock_timestamp()+interval '1 second'
            FROM snapshot_generation_retention_cycles
            WHERE cycle_id=@cycleId
            """, command => command.Parameters.AddWithValue(
                "cycleId",
                first.Cycle.CycleId));

        var error = await Assert.ThrowsAsync<
            SnapshotGenerationRetentionOfflineRefusal>(() =>
            CreatePlanner().ObserveCurrentOfflineAsync(
                new OfflineTestAttestation()));

        Assert.Equal("current_cycle_not_newest", error.Code);
        Assert.Equal(
            2,
            Scalar<long>(
                "SELECT count(*) FROM snapshot_generation_retention_cycles"));
    }

    [Fact]
    public async Task OfflineFenceRejectsDifferentDatabaseIdentity()
    {
        await using var primary = await _fixture.DataSource.OpenConnectionAsync();
        await using var primaryTransaction =
            await primary.BeginTransactionAsync();
        var signature =
            await SnapshotGenerationRetentionPlanner
                .CaptureOfflineDatabaseSignatureAsync(
                    primary,
                    primaryTransaction,
                    CancellationToken.None);
        using var other = SharedPostgresContainer.CreateDatabase();
        await using var otherConnection = await other.OpenConnectionAsync();
        await using var otherTransaction =
            await otherConnection.BeginTransactionAsync();

        var error = await Assert.ThrowsAsync<
            SnapshotGenerationRetentionOfflineRefusal>(() =>
            SnapshotGenerationRetentionPlanner
                .RequireOfflineDatabaseSignatureAsync(
                    otherConnection,
                    otherTransaction,
                    signature,
                    CancellationToken.None));

        Assert.Equal("offline_database_identity_changed", error.Code);
    }

    [Fact]
    public async Task WorkerAdmissionCannotClaimTheOfflineSafePoint()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        await Assert.ThrowsAsync<ArgumentException>(() => CreatePlanner().PlanAsync(
            CreateRequest() with
            {
                SafePointKind = SnapshotGenerationRetentionContract.OperatorOfflineSafePoint,
            }));
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
    }

    [Fact]
    public async Task OfflineSafePointSchemaUpgradePreservesExistingCycleAndHashChain()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        await CreatePlanner().PlanAsync(CreateRequest());
        var before = RetentionEvidenceSnapshot();
        var source = OfflineSourceSnapshot();
        Execute("""
            ALTER TABLE snapshot_generation_retention_cycles
                DROP CONSTRAINT ck_snapshot_generation_retention_cycle_safe_point;
            ALTER TABLE snapshot_generation_retention_cycles
                ADD CONSTRAINT ck_snapshot_generation_retention_cycle_safe_point
                CHECK (safe_point_kind='terminal_worker_post_publication');
            """);
        Execute(SnapshotGenerationRetentionSchema.Sql);
        Assert.Equal(before, RetentionEvidenceSnapshot());
        Assert.Equal(source, OfflineSourceSnapshot());
        Assert.Contains("operator_offline_post_publication", Scalar<string>("""
            SELECT pg_get_constraintdef(oid) FROM pg_constraint
            WHERE conrelid='snapshot_generation_retention_cycles'::regclass
              AND conname='ck_snapshot_generation_retention_cycle_safe_point'
            """));
    }

    [Fact]
    public async Task OfflineObservationDisablesSilentRowSecurityFiltering()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var oracle = new OfflineHookOracle(async (connection, transaction, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SHOW row_security";
            Assert.Equal("off", await command.ExecuteScalarAsync(ct));
        });
        var report = await CreatePlanner(oracle: oracle)
            .ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        Assert.True(report.Cycle.OracleAgreement);
    }

    [Fact]
    public async Task OfflineReportRejectsChangedCurrentObservationRatherThanDuplicatingIt()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var planner = CreatePlanner();
        await planner.ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        Execute("""
            INSERT INTO snapshot_generation_retention_holds
                (instrument, snapshot_id, hold_kind, reason, created_by)
            VALUES ('Solo_Guitar', 1307, 'restore_in_flight', 'test hold', 'test');
            """);
        var error = await Assert.ThrowsAsync<SnapshotGenerationRetentionOfflineRefusal>(() =>
            planner.ObserveCurrentOfflineAsync(new OfflineTestAttestation()));
        Assert.Equal("current_cycle_observation_changed", error.Code);
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
    }

    [Fact]
    public async Task OfflineReportRejectsDisabledPlanner()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var error = await Assert.ThrowsAsync<SnapshotGenerationRetentionOfflineRefusal>(() =>
            CreatePlanner(enabled: false)
                .ObserveCurrentOfflineAsync(new OfflineTestAttestation()));
        Assert.Equal("report_only_planner_disabled", error.Code);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
    }

    public static IEnumerable<object[]> OfflineAdmissionRefusals()
    {
        yield return ["UPDATE scrape_log SET status='failed' WHERE id=2000", "newest_scrape_not_completed"];
        yield return ["UPDATE scrape_log SET completed_at=NULL WHERE id=2000", "newest_scrape_not_completed"];
        yield return ["UPDATE scrape_log SET status='running' WHERE id=1307", "scrape_running"];
        yield return ["UPDATE scrape_publication_state SET published_scrape_id=1307", "publication_binding_mismatch"];
        yield return ["UPDATE scrape_publication_state SET current_publication_id=NULL", "publication_binding_mismatch"];
        yield return ["UPDATE publication_generations SET status='ready' WHERE publication_id=9000", "publication_binding_mismatch"];
        yield return ["UPDATE scrape_publication_state SET public_reads_frozen=TRUE", "public_reads_frozen"];
        yield return ["UPDATE scrape_publication_state SET working_publication_id=9000", "publication_mutation_pending"];
        yield return ["UPDATE scrape_publication_state SET publication_commit_intent_owner='test'", "publication_mutation_pending"];
        yield return ["UPDATE scrape_publication_state SET publication_commit_intent_heartbeat_at=now()", "publication_mutation_pending"];
        yield return ["UPDATE scrape_publication_state SET max_score_mutation_gate_backend_pid=99", "publication_mutation_pending"];
        yield return ["UPDATE service_worker_status SET status='running'", "scraper_not_offline"];
        yield return ["UPDATE service_worker_status SET status='stopping'", "scraper_not_offline"];
        yield return ["UPDATE service_worker_status SET status='starting'", "scraper_not_offline"];
        yield return ["UPDATE service_worker_status SET mode='registration'", "scraper_not_offline"];
        yield return ["DELETE FROM service_worker_status", "scraper_not_offline"];
        yield return ["UPDATE service_worker_status SET current_operation_json='{}'::jsonb", "worker_operation_present"];
        yield return ["UPDATE service_worker_status SET current_operation_json='null'::jsonb", "worker_operation_present"];
        yield return ["UPDATE service_worker_status SET instance_id=NULL", "offline_worker_identity_incomplete"];
        yield return ["UPDATE service_worker_status SET instance_id=' '", "offline_worker_identity_incomplete"];
        yield return ["UPDATE service_worker_status SET started_at=NULL", "offline_worker_identity_incomplete"];
        yield return ["UPDATE service_worker_status SET last_heartbeat_at=NULL", "offline_worker_identity_incomplete"];
        yield return ["UPDATE service_worker_status SET started_at=now()+interval '1 hour'", "offline_worker_boundary_stale_or_inconsistent"];
        yield return ["UPDATE service_worker_status SET last_status_change_at=now()-interval '16 minutes'", "offline_worker_boundary_stale_or_inconsistent"];
        yield return ["UPDATE service_worker_status SET last_heartbeat_at=now()+interval '1 hour',updated_at=now()+interval '1 hour'", "offline_worker_boundary_stale_or_inconsistent"];
        yield return ["UPDATE scrape_publication_state SET published_at=now()+interval '1 minute'", "offline_worker_boundary_stale_or_inconsistent"];
        yield return ["UPDATE scrape_publication_state SET improvement_notifications_status='pending',improvement_notifications_scrape_id=2000,improvement_notifications_projection_ready=TRUE,improvement_notifications_projection_scrape_id=2000", "notifications_not_terminal"];
        yield return ["UPDATE scrape_publication_state SET improvement_notifications_status='failed',improvement_notifications_scrape_id=2000,improvement_notifications_projection_ready=TRUE,improvement_notifications_projection_scrape_id=2000", "notifications_not_terminal"];
        yield return ["UPDATE scrape_publication_state SET improvement_notifications_status='completed',improvement_notifications_scrape_id=2000,improvement_notifications_projection_ready=TRUE,improvement_notifications_projection_scrape_id=2000", "notifications_not_terminal"];
        yield return ["UPDATE scrape_publication_state SET improvement_notifications_status=NULL,improvement_notifications_scrape_id=2000", "notifications_not_terminal"];
        yield return ["UPDATE scrape_publication_state SET improvement_notifications_status=NULL,improvement_notifications_projection_ready=TRUE", "notifications_not_terminal"];
        yield return ["UPDATE scrape_publication_state SET improvement_notifications_completed_at=now()-interval '30 seconds'", "notifications_not_terminal"];
    }

    [Theory]
    [MemberData(nameof(OfflineAdmissionRefusals))]
    public async Task OfflineAdmissionRefusesWithoutPersistingOrRepairing(string mutation, string expected)
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        Execute(mutation);
        var before = OfflineSourceSnapshot();
        var error = await Assert.ThrowsAsync<SnapshotGenerationRetentionOfflineRefusal>(() =>
            CreatePlanner().ObserveCurrentOfflineAsync(new OfflineTestAttestation()));
        Assert.Equal(expected, error.Code);
        Assert.Equal(before, OfflineSourceSnapshot());
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_deferrals"));
    }

    [Fact]
    public async Task OfflineReportAcceptsExactCompletedNotificationState()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        Execute("""
            UPDATE scrape_publication_state
            SET improvement_notifications_status='completed',
                improvement_notifications_scrape_id=2000,
                improvement_notifications_completed_at=now()-interval '30 seconds',
                improvement_notifications_projection_ready=TRUE,
                improvement_notifications_projection_scrape_id=2000;
            """);
        var result = await CreatePlanner().ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        Assert.Equal(SnapshotGenerationRetentionPlanDisposition.Observed, result.Result.Disposition);
    }

    [Fact]
    public void AcceptedWorkerSchemaCannotOmitItsStopTransitionTimestamp()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var error = Assert.Throws<PostgresException>(() =>
            Execute("UPDATE service_worker_status SET last_status_change_at=NULL"));
        Assert.Equal("23502", error.SqlState);
    }

    [Fact]
    public async Task OfflineReportPreservesSoloBass1308WriterFailureProtection()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307), ("Solo_Bass", 1308));
        Execute("""
            INSERT INTO scrape_writer_failures (
                scrape_id,writer_kind,instrument,song_id,page_count,row_count,
                exception_type,error_message,occurred_at)
            VALUES (1308,'online','Solo_Bass','failed-song',1,0,
                'InjectedFailure','retained evidence',now());
            """);
        var result = await CreatePlanner().ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        var observations = await ObservationsAsync(result.Result);
        Assert.Equal(SnapshotGenerationRetentionPlanDisposition.Observed, result.Result.Disposition);
        Assert.Equal(1, result.Result.CandidateCount);
        Assert.Equal(1, result.Result.ProtectedCount);
        Assert.Contains("unreplayed_writer_failure", ByInstrument(observations, "Solo_Bass").RootReasons);
        Assert.Equal(SnapshotGenerationRetentionClassification.Protected,
            ByInstrument(observations, "Solo_Bass").Classification);
    }

    [Fact]
    public async Task OfflineReportPersistsRealOracleMismatchWithZeroCandidates()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var result = await CreatePlanner(oracle: new TransformingOracle(
                value => value with { LiveKeys = value.ChildKeys }))
            .ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        Assert.Equal(SnapshotGenerationRetentionPlanDisposition.OracleMismatch, result.Result.Disposition);
        Assert.False(result.Cycle.OracleAgreement);
        Assert.Equal(0, result.Cycle.CandidateCount);
        Assert.Contains("liveness_oracle_mismatch", result.Cycle.GlobalBlockersJson);
        var error = await Assert.ThrowsAsync<SnapshotGenerationRetentionOfflineRefusal>(() =>
            CreatePlanner().ObserveCurrentOfflineAsync(new OfflineTestAttestation()));
        Assert.Equal("current_cycle_not_accepted", error.Code);
    }

    [Fact]
    public async Task OfflineReportPersistsNormalFailedCycleForOracleFailure()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var before = OfflineSourceSnapshot();
        var result = await CreatePlanner(oracle: new ThrowingOracle())
            .ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        Assert.Equal(SnapshotGenerationRetentionPlanDisposition.Failed, result.Result.Disposition);
        Assert.Equal("failed", result.Cycle.Status);
        Assert.Equal(0, result.Cycle.CandidateCount);
        Assert.Contains("planner_exception", result.Cycle.GlobalBlockersJson);
        Assert.DoesNotContain("injected oracle failure", result.Cycle.ErrorMessage!);
        Assert.Equal(before, OfflineSourceSnapshot());
    }

    [Fact]
    public async Task OfflineReportPersistsRealGlobalBlockerWithoutAcceptingCandidates()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        SetCurrentPublicationSourceBinding(expectedCount: 2, keyHash: new string('a', 64));
        var result = await CreatePlanner().ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        Assert.Equal(SnapshotGenerationRetentionPlanDisposition.Blocked, result.Result.Disposition);
        Assert.Equal(0, result.Cycle.CandidateCount);
        Assert.Contains("named_publication_source_set_invalid", result.Cycle.GlobalBlockersJson);
    }

    [Theory]
    [InlineData("schema", "retention_schema_lock_busy")]
    [InlineData("registration", "registration_mutation_lock_busy")]
    [InlineData("maintenance", "service_maintenance_lock_busy")]
    [InlineData("publication", "publication_lock_busy")]
    [InlineData("planner", "retention_planner_lock_busy")]
    [InlineData("ddl", "snapshot_partition_ddl_lock_busy")]
    public async Task OfflineReportLockContentionIsBoundedWithoutPublicationWaiters(string name, string code)
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var key = OfflineTestLockKey(name);
        await using var blocker = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = blocker.CreateCommand();
        command.CommandText = "SELECT pg_advisory_lock(@key)";
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync();
        var timer = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<SnapshotGenerationRetentionOfflineRefusal>(() =>
            CreatePlanner().ObserveCurrentOfflineAsync(new OfflineTestAttestation()));
        Assert.Equal(code, error.Code);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND NOT granted AND database=(SELECT oid FROM pg_database WHERE datname=current_database())"));
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
        command.CommandText = "SELECT pg_advisory_unlock(@key)";
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task OfflineReportCancellationRollsBackAndReleasesItsLocks()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var before = OfflineSourceSnapshot();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreatePlanner(oracle: new BlockingOracle()).ObserveCurrentOfflineAsync(
                new OfflineTestAttestation(), cancellation.Token));
        Assert.Equal(before, OfflineSourceSnapshot());
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
        foreach (var name in new[] { "schema", "registration", "maintenance", "publication", "planner", "ddl" })
        {
            using var connection = _fixture.DataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(@key)";
            command.Parameters.AddWithValue("key", OfflineTestLockKey(name));
            Assert.True((bool)command.ExecuteScalar()!);
            command.CommandText = "SELECT pg_advisory_unlock(@key)";
            Assert.True((bool)command.ExecuteScalar()!);
        }
    }

    [Fact]
    public async Task OfflineBoundaryCannotUseAWorkerSnapshotTakenBeforeAdmissionLocks()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var attestation = new OfflineTestAttestation((call, _) =>
        {
            if (call == 1)
                Execute("UPDATE service_worker_status SET status='running'");
            return Task.CompletedTask;
        });
        var error = await Assert.ThrowsAsync<SnapshotGenerationRetentionOfflineRefusal>(() =>
            CreatePlanner().ObserveCurrentOfflineAsync(attestation));
        Assert.Equal("scraper_not_offline", error.Code);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
    }

    [Theory]
    [InlineData("UPDATE scrape_publication_state SET public_reads_frozen=TRUE")]
    [InlineData("UPDATE service_worker_status SET status='running'")]
    [InlineData("INSERT INTO snapshot_generation_retention_holds (instrument,snapshot_id,hold_kind,reason,created_by) VALUES ('Solo_Guitar',1307,'restore_in_flight','race','test')")]
    public async Task OfflineObservationKeepsMutableAdmissionSurfacesLockedThroughPersistence(string mutation)
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var before = OfflineSourceSnapshot();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oracle = new OfflineHookOracle(async (_, _, ct) =>
        {
            held.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        var running = CreatePlanner(oracle: oracle)
            .ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var connection = await _fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SET lock_timeout='200ms'; " + mutation;
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal("55P03", error.SqlState);
        }
        finally
        {
            release.TrySetResult();
        }
        var report = await running;
        Assert.Equal(SnapshotGenerationRetentionPlanDisposition.Observed, report.Result.Disposition);
        Assert.Equal(before, OfflineSourceSnapshot());
    }

    private void SeedOfflineBaseline(params (string Instrument, long SnapshotId)[] children)
        => SeedOfflineBaselineConfiguration(true, children);

    private void SeedOfflineBaselineConfiguration(
        bool reportOnlyEnabled,
        params (string Instrument, long SnapshotId)[] children)
    {
        SeedBaseline(children);
        Execute("""
            UPDATE publication_generations
            SET created_at=now()-interval '3 minutes',ready_at=now()-interval '2 minutes',
                published_at=now()-interval '2 minutes'
            WHERE publication_id=9000;
            UPDATE scrape_publication_state SET published_at=now()-interval '2 minutes';
            INSERT INTO service_worker_status (
                worker_key,status,mode,instance_id,started_at,last_status_change_at,
                last_heartbeat_at,current_operation_json,updated_at)
            VALUES ('scraper','running','scraper','offline-test-worker',
                now()-interval '1 hour',now()-interval '1 second',
                now()-interval '1 second',NULL,now()-interval '1 second');
            """);
        new SnapshotGenerationRetentionWorkerConfigurationStore(_fixture.DataSource)
            .PublishFromWorkerAsync("offline-test-worker", reportOnlyEnabled, new string('a', 64))
            .GetAwaiter().GetResult();
        Execute("""
            UPDATE service_worker_status
            SET status='offline',last_status_change_at=now(),
                last_heartbeat_at=now(),updated_at=now();
            """);
    }

    private string OfflineSourceSnapshot() => Scalar<string>("""
        SELECT jsonb_build_object(
            'scrapes',(SELECT jsonb_agg(to_jsonb(s) ORDER BY id) FROM scrape_log s),
            'publication',(SELECT to_jsonb(s) FROM scrape_publication_state s WHERE id),
            'generations',(SELECT jsonb_agg(to_jsonb(s) ORDER BY publication_id) FROM publication_generations s),
            'workers',(SELECT jsonb_agg(to_jsonb(s) ORDER BY worker_key) FROM service_worker_status s),
            'children',(SELECT jsonb_agg(jsonb_build_object(
                'name',c.relname,'oid',c.oid,'relfilenode',c.relfilenode,
                'bytes',pg_total_relation_size(c.oid)) ORDER BY c.relname)
                FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='public' AND c.relkind='r'
                  AND c.relname ~ '^leaderboard_entries_snapshot_[a-z0-9_]+_s[1-9][0-9]*$')
        )::TEXT
        """);

    private string RetentionEvidenceSnapshot() => Scalar<string>("""
        SELECT jsonb_build_object(
            'cycles',(SELECT jsonb_agg(to_jsonb(item) ORDER BY cycle_id)
                FROM snapshot_generation_retention_cycles item),
            'observations',(SELECT jsonb_agg(to_jsonb(item) ORDER BY observation_id)
                FROM snapshot_generation_retention_observations item),
            'evidence',(SELECT jsonb_agg(to_jsonb(item) ORDER BY evidence_id)
                FROM snapshot_generation_retention_evidence item)
        )::TEXT
        """);

    private void RejectOfflineSourceMutations() => Execute("""
        CREATE FUNCTION offline_test_reject_source_mutation() RETURNS trigger
        LANGUAGE plpgsql AS $body$ BEGIN RAISE EXCEPTION 'offline source mutation forbidden'; END $body$;
        CREATE TRIGGER offline_source_guard BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
            ON scrape_log FOR EACH STATEMENT EXECUTE FUNCTION offline_test_reject_source_mutation();
        CREATE TRIGGER offline_source_guard BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
            ON scrape_publication_state FOR EACH STATEMENT EXECUTE FUNCTION offline_test_reject_source_mutation();
        CREATE TRIGGER offline_source_guard BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
            ON publication_generations FOR EACH STATEMENT EXECUTE FUNCTION offline_test_reject_source_mutation();
        CREATE TRIGGER offline_source_guard BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
            ON service_worker_status FOR EACH STATEMENT EXECUTE FUNCTION offline_test_reject_source_mutation();
        CREATE TRIGGER offline_source_guard BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
            ON leaderboard_entries_snapshot FOR EACH STATEMENT EXECUTE FUNCTION offline_test_reject_source_mutation();
        """);

    private long OfflineTestLockKey(string name) => name switch
    {
        "schema" => Scalar<long>("SELECT hashtextextended('fst.snapshot-generation-retention-schema',0)"),
        "registration" => FSTService.Persistence.RegistrationMutationGate.AdvisoryLockKey,
        "maintenance" => ServiceMaintenanceLock.AdvisoryLockKey,
        "publication" => FSTService.Persistence.PublicationGenerationSchema.AdvisoryLockKey,
        "planner" => SnapshotGenerationRetentionContract.PlannerAdvisoryLockKey,
        "ddl" => Scalar<long>("SELECT hashtextextended('fst.snapshot-generation-partition-ddl',0)"),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private sealed class OfflineTestAttestation(
        Func<int, CancellationToken, Task>? callback = null)
        : ISnapshotGenerationRetentionOfflineAttestation
    {
        private int _calls;
        public Task VerifyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) =>
            callback?.Invoke(++_calls, ct) ?? Task.CompletedTask;
    }

    private sealed class OfflineHookOracle(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> callback)
        : ISnapshotGenerationRetentionOracle
    {
        public async Task<SnapshotGenerationRetentionOracleResult> LoadAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction,
            long configuredResumeScrapeId, int commandTimeoutSeconds, CancellationToken ct = default)
        {
            await callback(connection, transaction, ct);
            return await new SnapshotGenerationRetentionOracle().LoadAsync(
                connection, transaction, configuredResumeScrapeId, commandTimeoutSeconds, ct);
        }
    }
}
