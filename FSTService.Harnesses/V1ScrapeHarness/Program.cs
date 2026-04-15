using System.Diagnostics;
using FSTService;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using V1ScrapeHarness;

// ═══════════════════════════════════════════════════════════════
//  V1 Scrape Diagnostic Harness
//  Runs the exact V1 alltime scrape pipeline with deep
//  instrumentation at every async boundary: DOP slot wait,
//  rate token wait, HTTP wire time, and persistence.
// ═══════════════════════════════════════════════════════════════

// ─── Parse CLI args ─────────────────────────────────────────

string? token = null, caller = null;
string outputDir = "./harness-output/v1";
string? pgConnStr = null;
int dop = 575;
int initialDop = 4;  // start low, ramp via AIMD slow-start
int rps = 0;  // 0 = unlimited
int numSongs = 0;  // 0 = all songs in file
int songConcurrency = 0;  // 0 = all songs at once (parallel mode default)
int maxPages = 0;  // 0 = unlimited
bool sequential = false;
int pageConcurrency = 10;
bool noPersist = false;
bool verbose = false;
var proxyUrls = new List<string>();
var extraAccounts = new List<(string Token, string Caller)>();
var controlUrls = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--token":            token = args[++i]; break;
        case "--caller":           caller = args[++i]; break;
        case "--numSongs":         numSongs = int.Parse(args[++i]); break;
        case "--output":           outputDir = args[++i]; break;
        case "--pg":               pgConnStr = args[++i]; break;
        case "--dop":              dop = int.Parse(args[++i]); break;
        case "--initial-dop":      initialDop = int.Parse(args[++i]); break;
        case "--rps":              rps = int.Parse(args[++i]); break;
        case "--song-concurrency": songConcurrency = int.Parse(args[++i]); break;
        case "--max-pages":        maxPages = int.Parse(args[++i]); break;
        case "--sequential":       sequential = true; break;
        case "--page-concurrency": pageConcurrency = int.Parse(args[++i]); break;
        case "--no-persist":       noPersist = true; break;
        case "--verbose":          verbose = true; break;
        case "--proxy":            proxyUrls.Add(args[++i]); break;
        case "--account":          var parts = args[++i].Split(':', 2); extraAccounts.Add((parts[0], parts[1])); break;
        case "--control-url":      controlUrls.Add(args[++i]); break;
    }
}

// Resolve songs.txt from known locations
var songsFile = ResolveFile("V1ScrapeHarness", "songs.txt");

if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(caller) || songsFile is null)
{
    Console.Error.WriteLine($"""
        Usage: V1ScrapeHarness --token <epic-token> --caller <account-id>

        Required:
          --token     Epic Bearer access token
          --caller    Caller's Epic account ID

        Optional:
          --numSongs <int>         Number of random songs to process (default: all)
          --dop <int>              Max degree of parallelism (default: 575)
          --initial-dop <int>     Starting DOP, ramps to --dop via slow-start (default: 4)
          --rps <int>              Max requests per second (default: 0 = unlimited)
          --song-concurrency <int> Max songs scraped concurrently (default: all)
          --max-pages <int>        Max pages per leaderboard (default: unlimited)
          --sequential             Use sequential scrape mode
          --page-concurrency <int> Pages per instrument in sequential mode (default: 10)
          --output <dir>           Output directory (default: ./harness-output/v1)
          --pg <conn-string>       PostgreSQL connection string (default: Testcontainers)
          --no-persist             Skip persistence (measure fetch-only)
          --verbose                Debug-level logging
          --proxy <url>            HTTP proxy URL (repeatable for round-robin rotation)
          --account <token:id>     Extra account for token rotation (repeatable)
        {(songsFile is null ? "\n        ERROR: Could not find songs.txt" : "")}
        """);
    return 1;
}

var allSongIds = ParseSongsFile(songsFile);
var songIds = SelectSongs(allSongIds, numSongs);

Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  V1 Scrape Diagnostic Harness");
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine();
Console.WriteLine($"Songs:            {songIds.Count}{(numSongs > 0 ? $" (random subset of {allSongIds.Count})" : "")}");
Console.WriteLine($"DOP:              {initialDop} → {dop} (slow-start)");
Console.WriteLine($"RPS:              {(rps == 0 ? "unlimited" : rps)}");
Console.WriteLine($"Mode:             {(sequential ? "Sequential" : "Parallel")}");
Console.WriteLine($"Song concurrency: {(songConcurrency == 0 ? "all" : songConcurrency)}");
Console.WriteLine($"Max pages:        {(maxPages == 0 ? "unlimited" : maxPages)}");
Console.WriteLine($"Persistence:      {(noPersist ? "OFF" : "ON")}");
Console.WriteLine($"Proxies:          {(proxyUrls.Count == 0 ? "NONE (direct)" : $"{proxyUrls.Count} (round-robin)")}");
Console.WriteLine($"Accounts:         {1 + extraAccounts.Count}");
Console.WriteLine($"Output:           {Path.GetFullPath(outputDir)}");
Console.WriteLine();

// ─── ThreadPool tuning ──────────────────────────────────────

int minThreads = Math.Max(200, dop);
ThreadPool.GetMinThreads(out var prevWorker, out var prevIo);
ThreadPool.SetMinThreads(minThreads, minThreads);
Console.WriteLine($"ThreadPool.SetMinThreads({minThreads}, {minThreads}) — was ({prevWorker}, {prevIo})");

// ─── Logging ────────────────────────────────────────────────

using var loggerFactory = LoggerFactory.Create(b =>
{
    b.AddConsole();
    b.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
    b.AddFilter("FSTService", verbose ? LogLevel.Debug : LogLevel.Warning);
    b.AddFilter("Npgsql", LogLevel.Warning);
});

// ─── Database ───────────────────────────────────────────────

PostgreSqlContainer? container = null;
NpgsqlDataSource pgDataSource;

if (noPersist)
{
    Console.WriteLine("Persistence disabled — skipping database setup.");
    pgDataSource = null!;
}
else if (pgConnStr is not null)
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

GlobalLeaderboardPersistence? persistence = null;
if (!noPersist)
{
    await DatabaseInitializer.EnsureSchemaAsync(pgDataSource);
    var metaDb = new MetaDatabase(pgDataSource, loggerFactory.CreateLogger<MetaDatabase>());
    var featureOptions = Options.Create(new FeatureOptions());
    persistence = new GlobalLeaderboardPersistence(
        metaDb, loggerFactory,
        loggerFactory.CreateLogger<GlobalLeaderboardPersistence>(),
        pgDataSource, featureOptions);
    persistence.Initialize();
    Console.WriteLine("Database schema created + persistence initialized.");
}

// ─── Instrumentation ────────────────────────────────────────

var harnessStopwatch = Stopwatch.StartNew();
var collector = new TimingCollector();

// HttpClient with instrumented handler wrapping SocketsHttpHandler (or proxy rotator)
Func<SocketsHttpHandler> handlerFactory = () => new SocketsHttpHandler
{
    MaxConnectionsPerServer = 2048,
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    EnableMultipleHttp2Connections = true,
    AutomaticDecompression = System.Net.DecompressionMethods.All,
};
HttpMessageHandler innerHandler;
if (proxyUrls.Count > 0)
{
    Console.WriteLine($"Proxy rotation enabled: {proxyUrls.Count} proxies");
    foreach (var p in proxyUrls) Console.WriteLine($"  → {p}");

    // Build account list for pinning: primary account + extra accounts
    List<(string Token, string AccountId)>? pinAccounts = null;
    if (extraAccounts.Count > 0)
    {
        pinAccounts = [(token, caller), .. extraAccounts];
        Console.WriteLine($"Token pinning: {pinAccounts.Count} accounts interleaved across {proxyUrls.Count} slots");
        for (int si = 0; si < proxyUrls.Count; si++)
        {
            var acct = pinAccounts[si % pinAccounts.Count];
            Console.WriteLine($"  Slot {si}: {proxyUrls[si]} → {acct.AccountId[..8]}...");
        }
    }

    innerHandler = new RoundRobinProxyHandler(proxyUrls, pinAccounts, handlerFactory,
        loggerFactory.CreateLogger("ProxyRotation"),
        controlUrls.Count > 0 ? controlUrls : null);
}
else
{
    innerHandler = handlerFactory();
}
var instrumentedHandler = new InstrumentedHttpHandler(collector, harnessStopwatch, innerHandler);
var httpClient = new HttpClient(instrumentedHandler) { Timeout = TimeSpan.FromSeconds(30) };

// Progress tracker
var progress = new ScrapeProgressTracker();

// Scraper
var scraper = new GlobalLeaderboardScraper(
    httpClient, progress,
    loggerFactory.CreateLogger<GlobalLeaderboardScraper>());

// SharedDopPool — starts at initialDop, ramps to dop via AIMD slow-start
var poolLogger = loggerFactory.CreateLogger("SharedDopPool");
initialDop = Math.Clamp(initialDop, 1, dop);
using var pool = new SharedDopPool(initialDop, minDop: Math.Min(4, dop), maxDop: dop,
    lowPriorityPercent: 10, poolLogger, rps);

// Wire timing callbacks into the limiter
pool.Limiter.OnSlotAcquired = collector.RecordSlotWait;
pool.Limiter.OnRateTokenAcquired = collector.RecordRateTokenWait;

Console.WriteLine("Instrumentation wired.");
Console.WriteLine();

// ─── Build scrape requests ──────────────────────────────────

var instruments = GlobalLeaderboardScraper.AllInstruments;
var scrapeRequests = songIds.Select(songId =>
    new GlobalLeaderboardScraper.SongScrapeRequest
    {
        SongId = songId,
        Instruments = instruments,
        Label = songId,  // harness uses song ID as label
    }).ToList();

int totalLeaderboards = scrapeRequests.Sum(r => r.Instruments.Count);
progress.BeginPass(totalLeaderboards, scrapeRequests.Count, 0);

Console.WriteLine($"Built {scrapeRequests.Count} song requests × {instruments.Count} instruments = {totalLeaderboards} leaderboards");
Console.WriteLine(new string('─', 60));

// ─── Start background instrumentation ───────────────────────

using var sampler = new DopSampler(pool.Limiter, harnessStopwatch, intervalMs: 25);
using var cts = new CancellationTokenSource();

// Handle Ctrl+C gracefully
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\nCancellation requested...");
};

sampler.Start();

using var liveReporter = new LiveReporter(
    pool.Limiter, collector, progress, harnessStopwatch,
    configuredDop: dop, configuredRps: rps, intervalMs: 3000,
    executor: scraper.Executor,
    totalSongs: songIds.Count);
liveReporter.Start();

// ─── Persistence setup ──────────────────────────────────────

if (persistence is not null)
    persistence.StartPageWriters(ct: cts.Token);

// Per-page persistence callback — matches production ScrapeOrchestrator exactly.
// Pages stream into the bounded channel during the fetch loop, providing backpressure.
Func<string, string, IReadOnlyList<LeaderboardEntry>, ValueTask>? onPageScraped = null;
if (!noPersist && persistence is not null)
{
    onPageScraped = async (songId, instrument, entries) =>
    {
        var enqueueStart = harnessStopwatch.ElapsedMilliseconds;
        await persistence.EnqueuePageAsync(songId, instrument, entries, cts.Token);
        var enqueueMs = harnessStopwatch.ElapsedMilliseconds - enqueueStart;

        collector.RecordPersistTiming(new PersistTimingSample(
            TimestampMs: enqueueStart,
            SongId: songId,
            EntryCount: entries.Count,
            EnqueueMs: enqueueMs,
            TotalMs: enqueueMs));
    };
}

// ─── Run the scrape ─────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("Starting V1 scrape...");

// Reset CDN state (matches ScrapeOrchestrator behavior)
scraper.ResetCdnState();
pool.ResetDop();

Dictionary<string, List<GlobalLeaderboardResult>> allResults;
try
{
    if (extraAccounts.Count == 0)
    {
        // Single account — original behavior
        allResults = await scraper.ScrapeManySongsAsync(
            scrapeRequests,
            token,
            caller,
            maxConcurrency: dop,
            onSongComplete: null,
            ct: cts.Token,
            maxPages: maxPages,
            sequential: sequential,
            pageConcurrency: pageConcurrency,
            songConcurrency: songConcurrency == 0 ? 1 : songConcurrency,
            maxRequestsPerSecond: rps,
            sharedLimiter: pool.Limiter,
            deferDeepScrape: false,
            onPageScraped: onPageScraped);
    }
    else
    {
        // Multi-account: split songs across N accounts, run concurrently sharing the DOP pool
        var allAccounts = new List<(string Token, string Caller)> { (token, caller) };
        allAccounts.AddRange(extraAccounts);
        int n = allAccounts.Count;
        Console.WriteLine($"Multi-account mode: splitting {scrapeRequests.Count} songs across {n} accounts");

        var chunks = new List<List<GlobalLeaderboardScraper.SongScrapeRequest>>();
        for (int ci = 0; ci < n; ci++)
            chunks.Add([]);
        for (int si = 0; si < scrapeRequests.Count; si++)
            chunks[si % n].Add(scrapeRequests[si]);

        var tasks = new List<Task<Dictionary<string, List<GlobalLeaderboardResult>>>>();
        for (int ai = 0; ai < n; ai++)
        {
            var (acctToken, acctCaller) = allAccounts[ai];
            var chunk = chunks[ai];
            Console.WriteLine($"  Account {ai + 1}: {acctCaller[..8]}... → {chunk.Count} songs");
            tasks.Add(scraper.ScrapeManySongsAsync(
                chunk,
                acctToken,
                acctCaller,
                maxConcurrency: dop,
                onSongComplete: null,
                ct: cts.Token,
                maxPages: maxPages,
                sequential: sequential,
                pageConcurrency: pageConcurrency,
                songConcurrency: songConcurrency == 0 ? 1 : songConcurrency,
                maxRequestsPerSecond: rps,
                sharedLimiter: pool.Limiter,
                deferDeepScrape: false,
                onPageScraped: onPageScraped));
        }

        var results = await Task.WhenAll(tasks);
        allResults = new();
        foreach (var r in results)
            foreach (var kv in r)
                allResults[kv.Key] = kv.Value;
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Scrape cancelled.");
    allResults = new();
}
catch (FSTService.Scraping.CdnBlockedException cdnEx)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\n*** CDN BLOCK: {cdnEx.Message}");
    Console.WriteLine("    Epic's CDN blocked all requests. This IS the production bug.");
    Console.WriteLine("    Report will contain partial data up to the block point.");
    Console.ResetColor();
    allResults = new();
}

harnessStopwatch.Stop();
await liveReporter.StopAsync();
await sampler.StopAsync();

// Drain persistence writers
if (persistence is not null)
{
    Console.WriteLine("Draining persistence writers...");
    await persistence.DrainPageWritersAsync();
}

var elapsed = harnessStopwatch.Elapsed;

Console.WriteLine(new string('─', 60));
Console.WriteLine($"Scrape completed in {elapsed.TotalSeconds:F1}s");
Console.WriteLine();

// ─── Collect result stats ───────────────────────────────────

int songsCompleted = allResults.Count(kvp => kvp.Value.Any(r => r.EntriesCount > 0));
int leaderboardsCompleted = allResults.Values.SelectMany(v => v).Count(r => r.EntriesCount > 0);
long totalBytes = allResults.Values.SelectMany(v => v).Sum(r => r.BytesReceived);
int totalPages = allResults.Values.SelectMany(v => v).Sum(r => r.PagesScraped);

// ─── Generate report ────────────────────────────────────────

var report = new HarnessReport(
    sampler.Samples,
    collector,
    elapsed,
    configuredDop: dop,
    configuredRps: rps,
    songCount: songIds.Count,
    maxPages: maxPages,
    sequential: sequential,
    noPersist: noPersist,
    totalWireSends: scraper.Executor?.TotalHttpSends ?? 0,
    cdnBlocks: scraper.Executor?.CdnBlocksDetected ?? 0,
    songsCompleted: songsCompleted,
    leaderboardsCompleted: leaderboardsCompleted,
    totalBytes: totalBytes,
    totalPages: totalPages);

report.WriteAll(outputDir);

// ─── Cleanup ────────────────────────────────────────────────

httpClient.Dispose();
instrumentedHandler.Dispose();
innerHandler.Dispose();
persistence?.Dispose();

if (container is not null)
{
    Console.WriteLine("Stopping Testcontainers...");
    await container.StopAsync();
    await container.DisposeAsync();
}

Console.WriteLine("Done.");
return 0;

// ─── Helpers ────────────────────────────────────────────────

static List<string> ParseSongsFile(string path)
{
    return File.ReadAllLines(path)
        .Select(l => l.Trim())
        .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#'))
        .ToList();
}

static List<string> SelectSongs(List<string> allSongs, int numSongs)
{
    if (numSongs <= 0 || numSongs >= allSongs.Count)
        return allSongs;

    // Fisher-Yates shuffle, take first numSongs
    var rng = Random.Shared;
    var shuffled = new List<string>(allSongs);
    for (int i = shuffled.Count - 1; i > 0; i--)
    {
        int j = rng.Next(i + 1);
        (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
    }
    return shuffled.GetRange(0, numSongs);
}

static string? ResolveFile(string projectDir, string fileName)
{
    // From build output (dotnet run)
    var fromBin = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", projectDir, fileName);
    if (File.Exists(fromBin)) return Path.GetFullPath(fromBin);
    // From repo root
    var fromRoot = Path.Combine(projectDir, fileName);
    if (File.Exists(fromRoot)) return Path.GetFullPath(fromRoot);
    // Current directory
    if (File.Exists(fileName)) return Path.GetFullPath(fileName);
    return null;
}
