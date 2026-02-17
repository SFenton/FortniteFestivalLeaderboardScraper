using System.Net;
using System.Reflection;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="ScoreBackfiller"/> — focused on the orchestration logic
/// (resume, progress, upsert). Uses real SQLite temps + mock HTTP for the scraper.
/// </summary>
public class ScoreBackfillerTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaDb = new();
    private readonly string _dataDir;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly ILogger<ScoreBackfiller> _log = Substitute.For<ILogger<ScoreBackfiller>>();

    public ScoreBackfillerTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_backfill_test_{Guid.NewGuid():N}");
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

    private (ScoreBackfiller backfiller, MockHttpMessageHandler handler) CreateBackfiller()
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler);
        var progress = new ScrapeProgressTracker();
        var scraperLog = Substitute.For<ILogger<GlobalLeaderboardScraper>>();
        var scraper = new GlobalLeaderboardScraper(http, progress, scraperLog, maxLookupRetries: 0);
        var backfiller = new ScoreBackfiller(scraper, _persistence, _log);
        return (backfiller, handler);
    }

    /// <summary>
    /// Populate FestivalService._songs via reflection so we don't need to call
    /// InitializeAsync (which makes real HTTP requests to Epic's content API).
    /// </summary>
    private static FestivalService CreateServiceWithSongs(IReadOnlyList<Song> songs)
    {
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var songsField = typeof(FestivalService).GetField("_songs", flags)!;
        var dirtyField = typeof(FestivalService).GetField("_songsDirty", flags)!;
        var dict = (Dictionary<string, Song>)songsField.GetValue(service)!;
        foreach (var s in songs)
            if (s.track?.su is not null)
                dict[s.track.su] = s;
        dirtyField.SetValue(service, true);
        return service;
    }

    // ─── Backfill with no songs → completes immediately ─

    [Fact]
    public async Task BackfillAccountAsync_NoSongs_Returns0()
    {
        var (backfiller, handler) = CreateBackfiller();
        var service = CreateServiceWithSongs(Array.Empty<Song>());

        var result = await backfiller.BackfillAccountAsync(
            "acct1", service, "token", "caller");

        // Total pairs = 0 songs * 6 instruments = 0 → immediate completion
        Assert.Equal(0, result);
    }

    // ─── Backfill finds entry via API ───────────────────

    [Fact]
    public async Task BackfillAccountAsync_FindsNewEntry_UpsertsPersists()
    {
        var (backfiller, handler) = CreateBackfiller();

        var songs = new List<Song>
        {
            new Song
            {
                track = new Track
                {
                    su = "songA",
                    tt = "Test Song",
                    @in = new In { gr = 3 }
                }
            }
        };
        var service = CreateServiceWithSongs(songs);

        // For each of the 6 instruments, the scraper will do a lookup.
        // Make guitar return a score, everything else empty.
        var scoreJson = """
        [{
            "teamId": "acct1", "rank": 100, "percentile": 0.5,
            "sessionHistory": [{ "trackedStats": { "SCORE": 50000, "ACCURACY": 90, "STARS_EARNED": 4, "FULL_COMBO": 0, "SEASON": 2 } }]
        }]
        """;
        var emptyJson = """[]""";    

        // Queue responses for 6 instruments
        handler.EnqueueJsonOk(scoreJson);  // Solo_Guitar
        for (int i = 0; i < 5; i++)
            handler.EnqueueJsonOk(emptyJson);

        var result = await backfiller.BackfillAccountAsync(
            "acct1", service, "token", "caller");

        Assert.Equal(1, result);

        // Verify the entry was persisted in the guitar DB
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var entry = db.GetEntry("songA", "acct1");
        Assert.NotNull(entry);
        Assert.Equal(50000, entry!.Score);
    }

    // ─── Backfill already complete → does nothing ───────

    [Fact]
    public async Task BackfillAccountAsync_AlreadyComplete_DoesNothing()
    {
        var (backfiller, handler) = CreateBackfiller();

        var songs = new List<Song>
        {
            new Song { track = new Track { su = "songA", @in = new In { gr = 1 } } }
        };
        var service = CreateServiceWithSongs(songs);

        // Pre-mark all pairs as checked
        _metaDb.Db.EnqueueBackfill("acct1", 6);
        _metaDb.Db.StartBackfill("acct1");
        foreach (var instrument in GlobalLeaderboardScraper.AllInstruments)
            _metaDb.Db.MarkBackfillSongChecked("acct1", "songA", instrument, entryFound: false);
        _metaDb.Db.UpdateBackfillProgress("acct1", 6, 0);

        var result = await backfiller.BackfillAccountAsync(
            "acct1", service, "token", "caller");

        Assert.Equal(0, result);
        // No HTTP requests should have been made
        Assert.Empty(handler.Requests);
    }

    // ─── Backfill skips existing entries ────────────────

    [Fact]
    public async Task BackfillAccountAsync_ExistingEntry_SkipsApiCall()
    {
        var (backfiller, handler) = CreateBackfiller();

        var songs = new List<Song>
        {
            new Song { track = new Track { su = "songA", @in = new In { gr = 1 } } }
        };
        var service = CreateServiceWithSongs(songs);

        // Pre-populate an existing entry in the guitar DB
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songA", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 100, Rank = 1
        }]);

        // Remaining 5 instruments will need API calls (all empty)
        for (int i = 0; i < 5; i++)
            handler.EnqueueJsonOk("""{"page":0,"totalPages":0,"entries":[]}""");

        var result = await backfiller.BackfillAccountAsync(
            "acct1", service, "token", "caller");

        Assert.Equal(0, result);
        // Only 5 HTTP requests (skipped guitar)
        Assert.Equal(5, handler.Requests.Count);
    }

    // ─── Backfill cancelled mid-run → saves progress and re-throws ──

    [Fact]
    public async Task BackfillAccountAsync_Cancelled_SavesProgressAndThrows()
    {
        var (backfiller, handler) = CreateBackfiller();

        var songs = new List<Song>
        {
            new Song { track = new Track { su = "songA", @in = new In { gr = 1 } } }
        };
        var service = CreateServiceWithSongs(songs);

        // Queue responses: first lookup throws OperationCanceledException
        handler.EnqueueException(new OperationCanceledException("cancelled"));

        _metaDb.Db.EnqueueBackfill("acct1", 6);
        _metaDb.Db.StartBackfill("acct1");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            backfiller.BackfillAccountAsync("acct1", service, "token", "caller"));
    }

    // ─── Backfill API error → caught internally, continues ──

    [Fact]
    public async Task BackfillAccountAsync_InternalApiError_CaughtAndContinues()
    {
        var (backfiller, handler) = CreateBackfiller();

        var songs = new List<Song>
        {
            new Song { track = new Track { su = "songA", @in = new In { gr = 1 } } }
        };
        var service = CreateServiceWithSongs(songs);

        // Queue exceptions for all 6 instruments — caught inside ProcessSingleLookupAsync
        for (int i = 0; i < 6; i++)
            handler.EnqueueException(new InvalidOperationException("Unexpected"));

        var result = await backfiller.BackfillAccountAsync("acct1", service, "token", "caller");

        Assert.Equal(0, result);
    }

    // ─── API lookup error → caught, lookup skipped ──────

    [Fact]
    public async Task BackfillAccountAsync_LookupHttpError_ContinuesGracefully()
    {
        var (backfiller, handler) = CreateBackfiller();

        var songs = new List<Song>
        {
            new Song { track = new Track { su = "songA", @in = new In { gr = 1 } } }
        };
        var service = CreateServiceWithSongs(songs);

        // Queue HTTP errors for all 6 instruments (non-retryable, returns null from LookupAccountAsync)
        for (int i = 0; i < 6; i++)
            handler.EnqueueError(HttpStatusCode.Forbidden, "forbidden");

        var result = await backfiller.BackfillAccountAsync("acct1", service, "token", "caller");

        Assert.Equal(0, result);
        Assert.Equal(6, handler.Requests.Count);
    }
}
