using System.Data;
using System.Globalization;
using System.Text.Json;
using FSTService.Scraping;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

public sealed partial class MetaDatabase
{
    internal const string InterruptedAcquisitionNormalizationMessage =
        "Interrupted acquisition attempt normalized for official active-scrape failure isolation.";

    internal Action?
        InterruptedAcquisitionNormalizationBeforeFenceTestHook
    { get; set; }
    internal Action?
        InterruptedAcquisitionNormalizationBeforeOfficialReadinessTestHook
    { get; set; }

    public InterruptedAcquisitionNormalizationReadiness
        GetInterruptedAcquisitionNormalizationReadiness(
            InterruptedAcquisitionNormalizationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            using var connection = _ds.OpenConnection();
            using var transaction = connection.BeginTransaction(
                IsolationLevel.RepeatableRead);
            ConfigureNormalizationTransaction(
                connection,
                transaction,
                readOnly: true);

            if (!HasInterruptedAcquisitionNormalizationSchema(
                    connection,
                    transaction))
            {
                transaction.Commit();
                return CreateUnavailableNormalizationReadiness(
                    command,
                    schemaReady: false,
                    fenceAcquired: false,
                    "Required interrupted-acquisition normalization schema is missing.");
            }

            if (!TryAcquirePublicationAdvisoryLock(
                    connection,
                    transaction,
                    shared: true))
            {
                transaction.Commit();
                return CreateUnavailableNormalizationReadiness(
                    command,
                    schemaReady: true,
                    fenceAcquired: false,
                    "The shared publication fence is busy.");
            }

            var readiness = ReadInterruptedAcquisitionNormalizationReadiness(
                connection,
                transaction,
                command,
                lockRows: false);
            transaction.Commit();
            return readiness;
        }
        catch (Exception ex) when (
            ex is NpgsqlException
                or TimeoutException)
        {
            _log.LogWarning(
                ex,
                "Interrupted-acquisition normalization readiness could not be proven.");
            return CreateUnavailableNormalizationReadiness(
                command,
                schemaReady: false,
                fenceAcquired: false,
                "Interrupted-acquisition normalization readiness could not be proven.");
        }
    }

    public InterruptedAcquisitionNormalizationExecutionResult
        ExecuteInterruptedAcquisitionNormalization(
            InterruptedAcquisitionNormalizationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var before =
            GetInterruptedAcquisitionNormalizationReadiness(command);
        if (before.AlreadyNormalized)
        {
            return FinishInterruptedAcquisitionNormalization(
                command,
                before,
                mutationReadiness: before,
                alreadyNormalized: true);
        }
        if (!before.ReadyToNormalize)
        {
            return new InterruptedAcquisitionNormalizationExecutionResult(
                Succeeded: false,
                AlreadyNormalized: false,
                Before: before,
                MutationReadiness: null,
                After: null,
                OfficialIsolationReadiness: null,
                Error: before.BlockingReason);
        }

        InterruptedAcquisitionNormalizationBeforeFenceTestHook?.Invoke();

        InterruptedAcquisitionNormalizationReadiness? mutationReadiness =
            null;
        try
        {
            using var connection = _ds.OpenConnection();
            using var transaction = connection.BeginTransaction(
                IsolationLevel.RepeatableRead);
            ConfigureNormalizationTransaction(
                connection,
                transaction,
                readOnly: false);

            if (!HasInterruptedAcquisitionNormalizationSchema(
                    connection,
                    transaction))
            {
                transaction.Rollback();
                return new InterruptedAcquisitionNormalizationExecutionResult(
                    Succeeded: false,
                    AlreadyNormalized: false,
                    Before: before,
                    MutationReadiness:
                        CreateUnavailableNormalizationReadiness(
                            command,
                            schemaReady: false,
                            fenceAcquired: false,
                            "Required interrupted-acquisition normalization schema is missing."),
                    After: null,
                    OfficialIsolationReadiness: null,
                    Error:
                        "Required interrupted-acquisition normalization schema is missing.");
            }

            if (!TryAcquirePublicationAdvisoryLock(
                    connection,
                    transaction,
                    shared: false))
            {
                transaction.Rollback();
                return new InterruptedAcquisitionNormalizationExecutionResult(
                    Succeeded: false,
                    AlreadyNormalized: false,
                    Before: before,
                    MutationReadiness:
                        CreateUnavailableNormalizationReadiness(
                            command,
                            schemaReady: true,
                            fenceAcquired: false,
                            "The exclusive publication fence is busy."),
                    After: null,
                    OfficialIsolationReadiness: null,
                    Error:
                        "The exclusive publication fence is busy.");
            }

            mutationReadiness =
                ReadInterruptedAcquisitionNormalizationReadiness(
                    connection,
                    transaction,
                    command,
                    lockRows: true);
            if (mutationReadiness.AlreadyNormalized)
            {
                transaction.Rollback();
                return FinishInterruptedAcquisitionNormalization(
                    command,
                    before,
                    mutationReadiness,
                    alreadyNormalized: true);
            }
            if (!mutationReadiness.ReadyToNormalize)
            {
                transaction.Rollback();
                return new InterruptedAcquisitionNormalizationExecutionResult(
                    Succeeded: false,
                    AlreadyNormalized: false,
                    Before: before,
                    MutationReadiness: mutationReadiness,
                    After: null,
                    OfficialIsolationReadiness: null,
                    Error: mutationReadiness.BlockingReason);
            }

            var raw = ReadInterruptedAcquisitionNormalizationState(
                connection,
                transaction,
                command,
                lockRows: true);
            if (!NormalizeInterruptedAcquisitionAttempt(
                    connection,
                    transaction,
                    command,
                    raw)
                || !NormalizeInterruptedAcquisitionWorkerOperation(
                    connection,
                    transaction,
                    command,
                    raw))
            {
                transaction.Rollback();
                return new InterruptedAcquisitionNormalizationExecutionResult(
                    Succeeded: false,
                    AlreadyNormalized: false,
                    Before: before,
                    MutationReadiness: mutationReadiness,
                    After: null,
                    OfficialIsolationReadiness: null,
                    Error:
                        "The exact interrupted attempt or worker operation changed before normalization.");
            }

            var normalizedUnderFence =
                ReadInterruptedAcquisitionNormalizationReadiness(
                    connection,
                    transaction,
                    command,
                    lockRows: true);
            if (!normalizedUnderFence.AlreadyNormalized)
            {
                transaction.Rollback();
                return new InterruptedAcquisitionNormalizationExecutionResult(
                    Succeeded: false,
                    AlreadyNormalized: false,
                    Before: before,
                    MutationReadiness: mutationReadiness,
                    After: normalizedUnderFence,
                    OfficialIsolationReadiness: null,
                    Error:
                        "The fenced transaction did not produce the exact normalized state.");
            }

            transaction.Commit();
        }
        catch (Exception ex) when (
            ex is NpgsqlException
                or TimeoutException)
        {
            _log.LogWarning(
                ex,
                "Interrupted-acquisition normalization execution could not be proven.");
            return new InterruptedAcquisitionNormalizationExecutionResult(
                Succeeded: false,
                AlreadyNormalized: false,
                Before: before,
                MutationReadiness: mutationReadiness,
                After: null,
                OfficialIsolationReadiness: null,
                Error:
                    "Interrupted-acquisition normalization execution could not be proven.");
        }

        return FinishInterruptedAcquisitionNormalization(
            command,
            before,
            mutationReadiness,
            alreadyNormalized: false);
    }

    private InterruptedAcquisitionNormalizationExecutionResult
        FinishInterruptedAcquisitionNormalization(
            InterruptedAcquisitionNormalizationCommand command,
            InterruptedAcquisitionNormalizationReadiness before,
            InterruptedAcquisitionNormalizationReadiness? mutationReadiness,
            bool alreadyNormalized)
    {
        var after =
            GetInterruptedAcquisitionNormalizationReadiness(command);
        ActiveScrapeFailureIsolationReadiness? officialReadiness = null;
        InterruptedAcquisitionNormalizationReadiness? finalState = null;
        try
        {
            if (after.AlreadyNormalized)
            {
                InterruptedAcquisitionNormalizationBeforeOfficialReadinessTestHook
                    ?.Invoke();
                officialReadiness =
                    GetActiveScrapeFailureIsolationReadiness(
                        command.ScrapeId,
                        command.ExpectedPublishedScrapeId);
                finalState =
                    GetInterruptedAcquisitionNormalizationReadiness(
                        command);
            }
        }
        catch (Exception ex) when (
            ex is NpgsqlException
                or TimeoutException)
        {
            _log.LogWarning(
                ex,
                "Official active-scrape failure-isolation readiness could not be proven after interrupted-acquisition normalization.");
        }

        var succeeded =
            after.AlreadyNormalized
            && officialReadiness is
            {
                CanExecute: true,
                AcquisitionFailureMutationRequired: true,
                PublicationMutationRequired: false,
                RunningPhaseAttemptCount: 0,
                WorkerCurrentOperationPresent: false,
                AcquisitionCheckpointPresent: false,
            }
            && string.Equals(
                officialReadiness.WorkerStatus,
                "offline",
                StringComparison.Ordinal)
            && officialReadiness.PublishedScrapeId
                == command.ExpectedPublishedScrapeId
            && officialReadiness.WorkingPublicationId
                == command.ExpectedWorkingPublicationId;
        succeeded =
            succeeded
            && IsExactNormalizedFinalState(
                finalState,
                command);
        return new InterruptedAcquisitionNormalizationExecutionResult(
            Succeeded: succeeded,
            AlreadyNormalized: alreadyNormalized,
            Before: before,
            MutationReadiness: mutationReadiness,
            After: finalState ?? after,
            OfficialIsolationReadiness: officialReadiness,
            Error: succeeded
                ? null
                : "The final state was not accepted by official acquisition-failure isolation readiness.");
    }

    private static bool IsExactNormalizedFinalState(
        InterruptedAcquisitionNormalizationReadiness? state,
        InterruptedAcquisitionNormalizationCommand command)
        => state is
            {
                SchemaReady: true,
                FenceAcquired: true,
                AlreadyNormalized: true,
                ScrapeStatus: "running",
                PublicReadsFrozen: false,
                PublicReadsFrozenAtUtc: null,
                FrozenScrapeId: null,
                FreezeReason: null,
                CommitIntentStartedAtUtc: null,
                CommitIntentHeartbeatAtUtc: null,
                CommitIntentOwner: null,
                AcquisitionCheckpointPresent: false,
                AcquisitionCheckpointPayloadPresent: false,
                OtherRunningScrapeCount: 0,
                NewerScrapeCount: 0,
                CurrentGenerationScrapeId:
                    var currentGenerationScrapeId,
                CurrentGenerationStatus: "current",
                CurrentGenerationPreviousPublicationId:
                    var currentGenerationPreviousPublicationId,
                PreviousGenerationScrapeId: not null,
                PreviousGenerationStatus: "retained",
                CandidateGenerationScrapeId:
                    var candidateGenerationScrapeId,
                CandidateGenerationStatus: "building",
                CandidateGenerationCount: 1,
                CandidateSourceMappingCount: 0,
                ActiveWorkerQueryCount: 0,
                WaitingLockCount: 0,
                AdvisoryLockCount: 0,
                MaintenanceActivityPresent: false,
                CandidateAttemptCount: 1,
                RunningPhaseAttemptCount: 0,
                GlobalRunningPhaseAttemptCount: 0,
                AttemptPhaseId: var attemptPhaseId,
                Attempt: var attempt,
                AttemptStatus: "failed",
                AttemptWorkerInstanceId: var attemptWorkerInstanceId,
                AttemptCompletedAtUtc: not null,
                AttemptErrorMessage:
                    InterruptedAcquisitionNormalizationMessage,
                WorkerStatus: "offline",
                WorkerMode: "scraper",
                WorkerInstanceId: var workerInstanceId,
                WorkerFreshnessUtc: var workerFreshnessUtc,
                WorkerCurrentOperationPresent: false,
                WorkerLastOperationPresent: true,
                WorkerOperationKey: var workerOperationKey,
                WorkerOperationPhaseId: var workerOperationPhaseId,
                WorkerOperationAttempt: var workerOperationAttempt,
                WorkerOperationScrapeId: var workerOperationScrapeId,
            }
            && state.ScrapeId == command.ScrapeId
            && state.PublishedScrapeId
                == command.ExpectedPublishedScrapeId
            && state.CurrentPublicationId
                == command.ExpectedCurrentPublicationId
            && state.PreviousPublicationId
                == command.ExpectedPreviousPublicationId
            && state.WorkingPublicationId
                == command.ExpectedWorkingPublicationId
            && currentGenerationScrapeId
                == command.ExpectedPublishedScrapeId
            && currentGenerationPreviousPublicationId
                == command.ExpectedPreviousPublicationId
            && candidateGenerationScrapeId == command.ScrapeId
            && string.Equals(
                attemptPhaseId,
                command.ExpectedPhaseId,
                StringComparison.Ordinal)
            && attempt == command.ExpectedAttempt
            && string.Equals(
                attemptWorkerInstanceId,
                command.ExpectedWorkerInstanceId,
                StringComparison.Ordinal)
            && string.Equals(
                workerInstanceId,
                command.ExpectedWorkerInstanceId,
                StringComparison.Ordinal)
            && SamePostgresMicrosecond(
                workerFreshnessUtc,
                command.ExpectedWorkerFreshnessUtc)
            && string.Equals(
                workerOperationKey,
                command.ExpectedPhaseId,
                StringComparison.Ordinal)
            && string.Equals(
                workerOperationPhaseId,
                command.ExpectedPhaseId,
                StringComparison.Ordinal)
            && workerOperationAttempt == command.ExpectedAttempt
            && workerOperationScrapeId == command.ScrapeId;

    private void ConfigureNormalizationTransaction(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        bool readOnly)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = readOnly
            ? """
                SET TRANSACTION READ ONLY;
                SELECT set_config('lock_timeout', '250ms', true);
                SELECT set_config('statement_timeout', '5s', true);
                """
            : """
                SELECT set_config('lock_timeout', @lockTimeout, true);
                SELECT set_config('statement_timeout', @statementTimeout, true);
                """;
        if (!readOnly)
        {
            command.Parameters.AddWithValue(
                "lockTimeout",
                $"{_publicationCommitOptions.RelationLockTimeoutMilliseconds}ms");
            command.Parameters.AddWithValue(
                "statementTimeout",
                $"{_publicationCommitOptions.StatementTimeoutMilliseconds}ms");
        }

        command.ExecuteNonQuery();
    }

    private static bool HasInterruptedAcquisitionNormalizationSchema(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH required_columns(table_name, column_name) AS (
                VALUES
                    ('scrape_log', 'id'),
                    ('scrape_log', 'status'),
                    ('scrape_log', 'completed_at'),
                    ('scrape_log', 'failed_at'),
                    ('scrape_log', 'failure_phase'),
                    ('scrape_log', 'failure_message'),
                    ('scrape_log', 'acquisition_completed_at'),
                    ('scrape_log', 'songs_scraped'),
                    ('scrape_log', 'total_entries'),
                    ('scrape_log', 'total_requests'),
                    ('scrape_log', 'total_bytes'),
                    ('scrape_log', 'expected_solo_scope_count'),
                    ('scrape_log', 'expected_solo_scope_fingerprint_version'),
                    ('scrape_log', 'expected_solo_scope_fingerprint'),
                    ('scrape_publication_state', 'id'),
                    ('scrape_publication_state', 'published_scrape_id'),
                    ('scrape_publication_state', 'current_publication_id'),
                    ('scrape_publication_state', 'previous_publication_id'),
                    ('scrape_publication_state', 'working_publication_id'),
                    ('scrape_publication_state', 'public_reads_frozen'),
                    ('scrape_publication_state', 'public_reads_frozen_at'),
                    ('scrape_publication_state', 'public_reads_frozen_scrape_id'),
                    ('scrape_publication_state', 'public_reads_frozen_reason'),
                    ('scrape_publication_state', 'publication_commit_intent_started_at'),
                    ('scrape_publication_state', 'publication_commit_intent_heartbeat_at'),
                    ('scrape_publication_state', 'publication_commit_intent_owner'),
                    ('scrape_publication_state', 'band_projection_generation'),
                    ('scrape_publication_state', 'improvement_notifications_scrape_id'),
                    ('scrape_publication_state', 'improvement_notifications_status'),
                    ('scrape_publication_state', 'improvement_notifications_attempt_count'),
                    ('scrape_publication_state', 'improvement_notifications_started_at'),
                    ('scrape_publication_state', 'improvement_notifications_completed_at'),
                    ('scrape_publication_state', 'improvement_notifications_error'),
                    ('scrape_publication_state', 'improvement_notifications_projection_scopes'),
                    ('scrape_publication_state', 'improvement_notifications_projection_ready'),
                    ('scrape_publication_state', 'improvement_notifications_projection_scrape_id'),
                    ('publication_generations', 'publication_id'),
                    ('publication_generations', 'scrape_id'),
                    ('publication_generations', 'status'),
                    ('publication_generations', 'previous_publication_id'),
                    ('publication_generations', 'failed_at'),
                    ('publication_generations', 'failure_phase'),
                    ('publication_generations', 'failure_message'),
                    ('leaderboard_published_scope_source', 'published_scrape_id'),
                    ('scrape_phase_attempts', 'scrape_id'),
                    ('scrape_phase_attempts', 'phase_id'),
                    ('scrape_phase_attempts', 'attempt'),
                    ('scrape_phase_attempts', 'operation_id'),
                    ('scrape_phase_attempts', 'phase_ordinal'),
                    ('scrape_phase_attempts', 'plan_version'),
                    ('scrape_phase_attempts', 'worker_instance_id'),
                    ('scrape_phase_attempts', 'status'),
                    ('scrape_phase_attempts', 'started_at'),
                    ('scrape_phase_attempts', 'last_progress_at'),
                    ('scrape_phase_attempts', 'heartbeat_at'),
                    ('scrape_phase_attempts', 'completed_at'),
                    ('scrape_phase_attempts', 'warning_message'),
                    ('scrape_phase_attempts', 'error_message'),
                    ('service_worker_status', 'worker_key'),
                    ('service_worker_status', 'status'),
                    ('service_worker_status', 'mode'),
                    ('service_worker_status', 'instance_id'),
                    ('service_worker_status', 'started_at'),
                    ('service_worker_status', 'last_heartbeat_at'),
                    ('service_worker_status', 'last_status_change_at'),
                    ('service_worker_status', 'current_operation_json'),
                    ('service_worker_status', 'last_operation_json'),
                    ('service_worker_status', 'updated_at'),
                    ('live_song_catalog', 'catalog_version'),
                    ('live_song_catalog', 'schema_version'),
                    ('live_song_catalog', 'source_kind'),
                    ('live_song_catalog', 'is_exact'),
                    ('publication_song_catalog', 'catalog_version'),
                    ('publication_song_catalog', 'schema_version'),
                    ('publication_song_catalog', 'source_kind'),
                    ('publication_song_catalog', 'is_exact')
            )
            SELECT
                to_regclass('public.scrape_log') IS NOT NULL
                AND to_regclass('public.scrape_publication_state') IS NOT NULL
                AND to_regclass('public.publication_generations') IS NOT NULL
                AND to_regclass('public.publication_surface_bindings') IS NOT NULL
                AND to_regclass('public.leaderboard_published_scope_source') IS NOT NULL
                AND to_regclass('public.scrape_phase_attempts') IS NOT NULL
                AND to_regclass('public.service_worker_status') IS NOT NULL
                AND to_regclass('public.live_song_catalog') IS NOT NULL
                AND to_regclass('public.publication_song_catalog') IS NOT NULL
                AND to_regclass('public.publication_api_response_cache') IS NOT NULL
                AND to_regclass('public.publication_api_response_cache_staging') IS NOT NULL
                AND NOT EXISTS (
                    SELECT table_name, column_name
                    FROM required_columns
                    EXCEPT
                    SELECT table_name, column_name
                    FROM information_schema.columns
                    WHERE table_schema = 'public')
                AND EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid =
                            to_regclass('public.scrape_log')
                      AND conname =
                            'ck_scrape_log_acquisition_checkpoint'
                      AND convalidated)
            """;
        return command.ExecuteScalar() is true;
    }

    private InterruptedAcquisitionNormalizationReadiness
        ReadInterruptedAcquisitionNormalizationReadiness(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            InterruptedAcquisitionNormalizationCommand command,
            bool lockRows)
    {
        var raw = ReadInterruptedAcquisitionNormalizationState(
            connection,
            transaction,
            command,
            lockRows);
        var blockingReason = GetNormalizationBlockingReason(
            raw,
            command,
            out var readyToNormalize,
            out var alreadyNormalized,
            out var operation);
        var attempt = raw.Attempts.Count == 1
            ? raw.Attempts[0]
            : null;
        var currentGeneration = raw.Generations.SingleOrDefault(
            generation => generation.PublicationId
                == command.ExpectedCurrentPublicationId);
        var previousGeneration = raw.Generations.SingleOrDefault(
            generation => generation.PublicationId
                == command.ExpectedPreviousPublicationId);
        var workingGeneration = raw.Generations.SingleOrDefault(
            generation => generation.PublicationId
                == command.ExpectedWorkingPublicationId);

        return new InterruptedAcquisitionNormalizationReadiness
        {
            ScrapeId = command.ScrapeId,
            ExpectedPublishedScrapeId =
                command.ExpectedPublishedScrapeId,
            ExpectedCurrentPublicationId =
                command.ExpectedCurrentPublicationId,
            ExpectedPreviousPublicationId =
                command.ExpectedPreviousPublicationId,
            ExpectedWorkingPublicationId =
                command.ExpectedWorkingPublicationId,
            ExpectedWorkerInstanceId =
                command.ExpectedWorkerInstanceId,
            ExpectedWorkerFreshnessUtc =
                command.ExpectedWorkerFreshnessUtc,
            ExpectedPhaseId = command.ExpectedPhaseId,
            ExpectedAttempt = command.ExpectedAttempt,
            SchemaReady = true,
            FenceAcquired = true,
            PublishedScrapeId =
                raw.Publication?.PublishedScrapeId,
            CurrentPublicationId =
                raw.Publication?.CurrentPublicationId,
            PreviousPublicationId =
                raw.Publication?.PreviousPublicationId,
            WorkingPublicationId =
                raw.Publication?.WorkingPublicationId,
            PublicReadsFrozen =
                raw.Publication?.PublicReadsFrozen,
            PublicReadsFrozenAtUtc =
                raw.Publication?.PublicReadsFrozenAtUtc,
            FrozenScrapeId =
                raw.Publication?.FrozenScrapeId,
            FreezeReason =
                raw.Publication?.FreezeReason,
            CommitIntentStartedAtUtc =
                raw.Publication?.CommitIntentStartedAtUtc,
            CommitIntentHeartbeatAtUtc =
                raw.Publication?.CommitIntentHeartbeatAtUtc,
            CommitIntentOwner =
                raw.Publication?.CommitIntentOwner,
            ScrapeStatus =
                raw.CandidateScrape?.Status,
            AcquisitionCheckpointPresent =
                raw.CandidateScrape?.AcquisitionCompletedAtUtc
                    is not null,
            AcquisitionCheckpointPayloadPresent =
                raw.CandidateScrape?
                    .AcquisitionCheckpointPayloadPresent
                ?? false,
            OtherRunningScrapeCount =
                raw.OtherRunningScrapeCount,
            NewerScrapeCount =
                raw.NewerScrapeCount,
            CurrentGenerationScrapeId =
                currentGeneration?.ScrapeId,
            CurrentGenerationStatus =
                currentGeneration?.Status,
            CurrentGenerationPreviousPublicationId =
                currentGeneration?.PreviousPublicationId,
            PreviousGenerationScrapeId =
                previousGeneration?.ScrapeId,
            PreviousGenerationStatus =
                previousGeneration?.Status,
            CandidateGenerationScrapeId =
                workingGeneration?.ScrapeId,
            CandidateGenerationStatus =
                workingGeneration?.Status,
            CandidateGenerationCount =
                raw.CandidateGenerationCount,
            CandidateSourceMappingCount =
                raw.CandidateSourceMappingCount,
            ActiveWorkerQueryCount =
                raw.ActiveWorkerQueryCount,
            WaitingLockCount = raw.WaitingLockCount,
            AdvisoryLockCount = raw.AdvisoryLockCount,
            MaintenanceActivityPresent =
                raw.MaintenanceActivityPresent,
            CandidateAttemptCount = raw.Attempts.Count,
            RunningPhaseAttemptCount =
                raw.Attempts.Count(item =>
                    item.Status == "running"),
            GlobalRunningPhaseAttemptCount =
                raw.GlobalRunningPhaseAttemptCount,
            AttemptPhaseId = attempt?.PhaseId,
            Attempt = attempt?.Attempt,
            AttemptStatus = attempt?.Status,
            AttemptWorkerInstanceId =
                attempt?.WorkerInstanceId,
            AttemptCompletedAtUtc =
                attempt?.CompletedAtUtc,
            AttemptWarningMessage =
                attempt?.WarningMessage,
            AttemptErrorMessage =
                attempt?.ErrorMessage,
            WorkerStatus = raw.Worker?.Status,
            WorkerMode = raw.Worker?.Mode,
            WorkerInstanceId = raw.Worker?.InstanceId,
            WorkerFreshnessUtc = raw.Worker?.UpdatedAtUtc,
            WorkerCurrentOperationPresent =
                raw.Worker?.CurrentOperationJson is not null,
            WorkerLastOperationPresent =
                raw.Worker?.LastOperationJson is not null,
            WorkerOperationKey = operation?.OperationKey,
            WorkerOperationPhaseId = operation?.PhaseId,
            WorkerOperationAttempt = operation?.PhaseAttempt,
            WorkerOperationScrapeId = operation?.ScrapeId,
            ReadyToNormalize = readyToNormalize,
            AlreadyNormalized = alreadyNormalized,
            BlockingReason = blockingReason,
        };
    }

    private static InterruptedAcquisitionNormalizationReadiness
        CreateUnavailableNormalizationReadiness(
            InterruptedAcquisitionNormalizationCommand command,
            bool schemaReady,
            bool fenceAcquired,
            string reason)
        => new()
        {
            ScrapeId = command.ScrapeId,
            ExpectedPublishedScrapeId =
                command.ExpectedPublishedScrapeId,
            ExpectedCurrentPublicationId =
                command.ExpectedCurrentPublicationId,
            ExpectedPreviousPublicationId =
                command.ExpectedPreviousPublicationId,
            ExpectedWorkingPublicationId =
                command.ExpectedWorkingPublicationId,
            ExpectedWorkerInstanceId =
                command.ExpectedWorkerInstanceId,
            ExpectedWorkerFreshnessUtc =
                command.ExpectedWorkerFreshnessUtc,
            ExpectedPhaseId = command.ExpectedPhaseId,
            ExpectedAttempt = command.ExpectedAttempt,
            SchemaReady = schemaReady,
            FenceAcquired = fenceAcquired,
            BlockingReason = reason,
        };

    private static string? GetNormalizationBlockingReason(
        InterruptedAcquisitionNormalizationState raw,
        InterruptedAcquisitionNormalizationCommand command,
        out bool readyToNormalize,
        out bool alreadyNormalized,
        out OperationIdentity? operation)
    {
        readyToNormalize = false;
        alreadyNormalized = false;
        operation = null;

        if (raw.Publication is null)
            return "The publication singleton is missing.";
        if (raw.Publication.PublishedScrapeId
            != command.ExpectedPublishedScrapeId)
        {
            return $"Expected published scrape {command.ExpectedPublishedScrapeId}, found {FormatNullable(raw.Publication.PublishedScrapeId)}.";
        }
        if (raw.Publication.CurrentPublicationId
            != command.ExpectedCurrentPublicationId)
        {
            return $"Expected current publication {command.ExpectedCurrentPublicationId}, found {FormatNullable(raw.Publication.CurrentPublicationId)}.";
        }
        if (raw.Publication.PreviousPublicationId
            != command.ExpectedPreviousPublicationId)
        {
            return $"Expected previous publication {command.ExpectedPreviousPublicationId}, found {FormatNullable(raw.Publication.PreviousPublicationId)}.";
        }
        if (raw.Publication.WorkingPublicationId
            != command.ExpectedWorkingPublicationId)
        {
            return $"Expected working publication {command.ExpectedWorkingPublicationId}, found {FormatNullable(raw.Publication.WorkingPublicationId)}.";
        }
        if (raw.Publication.PublicReadsFrozen
            || raw.Publication.PublicReadsFrozenAtUtc is not null
            || raw.Publication.FrozenScrapeId is not null
            || raw.Publication.FreezeReason is not null)
        {
            return "Public-read freeze state is not exactly unfrozen and empty.";
        }
        if (raw.Publication.CommitIntentStartedAtUtc is not null
            || raw.Publication.CommitIntentHeartbeatAtUtc is not null
            || raw.Publication.CommitIntentOwner is not null)
        {
            return "Publication commit-intent state is not empty.";
        }
        if (raw.CandidateScrape is null)
            return $"Scrape {command.ScrapeId} is missing.";
        if (!string.Equals(
                raw.CandidateScrape.Status,
                "running",
                StringComparison.Ordinal))
        {
            return $"Expected scrape {command.ScrapeId} status running, found {raw.CandidateScrape.Status}.";
        }
        if (raw.CandidateScrape.CompletedAtUtc is not null
            || raw.CandidateScrape.FailedAtUtc is not null
            || raw.CandidateScrape.FailurePhase is not null
            || raw.CandidateScrape.FailureMessage is not null)
        {
            return "The running scrape contains terminal scrape state.";
        }
        if (raw.CandidateScrape.AcquisitionCompletedAtUtc is not null)
            return "An acquisition checkpoint is present.";
        if (raw.CandidateScrape
            .AcquisitionCheckpointPayloadPresent)
        {
            return "Partial acquisition checkpoint metrics are present without a checkpoint.";
        }
        if (!string.Equals(
                raw.PublishedScrapeStatus,
                "completed",
                StringComparison.Ordinal))
        {
            return $"Expected published scrape {command.ExpectedPublishedScrapeId} status completed, found {raw.PublishedScrapeStatus ?? "missing"}.";
        }
        if (raw.OtherRunningScrapeCount != 0)
        {
            return $"{raw.OtherRunningScrapeCount} other running scrape(s) are present.";
        }
        if (raw.NewerScrapeCount != 0)
            return $"{raw.NewerScrapeCount} newer scrape(s) are present.";

        var currentGeneration = raw.Generations.SingleOrDefault(
            generation => generation.PublicationId
                == command.ExpectedCurrentPublicationId);
        if (currentGeneration is null
            || currentGeneration.ScrapeId
                != command.ExpectedPublishedScrapeId
            || !string.Equals(
                currentGeneration.Status,
                "current",
                StringComparison.Ordinal)
            || currentGeneration.PreviousPublicationId
                != command.ExpectedPreviousPublicationId
            || currentGeneration.FailedAtUtc is not null
            || currentGeneration.FailurePhase is not null
            || currentGeneration.FailureMessage is not null)
        {
            return "The expected current publication generation identity is not exact.";
        }
        var previousGeneration = raw.Generations.SingleOrDefault(
            generation => generation.PublicationId
                == command.ExpectedPreviousPublicationId);
        if (previousGeneration is null
            || previousGeneration.ScrapeId is null
            || !string.Equals(
                previousGeneration.Status,
                "retained",
                StringComparison.Ordinal)
            || previousGeneration.FailedAtUtc is not null
            || previousGeneration.FailurePhase is not null
            || previousGeneration.FailureMessage is not null)
        {
            return "The expected previous publication generation identity is not exact.";
        }
        var workingGeneration = raw.Generations.SingleOrDefault(
            generation => generation.PublicationId
                == command.ExpectedWorkingPublicationId);
        if (raw.CandidateGenerationCount != 1
            || workingGeneration is null
            || workingGeneration.ScrapeId != command.ScrapeId
            || !string.Equals(
                workingGeneration.Status,
                "building",
                StringComparison.Ordinal)
            || workingGeneration.PreviousPublicationId
                is not null
            || workingGeneration.FailedAtUtc is not null
            || workingGeneration.FailurePhase is not null
            || workingGeneration.FailureMessage is not null)
        {
            return "The expected working publication generation identity is not exact.";
        }
        if (raw.CandidateSourceMappingCount != 0)
        {
            return $"Scrape {command.ScrapeId} owns {raw.CandidateSourceMappingCount} candidate source mapping(s).";
        }
        if (raw.ActiveWorkerQueryCount != 0)
        {
            return $"Worker still owns {raw.ActiveWorkerQueryCount} active database query or transaction(s).";
        }
        if (raw.WaitingLockCount != 0)
            return $"{raw.WaitingLockCount} waiting database lock(s) remain.";
        if (raw.AdvisoryLockCount != 0)
            return $"{raw.AdvisoryLockCount} foreign advisory database lock(s) remain.";
        if (raw.MaintenanceActivityPresent)
            return "Database maintenance progress remains active.";
        if (raw.GlobalRunningPhaseAttemptCount != 0)
        {
            return $"{raw.GlobalRunningPhaseAttemptCount} running phase attempt(s) remain.";
        }
        if (raw.Attempts.Count != 1)
        {
            return $"Expected exactly one phase attempt for scrape {command.ScrapeId}, found {raw.Attempts.Count}.";
        }

        var attempt = raw.Attempts[0];
        if (!string.Equals(
                attempt.PhaseId,
                command.ExpectedPhaseId,
                StringComparison.Ordinal)
            || attempt.Attempt != command.ExpectedAttempt)
        {
            return "The sole phase attempt does not match the expected phase and attempt.";
        }
        if (!string.Equals(
                attempt.WorkerInstanceId,
                command.ExpectedWorkerInstanceId,
                StringComparison.Ordinal))
        {
            return "The interrupted phase attempt belongs to a foreign worker instance.";
        }
        if (string.IsNullOrWhiteSpace(attempt.OperationId)
            || string.IsNullOrWhiteSpace(attempt.PlanVersion)
            || attempt.PhaseOrdinal < 0
            || attempt.CompletedAtUtc is null
            || attempt.StartedAtUtc > attempt.CompletedAtUtc
            || attempt.LastProgressAtUtc
                != attempt.CompletedAtUtc
            || attempt.HeartbeatAtUtc
                != attempt.CompletedAtUtc
            || string.IsNullOrWhiteSpace(
                attempt.WarningMessage))
        {
            return "The interrupted phase attempt provenance or terminal timestamps are malformed.";
        }
        if (raw.Worker is null)
            return "The scraper worker row is missing.";
        if (!string.Equals(
                raw.Worker.Status,
                "offline",
                StringComparison.Ordinal)
            || !string.Equals(
                raw.Worker.Mode,
                "scraper",
                StringComparison.Ordinal))
        {
            return "The scraper worker is not exactly offline in scraper mode.";
        }
        if (!string.Equals(
                raw.Worker.InstanceId,
                command.ExpectedWorkerInstanceId,
                StringComparison.Ordinal))
        {
            return "The scraper worker instance identity changed.";
        }
        if (!SamePostgresMicrosecond(
                raw.Worker.UpdatedAtUtc,
                command.ExpectedWorkerFreshnessUtc)
            || !SamePostgresMicrosecond(
                raw.Worker.LastHeartbeatAtUtc,
                command.ExpectedWorkerFreshnessUtc)
            || !SamePostgresMicrosecond(
                raw.Worker.LastStatusChangeAtUtc,
                command.ExpectedWorkerFreshnessUtc)
            || raw.Worker.StartedAtUtc is null
            || raw.Worker.StartedAtUtc
                > command.ExpectedWorkerFreshnessUtc)
        {
            return "The scraper worker freshness identity changed.";
        }

        if (string.Equals(
                attempt.Status,
                "interrupted",
                StringComparison.Ordinal))
        {
            if (attempt.ErrorMessage is not null)
                return "The interrupted phase attempt already contains an error message.";
            if (raw.Worker.CurrentOperationJson is null)
                return "The exact interrupted worker current operation is missing.";
            if (!TryReadOperationIdentity(
                    raw.Worker.CurrentOperationJson,
                    out operation,
                    out var operationError))
            {
                return operationError;
            }
            if (!OperationMatchesAttempt(
                    operation,
                    attempt,
                    command,
                    normalized: false,
                    out operationError))
            {
                return operationError;
            }

            readyToNormalize = true;
            return null;
        }

        if (!string.Equals(
                attempt.Status,
                "failed",
                StringComparison.Ordinal)
            || !string.Equals(
                attempt.ErrorMessage,
                InterruptedAcquisitionNormalizationMessage,
                StringComparison.Ordinal))
        {
            return $"Expected interrupted or exactly normalized failed attempt, found {attempt.Status}.";
        }
        if (raw.Worker.CurrentOperationJson is not null)
            return "A current worker operation remains after normalization.";
        if (raw.Worker.LastOperationJson is null)
            return "The normalized worker last-operation history is missing.";
        if (!TryReadOperationIdentity(
                raw.Worker.LastOperationJson,
                out operation,
                out var normalizedOperationError))
        {
            return normalizedOperationError;
        }
        if (!OperationMatchesAttempt(
                operation,
                attempt,
                command,
                normalized: true,
                out normalizedOperationError))
        {
            return normalizedOperationError;
        }

        alreadyNormalized = true;
        return null;
    }

    private static bool OperationMatchesAttempt(
        OperationIdentity operation,
        PhaseAttemptState attempt,
        InterruptedAcquisitionNormalizationCommand command,
        bool normalized,
        out string? error)
    {
        error = null;
        if (operation.ContractVersion != 2
            || !string.Equals(
                operation.OperationKey,
                command.ExpectedPhaseId,
                StringComparison.Ordinal)
            || operation.ScrapeId != command.ScrapeId
            || !string.Equals(
                operation.PhaseId,
                command.ExpectedPhaseId,
                StringComparison.Ordinal)
            || operation.PhaseAttempt
                != command.ExpectedAttempt
            || !string.Equals(
                operation.OperationId,
                attempt.OperationId,
                StringComparison.Ordinal)
            || operation.PhaseOrdinal
                != attempt.PhaseOrdinal
            || !string.Equals(
                operation.PhasePlanVersion,
                attempt.PlanVersion,
                StringComparison.Ordinal))
        {
            error =
                "The worker operation identity does not exactly match the interrupted phase attempt.";
            return false;
        }
        if (!SamePostgresMicrosecond(
                operation.HeartbeatAtUtc,
                command.ExpectedWorkerFreshnessUtc)
            || operation.StartedAtUtc is null
            || operation.UpdatedAtUtc is null
            || operation.StartedAtUtc
                > operation.UpdatedAtUtc
            || operation.UpdatedAtUtc
                > command.ExpectedWorkerFreshnessUtc)
        {
            error =
                "The worker operation timestamps do not match the expected worker freshness.";
            return false;
        }

        if (!normalized)
        {
            if (!string.Equals(
                    operation.Status,
                    "running",
                    StringComparison.Ordinal)
                || !string.Equals(
                    operation.PhaseStatus,
                    "running",
                    StringComparison.Ordinal)
                || operation.EndedAtUtc is not null)
            {
                error =
                    "The worker current operation is not the exact running acquisition operation.";
                return false;
            }

            return true;
        }

        if (!string.Equals(
                operation.Status,
                "failed",
                StringComparison.Ordinal)
            || !string.Equals(
                operation.PhaseStatus,
                "failed",
                StringComparison.Ordinal)
            || !string.Equals(
                operation.Detail,
                InterruptedAcquisitionNormalizationMessage,
                StringComparison.Ordinal)
            || !SamePostgresMicrosecond(
                operation.EndedAtUtc,
                attempt.CompletedAtUtc)
            || !SamePostgresMicrosecond(
                operation.UpdatedAtUtc,
                attempt.CompletedAtUtc))
        {
            error =
                "The worker last-operation history is not the exact normalized acquisition operation.";
            return false;
        }

        return true;
    }

    private static bool TryReadOperationIdentity(
        string json,
        out OperationIdentity identity,
        out string error)
    {
        identity = new OperationIdentity();
        error = "The worker operation JSON is malformed.";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetInt32(
                    root,
                    "ContractVersion",
                    out var contractVersion)
                || !TryGetString(
                    root,
                    "OperationKey",
                    out var operationKey)
                || !TryGetString(
                    root,
                    "Status",
                    out var status)
                || !TryGetInt64(
                    root,
                    "ScrapeId",
                    out var scrapeId)
                || !TryGetString(
                    root,
                    "OperationId",
                    out var operationId)
                || !TryGetString(
                    root,
                    "PhaseId",
                    out var phaseId)
                || !TryGetString(
                    root,
                    "PhaseStatus",
                    out var phaseStatus)
                || !TryGetString(
                    root,
                    "PhasePlanVersion",
                    out var phasePlanVersion)
                || !TryGetInt32(
                    root,
                    "PhaseOrdinal",
                    out var phaseOrdinal)
                || !TryGetInt32(
                    root,
                    "PhaseAttempt",
                    out var phaseAttempt)
                || !TryGetUtcDateTime(
                    root,
                    "StartedAtUtc",
                    required: true,
                    out var startedAtUtc)
                || !TryGetUtcDateTime(
                    root,
                    "UpdatedAtUtc",
                    required: true,
                    out var updatedAtUtc)
                || !TryGetUtcDateTime(
                    root,
                    "HeartbeatAtUtc",
                    required: true,
                    out var heartbeatAtUtc)
                || !TryGetUtcDateTime(
                    root,
                    "EndedAtUtc",
                    required: false,
                    out var endedAtUtc))
            {
                return false;
            }

            _ = TryGetString(
                root,
                "Detail",
                out var detail,
                required: false);
            identity = new OperationIdentity
            {
                ContractVersion = contractVersion,
                OperationKey = operationKey,
                Status = status,
                ScrapeId = scrapeId,
                OperationId = operationId,
                PhaseId = phaseId,
                PhaseStatus = phaseStatus,
                PhasePlanVersion = phasePlanVersion,
                PhaseOrdinal = phaseOrdinal,
                PhaseAttempt = phaseAttempt,
                Detail = detail,
                StartedAtUtc = startedAtUtc,
                UpdatedAtUtc = updatedAtUtc,
                HeartbeatAtUtc = heartbeatAtUtc,
                EndedAtUtc = endedAtUtc,
            };
            error = "";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetString(
        JsonElement root,
        string propertyName,
        out string? value,
        bool required = true)
    {
        value = null;
        if (!root.TryGetProperty(
                propertyName,
                out var property))
        {
            return !required;
        }
        if (property.ValueKind == JsonValueKind.Null)
            return !required;
        if (property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString();
        return !required || !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetInt32(
        JsonElement root,
        string propertyName,
        out int? value)
    {
        value = null;
        if (!root.TryGetProperty(
                propertyName,
                out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var parsed))
        {
            return false;
        }
        value = parsed;
        return true;
    }

    private static bool TryGetInt64(
        JsonElement root,
        string propertyName,
        out long? value)
    {
        value = null;
        if (!root.TryGetProperty(
                propertyName,
                out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var parsed))
        {
            return false;
        }
        value = parsed;
        return true;
    }

    private static bool TryGetUtcDateTime(
        JsonElement root,
        string propertyName,
        bool required,
        out DateTime? value)
    {
        value = null;
        if (!root.TryGetProperty(
                propertyName,
                out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return !required;
        }
        if (property.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                property.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal
                | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }
        value = parsed.UtcDateTime;
        return true;
    }

    private static bool NormalizeInterruptedAcquisitionAttempt(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InterruptedAcquisitionNormalizationCommand command,
        InterruptedAcquisitionNormalizationState raw)
    {
        var attempt = raw.Attempts.Single();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE scrape_phase_attempts
            SET status = 'failed',
                error_message = @normalizationMessage
            WHERE scrape_id = @scrapeId
              AND phase_id = @phaseId
              AND attempt = @attempt
              AND operation_id = @operationId
              AND phase_ordinal = @phaseOrdinal
              AND plan_version = @planVersion
              AND worker_instance_id = @workerInstanceId
              AND status = 'interrupted'
              AND completed_at = @completedAt
              AND last_progress_at = @completedAt
              AND heartbeat_at = @completedAt
              AND warning_message IS NOT DISTINCT FROM @warningMessage
              AND error_message IS NULL
            """;
        update.Parameters.AddWithValue(
            "normalizationMessage",
            InterruptedAcquisitionNormalizationMessage);
        update.Parameters.AddWithValue(
            "scrapeId",
            command.ScrapeId);
        update.Parameters.AddWithValue(
            "phaseId",
            command.ExpectedPhaseId);
        update.Parameters.AddWithValue(
            "attempt",
            command.ExpectedAttempt);
        update.Parameters.AddWithValue(
            "operationId",
            attempt.OperationId);
        update.Parameters.AddWithValue(
            "phaseOrdinal",
            attempt.PhaseOrdinal);
        update.Parameters.AddWithValue(
            "planVersion",
            attempt.PlanVersion);
        update.Parameters.AddWithValue(
            "workerInstanceId",
            command.ExpectedWorkerInstanceId);
        update.Parameters.AddWithValue(
            "completedAt",
            attempt.CompletedAtUtc!.Value);
        update.Parameters.AddWithValue(
            "warningMessage",
            attempt.WarningMessage!);
        return update.ExecuteNonQuery() == 1;
    }

    private static bool NormalizeInterruptedAcquisitionWorkerOperation(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InterruptedAcquisitionNormalizationCommand command,
        InterruptedAcquisitionNormalizationState raw)
    {
        var attempt = raw.Attempts.Single();
        var worker = raw.Worker!;
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE service_worker_status
            SET last_operation_json =
                    current_operation_json
                    || jsonb_build_object(
                        'Status',
                        'failed',
                        'PhaseStatus',
                        'failed',
                        'Detail',
                        @normalizationMessage,
                        'UpdatedAtUtc',
                        @endedAtText,
                        'EndedAtUtc',
                        @endedAtText,
                        'ElapsedSeconds',
                        to_jsonb(
                            EXTRACT(
                                EPOCH FROM (
                                    @endedAt
                                    - (
                                        current_operation_json
                                            ->> 'StartedAtUtc'
                                      )::timestamptz)))),
                current_operation_json = NULL
            WHERE worker_key = @workerKey
              AND status = 'offline'
              AND mode = 'scraper'
              AND instance_id = @workerInstanceId
              AND updated_at = @workerFreshness
              AND last_heartbeat_at = @workerFreshness
              AND last_status_change_at = @workerFreshness
              AND current_operation_json = @currentOperation
            """;
        update.Parameters.AddWithValue(
            "normalizationMessage",
            InterruptedAcquisitionNormalizationMessage);
        update.Parameters.AddWithValue(
            "endedAt",
            attempt.CompletedAtUtc!.Value);
        update.Parameters.AddWithValue(
            "endedAtText",
            attempt.CompletedAtUtc.Value.ToString(
                "O",
                CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue(
            "workerKey",
            WorkerStatusPublisher.ScraperWorkerKey);
        update.Parameters.AddWithValue(
            "workerInstanceId",
            command.ExpectedWorkerInstanceId);
        update.Parameters.AddWithValue(
            "workerFreshness",
            command.ExpectedWorkerFreshnessUtc);
        var operationParameter = update.Parameters.Add(
            "currentOperation",
            NpgsqlDbType.Jsonb);
        operationParameter.Value = worker.CurrentOperationJson!;
        return update.ExecuteNonQuery() == 1;
    }

    private static InterruptedAcquisitionNormalizationState
        ReadInterruptedAcquisitionNormalizationState(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            InterruptedAcquisitionNormalizationCommand command,
            bool lockRows)
    {
        var state = new InterruptedAcquisitionNormalizationState
        {
            Publication = ReadNormalizationPublication(
                connection,
                transaction,
                lockRows),
        };
        ReadNormalizationScrapes(
            connection,
            transaction,
            command,
            lockRows,
            state);
        ReadNormalizationGenerations(
            connection,
            transaction,
            command,
            lockRows,
            state);
        state.Attempts.AddRange(
            ReadNormalizationAttempts(
                connection,
                transaction,
                command,
                lockRows));
        state.Worker = ReadNormalizationWorker(
            connection,
            transaction,
            lockRows);
        ReadNormalizationBlockers(
            connection,
            transaction,
            command,
            state);
        return state;
    }

    private static PublicationState? ReadNormalizationPublication(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        bool lockRows)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                published_scrape_id,
                current_publication_id,
                previous_publication_id,
                working_publication_id,
                public_reads_frozen,
                public_reads_frozen_at,
                public_reads_frozen_scrape_id,
                public_reads_frozen_reason,
                publication_commit_intent_started_at,
                publication_commit_intent_heartbeat_at,
                publication_commit_intent_owner
            FROM scrape_publication_state
            WHERE id = TRUE
            """
            + (lockRows ? " FOR UPDATE" : "");
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new PublicationState(
            ReadNullableInt64(reader, 0),
            ReadNullableInt64(reader, 1),
            ReadNullableInt64(reader, 2),
            ReadNullableInt64(reader, 3),
            reader.GetBoolean(4),
            GetNullableUtc(reader, 5),
            ReadNullableInt64(reader, 6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            GetNullableUtc(reader, 8),
            GetNullableUtc(reader, 9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private static void ReadNormalizationScrapes(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InterruptedAcquisitionNormalizationCommand requested,
        bool lockRows,
        InterruptedAcquisitionNormalizationState state)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                id,
                status,
                acquisition_completed_at,
                (
                    songs_scraped IS NOT NULL
                    OR total_entries IS NOT NULL
                    OR total_requests IS NOT NULL
                    OR total_bytes IS NOT NULL
                    OR expected_solo_scope_count IS NOT NULL
                    OR expected_solo_scope_fingerprint_version
                        IS NOT NULL
                    OR expected_solo_scope_fingerprint IS NOT NULL
                ),
                completed_at,
                failed_at,
                failure_phase,
                failure_message
            FROM scrape_log
            WHERE id IN (@scrapeId, @publishedScrapeId)
            ORDER BY id
            """
            + (lockRows ? " FOR UPDATE" : "");
        command.Parameters.AddWithValue(
            "scrapeId",
            requested.ScrapeId);
        command.Parameters.AddWithValue(
            "publishedScrapeId",
            requested.ExpectedPublishedScrapeId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var scrape = new ScrapeState(
                reader.GetString(1),
                GetNullableUtc(reader, 2),
                reader.GetBoolean(3),
                GetNullableUtc(reader, 4),
                GetNullableUtc(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7));
            if (id == requested.ScrapeId)
                state.CandidateScrape = scrape;
            if (id == requested.ExpectedPublishedScrapeId)
                state.PublishedScrapeStatus = scrape.Status;
        }
    }

    private static void ReadNormalizationGenerations(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InterruptedAcquisitionNormalizationCommand requested,
        bool lockRows,
        InterruptedAcquisitionNormalizationState state)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                publication_id,
                scrape_id,
                status,
                previous_publication_id,
                failed_at,
                failure_phase,
                failure_message
            FROM publication_generations
            WHERE publication_id IN (
                    @currentPublicationId,
                    @previousPublicationId,
                    @workingPublicationId)
               OR scrape_id = @scrapeId
            ORDER BY publication_id
            """
            + (lockRows ? " FOR UPDATE" : "");
        command.Parameters.AddWithValue(
            "currentPublicationId",
            requested.ExpectedCurrentPublicationId);
        command.Parameters.AddWithValue(
            "previousPublicationId",
            requested.ExpectedPreviousPublicationId);
        command.Parameters.AddWithValue(
            "workingPublicationId",
            requested.ExpectedWorkingPublicationId);
        command.Parameters.AddWithValue(
            "scrapeId",
            requested.ScrapeId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var generation = new GenerationState(
                reader.GetInt64(0),
                reader.IsDBNull(1)
                    ? null
                    : reader.GetInt64(1),
                reader.GetString(2),
                ReadNullableInt64(reader, 3),
                GetNullableUtc(reader, 4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6));
            state.Generations.Add(generation);
            if (generation.ScrapeId == requested.ScrapeId)
                state.CandidateGenerationCount++;
        }
    }

    private static IReadOnlyList<PhaseAttemptState>
        ReadNormalizationAttempts(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            InterruptedAcquisitionNormalizationCommand requested,
            bool lockRows)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                phase_id,
                attempt,
                operation_id,
                phase_ordinal,
                plan_version,
                worker_instance_id,
                status,
                started_at,
                last_progress_at,
                heartbeat_at,
                completed_at,
                warning_message,
                error_message
            FROM scrape_phase_attempts
            WHERE scrape_id = @scrapeId
            ORDER BY phase_id, attempt
            """
            + (lockRows ? " FOR UPDATE" : "");
        command.Parameters.AddWithValue(
            "scrapeId",
            requested.ScrapeId);
        using var reader = command.ExecuteReader();
        var attempts = new List<PhaseAttemptState>();
        while (reader.Read())
        {
            attempts.Add(new PhaseAttemptState(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                GetUtc(reader, 7),
                GetUtc(reader, 8),
                GetUtc(reader, 9),
                GetNullableUtc(reader, 10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }
        return attempts;
    }

    private static WorkerState? ReadNormalizationWorker(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        bool lockRows)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                status,
                mode,
                instance_id,
                started_at,
                last_heartbeat_at,
                last_status_change_at,
                current_operation_json::TEXT,
                last_operation_json::TEXT,
                updated_at
            FROM service_worker_status
            WHERE worker_key = @workerKey
            """
            + (lockRows ? " FOR UPDATE" : "");
        command.Parameters.AddWithValue(
            "workerKey",
            WorkerStatusPublisher.ScraperWorkerKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new WorkerState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            GetNullableUtc(reader, 3),
            GetNullableUtc(reader, 4),
            GetUtc(reader, 5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            GetUtc(reader, 8));
    }

    private static void ReadNormalizationBlockers(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InterruptedAcquisitionNormalizationCommand requested,
        InterruptedAcquisitionNormalizationState state)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (
                    SELECT COUNT(*)::INTEGER
                    FROM leaderboard_published_scope_source
                    WHERE published_scrape_id = @scrapeId
                ),
                (
                    SELECT COUNT(*)::INTEGER
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND state <> 'idle'
                      AND application_name =
                            ANY(@workerApplicationNames)
                ),
                (
                    SELECT COUNT(*)::INTEGER
                    FROM pg_locks lock
                    JOIN pg_stat_activity activity
                      ON activity.pid = lock.pid
                    WHERE activity.datname = current_database()
                      AND lock.pid <> pg_backend_pid()
                      AND NOT lock.granted
                ),
                (
                    SELECT COUNT(*)::INTEGER
                    FROM pg_locks lock
                    JOIN pg_stat_activity activity
                      ON activity.pid = lock.pid
                    WHERE activity.datname = current_database()
                      AND lock.pid <> pg_backend_pid()
                      AND lock.locktype = 'advisory'
                ),
                (
                    EXISTS (
                        SELECT 1
                        FROM pg_stat_progress_vacuum
                        WHERE datname = current_database())
                    OR EXISTS (
                        SELECT 1
                        FROM pg_stat_progress_create_index
                        WHERE datname = current_database())
                    OR EXISTS (
                        SELECT 1
                        FROM pg_stat_progress_cluster
                        WHERE datname = current_database())
                    OR EXISTS (
                        SELECT 1
                        FROM pg_stat_progress_analyze
                        WHERE datname = current_database())
                ),
                (
                    SELECT COUNT(*)::INTEGER
                    FROM scrape_phase_attempts
                    WHERE status = 'running'
                ),
                (
                    SELECT COUNT(*)::INTEGER
                    FROM scrape_log
                    WHERE status = 'running'
                      AND id <> @scrapeId
                ),
                (
                    SELECT COUNT(*)::INTEGER
                    FROM scrape_log
                    WHERE id > @scrapeId
                )
            """;
        command.Parameters.AddWithValue(
            "scrapeId",
            requested.ScrapeId);
        command.Parameters.AddWithValue(
            "workerApplicationNames",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            ActiveScrapeFailureIsolationWorkerApplicationNames);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                "Interrupted-acquisition normalization blocker state is missing.");
        }
        state.CandidateSourceMappingCount = reader.GetInt32(0);
        state.ActiveWorkerQueryCount = reader.GetInt32(1);
        state.WaitingLockCount = reader.GetInt32(2);
        state.AdvisoryLockCount = reader.GetInt32(3);
        state.MaintenanceActivityPresent = reader.GetBoolean(4);
        state.GlobalRunningPhaseAttemptCount = reader.GetInt32(5);
        state.OtherRunningScrapeCount = reader.GetInt32(6);
        state.NewerScrapeCount = reader.GetInt32(7);
    }

    private static long? ReadNullableInt64(
        NpgsqlDataReader reader,
        int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt64(reader.GetValue(ordinal));

    private static string FormatNullable(long? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static bool SamePostgresMicrosecond(
        DateTime? left,
        DateTime? right)
        => left.HasValue
            && right.HasValue
            && TruncateToPostgresMicrosecond(left.Value)
                == TruncateToPostgresMicrosecond(right.Value);

    private static DateTime TruncateToPostgresMicrosecond(
        DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();
        return new DateTime(
            utc.Ticks - (utc.Ticks % 10),
            DateTimeKind.Utc);
    }

    private sealed class InterruptedAcquisitionNormalizationState
    {
        public PublicationState? Publication { get; set; }
        public ScrapeState? CandidateScrape { get; set; }
        public string? PublishedScrapeStatus { get; set; }
        public List<GenerationState> Generations { get; } = [];
        public int CandidateGenerationCount { get; set; }
        public List<PhaseAttemptState> Attempts { get; } = [];
        public WorkerState? Worker { get; set; }
        public int CandidateSourceMappingCount { get; set; }
        public int ActiveWorkerQueryCount { get; set; }
        public int WaitingLockCount { get; set; }
        public int AdvisoryLockCount { get; set; }
        public bool MaintenanceActivityPresent { get; set; }
        public int GlobalRunningPhaseAttemptCount { get; set; }
        public int OtherRunningScrapeCount { get; set; }
        public int NewerScrapeCount { get; set; }
    }

    private sealed record PublicationState(
        long? PublishedScrapeId,
        long? CurrentPublicationId,
        long? PreviousPublicationId,
        long? WorkingPublicationId,
        bool PublicReadsFrozen,
        DateTime? PublicReadsFrozenAtUtc,
        long? FrozenScrapeId,
        string? FreezeReason,
        DateTime? CommitIntentStartedAtUtc,
        DateTime? CommitIntentHeartbeatAtUtc,
        string? CommitIntentOwner);

    private sealed record ScrapeState(
        string Status,
        DateTime? AcquisitionCompletedAtUtc,
        bool AcquisitionCheckpointPayloadPresent,
        DateTime? CompletedAtUtc,
        DateTime? FailedAtUtc,
        string? FailurePhase,
        string? FailureMessage);

    private sealed record GenerationState(
        long PublicationId,
        long? ScrapeId,
        string Status,
        long? PreviousPublicationId,
        DateTime? FailedAtUtc,
        string? FailurePhase,
        string? FailureMessage);

    private sealed record PhaseAttemptState(
        string PhaseId,
        int Attempt,
        string OperationId,
        int PhaseOrdinal,
        string PlanVersion,
        string WorkerInstanceId,
        string Status,
        DateTime StartedAtUtc,
        DateTime LastProgressAtUtc,
        DateTime HeartbeatAtUtc,
        DateTime? CompletedAtUtc,
        string? WarningMessage,
        string? ErrorMessage);

    private sealed record WorkerState(
        string Status,
        string? Mode,
        string? InstanceId,
        DateTime? StartedAtUtc,
        DateTime? LastHeartbeatAtUtc,
        DateTime LastStatusChangeAtUtc,
        string? CurrentOperationJson,
        string? LastOperationJson,
        DateTime UpdatedAtUtc);

    private sealed record OperationIdentity
    {
        public int? ContractVersion { get; init; }
        public string? OperationKey { get; init; }
        public string? Status { get; init; }
        public long? ScrapeId { get; init; }
        public string? OperationId { get; init; }
        public string? PhaseId { get; init; }
        public string? PhaseStatus { get; init; }
        public string? PhasePlanVersion { get; init; }
        public int? PhaseOrdinal { get; init; }
        public int? PhaseAttempt { get; init; }
        public string? Detail { get; init; }
        public DateTime? StartedAtUtc { get; init; }
        public DateTime? UpdatedAtUtc { get; init; }
        public DateTime? HeartbeatAtUtc { get; init; }
        public DateTime? EndedAtUtc { get; init; }
    }
}
