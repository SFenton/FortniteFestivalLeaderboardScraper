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
/// Tests for <see cref="FirstSeenSeasonCalculator"/> — verifies season probing logic,
/// MIN(Season) detection, and idempotent skip behavior.
/// </summary>
public class FirstSeenSeasonCalculatorTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaDb = new();
    private readonly string _dataDir;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly ScrapeProgressTracker _progress = new();
    private readonly ILogger<FirstSeenSeasonCalculator> _log = Substitute.For<ILogger<FirstSeenSeasonCalculator>>();

    public FirstSeenSeasonCalculatorTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_firstseen_test_{Guid.NewGuid():N}");
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

    // ─── No songs → returns 0 ────────────────────────────

    [Fact]
    public async Task CalculateAsync_NoSongs_Returns0()
    {
        var (calculator, _) = CreateCalculator();
        var service = CreateServiceWithSongs(Array.Empty<Song>());

        var result = await calculator.CalculateAsync(service, "token", "caller");

        Assert.Equal(0, result);
    }

    // ─── Song with MIN season 1 → no probe, stored as 1 ──

    [Fact]
    public async Task CalculateAsync_MinSeason1_NoProbeNeeded()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });

        SeedEntries("Solo_Guitar", "song1", season: 1, count: 3);
        SeedEntries("Solo_Drums", "song1", season: 2, count: 2);

        var result = await calculator.CalculateAsync(service, "token", "caller");

        Assert.Equal(1, result);
        Assert.Equal(1, _metaDb.Db.GetFirstSeenSeason("song1"));
        // No HTTP requests should have been made
        Assert.Empty(handler.Requests);
    }

    // ─── Song with MIN season 3, probe season 2 succeeds → stored as 2 ──

    [Fact]
    public async Task CalculateAsync_MinSeason3_ProbeFindsEarlier_StoresProbed()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });

        SeedEntries("Solo_Guitar", "song1", season: 3, count: 2);

        // V2 lookup returns no_score_found → window exists but caller has no score
        handler.EnqueueJsonResponse(HttpStatusCode.NotFound,
            "{\"errorCode\": \"errors.com.epicgames.events.no_score_found\"}");

        var result = await calculator.CalculateAsync(service, "token", "caller");

        Assert.Equal(1, result);
        Assert.Equal(2, _metaDb.Db.GetFirstSeenSeason("song1"));
        Assert.Single(handler.Requests);
    }

    // ─── Song with MIN season 3, probe season 2 fails → stored as 3 ──

    [Fact]
    public async Task CalculateAsync_MinSeason3_ProbeFailsHttpError_StoresMin()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });

        SeedEntries("Solo_Bass", "song1", season: 3, count: 1);

        // API returns 400 or similar → window doesn't exist
        handler.EnqueueError(HttpStatusCode.BadRequest, "invalid event");

        var result = await calculator.CalculateAsync(service, "token", "caller");

        Assert.Equal(1, result);
        Assert.Equal(3, _metaDb.Db.GetFirstSeenSeason("song1"));
    }

    // ─── Song with MIN season 2, probe evergreen succeeds → stored as 1 ──

    [Fact]
    public async Task CalculateAsync_MinSeason2_ProbeEvergreenSucceeds_StoresAs1()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });

        SeedEntries("Solo_Guitar", "song1", season: 2, count: 1);

        // V2 lookup with "evergreen" window succeeds (200 with a real entry)
        handler.EnqueueJsonOk(BuildV2LookupResponse("song1", "Solo_Guitar", "caller", 1000, 1));

        var result = await calculator.CalculateAsync(service, "token", "caller");

        Assert.Equal(1, result);
        Assert.Equal(1, _metaDb.Db.GetFirstSeenSeason("song1"));
    }

    // ─── Already calculated → skipped ────────────────────

    [Fact]
    public async Task CalculateAsync_AlreadyCalculated_Skips()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });

        SeedEntries("Solo_Guitar", "song1", season: 3, count: 1);

        // Pre-populate
        _metaDb.Db.UpsertFirstSeenSeason("song1", 2, 3, 2, "found_in_season002");

        var result = await calculator.CalculateAsync(service, "token", "caller");

        Assert.Equal(0, result);
        // Should still be the pre-populated value
        Assert.Equal(2, _metaDb.Db.GetFirstSeenSeason("song1"));
        Assert.Empty(handler.Requests);
    }

    // ─── Song with no entries → skipped (no crash) ───────

    [Fact]
    public async Task CalculateAsync_NoEntries_SkipsGracefully()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });

        // No entries seeded — but no global max either (empty DBs)

        var result = await calculator.CalculateAsync(service, "token", "caller");

        // No global max available, so nothing can be estimated
        Assert.Equal(0, result);
        Assert.Null(_metaDb.Db.GetFirstSeenSeason("song1"));
        Assert.Empty(handler.Requests);
    }

    // ─── Song with no entries but other songs exist → estimated ───

    [Fact]
    public async Task CalculateAsync_NoEntries_WithGlobalMax_StoresEstimated()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1"), MakeSong("song2") });

        // song1 has entries (provides global max), song2 has none
        SeedEntries("Solo_Guitar", "song1", season: 1);
        SeedEntries("Solo_Guitar", "song1", season: 7);

        var result = await calculator.CalculateAsync(service, "token", "caller");

        // song1 → firstSeen=1, song2 → estimated=7 (global max)
        Assert.Equal(2, result);
        Assert.Equal(1, _metaDb.Db.GetFirstSeenSeason("song1"));
        Assert.Null(_metaDb.Db.GetFirstSeenSeason("song2"));

        var all = _metaDb.Db.GetAllFirstSeenSeasons();
        Assert.Equal(7, all["song2"].EstimatedSeason);
        Assert.Null(all["song2"].FirstSeenSeason);
    }

    // ─── Multiple songs, mixed results ──────────────────

    [Fact]
    public async Task CalculateAsync_MultipleSongs_ProcessesAll()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[]
        {
            MakeSong("s1"), // MIN=1 → no probe
            MakeSong("s2"), // MIN=4 → probe season 3
            MakeSong("s3"), // no entries → skip
        });

        SeedEntries("Solo_Guitar", "s1", season: 1);
        SeedEntries("Solo_Guitar", "s2", season: 4);

        // Probe for s2 → season 3 not found
        handler.EnqueueError(HttpStatusCode.BadRequest, "invalid event");

        var result = await calculator.CalculateAsync(service, "token", "caller");

        // s1 → calculated (min 1), s2 → calculated (probe failed, stays 4), s3 → estimated (global max = 4)
        Assert.Equal(3, result);
        Assert.Equal(1, _metaDb.Db.GetFirstSeenSeason("s1"));
        Assert.Equal(4, _metaDb.Db.GetFirstSeenSeason("s2"));
        Assert.Null(_metaDb.Db.GetFirstSeenSeason("s3"));

        var all = _metaDb.Db.GetAllFirstSeenSeasons();
        Assert.Equal(4, all["s3"].EstimatedSeason);
    }

    // ─── MIN across multiple instruments ────────────────

    [Fact]
    public async Task CalculateAsync_MinAcrossInstruments_UsesGlobalMin()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });

        SeedEntries("Solo_Guitar", "song1", season: 5);
        SeedEntries("Solo_Drums", "song1", season: 3);
        SeedEntries("Solo_Vocals", "song1", season: 4);

        // Probe for season 2 → not found
        handler.EnqueueError(HttpStatusCode.BadRequest, "invalid event");

        var result = await calculator.CalculateAsync(service, "token", "caller");

        Assert.Equal(1, result);
        Assert.Equal(3, _metaDb.Db.GetFirstSeenSeason("song1"));
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

    // ─── Probe throws non-HttpRequestException → catch block fallback ──

    [Fact]
    public async Task CalculateAsync_ProbeThrowsNonHttp_CatchFallsBackToMin()
    {
        var (calculator, handler) = CreateCalculator();
        var service = CreateServiceWithSongs(new[] { MakeSong("song1") });

        SeedEntries("Solo_Guitar", "song1", season: 3, count: 1);

        // Enqueue an unexpected exception (not HttpRequestException) to trigger
        // the catch (Exception ex) block in the CalculateAsync lambda.
        handler.EnqueueException(new InvalidOperationException("unexpected internal error"));

        var result = await calculator.CalculateAsync(service, "token", "caller");

        Assert.Equal(1, result);
        // Falls back to observed MIN season
        Assert.Equal(3, _metaDb.Db.GetFirstSeenSeason("song1"));
    }
}
