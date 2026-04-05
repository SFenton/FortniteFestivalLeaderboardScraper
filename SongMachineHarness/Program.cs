using System.Diagnostics;
using System.Text.Json;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using Npgsql;
using SongMachineHarness;
using Testcontainers.PostgreSql;

// ═══════════════════════════════════════════════════════════════
//  SongMachine DOP Utilization Harness
//  Runs SongProcessingMachine against real Epic servers with
//  instrumented querier + DOP pool sampling. DOP=RPS=575 hardcoded.
// ═══════════════════════════════════════════════════════════════

const int Dop = 575;
const int Rps = 575;
const int SongDop = 575;
const int DefaultBatchSize = 100;

// ─── ThreadPool tuning ──────────────────────────────────────
// Sync DB I/O in SongProcessingMachine blocks ThreadPool threads.
// Default min threads is low (~Environment.ProcessorCount), causing
// slow growth ramp when many songs need concurrent DB work.
ThreadPool.GetMinThreads(out var prevWorker, out var prevIo);
ThreadPool.SetMinThreads(200, 200);
Console.WriteLine($"ThreadPool.SetMinThreads(200, 200) — was ({prevWorker}, {prevIo})");

// ─── Parse CLI args ─────────────────────────────────────────

string? token = null, caller = null, accountsArg = null, songsArg = null;
string outputDir = "./harness-output";
string? pgConnStr = null;
int[] seasons = [];
bool resume = false;
bool noBatch = false;
int? initialDopOverride = null;
int? maxDopOverride = null;
int? maxRpsOverride = null;
int batchSize = DefaultBatchSize;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--token":       token = args[++i]; break;
        case "--caller":      caller = args[++i]; break;
        case "--accounts":    accountsArg = args[++i]; break;
        case "--songs":       songsArg = args[++i]; break;
        case "--seasons":     seasons = args[++i].Split(',').Select(int.Parse).ToArray(); break;
        case "--output":      outputDir = args[++i]; break;
        case "--pg":          pgConnStr = args[++i]; break;
        case "--resume":      resume = true; break;
        case "--initial-dop": initialDopOverride = int.Parse(args[++i]); break;
        case "--max-dop":     maxDopOverride = int.Parse(args[++i]); break;
        case "--max-rps":     maxRpsOverride = int.Parse(args[++i]); break;
        case "--batch-size":  batchSize = int.Parse(args[++i]); break;
        case "--no-batch":   noBatch = true; break;
    }
}

if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(caller) ||
    string.IsNullOrEmpty(accountsArg) || string.IsNullOrEmpty(songsArg))
{
    Console.Error.WriteLine("""
        Usage: SongMachineHarness --token <epic-token> --caller <account-id>
               --accounts <csv-or-file> --songs <csv-or-file>
               [--seasons <csv-ints>] [--output <dir>] [--pg <conn-string>]

        Required:
          --token     Epic Bearer access token
          --caller    Caller's Epic account ID
          --accounts  Comma-separated account IDs or path to file (one per line)
          --songs     Comma-separated song IDs or path to file (one per line)

        Optional:
          --seasons   Comma-separated season numbers for seasonal queries
          --output    Output directory (default: ./harness-output)
          --pg        PostgreSQL connection string (default: Testcontainers)
          --resume    Resume from previous run's DOP state (reads dop-state.json)
          --initial-dop  Override starting DOP (default: 575, overrides --resume)
          --max-dop      Maximum DOP ceiling (default: 575)
          --max-rps      Maximum requests per second (default: 575)
          --batch-size   Accounts per API call (default: 100)
          --no-batch    Individual lookups (1 account per API call)
        """);
    return 1;
}

var accountIds = ParseListArg(accountsArg);
var songIds = ParseListArg(songsArg);

Console.WriteLine($"Songs:    {songIds.Count}");
Console.WriteLine($"Accounts: {accountIds.Count}");
Console.WriteLine($"Seasons:  [{string.Join(",", seasons)}]");
if (noBatch) batchSize = 1;
int maxDop = maxDopOverride ?? Dop;
int maxRps = maxRpsOverride ?? Rps;
Console.WriteLine($"DOP:      {maxDop}  RPS: {maxRps}  SongDOP: {SongDop}  Batch: {batchSize}{(noBatch ? " (no-batch)" : "")}");

// ─── Resume state from previous run ─────────────────────────

int initialDop = initialDopOverride ?? maxDop;
int initialSsthresh = 0;
var stateFile = Path.Combine(outputDir, "dop-state.json");

if (initialDopOverride.HasValue)
{
    Console.WriteLine($"Initial DOP override: {initialDop}");
}
else if (resume && File.Exists(stateFile))
{
    try
    {
        var stateJson = JsonDocument.Parse(File.ReadAllText(stateFile));
        var root = stateJson.RootElement;
        initialDop = root.GetProperty("currentDop").GetInt32();
        initialSsthresh = root.GetProperty("ssthresh").GetInt32();
        var ts = root.GetProperty("timestamp").GetString();
        var sends = root.GetProperty("totalHttpSends").GetInt64();
        Console.WriteLine($"Resuming from previous run: DOP={initialDop}, ssthresh={initialSsthresh}, sends={sends} (at {ts})");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Warning: failed to read {stateFile}: {ex.Message} — starting fresh");
    }
}
else if (resume)
{
    Console.WriteLine("--resume specified but no dop-state.json found — starting fresh");
}

Console.WriteLine();

// ─── Logging ────────────────────────────────────────────────

using var loggerFactory = LoggerFactory.Create(b =>
{
    b.AddConsole();
    b.SetMinimumLevel(LogLevel.Information);
    b.AddFilter("FSTService", LogLevel.Warning);    // suppress noisy persistence logs
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

// Create schema
await DatabaseInitializer.EnsureSchemaAsync(pgDataSource);
Console.WriteLine("Database schema created.");

// ─── Wire up dependencies ───────────────────────────────────

var metaDb = new MetaDatabase(pgDataSource, loggerFactory.CreateLogger<MetaDatabase>());
var persistence = new GlobalLeaderboardPersistence(
    metaDb, loggerFactory,
    loggerFactory.CreateLogger<GlobalLeaderboardPersistence>(),
    pgDataSource);
persistence.Initialize();
Console.WriteLine("Persistence initialized.");

var progress = new ScrapeProgressTracker();
var resultProcessor = new BatchResultProcessor(persistence, loggerFactory.CreateLogger<BatchResultProcessor>());

// HttpClient — matching FSTService's production config
var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 2048,
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    EnableMultipleHttp2Connections = true,
    AutomaticDecompression = System.Net.DecompressionMethods.All,
};
var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

var realScraper = new GlobalLeaderboardScraper(
    httpClient, progress,
    loggerFactory.CreateLogger<GlobalLeaderboardScraper>());

// SharedDopPool — DOP=575, RPS=575 (or resumed state)
var poolLogger = loggerFactory.CreateLogger("SharedDopPool");
using var pool = new SharedDopPool(initialDop, minDop: Math.Min(4, maxDop), maxDop: maxDop,
    lowPriorityPercent: 20, poolLogger, maxRps, initialSsthresh: initialSsthresh);

if (resume && (initialDop != maxDop || initialSsthresh != 0))
    Console.WriteLine($"Pool initialized: DOP={pool.CurrentDop}, ssthresh={pool.Limiter.SlowStartThreshold}");

// Instrumented querier — wraps the real scraper with per-call timing
var harnessStopwatch = new Stopwatch();
var instrumentedQuerier = new InstrumentedQuerier(realScraper, pool.Limiter, harnessStopwatch, realScraper.Executor);

// SongProcessingMachine — using instrumented querier
var notificationService = new NotificationService(loggerFactory.CreateLogger<NotificationService>());
var syncTracker = new UserSyncProgressTracker(notificationService, loggerFactory.CreateLogger<UserSyncProgressTracker>());
var machine = new SongProcessingMachine(
    instrumentedQuerier, resultProcessor, persistence, progress, syncTracker,
    loggerFactory.CreateLogger<SongProcessingMachine>());

// ─── Build work items ───────────────────────────────────────

var users = accountIds.Select(id => new UserWorkItem
{
    AccountId = id,
    AllTimeNeeded = true,
    Purposes = WorkPurpose.PostScrape,
    SeasonsNeeded = new HashSet<int>(seasons),
}).ToList();

var seasonWindows = seasons.Select(s => new SeasonWindowInfo
{
    SeasonNumber = s,
}).ToList();

// ─── Run with instrumentation ───────────────────────────────

Console.WriteLine();
Console.WriteLine("Starting SongProcessingMachine...");
Console.WriteLine(new string('─', 55));

using var sampler = new DopSampler(pool.Limiter, intervalMs: 25);
using var cts = new CancellationTokenSource();

// Handle Ctrl+C gracefully
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\nCancellation requested...");
};

harnessStopwatch.Start();
sampler.Start();

using var liveReporter = new LiveReporter(
    pool.Limiter, instrumentedQuerier, harnessStopwatch,
    configuredDop: maxDop, configuredRps: maxRps, intervalMs: 3000,
    executor: realScraper.Executor);
liveReporter.Start();

SongProcessingMachine.MachineResult result;
try
{
    result = await machine.RunAsync(
        songIds,
        users,
        seasonWindows,
        token,
        caller,
        pool,
        isHighPriority: true,
        batchSize: batchSize,
        reportProgress: true,
        maxConcurrentSongs: SongDop,
        ct: cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Run cancelled.");
    result = new SongProcessingMachine.MachineResult();
}

harnessStopwatch.Stop();
await liveReporter.StopAsync();
await sampler.StopAsync();
var elapsed = harnessStopwatch.Elapsed;

Console.WriteLine(new string('─', 55));
Console.WriteLine($"Machine completed in {elapsed.TotalSeconds:F1}s");
Console.WriteLine();

// ─── Generate report ────────────────────────────────────────

var report = new HarnessReport(
    sampler.Samples,
    instrumentedQuerier.Events,
    result,
    elapsed,
    configuredDop: Dop,
    configuredRps: Rps,
    songDop: SongDop,
    batchSize: batchSize,
    songCount: songIds.Count,
    accountCount: accountIds.Count,
    seasons: seasons,
    executor: realScraper.Executor);

report.WriteAll(outputDir);

// ─── Save DOP state for --resume ─────────────────────────────

var dopState = new
{
    currentDop = pool.CurrentDop,
    ssthresh = pool.Limiter.SlowStartThreshold,
    totalHttpSends = realScraper.Executor?.TotalHttpSends ?? 0,
    timestamp = DateTimeOffset.UtcNow.ToString("o"),
};
File.WriteAllText(stateFile, JsonSerializer.Serialize(dopState, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"  dop-state.json    (DOP={dopState.currentDop}, ssthresh={dopState.ssthresh}, sends={dopState.totalHttpSends})");

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
    // If the arg looks like a file path and the file exists, read lines from it
    if (File.Exists(arg))
    {
        return File.ReadAllLines(arg)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
    }

    // Otherwise treat as comma-separated
    return arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();
}
