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
    /// Root directory for all data files (instrument DBs, meta DB, core DB, credentials).
    /// </summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Path to the SQLite database file (core song catalog / personal scores).
    /// </summary>
    public string DatabasePath { get; set; } = "data/fst-service.db";

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
    /// cutoff used for counting and pruning is the raw CHOpt max (not multiplied).
    /// Default 1.05 = 5% above CHOpt's theoretical maximum.
    /// </summary>
    public double OverThresholdMultiplier { get; set; } = 1.05;

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
    /// Target number of valid (≤ raw CHOpt max) leaderboard entries to capture
    /// per song/instrument during deep scrape. When the top score exceeds the CHOpt
    /// trigger threshold (<c>CHOptMax × OverThresholdMultiplier</c>), wave 2 fetches
    /// additional pages in batches of <see cref="OverThresholdExtraPages"/>
    /// until this many valid entries are found, the leaderboard is exhausted, or a 403
    /// boundary is hit. All entries above CHOpt max are captured unconditionally.
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
    /// When true, the post-scrape refresh also queries the current season for each
    /// registered user to capture sub-optimal sessions (plays that didn't beat the
    /// all-time best) for the score history chart.
    /// Doubles the refresh API calls but provides complete play-by-play history.
    /// </summary>
    public bool RefreshCurrentSeasonSessions { get; set; } = true;

    // ─── Song Processing Machine ───────────────────────────

    /// <summary>
    /// Initial degree of parallelism for the song processing machine.
    /// Controls how many concurrent V2 batch API calls are in flight.
    /// </summary>
    public int MachineDop { get; set; } = 64;

    /// <summary>
    /// Minimum DOP the adaptive limiter can reduce to during machine operation.
    /// </summary>
    public int MachineMinDop { get; set; } = 2;

    /// <summary>
    /// Maximum DOP the adaptive limiter can increase to during machine operation.
    /// </summary>
    public int MachineMaxDop { get; set; } = 256;

    /// <summary>
    /// Percentage of max DOP available to low-priority callers (backfill/registration).
    /// High-priority callers (post-scrape) get access to the full DOP.
    /// Default 20 = low-priority machines can use up to 20% of the pool.
    /// </summary>
    public int MachineLowPriorityPercent { get; set; } = 20;

    /// <summary>
    /// Capacity of each per-instrument bounded channel in the persistence pipeline.
    /// Higher values allow more buffering between scraper and writer tasks; lower
    /// values apply earlier back-pressure. Default 32.
    /// </summary>
    public int BoundedChannelCapacity { get; set; } = 32;
}
