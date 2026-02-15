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
        var scraper = new GlobalLeaderboardScraper(http, _progress, _log);
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

    // ─── GetAvailableInstruments ────────────────────────

    [Fact]
    public void GetAvailableInstruments_AllZero_ReturnsEmpty()
    {
        var song = new Song
        {
            track = new Track { su = "song1", @in = new In() }
        };
        var result = GlobalLeaderboardScraper.GetAvailableInstruments(song);
        Assert.Empty(result);
    }

    [Fact]
    public void GetAvailableInstruments_SomeCharted_ReturnsOnlyThose()
    {
        var song = new Song
        {
            track = new Track
            {
                su = "song1",
                @in = new In { gr = 5, ba = 0, vl = 3, ds = 0, pg = 0, pb = 0 }
            }
        };
        var result = GlobalLeaderboardScraper.GetAvailableInstruments(song);
        Assert.Equal(2, result.Count);
        Assert.Contains("Solo_Guitar", result);
        Assert.Contains("Solo_Vocals", result);
    }

    [Fact]
    public void GetAvailableInstruments_AllCharted_Returns6()
    {
        var song = new Song
        {
            track = new Track
            {
                su = "song1",
                @in = new In { gr = 1, ba = 2, vl = 3, ds = 4, pg = 5, pb = 6 }
            }
        };
        var result = GlobalLeaderboardScraper.GetAvailableInstruments(song);
        Assert.Equal(6, result.Count);
    }

    [Fact]
    public void GetAvailableInstruments_NullDifficulty_ReturnsEmpty()
    {
        var song = new Song { track = new Track { su = "song1" } };
        var result = GlobalLeaderboardScraper.GetAvailableInstruments(song);
        Assert.Empty(result);
    }

    // ─── LookupAccountAsync ────────────────────────────

    [Fact]
    public async Task LookupAccountAsync_FoundEntry_ReturnsEntry()
    {
        var (scraper, handler) = CreateScraper();

        var json = """
        {
            "page": 0,
            "totalPages": 1,
            "entries": [
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
        }
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

        handler.EnqueueJsonOk("""{"page":0,"totalPages":0,"entries":[]}""");

        var entry = await scraper.LookupAccountAsync(
            "song1", "Solo_Guitar", "target_acct", "token", "caller_acct");

        Assert.Null(entry);
    }

    [Fact]
    public async Task LookupAccountAsync_HttpError_ReturnsNull()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueError(HttpStatusCode.Forbidden, "Forbidden");

        var entry = await scraper.LookupAccountAsync(
            "song1", "Solo_Guitar", "target_acct", "token", "caller_acct");

        Assert.Null(entry);
    }

    [Fact]
    public async Task LookupAccountAsync_SendsBearerAuth()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueJsonOk("""{"page":0,"totalPages":0,"entries":[]}""");

        await scraper.LookupAccountAsync("s1", "Solo_Guitar", "t1", "my_token", "caller");

        Assert.Single(handler.Requests);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("my_token", handler.Requests[0].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task LookupAccountAsync_UrlContainsTeamAccountIds()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueJsonOk("""{"page":0,"totalPages":0,"entries":[]}""");

        await scraper.LookupAccountAsync("song1", "Solo_Guitar", "target_acct", "token", "caller_acct");

        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("teamAccountIds=target_acct", url);
        Assert.Contains("alltime_song1_Solo_Guitar", url);
    }

    // ─── LookupSeasonalAsync ────────────────────────────

    [Fact]
    public async Task LookupSeasonalAsync_UsesWindowId()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueJsonOk("""{"page":0,"totalPages":0,"entries":[]}""");

        await scraper.LookupSeasonalAsync(
            "song1", "Solo_Guitar", "S5Window1", "target", "token", "caller");

        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("/S5Window1/", url);
    }

    // ─── LookupAccountAllInstrumentsAsync ───────────────

    [Fact]
    public async Task LookupAccountAllInstrumentsAsync_ReturnsOnlyNonNull()
    {
        var (scraper, handler) = CreateScraper();

        // Guitar = found
        var foundJson = """
        {
            "page": 0, "totalPages": 1,
            "entries": [{
                "teamId": "t1", "rank": 1, "percentile": 1.0,
                "sessionHistory": [{ "trackedStats": { "SCORE": 100 } }]
            }]
        }
        """;
        // Bass = empty
        var emptyJson = """{"page":0,"totalPages":0,"entries":[]}""";

        // Enqueue for 2 instruments
        handler.EnqueueJsonOk(foundJson);
        handler.EnqueueJsonOk(emptyJson);

        var instruments = new[] { "Solo_Guitar", "Solo_Bass" };
        var result = await scraper.LookupAccountAllInstrumentsAsync(
            "song1", "t1", "token", "caller", instruments);

        Assert.Single(result);
        Assert.True(result.ContainsKey("Solo_Guitar"));
        Assert.False(result.ContainsKey("Solo_Bass"));
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
        {
            "page": 0, "totalPages": 1,
            "entries": [{
                "teamId": "t1", "rank": 1, "percentile": 1.0,
                "sessionHistory": [
                    { "trackedStats": { "SCORE": 100, "ACCURACY": 80, "STARS_EARNED": 3 } },
                    { "trackedStats": { "SCORE": 500, "ACCURACY": 95, "STARS_EARNED": 5 } },
                    { "trackedStats": { "SCORE": 300, "ACCURACY": 90, "STARS_EARNED": 4 } }
                ]
            }]
        }
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
        {
            "page": 0, "totalPages": 1,
            "entries": [{
                "team_id": "t1", "rank": 1, "percentile": 1.0,
                "sessionHistory": [{ "trackedStats": { "SCORE": 1 } }]
            }]
        }
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
        {
            "page": 0, "totalPages": 1,
            "entries": [{
                "teamId": "t1", "rank": 1, "percentile": 1.0,
                "sessionHistory": [
                    { "endTime": "2024-01-01T00:00:00Z" },
                    { "trackedStats": { "SCORE": 500 }, "endTime": "2024-06-01T00:00:00Z" }
                ]
            }]
        }
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

        handler.EnqueueError(HttpStatusCode.Forbidden, "forbidden");

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct");

        Assert.Empty(result.Entries);
        Assert.Equal(1, handler.Requests.Count);
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
}
