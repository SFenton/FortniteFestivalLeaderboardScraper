namespace FSTService;

/// <summary>
/// Controls band rank-history persistence and background catch-up behavior.
/// These options intentionally separate current-ranking freshness from historical
/// maintenance so scrapes can complete without waiting for best-effort history work.
/// </summary>
public sealed class BandRankHistoryOptions
{
    public const string Section = "BandRankHistory";

    /// <summary>
    /// Execution mode for band rank history snapshots.
    /// Inline preserves legacy blocking behavior; Background enqueues resumable jobs;
    /// Disabled skips history writes while leaving current rankings published.
    /// </summary>
    public BandRankHistoryMode Mode { get; set; } = BandRankHistoryMode.Inline;

    /// <summary>Use a latest-state table instead of scanning the full history table every pass.</summary>
    public bool UseLatestState { get; set; } = true;

    /// <summary>Write compact API-oriented history points alongside the wide compatibility table.</summary>
    public bool UseNarrowHistory { get; set; } = true;

    /// <summary>Continue writing the legacy wide history table for rollback/API fallback.</summary>
    public bool UseWideHistoryCompatibilityWrite { get; set; } = true;

    /// <summary>Preferred source for band rank-history API reads.</summary>
    public BandRankHistoryApiReadSource ApiReadSource { get; set; } = BandRankHistoryApiReadSource.NarrowWithWideFallback;

    /// <summary>Maximum number of chunk rows to process at once when chunking by row count is needed.</summary>
    public int ChunkSize { get; set; } = 250_000;

    /// <summary>Maximum number of chunks a worker may process concurrently. Initial implementation keeps writes conservative.</summary>
    public int MaxParallelChunks { get; set; } = 1;

    /// <summary>Command timeout for history maintenance SQL. Zero means provider/infinite default.</summary>
    public int CommandTimeoutSeconds { get; set; } = 0;

    /// <summary>History retention window in days.</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>Allow best-effort history chunks to use synchronous_commit=off inside their transaction.</summary>
    public bool SynchronousCommitOff { get; set; } = false;

    /// <summary>Supersede incomplete same-day jobs when a newer scrape publishes equivalent history work.</summary>
    public bool CoalesceSameDaySnapshots { get; set; } = true;

    /// <summary>Maximum catch-up age before jobs are marked stale/superseded instead of processed forever.</summary>
    public int MaxCatchupAgeHours { get; set; } = 24;
}

public enum BandRankHistoryMode
{
    Inline,
    Background,
    Disabled,
}

public enum BandRankHistoryApiReadSource
{
    Wide,
    Narrow,
    NarrowWithWideFallback,
}

/// <summary>
/// Generic scrape-aware background-work controls.
/// </summary>
public sealed class BackgroundJobOptions
{
    public const string Section = "BackgroundJobs";

    public int StopBeforeNextScrapeMinutes { get; set; } = 5;
    public int MaxGracefulStopSeconds { get; set; } = 30;
    public bool PauseBestEffortOnScrapeStart { get; set; } = true;
    public int MaxDbConnections { get; set; } = 1;
}
