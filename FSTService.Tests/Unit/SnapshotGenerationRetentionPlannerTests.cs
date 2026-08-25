using FortniteFestival.Core;
using FSTService.Persistence;
using FSTService.Persistence.Maintenance;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class SnapshotGenerationRetentionPlannerTests
    : IDisposable
{
    private const long CandidateScrapeId = 100;
    private const long PreviousScrapeId = 200;
    private const long WorkingScrapeId = 250;
    private const long CurrentScrapeId = 300;
    private const long PreviousPublicationId = 2_000;
    private const long WorkingPublicationId = 2_500;
    private const long CurrentPublicationId = 3_000;
    private const string CatalogSongId = "catalog-song";

    private readonly InMemoryMetaDatabase _fixture = new();
    private readonly SnapshotGenerationRetentionRepository _repository;

    public SnapshotGenerationRetentionPlannerTests()
    {
        _repository = new SnapshotGenerationRetentionRepository(
            _fixture.DataSource);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task DisabledPlanner_WritesNothing()
    {
        var planner = CreatePlanner(
            new DatabaseMaintenanceOptions
            {
                SnapshotGenerationRetentionPlannerEnabled = false,
            });

        var result = await planner.PlanAsync(Request());

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Disabled,
            result.Disposition);
        Assert.Equal(
            0,
            Scalar<int>(
                "SELECT COUNT(*) FROM snapshot_generation_retention_cycles"));
        Assert.Equal(
            0,
            Scalar<int>(
                "SELECT COUNT(*) FROM snapshot_generation_retention_jobs"));
        Assert.Equal(
            0,
            Scalar<int>(
                "SELECT COUNT(*) FROM snapshot_generation_retention_evidence"));
    }

    [Fact]
    public async Task Planner_ReturnsBusyWhileRegistrationMutationIsActive()
    {
        SeedBaseline();
        await using var connection =
            await _fixture.DataSource.OpenConnectionAsync();
        await using (var acquire = connection.CreateCommand())
        {
            acquire.CommandText =
                "SELECT pg_advisory_lock_shared(@lockKey)";
            acquire.Parameters.AddWithValue(
                "lockKey",
                RegistrationMutationGate.AdvisoryLockKey);
            await acquire.ExecuteScalarAsync();
        }

        try
        {
            var result =
                await CreatePlanner().PlanAsync(Request());

            Assert.Equal(
                SnapshotGenerationRetentionPlanDisposition.Busy,
                result.Disposition);
            Assert.Contains(
                "registration mutation",
                result.Reason,
                StringComparison.Ordinal);
            Assert.Equal(
                0,
                Scalar<int>(
                    "SELECT COUNT(*) FROM snapshot_generation_retention_cycles"));
        }
        finally
        {
            await using var release =
                connection.CreateCommand();
            release.CommandText =
                "SELECT pg_advisory_unlock_shared(@lockKey)";
            release.Parameters.AddWithValue(
                "lockKey",
                RegistrationMutationGate.AdvisoryLockKey);
            await release.ExecuteScalarAsync();
        }
    }

    [Fact]
    public async Task Planner_CoexistsWithSharedPublicationReader()
    {
        SeedBaseline();
        await using var connection =
            await _fixture.DataSource.OpenConnectionAsync();
        await using (var acquire = connection.CreateCommand())
        {
            acquire.CommandText =
                "SELECT pg_advisory_lock_shared(@lockKey)";
            acquire.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            await acquire.ExecuteScalarAsync();
        }

        try
        {
            var result =
                await CreatePlanner().PlanAsync(Request());

            Assert.Equal(
                SnapshotGenerationRetentionPlanDisposition.Observed,
                result.Disposition);
        }
        finally
        {
            await using var release =
                connection.CreateCommand();
            release.CommandText =
                "SELECT pg_advisory_unlock_shared(@lockKey)";
            release.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            await release.ExecuteScalarAsync();
        }
    }

    [Fact]
    public async Task Planner_ReturnsBusyWhilePublicationWriterIsActive()
    {
        SeedBaseline();
        await using var connection =
            await _fixture.DataSource.OpenConnectionAsync();
        await using (var acquire = connection.CreateCommand())
        {
            acquire.CommandText =
                "SELECT pg_advisory_lock(@lockKey)";
            acquire.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            await acquire.ExecuteScalarAsync();
        }

        try
        {
            var result =
                await CreatePlanner().PlanAsync(Request());

            Assert.Equal(
                SnapshotGenerationRetentionPlanDisposition.Busy,
                result.Disposition);
            Assert.Contains(
                "publication allocation or commit",
                result.Reason,
                StringComparison.Ordinal);
            Assert.Equal(
                0,
                Scalar<int>(
                    "SELECT COUNT(*) FROM snapshot_generation_retention_cycles"));
        }
        finally
        {
            await using var release =
                connection.CreateCommand();
            release.CommandText =
                "SELECT pg_advisory_unlock(@lockKey)";
            release.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            await release.ExecuteScalarAsync();
        }
    }

    [Fact]
    public async Task Planner_PersistsDeterministicAllInstrumentPlanAndIsIdempotent()
    {
        SeedBaseline();
        var planner = CreatePlanner();

        var first = await planner.PlanAsync(Request());
        var second = await planner.PlanAsync(Request());

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Observed,
            first.Disposition);
        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Existing,
            second.Disposition);
        Assert.Equal(first.CycleId, second.CycleId);
        Assert.Equal(first.PlanDigest, second.PlanDigest);
        Assert.Equal(9, first.CandidateCount);
        Assert.Equal(0, first.PlannedCount);
        Assert.Equal(18, first.BlockedCount);

        var jobs = await _repository.GetJobsAsync(first.CycleId!.Value);
        Assert.Equal(27, jobs.Count);
        Assert.Equal(
            SnapshotGenerationRetentionContract.Instruments
                .Select(static item => item.Instrument)
                .OrderBy(static item => item, StringComparer.Ordinal),
            jobs.Select(static job => job.Instrument)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static item => item, StringComparer.Ordinal));
        Assert.All(
            SnapshotGenerationRetentionContract.Instruments,
            instrument => Assert.Equal(
                3,
                jobs.Count(job =>
                    job.Instrument == instrument.Instrument)));

        var observed = jobs.Where(
                job => job.Status ==
                    SnapshotGenerationRetentionJobStatus.Observed)
            .ToArray();
        Assert.Equal(9, observed.Length);
        Assert.All(
            observed,
            static job =>
            {
                Assert.Equal(CandidateScrapeId, job.SnapshotId);
                Assert.True(job.ReportOnly);
            });
        Assert.DoesNotContain(
            jobs,
            static job =>
                job.Status ==
                SnapshotGenerationRetentionJobStatus.Planned);
        Assert.DoesNotContain(
            jobs,
            static job => job.BlockerCodes.Contains(
                "snapshot_parent_partition_key_invalid",
                StringComparer.Ordinal)
                || job.BlockerCodes.Contains(
                    "root_partition_key_invalid",
                    StringComparer.Ordinal));

        foreach (var instrument in
                 SnapshotGenerationRetentionContract.Instruments)
        {
            var current = Assert.Single(
                jobs,
                job => job.Instrument == instrument.Instrument
                    && job.SnapshotId == CurrentScrapeId);
            Assert.Contains(
                "current_publication_generation",
                current.BlockerCodes);
            Assert.Contains(
                "current_publication_source",
                current.BlockerCodes);
            Assert.Contains(
                "active_snapshot",
                current.BlockerCodes);
            Assert.Contains(
                "projection_source",
                current.BlockerCodes);

            var previous = Assert.Single(
                jobs,
                job => job.Instrument == instrument.Instrument
                    && job.SnapshotId == PreviousScrapeId);
            Assert.Contains(
                "previous_publication_generation",
                previous.BlockerCodes);
            Assert.Contains(
                "previous_publication_source",
                previous.BlockerCodes);

        }

        var cycle = await _repository.GetCycleForSafePointAsync(
            SnapshotGenerationRetentionContract
                .PostPublicationSafePoint,
            CurrentPublicationId);
        Assert.NotNull(cycle);
        Assert.True(cycle!.ReportOnly);
        Assert.Equal(
            SnapshotGenerationRetentionCycleStatus.Observed,
            cycle.Status);

        var evidence =
            await _repository.GetEvidenceAsync(first.CycleId.Value);
        Assert.NotEmpty(evidence);
        Assert.Null(evidence[0].PreviousHash);
        for (var index = 1; index < evidence.Count; index++)
        {
            Assert.Equal(
                evidence[index - 1].CurrentHash,
                evidence[index].PreviousHash);
        }
        Assert.All(
            evidence,
            item => Assert.Matches(
                "^[0-9a-f]{64}$",
                item.CurrentHash));
        Assert.Equal(
            1,
            Scalar<int>(
                "SELECT COUNT(*) FROM snapshot_generation_retention_cycles"));
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)
                FROM pg_class
                WHERE oid = to_regclass(
                    'public.leaderboard_entries_snapshot_solo_drums_s100')
                """));
    }

    [Fact]
    public async Task Planner_IsolatesPerInstrumentProtection()
    {
        SeedBaseline();
        Execute(
            """
            UPDATE leaderboard_snapshot_state
            SET active_snapshot_id = 100,
                scrape_id = 100,
                updated_at = now()
            WHERE instrument = 'Solo_Guitar';

            UPDATE solo_current_projection_scope
            SET source_snapshot_id = 100,
                updated_at = now()
            WHERE instrument = 'Solo_Guitar';
            """);
        var planner = CreatePlanner();

        var result = await planner.PlanAsync(Request());
        var jobs = await _repository.GetJobsAsync(result.CycleId!.Value);

        Assert.Equal(8, result.CandidateCount);
        var guitar = Assert.Single(
            jobs,
            job => job.Instrument == "Solo_Guitar"
                && job.SnapshotId == CandidateScrapeId);
        Assert.Equal(
            SnapshotGenerationRetentionJobStatus.Blocked,
            guitar.Status);
        Assert.Contains("active_snapshot", guitar.BlockerCodes);
        Assert.Contains("projection_source", guitar.BlockerCodes);
        Assert.DoesNotContain(
            jobs.Where(job =>
                job.Instrument != "Solo_Guitar"
                && job.SnapshotId == CandidateScrapeId),
            job => job.BlockerCodes.Contains(
                "active_snapshot",
                StringComparer.Ordinal)
                || job.BlockerCodes.Contains(
                    "projection_source",
                    StringComparer.Ordinal));
    }

    [Fact]
    public async Task Planner_NonReportOnlyHonorsBoundedPlanLimit()
    {
        SeedBaseline();
        var planner = CreatePlanner(
            new DatabaseMaintenanceOptions
            {
                SnapshotGenerationRetentionPlannerEnabled = true,
                SnapshotGenerationRetentionReportOnly = false,
                SnapshotGenerationRetentionNewestGenerationsToKeep = -5,
                SnapshotGenerationRetentionMinimumLaterSuccessfulPublications = -5,
                SnapshotGenerationRetentionMaxPlannedChildrenPerCycle = 3,
                SnapshotGenerationRetentionBlockUnreplayedWriterFailures = true,
            });

        var result = await planner.PlanAsync(Request());
        var jobs = await _repository.GetJobsAsync(result.CycleId!.Value);

        Assert.Equal(3, result.PlannedCount);
        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Planned,
            result.Disposition);
        Assert.Equal(
            3,
            jobs.Count(job =>
                job.Status ==
                SnapshotGenerationRetentionJobStatus.Planned));
        Assert.All(jobs, static job => Assert.False(job.ReportOnly));
    }

    [Fact]
    public async Task Planner_DoesNotDuplicateOutstandingChildIntent()
    {
        SeedBaseline();
        Execute(
            """
            INSERT INTO snapshot_generation_retention_cycles (
                trigger_scrape_id,
                trigger_publication_id,
                safe_point_kind,
                safe_point_at,
                planner_version,
                config_version,
                report_only,
                plan_digest,
                status,
                completed_at)
            VALUES (
                199,
                1999,
                'post_publication',
                now(),
                1,
                1,
                FALSE,
                repeat('e', 64),
                'planned',
                now());

            INSERT INTO snapshot_generation_retention_jobs (
                cycle_id,
                report_only,
                operation_kind,
                instrument,
                root_relation,
                child_relation,
                snapshot_id,
                child_oid,
                child_relfilenode,
                partition_bound,
                tablespace_name,
                row_estimate,
                total_bytes,
                status)
            SELECT
                currval(
                    'snapshot_generation_retention_cycles_cycle_id_seq'),
                FALSE,
                'drop_whole_child',
                'Solo_Guitar',
                'leaderboard_entries_snapshot_solo_guitar',
                relation.relname,
                100,
                relation.oid,
                relation.relfilenode,
                'FOR VALUES IN (''100'')',
                'pg_default',
                0,
                0,
                'planned'
            FROM pg_class relation
            JOIN pg_namespace namespace
              ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = 'public'
              AND relation.relname =
                    'leaderboard_entries_snapshot_solo_guitar_s100';
            """);
        var planner = CreatePlanner(
            CreateEnabledOptions(
                reportOnly: false,
                newestGenerationsToKeep: 0,
                minimumLaterSuccessfulPublications: 0,
                maxPlannedChildrenPerCycle: 3));

        var result = await planner.PlanAsync(Request());
        var jobs = await _repository.GetJobsAsync(
            result.CycleId!.Value);
        var guitar = Assert.Single(
            jobs,
            job => job.Instrument == "Solo_Guitar"
                && job.SnapshotId == CandidateScrapeId);

        Assert.Equal(
            SnapshotGenerationRetentionJobStatus.Deferred,
            guitar.Status);
        Assert.Contains(
            "existing_job_intent",
            guitar.BlockerCodes);
        Assert.Equal(3, result.PlannedCount);
    }

    [Fact]
    public async Task Planner_DefersExecutableWorkAfterJobSafetyFailure()
    {
        SeedBaseline();
        Execute(
            """
            INSERT INTO snapshot_generation_retention_cycles (
                trigger_scrape_id,
                trigger_publication_id,
                safe_point_kind,
                safe_point_at,
                planner_version,
                config_version,
                report_only,
                plan_digest,
                status,
                completed_at)
            VALUES (
                199,
                1999,
                'post_publication',
                now(),
                1,
                1,
                FALSE,
                repeat('f', 64),
                'planned',
                now());

            INSERT INTO snapshot_generation_retention_jobs (
                cycle_id,
                report_only,
                operation_kind,
                instrument,
                root_relation,
                child_relation,
                snapshot_id,
                child_oid,
                child_relfilenode,
                partition_bound,
                tablespace_name,
                row_estimate,
                total_bytes,
                status)
            VALUES (
                currval(
                    'snapshot_generation_retention_cycles_cycle_id_seq'),
                FALSE,
                'drop_whole_child',
                'Solo_Guitar',
                'leaderboard_entries_snapshot_solo_guitar',
                'leaderboard_entries_snapshot_solo_guitar_s99',
                99,
                99,
                99,
                'FOR VALUES IN (''99'')',
                'pg_default',
                0,
                0,
                'safety_failed');
            """);
        var planner = CreatePlanner(
            CreateEnabledOptions(
                reportOnly: false,
                newestGenerationsToKeep: 0,
                minimumLaterSuccessfulPublications: 0,
                maxPlannedChildrenPerCycle: 3));

        var result = await planner.PlanAsync(Request());
        var jobs = await _repository.GetJobsAsync(
            result.CycleId!.Value);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Deferred,
            result.Disposition);
        Assert.Equal(0, result.PlannedCount);
        Assert.Contains(
            jobs,
            job => job.SnapshotId == CandidateScrapeId
                && job.BlockerCodes.Contains(
                    "global_safety_failure",
                    StringComparer.Ordinal));
    }

    [Fact]
    public async Task Planner_CanDisableUnreplayedWriterFailureFenceExplicitly()
    {
        SeedBaseline();
        Execute(
            """
            INSERT INTO scrape_writer_failures (
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
                100,
                'snapshot',
                'Solo_Guitar',
                'candidate',
                1,
                1,
                'Injected',
                'injected',
                now());
            """);
        var planner = CreatePlanner(
            new DatabaseMaintenanceOptions
            {
                SnapshotGenerationRetentionPlannerEnabled = true,
                SnapshotGenerationRetentionReportOnly = true,
                SnapshotGenerationRetentionNewestGenerationsToKeep = 2,
                SnapshotGenerationRetentionMinimumLaterSuccessfulPublications = 2,
                SnapshotGenerationRetentionMaxPlannedChildrenPerCycle = 1,
                SnapshotGenerationRetentionBlockUnreplayedWriterFailures = false,
            });

        var result = await planner.PlanAsync(Request());
        var jobs = await _repository.GetJobsAsync(result.CycleId!.Value);

        Assert.Equal(9, result.CandidateCount);
        Assert.DoesNotContain(
            jobs.Where(job =>
                job.SnapshotId == CandidateScrapeId),
            job => job.BlockerCodes.Contains(
                "unreplayed_writer_failure",
                StringComparer.Ordinal));
    }

    [Fact]
    public async Task Planner_BlocksWhenCurrentSourceMapMissesCatalogKey()
    {
        SeedBaseline();
        Execute(
            """
            DELETE FROM leaderboard_published_scope_source
            WHERE published_scrape_id = 300
              AND instrument = 'Solo_Guitar';
            """);
        var planner = CreatePlanner();

        var result = await planner.PlanAsync(Request());
        var jobs = await _repository.GetJobsAsync(result.CycleId!.Value);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        var guitar = Assert.Single(
            jobs,
            job => job.Instrument == "Solo_Guitar"
                && job.SnapshotId == CandidateScrapeId);
        Assert.Contains(
            "publication_source_key_missing",
            guitar.BlockerCodes);
        Assert.All(
            jobs,
            job => Assert.Contains(
                "publication_source_binding_invalid",
                job.BlockerCodes));
    }

    [Fact]
    public async Task Planner_RecordsDefaultCatalogAndPolicyFences()
    {
        SeedBaseline();
        Execute(
            """
            SELECT ensure_leaderboard_snapshot_generation_partition(
                'Solo_Guitar',
                50);

            INSERT INTO leaderboard_entries_snapshot (
                snapshot_id,
                song_id,
                instrument,
                account_id,
                score,
                first_seen_at,
                last_updated_at)
            VALUES (
                50,
                'missing-scrape',
                'Solo_Guitar',
                'missing-scrape-account',
                1,
                now(),
                now());

            INSERT INTO leaderboard_entries_snapshot (
                snapshot_id,
                song_id,
                instrument,
                account_id,
                score,
                first_seen_at,
                last_updated_at)
            VALUES (
                175,
                'default-row',
                'Solo_Guitar',
                'default-account',
                1,
                now(),
                now());

            CREATE TABLE
                leaderboard_entries_snapshot_solo_bass_bad_name
                PARTITION OF
                    leaderboard_entries_snapshot_solo_bass
                FOR VALUES IN (175);

            CREATE TABLE
                leaderboard_entries_snapshot_solo_vocals_s176
                PARTITION OF
                    leaderboard_entries_snapshot_solo_vocals
                FOR VALUES IN (177);

            CREATE TABLE
                leaderboard_entries_snapshot_solo_drums_s177
                PARTITION OF
                    leaderboard_entries_snapshot_solo_drums
                FOR VALUES IN (177)
                PARTITION BY LIST (account_id);

            CREATE INDEX
                ix_retention_test_extra_leaf_index
                ON leaderboard_entries_snapshot_solo_guitar_s100 (
                    song_id);

            INSERT INTO scrape_writer_failures (
                scrape_id,
                writer_kind,
                instrument,
                song_id,
                page_count,
                row_count,
                artifact_path,
                exception_type,
                error_message,
                occurred_at)
            VALUES (
                100,
                'snapshot',
                'Solo_Guitar',
                'candidate',
                1,
                1,
                'artifact',
                'Injected',
                'injected',
                now());
            """);
        var planner = CreatePlanner(
            new DatabaseMaintenanceOptions
            {
                SnapshotGenerationRetentionPlannerEnabled = true,
                SnapshotGenerationRetentionReportOnly = true,
                SnapshotGenerationRetentionNewestGenerationsToKeep = 0,
                SnapshotGenerationRetentionMinimumLaterSuccessfulPublications = 3,
                SnapshotGenerationRetentionMaxPlannedChildrenPerCycle = 1,
                SnapshotGenerationRetentionBlockUnreplayedWriterFailures = true,
            },
            new ScraperOptions
            {
                ResumeScrapeId = CandidateScrapeId,
            });

        var result = await planner.PlanAsync(Request());
        var jobs = await _repository.GetJobsAsync(result.CycleId!.Value);
        var guitar = Assert.Single(
            jobs,
            job => job.Instrument == "Solo_Guitar"
                && job.SnapshotId == CandidateScrapeId);
        Assert.Contains("default_not_empty", guitar.BlockerCodes);
        Assert.Contains("configured_resume_scrape", guitar.BlockerCodes);
        Assert.Contains(
            "insufficient_later_publications",
            guitar.BlockerCodes);
        Assert.Contains(
            "unreplayed_writer_failure",
            guitar.BlockerCodes);
        Assert.Contains(
            "child_index_shape_invalid",
            guitar.BlockerCodes);

        var missingScrape = Assert.Single(
            jobs,
            job => job.Instrument == "Solo_Guitar"
                && job.SnapshotId == 50);
        Assert.Contains(
            "scrape_identity_missing",
            missingScrape.BlockerCodes);

        var bass = Assert.Single(
            jobs,
            job => job.Instrument == "Solo_Bass"
                && job.SnapshotId == CandidateScrapeId);
        Assert.Contains("malformed_child_name", bass.BlockerCodes);

        var vocals = Assert.Single(
            jobs,
            job => job.Instrument == "Solo_Vocals"
                && job.SnapshotId == 176);
        Assert.Contains(
            "child_shape_invalid",
            vocals.BlockerCodes);
        Assert.DoesNotContain(
            jobs,
            job => job.Instrument == "Solo_Drums"
                && job.SnapshotId == 177);
        Assert.All(
            jobs.Where(
                job => job.Instrument == "Solo_Drums"),
            job => Assert.Contains(
                "child_shape_invalid",
                job.BlockerCodes));
    }

    [Fact]
    public async Task Planner_QueryFailurePersistsFailedCycleInsteadOfEligibility()
    {
        SeedBaseline();
        Execute("DROP TABLE solo_current_projection_scope;");
        var planner = CreatePlanner();

        var result = await planner.PlanAsync(Request());

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Failed,
            result.Disposition);
        Assert.NotNull(result.CycleId);
        var cycle = await _repository.GetCycleForSafePointAsync(
            SnapshotGenerationRetentionContract
                .PostPublicationSafePoint,
            CurrentPublicationId);
        Assert.NotNull(cycle);
        Assert.Equal(
            SnapshotGenerationRetentionCycleStatus.Failed,
            cycle!.Status);
        Assert.NotNull(cycle.ErrorMessage);
        Assert.Empty(
            await _repository.GetJobsAsync(cycle.CycleId));
        Assert.Contains(
            await _repository.GetEvidenceAsync(cycle.CycleId),
            item => item.Phase == "failure"
                && item.Kind == "planner_exception");
    }

    [Fact]
    public async Task Planner_UsesNonblockingAdvisoryTransactionLock()
    {
        SeedBaseline();
        await using var blocker =
            await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction =
            await blocker.BeginTransactionAsync();
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT pg_advisory_xact_lock(@lockKey)";
            command.Parameters.AddWithValue(
                "lockKey",
                SnapshotGenerationRetentionContract
                    .PlannerAdvisoryLockKey);
            await command.ExecuteNonQueryAsync();
        }
        var planner = CreatePlanner();

        var result = await planner.PlanAsync(Request());

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Busy,
            result.Disposition);
        Assert.Equal(
            0,
            Scalar<int>(
                "SELECT COUNT(*) FROM snapshot_generation_retention_cycles"));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Planner_UsesPublicationLockBeforePlannerLock()
    {
        SeedBaseline();
        await using var blocker =
            await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction =
            await blocker.BeginTransactionAsync();
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT pg_advisory_xact_lock(@lockKey)";
            command.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            await command.ExecuteNonQueryAsync();
        }

        var result = await CreatePlanner(
            CreateEnabledOptions(
                newestGenerationsToKeep: 1))
            .PlanAsync(Request());

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Busy,
            result.Disposition);
        Assert.Equal(
            0,
            Scalar<int>(
                "SELECT COUNT(*) FROM snapshot_generation_retention_cycles"));
        await transaction.RollbackAsync();
    }

    public static IEnumerable<object[]> TerminalSafePointFailures()
    {
        yield return
        [
            """
            INSERT INTO scrape_log (
                id, started_at, completed_at, status)
            VALUES (301, now(), now(), 'completed');
            UPDATE publication_generations
            SET scrape_id = 301
            WHERE publication_id = 3000;
            """,
            "current_generation_scrape_mismatch",
            false,
        ];
        yield return
        [
            """
            UPDATE scrape_publication_state
            SET previous_publication_id =
                    current_publication_id
            WHERE id = TRUE;
            """,
            "publication_pointer_duplicate",
            false,
        ];
        yield return
        [
            """
            UPDATE publication_generations
            SET previous_publication_id = NULL
            WHERE publication_id = 3000;
            """,
            "publication_predecessor_mismatch",
            false,
        ];
        yield return
        [
            """
            UPDATE scrape_publication_state
            SET public_reads_frozen = TRUE
            WHERE id = TRUE;
            """,
            "public_reads_frozen",
            true,
        ];
        yield return
        [
            """
            UPDATE scrape_publication_state
            SET working_publication_id = 2000
            WHERE id = TRUE;
            """,
            "working_publication_present",
            true,
        ];
        yield return
        [
            """
            UPDATE scrape_publication_state
            SET publication_commit_intent_started_at = now(),
                publication_commit_intent_heartbeat_at = now(),
                publication_commit_intent_owner = 'test'
            WHERE id = TRUE;
            """,
            "publication_commit_intent_present",
            true,
        ];
        yield return
        [
            """
            UPDATE scrape_publication_state
            SET improvement_notifications_scrape_id = 300,
                improvement_notifications_status = 'pending',
                improvement_notifications_completed_at = NULL,
                improvement_notifications_projection_ready = TRUE,
                improvement_notifications_projection_scrape_id = 300
            WHERE id = TRUE;
            """,
            "improvement_notifications_incomplete",
            true,
        ];
        yield return
        [
            """
            ALTER TABLE scrape_publication_state
                DROP CONSTRAINT ck_scrape_publication_notification_plan;
            UPDATE scrape_publication_state
            SET improvement_notifications_scrape_id = 200,
                improvement_notifications_status = 'completed',
                improvement_notifications_completed_at = now(),
                improvement_notifications_projection_ready = TRUE,
                improvement_notifications_projection_scrape_id = 200
            WHERE id = TRUE;
            """,
            "improvement_notifications_incomplete",
            true,
        ];
        yield return
        [
            """
            INSERT INTO registered_users (
                device_id,
                account_id,
                registered_at)
            VALUES (
                'device',
                'missing-backfill-account',
                now());
            """,
            "registration_drain_incomplete",
            true,
        ];
        yield return
        [
            """
            INSERT INTO backfill_status (
                account_id,
                status)
            VALUES ('pending-account', 'pending');
            """,
            "registration_drain_incomplete",
            true,
        ];
        yield return
        [
            """
            INSERT INTO registered_users (
                device_id,
                account_id,
                registered_at)
            VALUES (
                'device',
                'history-account',
                now());
            INSERT INTO backfill_status (
                account_id,
                status,
                completed_at)
            VALUES (
                'history-account',
                'complete',
                now());
            INSERT INTO history_recon_status (
                account_id,
                status)
            VALUES (
                'history-account',
                'pending');
            """,
            "registration_drain_incomplete",
            true,
        ];
        yield return
        [
            """
            INSERT INTO scrape_log (
                id, started_at, status)
            VALUES (301, now(), 'running');
            """,
            "running_scrape",
            true,
        ];
    }

    [Theory]
    [MemberData(nameof(TerminalSafePointFailures))]
    public async Task Planner_HandlesTerminalGateFailureByRetryability(
        string mutationSql,
        string blockerCode,
        bool retryable)
    {
        SeedBaseline();
        Execute(mutationSql);

        var result = await CreatePlanner(
            CreateEnabledOptions(
                newestGenerationsToKeep: 1))
            .PlanAsync(Request());
        if (retryable)
        {
            Assert.Equal(
                SnapshotGenerationRetentionPlanDisposition.Deferred,
                result.Disposition);
            Assert.Null(result.CycleId);
            Assert.Contains(
                blockerCode,
                result.Reason,
                StringComparison.Ordinal);
            Assert.Equal(
                0,
                Scalar<int>(
                    "SELECT COUNT(*) FROM snapshot_generation_retention_cycles"));
            Assert.Equal(
                0,
                Scalar<int>(
                    "SELECT COUNT(*) FROM snapshot_generation_retention_jobs"));
            return;
        }

        var jobs = await _repository.GetJobsAsync(result.CycleId!.Value);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.DoesNotContain(
            jobs,
            static job =>
                job.Status is
                    SnapshotGenerationRetentionJobStatus.Planned
                    or SnapshotGenerationRetentionJobStatus.Observed);
        Assert.All(
            jobs,
            job => Assert.Contains(blockerCode, job.BlockerCodes));
    }

    [Fact]
    public async Task Planner_AcceptsCompletedNotificationStateForPublishedScrape()
    {
        SeedBaseline();
        Execute(
            """
            UPDATE scrape_publication_state
            SET improvement_notifications_scrape_id = 300,
                improvement_notifications_status = 'completed',
                improvement_notifications_completed_at = now(),
                improvement_notifications_projection_ready = TRUE,
                improvement_notifications_projection_scrape_id = 300
            WHERE id = TRUE;
            """);

        var result = await CreatePlanner().PlanAsync(Request());

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Observed,
            result.Disposition);
    }

    public static IEnumerable<object[]> SourceBindingFailures()
    {
        yield return
        [
            """
            UPDATE publication_surface_bindings
            SET row_count = row_count - 1
            WHERE publication_id = 3000
              AND surface_name = 'solo_scope_sources';
            """,
        ];
        yield return
        [
            """
            UPDATE publication_surface_bindings
            SET status = 'building'
            WHERE publication_id = 3000
              AND surface_name = 'solo_scope_sources';
            """,
        ];
        yield return
        [
            """
            UPDATE publication_surface_bindings
            SET binding_json =
                binding_json || '{"extra":true}'::jsonb
            WHERE publication_id = 3000
              AND surface_name = 'solo_scope_sources';
            """,
        ];
    }

    [Theory]
    [MemberData(nameof(SourceBindingFailures))]
    public async Task Planner_RequiresExactSourceBinding(
        string mutationSql)
    {
        SeedBaseline();
        Execute(mutationSql);

        var result = await CreatePlanner(
            CreateEnabledOptions(
                newestGenerationsToKeep: 0))
            .PlanAsync(Request());
        var jobs = await _repository.GetJobsAsync(result.CycleId!.Value);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.All(
            jobs,
            job => Assert.Contains(
                "publication_source_binding_invalid",
                job.BlockerCodes));
    }

    [Fact]
    public async Task Planner_RejectsExtraAndWrongScopeSourceKeys()
    {
        SeedBaseline();
        Execute(
            """
            UPDATE leaderboard_published_scope_source
            SET scope_kind = 'seasonal'
            WHERE published_scrape_id = 300
              AND instrument = 'Solo_Guitar';

            INSERT INTO leaderboard_published_scope_source (
                published_scrape_id,
                song_id,
                instrument,
                scope_kind,
                source_kind,
                source_snapshot_id,
                source_scrape_id,
                row_count,
                content_fingerprint,
                coverage_fingerprint,
                reported_total_entries,
                reported_total_pages,
                is_complete,
                created_at,
                validated_at)
            VALUES (
                300,
                'extra-song',
                'Solo_Bass',
                'alltime',
                'snapshot',
                300,
                300,
                1,
                'extra-content',
                'extra-coverage',
                1,
                1,
                TRUE,
                now(),
                now());
            """);

        var result = await CreatePlanner(
            CreateEnabledOptions(
                newestGenerationsToKeep: 1))
            .PlanAsync(Request());
        var jobs = await _repository.GetJobsAsync(result.CycleId!.Value);

        Assert.Contains(
            jobs.Where(static job =>
                job.Instrument == "Solo_Guitar"),
            static job => job.BlockerCodes.Contains(
                "publication_source_scope_invalid",
                StringComparer.Ordinal));
        Assert.Contains(
            jobs.Where(static job =>
                job.Instrument == "Solo_Bass"),
            static job => job.BlockerCodes.Contains(
                "publication_source_key_extra",
                StringComparer.Ordinal));
    }

    [Fact]
    public async Task Planner_ValidatesPreviousPublicationMap()
    {
        SeedBaseline();
        Execute(
            """
            DELETE FROM leaderboard_published_scope_source
            WHERE published_scrape_id = 200
              AND instrument = 'Solo_Guitar';
            """);

        var result = await CreatePlanner().PlanAsync(Request());
        var jobs = await _repository.GetJobsAsync(result.CycleId!.Value);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.Contains(
            jobs.Where(static job =>
                job.Instrument == "Solo_Guitar"),
            static job => job.BlockerCodes.Contains(
                "publication_source_key_missing",
                StringComparer.Ordinal));
    }

    [Fact]
    public async Task Planner_RejectsMalformedSourceAndCurrentFingerprintMismatch()
    {
        SeedBaseline();
        Execute(
            """
            UPDATE leaderboard_published_scope_source
            SET is_complete = FALSE
            WHERE published_scrape_id = 200
              AND instrument = 'Solo_Guitar';

            UPDATE leaderboard_scope_fingerprints
            SET content_fingerprint = 'mutated-current'
            WHERE song_id = 'catalog-song'
              AND instrument = 'Solo_Bass'
              AND scope_kind = 'alltime';
            """);

        var result = await CreatePlanner().PlanAsync(Request());
        var jobs = await _repository.GetJobsAsync(result.CycleId!.Value);

        Assert.Contains(
            jobs.Where(static job =>
                job.Instrument == "Solo_Guitar"),
            static job => job.BlockerCodes.Contains(
                "publication_source_invalid",
                StringComparer.Ordinal));
        Assert.Contains(
            jobs.Where(static job =>
                job.Instrument == "Solo_Bass"),
            static job => job.BlockerCodes.Contains(
                "current_fingerprint_mismatch",
                StringComparer.Ordinal));
    }

    [Fact]
    public async Task Planner_RejectsDuplicateCatalogSongIdentity()
    {
        SeedBaseline();
        Execute(
            """
            UPDATE publication_song_catalog
            SET catalog_json =
                '{"schemaVersion":2,"songs":[{"track":{"su":"catalog-song"}},{"track":{"su":"catalog-song"}}]}'::jsonb,
                song_count = 2
            WHERE publication_id = 3000;
            UPDATE publication_surface_bindings
            SET row_count = 2
            WHERE publication_id = 3000
              AND surface_name = 'song_catalog';
            """);

        var result = await CreatePlanner().PlanAsync(Request());
        var jobs = await _repository.GetJobsAsync(result.CycleId!.Value);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.All(
            jobs,
            static job => Assert.Contains(
                "publication_catalog_invalid",
                job.BlockerCodes));
    }

    [Fact]
    public async Task Planner_AcceptsAuthoritativeEmptyWithoutPhysicalLeaf()
    {
        SeedBaseline();
        MakeAuthoritativeEmpty(
            "Solo_Guitar",
            CurrentScrapeId,
            dropLeaf: true);

        var result = await CreatePlanner(
            CreateEnabledOptions(
                newestGenerationsToKeep: 1))
            .PlanAsync(Request());
        var jobs = await _repository.GetJobsAsync(result.CycleId!.Value);
        var candidate = Assert.Single(
            jobs,
            static job =>
                job.Instrument == "Solo_Guitar"
                && job.SnapshotId == CandidateScrapeId);

        Assert.Equal(9, result.CandidateCount);
        Assert.DoesNotContain(
            "protected_leaf_missing",
            candidate.BlockerCodes);
        Assert.DoesNotContain(
            "authoritative_empty_projection_invalid",
            candidate.BlockerCodes);
    }

    [Fact]
    public async Task Planner_RejectsAuthoritativeEmptyWithoutReadyMatchingProjection()
    {
        SeedBaseline();
        MakeAuthoritativeEmpty(
            "Solo_Guitar",
            CurrentScrapeId,
            dropLeaf: true);
        Execute(
            """
            UPDATE solo_current_projection_scope
            SET status = 'building'
            WHERE song_id = 'catalog-song'
              AND instrument = 'Solo_Guitar';
            """);

        var result = await CreatePlanner(
            CreateEnabledOptions(
                newestGenerationsToKeep: 1))
            .PlanAsync(Request());
        var jobs = await _repository.GetJobsAsync(result.CycleId!.Value);
        var candidate = Assert.Single(
            jobs,
            static job =>
                job.Instrument == "Solo_Guitar"
                && job.SnapshotId == CandidateScrapeId);

        Assert.Contains(
            "current_projection_mismatch",
            candidate.BlockerCodes);
    }

    [Fact]
    public async Task Planner_AllUnchangedCurrentAndPreviousWithoutLeavesDoNotBlockOlderCandidate()
    {
        SeedBaseline();
        MakeAuthoritativeEmpty(
            "Solo_Guitar",
            CurrentScrapeId,
            dropLeaf: true);
        Execute(
            """
            UPDATE leaderboard_published_scope_source
            SET source_kind = 'empty',
                source_snapshot_id = NULL,
                source_scrape_id = 200,
                row_count = 0,
                content_fingerprint = 'empty-previous-guitar',
                coverage_fingerprint = 'empty-previous-guitar',
                reported_total_entries = 0,
                reported_total_pages = 0,
                is_complete = TRUE
            WHERE published_scrape_id = 200
              AND song_id = 'catalog-song'
              AND instrument = 'Solo_Guitar'
              AND scope_kind = 'alltime';

            DROP TABLE
                leaderboard_entries_snapshot_solo_guitar_s200;
            """);

        var result = await CreatePlanner(
            CreateEnabledOptions(
                newestGenerationsToKeep: 0))
            .PlanAsync(Request());
        var jobs = await _repository.GetJobsAsync(result.CycleId!.Value);
        var candidate = Assert.Single(
            jobs,
            static job =>
                job.Instrument == "Solo_Guitar"
                && job.SnapshotId == CandidateScrapeId);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Observed,
            result.Disposition);
        Assert.DoesNotContain(
            "protected_leaf_missing",
            candidate.BlockerCodes);
        Assert.Contains(
            candidate.Status,
            new[]
            {
                SnapshotGenerationRetentionJobStatus.Observed,
                SnapshotGenerationRetentionJobStatus.Deferred,
            });
    }

    [Fact]
    public async Task Planner_RequiresPhysicalLeafForNonemptySnapshotSource()
    {
        SeedBaseline();
        InsertScrape(150, "completed", completed: true);
        Execute(
            """
            UPDATE leaderboard_published_scope_source
            SET source_snapshot_id = 150,
                source_scrape_id = 150
            WHERE published_scrape_id = 200
              AND instrument = 'Solo_Bass';
            """);

        var result = await CreatePlanner().PlanAsync(Request());
        var jobs = await _repository.GetJobsAsync(result.CycleId!.Value);
        var candidate = Assert.Single(
            jobs,
            static job =>
                job.Instrument == "Solo_Bass"
                && job.SnapshotId == CandidateScrapeId);

        Assert.Contains(
            "protected_leaf_missing",
            candidate.BlockerCodes);
    }

    [Fact]
    public async Task Planner_SameCountSourceMutationChangesPlanDigest()
    {
        SeedBaseline();
        var first = await CreatePlanner().PlanAsync(Request());

        await ResetRetentionPlanStateAsync();
        Execute(
            """
            UPDATE leaderboard_published_scope_source
            SET content_fingerprint = 'same-count-mutated'
            WHERE published_scrape_id = 200
              AND instrument = 'Solo_Guitar';
            """);

        var second = await CreatePlanner().PlanAsync(Request());

        Assert.NotEqual(first.PlanDigest, second.PlanDigest);
    }

    [Fact]
    public async Task Planner_ProjectionMutationChangesDigestAndFingerprintsReachJobEvidence()
    {
        SeedBaseline();
        var first = await CreatePlanner().PlanAsync(Request());
        var firstJobs =
            await _repository.GetJobsAsync(first.CycleId!.Value);
        var firstReference = Assert.Single(
            firstJobs,
            static job =>
                job.Instrument == "Solo_Guitar"
                && job.SnapshotId == CandidateScrapeId)
            .ReferenceEvidenceJson;

        Assert.Contains(
            "activeStateFingerprint",
            firstReference,
            StringComparison.Ordinal);
        Assert.Contains(
            "projectionFingerprint",
            firstReference,
            StringComparison.Ordinal);
        Assert.Contains(
            "sourceMapFingerprint",
            firstReference,
            StringComparison.Ordinal);

        await ResetRetentionPlanStateAsync();
        Execute(
            """
            UPDATE solo_current_projection_scope
            SET projection_generation =
                    projection_generation + 1
            WHERE song_id = 'catalog-song'
              AND instrument = 'Solo_Guitar';
            """);

        var second = await CreatePlanner().PlanAsync(Request());

        Assert.NotEqual(first.PlanDigest, second.PlanDigest);
    }

    [Fact]
    public async Task Planner_ConcurrentDuplicateAttemptPersistsOneCycle()
    {
        SeedBaseline();
        var firstPlanner = CreatePlanner();
        var secondPlanner = CreatePlanner();

        var results = await Task.WhenAll(
            firstPlanner.PlanAsync(Request()),
            secondPlanner.PlanAsync(Request()));
        var retry = await secondPlanner.PlanAsync(Request());

        Assert.Contains(
            results,
            static result => result.Disposition is
                SnapshotGenerationRetentionPlanDisposition.Observed
                or SnapshotGenerationRetentionPlanDisposition.Existing);
        Assert.All(
            results,
            static result => Assert.Contains(
                result.Disposition,
                new[]
                {
                    SnapshotGenerationRetentionPlanDisposition.Observed,
                    SnapshotGenerationRetentionPlanDisposition.Existing,
                    SnapshotGenerationRetentionPlanDisposition.Busy,
                }));
        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Existing,
            retry.Disposition);
        Assert.Equal(
            1,
            Scalar<int>(
                "SELECT COUNT(*) FROM snapshot_generation_retention_cycles"));
        Assert.Equal(
            27,
            Scalar<int>(
                "SELECT COUNT(*) FROM snapshot_generation_retention_jobs"));
    }

    [Fact]
    public void CatalogValidation_BlocksNonPgDefaultShape()
    {
        var state =
            SnapshotGenerationRetentionPlanner.BuildCatalogState(
                [
                    new(
                        1,
                        "leaderboard_entries_snapshot",
                        "p",
                        "LIST (instrument)",
                        0,
                        "",
                        "alternate",
                        null,
                        null,
                        0,
                        0),
                ],
                []);

        Assert.Contains(
            state.GlobalBlockers,
            blocker => blocker.Code == "non_pg_default");
    }

    [Fact]
    public void CatalogValidation_RequiresExactListPartitionKeys()
    {
        var state =
            SnapshotGenerationRetentionPlanner.BuildCatalogState(
                [
                    new(
                        1,
                        "leaderboard_entries_snapshot",
                        "p",
                        "RANGE (instrument)",
                        0,
                        "",
                        "pg_default",
                        null,
                        null,
                        0,
                        0),
                    new(
                        2,
                        "leaderboard_entries_snapshot_solo_guitar",
                        "p",
                        "HASH (snapshot_id)",
                        0,
                        "FOR VALUES IN ('Solo_Guitar')",
                        "pg_default",
                        1,
                        "leaderboard_entries_snapshot",
                        0,
                        0),
                ],
                []);

        Assert.Contains(
            state.GlobalBlockers,
            static blocker =>
                blocker.Code ==
                "snapshot_parent_partition_key_invalid");
        Assert.Contains(
            state.InstrumentBlockers["Solo_Guitar"],
            static blocker =>
                blocker.Code ==
                "root_partition_key_invalid");
    }

    [Fact]
    public void CatalogValidation_RejectsIncorrectTopIndexDefinition()
    {
        var state =
            SnapshotGenerationRetentionPlanner.BuildCatalogState(
                [
                    new(
                        1,
                        "leaderboard_entries_snapshot",
                        "p",
                        "LIST (instrument)",
                        0,
                        "",
                        "pg_default",
                        null,
                        null,
                        0,
                        0),
                ],
                [
                    new(
                        1,
                        "leaderboard_entries_snapshot",
                        10,
                        "leaderboard_entries_snapshot_pkey",
                        "I",
                        true,
                        true,
                        "pg_default",
                        null,
                        null,
                        true,
                        true,
                        "btree",
                        false,
                        false,
                        true,
                        "snapshot_id, song_id, instrument, account_id",
                        "0 0 0 0",
                        "3124 3126 3126 3126",
                        "0 100 100 0",
                        "CREATE UNIQUE INDEX leaderboard_entries_snapshot_pkey ON ONLY public.leaderboard_entries_snapshot USING btree (snapshot_id, song_id, instrument, account_id)"),
                    new(
                        1,
                        "leaderboard_entries_snapshot",
                        11,
                        "ix_les_snapshot_song_score",
                        "I",
                        true,
                        true,
                        "pg_default",
                        null,
                        null,
                        false,
                        false,
                        "btree",
                        false,
                        false,
                        true,
                        "snapshot_id, song_id, instrument, score",
                        "0 0 0 0",
                        "3124 3126 3126 1978",
                        "0 100 100 0",
                        "CREATE INDEX ix_les_snapshot_song_score ON ONLY public.leaderboard_entries_snapshot USING btree (snapshot_id, song_id, instrument, score)"),
                ]);

        Assert.Contains(
            state.GlobalBlockers,
            blocker =>
                blocker.Code ==
                    "snapshot_parent_index_shape_invalid");
    }

    [Theory]
    [InlineData(
        "empty",
        null,
        0,
        0,
        0,
        true,
        null)]
    [InlineData(
        "empty",
        100L,
        0,
        0,
        0,
        true,
        "malformed")]
    [InlineData(
        "snapshot",
        100L,
        1,
        1,
        1,
        true,
        null)]
    public void PublicationSourceValidation_IsFailClosed(
        string sourceKind,
        long? sourceSnapshotId,
        long rowCount,
        long totalEntries,
        int totalPages,
        bool complete,
        string? expectedError)
    {
        var source =
            new SnapshotGenerationRetentionPlanner.PublicationSource(
                300,
                "song",
                "Solo_Guitar",
                "alltime",
                sourceKind,
                sourceSnapshotId,
                sourceSnapshotId ?? 300,
                rowCount,
                "content",
                "coverage",
                totalEntries,
                totalPages,
                complete);

        var error =
            SnapshotGenerationRetentionPlanner
                .ValidatePublicationSource(source);

        if (expectedError is null)
            Assert.Null(error);
        else
            Assert.Contains(expectedError, error!, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicationSourceValidation_RejectsEmptyOutsideAlltime()
    {
        var source =
            new SnapshotGenerationRetentionPlanner.PublicationSource(
                300,
                "song",
                "Solo_Guitar",
                "seasonal",
                "empty",
                null,
                300,
                0,
                "content",
                "coverage",
                0,
                0,
                true);

        var error =
            SnapshotGenerationRetentionPlanner
                .ValidatePublicationSource(source);

        Assert.Contains(
            "alltime",
            error!,
            StringComparison.Ordinal);
    }

    private SnapshotGenerationRetentionPlanner CreatePlanner(
        DatabaseMaintenanceOptions? options = null,
        ScraperOptions? scraperOptions = null) =>
        new(
            _fixture.DataSource,
            _repository,
            Options.Create(options ?? CreateEnabledOptions()),
            Options.Create(scraperOptions ?? new ScraperOptions()),
            NullLogger<SnapshotGenerationRetentionPlanner>.Instance);

    private static DatabaseMaintenanceOptions CreateEnabledOptions(
        bool reportOnly = true,
        int newestGenerationsToKeep = 2,
        int minimumLaterSuccessfulPublications = 2,
        int maxPlannedChildrenPerCycle = 1,
        bool blockUnreplayedWriterFailures = true) =>
        new()
        {
            SnapshotGenerationRetentionPlannerEnabled = true,
            SnapshotGenerationRetentionReportOnly = reportOnly,
            SnapshotGenerationRetentionNewestGenerationsToKeep =
                newestGenerationsToKeep,
            SnapshotGenerationRetentionMinimumLaterSuccessfulPublications =
                minimumLaterSuccessfulPublications,
            SnapshotGenerationRetentionMaxPlannedChildrenPerCycle =
                maxPlannedChildrenPerCycle,
            SnapshotGenerationRetentionBlockUnreplayedWriterFailures =
                blockUnreplayedWriterFailures,
        };

    private static SnapshotGenerationRetentionPlanRequest Request() =>
        new(
            CurrentScrapeId,
            CurrentPublicationId,
            new DateTime(
                2026,
                8,
                24,
                1,
                0,
                0,
                DateTimeKind.Utc));

    private void SeedBaseline()
    {
        InsertScrape(
            CandidateScrapeId,
            "failed",
            completed: false);
        InsertScrape(
            PreviousScrapeId,
            "completed",
            completed: true);
        InsertScrape(
            CurrentScrapeId,
            "completed",
            completed: true);

        Execute(
            """
            INSERT INTO publication_generations (
                publication_id,
                scrape_id,
                status,
                previous_publication_id,
                created_at,
                source_cut_at,
                ready_at,
                published_at)
            VALUES
                (2000, 200, 'retained', NULL, now(), now(), now(), now()),
                (3000, 300, 'current', 2000, now(), now(), now(), now());

            UPDATE scrape_publication_state
            SET published_scrape_id = 300,
                published_at = now(),
                public_reads_frozen = FALSE,
                current_publication_id = 3000,
                previous_publication_id = 2000,
                working_publication_id = NULL,
                publication_commit_intent_started_at = NULL,
                publication_commit_intent_heartbeat_at = NULL,
                publication_commit_intent_owner = NULL,
                improvement_notifications_scrape_id = NULL,
                improvement_notifications_status = 'disabled',
                improvement_notifications_completed_at = NULL,
                improvement_notifications_projection_scopes = '[]'::jsonb,
                improvement_notifications_projection_ready = FALSE,
                improvement_notifications_projection_scrape_id = NULL,
                updated_at = now()
            WHERE id = TRUE;
            """);

        InsertPublicationCatalogAndBinding(
            PreviousPublicationId,
            PreviousScrapeId);
        InsertPublicationCatalogAndBinding(
            CurrentPublicationId,
            CurrentScrapeId);

        foreach (var instrument in
                 SnapshotGenerationRetentionContract.Instruments)
        {
            foreach (var snapshotId in new[]
                     {
                         CandidateScrapeId,
                         PreviousScrapeId,
                         CurrentScrapeId,
                     })
            {
                EnsureGeneration(
                    instrument.Instrument,
                    snapshotId);
                InsertSnapshotRow(
                    instrument.Instrument,
                    snapshotId);
            }

            Execute(
                """
                INSERT INTO leaderboard_snapshot_state (
                    song_id,
                    instrument,
                    active_snapshot_id,
                    scrape_id,
                    is_finalized,
                    updated_at)
                VALUES (
                    @songId,
                    @instrument,
                    @snapshotId,
                    @snapshotId,
                    TRUE,
                    now());

                INSERT INTO solo_current_projection_scope (
                    song_id,
                    instrument,
                    projection_generation,
                    row_count,
                    source_snapshot_id,
                    source_kind,
                    status,
                    updated_at)
                VALUES (
                    @songId,
                    @instrument,
                    1,
                    1,
                    @snapshotId,
                    'snapshot',
                    'ready',
                    now());
                """,
                command =>
                {
                    command.Parameters.AddWithValue(
                        "songId",
                        CatalogSongId);
                    command.Parameters.AddWithValue(
                        "instrument",
                        instrument.Instrument);
                    command.Parameters.AddWithValue(
                        "snapshotId",
                        CurrentScrapeId);
                });

            InsertCurrentFingerprint(
                instrument.Instrument,
                CurrentScrapeId,
                "current");

            InsertPublicationSource(
                PreviousScrapeId,
                instrument.Instrument,
                PreviousScrapeId,
                "previous");
            InsertPublicationSource(
                CurrentScrapeId,
                instrument.Instrument,
                CurrentScrapeId,
                "current");
        }

        InsertSourceBinding(
            PreviousPublicationId,
            PreviousScrapeId);
        InsertSourceBinding(
            CurrentPublicationId,
            CurrentScrapeId);
    }

    private void InsertScrape(
        long scrapeId,
        string status,
        bool completed)
    {
        Execute(
            """
            INSERT INTO scrape_log (
                id,
                started_at,
                completed_at,
                status,
                failed_at,
                failure_phase,
                failure_message)
            VALUES (
                @scrapeId,
                now() - interval '2 days',
                CASE WHEN @completed
                    THEN now() - interval '1 day'
                    ELSE NULL
                END,
                @status,
                CASE WHEN @status = 'failed'
                    THEN now() - interval '1 day'
                    ELSE NULL
                END,
                CASE WHEN @status = 'failed'
                    THEN 'writer'
                    ELSE NULL
                END,
                CASE WHEN @status = 'failed'
                    THEN 'fixture failure'
                    ELSE NULL
                END)
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "scrapeId",
                    checked((int)scrapeId));
                command.Parameters.AddWithValue(
                    "completed",
                    completed);
                command.Parameters.AddWithValue("status", status);
            });
    }

    private void AddWorkingPublication()
    {
        InsertScrape(
            WorkingScrapeId,
            "completed",
            completed: true);
        Execute(
            """
            INSERT INTO publication_generations (
                publication_id,
                scrape_id,
                status,
                created_at,
                source_cut_at,
                ready_at)
            VALUES (
                2500,
                250,
                'ready',
                now(),
                now(),
                now());

            UPDATE scrape_publication_state
            SET working_publication_id = 2500,
                updated_at = now()
            WHERE id = TRUE;
            """);
        InsertPublicationCatalogAndBinding(
            WorkingPublicationId,
            WorkingScrapeId);
        foreach (var instrument in
                 SnapshotGenerationRetentionContract.Instruments)
        {
            EnsureGeneration(
                instrument.Instrument,
                WorkingScrapeId);
            InsertSnapshotRow(
                instrument.Instrument,
                WorkingScrapeId);
            InsertPublicationSource(
                WorkingScrapeId,
                instrument.Instrument,
                WorkingScrapeId,
                "working");
        }
        InsertSourceBinding(
            WorkingPublicationId,
            WorkingScrapeId);
    }

    private void EnsureGeneration(
        string instrument,
        long snapshotId)
    {
        Execute(
            """
            SELECT ensure_leaderboard_snapshot_generation_partition(
                @instrument,
                @snapshotId)
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "instrument",
                    instrument);
                command.Parameters.AddWithValue(
                    "snapshotId",
                    snapshotId);
            });
    }

    private void InsertSnapshotRow(
        string instrument,
        long snapshotId)
    {
        Execute(
            """
            INSERT INTO leaderboard_entries_snapshot (
                snapshot_id,
                song_id,
                instrument,
                account_id,
                score,
                first_seen_at,
                last_updated_at)
            VALUES (
                @snapshotId,
                @songId,
                @instrument,
                @accountId,
                @score,
                now(),
                now())
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "snapshotId",
                    snapshotId);
                command.Parameters.AddWithValue(
                    "songId",
                    CatalogSongId);
                command.Parameters.AddWithValue(
                    "instrument",
                    instrument);
                command.Parameters.AddWithValue(
                    "accountId",
                    $"account-{instrument}-{snapshotId}");
                command.Parameters.AddWithValue(
                    "score",
                    checked((int)snapshotId));
            });
    }

    private void InsertPublicationSource(
        long publishedScrapeId,
        string instrument,
        long sourceSnapshotId,
        string label)
    {
        Execute(
            """
            INSERT INTO leaderboard_published_scope_source (
                published_scrape_id,
                song_id,
                instrument,
                scope_kind,
                source_kind,
                source_snapshot_id,
                source_scrape_id,
                row_count,
                content_fingerprint,
                coverage_fingerprint,
                reported_total_entries,
                reported_total_pages,
                is_complete,
                created_at,
                validated_at)
            VALUES (
                @publishedScrapeId,
                @songId,
                @instrument,
                'alltime',
                'snapshot',
                @sourceSnapshotId,
                @sourceSnapshotId,
                1,
                @fingerprint,
                @fingerprint,
                1,
                1,
                TRUE,
                now(),
                now())
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "publishedScrapeId",
                    publishedScrapeId);
                command.Parameters.AddWithValue(
                    "songId",
                    CatalogSongId);
                command.Parameters.AddWithValue(
                    "instrument",
                    instrument);
                command.Parameters.AddWithValue(
                    "sourceSnapshotId",
                    sourceSnapshotId);
                command.Parameters.AddWithValue(
                    "fingerprint",
                    $"{label}-{instrument}");
            });
    }

    private void InsertPublicationCatalogAndBinding(
        long publicationId,
        long scrapeId)
    {
        var catalog = SongCatalogSnapshotBuilder.Create(
        [
            new Song
            {
                _title = "Catalog Song",
                track = new Track
                {
                    su = CatalogSongId,
                    tt = "Catalog Song",
                },
            },
        ]);
        Execute(
            """
            INSERT INTO publication_song_catalog (
                publication_id,
                catalog_version,
                schema_version,
                catalog_json,
                content_hash,
                song_count,
                source_kind,
                is_exact,
                source_captured_at,
                captured_at)
            VALUES (
                @publicationId,
                @publicationId,
                @schemaVersion,
                @catalogJson,
                @contentHash,
                @songCount,
                'provider_exact',
                TRUE,
                now(),
                now());

            INSERT INTO publication_surface_bindings (
                publication_id,
                surface_name,
                binding_kind,
                binding_json,
                row_count,
                content_hash,
                status,
                built_at)
            VALUES (
                @publicationId,
                'song_catalog',
                'generation_catalog_snapshot',
                jsonb_build_object(
                    'table',
                    'publication_song_catalog',
                    'publicationId',
                    @publicationId),
                @songCount,
                @contentHash,
                'ready',
                now());
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "publicationId",
                    publicationId);
                command.Parameters.AddWithValue(
                    "scrapeId",
                    scrapeId);
                command.Parameters.AddWithValue(
                    "schemaVersion",
                    SongCatalogSnapshotBuilder.SchemaVersion);
                command.Parameters.AddWithValue(
                    "catalogJson",
                    NpgsqlTypes.NpgsqlDbType.Jsonb,
                    catalog.CatalogJson);
                command.Parameters.AddWithValue(
                    "contentHash",
                    catalog.ContentHash);
                command.Parameters.AddWithValue(
                    "songCount",
                    catalog.SongCount);
            });
    }

    private void InsertSourceBinding(
        long publicationId,
        long scrapeId)
    {
        Execute(
            """
            INSERT INTO publication_surface_bindings (
                publication_id,
                surface_name,
                binding_kind,
                binding_json,
                row_count,
                content_hash,
                status,
                built_at)
            VALUES (
                @publicationId,
                'solo_scope_sources',
                'scrape_id',
                jsonb_build_object(
                    'publicationId',
                    @publicationId,
                    'table',
                    'leaderboard_published_scope_source',
                    'publishedScrapeId',
                    @scrapeId),
                @rowCount,
                NULL,
                'ready',
                now());
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "publicationId",
                    publicationId);
                command.Parameters.AddWithValue(
                    "scrapeId",
                    scrapeId);
                command.Parameters.AddWithValue(
                    "rowCount",
                    SnapshotGenerationRetentionContract
                        .Instruments.Count);
            });
    }

    private void InsertCurrentFingerprint(
        string instrument,
        long scrapeId,
        string label)
    {
        Execute(
            """
            INSERT INTO leaderboard_scope_fingerprints (
                song_id,
                instrument,
                scope_kind,
                fingerprint_version,
                content_fingerprint,
                coverage_fingerprint,
                entry_count,
                reported_total_entries,
                reported_total_pages,
                is_complete,
                source_scrape_id,
                published_scrape_id,
                first_seen_scrape_id,
                last_changed_scrape_id,
                last_seen_scrape_id,
                changed_at,
                seen_at)
            VALUES (
                @songId,
                @instrument,
                'alltime',
                2,
                @fingerprint,
                @fingerprint,
                1,
                1,
                1,
                TRUE,
                @scrapeId,
                @scrapeId,
                @scrapeId,
                @scrapeId,
                @scrapeId,
                now(),
                now());
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "songId",
                    CatalogSongId);
                command.Parameters.AddWithValue(
                    "instrument",
                    instrument);
                command.Parameters.AddWithValue(
                    "scrapeId",
                    scrapeId);
                command.Parameters.AddWithValue(
                    "fingerprint",
                    $"{label}-{instrument}");
            });
    }

    private void MakeAuthoritativeEmpty(
        string instrument,
        long scrapeId,
        bool dropLeaf)
    {
        Execute(
            """
            UPDATE leaderboard_published_scope_source
            SET source_kind = 'empty',
                source_snapshot_id = NULL,
                source_scrape_id = @scrapeId,
                row_count = 0,
                content_fingerprint = @fingerprint,
                coverage_fingerprint = @fingerprint,
                reported_total_entries = 0,
                reported_total_pages = 0,
                is_complete = TRUE
            WHERE published_scrape_id = @scrapeId
              AND song_id = @songId
              AND instrument = @instrument
              AND scope_kind = 'alltime';

            UPDATE leaderboard_scope_fingerprints
            SET content_fingerprint = @fingerprint,
                coverage_fingerprint = @fingerprint,
                entry_count = 0,
                reported_total_entries = 0,
                reported_total_pages = 0,
                is_complete = TRUE,
                source_scrape_id = @scrapeId,
                published_scrape_id = @scrapeId,
                last_seen_scrape_id = @scrapeId,
                seen_at = now()
            WHERE song_id = @songId
              AND instrument = @instrument
              AND scope_kind = 'alltime';

            UPDATE solo_current_projection_scope
            SET row_count = 0,
                source_snapshot_id = @scrapeId,
                source_kind = 'snapshot',
                status = 'ready',
                error_message = NULL,
                updated_at = now()
            WHERE song_id = @songId
              AND instrument = @instrument;
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "scrapeId",
                    scrapeId);
                command.Parameters.AddWithValue(
                    "songId",
                    CatalogSongId);
                command.Parameters.AddWithValue(
                    "instrument",
                    instrument);
                command.Parameters.AddWithValue(
                    "fingerprint",
                    $"empty-{scrapeId}-{instrument}");
            });
        if (!dropLeaf)
            return;

        if (instrument != "Solo_Guitar"
            || scrapeId != CurrentScrapeId)
        {
            throw new InvalidOperationException(
                "The authoritative-empty test helper only drops the current Solo Guitar leaf.");
        }
        Execute(
            "DROP TABLE leaderboard_entries_snapshot_solo_guitar_s300;");
    }

    private async Task ResetRetentionPlanStateAsync()
    {
        Execute(
            """
            DROP TRIGGER
                trg_reject_snapshot_generation_retention_evidence_mutation
                ON snapshot_generation_retention_evidence;
            DELETE FROM snapshot_generation_retention_evidence;
            DELETE FROM snapshot_generation_retention_jobs;
            DELETE FROM snapshot_generation_retention_cycles;
            """);
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
    }

    private T Scalar<T>(string sql)
    {
        using var connection =
            _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(
            command.ExecuteScalar()!,
            typeof(T));
    }

    private void Execute(
        string sql,
        Action<NpgsqlCommand>? configure = null)
    {
        using var connection =
            _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        command.ExecuteNonQuery();
    }
}
