using System.Data;
using Npgsql;

namespace FSTService.Persistence;

public sealed partial class MetaDatabase
{
    internal const string StartupFrozenAcquisitionAbandonmentPhase = "startup_acquisition_abandoned";
    internal Action? StartupFrozenAcquisitionRecoveryBeforeFenceTestHook { get; set; }
    internal Action? StartupFrozenAcquisitionRecoveryBeforeCommitTestHook { get; set; }

    /// <summary>
    /// In-process counterpart of <c>--frozen-acquisition-abandonment</c> for a scraper worker
    /// that starts while a previous worker's acquisition still holds the <c>scrape</c> freeze.
    /// It reuses the operator command's exact publication/generation/quiescence admission under
    /// the exclusive publication fence, but replaces the pinned offline-worker rule with proof
    /// that every running phase belongs to another instance and stopped before this worker
    /// started. Published data, staging and artifacts are preserved.
    /// </summary>
    public StartupFrozenAcquisitionRecoveryResult RecoverInterruptedFrozenAcquisitionOnStartup(
        string currentWorkerInstanceId,
        DateTime currentWorkerStartedAtUtc,
        TimeSpan minimumStaleness)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentWorkerInstanceId);
        if (minimumStaleness < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumStaleness));
        var startedAtUtc = currentWorkerStartedAtUtc.Kind == DateTimeKind.Utc
            ? currentWorkerStartedAtUtc
            : currentWorkerStartedAtUtc.ToUniversalTime();

        try
        {
            using (var probeConnection = _ds.OpenConnection())
            {
                using var probe = probeConnection.CreateCommand();
                probe.CommandText = """
                    SELECT EXISTS (
                        SELECT 1 FROM scrape_publication_state
                        WHERE id = TRUE AND public_reads_frozen
                          AND public_reads_frozen_reason = 'scrape')
                    """;
                if (probe.ExecuteScalar() is not true)
                    return StartupFrozenAcquisitionRecoveryResult.NotApplicable;
            }

            StartupFrozenAcquisitionRecoveryBeforeFenceTestHook?.Invoke();
            using var connection = _ds.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
            ConfigureNormalizationTransaction(connection, transaction, readOnly: false);
            if (!TryAcquirePublicationAdvisoryLock(connection, transaction, shared: false))
                return StartupFrozenAcquisitionRecoveryResult.Blocked("The publication fence is busy.");
            if (!HasInterruptedAcquisitionNormalizationSchema(connection, transaction))
                return StartupFrozenAcquisitionRecoveryResult.Blocked("Required acquisition recovery schema is missing.");

            var publication = ReadNormalizationPublication(connection, transaction, lockRows: true);
            if (publication is not { PublicReadsFrozen: true, FreezeReason: "scrape" })
                return StartupFrozenAcquisitionRecoveryResult.NotApplicable;

            var now = DateTime.UtcNow;
            var staleBefore = now - minimumStaleness;
            if (publication.PublishedScrapeId is not long publishedScrapeId
                || publication.FrozenScrapeId != publishedScrapeId
                || publication.PublicReadsFrozenAtUtc is not DateTime frozenAtUtc)
            {
                return StartupFrozenAcquisitionRecoveryResult.Blocked(
                    "The scrape freeze is not pinned to the published scrape.");
            }
            if (frozenAtUtc > startedAtUtc || frozenAtUtc > staleBefore)
            {
                return StartupFrozenAcquisitionRecoveryResult.Blocked(
                    "The scrape freeze is not older than this worker and the minimum staleness window.",
                    publishedScrapeId: publishedScrapeId);
            }
            if (publication.CommitIntentStartedAtUtc is not null
                || publication.CommitIntentHeartbeatAtUtc is not null
                || publication.CommitIntentOwner is not null)
            {
                return StartupFrozenAcquisitionRecoveryResult.Blocked(
                    "Publication commit-intent state is not empty.",
                    publishedScrapeId: publishedScrapeId);
            }
            if (!MaxScoreMutationGateIsFree(connection, transaction))
            {
                return StartupFrozenAcquisitionRecoveryResult.Blocked(
                    "The max-score mutation gate is occupied or missing.",
                    publishedScrapeId: publishedScrapeId);
            }

            return publication.WorkingPublicationId is long workingPublicationId
                ? AbandonInterruptedFrozenAcquisition(
                    connection, transaction, publication, publishedScrapeId, workingPublicationId,
                    currentWorkerInstanceId, startedAtUtc, staleBefore, now)
                : ReleasePreAllocationScrapeFreeze(
                    connection, transaction, publication, publishedScrapeId, now);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _log.LogWarning(ex, "Startup frozen-acquisition recovery transaction was rejected.");
            return StartupFrozenAcquisitionRecoveryResult.Blocked(
                "Startup frozen-acquisition recovery transaction was rejected.");
        }
    }

    private StartupFrozenAcquisitionRecoveryResult ReleasePreAllocationScrapeFreeze(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicationState publication,
        long publishedScrapeId,
        DateTime now)
    {
        // The worker froze reads but stopped before StartScrapeRun allocated a candidate.
        using (var running = connection.CreateCommand())
        {
            running.Transaction = transaction;
            running.CommandText = """
                SELECT
                    (SELECT COUNT(*)::INTEGER FROM scrape_log WHERE status = 'running'),
                    (SELECT COUNT(*)::INTEGER FROM scrape_phase_attempts WHERE status = 'running')
                """;
            using var reader = running.ExecuteReader();
            reader.Read();
            if (reader.GetInt32(0) != 0 || reader.GetInt32(1) != 0)
            {
                return StartupFrozenAcquisitionRecoveryResult.Blocked(
                    "A running scrape or phase exists without a working publication.",
                    publishedScrapeId: publishedScrapeId);
            }
        }

        if (!ReleaseExactScrapeFreeze(connection, transaction, publication, publishedScrapeId, now))
        {
            return StartupFrozenAcquisitionRecoveryResult.Blocked(
                "The pinned acquisition freeze changed.", publishedScrapeId: publishedScrapeId);
        }
        StartupFrozenAcquisitionRecoveryBeforeCommitTestHook?.Invoke();
        transaction.Commit();
        _log.LogWarning(
            "Released an orphaned scrape freeze on published scrape {PublishedScrapeId}; no candidate had been allocated.",
            publishedScrapeId);
        return new(StartupFrozenAcquisitionRecoveryOutcome.FreezeReleased, null, publishedScrapeId, null);
    }

    private StartupFrozenAcquisitionRecoveryResult AbandonInterruptedFrozenAcquisition(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicationState publication,
        long publishedScrapeId,
        long workingPublicationId,
        string currentWorkerInstanceId,
        DateTime startedAtUtc,
        DateTime staleBefore,
        DateTime now)
    {
        long scrapeId;
        using (var candidate = connection.CreateCommand())
        {
            candidate.Transaction = transaction;
            candidate.CommandText =
                "SELECT scrape_id FROM publication_generations WHERE publication_id = @working";
            candidate.Parameters.AddWithValue("working", workingPublicationId);
            var candidateScrapeId = candidate.ExecuteScalar();
            if (candidateScrapeId is null or DBNull)
            {
                return StartupFrozenAcquisitionRecoveryResult.Blocked(
                    "The working publication has no candidate scrape.", publishedScrapeId: publishedScrapeId);
            }
            scrapeId = Convert.ToInt64(candidateScrapeId, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (publication.CurrentPublicationId is not long currentPublicationId
            || publication.PreviousPublicationId is not long previousPublicationId)
        {
            return StartupFrozenAcquisitionRecoveryResult.Blocked(
                "Current and previous publications are required.", scrapeId, publishedScrapeId);
        }

        // Worker fields are unused by the shared admission; the in-process rule below replaces them.
        var identity = new InterruptedAcquisitionNormalizationCommand(
            Execute: true,
            CheckOnly: false,
            ScrapeId: scrapeId,
            ExpectedPublishedScrapeId: publishedScrapeId,
            ExpectedCurrentPublicationId: currentPublicationId,
            ExpectedPreviousPublicationId: previousPublicationId,
            ExpectedWorkingPublicationId: workingPublicationId,
            ExpectedWorkerInstanceId: currentWorkerInstanceId,
            ExpectedWorkerFreshnessUtc: startedAtUtc,
            ExpectedPhaseId: InterruptedAcquisitionNormalizationCommand.AcquisitionPhaseId,
            ExpectedAttempt: 1);
        var raw = ReadInterruptedAcquisitionNormalizationState(
            connection, transaction, identity, lockRows: true);
        var error = GetAcquisitionHandoffBlockingReason(raw, identity, frozenAcquisition: true)
            ?? GetStartupAcquisitionPhaseAndWorkerBlocker(
                raw, currentWorkerInstanceId, startedAtUtc, staleBefore);
        if (error is not null)
            return StartupFrozenAcquisitionRecoveryResult.Blocked(error, scrapeId, publishedScrapeId);

        var message =
            $"Scraper worker {currentWorkerInstanceId} started at {startedAtUtc:O} and found acquisition "
            + $"scrape {scrapeId} interrupted by a previous worker; abandoned at startup.";
        var runningAttempts = raw.Attempts.Count(attempt => attempt.Status == "running");
        using (var phase = connection.CreateCommand())
        {
            phase.Transaction = transaction;
            phase.CommandText = """
                UPDATE scrape_phase_attempts
                SET status = 'failed', completed_at = @now,
                    heartbeat_at = @now, last_progress_at = @now,
                    error_message = @message
                WHERE scrape_id = @scrapeId AND status = 'running'
                  AND completed_at IS NULL AND worker_instance_id <> @worker
                """;
            phase.Parameters.AddWithValue("now", now);
            phase.Parameters.AddWithValue("message", message);
            phase.Parameters.AddWithValue("scrapeId", scrapeId);
            phase.Parameters.AddWithValue("worker", currentWorkerInstanceId);
            if (phase.ExecuteNonQuery() != runningAttempts)
            {
                return StartupFrozenAcquisitionRecoveryResult.Blocked(
                    "The interrupted running phases changed.", scrapeId, publishedScrapeId);
            }
        }
        // No artifact or staging deletion: preserve the entire failed candidate for diagnosis.
        RecordFailedScrapeState(connection, transaction, scrapeId,
            StartupFrozenAcquisitionAbandonmentPhase, message);
        if (!ReleaseExactScrapeFreeze(connection, transaction, publication, publishedScrapeId, now))
        {
            return StartupFrozenAcquisitionRecoveryResult.Blocked(
                "The pinned acquisition freeze changed.", scrapeId, publishedScrapeId);
        }
        StartupFrozenAcquisitionRecoveryBeforeCommitTestHook?.Invoke();
        var terminal = ReadActiveScrapeFailureIsolationReadiness(
            connection, transaction, scrapeId, publishedScrapeId);
        var terminalRaw = ReadInterruptedAcquisitionNormalizationState(
            connection, transaction, identity, lockRows: true);
        if (!StartupAbandonmentReachedTerminalState(terminalRaw, identity, terminal, message))
        {
            return StartupFrozenAcquisitionRecoveryResult.Blocked(
                "The exact terminal state was not proven under the fence.", scrapeId, publishedScrapeId);
        }
        transaction.Commit();
        _log.LogWarning(
            "Abandoned interrupted frozen acquisition scrape {ScrapeId} at worker startup; published scrape {PublishedScrapeId} remains current and public reads were released.",
            scrapeId, publishedScrapeId);
        return new(StartupFrozenAcquisitionRecoveryOutcome.AcquisitionAbandoned,
            scrapeId, publishedScrapeId, null);
    }

    private static string? GetStartupAcquisitionPhaseAndWorkerBlocker(
        InterruptedAcquisitionNormalizationState raw,
        string currentWorkerInstanceId,
        DateTime startedAtUtc,
        DateTime staleBefore)
    {
        var running = raw.Attempts.Where(attempt => attempt.Status == "running").ToList();
        if (raw.GlobalRunningPhaseAttemptCount != running.Count)
            return "A running phase attempt outside the interrupted acquisition is present.";
        foreach (var attempt in running)
        {
            if (attempt.CompletedAtUtc is not null)
                return "A running acquisition phase contains terminal state.";
            if (string.Equals(attempt.WorkerInstanceId, currentWorkerInstanceId, StringComparison.Ordinal))
                return "A running acquisition phase belongs to this worker instance.";
            if (attempt.HeartbeatAtUtc > startedAtUtc || attempt.LastProgressAtUtc > startedAtUtc
                || attempt.HeartbeatAtUtc > staleBefore || attempt.LastProgressAtUtc > staleBefore)
                return "A running acquisition phase progressed after this worker started or within the minimum staleness window.";
        }
        var worker = raw.Worker;
        if (worker is not null
            && !string.Equals(worker.InstanceId, currentWorkerInstanceId, StringComparison.Ordinal)
            && !string.Equals(worker.Status, "offline", StringComparison.Ordinal))
            return "The scraper worker status row belongs to another live instance.";
        return null;
    }

    private static bool StartupAbandonmentReachedTerminalState(
        InterruptedAcquisitionNormalizationState raw,
        InterruptedAcquisitionNormalizationCommand identity,
        ActiveScrapeFailureIsolationReadiness terminal,
        string message)
        => terminal.PublicationIsolationComplete
            && terminal.RunningPhaseAttemptCount == 0
            && raw.Publication is { PublicReadsFrozen: false, WorkingPublicationId: null,
                FrozenScrapeId: null, FreezeReason: null, PublicReadsFrozenAtUtc: null,
                CommitIntentStartedAtUtc: null, CommitIntentHeartbeatAtUtc: null, CommitIntentOwner: null }
            && raw.Publication.PublishedScrapeId == identity.ExpectedPublishedScrapeId
            && raw.Publication.CurrentPublicationId == identity.ExpectedCurrentPublicationId
            && raw.Publication.PreviousPublicationId == identity.ExpectedPreviousPublicationId
            && raw.CandidateScrape is { Status: "failed", AcquisitionCompletedAtUtc: null,
                AcquisitionCheckpointPayloadPresent: false, FailurePhase: StartupFrozenAcquisitionAbandonmentPhase }
            && raw.CandidateScrape.FailureMessage == message
            && raw.PublishedScrapeStatus == "completed"
            && raw.OtherRunningScrapeCount == 0 && raw.NewerScrapeCount == 0
            && raw.GlobalRunningPhaseAttemptCount == 0
            && raw.Attempts.All(attempt => attempt.Status != "running");

    private static bool ReleaseExactScrapeFreeze(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicationState publication,
        long publishedScrapeId,
        DateTime now)
    {
        using var freeze = connection.CreateCommand();
        freeze.Transaction = transaction;
        freeze.CommandText = """
            UPDATE scrape_publication_state
            SET public_reads_frozen = FALSE, public_reads_frozen_at = NULL,
                public_reads_frozen_scrape_id = NULL,
                public_reads_frozen_reason = NULL, updated_at = @now
            WHERE id = TRUE AND published_scrape_id = @published
              AND current_publication_id IS NOT DISTINCT FROM @current
              AND previous_publication_id IS NOT DISTINCT FROM @previous
              AND working_publication_id IS NULL AND public_reads_frozen
              AND public_reads_frozen_scrape_id = @published
              AND public_reads_frozen_reason = 'scrape'
              AND publication_commit_intent_started_at IS NULL
              AND publication_commit_intent_owner IS NULL
            """;
        freeze.Parameters.AddWithValue("now", now);
        freeze.Parameters.AddWithValue("published", publishedScrapeId);
        freeze.Parameters.Add(new NpgsqlParameter("current", NpgsqlTypes.NpgsqlDbType.Bigint)
            { Value = (object?)publication.CurrentPublicationId ?? DBNull.Value });
        freeze.Parameters.Add(new NpgsqlParameter("previous", NpgsqlTypes.NpgsqlDbType.Bigint)
            { Value = (object?)publication.PreviousPublicationId ?? DBNull.Value });
        return freeze.ExecuteNonQuery() == 1;
    }

    private static bool MaxScoreMutationGateIsFree(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        using var mutationGate = connection.CreateCommand();
        mutationGate.Transaction = transaction;
        mutationGate.CommandText =
            "SELECT max_score_mutation_gate_token IS NULL FROM scrape_publication_state WHERE id = TRUE";
        return mutationGate.ExecuteScalar() is true;
    }
}
