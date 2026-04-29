namespace FSTService;

public enum LeaderboardWriteMode
{
    DiskSpool,
    OnlineBounded,
}

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
    public bool QueryProVocals { get; set; } = true;
    public bool QueryProCymbals { get; set; } = true;
    public bool QueryProDrums { get; set; } = true;

    /// <summary>
    /// Root directory for all data files (device auth, path images, precomputed responses).
    /// </summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Minimum age, in hours, before startup cleanup may delete stale scrape spool directories.
    /// </summary>
    public int StaleSpoolCleanupMinAgeHours { get; set; } = 24;

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

    // ─── Phase Selection ───────────────────────────────────────

    /// <summary>
    /// Raw phase flags set by CLI arguments (<c>--solo-scrape</c>, <c>--band-scrape</c>,
    /// <c>--solo-leaderboards</c>). <see cref="ScrapePhase.None"/> means "no filter"
    /// (run all phases). Use <see cref="ResolvedPhases"/> for the expanded set.
    /// </summary>
    public ScrapePhase EnabledPhases { get; set; } = ScrapePhase.None;

    /// <summary>
    /// Fully resolved phase set after group expansion and intermediary filling.
    /// When <see cref="EnabledPhases"/> is <see cref="ScrapePhase.None"/>, returns
    /// <see cref="ScrapePhase.All"/> (full pipeline).
    /// </summary>
    public ScrapePhase ResolvedPhases => ScrapePhaseResolver.Resolve(EnabledPhases);

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
    /// Enables low-priority direct V2 lookups for registered bands. This is a
    /// parallel band lifecycle that reuses the song-machine DOP/CDN wrapper.
    /// </summary>
    public bool EnableRegisteredBandTargetedProcessing { get; set; } = true;

    /// <summary>
    /// Maximum registered bands processed in one post-scrape pass. Set to 0 for no limit.
    /// </summary>
    public int RegisteredBandProcessingMaxBandsPerPass { get; set; } = 10;

    /// <summary>
    /// Maximum direct lookups per registered band in one pass. Set to 0 for no limit.
    /// </summary>
    public int RegisteredBandProcessingMaxLookupsPerBand { get; set; } = 50;

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
    /// callers (band scrape, API-triggered backfill, registration backfill).
    /// High-priority callers (solo scrape, post-scrape refresh) get the full DOP.
    /// Default 30 = low-priority callers can use up to 30% of the pool while
    /// high-priority work is active; 100% when no high-priority phase is running.
    /// </summary>
    public int LowPriorityPercent { get; set; } = 30;

    /// <summary>
    /// Initial DOP when the pool is created or reset between passes.
    /// The AIMD limiter starts at this value and ramps up toward
    /// <see cref="DegreeOfParallelism"/> via slow-start (multiplicative ×1.333
    /// per evaluation window). Avoids an immediate burst that triggers CDN blocks.
    /// Default 32. Set via <c>Scraper__InitialDop</c> env var.
    /// </summary>
    public int InitialDop { get; set; } = 32;

    /// <summary>
    /// Controls how scraped solo leaderboard pages are staged before PostgreSQL persistence.
    /// DiskSpool is the proven default. OnlineBounded is an experimental mode that
    /// writes during fetch through bounded channels and explicit database backpressure.
    /// Set via <c>Scraper__LeaderboardWriteMode</c> env var.
    /// </summary>
    public LeaderboardWriteMode LeaderboardWriteMode { get; set; } = LeaderboardWriteMode.DiskSpool;

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
    /// Maximum solo leaderboard pages per PostgreSQL COPY/merge batch in online
    /// bounded write mode. Default 64 matches the disk-spool flush chunk size.
    /// Set via <c>Scraper__OnlineWriteBatchPages</c> env var.
    /// </summary>
    public int OnlineWriteBatchPages { get; set; } = 64;

    /// <summary>
    /// Number of concurrent database workers used by online bounded solo writes.
    /// Keep this small so PostgreSQL backpressure slows fetchers instead of growing RAM.
    /// Set via <c>Scraper__OnlineDbWriterConcurrency</c> env var.
    /// </summary>
    public int OnlineDbWriterConcurrency { get; set; } = 2;

    /// <summary>
    /// Number of leaderboard neighbors above/below to include when computing
    /// leaderboard rivals. Each neighbor is compared per-song against the user.
    /// Default 10.
    /// </summary>
    public int LeaderboardRivalRadius { get; set; } = 10;

    /// <summary>
    /// Retained for configuration compatibility.
    /// Scrape passes no longer apply an internal timeout and instead run until
    /// completion or external cancellation.
    /// </summary>
    public int ScrapePassTimeoutMinutes { get; set; }

    // ─── Band Scraping ─────────────────────────────────

    /// <summary>
    /// HTTP proxy URLs for round-robin rotation during leaderboard scraping.
    /// Each URL should be a full proxy address (e.g. "http://127.0.0.1:8888").
    /// When empty, requests go direct (no proxy). When populated, each request
    /// is dispatched through the next proxy in round-robin order.
    /// Set via <c>Scraper__ProxyUrls__0</c>, <c>Scraper__ProxyUrls__1</c>, etc.
    /// </summary>
    public List<string> ProxyUrls { get; set; } = [];

    /// <summary>
    /// Gluetun control API URLs for VPN city cycling on CDN blocks.
    /// Each URL maps 1:1 to the corresponding <see cref="ProxyUrls"/> entry.
    /// When a proxy gets CDN-blocked, the handler cycles its VPN to a new city
    /// for a fresh egress IP, then verifies connectivity before resuming.
    /// Set via <c>Scraper__ControlUrls__0</c>, <c>Scraper__ControlUrls__1</c>, etc.
    /// Example: "http://172.17.0.2:8000"
    /// </summary>
    public List<string> ControlUrls { get; set; } = [];

    /// <summary>
    /// Docker container names for each gluetun proxy, mapped 1:1 to <see cref="ProxyUrls"/>.
    /// When populated, the handler recreates the container (stop/rm/run) instead of using
    /// the gluetun control API to cycle VPN servers. This is more reliable when gluetun is
    /// crash-looping or the control API is wedged.
    /// Requires the Docker socket to be mounted into the FSTService container.
    /// Set via <c>Scraper__ContainerNames__0</c>, <c>Scraper__ContainerNames__1</c>, etc.
    /// Example: "gluetun-1"
    /// </summary>
    public List<string> ContainerNames { get; set; } = [];

    /// <summary>
    /// When true, use active/standby proxy mode: all traffic goes through one proxy
    /// at a time, failing over to the next on CDN block. Other proxies stay idle with
    /// fresh IPs. When false (default), round-robin distributes across all proxies.
    /// Set via <c>Scraper__ProxyActiveStandby</c> env var.
    /// </summary>
    public bool ProxyActiveStandby { get; set; } = true;

    /// <summary>
    /// When true, scrape Band_Duets, Band_Trios, and Band_Quad leaderboards
    /// in a background phase after registered-user refresh completes.
    /// Default true — band scraping runs alongside post-scrape enrichment.
    /// </summary>
    public bool EnableBandScraping { get; set; } = true;

    /// <summary>
    /// Maximum number of songs processed concurrently by post-scrape band context
    /// extraction. Zero chooses a conservative automatic cap based on CPU count.
    /// Set via <c>Scraper__BandExtractionParallelism</c> env var.
    /// </summary>
    public int BandExtractionParallelism { get; set; } = 0;

    /// <summary>
    /// Number of impacted band teams rebuilt per membership-summary transaction
    /// after post-scrape extraction. Larger batches reduce transaction overhead;
    /// smaller batches reduce lock duration and retry scope.
    /// Set via <c>Scraper__BandMembershipRebuildBatchSize</c> env var.
    /// </summary>
    public int BandMembershipRebuildBatchSize { get; set; } = 500;

    /// <summary>
    /// Maximum pages to fetch per band leaderboard (25 entries per page).
    /// Band leaderboards use per-member CHOpt validation instead of a single
    /// max-score threshold. Pagination continues until <see cref="BandValidEntryTarget"/>
    /// valid entries are found or pages are exhausted.
    /// Default 400 = top 10,000 entries (25/page × 400 pages).
    /// </summary>
    public int BandMaxPagesPerLeaderboard { get; set; } = 400;

    /// <summary>
    /// Target number of valid band entries per (song, band_type).
    /// An entry is valid when ALL members' individual scores are ≤ CHOptMax × 0.95
    /// for their respective instruments. Over-threshold entries are still persisted
    /// (client-side filter controls visibility) but do not count toward the target.
    /// Pagination stops once this many valid entries are collected or pages are exhausted.
    /// Default 10,000.
    /// </summary>
    public int BandValidEntryTarget { get; set; } = 10_000;
}
