using System.Diagnostics;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

// ═══════════════════════════════════════════════════════════════
//  FirstSeen Binary Search Harness
//  Tests FirstSeenSeasonCalculator against real Epic APIs with
//  SharedDopPool DOP/RPS enforcement. Probes season windows,
//  then binary searches each song to find first-seen season.
// ═══════════════════════════════════════════════════════════════

const int DefaultDop = 16;
const int DefaultRps = 16;

// ─── Parse CLI args ─────────────────────────────────────────

string? token = null, caller = null, songsArg = null;
string? pgConnStr = null;
int maxDop = DefaultDop;
int maxRps = DefaultRps;
bool verbose = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--token":   token = args[++i]; break;
        case "--caller":  caller = args[++i]; break;
        case "--songs":   songsArg = args[++i]; break;
        case "--pg":      pgConnStr = args[++i]; break;
        case "--dop":     maxDop = int.Parse(args[++i]); break;
        case "--rps":     maxRps = int.Parse(args[++i]); break;
        case "--verbose": verbose = true; break;
    }
}

if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(caller) || string.IsNullOrEmpty(songsArg))
{
    Console.Error.WriteLine("""
        Usage: FirstSeenHarness --token <epic-token> --caller <account-id>
               --songs <csv-or-file>
               [--pg <conn-string>] [--dop <int>] [--rps <int>] [--verbose]

        Required:
          --token     Epic Bearer access token
          --caller    Caller's Epic account ID
          --songs     Comma-separated song IDs or path to file (one per line)

        Optional:
          --pg        PostgreSQL connection string (default: Testcontainers)
          --dop       Max degree of parallelism (default: 16)
          --rps       Max requests per second (default: 16)
          --verbose   Show debug-level logs
        """);
    return 1;
}

var songIds = ParseListArg(songsArg);
Console.WriteLine($"Songs:  {songIds.Count}");
Console.WriteLine($"DOP:    {maxDop}  RPS: {maxRps}");
Console.WriteLine();

// ─── Logging ────────────────────────────────────────────────

using var loggerFactory = LoggerFactory.Create(b =>
{
    b.AddConsole();
    b.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
    b.AddFilter("Npgsql", LogLevel.Warning);
});

// ─── Database ───────────────────────────────────────────────

PostgreSqlContainer? container = null;
NpgsqlDataSource pgDataSource;

if (pgConnStr is not null)
{
    Console.WriteLine("Using provided PostgreSQL connection string.");
    pgDataSource = NpgsqlDataSource.Create(pgConnStr);
}
else
{
    Console.WriteLine("Starting Testcontainers PostgreSQL...");
    container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();
    await container.StartAsync();
    pgConnStr = container.GetConnectionString();
    pgDataSource = NpgsqlDataSource.Create(pgConnStr);
    Console.WriteLine("Testcontainers PostgreSQL started.");
}

await DatabaseInitializer.EnsureSchemaAsync(pgDataSource);
Console.WriteLine("Database schema created.");

// ─── Wire up dependencies ───────────────────────────────────

var metaDb = new MetaDatabase(pgDataSource, loggerFactory.CreateLogger<MetaDatabase>());
var persistence = new GlobalLeaderboardPersistence(
    metaDb, loggerFactory,
    loggerFactory.CreateLogger<GlobalLeaderboardPersistence>(),
    pgDataSource);
persistence.Initialize();

var progress = new ScrapeProgressTracker();

var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 256,
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    EnableMultipleHttp2Connections = true,
    AutomaticDecompression = System.Net.DecompressionMethods.All,
};
var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

var scraper = new GlobalLeaderboardScraper(
    httpClient, progress,
    loggerFactory.CreateLogger<GlobalLeaderboardScraper>());

var poolLogger = loggerFactory.CreateLogger("SharedDopPool");
using var pool = new SharedDopPool(maxDop, minDop: 2, maxDop: maxDop,
    lowPriorityPercent: 100, poolLogger, maxRps);

// ─── Discover season windows ────────────────────────────────

Console.WriteLine();
Console.WriteLine("Probing season windows...");

var probeSongId = songIds[0]; // use the first song from the list
var seasonWindows = new List<SeasonWindowInfo>();
int consecutiveFailures = 0;

for (int season = 1; season <= 20 && consecutiveFailures < 2; season++)
{
    var seasonPrefix = season == 1 ? "evergreen" : $"season{season:D3}";
    var lowToken = await pool.AcquireLowAsync(CancellationToken.None);
    try
    {
        await scraper.LookupSeasonalAsync(
            probeSongId, "Solo_Guitar", seasonPrefix,
            caller, token, caller, ct: CancellationToken.None);

        seasonWindows.Add(new SeasonWindowInfo
        {
            SeasonNumber = season,
            EventId = $"{seasonPrefix}_{probeSongId}",
            WindowId = seasonPrefix,
        });
        metaDb.UpsertSeasonWindow(season, $"{seasonPrefix}_{probeSongId}", seasonPrefix);
        pool.ReportSuccess();
        consecutiveFailures = 0;
        Console.WriteLine($"  Season {season,2} ({seasonPrefix}) — exists");
    }
    catch (HttpRequestException)
    {
        pool.ReportFailure();
        consecutiveFailures++;
        Console.WriteLine($"  Season {season,2} ({seasonPrefix}) — not found ({consecutiveFailures}/2)");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        pool.ReportFailure();
        consecutiveFailures++;
        Console.WriteLine($"  Season {season,2} ({seasonPrefix}) — error: {ex.Message}");
    }
    finally
    {
        pool.ReleaseLow(lowToken);
    }
}

Console.WriteLine($"Discovered {seasonWindows.Count} season(s): [{string.Join(", ", seasonWindows.Select(w => w.SeasonNumber))}]");

if (seasonWindows.Count == 0)
{
    Console.Error.WriteLine("No season windows found. Check your token and probe song ID.");
    return 1;
}

// ─── Build a FestivalService with just these songs ──────────

var festivalService = new FortniteFestival.Core.Services.FestivalService(
    (FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
var songsField = typeof(FortniteFestival.Core.Services.FestivalService).GetField("_songs", flags)!;
var dirtyField = typeof(FortniteFestival.Core.Services.FestivalService).GetField("_songsDirty", flags)!;
var dict = (Dictionary<string, FortniteFestival.Core.Song>)songsField.GetValue(festivalService)!;
foreach (var id in songIds)
{
    dict[id] = new FortniteFestival.Core.Song
    {
        track = new FortniteFestival.Core.Track { su = id, tt = id, an = "harness" }
    };
}
dirtyField.SetValue(festivalService, true);

// ─── Run FirstSeenSeasonCalculator ──────────────────────────

var calculator = new FirstSeenSeasonCalculator(
    scraper, persistence, progress,
    loggerFactory.CreateLogger<FirstSeenSeasonCalculator>());

Console.WriteLine();
Console.WriteLine($"Running FirstSeenSeasonCalculator v{FirstSeenSeasonCalculator.CurrentVersion}...");
Console.WriteLine(new string('─', 60));

var sw = Stopwatch.StartNew();
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\nCancellation requested...");
};

int calculated;
try
{
    calculated = await calculator.CalculateAsync(
        festivalService, token, caller, pool, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Run cancelled.");
    calculated = 0;
}

sw.Stop();
Console.WriteLine(new string('─', 60));
Console.WriteLine($"Completed in {sw.Elapsed.TotalSeconds:F1}s — {calculated} song(s) calculated.");
Console.WriteLine();

// ─── Print results ──────────────────────────────────────────

var allFirstSeen = metaDb.GetAllFirstSeenSeasons();
Console.WriteLine($"{"Song ID",-40} {"First Seen",10} {"Estimated",10} {"Version",8} {"Probe Result"}");
Console.WriteLine(new string('─', 100));

foreach (var songId in songIds)
{
    if (allFirstSeen.TryGetValue(songId, out var entry))
    {
        var firstSeen = entry.FirstSeenSeason?.ToString() ?? "null";
        var estimated = entry.EstimatedSeason.ToString();
        var ver = entry.CalculationVersion?.ToString() ?? "?";
        Console.WriteLine($"{songId,-40} {firstSeen,10} {estimated,10} {ver,8}");
    }
    else
    {
        Console.WriteLine($"{songId,-40} {"—",10} {"—",10} {"—",8}  (no result)");
    }
}

Console.WriteLine();
Console.WriteLine($"Pool final DOP: {pool.CurrentDop}");

// ─── Cleanup ────────────────────────────────────────────────

httpClient.Dispose();
handler.Dispose();
pgDataSource.Dispose();
persistence.Dispose();

if (container is not null)
{
    Console.WriteLine("Stopping Testcontainers...");
    await container.StopAsync();
    await container.DisposeAsync();
}

return 0;

// ─── Helpers ────────────────────────────────────────────────

static List<string> ParseListArg(string arg)
{
    if (File.Exists(arg))
    {
        return File.ReadAllLines(arg)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
    }
    return arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();
}
