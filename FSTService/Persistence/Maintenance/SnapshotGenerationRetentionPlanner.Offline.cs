using System.Data;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Persistence.Maintenance;

public interface ISnapshotGenerationRetentionOfflineAttestation
{
    Task VerifyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct);
}

public sealed record SnapshotGenerationRetentionOfflineBoundary(
    long ScrapeId,
    long PublicationId,
    string WorkerInstanceId,
    DateTime WorkerStartedAtUtc,
    DateTime WorkerStoppedAtUtc,
    DateTime WorkerHeartbeatAtUtc,
    DateTime ObservedAtUtc,
    string WorkerConfigurationSha256);

public sealed record SnapshotGenerationRetentionOfflineResult(
    SnapshotGenerationRetentionPlanResult Result,
    SnapshotGenerationRetentionCycle Cycle,
    SnapshotGenerationRetentionOfflineBoundary Boundary)
{
    public IReadOnlyList<SnapshotGenerationRetentionPhaseTiming> Timings { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public SnapshotGenerationRetentionOfflineCompletion? Completion { get; init; }
}

public sealed class SnapshotGenerationRetentionOfflineRefusal(string code)
    : InvalidOperationException(code)
{
    public string Code { get; } = code;
    public string? Phase { get; internal set; }
    public double ElapsedMilliseconds { get; internal set; }
    public IReadOnlyList<SnapshotGenerationRetentionPhaseTiming> Timings { get; internal set; } = [];
    public long? PossibleCommittedCycleId { get; init; }
}

public sealed partial class SnapshotGenerationRetentionPlanner
{
    public const int OfflineCommandTimeoutSeconds = 15;
    public const int OfflineTotalTimeoutSeconds = 120;
    public const int OfflineBoundaryMaximumAgeSeconds = 900;

    private PostgresUnpooledConnectionFactory? _offlineConnections;

    public static SnapshotGenerationRetentionPlanner CreateForOffline(
        NpgsqlDataSource dataSource,
        SnapshotGenerationRetentionRepository repository,
        ISnapshotGenerationRetentionOracle oracle,
        ServiceMaintenanceLock serviceMaintenanceLock,
        IOptions<DatabaseMaintenanceOptions> options,
        IOptions<ScraperOptions> scraperOptions,
        ILogger<SnapshotGenerationRetentionPlanner> log,
        PostgresUnpooledConnectionFactory dedicatedConnections) =>
        new(dataSource, repository, oracle, serviceMaintenanceLock, options, scraperOptions, log)
        {
            _offlineConnections = dedicatedConnections
                ?? throw new ArgumentNullException(nameof(dedicatedConnections)),
        };

    public async Task<SnapshotGenerationRetentionOfflineResult> ObserveCurrentOfflineAsync(
        ISnapshotGenerationRetentionOfflineAttestation attestation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        var connections = _offlineConnections
            ?? throw new SnapshotGenerationRetentionOfflineRefusal("dedicated_connection_factory_required");
        if (!IsEnabled)
            throw new SnapshotGenerationRetentionOfflineRefusal("report_only_planner_disabled");
        if (OfflineObservationBudget <= TimeSpan.Zero
            || OfflineObservationBudget > TimeSpan.FromSeconds(OfflineTotalTimeoutSeconds)
            || OfflineFenceTransactionTimeoutSeconds is < 1 or > OfflineTotalTimeoutSeconds)
            throw new InvalidOperationException("Offline observation bounds cannot exceed the accepted budget.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(OfflineObservationBudget);
        var timing = new SnapshotGenerationRetentionTiming();
        var operation = new OfflineOperationState();
        try
        {
            var result = await ObserveCurrentOfflineCoreAsync(
                connections, attestation, operation, timing, ct, deadline.Token);
            return result with { Timings = timing.Snapshot() };
        }
        catch (Exception exception)
        {
            if (operation.CommittedCandidate is { } committed)
            {
                var recovered = await ConfirmCommittedAfterCleanupAsync(
                    connections, committed, CancellationToken.None);
                if (recovered is not null)
                    return recovered with { Timings = timing.Snapshot() };
                throw new SnapshotGenerationRetentionOfflineRefusal("commit_outcome_uncertain")
                {
                    PossibleCommittedCycleId = committed.Cycle.CycleId,
                    Phase = timing.Phase,
                    ElapsedMilliseconds = timing.ElapsedMilliseconds,
                    Timings = timing.Snapshot(),
                };
            }
            if (IsOfflineBudgetFailure(exception, ct, deadline.Token))
                throw new SnapshotGenerationRetentionOfflineRefusal("observation_budget_exceeded")
                {
                    Phase = timing.Phase, ElapsedMilliseconds = timing.ElapsedMilliseconds,
                    Timings = timing.Snapshot(),
                };
            if (exception is SnapshotGenerationRetentionOfflineRefusal refusal)
            {
                refusal.Phase = timing.Phase;
                refusal.ElapsedMilliseconds = timing.ElapsedMilliseconds;
                refusal.Timings = timing.Snapshot();
            }
            throw;
        }
    }

    private async Task<SnapshotGenerationRetentionOfflineResult> ObserveCurrentOfflineCoreAsync(
        PostgresUnpooledConnectionFactory connections,
        ISnapshotGenerationRetentionOfflineAttestation attestation,
        OfflineOperationState operation,
        SnapshotGenerationRetentionTiming timing,
        CancellationToken external,
        CancellationToken ct)
    {
        await using var connection = connections.CreateHostConnection(
            10, OfflineCommandTimeoutSeconds,
            "-c statement_timeout=15s -c lock_timeout=2s -c row_security=off "
                + "-c idle_session_timeout=20s -c idle_in_transaction_session_timeout=20s "
                + "-c transaction_timeout=120s");
        await connection.OpenAsync(ct);
        await using (var settings = connection.CreateCommand())
        {
            settings.CommandTimeout = OfflineCommandTimeoutSeconds;
            settings.CommandText = """
                SET search_path TO pg_catalog, public;
                SET row_security TO off;
                SET lock_timeout TO '2s';
                SET statement_timeout TO '15s';
                SET idle_session_timeout TO '20s';
                SET idle_in_transaction_session_timeout TO '20s';
                SET transaction_timeout TO '120s';
                """;
            await settings.ExecuteNonQueryAsync(ct);
        }

        string databaseSignature;
        using (timing.Measure("preflight_identity"))
        await using (var identityTransaction = await connection.BeginTransactionAsync(ct))
        {
            await using var readOnly = connection.CreateCommand();
            readOnly.Transaction = identityTransaction;
            readOnly.CommandTimeout = OfflineCommandTimeoutSeconds;
            readOnly.CommandText = "SET TRANSACTION READ ONLY";
            await readOnly.ExecuteNonQueryAsync(ct);
            await attestation.VerifyAsync(connection, identityTransaction, ct);
            databaseSignature = await CaptureOfflineDatabaseSignatureAsync(connection, identityTransaction, ct);
            await identityTransaction.CommitAsync(ct);
        }

        await using var fenceConnection = connections.CreateHostConnection(
            10, OfflineCommandTimeoutSeconds,
            "-c lock_timeout=2s -c statement_timeout=15s -c transaction_timeout="
                + OfflineFenceTransactionTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s "
                + "-c idle_in_transaction_session_timeout=120s");
        await fenceConnection.OpenAsync(ct);
        await using var fenceTransaction = await fenceConnection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        var fenceOwner = await CaptureOfflineOwnerAsync(fenceConnection, fenceTransaction, ct);
        using (timing.Measure("transactional_admission"))
        {
            var failure = await AcquirePlannerTransactionLocksAsync(
                fenceConnection, fenceTransaction, TimeSpan.FromMilliseconds(250), ct);
            if (failure is not null)
                throw new SnapshotGenerationRetentionOfflineRefusal(failure.Code);
        }
        if (OfflineFenceAdmittedTestHook is not null)
            await OfflineFenceAdmittedTestHook(fenceConnection, fenceTransaction, ct);

        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, ct);
        await using (var fence = connection.CreateCommand())
        {
            fence.Transaction = transaction;
            fence.CommandTimeout = OfflineCommandTimeoutSeconds;
            fence.CommandText = """
                LOCK TABLE ONLY public.snapshot_generation_retention_cycles,
                    ONLY public.snapshot_generation_retention_observations,
                    ONLY public.snapshot_generation_retention_deferrals,
                    ONLY public.snapshot_generation_retention_evidence
                IN SHARE ROW EXCLUSIVE MODE;
                LOCK TABLE ONLY public.scrape_writer_failures
                IN SHARE MODE;
                LOCK TABLE ONLY public.snapshot_generation_retention_holds
                IN SHARE MODE;
                LOCK TABLE ONLY public.service_worker_status
                IN SHARE MODE;
                LOCK TABLE ONLY public.snapshot_generation_retention_worker_configuration
                IN SHARE MODE;
                SELECT id
                FROM public.scrape_publication_state
                WHERE id = TRUE
                FOR SHARE;
                """;
            await fence.ExecuteNonQueryAsync(ct);
        }
        var dataOwner = await CaptureOfflineOwnerAsync(connection, transaction, ct);
        using (timing.Measure("locked_identity"))
            await attestation.VerifyAsync(connection, transaction, ct);
        var boundary = await ReadOfflineBoundaryAsync(connection, transaction, ct);
        var newest = await SnapshotGenerationRetentionRepository.GetNewestCycleAsync(
            connection, transaction, OfflineCommandTimeoutSeconds, ct);
        var existing = newest is not null
            && newest.TriggerScrapeId == boundary.ScrapeId
            && newest.TriggerPublicationId == boundary.PublicationId
                ? newest
                : null;
        if (existing is not null && !IsAcceptedOfflineExistingCycle(existing))
            throw new SnapshotGenerationRetentionOfflineRefusal("current_cycle_not_accepted");

        var safePoint = new SnapshotGenerationRetentionSafePoint(
            boundary.ScrapeId,
            boundary.PublicationId,
            boundary.ObservedAtUtc,
            existing?.SafePointKind
                ?? SnapshotGenerationRetentionContract.OperatorOfflineSafePoint);
        var state = await LoadSafePointStateAsync(
            connection, transaction, safePoint, configuredResumeScrapeId: 0,
            OfflineCommandTimeoutSeconds, ct);
        if (state.DeferralBlockers.Count > 0)
            throw new SnapshotGenerationRetentionOfflineRefusal(
                state.DeferralBlockers[0].Code);

        SnapshotGenerationRetentionPersistRequest persistence;
        await transaction.SaveAsync("offline_retention_observation", ct);
        try
        {
            using (timing.Measure("observation"))
            {
                var observation = await ReadObservationAsync(
                    connection, transaction, state, configuredResumeScrapeId: 0,
                    OfflineCommandTimeoutSeconds, ct, timing);
                persistence = BuildPersistRequest(safePoint, observation);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (IsOfflineBudgetFailure(exception, external, ct))
                throw;
            await transaction.RollbackAsync("offline_retention_observation", ct);
            if (existing is not null)
                throw new SnapshotGenerationRetentionOfflineRefusal(
                    "current_cycle_revalidation_failed");
            var reason = exception is PostgresException postgres
                ? $"Offline retention observation failed (SQLSTATE {postgres.SqlState})."
                : $"Offline retention observation failed ({exception.GetType().Name}).";
            persistence = BuildFailureRequest(
                safePoint, new InvalidOperationException(reason));
        }

        await CaptureOfflineOwnerAsync(fenceConnection, fenceTransaction, ct);
        using (timing.Measure("final_identity"))
            await attestation.VerifyAsync(connection, transaction, ct);
        var finalBoundary = await ReadOfflineBoundaryAsync(connection, transaction, ct);
        if (finalBoundary.WorkerInstanceId != boundary.WorkerInstanceId
            || finalBoundary.ScrapeId != boundary.ScrapeId
            || finalBoundary.PublicationId != boundary.PublicationId)
        {
            throw new SnapshotGenerationRetentionOfflineRefusal("offline_boundary_changed");
        }
        if (existing is not null)
        {
            if (persistence.Status != SnapshotGenerationRetentionCycleStatus.Observed
                || persistence.CandidateIdentityHash != existing.CandidateIdentityHash
                || persistence.ObservationHash != existing.ObservationHash)
            {
                throw new SnapshotGenerationRetentionOfflineRefusal(
                    "current_cycle_observation_changed");
            }
            var result = new SnapshotGenerationRetentionOfflineResult(
                BuildResult(existing, SnapshotGenerationRetentionPlanDisposition.Existing,
                    "the current accepted cycle was revalidated; no new cycle was written",
                    Retryable: false),
                existing,
                finalBoundary)
            {
                Completion = new(databaseSignature, [fenceOwner, dataOwner]),
            };
            operation.CommittedCandidate = result;
            using (timing.Measure("commit"))
            {
                await transaction.CommitAsync(ct);
                await fenceTransaction.CommitAsync(ct);
            }
            if (OfflinePostCommitCleanupTestHook is not null)
                await OfflinePostCommitCleanupTestHook();
            return result;
        }

        (SnapshotGenerationRetentionCycle Cycle, bool Inserted) persisted;
        using (timing.Measure("persistence"))
            persisted = await _repository.PersistInTransactionAsync(
                connection, transaction, persistence, OfflineCommandTimeoutSeconds, ct);
        finalBoundary = await ReadOfflineBoundaryAsync(connection, transaction, ct);
        await CaptureOfflineOwnerAsync(fenceConnection, fenceTransaction, ct);
        var completed = new SnapshotGenerationRetentionOfflineResult(
            CompletePersistedObservation(persisted, persistence),
            persisted.Cycle,
            finalBoundary)
        {
            Completion = new(databaseSignature, [fenceOwner, dataOwner]),
        };
        operation.CommittedCandidate = completed;
        using (timing.Measure("commit"))
        {
            await transaction.CommitAsync(ct);
            await fenceTransaction.CommitAsync(ct);
        }
        if (OfflinePostCommitCleanupTestHook is not null)
            await OfflinePostCommitCleanupTestHook();
        return completed;
    }

    private static bool IsAcceptedOfflineExistingCycle(
        SnapshotGenerationRetentionCycle cycle) =>
        cycle.PlannerVersion == SnapshotGenerationRetentionContract.PlannerVersion
        && cycle.ConfigVersion == SnapshotGenerationRetentionContract.ConfigVersion
        && cycle.SafePointKind is SnapshotGenerationRetentionContract.TerminalWorkerSafePoint
            or SnapshotGenerationRetentionContract.OperatorOfflineSafePoint
        && cycle.Status == SnapshotGenerationRetentionCycleStatus.Observed
        && cycle.OracleAgreement
        && cycle.BlockedCount == 0
        && cycle.GlobalBlockersJson == "[]"
        && cycle.PlannerChildSetJson == cycle.OracleChildSetJson
        && cycle.PlannerLiveSetJson == cycle.OracleLiveSetJson
        && cycle.PlannerCandidateSetJson == cycle.OracleCandidateSetJson;

    private static async Task<SnapshotGenerationRetentionOfflineBoundary>
        ReadOfflineBoundaryAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = OfflineCommandTimeoutSeconds;
        command.CommandText = """
            WITH newest AS (
                SELECT id, status, completed_at
                FROM public.scrape_log
                ORDER BY id DESC LIMIT 1
            )
            SELECT pg_catalog.clock_timestamp(),
                newest.id::BIGINT, newest.status, newest.completed_at,
                EXISTS (SELECT 1 FROM public.scrape_log WHERE status = 'running'),
                state.published_scrape_id::BIGINT,
                state.current_publication_id, state.published_at,
                state.public_reads_frozen,
                state.working_publication_id IS NULL
                    AND state.publication_commit_intent_started_at IS NULL
                    AND state.publication_commit_intent_heartbeat_at IS NULL
                    AND state.publication_commit_intent_owner IS NULL
                    AND state.max_score_mutation_gate_token IS NULL
                    AND state.max_score_mutation_gate_publication_id IS NULL
                    AND state.max_score_mutation_gate_backend_pid IS NULL
                    AND state.max_score_mutation_gate_backend_start IS NULL
                    AND state.max_score_mutation_gate_acquired_at IS NULL,
                generation.status, generation.scrape_id,
                worker.status, worker.mode, worker.instance_id, worker.started_at,
                worker.last_status_change_at, worker.last_heartbeat_at,
                worker.current_operation_json IS NULL, worker.updated_at,
                state.improvement_notifications_status,
                state.improvement_notifications_scrape_id::BIGINT,
                state.improvement_notifications_completed_at,
                state.improvement_notifications_projection_ready,
                state.improvement_notifications_projection_scrape_id::BIGINT
            FROM public.scrape_publication_state state
            LEFT JOIN newest ON TRUE
            LEFT JOIN public.publication_generations generation
                ON generation.publication_id = state.current_publication_id
            LEFT JOIN public.service_worker_status worker
                ON worker.worker_key = 'scraper'
            WHERE state.id = TRUE
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new SnapshotGenerationRetentionOfflineRefusal("publication_state_missing");
        var now = reader.GetDateTime(0);
        if (reader.IsDBNull(1) || reader.IsDBNull(2)
            || reader.GetString(2) != "completed" || reader.IsDBNull(3))
        {
            throw new SnapshotGenerationRetentionOfflineRefusal(
                "newest_scrape_not_completed");
        }
        if (reader.GetBoolean(4))
            throw new SnapshotGenerationRetentionOfflineRefusal("scrape_running");
        var scrapeId = reader.GetInt64(1);
        var completedAt = reader.GetDateTime(3);
        if (reader.IsDBNull(5) || reader.GetInt64(5) != scrapeId
            || reader.IsDBNull(6) || reader.GetInt64(6) <= 0
            || reader.IsDBNull(7) || reader.IsDBNull(10)
            || reader.GetString(10) != "current"
            || reader.IsDBNull(11) || reader.GetInt64(11) != scrapeId)
        {
            throw new SnapshotGenerationRetentionOfflineRefusal("publication_binding_mismatch");
        }
        if (reader.GetBoolean(8))
            throw new SnapshotGenerationRetentionOfflineRefusal("public_reads_frozen");
        if (!reader.GetBoolean(9))
            throw new SnapshotGenerationRetentionOfflineRefusal("publication_mutation_pending");
        if (reader.IsDBNull(12) || reader.GetString(12) != "offline"
            || reader.IsDBNull(13) || reader.GetString(13) != "scraper")
        {
            throw new SnapshotGenerationRetentionOfflineRefusal("scraper_not_offline");
        }
        if (!reader.GetBoolean(18))
            throw new SnapshotGenerationRetentionOfflineRefusal("worker_operation_present");
        if (reader.IsDBNull(14) || string.IsNullOrWhiteSpace(reader.GetString(14))
            || reader.IsDBNull(15) || reader.IsDBNull(16)
            || reader.IsDBNull(17) || reader.IsDBNull(19))
        {
            throw new SnapshotGenerationRetentionOfflineRefusal("offline_worker_identity_incomplete");
        }
        var startedAt = reader.GetDateTime(15);
        var stoppedAt = reader.GetDateTime(16);
        var heartbeatAt = reader.GetDateTime(17);
        var updatedAt = reader.GetDateTime(19);
        var publishedAt = reader.GetDateTime(7);
        if (startedAt > stoppedAt || stoppedAt > heartbeatAt
            || heartbeatAt > updatedAt || updatedAt > now
            || completedAt > publishedAt || publishedAt > stoppedAt
            || now - stoppedAt > TimeSpan.FromSeconds(OfflineBoundaryMaximumAgeSeconds)
            || heartbeatAt - stoppedAt > TimeSpan.FromMinutes(2))
        {
            throw new SnapshotGenerationRetentionOfflineRefusal("offline_worker_boundary_stale_or_inconsistent");
        }
        var notificationsCompleted = !reader.IsDBNull(20)
            && reader.GetString(20) == "completed"
            && !reader.IsDBNull(21) && reader.GetInt64(21) == scrapeId
            && !reader.IsDBNull(22) && reader.GetDateTime(22) <= stoppedAt
            && reader.GetBoolean(23)
            && !reader.IsDBNull(24) && reader.GetInt64(24) == scrapeId;
        var notificationsDisabled = !reader.IsDBNull(20)
            && reader.GetString(20) == "disabled"
            && reader.IsDBNull(21) && reader.IsDBNull(22)
            && !reader.GetBoolean(23) && reader.IsDBNull(24);
        if (!notificationsCompleted && !notificationsDisabled)
            throw new SnapshotGenerationRetentionOfflineRefusal("notifications_not_terminal");
        var publicationId = reader.GetInt64(6);
        var instance = reader.GetString(14);
        await reader.DisposeAsync();
        var configuration = await SnapshotGenerationRetentionWorkerConfigurationStore
            .ReadCurrentAsync(connection, transaction, ct);
        if (configuration is null || configuration.WorkerInstanceId != instance)
            throw new SnapshotGenerationRetentionOfflineRefusal("report_only_configuration_missing");
        if (!configuration.ReportOnlyEnabled)
            throw new SnapshotGenerationRetentionOfflineRefusal("report_only_configuration_disabled");
        if (!configuration.IsAuthentic
            || configuration.CanonicalCycleIdentityVersion != SnapshotGenerationRetentionContract.CanonicalCycleIdentityVersion
            || configuration.PlannerVersion != SnapshotGenerationRetentionContract.PlannerVersion
            || configuration.ConfigVersion != SnapshotGenerationRetentionContract.ConfigVersion
            || configuration.ConfiguredAtUtc < startedAt
            || configuration.ConfiguredAtUtc > stoppedAt)
            throw new SnapshotGenerationRetentionOfflineRefusal("report_only_configuration_incompatible");
        return new(scrapeId, publicationId, instance,
            startedAt, stoppedAt, heartbeatAt, now, configuration.ConfigurationSha256);
    }
}
