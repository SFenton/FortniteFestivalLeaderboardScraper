namespace FSTService.Persistence;

public sealed record ScrapePhaseOutcomeRecord(
    long ScrapeId,
    string Phase,
    string Criticality,
    string Status,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    long DurationMs,
    string? ErrorMessage);

public sealed record ScopeManifestPersistenceResult(
    long ScrapeId,
    int ExpectedScopeCount,
    int ObservedScopeCount,
    int PersistedScopeCount,
    int MissingScopeCount,
    int IncompleteScopeCount)
{
    public bool IsComplete =>
        ExpectedScopeCount > 0
        && ObservedScopeCount == ExpectedScopeCount
        && PersistedScopeCount == ExpectedScopeCount
        && MissingScopeCount == 0
        && IncompleteScopeCount == 0;
}

public sealed record ScrapeFailureSummary(
    long ScrapeId,
    string Status,
    DateTime? FailedAtUtc,
    string? FailurePhase,
    string? FailureMessage,
    int BestEffortFailureCount,
    IReadOnlyList<string> BestEffortFailedPhases);

public sealed record ScrapeResumeState(
    long ScrapeId,
    DateTime StartedAtUtc,
    string Status,
    long? PublishedScrapeId,
    int ManifestCount,
    int CompleteManifestCount,
    int WriterFailureCount,
    int CriticalPhaseFailureCount,
    IReadOnlyList<ScrapePhaseOutcomeRecord> PhaseOutcomes)
{
    public bool CanResume =>
        Status == "running"
        && PublishedScrapeId != ScrapeId
        && ManifestCount > 0
        && CompleteManifestCount == ManifestCount
        && WriterFailureCount == 0
        && CriticalPhaseFailureCount == 0;
}
