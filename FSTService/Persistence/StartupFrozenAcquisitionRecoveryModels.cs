namespace FSTService.Persistence;

public enum StartupFrozenAcquisitionRecoveryOutcome
{
    /// <summary>No orphaned acquisition freeze exists; nothing was changed.</summary>
    NotApplicable,

    /// <summary>An orphaned freeze exists but a safety precondition failed; nothing was changed.</summary>
    Blocked,

    /// <summary>A freeze left before candidate allocation was released; no scrape was allocated.</summary>
    FreezeReleased,

    /// <summary>The interrupted candidate was failed and isolated and its freeze released.</summary>
    AcquisitionAbandoned,
}

public sealed record StartupFrozenAcquisitionRecoveryResult(
    StartupFrozenAcquisitionRecoveryOutcome Outcome,
    long? ScrapeId,
    long? PublishedScrapeId,
    string? Reason)
{
    public static StartupFrozenAcquisitionRecoveryResult NotApplicable { get; } =
        new(StartupFrozenAcquisitionRecoveryOutcome.NotApplicable, null, null, null);

    public bool Recovered =>
        Outcome is StartupFrozenAcquisitionRecoveryOutcome.FreezeReleased
            or StartupFrozenAcquisitionRecoveryOutcome.AcquisitionAbandoned;

    public static StartupFrozenAcquisitionRecoveryResult Blocked(
        string reason,
        long? scrapeId = null,
        long? publishedScrapeId = null)
        => new(StartupFrozenAcquisitionRecoveryOutcome.Blocked, scrapeId, publishedScrapeId, reason);
}
