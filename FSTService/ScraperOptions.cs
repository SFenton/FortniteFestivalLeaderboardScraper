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
}
