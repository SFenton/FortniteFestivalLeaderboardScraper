using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class InterruptedAcquisitionNormalizationCommandTests
{
    [Fact]
    public void Parse_accepts_exact_typed_command()
    {
        var command =
            InterruptedAcquisitionNormalizationCommand.Parse(
                CommandArguments(execute: true));

        Assert.NotNull(command);
        Assert.True(command!.Execute);
        Assert.False(command.CheckOnly);
        Assert.Equal(1407, command.ScrapeId);
        Assert.Equal(1406, command.ExpectedPublishedScrapeId);
        Assert.Equal(312, command.ExpectedCurrentPublicationId);
        Assert.Equal(310, command.ExpectedPreviousPublicationId);
        Assert.Equal(314, command.ExpectedWorkingPublicationId);
        Assert.Equal(
            ExactState.WorkerInstanceId,
            command.ExpectedWorkerInstanceId);
        Assert.Equal(
            ExactState.WorkerFreshnessUtc,
            command.ExpectedWorkerFreshnessUtc);
        Assert.Equal(
            InterruptedAcquisitionNormalizationCommand
                .AcquisitionPhaseId,
            command.ExpectedPhaseId);
        Assert.Equal(1, command.ExpectedAttempt);
    }

    [Theory]
    [InlineData("--api-only")]
    [InlineData("--interrupted-acquisition-phase-id=post.compute_rankings")]
    [InlineData("--interrupted-acquisition-attempt=0")]
    [InlineData("--interrupted-acquisition-worker-freshness-utc=2026-09-18T13:34:43-07:00")]
    public void Parse_rejects_non_exact_command_shape(
        string replacement)
    {
        var args = CommandArguments(execute: false).ToList();
        if (replacement == "--api-only")
        {
            args.Add(replacement);
        }
        else
        {
            var flag = replacement[..replacement.IndexOf('=')];
            var index = args.FindIndex(argument =>
                argument.StartsWith(
                    flag,
                    StringComparison.Ordinal));
            args[index] = replacement;
        }

        Assert.Throws<ArgumentException>(() =>
            InterruptedAcquisitionNormalizationCommand.Parse(args));
    }

    internal static IReadOnlyList<string> CommandArguments(
        bool execute)
        =>
        [
            InterruptedAcquisitionNormalizationCommand.MaintenanceFlag,
            execute
                ? InterruptedAcquisitionNormalizationCommand.ExecuteFlag
                : InterruptedAcquisitionNormalizationCommand.CheckFlag,
            $"{InterruptedAcquisitionNormalizationCommand.ScrapeIdFlag}=1407",
            $"{InterruptedAcquisitionNormalizationCommand.PublishedScrapeIdFlag}=1406",
            $"{InterruptedAcquisitionNormalizationCommand.CurrentPublicationIdFlag}=312",
            $"{InterruptedAcquisitionNormalizationCommand.PreviousPublicationIdFlag}=310",
            $"{InterruptedAcquisitionNormalizationCommand.WorkingPublicationIdFlag}=314",
            $"{InterruptedAcquisitionNormalizationCommand.WorkerInstanceIdFlag}={ExactState.WorkerInstanceId}",
            $"{InterruptedAcquisitionNormalizationCommand.WorkerFreshnessFlag}=2026-09-18T20:34:43.441926Z",
            $"{InterruptedAcquisitionNormalizationCommand.PhaseIdFlag}={InterruptedAcquisitionNormalizationCommand.AcquisitionPhaseId}",
            $"{InterruptedAcquisitionNormalizationCommand.AttemptFlag}=1",
        ];
}

public sealed class InterruptedAcquisitionNormalizationTests :
    IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    private MetaDatabase Db => _fixture.Db;
    private NpgsqlDataSource DataSource => _fixture.DataSource;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Check_is_read_only_and_accepts_exact_interrupted_state()
    {
        var state = SeedExactState();
        var before = ReadProtectedState();

        var readiness =
            Db.GetInterruptedAcquisitionNormalizationReadiness(
                state.Command);

        var after = ReadProtectedState();
        Assert.Equal(before, after);
        Assert.True(readiness.SchemaReady);
        Assert.True(readiness.FenceAcquired);
        Assert.True(readiness.ReadyToNormalize);
        Assert.False(readiness.AlreadyNormalized);
        Assert.True(readiness.CanExecute);
        Assert.Equal("ready", readiness.State);
        Assert.Null(readiness.BlockingReason);
        Assert.Equal(1406, readiness.PublishedScrapeId);
        Assert.Equal(312, readiness.CurrentPublicationId);
        Assert.Equal(310, readiness.PreviousPublicationId);
        Assert.Equal(314, readiness.WorkingPublicationId);
        Assert.Equal("interrupted", readiness.AttemptStatus);
        Assert.Equal("offline", readiness.WorkerStatus);
        Assert.True(readiness.WorkerCurrentOperationPresent);
    }

    [Fact]
    public void Execute_changes_only_exact_attempt_and_worker_operation()
    {
        var state = SeedExactState();
        var before = ReadMutationIsolationState();

        var result =
            Db.ExecuteInterruptedAcquisitionNormalization(
                state.Command with
                {
                    Execute = true,
                    CheckOnly = false,
                });

        var after = ReadMutationIsolationState();
        Assert.True(result.Succeeded);
        Assert.False(result.AlreadyNormalized);
        Assert.NotNull(result.MutationReadiness);
        Assert.NotNull(result.After);
        Assert.True(result.After!.AlreadyNormalized);
        Assert.NotNull(result.OfficialIsolationReadiness);
        Assert.True(
            result.OfficialIsolationReadiness!
                .AcquisitionFailureMutationRequired);
        Assert.True(result.OfficialIsolationReadiness.CanExecute);

        Assert.Equal(before.Scrape, after.Scrape);
        Assert.Equal(before.Publication, after.Publication);
        Assert.Equal(before.Generations, after.Generations);
        Assert.Equal(before.SourceMappings, after.SourceMappings);
        Assert.Equal(
            before.AttemptWithoutNormalizedFields,
            after.AttemptWithoutNormalizedFields);
        Assert.Equal(
            before.WorkerWithoutOperationFields,
            after.WorkerWithoutOperationFields);
        Assert.Equal("failed", after.AttemptStatus);
        Assert.Equal(
            MetaDatabase
                .InterruptedAcquisitionNormalizationMessage,
            after.AttemptError);
        Assert.False(after.WorkerCurrentOperationPresent);
        Assert.Equal("failed", after.WorkerLastOperationStatus);
        Assert.Equal(
            InterruptedAcquisitionNormalizationCommand
                .AcquisitionPhaseId,
            after.WorkerLastOperationPhaseId);
        Assert.Equal(1, after.WorkerLastOperationAttempt);
        Assert.Equal(1407, after.WorkerLastOperationScrapeId);
        Assert.Equal(
            MetaDatabase
                .InterruptedAcquisitionNormalizationMessage,
            after.WorkerLastOperationDetail);
    }

    [Fact]
    public void Execute_is_idempotent_only_for_exact_normalized_state()
    {
        var state = SeedExactState();
        var command = state.Command with
        {
            Execute = true,
            CheckOnly = false,
        };
        var first =
            Db.ExecuteInterruptedAcquisitionNormalization(command);
        var afterFirst = ReadProtectedState();

        var second =
            Db.ExecuteInterruptedAcquisitionNormalization(command);
        var afterSecond = ReadProtectedState();

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.True(second.AlreadyNormalized);
        Assert.Equal("already_normalized", second.After?.State);
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public void Normalized_state_is_immediately_ready_for_official_isolation()
    {
        var state = SeedExactState();

        var normalization =
            Db.ExecuteInterruptedAcquisitionNormalization(
                state.Command with
                {
                    Execute = true,
                    CheckOnly = false,
                });
        var official =
            Db.GetActiveScrapeFailureIsolationReadiness(
                ExactState.ActiveScrapeId,
                ExactState.PublishedScrapeId);

        Assert.True(normalization.Succeeded);
        Assert.True(official.CanExecute);
        Assert.True(
            official.AcquisitionFailureMutationRequired);
        Assert.False(official.PublicationMutationRequired);
        Assert.Equal(1, official.FailedAcquisitionPhaseAttemptCount);
        Assert.Equal(0, official.RunningPhaseAttemptCount);
        Assert.False(official.WorkerCurrentOperationPresent);
        Assert.Equal("offline", official.WorkerStatus);
        Assert.False(official.AcquisitionCheckpointPresent);
        Assert.Equal(
            ExactState.WorkingPublicationId,
            official.WorkingPublicationId);
    }

    [Fact]
    public void Execute_rejects_replacement_worker_race()
    {
        var state = SeedExactState();
        Db.InterruptedAcquisitionNormalizationBeforeFenceTestHook =
            () => ReplaceWorker();
        try
        {
            var result =
                Db.ExecuteInterruptedAcquisitionNormalization(
                    state.Command with
                    {
                        Execute = true,
                        CheckOnly = false,
                    });

            Assert.False(result.Succeeded);
            Assert.NotNull(result.MutationReadiness);
            Assert.Contains(
                "worker instance identity",
                result.Error ?? result.MutationReadiness!.BlockingReason,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                "interrupted",
                ReadMutationIsolationState().AttemptStatus);
        }
        finally
        {
            Db.InterruptedAcquisitionNormalizationBeforeFenceTestHook =
                null;
        }
    }

    [Fact]
    public void Execute_rejects_replacement_worker_after_official_readiness()
    {
        var state = SeedExactState();
        Db.InterruptedAcquisitionNormalizationBeforeOfficialReadinessTestHook =
            () => ReplaceWorker();
        try
        {
            var result =
                Db.ExecuteInterruptedAcquisitionNormalization(
                    state.Command with
                    {
                        Execute = true,
                        CheckOnly = false,
                    });

            Assert.False(result.Succeeded);
            Assert.NotNull(result.OfficialIsolationReadiness);
            Assert.True(result.OfficialIsolationReadiness!.CanExecute);
            Assert.NotNull(result.After);
            Assert.False(result.After!.AlreadyNormalized);
            Assert.Contains(
                "worker instance identity",
                result.After.BlockingReason,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                "failed",
                ReadMutationIsolationState().AttemptStatus);
        }
        finally
        {
            Db.InterruptedAcquisitionNormalizationBeforeOfficialReadinessTestHook =
                null;
        }
    }

    [Fact]
    public void Execute_rejects_newer_attempt_after_official_readiness()
    {
        var state = SeedExactState();
        Db.InterruptedAcquisitionNormalizationBeforeOfficialReadinessTestHook =
            () => AddInterruptedAttempt(
                state,
                InterruptedAcquisitionNormalizationCommand
                    .AcquisitionPhaseId);
        try
        {
            var result =
                Db.ExecuteInterruptedAcquisitionNormalization(
                    state.Command with
                    {
                        Execute = true,
                        CheckOnly = false,
                    });

            Assert.False(result.Succeeded);
            Assert.NotNull(result.OfficialIsolationReadiness);
            Assert.True(result.OfficialIsolationReadiness!.CanExecute);
            Assert.NotNull(result.After);
            Assert.False(result.After!.AlreadyNormalized);
            Assert.Equal(2, result.After.CandidateAttemptCount);
            Assert.Contains(
                "exactly one phase attempt",
                result.After.BlockingReason,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                "failed",
                ReadMutationIsolationState().AttemptStatus);
        }
        finally
        {
            Db.InterruptedAcquisitionNormalizationBeforeOfficialReadinessTestHook =
                null;
        }
    }

    [Fact]
    public void Already_normalized_retry_rejects_replacement_worker_after_official_readiness()
    {
        var state = SeedExactState();
        var command = state.Command with
        {
            Execute = true,
            CheckOnly = false,
        };
        var first =
            Db.ExecuteInterruptedAcquisitionNormalization(command);
        Assert.True(first.Succeeded);

        Db.InterruptedAcquisitionNormalizationBeforeOfficialReadinessTestHook =
            () => ReplaceWorker();
        try
        {
            var retry =
                Db.ExecuteInterruptedAcquisitionNormalization(command);

            Assert.False(retry.Succeeded);
            Assert.True(retry.Before.AlreadyNormalized);
            Assert.NotNull(retry.OfficialIsolationReadiness);
            Assert.True(retry.OfficialIsolationReadiness!.CanExecute);
            Assert.NotNull(retry.After);
            Assert.False(retry.After!.AlreadyNormalized);
            Assert.Contains(
                "worker instance identity",
                retry.After.BlockingReason,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Db.InterruptedAcquisitionNormalizationBeforeOfficialReadinessTestHook =
                null;
        }
    }

    [Fact]
    public void Already_normalized_retry_rejects_newer_attempt_after_official_readiness()
    {
        var state = SeedExactState();
        var command = state.Command with
        {
            Execute = true,
            CheckOnly = false,
        };
        var first =
            Db.ExecuteInterruptedAcquisitionNormalization(command);
        Assert.True(first.Succeeded);

        Db.InterruptedAcquisitionNormalizationBeforeOfficialReadinessTestHook =
            () => AddInterruptedAttempt(
                state,
                InterruptedAcquisitionNormalizationCommand
                    .AcquisitionPhaseId);
        try
        {
            var retry =
                Db.ExecuteInterruptedAcquisitionNormalization(command);

            Assert.False(retry.Succeeded);
            Assert.True(retry.Before.AlreadyNormalized);
            Assert.NotNull(retry.OfficialIsolationReadiness);
            Assert.True(retry.OfficialIsolationReadiness!.CanExecute);
            Assert.NotNull(retry.After);
            Assert.False(retry.After!.AlreadyNormalized);
            Assert.Equal(2, retry.After.CandidateAttemptCount);
            Assert.Contains(
                "exactly one phase attempt",
                retry.After.BlockingReason,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Db.InterruptedAcquisitionNormalizationBeforeOfficialReadinessTestHook =
                null;
        }
    }

    [Fact]
    public void Execute_rejects_exclusive_publication_fence_race()
    {
        var state = SeedExactState();
        NpgsqlConnection? lockConnection = null;
        Db.InterruptedAcquisitionNormalizationBeforeFenceTestHook =
            () =>
            {
                lockConnection = DataSource.OpenConnection();
                using var acquire =
                    lockConnection.CreateCommand();
                acquire.CommandText =
                    "SELECT pg_advisory_lock_shared(@lockKey)";
                acquire.Parameters.AddWithValue(
                    "lockKey",
                    PublicationGenerationSchema.AdvisoryLockKey);
                acquire.ExecuteNonQuery();
            };
        try
        {
            var result =
                Db.ExecuteInterruptedAcquisitionNormalization(
                    state.Command with
                    {
                        Execute = true,
                        CheckOnly = false,
                    });

            Assert.False(result.Succeeded);
            Assert.Contains(
                "exclusive publication fence",
                result.Error,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                "interrupted",
                ReadMutationIsolationState().AttemptStatus);
        }
        finally
        {
            Db.InterruptedAcquisitionNormalizationBeforeFenceTestHook =
                null;
            if (lockConnection is not null)
            {
                using var release =
                    lockConnection.CreateCommand();
                release.CommandText =
                    "SELECT pg_advisory_unlock_shared(@lockKey)";
                release.Parameters.AddWithValue(
                    "lockKey",
                    PublicationGenerationSchema.AdvisoryLockKey);
                release.ExecuteNonQuery();
                lockConnection.Dispose();
            }
        }
    }

    [Fact]
    public void Check_rejects_missing_attempt()
    {
        var state = SeedExactState();
        ExecuteSql(
            """
            DELETE FROM scrape_phase_attempts
            WHERE scrape_id = @scrapeId
            """,
            ("scrapeId", ExactState.ActiveScrapeId));

        var readiness =
            Db.GetInterruptedAcquisitionNormalizationReadiness(
                state.Command);

        Assert.False(readiness.CanExecute);
        Assert.Equal(0, readiness.CandidateAttemptCount);
        Assert.Contains(
            "exactly one phase attempt",
            readiness.BlockingReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_rejects_malformed_terminal_timestamps()
    {
        var state = SeedExactState();
        ExecuteSql(
            """
            UPDATE scrape_phase_attempts
            SET heartbeat_at = completed_at - interval '1 second'
            WHERE scrape_id = @scrapeId
            """,
            ("scrapeId", ExactState.ActiveScrapeId));

        var readiness =
            Db.GetInterruptedAcquisitionNormalizationReadiness(
                state.Command);

        Assert.False(readiness.CanExecute);
        Assert.Contains(
            "terminal timestamps",
            readiness.BlockingReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_rejects_newer_scrape_state()
    {
        var state = SeedExactState();
        ExecuteSql(
            """
            INSERT INTO scrape_log (
                id,
                started_at,
                status,
                failed_at,
                failure_phase,
                failure_message)
            VALUES (
                1408,
                now(),
                'failed',
                now(),
                'test',
                'newer state')
            """);

        var readiness =
            Db.GetInterruptedAcquisitionNormalizationReadiness(
                state.Command);

        Assert.False(readiness.CanExecute);
        Assert.Equal(1, readiness.NewerScrapeCount);
        Assert.Contains(
            "newer scrape",
            readiness.BlockingReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_rejects_missing_worker_state()
    {
        var state = SeedExactState();
        ExecuteSql(
            """
            DELETE FROM service_worker_status
            WHERE worker_key = 'scraper'
            """);

        var readiness =
            Db.GetInterruptedAcquisitionNormalizationReadiness(
                state.Command);

        Assert.False(readiness.CanExecute);
        Assert.Contains(
            "worker row is missing",
            readiness.BlockingReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_rejects_attempt_two()
    {
        var state = SeedExactState();
        AddInterruptedAttempt(
            state,
            phaseId:
                InterruptedAcquisitionNormalizationCommand
                    .AcquisitionPhaseId);

        var readiness =
            Db.GetInterruptedAcquisitionNormalizationReadiness(
                state.Command);

        Assert.False(readiness.CanExecute);
        Assert.Equal(2, readiness.CandidateAttemptCount);
        Assert.Contains(
            "exactly one phase attempt",
            readiness.BlockingReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_rejects_multiple_phase_attempts()
    {
        var state = SeedExactState();
        AddInterruptedAttempt(
            state,
            phaseId: "post.rank_recompute");

        var readiness =
            Db.GetInterruptedAcquisitionNormalizationReadiness(
                state.Command);

        Assert.False(readiness.CanExecute);
        Assert.Equal(2, readiness.CandidateAttemptCount);
        Assert.Contains(
            "exactly one phase attempt",
            readiness.BlockingReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_rejects_foreign_current_operation()
    {
        var state = SeedExactState();
        ExecuteSql(
            """
            UPDATE service_worker_status
            SET current_operation_json =
                    jsonb_set(
                        current_operation_json,
                        '{PhaseId}',
                        '"post.rank_recompute"'::jsonb)
            WHERE worker_key = 'scraper'
            """);

        var readiness =
            Db.GetInterruptedAcquisitionNormalizationReadiness(
                state.Command);

        Assert.False(readiness.CanExecute);
        Assert.Contains(
            "operation identity",
            readiness.BlockingReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("current_publication_id", 310)]
    [InlineData("previous_publication_id", 312)]
    [InlineData("working_publication_id", 312)]
    public void Check_rejects_pointer_drift(
        string column,
        long value)
    {
        var state = SeedExactState();
        ExecuteSql(
            $"UPDATE scrape_publication_state SET {column} = @value WHERE id = TRUE",
            ("value", value));

        var readiness =
            Db.GetInterruptedAcquisitionNormalizationReadiness(
                state.Command);

        Assert.False(readiness.CanExecute);
        Assert.Contains(
            "publication",
            readiness.BlockingReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_rejects_acquisition_checkpoint()
    {
        var state = SeedExactState();
        ExecuteSql(
            """
            UPDATE scrape_log
            SET acquisition_completed_at = now(),
                songs_scraped = 1,
                total_entries = 1,
                total_requests = 1,
                total_bytes = 1,
                expected_solo_scope_count = 1,
                expected_solo_scope_fingerprint_version = 1,
                expected_solo_scope_fingerprint = repeat('0', 64)
            WHERE id = @scrapeId
            """,
            ("scrapeId", ExactState.ActiveScrapeId));

        var readiness =
            Db.GetInterruptedAcquisitionNormalizationReadiness(
                state.Command);

        Assert.False(readiness.CanExecute);
        Assert.True(readiness.AcquisitionCheckpointPresent);
        Assert.Contains(
            "checkpoint",
            readiness.BlockingReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_rejects_partial_checkpoint_payload()
    {
        var state = SeedExactState();
        ExecuteSql(
            """
            UPDATE scrape_log
            SET songs_scraped = 1
            WHERE id = @scrapeId
            """,
            ("scrapeId", ExactState.ActiveScrapeId));

        var readiness =
            Db.GetInterruptedAcquisitionNormalizationReadiness(
                state.Command);

        Assert.False(readiness.CanExecute);
        Assert.False(readiness.AcquisitionCheckpointPresent);
        Assert.True(
            readiness.AcquisitionCheckpointPayloadPresent);
        Assert.Contains(
            "partial acquisition checkpoint",
            readiness.BlockingReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_rejects_generation_drift()
    {
        var state = SeedExactState();
        ExecuteSql(
            """
            UPDATE publication_generations
            SET status = 'ready'
            WHERE publication_id = @publicationId
            """,
            ("publicationId", ExactState.WorkingPublicationId));

        var readiness =
            Db.GetInterruptedAcquisitionNormalizationReadiness(
                state.Command);

        Assert.False(readiness.CanExecute);
        Assert.Contains(
            "working publication generation",
            readiness.BlockingReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_rejects_freeze_drift()
    {
        var state = SeedExactState();
        ExecuteSql(
            """
            UPDATE scrape_publication_state
            SET public_reads_frozen = TRUE,
                public_reads_frozen_at = now(),
                public_reads_frozen_scrape_id = @publishedScrapeId,
                public_reads_frozen_reason = 'post-process'
            WHERE id = TRUE
            """,
            ("publishedScrapeId", ExactState.PublishedScrapeId));

        var readiness =
            Db.GetInterruptedAcquisitionNormalizationReadiness(
                state.Command);

        Assert.False(readiness.CanExecute);
        Assert.Contains(
            "freeze",
            readiness.BlockingReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_rejects_candidate_source_mapping()
    {
        var state = SeedExactState();
        ExecuteSql(
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
                @scrapeId,
                'song',
                'Solo_Guitar',
                'alltime',
                'empty',
                NULL,
                @scrapeId,
                0,
                'empty',
                'empty',
                0,
                0,
                TRUE,
                now(),
                now())
            """,
            ("scrapeId", ExactState.ActiveScrapeId));

        var readiness =
            Db.GetInterruptedAcquisitionNormalizationReadiness(
                state.Command);

        Assert.False(readiness.CanExecute);
        Assert.Equal(1, readiness.CandidateSourceMappingCount);
    }

    [Fact]
    public async Task Check_rejects_worker_query()
    {
        var state = SeedExactState();
        await using var connection =
            await DataSource.OpenConnectionAsync();
        await using (var application =
                     connection.CreateCommand())
        {
            application.CommandText = """
                SELECT set_config(
                    'application_name',
                    'fstworker-scraper',
                    false)
                """;
            await application.ExecuteNonQueryAsync();
        }

        await using var sleeper = connection.CreateCommand();
        sleeper.CommandText = "SELECT pg_sleep(2)";
        var sleepTask = sleeper.ExecuteNonQueryAsync();
        try
        {
            await WaitForAsync(() =>
                CountSql(
                    """
                    SELECT COUNT(*)
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND application_name = 'fstworker-scraper'
                      AND state <> 'idle'
                    """) > 0);

            var readiness =
                Db.GetInterruptedAcquisitionNormalizationReadiness(
                    state.Command);

            Assert.False(readiness.CanExecute);
            Assert.True(readiness.ActiveWorkerQueryCount > 0);
        }
        finally
        {
            await sleepTask;
        }
    }

    [Fact]
    public void Check_rejects_foreign_advisory_lock()
    {
        var state = SeedExactState();
        using var connection = DataSource.OpenConnection();
        using var acquire = connection.CreateCommand();
        acquire.CommandText =
            "SELECT pg_advisory_lock(710920260918)";
        acquire.ExecuteNonQuery();
        try
        {
            var readiness =
                Db.GetInterruptedAcquisitionNormalizationReadiness(
                    state.Command);

            Assert.False(readiness.CanExecute);
            Assert.True(readiness.AdvisoryLockCount > 0);
        }
        finally
        {
            using var release = connection.CreateCommand();
            release.CommandText =
                "SELECT pg_advisory_unlock(710920260918)";
            release.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task Check_rejects_waiting_lock()
    {
        var state = SeedExactState();
        await using var blocker =
            await DataSource.OpenConnectionAsync();
        await using var blockerTransaction =
            await blocker.BeginTransactionAsync();
        await using (var lockRow = blocker.CreateCommand())
        {
            lockRow.Transaction = blockerTransaction;
            lockRow.CommandText = """
                SELECT id
                FROM scrape_log
                WHERE id = @scrapeId
                FOR UPDATE
                """;
            lockRow.Parameters.AddWithValue(
                "scrapeId",
                ExactState.ActiveScrapeId);
            await lockRow.ExecuteScalarAsync();
        }

        await using var waiter =
            await DataSource.OpenConnectionAsync();
        await using var waiterTransaction =
            await waiter.BeginTransactionAsync();
        await using var waitCommand = waiter.CreateCommand();
        waitCommand.Transaction = waiterTransaction;
        waitCommand.CommandText = """
            SELECT id
            FROM scrape_log
            WHERE id = @scrapeId
            FOR UPDATE
            """;
        waitCommand.Parameters.AddWithValue(
            "scrapeId",
            ExactState.ActiveScrapeId);
        var waitTask = waitCommand.ExecuteScalarAsync();
        try
        {
            await WaitForAsync(() =>
                CountSql(
                    """
                    SELECT COUNT(*)
                    FROM pg_locks lock
                    JOIN pg_stat_activity activity
                      ON activity.pid = lock.pid
                    WHERE activity.datname = current_database()
                      AND NOT lock.granted
                    """) > 0);

            var readiness =
                Db.GetInterruptedAcquisitionNormalizationReadiness(
                    state.Command);

            Assert.False(readiness.CanExecute);
            Assert.True(readiness.WaitingLockCount > 0);
        }
        finally
        {
            await blockerTransaction.RollbackAsync();
            await waitTask;
            await waiterTransaction.RollbackAsync();
        }
    }

    [Fact]
    public void Idempotent_retry_rejects_replacement_worker()
    {
        var state = SeedExactState();
        var command = state.Command with
        {
            Execute = true,
            CheckOnly = false,
        };
        Assert.True(
            Db.ExecuteInterruptedAcquisitionNormalization(command)
                .Succeeded);
        ReplaceWorker();

        var retry =
            Db.ExecuteInterruptedAcquisitionNormalization(command);

        Assert.False(retry.Succeeded);
        Assert.False(retry.Before.AlreadyNormalized);
        Assert.Contains(
            "worker instance identity",
            retry.Error ?? retry.Before.BlockingReason,
            StringComparison.OrdinalIgnoreCase);
    }

    private ExactState SeedExactState()
    {
        SetSequences(
            scrapeSequenceValue: 1404,
            publicationSequenceValue: 309);
        var previousScrapeId = Db.StartScrapeRun();
        Assert.Equal(1405, previousScrapeId);
        Db.CompleteScrapeRun(
            previousScrapeId,
            1,
            10,
            1,
            100);
        Db.PublishScrapeRun(
            previousScrapeId,
            promoteCachedResponses: false);

        SetSequences(
            scrapeSequenceValue: 1405,
            publicationSequenceValue: 311);
        var publishedScrapeId = Db.StartScrapeRun();
        Assert.Equal(
            ExactState.PublishedScrapeId,
            publishedScrapeId);
        Db.CompleteScrapeRun(
            publishedScrapeId,
            1,
            10,
            1,
            100);
        Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);

        SetSequences(
            scrapeSequenceValue: 1406,
            publicationSequenceValue: 313);
        var activeScrapeId = Db.StartScrapeRun();
        Assert.Equal(ExactState.ActiveScrapeId, activeScrapeId);
        Assert.Equal(
            ExactState.WorkingPublicationId,
            Db.GetPublicationGenerationForScrape(
                activeScrapeId)!.PublicationId);

        var startedAt =
            new DateTime(
                2026,
                9,
                18,
                20,
                7,
                39,
                376,
                DateTimeKind.Utc)
            .AddTicks(7510);
        var completedAt =
            new DateTime(
                2026,
                9,
                18,
                20,
                34,
                43,
                424,
                DateTimeKind.Utc)
            .AddTicks(160);
        var operationStartedAt =
            new DateTime(
                2026,
                9,
                18,
                20,
                7,
                39,
                222,
                DateTimeKind.Utc)
            .AddTicks(8517);
        var operationUpdatedAt =
            new DateTime(
                2026,
                9,
                18,
                20,
                34,
                39,
                989,
                DateTimeKind.Utc)
            .AddTicks(8317);
        var attempt = Db.StartScrapePhaseAttempt(
            new ScrapePhaseAttemptStart(
                activeScrapeId,
                InterruptedAcquisitionNormalizationCommand
                    .AcquisitionPhaseId,
                "scrape.update",
                100,
                "fst.scrape-plan.v2",
                ExactState.WorkerInstanceId,
                "fetching_leaderboards",
                "running",
                "leaderboards",
                4443,
                6516,
                true,
                68.2,
                "indeterminate",
                null,
                null,
                null,
                null,
                null,
                null,
                startedAt,
                operationUpdatedAt,
                operationUpdatedAt,
                "build-test",
                "config-test"));
        Assert.Equal(1, attempt);
        ExecuteSql(
            """
            UPDATE scrape_phase_attempts
            SET status = 'interrupted',
                last_progress_at = @completedAt,
                heartbeat_at = @completedAt,
                completed_at = @completedAt,
                warning_message =
                    'Scrape pass exited before a publication decision.',
                error_message = NULL
            WHERE scrape_id = @scrapeId
              AND phase_id = @phaseId
              AND attempt = 1
            """,
            ("completedAt", completedAt),
            ("scrapeId", activeScrapeId),
            (
                "phaseId",
                InterruptedAcquisitionNormalizationCommand
                    .AcquisitionPhaseId));

        var currentOperation = new WorkerOperationInfo
        {
            ContractVersion = 2,
            OperationKey =
                InterruptedAcquisitionNormalizationCommand
                    .AcquisitionPhaseId,
            OperationLabel = "Scraping leaderboard scores",
            Status = "running",
            ScrapeId = activeScrapeId,
            Phase = "Scraping",
            SubOperation = "fetching_leaderboards",
            StartedAtUtc = operationStartedAt,
            UpdatedAtUtc = operationUpdatedAt,
            ElapsedSeconds =
                (operationUpdatedAt - operationStartedAt)
                .TotalSeconds,
            OperationId = "scrape.update",
            PhaseId =
                InterruptedAcquisitionNormalizationCommand
                    .AcquisitionPhaseId,
            PhaseStatus = "running",
            SubphaseId = "fetching_leaderboards",
            PhasePlanVersion = "fst.scrape-plan.v2",
            PhaseOrdinal = 100,
            PhaseAttempt = 1,
            UnitsKind = "leaderboards",
            UnitsCompleted = 4443,
            UnitsTotal = 6516,
            UnitsTotalFinal = true,
            PhasePercent = 68.2,
            OverallPercentKind = "indeterminate",
            LastProgressAtUtc = operationUpdatedAt,
            HeartbeatAtUtc = ExactState.WorkerFreshnessUtc,
        };
        var previousOperation = new WorkerOperationInfo
        {
            ContractVersion = 2,
            OperationKey = "scrape.pass",
            OperationLabel = "Running leaderboard update",
            Status = "failed",
            ScrapeId = activeScrapeId,
            Phase = "Scraping",
            SubOperation = "scrape_pass",
            Detail =
                "Scrape pass exited before a publication decision.",
            StartedAtUtc = operationStartedAt,
            UpdatedAtUtc = completedAt,
            EndedAtUtc = completedAt,
            ElapsedSeconds =
                (completedAt - operationStartedAt).TotalSeconds,
            OverallPercentKind = "indeterminate",
        };
        Db.UpsertWorkerHeartbeat(
            WorkerStatusPublisher.ScraperWorkerKey,
            "offline",
            "scraper",
            ExactState.WorkerInstanceId,
            operationStartedAt.AddSeconds(-5),
            ExactState.WorkerFreshnessUtc,
            "Worker service stopped",
            currentOperation);
        Db.UpdateWorkerActivity(
            WorkerStatusPublisher.ScraperWorkerKey,
            currentOperation,
            previousOperation,
            status: "offline",
            message: "Worker service stopped",
            updatedAtUtc: ExactState.WorkerFreshnessUtc,
            instanceId: ExactState.WorkerInstanceId);

        var pointer = Db.GetPublicationPointerState();
        Assert.Equal(
            ExactState.PublishedScrapeId,
            pointer.PublishedScrapeId);
        Assert.Equal(
            ExactState.CurrentPublicationId,
            pointer.CurrentPublicationId);
        Assert.Equal(
            ExactState.PreviousPublicationId,
            pointer.PreviousPublicationId);
        Assert.Equal(
            ExactState.WorkingPublicationId,
            pointer.WorkingPublicationId);
        Assert.False(Db.GetPublicReadFreezeState().IsFrozen);

        return new ExactState(
            new InterruptedAcquisitionNormalizationCommand(
                Execute: false,
                CheckOnly: true,
                ScrapeId: ExactState.ActiveScrapeId,
                ExpectedPublishedScrapeId:
                    ExactState.PublishedScrapeId,
                ExpectedCurrentPublicationId:
                    ExactState.CurrentPublicationId,
                ExpectedPreviousPublicationId:
                    ExactState.PreviousPublicationId,
                ExpectedWorkingPublicationId:
                    ExactState.WorkingPublicationId,
                ExpectedWorkerInstanceId:
                    ExactState.WorkerInstanceId,
                ExpectedWorkerFreshnessUtc:
                    ExactState.WorkerFreshnessUtc,
                ExpectedPhaseId:
                    InterruptedAcquisitionNormalizationCommand
                        .AcquisitionPhaseId,
                ExpectedAttempt: 1),
            completedAt);
    }

    private void AddInterruptedAttempt(
        ExactState state,
        string phaseId)
    {
        var attempt = Db.StartScrapePhaseAttempt(
            new ScrapePhaseAttemptStart(
                ExactState.ActiveScrapeId,
                phaseId,
                "scrape.update",
                phaseId == state.Command.ExpectedPhaseId
                    ? 100
                    : 200,
                "fst.scrape-plan.v2",
                ExactState.WorkerInstanceId,
                null,
                "running",
                "items",
                0,
                1,
                true,
                0,
                "indeterminate",
                null,
                null,
                null,
                null,
                null,
                null,
                state.AttemptCompletedAtUtc,
                state.AttemptCompletedAtUtc,
                state.AttemptCompletedAtUtc,
                "build-test",
                "config-test"));
        ExecuteSql(
            """
            UPDATE scrape_phase_attempts
            SET status = 'interrupted',
                completed_at = @completedAt,
                warning_message = 'extra attempt'
            WHERE scrape_id = @scrapeId
              AND phase_id = @phaseId
              AND attempt = @attempt
            """,
            ("completedAt", state.AttemptCompletedAtUtc),
            ("scrapeId", ExactState.ActiveScrapeId),
            ("phaseId", phaseId),
            ("attempt", attempt));
    }

    private void ReplaceWorker()
    {
        var newer =
            ExactState.WorkerFreshnessUtc.AddMinutes(1);
        Db.UpsertWorkerHeartbeat(
            WorkerStatusPublisher.ScraperWorkerKey,
            "offline",
            "scraper",
            "replacement-worker",
            newer,
            newer,
            "Replacement worker",
            currentOperation: null);
    }

    private void SetSequences(
        long scrapeSequenceValue,
        long publicationSequenceValue)
        => ExecuteSql(
            """
            SELECT setval(
                pg_get_serial_sequence(
                    'public.scrape_log',
                    'id'),
                @scrapeSequenceValue,
                true);
            SELECT setval(
                pg_get_serial_sequence(
                    'public.publication_generations',
                    'publication_id'),
                @publicationSequenceValue,
                true);
            """,
            ("scrapeSequenceValue", scrapeSequenceValue),
            (
                "publicationSequenceValue",
                publicationSequenceValue));

    private string ReadProtectedState()
        => ReadScalarText(
            """
            SELECT jsonb_build_object(
                'scrape',
                    (
                        SELECT to_jsonb(scrape)
                        FROM scrape_log scrape
                        WHERE id = 1407),
                'publication',
                    (
                        SELECT to_jsonb(publication)
                        FROM scrape_publication_state publication
                        WHERE id = TRUE),
                'generations',
                    (
                        SELECT jsonb_agg(
                            to_jsonb(generation)
                            ORDER BY publication_id)
                        FROM publication_generations generation),
                'attempts',
                    (
                        SELECT jsonb_agg(
                            to_jsonb(attempt_row)
                            ORDER BY phase_id, attempt)
                        FROM scrape_phase_attempts attempt_row
                        WHERE scrape_id = 1407),
                'worker',
                    (
                        SELECT to_jsonb(worker)
                        FROM service_worker_status worker
                        WHERE worker_key = 'scraper'),
                'mappings',
                    (
                        SELECT jsonb_agg(
                            to_jsonb(mapping)
                            ORDER BY instrument, song_id)
                        FROM leaderboard_published_scope_source mapping
                        WHERE published_scrape_id = 1407)
            )::TEXT
            """);

    private MutationIsolationState ReadMutationIsolationState()
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (
                    SELECT to_jsonb(scrape)::TEXT
                    FROM scrape_log scrape
                    WHERE id = 1407),
                (
                    SELECT to_jsonb(publication)::TEXT
                    FROM scrape_publication_state publication
                    WHERE id = TRUE),
                (
                    SELECT jsonb_agg(
                        to_jsonb(generation)
                        ORDER BY publication_id)::TEXT
                    FROM publication_generations generation),
                (
                    SELECT jsonb_agg(
                        to_jsonb(mapping)
                        ORDER BY instrument, song_id)::TEXT
                    FROM leaderboard_published_scope_source mapping
                    WHERE published_scrape_id = 1407),
                (
                    SELECT (
                        to_jsonb(attempt_row)
                        - 'status'
                        - 'error_message')::TEXT
                    FROM scrape_phase_attempts attempt_row
                    WHERE scrape_id = 1407
                      AND phase_id = 'scrape.leaderboards'
                      AND attempt = 1),
                (
                    SELECT status
                    FROM scrape_phase_attempts
                    WHERE scrape_id = 1407
                      AND phase_id = 'scrape.leaderboards'
                      AND attempt = 1),
                (
                    SELECT error_message
                    FROM scrape_phase_attempts
                    WHERE scrape_id = 1407
                      AND phase_id = 'scrape.leaderboards'
                      AND attempt = 1),
                (
                    SELECT (
                        to_jsonb(worker)
                        - 'current_operation_json'
                        - 'last_operation_json')::TEXT
                    FROM service_worker_status worker
                    WHERE worker_key = 'scraper'),
                (
                    SELECT current_operation_json IS NOT NULL
                    FROM service_worker_status
                    WHERE worker_key = 'scraper'),
                (
                    SELECT last_operation_json ->> 'Status'
                    FROM service_worker_status
                    WHERE worker_key = 'scraper'),
                (
                    SELECT last_operation_json ->> 'PhaseId'
                    FROM service_worker_status
                    WHERE worker_key = 'scraper'),
                (
                    SELECT (
                        last_operation_json ->> 'PhaseAttempt'
                    )::INTEGER
                    FROM service_worker_status
                    WHERE worker_key = 'scraper'),
                (
                    SELECT (
                        last_operation_json ->> 'ScrapeId'
                    )::BIGINT
                    FROM service_worker_status
                    WHERE worker_key = 'scraper'),
                (
                    SELECT last_operation_json ->> 'Detail'
                    FROM service_worker_status
                    WHERE worker_key = 'scraper')
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new MutationIsolationState(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetBoolean(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetInt32(11),
            reader.IsDBNull(12) ? null : reader.GetInt64(12),
            reader.IsDBNull(13) ? null : reader.GetString(13));
    }

    private void ExecuteSql(
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(
                parameter.Name,
                parameter.Value);
        }
        command.ExecuteNonQuery();
    }

    private long CountSql(string sql)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private string ReadScalarText(string sql)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }
        throw new TimeoutException(
            "The expected PostgreSQL state did not become visible.");
    }

    private sealed record MutationIsolationState(
        string Scrape,
        string Publication,
        string Generations,
        string? SourceMappings,
        string AttemptWithoutNormalizedFields,
        string AttemptStatus,
        string? AttemptError,
        string WorkerWithoutOperationFields,
        bool WorkerCurrentOperationPresent,
        string? WorkerLastOperationStatus,
        string? WorkerLastOperationPhaseId,
        int? WorkerLastOperationAttempt,
        long? WorkerLastOperationScrapeId,
        string? WorkerLastOperationDetail);
}

public sealed class InterruptedAcquisitionNormalizationSchemaTests
{
    [Fact]
    public void Missing_schema_fails_without_initialization_or_mutation()
    {
        var connectionString =
            SharedPostgresContainer
                .CreateEmptyDatabaseConnectionString(
                    "fst_interrupted_normalization_");
        using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        using var database = new MetaDatabase(
            dataSource,
            Substitute.For<ILogger<MetaDatabase>>());
        var command =
            InterruptedAcquisitionNormalizationCommand.Parse(
                InterruptedAcquisitionNormalizationCommandTests
                    .CommandArguments(execute: false))!;

        var check =
            database.GetInterruptedAcquisitionNormalizationReadiness(
                command);
        var execute =
            database.ExecuteInterruptedAcquisitionNormalization(
                command with
                {
                    Execute = true,
                    CheckOnly = false,
                });

        Assert.False(check.SchemaReady);
        Assert.False(check.CanExecute);
        Assert.False(execute.Succeeded);
        using var connection = dataSource.OpenConnection();
        using var probe = connection.CreateCommand();
        probe.CommandText = """
            SELECT
                to_regclass('public.scrape_log') IS NULL
                AND to_regclass(
                    'public.scrape_phase_attempts') IS NULL
                AND to_regclass(
                    'public.service_worker_status') IS NULL
            """;
        Assert.True((bool)probe.ExecuteScalar()!);
    }
}

[CollectionDefinition(
    InterruptedAcquisitionNormalizationEntryPointCollection.Name,
    DisableParallelization = true)]
public sealed class
    InterruptedAcquisitionNormalizationEntryPointCollection
{
    public const string Name =
        "Interrupted acquisition normalization entrypoint";
}

[Collection(
    InterruptedAcquisitionNormalizationEntryPointCollection.Name)]
public sealed class
    InterruptedAcquisitionNormalizationEntryPointTests
{
    [Fact]
    public async Task Check_entrypoint_writes_one_json_and_does_not_initialize_schema()
    {
        var connectionString =
            SharedPostgresContainer
                .CreateEmptyDatabaseConnectionString(
                    "fst_interrupted_entrypoint_");
        var workingDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".test-temp",
            $"interrupted-acquisition-normalization-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.Environment[
                "ConnectionStrings__PostgreSQL"] =
                connectionString;
            startInfo.Environment[
                "DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE"] =
                "false";
            startInfo.Environment["TMPDIR"] =
                workingDirectory;
            startInfo.ArgumentList.Add(
                typeof(Program).Assembly.Location);
            foreach (var argument in
                     InterruptedAcquisitionNormalizationCommandTests
                         .CommandArguments(execute: false))
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Could not start normalization entrypoint.");
            var stdoutTask =
                process.StandardOutput.ReadToEndAsync();
            var stderrTask =
                process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            _ = await stderrTask;

            Assert.Equal(2, process.ExitCode);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal(
                JsonValueKind.Object,
                document.RootElement.ValueKind);
            Assert.False(
                document.RootElement
                    .GetProperty("SchemaReady")
                    .GetBoolean());
            Assert.Equal(
                stdout.Trim(),
                document.RootElement.GetRawText());

            using var dataSource =
                NpgsqlDataSource.Create(connectionString);
            using var connection =
                dataSource.OpenConnection();
            using var probe = connection.CreateCommand();
            probe.CommandText =
                "SELECT to_regclass('public.scrape_log') IS NULL";
            Assert.True((bool)probe.ExecuteScalar()!);
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(
                    workingDirectory,
                    recursive: true);
            }
        }
    }
}

internal sealed record ExactState(
    InterruptedAcquisitionNormalizationCommand Command,
    DateTime AttemptCompletedAtUtc)
{
    public const long ActiveScrapeId = 1407;
    public const long PublishedScrapeId = 1406;
    public const long CurrentPublicationId = 312;
    public const long PreviousPublicationId = 310;
    public const long WorkingPublicationId = 314;
    public const string WorkerInstanceId =
        "1bd93ecb9a86:1:70a2a8ef1826456d9792685ef8de5056";
    public static readonly DateTime WorkerFreshnessUtc =
        new(
            2026,
            9,
            18,
            20,
            34,
            43,
            441,
            DateTimeKind.Utc);

    static ExactState()
    {
        WorkerFreshnessUtc =
            WorkerFreshnessUtc.AddTicks(9260);
    }
}
