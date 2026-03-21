using System.Net;
using FortniteFestival.Core;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class GlobalLeaderboardScraperTests
{
    private readonly ILogger<GlobalLeaderboardScraper> _log = Substitute.For<ILogger<GlobalLeaderboardScraper>>();
    private readonly ScrapeProgressTracker _progress = new();

    private (GlobalLeaderboardScraper scraper, MockHttpMessageHandler handler) CreateScraper()
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler);
        var scraper = new GlobalLeaderboardScraper(http, _progress, _log, maxLookupRetries: 0);
        return (scraper, handler);
    }

    // ─── AllInstruments ─────────────────────────────────

    [Fact]
    public void AllInstruments_Contains6Instruments()
    {
        Assert.Equal(6, GlobalLeaderboardScraper.AllInstruments.Count);
        Assert.Contains("Solo_Guitar", GlobalLeaderboardScraper.AllInstruments);
        Assert.Contains("Solo_Bass", GlobalLeaderboardScraper.AllInstruments);
        Assert.Contains("Solo_Vocals", GlobalLeaderboardScraper.AllInstruments);
        Assert.Contains("Solo_Drums", GlobalLeaderboardScraper.AllInstruments);
        Assert.Contains("Solo_PeripheralGuitar", GlobalLeaderboardScraper.AllInstruments);
        Assert.Contains("Solo_PeripheralBass", GlobalLeaderboardScraper.AllInstruments);
    }

    // ─── LookupAccountAsync ────────────────────────────

    [Fact]
    public async Task LookupAccountAsync_FoundEntry_ReturnsEntry()
    {
        var (scraper, handler) = CreateScraper();

        // V2 response format: flat JSON array
        var json = """
        [
            {
                "teamId": "target_acct",
                "rank": 42,
                "percentile": 0.95,
                "sessionHistory": [
                    {
                        "endTime": "2025-01-01T00:00:00Z",
                        "trackedStats": {
                            "SCORE": 999000,
                            "ACCURACY": 100,
                            "FULL_COMBO": 1,
                            "STARS_EARNED": 5,
                            "SEASON": 3
                        }
                    }
                ]
            }
        ]
        """;
        handler.EnqueueJsonOk(json);

        var entry = await scraper.LookupAccountAsync(
            "song1", "Solo_Guitar", "target_acct", "token", "caller_acct");

        Assert.NotNull(entry);
        Assert.Equal("target_acct", entry!.AccountId);
        Assert.Equal(42, entry.Rank);
        Assert.Equal(999000, entry.Score);
        Assert.Equal(100, entry.Accuracy);
        Assert.True(entry.IsFullCombo);
        Assert.Equal(5, entry.Stars);
        Assert.Equal(3, entry.Season);
    }

    [Fact]
    public async Task LookupAccountAsync_NotFound_ReturnsNull()
    {
        var (scraper, handler) = CreateScraper();

        // V2 response: empty array
        handler.EnqueueJsonOk("[]");

        var entry = await scraper.LookupAccountAsync(
            "song1", "Solo_Guitar", "target_acct", "token", "caller_acct");

        Assert.Null(entry);
    }

    [Fact]
    public async Task LookupAccountAsync_HttpError_Throws()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueError(HttpStatusCode.Forbidden, "Forbidden");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            scraper.LookupAccountAsync(
                "song1", "Solo_Guitar", "target_acct", "token", "caller_acct"));
    }

    [Fact]
    public async Task LookupAccountAsync_NoScoreFound_ReturnsNull()
    {
        var (scraper, handler) = CreateScraper();

        // Epic returns 404 with no_score_found when the leaderboard exists but
        // the player has no entry — this should return null, not throw.
        handler.EnqueueError(HttpStatusCode.NotFound,
            """{"errorCode":"com.epicgames.events.no_score_found","errorMessage":"No score found."}""");

        var entry = await scraper.LookupAccountAsync(
            "song1", "Solo_Guitar", "target_acct", "token", "caller_acct");

        Assert.Null(entry);
    }

    [Fact]
    public async Task LookupAccountAsync_SendsBearerAuth()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueJsonOk("[]");

        await scraper.LookupAccountAsync("s1", "Solo_Guitar", "t1", "my_token", "caller");

        Assert.Single(handler.Requests);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("my_token", handler.Requests[0].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task LookupAccountAsync_UrlContainsV2Format()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueJsonOk("[]");

        await scraper.LookupAccountAsync("song1", "Solo_Guitar", "target_acct", "token", "caller_acct");

        var req = handler.Requests[0];
        var url = req.RequestUri!.ToString();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("/api/v2/games/FNFestival/leaderboards/alltime_song1_Solo_Guitar/alltime/scores", url);
        Assert.Contains("accountId=caller_acct", url);
    }

    // ─── LookupSeasonalAsync ────────────────────────────

    [Fact]
    public async Task LookupSeasonalAsync_UsesWindowId()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueJsonOk("[]");

        await scraper.LookupSeasonalAsync(
            "song1", "Solo_Guitar", "S5Window1", "target", "token", "caller");

        var url = handler.Requests[0].RequestUri!.ToString();
        // V2 seasonal: eventId = S5Window1_song1, windowId = song1_Solo_Guitar
        Assert.Contains("S5Window1_song1", url);
        Assert.Contains("song1_Solo_Guitar", url);
    }

    // ─── ScrapeLeaderboardAsync ─────────────────────────

    [Fact]
    public async Task ScrapeLeaderboardAsync_SinglePage_ReturnsEntries()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueJsonOk("""
        {
            "page": 0, "totalPages": 1,
            "entries": [
                {
                    "teamId": "p1", "rank": 1, "percentile": 1.0,
                    "sessionHistory": [{ "trackedStats": { "SCORE": 500 } }]
                },
                {
                    "teamId": "p2", "rank": 2, "percentile": 0.5,
                    "sessionHistory": [{ "trackedStats": { "SCORE": 300 } }]
                }
            ]
        }
        """);

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct");

        Assert.Equal("song1", result.SongId);
        Assert.Equal("Solo_Guitar", result.Instrument);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(1, result.TotalPages);
        Assert.Equal(1, result.PagesScraped);
    }

    [Fact]
    public async Task ScrapeLeaderboardAsync_EmptyLeaderboard_ReturnsEmpty()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueJsonOk("""{"page":0,"totalPages":0,"entries":[]}""");

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct");

        Assert.Empty(result.Entries);
        Assert.Equal(0, result.TotalPages);
    }

    [Fact]
    public async Task ScrapeLeaderboardAsync_MultiplePages_MergesAll()
    {
        var (scraper, handler) = CreateScraper();

        // Page 0: 2 entries, 2 pages total
        handler.EnqueueJsonOk("""
        {
            "page": 0, "totalPages": 2,
            "entries": [
                { "teamId": "p1", "rank": 1, "percentile": 1.0, "sessionHistory": [{ "trackedStats": { "SCORE": 500 } }] }
            ]
        }
        """);
        // Page 1: 1 entry
        handler.EnqueueJsonOk("""
        {
            "page": 1, "totalPages": 2,
            "entries": [
                { "teamId": "p2", "rank": 2, "percentile": 0.5, "sessionHistory": [{ "trackedStats": { "SCORE": 300 } }] }
            ]
        }
        """);

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct");

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(2, result.PagesScraped);
        Assert.Equal(2, result.TotalPages);
    }

    // ─── Parsing: sessionHistory best score ─────────────

    [Fact]
    public async Task LookupAccountAsync_MultipleSessionHistory_TakesBestScore()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueJsonOk("""
        [
            {
                "teamId": "t1", "rank": 1, "percentile": 1.0,
                "sessionHistory": [
                    { "trackedStats": { "SCORE": 100, "ACCURACY": 80, "STARS_EARNED": 3 } },
                    { "trackedStats": { "SCORE": 500, "ACCURACY": 95, "STARS_EARNED": 5 } },
                    { "trackedStats": { "SCORE": 300, "ACCURACY": 90, "STARS_EARNED": 4 } }
                ]
            }
        ]
        """);

        var entry = await scraper.LookupAccountAsync("s", "Solo_Guitar", "t1", "tok", "c");

        Assert.NotNull(entry);
        Assert.Equal(500, entry!.Score);
        Assert.Equal(95, entry.Accuracy);
        Assert.Equal(5, entry.Stars);
    }

    // ─── Parsing: team_id vs teamId ─────────────────────

    [Fact]
    public async Task LookupAccountAsync_UsesTeamIdFallback()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueJsonOk("""
        [
            {
                "team_id": "t1", "rank": 1, "percentile": 1.0,
                "sessionHistory": [{ "trackedStats": { "SCORE": 1 } }]
            }
        ]
        """);

        var entry = await scraper.LookupAccountAsync("s", "Solo_Guitar", "t1", "tok", "c");

        Assert.NotNull(entry);
        Assert.Equal("t1", entry!.AccountId);
    }

    // ─── Parsing: session without trackedStats ──────────

    [Fact]
    public async Task LookupAccountAsync_SessionMissingTrackedStats_SkipsToNext()
    {
        var (scraper, handler) = CreateScraper();

        // First session has no trackedStats → should be skipped
        // Second session has valid data → should be used
        handler.EnqueueJsonOk("""
        [
            {
                "teamId": "t1", "rank": 1, "percentile": 1.0,
                "sessionHistory": [
                    { "endTime": "2024-01-01T00:00:00Z" },
                    { "trackedStats": { "SCORE": 500 }, "endTime": "2024-06-01T00:00:00Z" }
                ]
            }
        ]
        """);

        var entry = await scraper.LookupAccountAsync("s", "Solo_Guitar", "t1", "tok", "c");

        Assert.NotNull(entry);
        Assert.Equal(500, entry!.Score);
    }

    // ─── ScrapeSongAsync ────────────────────────────────

    [Fact]
    public async Task ScrapeSongAsync_SpecificInstruments_QueriesOnlyThose()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueJsonOk("""{"page":0,"totalPages":0,"entries":[]}""");
        handler.EnqueueJsonOk("""{"page":0,"totalPages":0,"entries":[]}""");

        var instruments = new[] { "Solo_Guitar", "Solo_Bass" };
        var results = await scraper.ScrapeSongAsync(
            "song1", "token", "acct", instruments);

        Assert.Equal(2, results.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    // ─── ScrapeLeaderboardAsync (exercises FetchPageAsync retry logic) ───

    private const string EmptyPage = """{"page":0,"totalPages":0,"entries":[]}""";
    private const string OnePage = """{"page":0,"totalPages":1,"entries":[{"teamId":"a1","rank":1,"percentile":1.0,"sessionHistory":[{"trackedStats":{"SCORE":100}}]}]}""";

    [Fact]
    public async Task ScrapeLeaderboardAsync_ServerError_RetriesAndSucceeds()
    {
        var (scraper, handler) = CreateScraper();

        // First request returns 500, second returns valid data
        handler.EnqueueError(HttpStatusCode.InternalServerError, "server error");
        handler.EnqueueJsonOk(OnePage);

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct");

        Assert.Equal(1, result.Entries.Count);
        Assert.Equal(2, handler.Requests.Count); // 1 retry + 1 success
    }

    [Fact]
    public async Task ScrapeLeaderboardAsync_RateLimit429_RetriesWithBackoff()
    {
        var (scraper, handler) = CreateScraper();

        // 429 response
        var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited"),
        };
        rateLimited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));
        handler.EnqueueResponse(rateLimited);
        handler.EnqueueJsonOk(OnePage);

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct");

        Assert.Equal(1, result.Entries.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ScrapeLeaderboardAsync_NonRetryableError_ReturnsEmpty()
    {
        var (scraper, handler) = CreateScraper();

        // 403 is retried once with 5s backoff, then treated as boundary
        handler.EnqueueError(HttpStatusCode.Forbidden, "forbidden");
        handler.EnqueueError(HttpStatusCode.Forbidden, "forbidden");

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct");

        Assert.Empty(result.Entries);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ScrapeLeaderboardAsync_ParseFailure_RetriesAndFails()
    {
        var (scraper, handler) = CreateScraper();

        // Multiple 200 responses with invalid JSON → parse failures → all retries exhausted
        handler.EnqueueJsonOk("not json {{{");
        handler.EnqueueJsonOk("still not json");
        handler.EnqueueJsonOk("nope");
        handler.EnqueueJsonOk("nah");

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct");

        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task ScrapeLeaderboardAsync_MultiplePages_FetchesAll()
    {
        var (scraper, handler) = CreateScraper();

        // Page 0: reports 2 total pages
        handler.EnqueueJsonOk("""{"page":0,"totalPages":2,"entries":[{"teamId":"a1","rank":1,"percentile":1.0,"sessionHistory":[{"trackedStats":{"SCORE":100}}]}]}""");
        // Page 1
        handler.EnqueueJsonOk("""{"page":1,"totalPages":2,"entries":[{"teamId":"a2","rank":2,"percentile":0.5,"sessionHistory":[{"trackedStats":{"SCORE":200}}]}]}""");

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct");

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(2, result.TotalPages);
        Assert.Equal(2, result.PagesScraped);
    }

    [Fact]
    public async Task ScrapeLeaderboardAsync_500ThenSuccess_OnPageTwo()
    {
        var (scraper, handler) = CreateScraper();

        // Page 0: 2 pages
        handler.EnqueueJsonOk("""{"page":0,"totalPages":2,"entries":[{"teamId":"a1","rank":1,"percentile":1.0,"sessionHistory":[{"trackedStats":{"SCORE":100}}]}]}""");
        // Page 1: first attempt fails with 500
        handler.EnqueueError(HttpStatusCode.InternalServerError, "oops");
        // Page 1: retry succeeds
        handler.EnqueueJsonOk("""{"page":1,"totalPages":2,"entries":[{"teamId":"a2","rank":2,"percentile":0.5,"sessionHistory":[{"trackedStats":{"SCORE":200}}]}]}""");

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct");

        Assert.Equal(2, result.Entries.Count);
    }

    // ─── ScrapeManySongsAsync ───────────────────────────

    [Fact]
    public async Task ScrapeManySongsAsync_MultipleSongs_ReturnsAllResults()
    {
        var (scraper, handler) = CreateScraper();

        // Song 1, instrument 1
        handler.EnqueueJsonOk(OnePage);
        // Song 2, instrument 1
        handler.EnqueueJsonOk(EmptyPage);

        var requests = new List<GlobalLeaderboardScraper.SongScrapeRequest>
        {
            new() { SongId = "s1", Instruments = new[] { "Solo_Guitar" }, Label = "Song 1" },
            new() { SongId = "s2", Instruments = new[] { "Solo_Guitar" }, Label = "Song 2" },
        };

        var songCompleteIds = new List<string>();
        var results = await scraper.ScrapeManySongsAsync(
            requests, "token", "acct", 4,
            onSongComplete: (songId, songResults) =>
            {
                lock (songCompleteIds) songCompleteIds.Add(songId);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(2, results.Count);
        Assert.True(results.ContainsKey("s1"));
        Assert.True(results.ContainsKey("s2"));
        Assert.Equal(2, songCompleteIds.Count);
    }

    [Fact]
    public async Task ScrapeManySongsAsync_NoCallback_StillWorks()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueJsonOk(EmptyPage);

        var requests = new List<GlobalLeaderboardScraper.SongScrapeRequest>
        {
            new() { SongId = "s1", Instruments = new[] { "Solo_Guitar" } },
        };

        var results = await scraper.ScrapeManySongsAsync(
            requests, "token", "acct", 4, onSongComplete: null);

        Assert.Single(results);
    }

    // ─── Retry: HttpRequestException ───

    [Fact]
    public async Task ScrapeLeaderboardAsync_HttpRequestException_RetriesAndSucceeds()
    {
        var (scraper, handler) = CreateScraper();

        // First attempt throws network error, second succeeds
        handler.EnqueueException(new HttpRequestException("Connection refused"));
        handler.EnqueueJsonOk(OnePage);

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct");

        Assert.Equal(1, result.Entries.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    // ─── Retry: TaskCanceledException (timeout, not cancellation) ───

    [Fact]
    public async Task ScrapeLeaderboardAsync_Timeout_RetriesAndSucceeds()
    {
        var (scraper, handler) = CreateScraper();

        // Timeout (TaskCanceledException with non-cancelled token), then success
        handler.EnqueueException(new TaskCanceledException("The request timed out"));
        handler.EnqueueJsonOk(OnePage);

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct");

        Assert.Equal(1, result.Entries.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    // ─── HttpRequestException exhausts all retries ───

    [Fact]
    public async Task ScrapeLeaderboardAsync_HttpRequestException_ExhaustsRetries_ReturnsEmpty()
    {
        var (scraper, handler) = CreateScraper();

        // All attempts throw → should exhaust retries and propagate
        for (int i = 0; i <= 3; i++)
            handler.EnqueueException(new HttpRequestException("Connection refused"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            scraper.ScrapeLeaderboardAsync("song1", "Solo_Guitar", "token", "acct"));
    }

    // ─── ParseAllSessionsFromEntry ─────────────────────

    [Fact]
    public void ParseAllSessionsFromEntry_MultipleSessions_ReturnsAll()
    {
        var json = """
        {
            "teamId": "acct1",
            "rank": 42,
            "percentile": 0.95,
            "sessionHistory": [
                {
                    "endTime": "2025-01-01T00:00:00Z",
                    "trackedStats": { "SCORE": 100000, "ACCURACY": 800000, "FULL_COMBO": 0, "STARS_EARNED": 3, "SEASON": 7 }
                },
                {
                    "endTime": "2025-01-15T00:00:00Z",
                    "trackedStats": { "SCORE": 300000, "ACCURACY": 900000, "FULL_COMBO": 0, "STARS_EARNED": 4, "SEASON": 7 }
                },
                {
                    "endTime": "2025-02-01T00:00:00Z",
                    "trackedStats": { "SCORE": 500000, "ACCURACY": 950000, "FULL_COMBO": 1, "STARS_EARNED": 5, "SEASON": 7 }
                }
            ]
        }
        """;

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var sessions = GlobalLeaderboardScraper.ParseAllSessionsFromEntry(
            doc.RootElement, "acct1", 42, 0.95);

        Assert.Equal(3, sessions.Count);

        Assert.Equal(100000, sessions[0].Score);
        Assert.Equal(800000, sessions[0].Accuracy);
        Assert.False(sessions[0].IsFullCombo);
        Assert.Equal(3, sessions[0].Stars);
        Assert.Equal("2025-01-01T00:00:00Z", sessions[0].EndTime);
        Assert.Equal("acct1", sessions[0].AccountId);
        Assert.Equal(42, sessions[0].Rank);

        Assert.Equal(300000, sessions[1].Score);
        Assert.Equal(900000, sessions[1].Accuracy);

        Assert.Equal(500000, sessions[2].Score);
        Assert.True(sessions[2].IsFullCombo);
        Assert.Equal(5, sessions[2].Stars);
    }

    [Fact]
    public void ParseAllSessionsFromEntry_NoSessionHistory_ReturnsEmpty()
    {
        var json = """{ "teamId": "acct1", "rank": 1, "percentile": 1.0 }""";

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var sessions = GlobalLeaderboardScraper.ParseAllSessionsFromEntry(
            doc.RootElement, "acct1", 1, 1.0);

        Assert.Empty(sessions);
    }

    [Fact]
    public void ParseAllSessionsFromEntry_SingleSession_ReturnsList()
    {
        var json = """
        {
            "teamId": "acct1",
            "rank": 10,
            "percentile": 0.5,
            "sessionHistory": [
                {
                    "endTime": "2025-03-01T12:00:00Z",
                    "trackedStats": { "SCORE": 750000, "ACCURACY": 990000, "FULL_COMBO": 1, "STARS_EARNED": 6, "SEASON": 9 }
                }
            ]
        }
        """;

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var sessions = GlobalLeaderboardScraper.ParseAllSessionsFromEntry(
            doc.RootElement, "acct1", 10, 0.5);

        Assert.Single(sessions);
        Assert.Equal(750000, sessions[0].Score);
        Assert.Equal(9, sessions[0].Season);
    }

    // ─── ParseV2AllSessionsResponseAsync ────────────────

    [Fact]
    public async Task ParseV2AllSessionsResponseAsync_MultipleSessionsInResponse_ReturnsAll()
    {
        var json = """
        [
            {
                "teamId": "target_acct",
                "rank": 5,
                "percentile": 0.99,
                "sessionHistory": [
                    { "endTime": "2025-01-01T00:00:00Z", "trackedStats": { "SCORE": 200000, "ACCURACY": 850000 } },
                    { "endTime": "2025-01-10T00:00:00Z", "trackedStats": { "SCORE": 400000, "ACCURACY": 920000 } },
                    { "endTime": "2025-01-20T00:00:00Z", "trackedStats": { "SCORE": 600000, "ACCURACY": 980000, "FULL_COMBO": 1 } }
                ]
            }
        ]
        """;

        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var sessions = await GlobalLeaderboardScraper.ParseV2AllSessionsResponseAsync(
            stream, "target_acct", CancellationToken.None);

        Assert.NotNull(sessions);
        Assert.Equal(3, sessions!.Count);
        Assert.Equal(200000, sessions[0].Score);
        Assert.Equal(400000, sessions[1].Score);
        Assert.Equal(600000, sessions[2].Score);
        Assert.True(sessions[2].IsFullCombo);
        // Rank and percentile are shared across all sessions from the same entry
        Assert.All(sessions, s => Assert.Equal(5, s.Rank));
        Assert.All(sessions, s => Assert.Equal(0.99, s.Percentile));
    }

    [Fact]
    public async Task ParseV2AllSessionsResponseAsync_TargetNotFound_ReturnsNull()
    {
        var json = """
        [
            {
                "teamId": "other_acct",
                "rank": 1,
                "sessionHistory": [
                    { "trackedStats": { "SCORE": 100000 } }
                ]
            }
        ]
        """;

        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var sessions = await GlobalLeaderboardScraper.ParseV2AllSessionsResponseAsync(
            stream, "target_acct", CancellationToken.None);

        Assert.Null(sessions);
    }

    [Fact]
    public async Task ParseV2AllSessionsResponseAsync_EmptyArray_ReturnsNull()
    {
        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes("[]"));
        var sessions = await GlobalLeaderboardScraper.ParseV2AllSessionsResponseAsync(
            stream, "target_acct", CancellationToken.None);

        Assert.Null(sessions);
    }

    // ─── LookupSeasonalSessionsAsync (integration via MockHttp) ─

    [Fact]
    public async Task LookupSeasonalSessionsAsync_ReturnsAllSessions()
    {
        var (scraper, handler) = CreateScraper();

        var json = """
        [
            {
                "teamId": "target_acct",
                "rank": 84,
                "percentile": 0.5,
                "sessionHistory": [
                    { "endTime": "2025-03-15T00:00:00Z", "trackedStats": { "SCORE": 412000, "ACCURACY": 850000, "STARS_EARNED": 4, "SEASON": 7 } },
                    { "endTime": "2025-03-20T00:00:00Z", "trackedStats": { "SCORE": 550000, "ACCURACY": 920000, "STARS_EARNED": 5, "SEASON": 7 } },
                    { "endTime": "2025-04-06T00:00:00Z", "trackedStats": { "SCORE": 668157, "ACCURACY": 990000, "FULL_COMBO": 0, "STARS_EARNED": 6, "SEASON": 7 } }
                ]
            }
        ]
        """;
        handler.EnqueueJsonOk(json);

        var sessions = await scraper.LookupSeasonalSessionsAsync(
            "song1", "Solo_Guitar", "evergreen", "target_acct", "token", "caller_acct");

        Assert.NotNull(sessions);
        Assert.Equal(3, sessions!.Count);
        Assert.Equal(412000, sessions[0].Score);
        Assert.Equal(550000, sessions[1].Score);
        Assert.Equal(668157, sessions[2].Score);
    }

    [Fact]
    public async Task LookupSeasonalSessionsAsync_NoScoreFound_ReturnsNull()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueError(HttpStatusCode.NotFound,
            """{"errorCode":"com.epicgames.events.no_score_found","errorMessage":"No score found."}""");

        var sessions = await scraper.LookupSeasonalSessionsAsync(
            "song1", "Solo_Guitar", "evergreen", "target_acct", "token", "caller_acct");

        Assert.Null(sessions);
    }

    [Fact]
    public async Task LookupSeasonalSessionsAsync_HttpError_Throws()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueError(HttpStatusCode.Forbidden, "Forbidden");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            scraper.LookupSeasonalSessionsAsync(
                "song1", "Solo_Guitar", "evergreen", "target_acct", "token", "caller_acct"));
    }
}
