namespace FSTService.Persistence;

using FSTService.Scraping;

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
    public DateTime? AcquisitionCompletedAtUtc { get; init; }
    public int? SongsScraped { get; init; }
    public long? TotalEntries { get; init; }
    public int? TotalRequests { get; init; }
    public long? TotalBytes { get; init; }
    public bool? EpicReportedOver100Pages { get; init; }
    public int? PublicationSongCount { get; init; }
    public bool PublicationSongCatalogIsExact { get; init; }
    public int? ExpectedSoloScopeCount { get; init; }
    public int? ExpectedSoloScopeFingerprintVersion { get; init; }
    public string? ExpectedSoloScopeFingerprint { get; init; }
    public int ActualCompleteSoloScopeCount { get; init; }
    public string ActualCompleteSoloScopeFingerprint { get; init; } =
        string.Empty;
    public bool ActualCompleteSoloScopeOwnedByCatalog { get; init; }

    public string? AcquisitionMetricsValidationError
    {
        get
        {
            if (AcquisitionCompletedAtUtc is null)
                return "acquisition checkpoint is missing";
            if (AcquisitionCompletedAtUtc < StartedAtUtc)
                return "acquisition checkpoint predates scrape start";
            if (SongsScraped is null
                || TotalEntries is null
                || TotalRequests is null
                || TotalBytes is null
                || EpicReportedOver100Pages is null)
            {
                return "acquisition metrics are incomplete";
            }
            if (SongsScraped < 0
                || TotalEntries < 0
                || TotalRequests < 0
                || TotalBytes < 0)
            {
                return "acquisition metrics contain a negative value";
            }
            if (ExpectedSoloScopeCount is null
                || ExpectedSoloScopeFingerprintVersion is null
                || ExpectedSoloScopeFingerprint is null)
            {
                return "solo acquisition scope contract is incomplete";
            }
            if (ExpectedSoloScopeCount <= 0)
                return "solo acquisition scope contract is empty";
            if (ExpectedSoloScopeFingerprintVersion
                != SoloAcquisitionScopeFingerprint.Version)
            {
                return "solo acquisition scope fingerprint version is unsupported";
            }
            if (ExpectedSoloScopeFingerprint.Length != 64
                || ExpectedSoloScopeFingerprint.Any(
                    static character =>
                        character is not (>= '0' and <= '9')
                        and not (>= 'a' and <= 'f')))
            {
                return "solo acquisition scope fingerprint is invalid";
            }
            if (PublicationSongCount is null
                || PublicationSongCount < 0
                || !PublicationSongCatalogIsExact)
            {
                return "publication song catalog is missing";
            }
            if (SongsScraped > PublicationSongCount)
                return "songs scraped exceeds the publication song catalog";
            var requiredScopeCount =
                (long)PublicationSongCount
                * GlobalLeaderboardScraper.AllInstruments.Count;
            if (ExpectedSoloScopeCount != requiredScopeCount)
            {
                return "solo acquisition scope does not cover every catalog song and canonical instrument";
            }
            if (ActualCompleteSoloScopeCount != ExpectedSoloScopeCount)
                return "complete solo manifest count differs from the acquisition checkpoint";
            if (!string.Equals(
                    ActualCompleteSoloScopeFingerprint,
                    ExpectedSoloScopeFingerprint,
                    StringComparison.Ordinal))
            {
                return "complete solo manifest fingerprint differs from the acquisition checkpoint";
            }
            if (!ActualCompleteSoloScopeOwnedByCatalog)
                return "complete solo manifests are not owned by the publication song catalog";
            if (ManifestCount > 0 && TotalRequests == 0)
                return "completed manifests require at least one logical request";
            return null;
        }
    }

    public bool CanResume =>
        Status == "running"
        && PublishedScrapeId != ScrapeId
        && ManifestCount > 0
        && CompleteManifestCount == ManifestCount
        && WriterFailureCount == 0
        && CriticalPhaseFailureCount == 0
        && AcquisitionMetricsValidationError is null;
}
