using System.Diagnostics;
using FortniteFestival.Core.Scraping;
using FSTService.Scraping;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════
//  Band Scrape Harness
//  Tests the BandPageFetcher production flow:
//    Phase 1: page 0 for all (song, bandType) combos
//    Phase 2: remaining pages as flat parallel pool
//  Spools to disk, reports timing/throughput/error rates.
//  No PG persistence — measures network fetch only.
// ═══════════════════════════════════════════════════════════════

string? token = null, caller = null;
int numSongs = 20;
int maxPages = 10;
int dop = 512;
int rps = 0;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--token":     token = args[++i]; break;
        case "--caller":    caller = args[++i]; break;
        case "--numSongs":  numSongs = int.Parse(args[++i]); break;
        case "--max-pages": maxPages = int.Parse(args[++i]); break;
        case "--dop":       dop = int.Parse(args[++i]); break;
        case "--rps":       rps = int.Parse(args[++i]); break;
    }
}

// Load songs from songs.txt (same format as V1ScrapeHarness)
var songsFile = ResolveFile("BandScrapeHarness", "songs.txt")
    ?? ResolveFile("V1ScrapeHarness", "songs.txt");

if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(caller) || songsFile is null)
{
    Console.Error.WriteLine("""
        Usage: BandScrapeHarness --token <epic-token> --caller <account-id>

        Required:
          --token     Epic Bearer access token
          --caller    Caller's Epic account ID

        Optional:
          --numSongs <int>    Songs to test (default: 20)
          --max-pages <int>   Max pages per leaderboard (default: 10)
          --dop <int>         Max DOP (default: 512)
          --rps <int>         Max requests/sec (default: unlimited)
        """);

    if (songsFile is null)
        Console.Error.WriteLine("        ERROR: Could not find songs.txt");
    return 1;
}

// Parse song IDs
var allSongIds = File.ReadAllLines(songsFile)
    .Select(l => l.Trim())
    .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#'))
    .Take(numSongs)
    .ToList();

Console.WriteLine($"Band Scrape Harness: {allSongIds.Count} songs, maxPages={maxPages}, DOP={dop}");

// Set up logging
using var logFactory = LoggerFactory.Create(b =>
{
    b.SetMinimumLevel(LogLevel.Information);
    b.AddSimpleConsole(o => { o.TimestampFormat = "HH:mm:ss "; o.SingleLine = true; });
});
var log = logFactory.CreateLogger("BandHarness");

// Set up HTTP + scraper
var handler = new HttpClientHandler { MaxConnectionsPerServer = dop };
var httpClient = new HttpClient(handler);

var scraperLog = logFactory.CreateLogger<GlobalLeaderboardScraper>();
var progress = new ScrapeProgressTracker();

var scraper = new GlobalLeaderboardScraper(httpClient, progress, scraperLog);

// Set up shared DOP pool (band uses low-priority slots)
var pool = new SharedDopPool(
    dop, minDop: 4, maxDop: dop, lowPriorityPercent: 100,
    logFactory.CreateLogger("Pool"),
    maxRequestsPerSecond: rps);

// Set up spool (no-op flush — we just measure fetch throughput)
var spoolDir = Path.Combine(Path.GetTempPath(), $"band_harness_{Guid.NewGuid():N}");
long spooledEntries = 0;
await using var spool = new SpoolWriter<BandLeaderboardEntry>(
    log, "harness",
    serialize: BandSpoolWriterFactory.TestSerialize,
    deserialize: BandSpoolWriterFactory.TestDeserialize,
    flush: (_, batch) => { /* no-op */ },
    baseDirectory: spoolDir);

// Run the fetcher
var bandTypes = new List<string> { "Band_Duets", "Band_Trios", "Band_Quad" };
var fetcher = new BandPageFetcher(scraper.Executor, pool, spool, progress, log);

var sw = Stopwatch.StartNew();
try
{
    await fetcher.FetchAllAsync(allSongIds, bandTypes, token, caller, maxPages, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fetch failed: {ex.Message}");
}
sw.Stop();

// Report
Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════");
Console.WriteLine($"  Songs:      {allSongIds.Count}");
Console.WriteLine($"  Band types: {bandTypes.Count}");
Console.WriteLine($"  Max pages:  {maxPages}");
Console.WriteLine($"  DOP:        {dop}");
Console.WriteLine($"───────────────────────────────────────────");
Console.WriteLine($"  Total pages:    {Interlocked.Read(ref fetcher.TotalPages):N0}");
Console.WriteLine($"  Total entries:  {Interlocked.Read(ref fetcher.TotalEntries):N0}");
Console.WriteLine($"  Total requests: {Interlocked.Read(ref fetcher.TotalRequests):N0}");
Console.WriteLine($"  Songs w/ data:  {fetcher.SongsWithData}");
Console.WriteLine($"  Spool records:  {spool.RecordCount:N0}");
Console.WriteLine($"  Spool bytes:    {spool.TotalBytesWritten:N0}");
Console.WriteLine($"  Wall time:      {sw.Elapsed.TotalSeconds:F1}s");
Console.WriteLine($"  RPS:            {Interlocked.Read(ref fetcher.TotalRequests) / sw.Elapsed.TotalSeconds:F0}");
Console.WriteLine("═══════════════════════════════════════════");

// Cleanup
spool.Complete();
return 0;

// ─── Helpers ────────────

static string? ResolveFile(string project, string filename)
{
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, filename),
        Path.Combine(Directory.GetCurrentDirectory(), filename),
        Path.Combine(Directory.GetCurrentDirectory(), $"FSTService.Harnesses/{project}/{filename}"),
        Path.Combine(Directory.GetCurrentDirectory(), $"../{project}/{filename}"),
    };
    return candidates.FirstOrDefault(File.Exists);
}
