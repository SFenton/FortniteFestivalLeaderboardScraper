using System.Security.Cryptography;
using FSTService.Persistence;
using FSTService.Persistence.Maintenance;
using FSTService.Tests.Helpers;
using FstSnapshotGenerationRetirement;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Tests.Unit;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SnapshotGenerationRetirementPlanCollection
{
    public const string Name =
        "Snapshot generation retirement plan";
}

[Collection(
    SnapshotGenerationRetirementPlanCollection.Name)]
public sealed class SnapshotGenerationRetirementPlanTests
{
    [Fact]
    public async Task SchemaDefaultsOffAndPreservesReportOnlySchema()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);

        Assert.True(
            await database.IsSchemaInitializedAsync());
        var status = await database.ReadStatusAsync(
            TestCodeIdentity());
        Assert.True(status.SchemaInitialized);
        Assert.False(status.Control!.Enabled);
        Assert.Null(status.ActivePolicy);
        Assert.Null(status.LatestPolicy);
        Assert.Null(status.ActiveJob);
        Assert.Null(status.LatestJob);

        Assert.Equal(
            0,
            await ScalarAsync<int>(
                fixture.DataSource,
                """
                SELECT COUNT(*)::INTEGER
                FROM public.snapshot_generation_retirement_policy_epochs
                """));
        Assert.Empty(
            await QueryStringsAsync(
                fixture.DataSource,
                """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name LIKE
                        'snapshot_generation_retirement_%'
                  AND column_name IN (
                      'archive_path',
                      'artifact_key',
                      'lease_owner',
                      'lease_expires_at',
                      'process_id',
                      'container_id',
                      'cleanup_fence_active',
                      'drop_requested')
                ORDER BY column_name
                """));

        var reportOnlySchema = Path.Combine(
            FindRepositoryRoot(),
            "FSTService",
            "Persistence",
            "Maintenance",
            "SnapshotGenerationRetentionSchema.cs");
        Assert.Equal(
            "1111efef69b21fb2fc9b3a6b0076b119886dac82281e1c7b82a04b83ec504afd",
            Convert.ToHexString(
                    SHA256.HashData(
                        File.ReadAllBytes(
                            reportOnlySchema)))
                .ToLowerInvariant());
    }

    [Fact]
    public void CommandSurfaceRejectsExecutionAndTargetArguments()
    {
        Assert.Equal(
            PublicationGenerationSchema.AdvisoryLockKey,
            SnapshotGenerationRetirementContract
                .PublicationAdvisoryLockKey);
        Assert.Equal(
            SnapshotGenerationRetentionContract
                .PlannerAdvisoryLockKey,
            SnapshotGenerationRetirementContract
                .PlannerAdvisoryLockKey);
        Assert.Equal(
            SnapshotGenerationRetirementSchema
                .SchemaAdvisoryLockKey,
            SnapshotGenerationRetirementContract
                .SchemaAdvisoryLockKey);

        foreach (var command in new[]
                 {
                     "status",
                     "reconcile",
                     "deactivate-policy-epoch",
                     "plan-cycle",
                 })
        {
            Assert.Equal(
                command,
                RetirementCommandLine.Parse(
                        [command])
                    .Command);
        }

        foreach (var rejected in new[]
                 {
                     "archive-cycle",
                     "drop",
                     "quarantine",
                     "restore",
                     "delete",
                 })
        {
            Assert.Throws<ArgumentException>(
                () => RetirementCommandLine.Parse(
                    [rejected]));
        }
        Assert.Throws<ArgumentException>(
            () => RetirementCommandLine.Parse(
                [
                    "plan-cycle",
                    "--snapshot-id",
                    "1",
                ]));
    }

    [Fact]
    public async Task AuthorizationRequiresExactRuntimeAndDistinctReviewers()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var codeIdentity = TestCodeIdentity();
        var request =
            (await BuildAuthorizationAsync(
                database,
                codeIdentity)) with
            {
                ApprovedBy = " approver-a ",
                ReviewedBy = " reviewer-b ",
                ApprovalReference =
                    " review-evidence ",
            };

        var policy =
            await database.AuthorizePolicyEpochAsync(
                request,
                codeIdentity);
        Assert.Equal(
            SnapshotGenerationRetirementContract
                .StagePlan,
            await ScalarAsync<string>(
                fixture.DataSource,
                """
                SELECT stage_ceiling
                FROM public.snapshot_generation_retirement_policy_epochs
                """));
        Assert.Equal(
            request.ExpectedSourceIdentitySha256,
            policy.RuntimeIdentity
                .SourceIdentitySha256);
        Assert.Equal("approver-a", policy.ApprovedBy);
        Assert.Equal("reviewer-b", policy.ReviewedBy);
        Assert.Equal(
            "review-evidence",
            policy.ApprovalReference);

        await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => database.AuthorizePolicyEpochAsync(
                request,
                codeIdentity));

        using var secondFixture =
            new InMemoryMetaDatabase();
        await using var secondDatabase =
            RetirementDatabase.FromDataSource(
                secondFixture.DataSource);
        await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => secondDatabase
                .AuthorizePolicyEpochAsync(
                    request with
                    {
                        ExpectedSourceIdentitySha256 =
                            new string('f', 64),
                    },
                    codeIdentity));

        Assert.Throws<ArgumentException>(
            () => (request with
            {
                ReviewedBy = request.ApprovedBy,
            }).Validate());
        Assert.Throws<ArgumentException>(
            () => (request with
            {
                ExpiresAt =
                    request.ExpiresAt.AddTicks(1),
            }).Validate());

        using var expiredFixture =
            new InMemoryMetaDatabase();
        await using var expiredDatabase =
            RetirementDatabase.FromDataSource(
                expiredFixture.DataSource);
        var expiredRequest =
            await BuildAuthorizationAsync(
                expiredDatabase,
                codeIdentity);
        expiredRequest = expiredRequest with
        {
            NotBefore =
                PostgresTimestamp(
                    DateTimeOffset.UtcNow
                        .AddMinutes(-2)),
            ExpiresAt =
                PostgresTimestamp(
                    DateTimeOffset.UtcNow
                        .AddSeconds(-1)),
        };
        await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => expiredDatabase
                .AuthorizePolicyEpochAsync(
                    expiredRequest,
                    codeIdentity));
    }

    [Fact]
    public async Task PlanSelectsLargestEligibleAndIsIdempotent()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 1);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 4,
            maxTotalBytes:
                seeded.TotalCandidateBytes * 4);

        var first = await database.PlanCycleAsync(
            codeIdentity);
        var second = await database.PlanCycleAsync(
            codeIdentity);

        Assert.Equal(first.JobId, second.JobId);
        Assert.Equal("Solo_Guitar", first.Instrument);
        Assert.Equal(
            seeded.LargestEligibleSnapshotId,
            first.SnapshotId);
        Assert.Equal(
            seeded.LargestEligibleBytes,
            first.TargetBytes);
        Assert.NotEqual(1308, first.SnapshotId);
        Assert.Equal(
            1,
            await ScalarAsync<int>(
                fixture.DataSource,
                """
                SELECT COUNT(*)::INTEGER
                FROM public.snapshot_generation_retirement_jobs
                """));
        Assert.Equal(
            new[]
            {
                "policy_authorized",
                "job_planned",
            },
            await QueryStringsAsync(
                fixture.DataSource,
                """
                SELECT event_type
                FROM public.snapshot_generation_retirement_events
                ORDER BY sequence
                """));
        Assert.True(
            await ScalarAsync<bool>(
                fixture.DataSource,
                """
                WITH chain AS (
                    SELECT sequence,
                           previous_hash,
                           lag(current_hash) OVER (
                               ORDER BY sequence)
                               AS expected_previous_hash
                    FROM
                        public.snapshot_generation_retirement_events)
                SELECT bool_and(
                    CASE
                        WHEN sequence = 1
                            THEN previous_hash IS NULL
                        ELSE previous_hash =
                            expected_previous_hash
                    END)
                FROM chain
                """));
    }

    [Fact]
    public async Task ConcurrentPlanningCreatesOneJob()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 2);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 4,
            maxTotalBytes:
                seeded.TotalCandidateBytes * 4);

        var results = await Task.WhenAll(
            database.PlanCycleAsync(codeIdentity),
            database.PlanCycleAsync(codeIdentity));

        Assert.Equal(
            results[0].JobId,
            results[1].JobId);
        Assert.Equal(
            1,
            await ScalarAsync<int>(
                fixture.DataSource,
                """
                SELECT COUNT(*)::INTEGER
                FROM public.snapshot_generation_retirement_jobs
                """));
    }

    [Fact]
    public async Task ReconcileSupersedesOldCycleBeforeNextPlan()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var firstCycle = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 3);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 4,
            maxTotalBytes:
                firstCycle.TotalCandidateBytes * 8);
        var first = await database.PlanCycleAsync(
            codeIdentity);

        var secondCycle =
            await SeedCurrentCycleAsync(
                fixture.DataSource,
                seed: 4);
        var reconcile =
            await database.ReconcileAsync();
        Assert.Equal(
            "job_superseded",
            reconcile.Outcome);
        Assert.Equal(
            "newer_retention_cycle",
            reconcile.Job!.StateReason);

        var second = await database.PlanCycleAsync(
            codeIdentity);
        Assert.NotEqual(first.JobId, second.JobId);
        Assert.Equal(
            secondCycle.CycleId,
            second.CycleId);
        Assert.Equal(
            1,
            await ScalarAsync<int>(
                fixture.DataSource,
                """
                SELECT COUNT(*)::INTEGER
                FROM public.snapshot_generation_retirement_jobs
                WHERE state = 'planned'
                """));
    }

    [Fact]
    public async Task PlanRevalidatesExistingJobInsideItsTransaction()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var firstCycle = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 12);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 4,
            maxTotalBytes:
                firstCycle.TotalCandidateBytes * 8);
        var first = await database.PlanCycleAsync(
            codeIdentity);
        var secondCycle =
            await SeedCurrentCycleAsync(
                fixture.DataSource,
                seed: 13);
        var controller = new RetirementController(
            database,
            new FixedIdentityProvider(
                codeIdentity));

        await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => controller.PlanCycleAsync());
        Assert.Equal(
            "superseded",
            (await database.ReadStatusAsync(
                codeIdentity))
            .LatestJob!
            .State);
        var second = await controller.PlanCycleAsync();

        Assert.NotEqual(first.JobId, second.JobId);
        Assert.Equal(
            secondCycle.CycleId,
            second.CycleId);
        Assert.Equal(
            "superseded",
            await ScalarAsync<string>(
                fixture.DataSource,
                """
                SELECT state
                FROM public.snapshot_generation_retirement_jobs
                WHERE job_id = @jobId
                """,
                command => command.Parameters
                    .AddWithValue(
                        "jobId",
                        first.JobId)));
    }

    [Fact]
    public async Task PlanningWaitsForPublicationFenceAndRejectsRegression()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 16);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 2,
            maxTotalBytes:
                seeded.TotalCandidateBytes * 2);

        await using var writer =
            await fixture.DataSource.OpenConnectionAsync();
        await using var transaction =
            await writer.BeginTransactionAsync();
        await ExecuteAsync(
            writer,
            """
            SELECT pg_catalog.pg_advisory_xact_lock(
                @lockKey);
            UPDATE public.scrape_publication_state
            SET improvement_notifications_completed_at =
                NULL
            WHERE id = TRUE;
            """,
            command => command.Parameters
                .AddWithValue(
                    "lockKey",
                    PublicationGenerationSchema
                        .AdvisoryLockKey),
            transaction);
        var planning =
            database.PlanCycleAsync(codeIdentity);
        await Task.Delay(
            TimeSpan.FromMilliseconds(100));
        Assert.False(planning.IsCompleted);
        await transaction.CommitAsync();

        await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => planning);
        Assert.Equal(
            0,
            await ScalarAsync<int>(
                fixture.DataSource,
                """
                SELECT COUNT(*)::INTEGER
                FROM public.snapshot_generation_retirement_jobs
                """));
    }

    [Fact]
    public async Task PlanningWaitsForPartitionDdlFence()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 18);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 2,
            maxTotalBytes:
                seeded.TotalCandidateBytes * 2);

        await using var ddl =
            await fixture.DataSource.OpenConnectionAsync();
        await using var transaction =
            await ddl.BeginTransactionAsync();
        await ExecuteAsync(
            ddl,
            """
            SELECT pg_catalog.pg_advisory_xact_lock(
                pg_catalog.hashtextextended(
                    'fst.snapshot-generation-partition-ddl',
                    0))
            """,
            transaction: transaction);
        var planning =
            database.PlanCycleAsync(codeIdentity);
        await Task.Delay(
            TimeSpan.FromMilliseconds(100));
        Assert.False(planning.IsCompleted);
        await transaction.CommitAsync();

        Assert.Equal(
            seeded.LargestEligibleSnapshotId,
            (await planning).SnapshotId);
    }

    [Fact]
    public async Task TargetLockDoesNotCoverSiblingPartitions()
    {
        using var fixture = new InMemoryMetaDatabase();
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 19);
        await using var connection =
            await fixture.DataSource.OpenConnectionAsync();
        var observationId = await ScalarAsync<long>(
            connection,
            """
            SELECT observation_id
            FROM public.snapshot_generation_retention_observations
            WHERE cycle_id = @cycleId
              AND instrument = 'Solo_Guitar'
            """,
            command => command.Parameters
                .AddWithValue(
                    "cycleId",
                    seeded.CycleId));
        await using var transaction =
            await connection.BeginTransactionAsync();
        await ScalarAsync<long>(
            connection,
            """
            SELECT
                public.fst_lock_snapshot_generation_retirement_plan_target(
                    @cycleId,
                    @observationId)
            """,
            command =>
            {
                command.Transaction = transaction;
                command.Parameters.AddWithValue(
                    "cycleId",
                    seeded.CycleId);
                command.Parameters.AddWithValue(
                    "observationId",
                    observationId);
            });

        await using var locks =
            connection.CreateCommand();
        locks.Transaction = transaction;
        locks.CommandText = """
            SELECT relation.relname,
                   lock.mode
            FROM pg_catalog.pg_locks lock
            JOIN pg_catalog.pg_class relation
              ON relation.oid = lock.relation
            WHERE lock.pid =
                    pg_catalog.pg_backend_pid()
              AND relation.relname IN (
                    'leaderboard_entries_snapshot_solo_guitar',
                    'leaderboard_entries_snapshot_solo_guitar_s1419',
                    'leaderboard_entries_snapshot_solo_guitar_default')
            ORDER BY relation.relname,
                     lock.mode
            """;
        var heldLocks =
            new Dictionary<string, List<string>>(
                StringComparer.Ordinal);
        await using (var reader =
                     await locks.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var relation = reader.GetString(0);
                if (!heldLocks.TryGetValue(
                        relation,
                        out var modes))
                {
                    modes = [];
                    heldLocks.Add(relation, modes);
                }
                modes.Add(reader.GetString(1));
            }
        }
        Assert.Contains(
            "ShareRowExclusiveLock",
            heldLocks[
                "leaderboard_entries_snapshot_solo_guitar"]);
        Assert.Contains(
            "ShareRowExclusiveLock",
            heldLocks[
                "leaderboard_entries_snapshot_solo_guitar_s1419"]);
        Assert.DoesNotContain(
            "leaderboard_entries_snapshot_solo_guitar_default",
            heldLocks.Keys);

        await using var ddlConnection =
            await fixture.DataSource.OpenConnectionAsync();
        var ddlBlocked =
            await Assert.ThrowsAsync<PostgresException>(
                () => ExecuteAsync(
                    ddlConnection,
                    """
                    SET lock_timeout TO '250ms';
                    CREATE INDEX
                        ix_retirement_target_concurrent_test
                    ON
                        public.leaderboard_entries_snapshot_solo_guitar_s1419 (
                            account_id)
                    """));
        Assert.Equal(
            PostgresErrorCodes.LockNotAvailable,
            ddlBlocked.SqlState);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ReconcileExpiresPolicyAndReleasesGlobalSlot()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 5);
        var codeIdentity = TestCodeIdentity();
        var request = await BuildAuthorizationAsync(
            database,
            codeIdentity,
            expiresAt:
                PostgresTimestamp(
                    DateTimeOffset.UtcNow
                        .AddSeconds(1)),
            maxTotalBytes:
                seeded.TotalCandidateBytes * 2);
        await database.AuthorizePolicyEpochAsync(
            request,
            codeIdentity);
        await database.PlanCycleAsync(codeIdentity);

        await Task.Delay(TimeSpan.FromSeconds(1.2));
        var result = await database.ReconcileAsync();

        Assert.Equal(
            "policy_expired",
            result.Outcome);
        Assert.Equal("expired", result.Job!.State);
        var status = await database.ReadStatusAsync(
            codeIdentity);
        Assert.False(status.Control!.Enabled);
        Assert.Null(status.ActivePolicy);
        Assert.NotNull(status.LatestPolicy);
        Assert.Null(status.ActiveJob);
    }

    [Fact]
    public async Task PlanChecksExpiryAfterWaitingForControlLock()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 14);
        var codeIdentity = TestCodeIdentity();
        var request = await BuildAuthorizationAsync(
            database,
            codeIdentity,
            expiresAt:
                PostgresTimestamp(
                    DateTimeOffset.UtcNow
                        .AddSeconds(1)),
            maxTotalBytes:
                seeded.TotalCandidateBytes * 2);
        await database.AuthorizePolicyEpochAsync(
            request,
            codeIdentity);
        await database.PlanCycleAsync(codeIdentity);

        await using var blocker =
            await fixture.DataSource.OpenConnectionAsync();
        await using var transaction =
            await blocker.BeginTransactionAsync();
        await ExecuteAsync(
            blocker,
            """
            SELECT control_key
            FROM public.snapshot_generation_retirement_control
            WHERE control_key = TRUE
            FOR UPDATE
            """,
            transaction: transaction);
        var waitingPlan =
            database.PlanCycleAsync(codeIdentity);
        await Task.Delay(
            TimeSpan.FromSeconds(1.2));
        await transaction.CommitAsync();

        await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => waitingPlan);
        var status = await database.ReadStatusAsync(
            codeIdentity);
        Assert.False(status.Control!.Enabled);
        Assert.Equal(
            "expired",
            status.LatestJob!.State);
    }

    [Fact]
    public async Task OperatorDeactivationTerminalizesPlannedJob()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 11);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 2,
            maxTotalBytes:
                seeded.TotalCandidateBytes * 2);
        await database.PlanCycleAsync(codeIdentity);

        var result =
            await database.DeactivatePolicyEpochAsync();

        Assert.Equal(
            "operator_deactivated",
            result.Outcome);
        Assert.Equal(
            "operator_deactivated",
            result.Job!.StateReason);
        Assert.False(
            (await database.ReadStatusAsync(
                codeIdentity))
            .Control!
            .Enabled);
    }

    [Fact]
    public async Task RuntimeMismatchBlocksPlanning()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 6);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 2,
            maxTotalBytes:
                seeded.TotalCandidateBytes * 2);

        await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => database.PlanCycleAsync(
                codeIdentity with
                {
                    RepositoryCommit =
                        new string('9', 40),
                }));
        Assert.Equal(
            0,
            await ScalarAsync<int>(
                fixture.DataSource,
                """
                SELECT COUNT(*)::INTEGER
                FROM public.snapshot_generation_retirement_jobs
                """));
    }

    [Fact]
    public async Task InstalledControlSchemaChangeBlocksPlanning()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 23);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 2,
            maxTotalBytes:
                seeded.TotalCandidateBytes * 2);

        await using (var connection =
                     await fixture.DataSource
                         .OpenConnectionAsync())
        {
            await ExecuteAsync(
                connection,
                """
                CREATE INDEX
                    ix_retirement_test_schema_change
                ON
                    public.snapshot_generation_retirement_control (
                        updated_at)
                """);
        }

        await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => database.PlanCycleAsync(
                codeIdentity));
        Assert.Equal(
            0,
            await ScalarAsync<int>(
                fixture.DataSource,
                """
                SELECT COUNT(*)::INTEGER
                FROM public.snapshot_generation_retirement_jobs
                """));
    }

    [Fact]
    public async Task SchemaInitializationReenablesSafetyTrigger()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 26);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 2,
            maxTotalBytes:
                seeded.TotalCandidateBytes * 2);
        var approvedFingerprint =
            (await database.ReadStatusAsync(
                codeIdentity))
            .ObservedIdentity
            .ControlSchemaSha256;

        await using (var connection =
                     await fixture.DataSource
                         .OpenConnectionAsync())
        {
            await ExecuteAsync(
                connection,
                """
                ALTER TABLE
                    public.snapshot_generation_retirement_jobs
                DISABLE TRIGGER
                    trg_validate_snapshot_generation_retirement_job_insert
                """);
        }
        Assert.NotEqual(
            approvedFingerprint,
            (await database.ReadStatusAsync(
                codeIdentity))
            .ObservedIdentity
            .ControlSchemaSha256);
        await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => database.PlanCycleAsync(
                codeIdentity));

        await DatabaseInitializer.EnsureSchemaAsync(
            fixture.DataSource);
        var restored = await database.ReadStatusAsync(
            codeIdentity);
        Assert.Equal(
            approvedFingerprint,
            restored.ObservedIdentity
                .ControlSchemaSha256);
        Assert.Equal(
            "O",
            await ScalarAsync<string>(
                fixture.DataSource,
                """
                SELECT trigger_row.tgenabled::TEXT
                FROM pg_catalog.pg_trigger trigger_row
                WHERE trigger_row.tgrelid =
                        'public.snapshot_generation_retirement_jobs'::REGCLASS
                  AND trigger_row.tgname =
                        'trg_validate_snapshot_generation_retirement_job_insert'
                """));
        Assert.Equal(
            "planned",
            (await database.PlanCycleAsync(
                codeIdentity))
            .State);
    }

    [Fact]
    public async Task ReconcileDeactivatesExhaustedJobBudget()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var firstCycle = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 8);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 1,
            maxTotalBytes:
                firstCycle.TotalCandidateBytes * 2);
        await database.PlanCycleAsync(codeIdentity);
        await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 9);

        Assert.Equal(
            "job_superseded",
            (await database.ReconcileAsync()).Outcome);
        Assert.Equal(
            "policy_job_budget_exhausted",
            (await database.ReconcileAsync()).Outcome);
        Assert.False(
            (await database.ReadStatusAsync(
                codeIdentity))
            .Control!
            .Enabled);
    }

    [Fact]
    public async Task PlanningDeactivatesInsufficientByteBudget()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 10);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 1,
            maxTotalBytes: 1);

        await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => database.PlanCycleAsync(
                codeIdentity));
        var status = await database.ReadStatusAsync(
            codeIdentity);
        Assert.False(status.Control!.Enabled);
        Assert.Equal(
            "policy_deactivated",
            await ScalarAsync<string>(
                fixture.DataSource,
                """
                SELECT event_type
                FROM public.snapshot_generation_retirement_events
                ORDER BY sequence DESC
                LIMIT 1
                """));
    }

    [Fact]
    public async Task TargetLockWaitPrefersExpiryOverByteBudget()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 20);
        var codeIdentity = TestCodeIdentity();
        var request = await BuildAuthorizationAsync(
            database,
            codeIdentity,
            expiresAt:
                PostgresTimestamp(
                    DateTimeOffset.UtcNow
                        .AddSeconds(1)),
            maxJobs: 1,
            maxTotalBytes: 1);
        await database.AuthorizePolicyEpochAsync(
            request,
            codeIdentity);

        await using var blocker =
            await fixture.DataSource.OpenConnectionAsync();
        await using var transaction =
            await blocker.BeginTransactionAsync();
        await ExecuteAsync(
            blocker,
            """
            LOCK TABLE ONLY
                public.leaderboard_entries_snapshot_solo_guitar_s1420
            IN ACCESS EXCLUSIVE MODE
            """,
            transaction: transaction);
        var planning =
            database.PlanCycleAsync(codeIdentity);
        await Task.Delay(
            TimeSpan.FromSeconds(1.2));
        await transaction.CommitAsync();

        await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => planning);
        Assert.Equal(
            "policy_expired",
            await ScalarAsync<string>(
                fixture.DataSource,
                """
                SELECT payload ->> 'reason'
                FROM public.snapshot_generation_retirement_events
                WHERE event_type = 'policy_deactivated'
                ORDER BY sequence DESC
                LIMIT 1
                """));
    }

    [Fact]
    public async Task PolicyJobAndEventEvidenceRejectMutation()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 7);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 2,
            maxTotalBytes:
                seeded.TotalCandidateBytes * 2);
        var job = await database.PlanCycleAsync(
            codeIdentity);

        await using var connection =
            await fixture.DataSource.OpenConnectionAsync();
        foreach (var sql in new[]
                 {
                     """
                     UPDATE public.snapshot_generation_retirement_policy_epochs
                     SET max_jobs = max_jobs + 1
                     """,
                     """
                     UPDATE public.snapshot_generation_retirement_events
                     SET event_type = 'policy_deactivated'
                     """,
                     """
                     DELETE FROM public.snapshot_generation_retirement_jobs
                     """,
                 })
        {
            var exception =
                await Assert.ThrowsAsync<
                    PostgresException>(
                    () => ExecuteAsync(
                        connection,
                        sql));
            Assert.Equal("55000", exception.SqlState);
        }

        var identityMutation =
            await Assert.ThrowsAsync<
                PostgresException>(
                () => ExecuteAsync(
                    connection,
                    """
                    UPDATE public.snapshot_generation_retirement_jobs
                    SET snapshot_id = snapshot_id + 1
                    WHERE job_id = @jobId
                    """,
                    command => command.Parameters
                        .AddWithValue(
                            "jobId",
                            job.JobId)));
        Assert.Equal("55000", identityMutation.SqlState);

        var missingReason =
            await Assert.ThrowsAnyAsync<
                PostgresException>(
                () => ExecuteAsync(
                    connection,
                    """
                    UPDATE public.snapshot_generation_retirement_jobs
                    SET state = 'expired',
                        state_reason = NULL,
                        terminal_at = pg_catalog.now(),
                        updated_at = pg_catalog.now()
                    WHERE job_id = @jobId
                    """,
                    command => command.Parameters
                        .AddWithValue(
                            "jobId",
                            job.JobId)));
        Assert.Contains(
            missingReason.SqlState,
            new[] { "23514", "55000" });
    }

    [Fact]
    public async Task NotificationRegressionSupersedesPlan()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 15);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 2,
            maxTotalBytes:
                seeded.TotalCandidateBytes * 2);
        await database.PlanCycleAsync(codeIdentity);

        await using (var connection =
                     await fixture.DataSource
                         .OpenConnectionAsync())
        {
            await ExecuteAsync(
                connection,
                """
                UPDATE public.scrape_publication_state
                SET improvement_notifications_completed_at =
                    NULL
                WHERE id = TRUE
                """);
        }
        var result = await database.ReconcileAsync();

        Assert.Equal(
            "job_superseded",
            result.Outcome);
        Assert.Equal(
            "publication_changed",
            result.Job!.StateReason);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("completed")]
    public async Task CanonicalNotificationTerminalStatesAllowPlanning(
        string notificationStatus)
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: notificationStatus == "disabled"
                ? 24
                : 25);
        if (notificationStatus == "disabled")
        {
            await using var connection =
                await fixture.DataSource
                    .OpenConnectionAsync();
            await ExecuteAsync(
                connection,
                """
                UPDATE public.scrape_publication_state
                SET improvement_notifications_scrape_id =
                        NULL,
                    improvement_notifications_status =
                        'disabled',
                    improvement_notifications_completed_at =
                        NULL,
                    improvement_notifications_projection_ready =
                        FALSE,
                    improvement_notifications_projection_scrape_id =
                        NULL
                WHERE id = TRUE
                """);
        }
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 2,
            maxTotalBytes:
                seeded.TotalCandidateBytes * 2);

        var job = await database.PlanCycleAsync(
            codeIdentity);

        Assert.Equal("planned", job.State);
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("writer_failure")]
    [InlineData("running_scrape")]
    [InlineData("worker_operation")]
    public async Task MutableLivenessRootSupersedesPlan(
        string livenessRoot)
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 21);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 2,
            maxTotalBytes:
                seeded.TotalCandidateBytes * 2);
        var job = await database.PlanCycleAsync(
            codeIdentity);

        await using (var connection =
                     await fixture.DataSource
                         .OpenConnectionAsync())
        {
            var sql = livenessRoot switch
            {
                "hold" =>
                    """
                    INSERT INTO
                        public.snapshot_generation_retention_holds (
                            instrument,
                            snapshot_id,
                            hold_kind,
                            reason,
                            created_by)
                    VALUES (
                        'Solo_Guitar',
                        1421,
                        'operator',
                        'test hold',
                        'test')
                    """,
                "writer_failure" =>
                    """
                    INSERT INTO public.scrape_writer_failures (
                        scrape_id,
                        writer_kind,
                        instrument,
                        song_id,
                        page_count,
                        row_count,
                        exception_type,
                        error_message,
                        occurred_at)
                    VALUES (
                        1421,
                        'test',
                        'Solo_Guitar',
                        'song',
                        1,
                        1,
                        'TestException',
                        'test failure',
                        pg_catalog.now())
                    """,
                "running_scrape" =>
                    """
                    UPDATE public.scrape_log
                    SET status = 'running'
                    WHERE id = 1421
                    """,
                "worker_operation" =>
                    """
                    INSERT INTO public.service_worker_status (
                        worker_key,
                        status,
                        last_status_change_at,
                        current_operation_json,
                        updated_at)
                    VALUES (
                        'scraper',
                        'running',
                        pg_catalog.now(),
                        '{"operationKey":"scrape","status":"running"}',
                        pg_catalog.now())
                    ON CONFLICT (worker_key)
                    DO UPDATE
                    SET current_operation_json =
                            EXCLUDED.current_operation_json,
                        updated_at = EXCLUDED.updated_at
                    """,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(livenessRoot)),
            };
            await ExecuteAsync(connection, sql);
        }

        var result = await database.ReconcileAsync();

        Assert.Equal(job.JobId, result.Job!.JobId);
        Assert.Equal(
            "target_catalog_changed",
            result.Job.StateReason);
    }

    [Fact]
    public async Task PlanningCommitsConcurrentLivenessSupersession()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 22);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 2,
            maxTotalBytes:
                seeded.TotalCandidateBytes * 2);
        var job = await database.PlanCycleAsync(
            codeIdentity);
        await using (var connection =
                     await fixture.DataSource
                         .OpenConnectionAsync())
        {
            await ExecuteAsync(
                connection,
                """
                INSERT INTO
                    public.snapshot_generation_retention_holds (
                        instrument,
                        snapshot_id,
                        hold_kind,
                        reason,
                        created_by)
                VALUES (
                    'Solo_Guitar',
                    1422,
                    'operator',
                    'test hold',
                    'test')
                """);
        }

        await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => database.PlanCycleAsync(
                codeIdentity));
        var latest = (await database.ReadStatusAsync(
            codeIdentity)).LatestJob;

        Assert.Equal(job.JobId, latest!.JobId);
        Assert.Equal("superseded", latest.State);
        Assert.Equal(
            "target_catalog_changed",
            latest.StateReason);
    }

    [Fact]
    public async Task DetachedChildSupersedesPlan()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using var database =
            RetirementDatabase.FromDataSource(
                fixture.DataSource);
        var seeded = await SeedCurrentCycleAsync(
            fixture.DataSource,
            seed: 17);
        var codeIdentity = TestCodeIdentity();
        await AuthorizeAsync(
            database,
            codeIdentity,
            maxJobs: 2,
            maxTotalBytes:
                seeded.TotalCandidateBytes * 2);
        var job = await database.PlanCycleAsync(
            codeIdentity);

        await using (var connection =
                     await fixture.DataSource
                         .OpenConnectionAsync())
        {
            await ExecuteAsync(
                connection,
                """
                ALTER TABLE
                    public.leaderboard_entries_snapshot_solo_guitar
                DETACH PARTITION
                    public.leaderboard_entries_snapshot_solo_guitar_s1417
                """);
        }
        var result = await database.ReconcileAsync();

        Assert.Equal(job.JobId, result.Job!.JobId);
        Assert.Equal(
            "target_catalog_changed",
            result.Job.StateReason);
    }

    [Fact]
    public async Task FixedSearchPathIgnoresShadowControlTables()
    {
        using var fixture = new InMemoryMetaDatabase();
        await using (var connection =
                     await fixture.DataSource
                         .OpenConnectionAsync())
        {
            await ExecuteAsync(
                connection,
                """
                CREATE SCHEMA evil;
                CREATE TABLE
                    evil.snapshot_generation_retirement_control (
                        control_key BOOLEAN,
                        enabled BOOLEAN,
                        active_policy_epoch_id UUID,
                        updated_by TEXT,
                        updated_at TIMESTAMPTZ);
                INSERT INTO
                    evil.snapshot_generation_retirement_control
                VALUES (
                    TRUE,
                    TRUE,
                    pg_catalog.gen_random_uuid(),
                    'evil',
                    pg_catalog.now());
                """);
        }

        var builder =
            new NpgsqlConnectionStringBuilder(
                fixture.DataSource.ConnectionString)
            {
                SearchPath = "evil,public",
            };
        await using var database =
            RetirementDatabase.FromConnectionString(
                builder.ConnectionString);
        var status = await database.ReadStatusAsync(
            TestCodeIdentity());

        Assert.False(status.Control!.Enabled);
        Assert.Equal(
            "schema-default",
            status.Control.UpdatedBy);
    }

    [Fact]
    public void RuntimeIdentityDigestRejectsRestartedClone()
    {
        var source =
            new RetirementDatabaseIdentity(
                "fstservice",
                12345,
                "1234567890123456789",
                170000,
                "/var/lib/postgresql/data",
                DateTimeOffset.Parse(
                    "2026-09-04T00:00:00Z"));
        var restartedClone = source with
        {
            PostmasterStartedAtUtc =
                source.PostmasterStartedAtUtc
                    .AddSeconds(1),
        };

        Assert.NotEqual(
            source.ComputeDigest(),
            restartedClone.ComputeDigest());
    }

    private static RetirementCodeIdentity
        TestCodeIdentity() =>
        new(
            new string('1', 40),
            new string('2', 40),
            new string('3', 64),
            new string('4', 64),
            new string('5', 64));

    private static async Task<
        SnapshotGenerationRetirementPolicy>
        AuthorizeAsync(
            RetirementDatabase database,
            RetirementCodeIdentity codeIdentity,
            int maxJobs,
            long maxTotalBytes)
    {
        var request = await BuildAuthorizationAsync(
            database,
            codeIdentity,
            maxJobs: maxJobs,
            maxTotalBytes: maxTotalBytes);
        return await database.AuthorizePolicyEpochAsync(
            request,
            codeIdentity);
    }

    private static async Task<
        RetirementAuthorizationRequest>
        BuildAuthorizationAsync(
            RetirementDatabase database,
            RetirementCodeIdentity codeIdentity,
            DateTimeOffset? expiresAt = null,
            int maxJobs = 4,
            long maxTotalBytes = 1L << 40)
    {
        var observed =
            (await database.ReadStatusAsync(
                codeIdentity))
            .ObservedIdentity;
        return new(
            PostgresTimestamp(
                DateTimeOffset.UtcNow
                    .AddMinutes(-1)),
            expiresAt
                ?? PostgresTimestamp(
                    DateTimeOffset.UtcNow
                        .AddHours(1)),
            maxJobs,
            maxTotalBytes,
            "approver-a",
            "reviewer-b",
            "review-evidence",
            codeIdentity.RepositoryCommit,
            codeIdentity.RepositoryTree,
            codeIdentity.SupervisorBinarySha256,
            codeIdentity.SupervisorSourceSha256,
            codeIdentity.WrapperSha256,
            observed.ControlSchemaSha256,
            observed.SourceIdentitySha256);
    }

    private static DateTimeOffset PostgresTimestamp(
        DateTimeOffset value) =>
        new(
            value.Ticks - value.Ticks % 10,
            TimeSpan.Zero);

    private static async Task<SeededCycle>
        SeedCurrentCycleAsync(
            NpgsqlDataSource dataSource,
            int seed)
    {
        var scrapeId = 910000L + seed;
        var publicationId = 91000L + seed;
        var largeSnapshotId = 1400L + seed;
        var smallSnapshotId = 1600L + seed;
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await EnsureAndPopulateCandidateAsync(
            connection,
            "Solo_Bass",
            1308,
            seed,
            rowCount: 1200);
        await EnsureAndPopulateCandidateAsync(
            connection,
            "Solo_Guitar",
            largeSnapshotId,
            seed,
            rowCount: 600);
        await EnsureAndPopulateCandidateAsync(
            connection,
            "Solo_PeripheralDrums",
            smallSnapshotId,
            seed,
            rowCount: 10);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO public.scrape_log (
                id,
                started_at,
                completed_at,
                status)
            VALUES
                (
                    1308,
                    pg_catalog.now(),
                    pg_catalog.now(),
                    'completed'),
                (
                    @largeSnapshotId,
                    pg_catalog.now(),
                    pg_catalog.now(),
                    'completed'),
                (
                    @smallSnapshotId,
                    pg_catalog.now(),
                    pg_catalog.now(),
                    'completed')
            ON CONFLICT (id) DO NOTHING
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "largeSnapshotId",
                    largeSnapshotId);
                command.Parameters.AddWithValue(
                    "smallSnapshotId",
                    smallSnapshotId);
            });
        await ExecuteAsync(
            connection,
            """
            UPDATE public.publication_generations
            SET status = 'retained'
            WHERE status = 'current';

            INSERT INTO public.scrape_log (
                id,
                started_at,
                completed_at,
                status)
            VALUES (
                @scrapeId,
                pg_catalog.now(),
                pg_catalog.now(),
                'completed');

            INSERT INTO public.publication_generations (
                publication_id,
                scrape_id,
                status,
                created_at,
                ready_at,
                published_at)
            VALUES (
                @publicationId,
                @scrapeId,
                'current',
                pg_catalog.now(),
                pg_catalog.now(),
                pg_catalog.now());

            UPDATE public.scrape_publication_state
            SET published_scrape_id = @scrapeId,
                current_publication_id =
                    @publicationId,
                previous_publication_id = NULL,
                working_publication_id = NULL,
                public_reads_frozen = FALSE,
                publication_commit_intent_started_at =
                    NULL,
                max_score_mutation_gate_token = NULL,
                improvement_notifications_scrape_id =
                    @scrapeId,
                improvement_notifications_status =
                    'completed',
                improvement_notifications_completed_at =
                    pg_catalog.now(),
                improvement_notifications_projection_ready =
                    TRUE,
                improvement_notifications_projection_scrape_id =
                    @scrapeId,
                updated_at = pg_catalog.now()
            WHERE id = TRUE;
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "scrapeId",
                    scrapeId);
                command.Parameters.AddWithValue(
                    "publicationId",
                    publicationId);
            });

        var cycleId = await ScalarAsync<long>(
            connection,
            """
            INSERT INTO
                public.snapshot_generation_retention_cycles (
                    trigger_scrape_id,
                    trigger_publication_id,
                    safe_point_kind,
                    safe_point_at,
                    planner_version,
                    config_version,
                    report_only,
                    status,
                    oracle_agreement,
                    candidate_identity_hash,
                    observation_hash,
                    planner_child_set,
                    planner_live_set,
                    planner_candidate_set,
                    oracle_child_set,
                    oracle_live_set,
                    oracle_candidate_set,
                    candidate_count,
                    protected_count,
                    blocked_count,
                    candidate_bytes,
                    global_blockers,
                    anomalies,
                    created_at)
            VALUES (
                @scrapeId,
                @publicationId,
                'terminal_worker_post_publication',
                pg_catalog.now(),
                3,
                1,
                TRUE,
                'observed',
                TRUE,
                repeat('a', 64),
                repeat('b', 64),
                '[]',
                '[]',
                '[]',
                '[]',
                '[]',
                '[]',
                3,
                0,
                0,
                1,
                '[]',
                '[]',
                pg_catalog.clock_timestamp())
            RETURNING cycle_id
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "scrapeId",
                    scrapeId);
                command.Parameters.AddWithValue(
                    "publicationId",
                    publicationId);
            });

        var excluded = await AddObservationAsync(
            connection,
            cycleId,
            "Solo_Bass",
            1308,
            "leaderboard_entries_snapshot_solo_bass",
            "leaderboard_entries_snapshot_solo_bass_s1308");
        var large = await AddObservationAsync(
            connection,
            cycleId,
            "Solo_Guitar",
            largeSnapshotId,
            "leaderboard_entries_snapshot_solo_guitar",
            $"leaderboard_entries_snapshot_solo_guitar_s{largeSnapshotId}");
        var small = await AddObservationAsync(
            connection,
            cycleId,
            "Solo_PeripheralDrums",
            smallSnapshotId,
            "leaderboard_entries_snapshot_pro_drums",
            $"leaderboard_entries_snapshot_pro_drums_s{smallSnapshotId}");
        return new(
            cycleId,
            excluded.TotalBytes,
            largeSnapshotId,
            large.TotalBytes,
            small.TotalBytes);
    }

    private static async Task<CandidateSize>
        AddObservationAsync(
            NpgsqlConnection connection,
            long cycleId,
            string instrument,
            long snapshotId,
            string rootRelation,
            string childRelation)
    {
            CandidateCatalog catalog;
            await using (var metadata =
                         connection.CreateCommand())
            {
                metadata.CommandText = """
                    SELECT
                        root_inheritance.inhparent::BIGINT,
                        root.oid::BIGINT,
                        pg_catalog.pg_get_partkeydef(
                            root.oid),
                        pg_catalog.pg_get_expr(
                            root.relpartbound,
                            root.oid,
                            TRUE),
                        COALESCE(
                            root_tablespace.spcname,
                            database_tablespace.spcname),
                        pg_catalog.to_jsonb(
                            ARRAY(
                                SELECT option
                                FROM pg_catalog.unnest(
                                    COALESCE(
                                        root.reloptions,
                                        ARRAY[]::TEXT[]))
                                    option
                                ORDER BY option))::TEXT,
                        public.fst_snapshot_generation_retirement_index_configuration(
                            root.oid::BIGINT)::TEXT,
                        child.oid::BIGINT,
                        child.relfilenode::BIGINT,
                        pg_catalog.pg_get_expr(
                            child.relpartbound,
                            child.oid,
                            TRUE),
                        COALESCE(
                            child_tablespace.spcname,
                            database_tablespace.spcname),
                        child.relkind::TEXT,
                        child.relpersistence::TEXT,
                        access_method.amname,
                        pg_catalog.to_jsonb(
                            ARRAY(
                                SELECT option
                                FROM pg_catalog.unnest(
                                    COALESCE(
                                        child.reloptions,
                                        ARRAY[]::TEXT[]))
                                    option
                                ORDER BY option))::TEXT,
                        public.fst_snapshot_generation_retirement_index_configuration(
                            child.oid::BIGINT)::TEXT,
                        pg_catalog.pg_total_relation_size(
                            child.oid)::BIGINT
                    FROM pg_catalog.pg_class root
                    JOIN pg_catalog.pg_namespace root_namespace
                      ON root_namespace.oid =
                            root.relnamespace
                    JOIN pg_catalog.pg_inherits root_inheritance
                      ON root_inheritance.inhrelid =
                            root.oid
                    JOIN pg_catalog.pg_class child
                      ON child.relname =
                            @childRelation
                    JOIN pg_catalog.pg_namespace child_namespace
                      ON child_namespace.oid =
                            child.relnamespace
                     AND child_namespace.nspname = 'public'
                    JOIN pg_catalog.pg_inherits child_inheritance
                      ON child_inheritance.inhrelid =
                            child.oid
                     AND child_inheritance.inhparent =
                            root.oid
                    JOIN pg_catalog.pg_am access_method
                      ON access_method.oid =
                            child.relam
                    LEFT JOIN pg_catalog.pg_tablespace root_tablespace
                      ON root_tablespace.oid =
                            root.reltablespace
                    LEFT JOIN pg_catalog.pg_tablespace child_tablespace
                      ON child_tablespace.oid =
                            child.reltablespace
                    CROSS JOIN LATERAL (
                        SELECT default_tablespace.spcname
                        FROM pg_catalog.pg_database database
                        JOIN pg_catalog.pg_tablespace default_tablespace
                          ON default_tablespace.oid =
                                database.dattablespace
                        WHERE database.datname =
                                pg_catalog.current_database()
                    ) database_tablespace
                    WHERE root_namespace.nspname = 'public'
                      AND root.relname = @rootRelation
                    """;
                metadata.Parameters.AddWithValue(
                    "rootRelation",
                    rootRelation);
                metadata.Parameters.AddWithValue(
                    "childRelation",
                    childRelation);
                await using var reader =
                    await metadata.ExecuteReaderAsync();
                await reader.ReadAsync();
                catalog = new(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetString(12),
                    reader.GetString(13),
                    reader.GetString(14),
                    reader.GetString(15),
                    reader.GetInt64(16));
            }

        await ExecuteAsync(
            connection,
            """
            INSERT INTO
                public.snapshot_generation_retention_observations (
                    cycle_id,
                    report_only,
                    instrument,
                    root_schema,
                    root_relation,
                    snapshot_parent_oid,
                    root_oid,
                    root_partition_key,
                    root_partition_bound,
                    root_tablespace_name,
                    root_relation_options,
                    root_index_configuration,
                    child_schema,
                    child_relation,
                    snapshot_id,
                    child_oid,
                    child_relfilenode,
                    partition_bound,
                    tablespace_name,
                    relation_kind,
                    persistence_kind,
                    access_method,
                    relation_options,
                    index_configuration,
                    stable_child_identity_hash,
                    stable_config_schema_hash,
                    row_estimate,
                    total_bytes,
                    observation_metrics_hash,
                    planner_live,
                    oracle_live,
                    classification,
                    root_reasons,
                    blocker_codes,
                    details)
            VALUES (
                @cycleId,
                TRUE,
                @instrument,
                'public',
                @rootRelation,
                @snapshotParentOid,
                @rootOid,
                @rootPartitionKey,
                @rootPartitionBound,
                @rootTablespaceName,
                @rootRelationOptions,
                @rootIndexConfiguration,
                'public',
                @childRelation,
                @snapshotId,
                @childOid,
                @childRelfilenode,
                @partitionBound,
                @tablespaceName,
                @relationKind,
                @persistenceKind,
                @accessMethod,
                @relationOptions,
                @indexConfiguration,
                @identityHash,
                @configHash,
                1,
                @bytes,
                @metricsHash,
                FALSE,
                FALSE,
                'candidate',
                ARRAY[]::TEXT[],
                ARRAY[]::TEXT[],
                '{}')
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "cycleId",
                    cycleId);
                command.Parameters.AddWithValue(
                    "instrument",
                    instrument);
                command.Parameters.AddWithValue(
                    "rootRelation",
                    rootRelation);
                command.Parameters.AddWithValue(
                    "snapshotParentOid",
                    catalog.SnapshotParentOid);
                command.Parameters.AddWithValue(
                    "rootOid",
                    catalog.RootOid);
                command.Parameters.AddWithValue(
                    "rootPartitionKey",
                    catalog.RootPartitionKey);
                command.Parameters.AddWithValue(
                    "rootPartitionBound",
                    catalog.RootPartitionBound);
                command.Parameters.AddWithValue(
                    "rootTablespaceName",
                    catalog.RootTablespaceName);
                AddJsonParameter(
                    command,
                    "rootRelationOptions",
                    catalog.RootRelationOptions);
                AddJsonParameter(
                    command,
                    "rootIndexConfiguration",
                    catalog.RootIndexConfiguration);
                command.Parameters.AddWithValue(
                    "childRelation",
                    childRelation);
                command.Parameters.AddWithValue(
                    "snapshotId",
                    snapshotId);
                command.Parameters.AddWithValue(
                    "childOid",
                    catalog.ChildOid);
                command.Parameters.AddWithValue(
                    "childRelfilenode",
                    catalog.ChildRelfilenode);
                command.Parameters.AddWithValue(
                    "partitionBound",
                    catalog.PartitionBound);
                command.Parameters.AddWithValue(
                    "tablespaceName",
                    catalog.TablespaceName);
                command.Parameters.AddWithValue(
                    "relationKind",
                    catalog.RelationKind);
                command.Parameters.AddWithValue(
                    "persistenceKind",
                    catalog.PersistenceKind);
                command.Parameters.AddWithValue(
                    "accessMethod",
                    catalog.AccessMethod);
                AddJsonParameter(
                    command,
                    "relationOptions",
                    catalog.RelationOptions);
                AddJsonParameter(
                    command,
                    "indexConfiguration",
                    catalog.IndexConfiguration);
                command.Parameters.AddWithValue(
                    "identityHash",
                    new string('a', 64));
                command.Parameters.AddWithValue(
                    "configHash",
                    new string('b', 64));
                command.Parameters.AddWithValue(
                    "bytes",
                    catalog.TotalBytes);
                command.Parameters.AddWithValue(
                    "metricsHash",
                    new string('c', 64));
            });
        return new(
            catalog.ChildOid,
            catalog.ChildRelfilenode,
            catalog.TotalBytes);
    }

    private static async Task
        EnsureAndPopulateCandidateAsync(
            NpgsqlConnection connection,
            string instrument,
            long snapshotId,
            int seed,
            int rowCount)
    {
        await ExecuteAsync(
            connection,
            """
            SELECT
                public.ensure_leaderboard_snapshot_generation_partition(
                    @instrument,
                    @snapshotId);

            INSERT INTO public.leaderboard_entries_snapshot (
                snapshot_id,
                song_id,
                instrument,
                account_id,
                score,
                first_seen_at,
                last_updated_at)
            SELECT
                @snapshotId,
                'song-' || @seed::TEXT || '-'
                    || value::TEXT || '-'
                    || repeat('s', 96),
                @instrument,
                'account-' || @seed::TEXT || '-'
                    || value::TEXT || '-'
                    || repeat('a', 96),
                value,
                pg_catalog.now(),
                pg_catalog.now()
            FROM pg_catalog.generate_series(
                1,
                @rowCount) value;
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "instrument",
                    instrument);
                command.Parameters.AddWithValue(
                    "snapshotId",
                    snapshotId);
                command.Parameters.AddWithValue(
                    "seed",
                    seed);
                command.Parameters.AddWithValue(
                    "rowCount",
                    rowCount);
            });
    }

    private static void AddJsonParameter(
        NpgsqlCommand command,
        string name,
        string value) =>
        command.Parameters.Add(
            name,
            NpgsqlDbType.Jsonb).Value = value;

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        Action<NpgsqlCommand>? configure = null,
        NpgsqlTransaction? transaction = null)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        configure?.Invoke(command);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlDataSource dataSource,
        string sql,
        Action<NpgsqlCommand>? configure = null)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        return await ScalarAsync<T>(
            connection,
            sql,
            configure);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        Action<NpgsqlCommand>? configure = null)
    {
        await using var command =
            connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        return (T)Convert.ChangeType(
            (await command.ExecuteScalarAsync())!,
            typeof(T));
    }

    private static async Task<string[]>
        QueryStringsAsync(
            NpgsqlDataSource dataSource,
            string sql)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var command =
            connection.CreateCommand();
        command.CommandText = sql;
        await using var reader =
            await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(
            AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        current.FullName,
                        "FortniteFestivalLeaderboardScraper.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException(
            "Repository root was not found.");
    }

    private sealed record SeededCycle(
        long CycleId,
        long ExcludedBytes,
        long LargestEligibleSnapshotId,
        long LargestEligibleBytes,
        long SmallBytes)
    {
        public long TotalCandidateBytes =>
            checked(
                ExcludedBytes
                + LargestEligibleBytes
                + SmallBytes);
    }

    private sealed record CandidateSize(
        long Oid,
        long Relfilenode,
        long TotalBytes);

    private sealed record CandidateCatalog(
        long SnapshotParentOid,
        long RootOid,
        string RootPartitionKey,
        string RootPartitionBound,
        string RootTablespaceName,
        string RootRelationOptions,
        string RootIndexConfiguration,
        long ChildOid,
        long ChildRelfilenode,
        string PartitionBound,
        string TablespaceName,
        string RelationKind,
        string PersistenceKind,
        string AccessMethod,
        string RelationOptions,
        string IndexConfiguration,
        long TotalBytes);

    private sealed class FixedIdentityProvider(
        RetirementCodeIdentity identity)
        : IRetirementRuntimeIdentityProvider
    {
        public Task<RetirementCodeIdentity> CaptureAsync(
            bool requireCleanRepository,
            CancellationToken ct = default) =>
            Task.FromResult(identity);
    }
}
