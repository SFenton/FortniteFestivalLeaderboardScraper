using System.Net;
using FortniteFestival.Core.Scraping;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="HistoryReconstructor"/> instance methods:
/// DiscoverSeasonWindowsAsync, ReconstructAccountAsync.
/// Static methods (ExtractSeasonNumber, ParseSeasonWindowsFromEventsJson)
/// are covered in the existing HistoryReconstructorTests file.
/// </summary>
public class HistoryReconstructorInstanceTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaDb = new();
    private readonly string _dataDir;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly ILogger<HistoryReconstructor> _log = Substitute.For<ILogger<HistoryReconstructor>>();
    private readonly AdaptiveConcurrencyLimiter _limiter;
    private readonly SharedDopPool _pool;

    public HistoryReconstructorInstanceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_histinst_test_{Guid.NewGuid():N}");
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

    private (HistoryReconstructor recon, MockHttpMessageHandler scraperHandler, MockHttpMessageHandler eventsHandler) CreateReconstructor()
    {
        var scraperHandler = new MockHttpMessageHandler();
        var scraperHttp = new HttpClient(scraperHandler);
        var progress = new ScrapeProgressTracker();
        var scraperLog = Substitute.For<ILogger<GlobalLeaderboardScraper>>();
        var scraper = new GlobalLeaderboardScraper(scraperHttp, progress, scraperLog, maxLookupRetries: 0);

        var eventsHandler = new MockHttpMessageHandler();
        var eventsHttp = new HttpClient(eventsHandler);

        var recon = new HistoryReconstructor(scraper, _persistence, eventsHttp, progress, new UserSyncProgressTracker(new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), _log);
        return (recon, scraperHandler, eventsHandler);
    }

    // â”€â”€â”€ DiscoverSeasonWindowsAsync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task DiscoverSeasonWindowsAsync_CachedWindows_ReturnsCached()
    {
        var (recon, _, _) = CreateReconstructor();

        // Pre-cache season windows
        _metaDb.Db.UpsertSeasonWindow(1, "event1", "season_1");
        _metaDb.Db.UpsertSeasonWindow(2, "event2", "season_2");

        var result = await recon.DiscoverSeasonWindowsAsync("token", "caller");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task DiscoverSeasonWindowsAsync_ApiReturnsEvents_ParsesAndCaches()
    {
        var (recon, _, eventsHandler) = CreateReconstructor();

        var eventsJson = """
        {
            "events": [{
                "eventId": "FNFestival",
                "eventWindows": [
                    { "eventWindowId": "season_1" },
                    { "eventWindowId": "season_2" },
                    { "eventWindowId": "season_3" }
                ]
            }]
        }
        """;
        eventsHandler.EnqueueJsonOk(eventsJson);

        var result = await recon.DiscoverSeasonWindowsAsync("token", "caller");

        Assert.Equal(3, result.Count);
        Assert.Equal(1, result[0].SeasonNumber);
        Assert.Equal(3, result[2].SeasonNumber);

        // Verify they were cached in DB
        var cached = _metaDb.Db.GetSeasonWindows();
        Assert.Equal(3, cached.Count);
    }

    [Fact]
    public async Task DiscoverSeasonWindowsAsync_ApiFails_FallsBackToProbing()
    {
        var (recon, scraperHandler, eventsHandler) = CreateReconstructor();

        // Events API fails
        eventsHandler.EnqueueError(HttpStatusCode.InternalServerError);

        // Probing needs a song in the DB
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songA", [new LeaderboardEntry
        {
            AccountId = "probe_acct", Score = 100, Rank = 1
        }]);

        // Probe evergreen (season 1): found (non-error response â†’ window exists)
        scraperHandler.EnqueueJsonOk("[]");
        // Probe season002 (season 2): found
        scraperHandler.EnqueueJsonOk("[]");
        // Seasons 3 and 4: no more queued responses â†’ MockHttpMessageHandler throws
        // â†’ ProbeSeasonWindowsAsync catches the exception â†’ consecutiveFailures reaches 2 â†’ stops

        var result = await recon.DiscoverSeasonWindowsAsync("token", "caller");

        // Should have found 2 seasons before 2 consecutive failures stopped probing
        Assert.Equal(2, result.Count);
        Assert.Equal("evergreen", result[0].WindowId);
        Assert.Equal("season002", result[1].WindowId);
    }

    // â”€â”€â”€ ReconstructAccountAsync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task ReconstructAccountAsync_AlreadyComplete_Returns0()
    {
        var (recon, _, _) = CreateReconstructor();

        _metaDb.Db.EnqueueHistoryRecon("acct1", 0);
        _metaDb.Db.CompleteHistoryRecon("acct1");

        var windows = new List<SeasonWindowInfo>
        {
            new() { SeasonNumber = 1, EventId = "e1", WindowId = "season_1" }
        };

        var result = await recon.ReconstructAccountAsync(
            "acct1", windows, "token", "caller", _pool);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ReconstructAccountAsync_NoScoresAboveSeason1_Returns0()
    {
        var (recon, _, _) = CreateReconstructor();

        // Add entries with season 0 and 1 only
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songA", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 1000, Rank = 1, Season = 1
        }]);

        var windows = new List<SeasonWindowInfo>
        {
            new() { SeasonNumber = 1, EventId = "e1", WindowId = "season_1" }
        };

        var result = await recon.ReconstructAccountAsync(
            "acct1", windows, "token", "caller", _pool);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ReconstructAccountAsync_ReconstructsProgression()
    {
        var (recon, scraperHandler, _) = CreateReconstructor();

        // Add an all-time entry with season 3
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songA", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 5000, Rank = 100, Season = 3
        }]);
        _metaDb.Db.UpsertFirstSeenSeason("songA", 1, 1, 1, null, 2);

        var windows = new List<SeasonWindowInfo>
        {
            new() { SeasonNumber = 1, EventId = "e1", WindowId = "season_1" },
            new() { SeasonNumber = 2, EventId = "e2", WindowId = "season_2" },
            new() { SeasonNumber = 3, EventId = "e3", WindowId = "season_3" },
        };

        // Seasonal lookups: season 1 â†’ 1000, season 2 â†’ 3000, season 3 â†’ 5000
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":500,"percentile":0.1,
            "sessionHistory":[{"endTime":"2024-01-01T00:00:00Z","trackedStats":{"SCORE":1000,"ACCURACY":80,"STARS_EARNED":3}}]
        }]
        """);
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":300,"percentile":0.3,
            "sessionHistory":[{"endTime":"2024-04-01T00:00:00Z","trackedStats":{"SCORE":3000,"ACCURACY":90,"STARS_EARNED":4}}]
        }]
        """);
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":100,"percentile":0.5,
            "sessionHistory":[{"endTime":"2024-07-01T00:00:00Z","trackedStats":{"SCORE":5000,"ACCURACY":95,"STARS_EARNED":5}}]
        }]
        """);

        var result = await recon.ReconstructAccountAsync(
            "acct1", windows, "token", "caller", _pool);

        // All 3 entries should be part of the progression (1000 < 3000 < 5000)
        Assert.Equal(3, result);

        // Verify score history was inserted
        var history = _metaDb.Db.GetScoreHistory("acct1", 100);
        Assert.Equal(3, history.Count);
    }

    [Fact]
    public async Task ReconstructAccountAsync_RecordsNonIncreasingScores()
    {
        var (recon, scraperHandler, _) = CreateReconstructor();

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songA", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 5000, Rank = 100, Season = 3
        }]);
        _metaDb.Db.UpsertFirstSeenSeason("songA", 1, 1, 1, null, 2);

        var windows = new List<SeasonWindowInfo>
        {
            new() { SeasonNumber = 1, EventId = "e1", WindowId = "season_1" },
            new() { SeasonNumber = 2, EventId = "e2", WindowId = "season_2" },
            new() { SeasonNumber = 3, EventId = "e3", WindowId = "season_3" },
        };

        // Season 1: 3000, Season 2: 2000 (no increase), Season 3: 5000
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":1,"percentile":1.0,
            "sessionHistory":[{"endTime":"2024-01-01T00:00:00Z","trackedStats":{"SCORE":3000}}]
        }]
        """);
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":1,"percentile":1.0,
            "sessionHistory":[{"endTime":"2024-04-01T00:00:00Z","trackedStats":{"SCORE":2000}}]
        }]
        """);
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":1,"percentile":1.0,
            "sessionHistory":[{"endTime":"2024-07-01T00:00:00Z","trackedStats":{"SCORE":5000}}]
        }]
        """);

        var result = await recon.ReconstructAccountAsync(
            "acct1", windows, "token", "caller", _pool);

        // All 3 sessions recorded (including the 2000 non-improvement)
        Assert.Equal(3, result);
    }

    // â”€â”€â”€ Seasonal lookup failure â†’ skipped, continues â”€â”€â”€

    [Fact]
    public async Task ReconstructAccountAsync_SeasonalLookupFails_SkipsSongContinues()
    {
        var (recon, scraperHandler, _) = CreateReconstructor();

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songA", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 5000, Rank = 100, Season = 2
        }]);
        _metaDb.Db.UpsertFirstSeenSeason("songA", 1, 1, 1, null, 2);

        var windows = new List<SeasonWindowInfo>
        {
            new() { SeasonNumber = 1, EventId = "e1", WindowId = "season_1" },
            new() { SeasonNumber = 2, EventId = "e2", WindowId = "season_2" },
        };

        // Season 1 lookup throws error
        scraperHandler.EnqueueException(new HttpRequestException("connection error"));
        // Season 2 lookup returns score
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":50,"percentile":0.5,
            "sessionHistory":[{"endTime":"2024-04-01T00:00:00Z","trackedStats":{"SCORE":5000}}]
        }]
        """);

        var result = await recon.ReconstructAccountAsync(
            "acct1", windows, "token", "caller", _pool);

        // Only season 2 entry exists (season 1 failed)
        Assert.Equal(1, result);
    }

    // â”€â”€â”€ Missing season window â†’ skipped â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task ReconstructAccountAsync_MissingSeason_SkipsIt()
    {
        var (recon, scraperHandler, _) = CreateReconstructor();

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songA", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 3000, Rank = 200, Season = 3
        }]);
        _metaDb.Db.UpsertFirstSeenSeason("songA", 1, 1, 1, null, 2);

        // Only provide windows for season 1 and 3 â€” season 2 is missing
        var windows = new List<SeasonWindowInfo>
        {
            new() { SeasonNumber = 1, EventId = "e1", WindowId = "season_1" },
            new() { SeasonNumber = 3, EventId = "e3", WindowId = "season_3" },
        };

        // Season 1: 1000, Season 3: 3000
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":500,"percentile":0.1,
            "sessionHistory":[{"endTime":"2024-01-01T00:00:00Z","trackedStats":{"SCORE":1000}}]
        }]
        """);
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":200,"percentile":0.3,
            "sessionHistory":[{"endTime":"2024-07-01T00:00:00Z","trackedStats":{"SCORE":3000}}]
        }]
        """);

        var result = await recon.ReconstructAccountAsync(
            "acct1", windows, "token", "caller", _pool);

        // Both seasons found, both have increasing scores
        Assert.Equal(2, result);
    }

    // â”€â”€â”€ No entry from seasonal lookup â†’ counted as query but no entry â”€â”€

    [Fact]
    public async Task ReconstructAccountAsync_EmptySeasonalResult_Returns0()
    {
        var (recon, scraperHandler, _) = CreateReconstructor();

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songA", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 1000, Rank = 100, Season = 2
        }]);
        _metaDb.Db.UpsertFirstSeenSeason("songA", 1, 1, 1, null, 2);

        var windows = new List<SeasonWindowInfo>
        {
            new() { SeasonNumber = 1, EventId = "e1", WindowId = "season_1" },
            new() { SeasonNumber = 2, EventId = "e2", WindowId = "season_2" },
        };

        // Both seasons return empty â€” no entries for this account
        scraperHandler.EnqueueJsonOk("""{"page":0,"totalPages":0,"entries":[]}""");
        scraperHandler.EnqueueJsonOk("""{"page":0,"totalPages":0,"entries":[]}""");

        var result = await recon.ReconstructAccountAsync(
            "acct1", windows, "token", "caller", _pool);

        Assert.Equal(0, result);
    }

    // â”€â”€â”€ Already processed songs â†’ skipped on resume â”€â”€â”€â”€

    [Fact]
    public async Task ReconstructAccountAsync_AlreadyProcessedPair_Skips()
    {
        var (recon, scraperHandler, _) = CreateReconstructor();

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songA", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 5000, Rank = 100, Season = 3
        }]);
        guitarDb.UpsertEntries("songB", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 3000, Rank = 200, Season = 2
        }]);
        _metaDb.Db.UpsertFirstSeenSeason("songA", 1, 1, 1, null, 2);
        _metaDb.Db.UpsertFirstSeenSeason("songB", 1, 1, 1, null, 2);

        var windows = new List<SeasonWindowInfo>
        {
            new() { SeasonNumber = 1, EventId = "e1", WindowId = "season_1" },
            new() { SeasonNumber = 2, EventId = "e2", WindowId = "season_2" },
            new() { SeasonNumber = 3, EventId = "e3", WindowId = "season_3" },
        };

        // Pre-mark songA as already processed (simulates partial resumption)
        _metaDb.Db.EnqueueHistoryRecon("acct1", 2);
        _metaDb.Db.StartHistoryRecon("acct1");
        _metaDb.Db.MarkHistoryReconSongProcessed("acct1", "songA", "Solo_Guitar");

        // Only songB should be processed (2 seasons to query)
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":300,"percentile":0.2,
            "sessionHistory":[{"endTime":"2024-01-01T00:00:00Z","trackedStats":{"SCORE":1000}}]
        }]
        """);
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":200,"percentile":0.3,
            "sessionHistory":[{"endTime":"2024-04-01T00:00:00Z","trackedStats":{"SCORE":3000}}]
        }]
        """);

        var result = await recon.ReconstructAccountAsync(
            "acct1", windows, "token", "caller", _pool);

        Assert.Equal(2, result);
        // Only 2 HTTP requests (songB's 2 seasons, songA skipped)
        Assert.Equal(2, scraperHandler.Requests.Count);
    }

    // â”€â”€â”€ Per-song error during reconstruction â†’ caught, continues â”€â”€

    [Fact]
    public async Task ReconstructAccountAsync_OneSongThrows_ContinuesToNext()
    {
        var (recon, scraperHandler, _) = CreateReconstructor();

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songA", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 5000, Rank = 100, Season = 2
        }]);
        guitarDb.UpsertEntries("songB", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 3000, Rank = 200, Season = 2
        }]);
        _metaDb.Db.UpsertFirstSeenSeason("songA", 1, 1, 1, null, 2);
        _metaDb.Db.UpsertFirstSeenSeason("songB", 1, 1, 1, null, 2);

        var windows = new List<SeasonWindowInfo>
        {
            new() { SeasonNumber = 1, EventId = "e1", WindowId = "season_1" },
            new() { SeasonNumber = 2, EventId = "e2", WindowId = "season_2" },
        };

        // SongA season 1 fails, season 2 returns ok (but songA overall fails with exception)
        scraperHandler.EnqueueException(new HttpRequestException("songA lookup failed"));
        // SongA season 2 won't be queried because season 1 exception isn't caught inside ReconstructSongHistoryAsync's LookupSeasonalAsync call
        // Actually, let me re-read: the catch in ReconstructSongHistoryAsync catches and continues
        scraperHandler.EnqueueJsonOk("""[]""");

        // SongB: success for both seasons
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":300,"percentile":0.2,
            "sessionHistory":[{"endTime":"2024-01-01T00:00:00Z","trackedStats":{"SCORE":1000}}]
        }]
        """);
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":200,"percentile":0.3,
            "sessionHistory":[{"endTime":"2024-04-01T00:00:00Z","trackedStats":{"SCORE":3000}}]
        }]
        """);

        var result = await recon.ReconstructAccountAsync(
            "acct1", windows, "token", "caller", _pool);

        // SongB contributes 2 entries (1000 â†’ 3000), songA only has the non-failed season entry
        Assert.True(result >= 2, $"Expected at least 2 history entries but got {result}");
    }

    // â”€â”€â”€ DiscoverSeasonWindows: probing fallback â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task DiscoverSeasonWindowsAsync_ApiFails_ProbesCorrectly()
    {
        var (recon, scraperHandler, eventsHandler) = CreateReconstructor();

        // Need entries in instrument DB for FindProbeSongId() to find a song
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("probeSong", [new LeaderboardEntry
        {
            AccountId = "someone", Score = 1000, Rank = 1, Season = 1
        }]);

        // Events API throws â†’ fallback to probing
        eventsHandler.EnqueueException(new HttpRequestException("Events API down"));

        // Probing calls LookupSeasonalAsync; non-success = returns null (NOT exception)
        // so null return still adds the window. Only exceptions increment consecutiveFailures.
        // Season 1 probe: success â†’ window added
        scraperHandler.EnqueueJsonOk("""
        { "page":0,"totalPages":1,"entries":[{
            "teamId":"probe","rank":1,"percentile":1.0,
            "sessionHistory":[{"trackedStats":{"SCORE":100}}]
        }]}
        """);
        // Season 2 & 3: throw â†’ two consecutive failures â†’ stop
        scraperHandler.EnqueueException(new HttpRequestException("probe fail"));
        scraperHandler.EnqueueException(new HttpRequestException("probe fail"));

        var windows = await recon.DiscoverSeasonWindowsAsync("token", "caller");

        // Should have discovered exactly 1 season window (season_1)
        Assert.Single(windows);
        Assert.Equal(1, windows[0].SeasonNumber);
    }

    // â”€â”€â”€ Multi-session reconstruction â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task ReconstructAccountAsync_MultipleSessionsPerSeason_CapturesAllImprovements()
    {
        var (recon, scraperHandler, _) = CreateReconstructor();

        // Add an all-time entry with season 2 (so reconstruction queries seasons 1 & 2)
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songA", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 600000, Rank = 10, Season = 2
        }]);
        _metaDb.Db.UpsertFirstSeenSeason("songA", 1, 1, 1, null, 2);

        var windows = new List<SeasonWindowInfo>
        {
            new() { SeasonNumber = 1, EventId = "e1", WindowId = "season_1" },
            new() { SeasonNumber = 2, EventId = "e2", WindowId = "season_2" },
        };

        // Season 1: 3 sessions showing progression within the season
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":500,"percentile":0.1,
            "sessionHistory":[
                {"endTime":"2024-01-01T00:00:00Z","trackedStats":{"SCORE":100000,"ACCURACY":800000,"STARS_EARNED":3}},
                {"endTime":"2024-01-15T00:00:00Z","trackedStats":{"SCORE":250000,"ACCURACY":880000,"STARS_EARNED":4}},
                {"endTime":"2024-02-01T00:00:00Z","trackedStats":{"SCORE":400000,"ACCURACY":950000,"STARS_EARNED":5}}
            ]
        }]
        """);

        // Season 2: 2 sessions
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":10,"percentile":0.5,
            "sessionHistory":[
                {"endTime":"2024-04-01T00:00:00Z","trackedStats":{"SCORE":500000,"ACCURACY":970000,"STARS_EARNED":5}},
                {"endTime":"2024-05-01T00:00:00Z","trackedStats":{"SCORE":600000,"ACCURACY":990000,"FULL_COMBO":1,"STARS_EARNED":6}}
            ]
        }]
        """);

        var result = await recon.ReconstructAccountAsync(
            "acct1", windows, "token", "caller", _pool);

        // All 5 sessions show strictly increasing scores:
        // 100k â†’ 250k â†’ 400k â†’ 500k â†’ 600k
        Assert.Equal(5, result);

        var history = _metaDb.Db.GetScoreHistory("acct1", 100);
        Assert.Equal(5, history.Count);
    }

    [Fact]
    public async Task ReconstructAccountAsync_MultipleSessionsWithNonIncreasing_RecordsAll()
    {
        var (recon, scraperHandler, _) = CreateReconstructor();

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songA", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 500000, Rank = 50, Season = 2
        }]);
        _metaDb.Db.UpsertFirstSeenSeason("songA", 1, 1, 1, null, 2);

        var windows = new List<SeasonWindowInfo>
        {
            new() { SeasonNumber = 1, EventId = "e1", WindowId = "season_1" },
            new() { SeasonNumber = 2, EventId = "e2", WindowId = "season_2" },
        };

        // Season 1: 4 sessions, scores go 100k â†’ 200k â†’ 150k â†’ 300k
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":100,"percentile":0.2,
            "sessionHistory":[
                {"endTime":"2024-01-01T00:00:00Z","trackedStats":{"SCORE":100000}},
                {"endTime":"2024-01-10T00:00:00Z","trackedStats":{"SCORE":200000}},
                {"endTime":"2024-01-20T00:00:00Z","trackedStats":{"SCORE":150000}},
                {"endTime":"2024-02-01T00:00:00Z","trackedStats":{"SCORE":300000}}
            ]
        }]
        """);

        // Season 2: 2 sessions, 400k â†’ 500k
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":50,"percentile":0.5,
            "sessionHistory":[
                {"endTime":"2024-04-01T00:00:00Z","trackedStats":{"SCORE":400000}},
                {"endTime":"2024-05-01T00:00:00Z","trackedStats":{"SCORE":500000}}
            ]
        }]
        """);

        var result = await recon.ReconstructAccountAsync(
            "acct1", windows, "token", "caller", _pool);

        // All 6 sessions recorded (including 150k non-improvement)
        Assert.Equal(6, result);
    }

    // â”€â”€â”€ FirstSeenSeason optimization â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task ReconstructAccountAsync_WithFirstSeenSeason_SkipsEarlierSeasons()
    {
        var (recon, scraperHandler, _) = CreateReconstructor();

        // Song has all-time entry in season 5
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songA", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 10000, Rank = 50, Season = 5
        }]);

        // Record that songA first appeared in season 3
        _metaDb.Db.UpsertFirstSeenSeason("songA",
            firstSeenSeason: 3, minObservedSeason: 3,
            estimatedSeason: 3, probeResult: "first_play", calculationVersion: 2);

        var windows = new List<SeasonWindowInfo>
        {
            new() { SeasonNumber = 1, EventId = "e1", WindowId = "season_1" },
            new() { SeasonNumber = 2, EventId = "e2", WindowId = "season_2" },
            new() { SeasonNumber = 3, EventId = "e3", WindowId = "season_3" },
            new() { SeasonNumber = 4, EventId = "e4", WindowId = "season_4" },
            new() { SeasonNumber = 5, EventId = "e5", WindowId = "season_5" },
        };

        // Only seasons 3, 4, 5 should be queried (not 1, 2)
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":200,"percentile":0.2,
            "sessionHistory":[{"endTime":"2024-07-01T00:00:00Z","trackedStats":{"SCORE":3000}}]
        }]
        """);
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":100,"percentile":0.3,
            "sessionHistory":[{"endTime":"2024-10-01T00:00:00Z","trackedStats":{"SCORE":7000}}]
        }]
        """);
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":50,"percentile":0.5,
            "sessionHistory":[{"endTime":"2025-01-01T00:00:00Z","trackedStats":{"SCORE":10000}}]
        }]
        """);

        var result = await recon.ReconstructAccountAsync(
            "acct1", windows, "token", "caller", _pool);

        // 3 entries: 3000 â†’ 7000 â†’ 10000
        Assert.Equal(3, result);
        // Only 3 HTTP requests (seasons 3, 4, 5), not 5
        Assert.Equal(3, scraperHandler.Requests.Count);
    }

    [Fact]
    public async Task ReconstructAccountAsync_WithEstimatedSeason_SkipsEarlierSeasons()
    {
        var (recon, scraperHandler, _) = CreateReconstructor();

        // Song has all-time entry in season 4
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songB", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 5000, Rank = 100, Season = 4
        }]);

        // Song has no real FirstSeenSeason, only EstimatedSeason
        _metaDb.Db.UpsertFirstSeenSeason("songB",
            firstSeenSeason: null, minObservedSeason: null,
            estimatedSeason: 2, probeResult: null, calculationVersion: 2);

        var windows = new List<SeasonWindowInfo>
        {
            new() { SeasonNumber = 1, EventId = "e1", WindowId = "season_1" },
            new() { SeasonNumber = 2, EventId = "e2", WindowId = "season_2" },
            new() { SeasonNumber = 3, EventId = "e3", WindowId = "season_3" },
            new() { SeasonNumber = 4, EventId = "e4", WindowId = "season_4" },
        };

        // Only seasons 2, 3, 4 should be queried (not 1)
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":300,"percentile":0.1,
            "sessionHistory":[{"endTime":"2024-04-01T00:00:00Z","trackedStats":{"SCORE":2000}}]
        }]
        """);
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":200,"percentile":0.2,
            "sessionHistory":[{"endTime":"2024-07-01T00:00:00Z","trackedStats":{"SCORE":3500}}]
        }]
        """);
        scraperHandler.EnqueueJsonOk("""
        [{
            "teamId":"acct1","rank":100,"percentile":0.3,
            "sessionHistory":[{"endTime":"2024-10-01T00:00:00Z","trackedStats":{"SCORE":5000}}]
        }]
        """);

        var result = await recon.ReconstructAccountAsync(
            "acct1", windows, "token", "caller", _pool);

        Assert.Equal(3, result);
        Assert.Equal(3, scraperHandler.Requests.Count);
    }

    [Fact]
    public async Task ReconstructAccountAsync_NoFirstSeenData_SkipsSong()
    {
        var (recon, scraperHandler, _) = CreateReconstructor();

        // Song without any FirstSeenSeason data â€” likely not released yet
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songC", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 5000, Rank = 100, Season = 3
        }]);

        // No UpsertFirstSeenSeason â€” songC is NOT in the firstSeenMap

        var windows = new List<SeasonWindowInfo>
        {
            new() { SeasonNumber = 1, EventId = "e1", WindowId = "season_1" },
            new() { SeasonNumber = 2, EventId = "e2", WindowId = "season_2" },
            new() { SeasonNumber = 3, EventId = "e3", WindowId = "season_3" },
        };

        // No HTTP requests should be made â€” the song is skipped entirely
        var result = await recon.ReconstructAccountAsync(
            "acct1", windows, "token", "caller", _pool);

        Assert.Equal(0, result);
        Assert.Equal(0, scraperHandler.Requests.Count);
    }
}
