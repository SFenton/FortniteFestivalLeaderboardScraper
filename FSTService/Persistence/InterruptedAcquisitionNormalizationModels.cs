namespace FSTService.Persistence;

public sealed record InterruptedAcquisitionNormalizationReadiness
{
    public required long ScrapeId { get; init; }
    public required long ExpectedPublishedScrapeId { get; init; }
    public required long ExpectedCurrentPublicationId { get; init; }
    public required long ExpectedPreviousPublicationId { get; init; }
    public required long ExpectedWorkingPublicationId { get; init; }
    public required string ExpectedWorkerInstanceId { get; init; }
    public required DateTime ExpectedWorkerFreshnessUtc { get; init; }
    public required string ExpectedPhaseId { get; init; }
    public required int ExpectedAttempt { get; init; }
    public required bool SchemaReady { get; init; }
    public required bool FenceAcquired { get; init; }
    public long? PublishedScrapeId { get; init; }
    public long? CurrentPublicationId { get; init; }
    public long? PreviousPublicationId { get; init; }
    public long? WorkingPublicationId { get; init; }
    public bool? PublicReadsFrozen { get; init; }
    public DateTime? PublicReadsFrozenAtUtc { get; init; }
    public long? FrozenScrapeId { get; init; }
    public string? FreezeReason { get; init; }
    public DateTime? CommitIntentStartedAtUtc { get; init; }
    public DateTime? CommitIntentHeartbeatAtUtc { get; init; }
    public string? CommitIntentOwner { get; init; }
    public string? ScrapeStatus { get; init; }
    public bool AcquisitionCheckpointPresent { get; init; }
    public bool AcquisitionCheckpointPayloadPresent { get; init; }
    public int OtherRunningScrapeCount { get; init; }
    public int NewerScrapeCount { get; init; }
    public long? CurrentGenerationScrapeId { get; init; }
    public string? CurrentGenerationStatus { get; init; }
    public long? CurrentGenerationPreviousPublicationId { get; init; }
    public long? PreviousGenerationScrapeId { get; init; }
    public string? PreviousGenerationStatus { get; init; }
    public long? CandidateGenerationScrapeId { get; init; }
    public string? CandidateGenerationStatus { get; init; }
    public int CandidateGenerationCount { get; init; }
    public int CandidateSourceMappingCount { get; init; }
    public int ActiveWorkerQueryCount { get; init; }
    public int WaitingLockCount { get; init; }
    public int AdvisoryLockCount { get; init; }
    public bool MaintenanceActivityPresent { get; init; }
    public int CandidateAttemptCount { get; init; }
    public int RunningPhaseAttemptCount { get; init; }
    public int GlobalRunningPhaseAttemptCount { get; init; }
    public string? AttemptPhaseId { get; init; }
    public int? Attempt { get; init; }
    public string? AttemptStatus { get; init; }
    public string? AttemptWorkerInstanceId { get; init; }
    public DateTime? AttemptCompletedAtUtc { get; init; }
    public string? AttemptWarningMessage { get; init; }
    public string? AttemptErrorMessage { get; init; }
    public string? WorkerStatus { get; init; }
    public string? WorkerMode { get; init; }
    public string? WorkerInstanceId { get; init; }
    public DateTime? WorkerFreshnessUtc { get; init; }
    public bool WorkerCurrentOperationPresent { get; init; }
    public bool WorkerLastOperationPresent { get; init; }
    public string? WorkerOperationKey { get; init; }
    public string? WorkerOperationPhaseId { get; init; }
    public int? WorkerOperationAttempt { get; init; }
    public long? WorkerOperationScrapeId { get; init; }
    public bool ReadyToNormalize { get; init; }
    public bool AlreadyNormalized { get; init; }
    public string? BlockingReason { get; init; }

    public bool CanExecute =>
        ReadyToNormalize || AlreadyNormalized;

    public string State => ReadyToNormalize
        ? "ready"
        : AlreadyNormalized
            ? "already_normalized"
            : "rejected";
}

public sealed record InterruptedAcquisitionNormalizationExecutionResult(
    bool Succeeded,
    bool AlreadyNormalized,
    InterruptedAcquisitionNormalizationReadiness Before,
    InterruptedAcquisitionNormalizationReadiness? MutationReadiness,
    InterruptedAcquisitionNormalizationReadiness? After,
    ActiveScrapeFailureIsolationReadiness? OfficialIsolationReadiness,
    string? Error);
