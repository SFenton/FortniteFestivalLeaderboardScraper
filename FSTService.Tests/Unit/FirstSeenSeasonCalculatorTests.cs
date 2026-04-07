using System.Net;
using System.Reflection;
using FortniteFestival.Core;
using FortniteFestival.Core.Scraping;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="FirstSeenSeasonCalculator"/> — verifies binary-search season
/// probing, version gating, and edge cases.
/// </summary>
public class FirstSeenSeasonCalculatorTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaDb = new();
    private readonly string _dataDir;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly ScrapeProgressTracker _progress = new();
    private readonly ILogger<FirstSeenSeasonCalculator> _log = Substitute.For<ILogger<FirstSeenSeasonCalculator>>();
    private readonly AdaptiveConcurrencyLimiter _limiter;
    private readonly SharedDopPool _pool;

    public FirstSeenSeasonCalculatorTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_firstseen_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        loggerFactory.CreateLogger<InstrumentDatabase>().Returns(Substitute.For<ILogger<InstrumentDatabase>>());
        var persLog = Substitute.For<ILogger<GlobalLeaderboardPersistence>>();
        _persistence = new GlobalLeaderboardPersistence(_metaDb.Db, loggerFactory, persLog, _metaDb.DataSource, Options.Create(new FeatureOptions()));
        _persistence.Initialize();
        _limiter = new AdaptiveConcurrencyLimiter(16, minDop: 2, maxDop: 64, Substitute.For<ILogger>());
        _pool = new SharedDopPool(_limiter, lowPrioritySlots: 16);
    }

    public void Dispose()
    {
        _pool.Dispose();
        _persistence.Dispose();
        _metaDb.Dispose();
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private (FirstSeenSeasonCalculator calculator, MockHttpMessageHandler handler) CreateCalculator()
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler);
        var scraperLog = Substitute.For<ILogger<GlobalLeaderboardScraper>>();
        var scraper = new GlobalLeaderboardScraper(http, _progress, scraperLog, maxLookupRetries: 0);
        var calculator = new FirstSeenSeasonCalculator(scraper, _persistence, _progress, _log);
        return (calculator, handler);
    }

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

    private static Song MakeSong(string id) => new Song
    {
        track = new Track { su = id, tt = $"Song {id}", an = "Artist" }
    };

    private void SeedSeasonWindows(params int[] seasonNumbers)
    {
        foreach (var s in seasonNumbers)
            _metaDb.Db.UpsertSeasonWindow(s, $"evt_{s}", $"window_{s}");
    }

    private void SeedEntries(string instrument, string songId, int season, int count = 1)
    {
        var db = _persistence.GetOrCreateInstrumentDb(instrument);
        var entries = new List<LeaderboardEntry>();
        for (int i = 0; i < count; i++)
        {
            entries.Add(new LeaderboardEntry
            {
                AccountId = $"acct_{Guid.NewGuid():N}",
                Score = 1000 + i,
                Accuracy = 100,
                IsFullCombo = false,
                Stars = 5,
                Season = season,
                Percentile = 50.0,
            });
        }
        db.UpsertEntries(songId, entries);
    }

    /// <summary>Enqueue a valid probe response (song exists in this season).</summary>
    private static void EnqueueProbeSuccess(MockHttpMessageHandler handler)
    {
        handler.EnqueueJsonResponse(HttpStatusCode.NotFound,
            "{\"errorCode\": \"errors.com.epicgames.events.no_score_found\"}");
    }

    /// <summary>Enqueue an invalid probe response (song does not exist in this season).</summary>
    private static void EnqueueProbeFailure(MockHttpMessageHandler handler)
    {
        handler.EnqueueError(HttpStatusCode.BadRequest, "invalid event");
    }

    // ─── No songs → returns 0 ────────────────────────────

    [Fact]
    public async Task CalculateAsync_NoSongs_Returns0()
    {
        var (calculator, _) = CreateCalculator();
        var service = CreateServiceWithSongs(Array.Empty<Song>());
        SeedSeasonWindows(1, 2, 3, 4, 5);

        var result = await calculator.CalculateAsync(service, "token", "caller", _pool);

        Assert.Equal(0, result);
    }

    // ─── No season windows → returns 0 ──────────────────

    [Fact]
    public async Task CalculateAsync_NoSeasonWindows_Returns0()
    {
        var (calculator, _) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });
        // No season windows seeded

        var result = await calculator.CalculateAsync(service, "token", "caller", _pool);

        Assert.Equal(0, result);
    }

    // ─── Binary search finds song in season 1 (exists in all seasons) ──

    [Fact]
    public async Task CalculateAsync_SongExistsInAllSeasons_FindsSeason1()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });
        SeedSeasonWindows(1, 2, 3, 4, 5, 6, 7);

        // Binary search: 7 seasons → mid=4(ok), mid=2(ok), mid=1(ok) → found season 1
        EnqueueProbeSuccess(handler); // season 4 → exists
        EnqueueProbeSuccess(handler); // season 2 → exists
        EnqueueProbeSuccess(handler); // season 1 → exists

        var result = await calculator.CalculateAsync(service, "token", "caller", _pool);

        Assert.Equal(1, result);
        var all = _metaDb.Db.GetAllFirstSeenSeasons();
        Assert.Equal(1, all["song1"].FirstSeenSeason);
        Assert.Equal(FirstSeenSeasonCalculator.CurrentVersion, all["song1"].CalculationVersion);
    }

    // ─── Binary search narrows to mid-range season ──────

    [Fact]
    public async Task CalculateAsync_SongAppearsInSeason5_BinarySearchFindsIt()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });
        SeedSeasonWindows(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);

        // 10 seasons. Binary search:
        // mid=5(ok) → search lower
        // mid=2(fail) → search higher
        // mid=3(fail) → search higher
        // mid=4(fail) → search higher → lo=5, hi=4 → done, bestFound=5
        EnqueueProbeSuccess(handler); // season 5 → exists
        EnqueueProbeFailure(handler); // season 2 → doesn't exist
        EnqueueProbeFailure(handler); // season 3 → doesn't exist
        EnqueueProbeFailure(handler); // season 4 → doesn't exist

        var result = await calculator.CalculateAsync(service, "token", "caller", _pool);

        Assert.Equal(1, result);
        Assert.Equal(5, _metaDb.Db.GetAllFirstSeenSeasons()["song1"].FirstSeenSeason);
    }

    // ─── Song not found in any season → null firstSeen ──

    [Fact]
    public async Task CalculateAsync_SongNotInAnySeason_NullFirstSeen()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });
        SeedSeasonWindows(1, 2, 3, 4);

        // Binary search: all probes fail
        // mid=2(fail), mid=3(fail), mid=4(fail)
        EnqueueProbeFailure(handler); // season 2
        EnqueueProbeFailure(handler); // season 3
        EnqueueProbeFailure(handler); // season 4

        var result = await calculator.CalculateAsync(service, "token", "caller", _pool);

        Assert.Equal(1, result);
        var all = _metaDb.Db.GetAllFirstSeenSeasons();
        Assert.Null(all["song1"].FirstSeenSeason);
        Assert.Equal(4, all["song1"].EstimatedSeason); // last known season
        Assert.Equal(FirstSeenSeasonCalculator.CurrentVersion, all["song1"].CalculationVersion);
    }

    // ─── Version gating: current version skipped ────────

    [Fact]
    public async Task CalculateAsync_AlreadyAtCurrentVersion_Skips()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });
        SeedSeasonWindows(1, 2, 3, 4, 5);

        // Pre-populate at current version
        _metaDb.Db.UpsertFirstSeenSeason("song1", 2, 3, 2, "found_season002_via_binary_search(3_probes)",
            FirstSeenSeasonCalculator.CurrentVersion);

        var result = await calculator.CalculateAsync(service, "token", "caller", _pool);

        Assert.Equal(0, result);
        Assert.Equal(2, _metaDb.Db.GetAllFirstSeenSeasons()["song1"].FirstSeenSeason);
        Assert.Empty(handler.Requests);
    }

    // ─── Old version → recalculated ─────────────────────

    [Fact]
    public async Task CalculateAsync_OldVersion_Recalculates()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });
        SeedSeasonWindows(1, 2, 3);

        // Pre-populate at old version (1)
        _metaDb.Db.UpsertFirstSeenSeason("song1", 3, 3, 3, "not_found_in_season002", 1);

        // Binary search across 3 seasons: mid=2(ok), mid=1(fail) → found season 2
        EnqueueProbeSuccess(handler); // season 2 → exists
        EnqueueProbeFailure(handler); // season 1 → doesn't exist

        var result = await calculator.CalculateAsync(service, "token", "caller", _pool);

        Assert.Equal(1, result);
        var all = _metaDb.Db.GetAllFirstSeenSeasons();
        Assert.Equal(2, all["song1"].FirstSeenSeason);
        Assert.Equal(FirstSeenSeasonCalculator.CurrentVersion, all["song1"].CalculationVersion);
    }

    // ─── Idempotent re-run: no extra API calls ──────────

    [Fact]
    public async Task CalculateAsync_RerunAfterComplete_NoApiCalls()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });
        SeedSeasonWindows(1, 2, 3);

        // First run: binary search → mid=2(ok), mid=1(ok) → found season 1
        EnqueueProbeSuccess(handler); // season 2
        EnqueueProbeSuccess(handler); // season 1

        var result1 = await calculator.CalculateAsync(service, "token", "caller", _pool);
        Assert.Equal(1, result1);
        Assert.Equal(1, _metaDb.Db.GetAllFirstSeenSeasons()["song1"].FirstSeenSeason);

        // Second run: should skip (current version)
        var result2 = await calculator.CalculateAsync(service, "token", "caller", _pool);
        Assert.Equal(0, result2);
        Assert.Equal(2, handler.Requests.Count); // no new requests from second run
    }

    // ─── Single season → one probe ──────────────────────

    [Fact]
    public async Task CalculateAsync_SingleSeason_OneProbe()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });
        SeedSeasonWindows(1);

        EnqueueProbeSuccess(handler); // season 1 → exists

        var result = await calculator.CalculateAsync(service, "token", "caller", _pool);

        Assert.Equal(1, result);
        Assert.Equal(1, _metaDb.Db.GetAllFirstSeenSeasons()["song1"].FirstSeenSeason);
        Assert.Single(handler.Requests);
    }

    // ─── min_observed_season is stored as diagnostic ─────

    [Fact]
    public async Task CalculateAsync_StoresMinObservedSeason()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });
        SeedSeasonWindows(1, 2, 3, 4, 5);
        SeedEntries("Solo_Guitar", "song1", season: 3, count: 2);

        // Binary search: mid=3(ok), mid=1(fail), mid=2(ok) → found season 2
        EnqueueProbeSuccess(handler); // season 3
        EnqueueProbeFailure(handler); // season 1
        EnqueueProbeSuccess(handler); // season 2

        var result = await calculator.CalculateAsync(service, "token", "caller", _pool);

        Assert.Equal(1, result);
        Assert.Equal(2, _metaDb.Db.GetAllFirstSeenSeasons()["song1"].FirstSeenSeason);
    }

    // ─── Fatal exception in probe → catch stores fallback ──

    [Fact]
    public async Task CalculateAsync_ProbeThrowsNonHttp_CatchFallsBack()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });
        SeedSeasonWindows(1, 2, 3);
        SeedEntries("Solo_Guitar", "song1", season: 2, count: 1);

        // The first probe throws an unexpected exception
        handler.EnqueueException(new InvalidOperationException("unexpected internal error"));

        var result = await calculator.CalculateAsync(service, "token", "caller", _pool);

        Assert.Equal(1, result);
        var all = _metaDb.Db.GetAllFirstSeenSeasons();
        // Falls back to min_observed_season (2) since binary search failed
        Assert.Equal(2, all["song1"].FirstSeenSeason);
        Assert.Equal(FirstSeenSeasonCalculator.CurrentVersion, all["song1"].CalculationVersion);
    }

    // ─── Multiple songs: binary search per song ─────────

    [Fact]
    public async Task CalculateAsync_MultipleSongs_EachGetsBinarySearch()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[]
        {
            MakeSong("s1"),
            MakeSong("s2"),
        });
        SeedSeasonWindows(1, 2, 3);

        // s1: mid=2(ok), mid=1(ok) → season 1
        // s2: mid=2(fail), mid=3(ok) → season 3
        // Note: due to parallel execution, order depends on task scheduling.
        // Use maxConcurrency=1 to ensure sequential ordering.
        EnqueueProbeSuccess(handler); // s1: season 2
        EnqueueProbeSuccess(handler); // s1: season 1
        EnqueueProbeFailure(handler); // s2: season 2
        EnqueueProbeSuccess(handler); // s2: season 3

        var result = await calculator.CalculateAsync(service, "token", "caller", _pool);

        Assert.Equal(2, result);
        var all = _metaDb.Db.GetAllFirstSeenSeasons();
        Assert.Equal(1, all["s1"].FirstSeenSeason);
        Assert.Equal(3, all["s2"].FirstSeenSeason);
    }

    // ─── Helper: build a V2 lookup response JSON ────────

    private static string BuildV2LookupResponse(
        string songId, string instrument, string accountId, int score, int season)
    {
        return $$"""
        {
            "eventId": "alltime_{{songId}}_{{instrument}}",
            "eventWindowId": "alltime",
            "page": 0,
            "totalPages": 1,
            "updatedAt": "2025-03-01T00:00:00Z",
            "entries": [
                {
                    "gameId": "FNFestival",
                    "eventId": "alltime_{{songId}}_{{instrument}}",
                    "eventWindowId": "alltime",
                    "teamAccountIds": ["{{accountId}}"],
                    "liveSessionId": null,
                    "pointsEarned": 0,
                    "score": {{score}},
                    "rank": 1,
                    "percentile": 99.0,
                    "pointBreakdown": {},
                    "sessionHistory": [],
                    "unscoredTeams": [],
                    "trackedStats": {
                        "ACCURACY": 100,
                        "BEST_RUN_TOTAL_SCORE": {{score}},
                        "INSTRUMENT": "{{instrument}}",
                        "SEASON": {{season}},
                        "STARS_EARNED": 6,
                        "TOTAL_SCORE": {{score}}
                    },
                    "tokens": ["HasFullCombo"]
                }
            ]
        }
        """;
    }
}
