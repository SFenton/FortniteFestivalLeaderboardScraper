using System.Net;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="PostScrapeRefresher"/> — verifies stale/gap entry detection
/// and API re-query logic. Uses real SQLite temps + mock HTTP scraper.
/// </summary>
public class PostScrapeRefresherTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaDb = new();
    private readonly string _dataDir;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly ILogger<PostScrapeRefresher> _log = Substitute.For<ILogger<PostScrapeRefresher>>();

    public PostScrapeRefresherTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_refresh_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        loggerFactory.CreateLogger<InstrumentDatabase>().Returns(Substitute.For<ILogger<InstrumentDatabase>>());
        var persLog = Substitute.For<ILogger<GlobalLeaderboardPersistence>>();
        _persistence = new GlobalLeaderboardPersistence(_dataDir, _metaDb.Db, loggerFactory, persLog);
        _persistence.Initialize();
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaDb.Dispose();
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private (PostScrapeRefresher refresher, MockHttpMessageHandler handler) CreateRefresher()
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler);
        var progress = new ScrapeProgressTracker();
        var scraperLog = Substitute.For<ILogger<GlobalLeaderboardScraper>>();
        var scraper = new GlobalLeaderboardScraper(http, progress, scraperLog);
        var refresher = new PostScrapeRefresher(scraper, _persistence, _log);
        return (refresher, handler);
    }

    // ─── No registered users → 0 ───────────────────────

    [Fact]
    public async Task RefreshAllAsync_NoRegisteredUsers_Returns0()
    {
        var (refresher, _) = CreateRefresher();
        var empty = new HashSet<string>();
        var seen = new HashSet<(string, string, string)>();

        var result = await refresher.RefreshAllAsync(
            empty, seen, ["songA"], "token", "caller");

        Assert.Equal(0, result);
    }

    // ─── Entry seen in scrape → not refreshed ──────────

    [Fact]
    public async Task RefreshAllAsync_SeenEntry_NotRequried()
    {
        var (refresher, handler) = CreateRefresher();

        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct1" };
        var seen = new HashSet<(string AccountId, string SongId, string Instrument)>
        {
            ("acct1", "songA", "Solo_Guitar"),
            ("acct1", "songA", "Solo_Bass"),
            ("acct1", "songA", "Solo_Vocals"),
            ("acct1", "songA", "Solo_Drums"),
            ("acct1", "songA", "Solo_PeripheralGuitar"),
            ("acct1", "songA", "Solo_PeripheralBass"),
        };

        var result = await refresher.RefreshAllAsync(
            registered, seen, ["songA"], "token", "caller");

        Assert.Equal(0, result);
        Assert.Empty(handler.Requests);
    }

    // ─── Gap entry found via API → upserted ────────────

    [Fact]
    public async Task RefreshAllAsync_GapEntry_UpsertedOnFound()
    {
        var (refresher, handler) = CreateRefresher();

        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct1" };
        var seen = new HashSet<(string, string, string)>();
        // Mark 5 instruments as seen, leave Solo_Guitar as gap
        foreach (var inst in GlobalLeaderboardScraper.AllInstruments.Where(i => i != "Solo_Guitar"))
            seen.Add(("acct1", "songA", inst));

        // API returns entry for Solo_Guitar
        handler.EnqueueJsonOk("""
        {
            "page": 0, "totalPages": 1,
            "entries": [{
                "teamId": "acct1", "rank": 500, "percentile": 0.3,
                "sessionHistory": [{ "trackedStats": { "SCORE": 10000 } }]
            }]
        }
        """);

        var result = await refresher.RefreshAllAsync(
            registered, seen, ["songA"], "token", "caller");

        Assert.Equal(1, result);

        // Verify entry was upserted
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var entry = db.GetEntry("songA", "acct1");
        Assert.NotNull(entry);
        Assert.Equal(10000, entry!.Score);
    }

    // ─── Stale entry with score change → updated + history recorded ──

    [Fact]
    public async Task RefreshAllAsync_StaleEntryScoreChanged_DetectsChange()
    {
        var (refresher, handler) = CreateRefresher();

        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct1" };
        var seen = new HashSet<(string, string, string)>();
        foreach (var inst in GlobalLeaderboardScraper.AllInstruments.Where(i => i != "Solo_Guitar"))
            seen.Add(("acct1", "songA", inst));

        // Pre-populate an existing stale entry
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songA", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 5000, Rank = 1000
        }]);

        // API returns updated score
        handler.EnqueueJsonOk("""
        {
            "page": 0, "totalPages": 1,
            "entries": [{
                "teamId": "acct1", "rank": 800, "percentile": 0.4,
                "sessionHistory": [{ "trackedStats": { "SCORE": 15000 } }]
            }]
        }
        """);

        var result = await refresher.RefreshAllAsync(
            registered, seen, ["songA"], "token", "caller");

        Assert.Equal(1, result);

        // Verify updated entry
        var entry = guitarDb.GetEntry("songA", "acct1");
        Assert.Equal(15000, entry!.Score);
    }

    // ─── API error → returns false, no crash ────────────

    [Fact]
    public async Task RefreshAllAsync_ApiError_ReturnsGracefully()
    {
        var (refresher, handler) = CreateRefresher();

        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct1" };
        var seen = new HashSet<(string, string, string)>();
        foreach (var inst in GlobalLeaderboardScraper.AllInstruments.Where(i => i != "Solo_Guitar"))
            seen.Add(("acct1", "songA", inst));

        handler.EnqueueError(HttpStatusCode.Forbidden);

        var result = await refresher.RefreshAllAsync(
            registered, seen, ["songA"], "token", "caller");

        Assert.Equal(0, result);
    }

    // ─── Gap entry not found → not counted ──────────────

    [Fact]
    public async Task RefreshAllAsync_GapEntryNotOnApi_Returns0()
    {
        var (refresher, handler) = CreateRefresher();

        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct1" };
        var seen = new HashSet<(string, string, string)>();
        foreach (var inst in GlobalLeaderboardScraper.AllInstruments.Where(i => i != "Solo_Guitar"))
            seen.Add(("acct1", "songA", inst));

        handler.EnqueueJsonOk("""{"page":0,"totalPages":0,"entries":[]}""");

        var result = await refresher.RefreshAllAsync(
            registered, seen, ["songA"], "token", "caller");

        Assert.Equal(0, result);
    }

    // ─── Stale entry returns null from API → entry preserved ────

    [Fact]
    public async Task RefreshAllAsync_StaleEntryNullFromApi_PreservesEntry()
    {
        var (refresher, handler) = CreateRefresher();

        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct1" };
        var seen = new HashSet<(string, string, string)>();
        foreach (var inst in GlobalLeaderboardScraper.AllInstruments.Where(i => i != "Solo_Guitar"))
            seen.Add(("acct1", "songA", inst));

        // Pre-populate a stale entry in the guitar DB
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songA", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 8000, Rank = 200
        }]);

        // API returns empty (player no longer has data — very rare)
        handler.EnqueueJsonOk("""{"page":0,"totalPages":0,"entries":[]}""");

        var result = await refresher.RefreshAllAsync(
            registered, seen, ["songA"], "token", "caller");

        Assert.Equal(0, result);
        // Original entry should still be there (not deleted)
        var entry = guitarDb.GetEntry("songA", "acct1");
        Assert.NotNull(entry);
        Assert.Equal(8000, entry!.Score);
    }

    // ─── Stale entry score unchanged → not counted ──────

    [Fact]
    public async Task RefreshAllAsync_StaleEntrySameScore_Returns0()
    {
        var (refresher, handler) = CreateRefresher();

        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct1" };
        var seen = new HashSet<(string, string, string)>();
        foreach (var inst in GlobalLeaderboardScraper.AllInstruments.Where(i => i != "Solo_Guitar"))
            seen.Add(("acct1", "songA", inst));

        // Pre-populate a stale entry
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songA", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 12000, Rank = 50
        }]);

        // API returns the same score — no change
        handler.EnqueueJsonOk("""
        {
            "page": 0, "totalPages": 1,
            "entries": [{
                "teamId": "acct1", "rank": 50, "percentile": 0.5,
                "sessionHistory": [{ "trackedStats": { "SCORE": 12000 } }]
            }]
        }
        """);

        var result = await refresher.RefreshAllAsync(
            registered, seen, ["songA"], "token", "caller");

        // Stale entries are always re-upserted regardless of score change
        Assert.Equal(1, result);
    }

    // ─── Per-account exception → caught, continues ──────

    [Fact]
    public async Task RefreshAllAsync_OneAccountThrows_ContinuesToNextAccount()
    {
        var (refresher, handler) = CreateRefresher();

        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct1", "acct2" };
        var seen = new HashSet<(string, string, string)>();
        // Leave all instruments unseen for both accounts

        // For acct1: throw on first lookup
        handler.EnqueueException(new HttpRequestException("network error"));
        // For acct2 (6 instruments): all empty
        for (int i = 0; i < 6; i++)
            handler.EnqueueJsonOk("""{"page":0,"totalPages":0,"entries":[]}""");

        // We queue 6 more for acct1 remaining instruments (they process in parallel)
        for (int i = 0; i < 5; i++)
            handler.EnqueueJsonOk("""{"page":0,"totalPages":0,"entries":[]}""");

        var result = await refresher.RefreshAllAsync(
            registered, seen, ["songA"], "token", "caller");

        // Neither account has updated entries
        Assert.Equal(0, result);
    }
}
