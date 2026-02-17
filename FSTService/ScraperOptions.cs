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
    /// aligned to clock boundaries (default: 15 minutes → :00, :15, :30, :45).
    /// </summary>
    public TimeSpan SongSyncInterval { get; set; } = TimeSpan.FromMinutes(15);

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
    /// When set, fetch scores for a single matching song and exit.
    /// The value is matched case-insensitively against song titles.
    /// Set via <c>--test "song name"</c> CLI argument.
    /// </summary>
    public string? TestSongQuery { get; set; }
}
