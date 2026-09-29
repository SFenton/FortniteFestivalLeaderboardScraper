using System.Data;
using Npgsql;

namespace FSTService.Persistence;

public sealed partial class MetaDatabase
{
    internal const string FrozenAcquisitionAbandonmentPhase = "operator_acquisition_abandoned";
    internal Action? FrozenAcquisitionAbandonmentBeforeFenceTestHook { get; set; }
    internal Action? FrozenAcquisitionAbandonmentBeforeCommitTestHook { get; set; }

    public FrozenAcquisitionAbandonmentReadiness GetFrozenAcquisitionAbandonmentReadiness(
        FrozenAcquisitionAbandonmentCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            using var connection = _ds.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
            ConfigureNormalizationTransaction(connection, transaction, readOnly: true);
            var readiness = ReadFrozenAcquisitionAbandonmentReadiness(
                connection, transaction, command, exclusive: false);
            transaction.Commit();
            return readiness;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _log.LogWarning(ex, "Frozen-acquisition abandonment readiness could not be proven.");
            return new(false, "Frozen-acquisition abandonment readiness could not be proven.", command);
        }
    }

    public FrozenAcquisitionAbandonmentExecutionResult ExecuteFrozenAcquisitionAbandonment(
        FrozenAcquisitionAbandonmentCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var before = GetFrozenAcquisitionAbandonmentReadiness(command);
        if (!before.CanExecute) return new(false, before, null, null, before.BlockingReason);
        FrozenAcquisitionAbandonmentBeforeFenceTestHook?.Invoke();
        FrozenAcquisitionAbandonmentReadiness? mutation = null;
        try
        {
            using var connection = _ds.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
            ConfigureNormalizationTransaction(connection, transaction, readOnly: false);
            mutation = ReadFrozenAcquisitionAbandonmentReadiness(
                connection, transaction, command, exclusive: true);
            if (!mutation.CanExecute)
                return new(false, before, mutation, null, mutation.BlockingReason);
            var identity = command.Identity;
            var now = DateTime.UtcNow;
            using (var phase = connection.CreateCommand())
            {
                phase.Transaction = transaction;
                phase.CommandText = """
                    UPDATE scrape_phase_attempts
                    SET status = 'failed', completed_at = @now,
                        heartbeat_at = @now, last_progress_at = @now,
                        error_message = @message
                    WHERE scrape_id = @scrapeId AND phase_id = @phaseId
                      AND attempt = @attempt AND status = 'running'
                      AND completed_at IS NULL AND worker_instance_id = @worker
                    """;
                phase.Parameters.AddWithValue("now", now);
                phase.Parameters.AddWithValue("message", command.FailureMessage);
                phase.Parameters.AddWithValue("scrapeId", identity.ScrapeId);
                phase.Parameters.AddWithValue("phaseId", identity.ExpectedPhaseId);
                phase.Parameters.AddWithValue("attempt", identity.ExpectedAttempt);
                phase.Parameters.AddWithValue("worker", command.ExpectedAttemptWorkerInstanceId);
                if (phase.ExecuteNonQuery() != 1)
                    return new(false, before, mutation, null, "The pinned running phase changed.");
            }
            // No artifact or staging deletion: preserve the entire failed candidate for diagnosis.
            RecordFailedScrapeState(connection, transaction, identity.ScrapeId,
                FrozenAcquisitionAbandonmentPhase, command.FailureMessage);
            using (var freeze = connection.CreateCommand())
            {
                freeze.Transaction = transaction;
                freeze.CommandText = """
                    UPDATE scrape_publication_state
                    SET public_reads_frozen = FALSE, public_reads_frozen_at = NULL,
                        public_reads_frozen_scrape_id = NULL,
                        public_reads_frozen_reason = NULL, updated_at = @now
                    WHERE id = TRUE AND published_scrape_id = @published
                      AND current_publication_id = @current AND previous_publication_id = @previous
                      AND working_publication_id IS NULL AND public_reads_frozen
                      AND public_reads_frozen_scrape_id = @published
                      AND public_reads_frozen_reason = 'scrape'
                    """;
                freeze.Parameters.AddWithValue("now", now);
                freeze.Parameters.AddWithValue("published", identity.ExpectedPublishedScrapeId);
                freeze.Parameters.AddWithValue("current", identity.ExpectedCurrentPublicationId);
                freeze.Parameters.AddWithValue("previous", identity.ExpectedPreviousPublicationId);
                if (freeze.ExecuteNonQuery() != 1)
                    return new(false, before, mutation, null, "The pinned acquisition freeze changed.");
            }
            FrozenAcquisitionAbandonmentBeforeCommitTestHook?.Invoke();
            var terminal = ReadActiveScrapeFailureIsolationReadiness(
                connection, transaction, identity.ScrapeId, identity.ExpectedPublishedScrapeId);
            var raw = ReadInterruptedAcquisitionNormalizationState(
                connection, transaction, identity, lockRows: true);
            if (!FrozenAcquisitionAbandonmentReachedTerminalState(raw, command, terminal))
                return new(false, before, mutation, terminal, "The exact terminal state was not proven under the fence.");
            transaction.Commit();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _log.LogWarning(ex, "Frozen-acquisition abandonment transaction was rejected.");
            return new(false, before, mutation, null, "Frozen-acquisition abandonment transaction was rejected.");
        }
        // Independent read-only proof after commit. Retries do not mutate an already terminal candidate.
        using var afterConnection = _ds.OpenConnection();
        using var afterTransaction = afterConnection.BeginTransaction(IsolationLevel.RepeatableRead);
        ConfigureNormalizationTransaction(afterConnection, afterTransaction, readOnly: true);
        if (!TryAcquirePublicationAdvisoryLock(afterConnection, afterTransaction, shared: true))
            return new(false, before, mutation, null, "The final publication proof fence is busy.");
        var after = ReadActiveScrapeFailureIsolationReadiness(afterConnection, afterTransaction,
            command.Identity.ScrapeId, command.Identity.ExpectedPublishedScrapeId);
        var finalRaw = ReadInterruptedAcquisitionNormalizationState(
            afterConnection, afterTransaction, command.Identity, lockRows: false);
        var succeeded = FrozenAcquisitionAbandonmentReachedTerminalState(finalRaw, command, after);
        afterTransaction.Commit();
        return new(succeeded, before, mutation, after,
            succeeded ? null : "The final exact terminal state could not be proven.");
    }

    private FrozenAcquisitionAbandonmentReadiness ReadFrozenAcquisitionAbandonmentReadiness(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        FrozenAcquisitionAbandonmentCommand command, bool exclusive)
    {
        if (!HasInterruptedAcquisitionNormalizationSchema(connection, transaction))
            return new(false, "Required acquisition recovery schema is missing.", command);
        if (!TryAcquirePublicationAdvisoryLock(connection, transaction, shared: !exclusive))
            return new(false, "The publication fence is busy.", command);
        var raw = ReadInterruptedAcquisitionNormalizationState(
            connection, transaction, command.Identity, lockRows: exclusive);
        var error = GetAcquisitionHandoffBlockingReason(raw, command.Identity, frozenAcquisition: true)
            ?? GetFrozenAcquisitionPhaseAndWorkerBlocker(raw, command);
        using var mutationGate = connection.CreateCommand();
        mutationGate.Transaction = transaction;
        mutationGate.CommandText = "SELECT max_score_mutation_gate_token IS NULL FROM scrape_publication_state WHERE id = TRUE";
        if (mutationGate.ExecuteScalar() is not true)
            error ??= "The max-score mutation gate is occupied or missing.";
        return new(error is null, error, command);
    }

    private static string? GetFrozenAcquisitionPhaseAndWorkerBlocker(
        InterruptedAcquisitionNormalizationState raw, FrozenAcquisitionAbandonmentCommand command)
    {
        var identity = command.Identity;
        if (raw.Attempts.Count != 1 || raw.GlobalRunningPhaseAttemptCount != 1)
            return "Exactly one candidate phase and one globally running phase are required.";
        var attempt = raw.Attempts[0];
        if (attempt.PhaseId != identity.ExpectedPhaseId || attempt.Attempt != identity.ExpectedAttempt
            || attempt.WorkerInstanceId != command.ExpectedAttemptWorkerInstanceId
            || attempt.Status != "running" || attempt.CompletedAtUtc is not null
            || attempt.ErrorMessage is not null || attempt.WarningMessage is not null
            || attempt.OperationId != "scrape.update" || attempt.PlanVersion != "fst.scrape-plan.v2"
            || attempt.PhaseOrdinal != 100 || attempt.StartedAtUtc > attempt.LastProgressAtUtc
            || attempt.StartedAtUtc > attempt.HeartbeatAtUtc)
            return "The pinned running acquisition phase identity or provenance is not exact.";
        var worker = raw.Worker;
        if (worker is null || worker.Status != "offline" || worker.Mode != "scraper"
            || worker.InstanceId != identity.ExpectedWorkerInstanceId
            || worker.CurrentOperationJson is not null || worker.StartedAtUtc is null
            || worker.StartedAtUtc > identity.ExpectedWorkerFreshnessUtc
            || !SamePostgresMicrosecond(worker.UpdatedAtUtc, identity.ExpectedWorkerFreshnessUtc)
            || !SamePostgresMicrosecond(worker.LastHeartbeatAtUtc, identity.ExpectedWorkerFreshnessUtc)
            || !SamePostgresMicrosecond(worker.LastStatusChangeAtUtc, identity.ExpectedWorkerFreshnessUtc))
            return "The pinned worker is not exactly offline with empty operation and unchanged freshness.";
        if (attempt.HeartbeatAtUtc > identity.ExpectedWorkerFreshnessUtc
            || attempt.LastProgressAtUtc > identity.ExpectedWorkerFreshnessUtc
            || (attempt.WorkerInstanceId != worker.InstanceId && attempt.HeartbeatAtUtc > worker.StartedAtUtc))
            return "The acquisition phase progressed after its worker stopped or was replaced.";
        return null;
    }

    private static bool FrozenAcquisitionAbandonmentReachedTerminalState(
        InterruptedAcquisitionNormalizationState raw, FrozenAcquisitionAbandonmentCommand command,
        ActiveScrapeFailureIsolationReadiness terminal)
    {
        var identity = command.Identity;
        return terminal.CanExecute && terminal.PublicationIsolationComplete
            && terminal.RunningPhaseAttemptCount == 0 && !terminal.WorkerCurrentOperationPresent
            && raw.Publication is { PublicReadsFrozen: false, WorkingPublicationId: null,
                FrozenScrapeId: null, FreezeReason: null, PublicReadsFrozenAtUtc: null,
                CommitIntentStartedAtUtc: null, CommitIntentHeartbeatAtUtc: null, CommitIntentOwner: null }
            && raw.Publication.PublishedScrapeId == identity.ExpectedPublishedScrapeId
            && raw.Publication.CurrentPublicationId == identity.ExpectedCurrentPublicationId
            && raw.Publication.PreviousPublicationId == identity.ExpectedPreviousPublicationId
            && raw.CandidateScrape is { Status: "failed", AcquisitionCompletedAtUtc: null,
                AcquisitionCheckpointPayloadPresent: false, FailurePhase: FrozenAcquisitionAbandonmentPhase }
            && raw.CandidateScrape.FailureMessage == command.FailureMessage
            && raw.OtherRunningScrapeCount == 0 && raw.NewerScrapeCount == 0
            && raw.GlobalRunningPhaseAttemptCount == 0 && raw.Attempts.Count == 1
            && raw.Attempts[0] is { Status: "failed", CompletedAtUtc: not null }
            && raw.Attempts[0].PhaseId == identity.ExpectedPhaseId
            && raw.Attempts[0].Attempt == identity.ExpectedAttempt
            && raw.Attempts[0].WorkerInstanceId == command.ExpectedAttemptWorkerInstanceId
            && raw.Attempts[0].ErrorMessage == command.FailureMessage
            && raw.Worker is { Status: "offline", CurrentOperationJson: null }
            && raw.Worker.InstanceId == identity.ExpectedWorkerInstanceId
            && SamePostgresMicrosecond(raw.Worker.UpdatedAtUtc, identity.ExpectedWorkerFreshnessUtc)
            && SamePostgresMicrosecond(raw.Worker.LastHeartbeatAtUtc, identity.ExpectedWorkerFreshnessUtc)
            && SamePostgresMicrosecond(raw.Worker.LastStatusChangeAtUtc, identity.ExpectedWorkerFreshnessUtc);
    }
}
