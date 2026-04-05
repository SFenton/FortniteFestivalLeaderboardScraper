namespace FSTService;

/// <summary>
/// Configuration for the scraping service, loaded from appsettings.json.
/// </summary>
public sealed class ScraperOptions
{
    public const string Section = "Scraper";

    /// <summary>
    /// How often to run a full score scrape (default: 4 hours).
    /// </summary>
    public TimeSpan ScrapeInterval { get; set; } = TimeSpan.FromHours(4);

    /// <summary>
    /// How often to re-sync the song catalog from Epic in the background,
    /// aligned to clock boundaries (default: 5 minutes).
    /// </summary>
    public TimeSpan SongSyncInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Max concurrent leaderboard requests per scrape pass.
    /// </summary>
    public int DegreeOfParallelism { get; set; } = 16;

    /// <summary>
    /// Hard cap on requests per second across all phases. Implemented as a token
    /// bucket inside the adaptive concurrency limiter. 0 = unlimited (default).
    /// Set via <c>Scraper__MaxRequestsPerSecond</c> env var.
    /// </summary>
    public int MaxRequestsPerSecond { get; set; }

    /// <summary>
    /// Which instruments to query.
    /// </summary>
    public bool QueryLead { get; set; } = true;
    public bool QueryDrums { get; set; } = true;
    public bool QueryVocals { get; set; } = true;
    public bool QueryBass { get; set; } = true;
    public bool QueryProLead { get; set; } = true;
    public bool QueryProBass { get; set; } = true;

    /// <summary>
    /// Root directory for all data files (device auth, MIDI cache, path images, precomputed responses).
    /// </summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Path to the device auth credentials file.
    /// </summary>
    public string DeviceAuthPath { get; set; } = "data/device-auth.json";

    /// <summary>
    /// When true, start only the HTTP API layer — do not run any background
    /// scraping.  Useful for testing API endpoints (e.g. auth callback) without
    /// waiting for the full scrape loop.
    /// Set via <c>--api-only</c> CLI argument or <c>Scraper__ApiOnly=true</c> env var.
    /// </summary>
    public bool ApiOnly { get; set; }

    /// <summary>
    /// When true, only run the device-code auth setup and exit.
    /// Set via <c>--setup</c> CLI argument.
    /// </summary>
    public bool SetupOnly { get; set; }

    /// <summary>
    /// When true, run a single scrape + resolve pass and then exit
    /// instead of looping on the interval timer.
    /// Set via <c>--once</c> CLI argument.
    /// </summary>
    public bool RunOnce { get; set; }

    /// <summary>
    /// When true, skip scraping and only run account name resolution
    /// against unresolved IDs already in the meta DB. Then exit.
    /// Set via <c>--resolve-only</c> CLI argument.
    /// </summary>
    public bool ResolveOnly { get; set; }

    /// <summary>
    /// When true, skip scraping and only run the backfill enrichment
    /// phase for registered users (fetches rank/percentile from Epic API
    /// for existing entries). Then exit.
    /// Set via <c>--backfill-only</c> CLI argument.
    /// </summary>
    public bool BackfillOnly { get; set; }

    /// <summary>
    /// When set, fetch scores for a single matching song and exit.
    /// The value is matched case-insensitively against song titles.
    /// Set via <c>--test "song name"</c> CLI argument.
    /// </summary>
    public string? TestSongQuery { get; set; }

    /// <summary>
    /// When true, precompute player and leaderboard API responses to disk and exit.
    /// The service loads these on next startup for instant responses from the first request.
    /// Set via <c>--precompute</c> CLI argument.
    /// </summary>
    public bool PrecomputeOnly { get; set; }

    // ─── Path Generation ───────────────────────────────────────

    /// <summary>
    /// Path to the CHOpt CLI binary. Relative paths are resolved from the working directory.
    /// </summary>
    public string CHOptPath { get; set; } = "tools/CHOpt";

    /// <summary>
    /// Hex-encoded 128-bit AES key for decrypting Fortnite Festival MIDI .dat files.
    /// Can also be set via the FESTIVAL_MIDI_KEY environment variable.
    /// </summary>
    public string? MidiEncryptionKey { get; set; }

    /// <summary>
    /// Maximum number of concurrent CHOpt processes during path generation.
    /// </summary>
    public int PathGenerationParallelism { get; set; } = 4;

    /// <summary>
    /// Enable or disable automatic path generation when new songs are detected.
    /// </summary>
    public bool EnablePathGeneration { get; set; } = true;

    /// <summary>
    /// Maximum pages to fetch per leaderboard (100 entries per page).
    /// Caps the number of tasks spawned per song/instrument, regardless of what Epic reports.
    /// Default 100 = top 10,000 entries. Set to 0 for unlimited.
    /// </summary>
    public int MaxPagesPerLeaderboard { get; set; } = 100;

    /// <summary>
    /// When CHOpt max scores are available, any leaderboard whose top score exceeds
    /// <c>CHOptMax × OverThresholdMultiplier</c> triggers a "deep scrape" wave 2 that
    /// fetches additional pages beyond <see cref="MaxPagesPerLeaderboard"/>.
    /// This multiplier only controls the <b>trigger condition</b>; the valid-entry
    /// cutoff used for counting and pruning is <c>CHOptMax × <see cref="ValidCutoffMultiplier"/></c>.
    /// Default 1.05 = 5% above CHOpt's theoretical maximum.
    /// </summary>
    public double OverThresholdMultiplier { get; set; } = 1.05;

    /// <summary>
    /// Multiplier applied to CHOpt max scores to determine the valid-entry cutoff
    /// used for deep-scrape counting and post-scrape pruning.
    /// <c>ValidCutoff = CHOptMax × ValidCutoffMultiplier</c>.
    /// Entries above this cutoff are treated as over-threshold (preserved unconditionally).
    /// Default 0.95 ensures 10,000 valid entries remain visible even when the
    /// frontend leeway slider is set to its minimum of −5%.
    /// </summary>
    public double ValidCutoffMultiplier { get; set; } = 0.95;

    /// <summary>
    /// Batch size (in pages) for deep-scrape wave 2 extension fetches.
    /// When <see cref="ValidEntryTarget"/> is set, wave 2 fetches in batches of this
    /// many pages, counting valid entries after each batch until the target is met.
    /// When <see cref="ValidEntryTarget"/> is 0 (legacy mode), this is the fixed number
    /// of extra pages fetched beyond <see cref="MaxPagesPerLeaderboard"/>.
    /// Default 100 = 10,000 entries per batch.
    /// </summary>
    public int OverThresholdExtraPages { get; set; } = 100;

    /// <summary>
    /// Target number of valid (≤ <c>CHOptMax × <see cref="ValidCutoffMultiplier"/></c>)
    /// leaderboard entries to capture per song/instrument during deep scrape. When the
    /// top score exceeds the CHOpt trigger threshold
    /// (<c>CHOptMax × OverThresholdMultiplier</c>), wave 2 fetches
    /// additional pages in batches of <see cref="OverThresholdExtraPages"/>
    /// until this many valid entries are found, the leaderboard is exhausted, or a 403
    /// boundary is hit. All entries above the valid cutoff are captured unconditionally.
    /// Set to 0 to use legacy fixed-page behavior. Default 10,000.
    /// </summary>
    public int ValidEntryTarget { get; set; } = 10_000;

    /// <summary>
    /// When true, scrape songs one at a time instead of all in parallel.
    /// Instruments still run in parallel (~6), but page concurrency is controlled by <see cref="PageConcurrency"/>.
    /// </summary>
    public bool SequentialScrape { get; set; }

    /// <summary>
    /// Max concurrent page fetches per instrument when <see cref="SequentialScrape"/> is true.
    /// Default 10 = ~60 concurrent requests (6 instruments × 10 pages). Set to 1 for fully sequential.
    /// Ignored when SequentialScrape is false (parallel mode uses DegreeOfParallelism instead).
    /// </summary>
    public int PageConcurrency { get; set; } = 10;

    /// <summary>
    /// Max songs scraped concurrently when <see cref="SequentialScrape"/> is true.
    /// Default 1 = one song at a time. Higher values scrape multiple songs in parallel
    /// while still using bounded page concurrency per instrument.
    /// Total concurrent requests = SongConcurrency × 6 instruments × PageConcurrency.
    /// </summary>
    public int SongConcurrency { get; set; } = 1;

    /// <summary>
    /// Max accounts per batched V2 lookup request. Each request includes the caller
    /// plus up to this many target accounts. The V2 endpoint has a ~19 KB body limit
    /// (empirically ~518 teams). Default 500 is safely under that limit.
    /// Configurable via <c>Scraper__LookupBatchSize</c> env var.
    /// </summary>
    public int LookupBatchSize { get; set; } = 500;

    /// <summary>
    /// Maximum number of songs processed concurrently inside the
    /// <see cref="FSTService.Scraping.SongProcessingMachine"/>. Each song fans out
    /// into 6 parallel instrument tasks, so total concurrent API calls ≈ this × 6.
    /// Prevents CDN blocks that occur when the full scrape DOP is used for heavy
    /// V2 POST batch lookups.
    /// Default 32 → ~192 concurrent V2 requests.
    /// Configurable via <c>Scraper__SongMachineDop</c> env var.
    /// </summary>
    public int SongMachineDop { get; set; } = 32;

    /// <summary>
    /// When true, the post-scrape refresh also queries the current season for each
    /// registered user to capture sub-optimal sessions (plays that didn't beat the
    /// all-time best) for the score history chart.
    /// Doubles the refresh API calls but provides complete play-by-play history.
    /// </summary>
    public bool RefreshCurrentSeasonSessions { get; set; } = true;

    // ─── Shared DOP Pool ───────────────────────────────────

    /// <summary>
    /// Percentage of <see cref="DegreeOfParallelism"/> available to low-priority
    /// callers (API-triggered backfill, registration backfill).
    /// High-priority callers (main scrape, post-scrape refresh) get the full DOP.
    /// Default 20 = low-priority callers can use up to 20% of the pool.
    /// </summary>
    public int LowPriorityPercent { get; set; } = 20;

    /// <summary>
    /// Capacity of each per-instrument bounded channel in the persistence pipeline.
    /// Higher values allow more buffering between scraper and writer tasks; lower
    /// values apply earlier back-pressure. Default 128.
    /// </summary>
    public int BoundedChannelCapacity { get; set; } = 128;

    /// <summary>
    /// Maximum number of work items batched into a single PostgreSQL transaction
    /// by the pipelined writer. Higher values reduce commit overhead but increase
    /// transaction size and memory usage. Default 10.
    /// </summary>
    public int WriteBatchSize { get; set; } = 10;

    /// <summary>
    /// Number of leaderboard neighbors above/below to include when computing
    /// leaderboard rivals. Each neighbor is compared per-song against the user.
    /// Default 10.
    /// </summary>
    public int LeaderboardRivalRadius { get; set; } = 10;

    /// <summary>
    /// Maximum time (in minutes) for a single scrape pass before it is cancelled.
    /// Acts as a safety net against infinite hangs. The CDN slot-release mechanism
    /// is the primary defence; this is a backstop.
    /// Default 45 minutes. Set to 0 to disable.
    /// When <see cref="FullCrawlEnabled"/> is true, consider increasing to 300+.
    /// </summary>
    public int ScrapePassTimeoutMinutes { get; set; } = 45;
}
