namespace FSTService.Persistence;

public sealed record ActiveScrapeFailureIsolationReadiness(
    long ScrapeId,
    long ExpectedPublishedScrapeId,
    long? PublishedScrapeId,
    string? CandidateStatus,
    bool PublicReadsFrozen,
    long? FrozenScrapeId,
    string? FreezeReason,
    long? WorkingPublicationId,
    long? CandidatePublicationId,
    string? CandidatePublicationStatus,
    int CandidatePublishedScopeRowCount,
    int ActiveWorkerQueryCount,
    int WaitingLockCount,
    int AdvisoryLockCount,
    bool MaintenanceActivityPresent,
    int RunningPhaseAttemptCount,
    int ForeignRunningPhaseAttemptCount,
    string? WorkerStatus,
    string? WorkerInstanceId,
    DateTime? WorkerUpdatedAtUtc,
    bool WorkerCurrentOperationPresent,
    int FailedAcquisitionPhaseAttemptCount,
    bool AcquisitionCheckpointPresent,
    string? CandidateFailurePhase)
{
    public bool PublicationMutationRequired =>
        CandidateStatusIsRunningOrFailed
        && PublicReadsFrozen
        && FrozenScrapeId == ExpectedPublishedScrapeId
        && string.Equals(
            FreezeReason,
            MetaDatabase.ActiveScrapeFailureIsolationFreezeReason,
            StringComparison.Ordinal)
        && WorkingPublicationId.HasValue
        && CandidatePublicationId == WorkingPublicationId
        && CandidatePublicationStatus is not null
        && !CandidatePublicationStatus.Equals(
            "current",
            StringComparison.OrdinalIgnoreCase)
        && !CandidatePublicationStatus.Equals(
            "retained",
            StringComparison.OrdinalIgnoreCase)
        && !CandidatePublicationStatus.Equals(
            "retired",
            StringComparison.OrdinalIgnoreCase);

    public bool PublicationIsolationComplete =>
        string.Equals(
            CandidateStatus,
            "failed",
            StringComparison.OrdinalIgnoreCase)
        && !PublicReadsFrozen
        && FrozenScrapeId is null
        && FreezeReason is null
        && WorkingPublicationId is null
        && CandidatePublicationId.HasValue
        && string.Equals(
            CandidatePublicationStatus,
            "failed",
            StringComparison.OrdinalIgnoreCase);

    public bool AcquisitionFailureMutationRequired =>
        string.Equals(
            CandidateStatus,
            "running",
            StringComparison.OrdinalIgnoreCase)
        && !PublicReadsFrozen
        && FrozenScrapeId is null
        && FreezeReason is null
        && WorkingPublicationId.HasValue
        && CandidatePublicationId == WorkingPublicationId
        && CandidatePublicationStatus is not null
        && !CandidatePublicationStatus.Equals(
            "current",
            StringComparison.OrdinalIgnoreCase)
        && !CandidatePublicationStatus.Equals(
            "retained",
            StringComparison.OrdinalIgnoreCase)
        && !CandidatePublicationStatus.Equals(
            "retired",
            StringComparison.OrdinalIgnoreCase)
        && RunningPhaseAttemptCount == 0
        && FailedAcquisitionPhaseAttemptCount > 0
        && !AcquisitionCheckpointPresent
        && string.Equals(
            WorkerStatus,
            "offline",
            StringComparison.OrdinalIgnoreCase)
        && !WorkerCurrentOperationPresent;

    public bool CanExecute =>
        PublishedScrapeId == ExpectedPublishedScrapeId
        && ScrapeId != ExpectedPublishedScrapeId
        && CandidateStatus is not null
        && CandidatePublicationId.HasValue
        && CandidatePublishedScopeRowCount == 0
        && ActiveWorkerQueryCount == 0
        && WaitingLockCount == 0
        && AdvisoryLockCount == 0
        && !MaintenanceActivityPresent
        && ForeignRunningPhaseAttemptCount == 0
        && !string.IsNullOrWhiteSpace(WorkerStatus)
        && !string.IsNullOrWhiteSpace(WorkerInstanceId)
        && WorkerUpdatedAtUtc.HasValue
        && (
            PublicationMutationRequired
            || PublicationIsolationComplete
            || AcquisitionFailureMutationRequired);

    public string? BlockingReason
    {
        get
        {
            if (PublishedScrapeId != ExpectedPublishedScrapeId)
            {
                return $"expected published scrape {ExpectedPublishedScrapeId}, found {PublishedScrapeId?.ToString() ?? "null"}";
            }
            if (ScrapeId == ExpectedPublishedScrapeId)
            {
                return "the active candidate must differ from the preserved published scrape";
            }
            if (CandidateStatus is null)
                return $"scrape {ScrapeId} does not exist";
            if (!CandidatePublicationId.HasValue)
                return $"scrape {ScrapeId} has no publication generation";
            if (CandidatePublishedScopeRowCount != 0)
            {
                return $"scrape {ScrapeId} owns {CandidatePublishedScopeRowCount} published-scope row(s)";
            }
            if (ActiveWorkerQueryCount != 0)
            {
                return $"worker still owns {ActiveWorkerQueryCount} active database quer{(ActiveWorkerQueryCount == 1 ? "y" : "ies")}";
            }
            if (WaitingLockCount != 0)
                return $"{WaitingLockCount} waiting database lock(s) remain";
            if (AdvisoryLockCount != 0)
                return $"{AdvisoryLockCount} advisory database lock(s) remain";
            if (MaintenanceActivityPresent)
                return "database maintenance progress remains active";
            if (ForeignRunningPhaseAttemptCount != 0)
            {
                return $"{ForeignRunningPhaseAttemptCount} running phase attempt(s) belong to another worker instance";
            }
            if (string.IsNullOrWhiteSpace(WorkerStatus))
                return "scraper worker status is not present";
            if (string.IsNullOrWhiteSpace(WorkerInstanceId))
                return "scraper worker instance identity is not present";
            if (!WorkerUpdatedAtUtc.HasValue)
                return "scraper worker freshness timestamp is not present";
            if (PublicationMutationRequired
                || PublicationIsolationComplete
                || AcquisitionFailureMutationRequired)
            {
                return null;
            }
            if (PublicReadsFrozen)
            {
                if (FrozenScrapeId
                    != ExpectedPublishedScrapeId)
                {
                    return $"expected frozen published scrape {ExpectedPublishedScrapeId}, found {FrozenScrapeId?.ToString() ?? "null"}";
                }
                if (!string.Equals(
                        FreezeReason,
                        MetaDatabase
                            .ActiveScrapeFailureIsolationFreezeReason,
                        StringComparison.Ordinal))
                {
                    return $"expected freeze reason {MetaDatabase.ActiveScrapeFailureIsolationFreezeReason}, found {FreezeReason ?? "null"}";
                }
                if (!WorkingPublicationId.HasValue)
                    return "working publication is not present";
                if (CandidatePublicationId
                    != WorkingPublicationId)
                {
                    return $"candidate publication {CandidatePublicationId} does not own working publication {WorkingPublicationId}";
                }
                if (!CandidateStatusIsRunningOrFailed)
                {
                    return $"expected scrape {ScrapeId} status running or failed, found {CandidateStatus}";
                }
            }
            else
            {
                if (string.Equals(
                        CandidateStatus,
                        "running",
                        StringComparison.OrdinalIgnoreCase)
                    && WorkingPublicationId
                        == CandidatePublicationId)
                {
                    if (RunningPhaseAttemptCount != 0)
                    {
                        return $"{RunningPhaseAttemptCount} running phase attempt(s) remain for acquisition failure isolation";
                    }
                    if (FailedAcquisitionPhaseAttemptCount == 0)
                        return "no failed acquisition phase attempt is present";
                    if (AcquisitionCheckpointPresent)
                        return "an acquisition checkpoint is present; guarded resume must be used";
                    if (!string.Equals(
                            WorkerStatus,
                            "offline",
                            StringComparison.OrdinalIgnoreCase)
                        || WorkerCurrentOperationPresent)
                    {
                        return "acquisition failure isolation requires an offline worker with no current operation";
                    }
                }
                if (!string.Equals(
                        CandidateStatus,
                        "failed",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return $"expected terminal scrape {ScrapeId} status failed, found {CandidateStatus}";
                }
                if (FrozenScrapeId is not null
                    || FreezeReason is not null)
                {
                    return "terminalized publication state retained stale freeze identity";
                }
                if (WorkingPublicationId is not null)
                {
                    return $"terminalized publication state retained working publication {WorkingPublicationId}";
                }
                if (!string.Equals(
                        CandidatePublicationStatus,
                        "failed",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return $"expected candidate publication status failed, found {CandidatePublicationStatus ?? "null"}";
                }
            }

            return "publication state is neither an exact frozen post-process candidate, an exact unfrozen acquisition failure, nor an exact terminalized failed candidate awaiting runtime convergence";
        }
    }

    private bool CandidateStatusIsRunningOrFailed =>
        CandidateStatus is not null
        && (
            CandidateStatus.Equals(
                "running",
                StringComparison.OrdinalIgnoreCase)
            || CandidateStatus.Equals(
                "failed",
                StringComparison.OrdinalIgnoreCase));
}

public sealed record ActiveScrapeFailureIsolationExecutionResult(
    bool Succeeded,
    string FailurePhase,
    string FailureMessage,
    ActiveScrapeFailureIsolationReadiness Before,
    ActiveScrapeFailureIsolationReadiness? MutationReadiness,
    ActiveScrapeFailureIsolationReadiness? After,
    string? Error);
