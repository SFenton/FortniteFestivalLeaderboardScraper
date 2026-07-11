namespace FSTService.Persistence;

public sealed record PublishedScopeSource(
    long PublishedScrapeId,
    string SongId,
    string Instrument,
    string ScopeKind,
    string SourceKind,
    long? SourceSnapshotId,
    long SourceScrapeId,
    long RowCount,
    string? ContentFingerprint,
    string? CoverageFingerprint,
    long? ReportedTotalEntries,
    int? ReportedTotalPages,
    bool IsComplete,
    DateTime CreatedAtUtc,
    DateTime ValidatedAtUtc);

public sealed record PublishedScopeSourceBuildResult(
    long PublishedScrapeId,
    int ExpectedScopeCount,
    int ValidatedScopeCount,
    int MappedScopeCount,
    int MissingScopeCount)
{
    public bool IsComplete =>
        ExpectedScopeCount > 0
        && ValidatedScopeCount == ExpectedScopeCount
        && MappedScopeCount == ExpectedScopeCount
        && MissingScopeCount == 0;
}

public sealed record LeaderboardScopeCoverageResult(
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

public sealed record PublishedScopeSourceBackfillResult(
    long? PublishedScrapeId,
    int ExpectedScopeCount,
    int MappedScopeCount,
    bool Applied,
    string Status);
