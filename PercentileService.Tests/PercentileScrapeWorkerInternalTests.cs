using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PercentileService.Tests.Helpers;

namespace PercentileService.Tests;

public sealed class PercentileScrapeWorkerInternalTests
{
    [Fact]
    public void CalculateDelayUntilNextRun_returns_positive_timespan()
    {
        var worker = CreateWorker();
        var delay = worker.CalculateDelayUntilNextRun();

        Assert.True(delay > TimeSpan.Zero);
        Assert.True(delay <= TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void CalculateDelayUntilNextRun_with_invalid_time_uses_fallback()
    {
        var worker = CreateWorker(o => o.ScrapeTimeOfDay = "invalid-time");
        var delay = worker.CalculateDelayUntilNextRun();

        // Should fall back to 03:30 and return a positive delay
        Assert.True(delay > TimeSpan.Zero);
    }

    [Fact]
    public void CalculateDelayUntilNextRun_schedules_for_tomorrow_if_past()
    {
        // Set scrape time to 1 minute ago in UTC terms
        var now = DateTime.UtcNow;
        var pastTime = now.AddMinutes(-1);
        var worker = CreateWorker(o =>
        {
            o.ScrapeTimeOfDay = pastTime.ToString("HH:mm");
            o.ScrapeTimeZone = "UTC";
        });

        var delay = worker.CalculateDelayUntilNextRun();

        // Should schedule for tomorrow, so delay should be ~23h+
        Assert.True(delay > TimeSpan.FromHours(23));
    }

    [Fact]
    public async Task QueryWithAdaptiveLimiter_returns_result_and_tracks_progress()
    {
        var json = """
        {
            "entries": [
                {
                    "teamAccountIds": ["test-acct"],
                    "rank": 5,
                    "score": 100000,
                    "percentile": 0.001
                }
            ]
        }
        """;
        var handler = MockHttpHandler.WithJsonResponse(json);
        var (worker, progress) = CreateWorkerWithAuth(handler);
        progress.BeginScrape(1);

        using var limiter = new AdaptiveConcurrencyLimiter(1, 1, 10,
            Substitute.For<ILogger>());
        await limiter.WaitAsync(CancellationToken.None);
        var results = new List<LeaderboardPopulationItem>();
        var resultsLock = new object();

        await worker.QueryWithAdaptiveLimiter(
            "song1", "Solo_Guitar", limiter, results, resultsLock, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("song1", results[0].SongId);
    }

    [Fact]
    public async Task QueryWithAdaptiveLimiter_throws_when_no_access_token()
    {
        var handler = MockHttpHandler.WithJsonResponse("{}");
        var (worker, progress) = CreateWorkerPair(); // No auth → null token
        progress.BeginScrape(1);

        using var limiter = new AdaptiveConcurrencyLimiter(1, 1, 10,
            Substitute.For<ILogger>());
        await limiter.WaitAsync(CancellationToken.None);
        var results = new List<LeaderboardPopulationItem>();
        var resultsLock = new object();

        // With adaptive limiter, errors are caught - verify it doesn't add to results
        await worker.QueryWithAdaptiveLimiter(
            "s", "i", limiter, results, resultsLock, CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public async Task QueryWithAdaptiveLimiter_skipped_when_null_result()
    {
        // 404 response → querier returns null → skipped
        var handler = MockHttpHandler.WithJsonResponse("{}", HttpStatusCode.NotFound);
        var (worker, progress) = CreateWorkerWithAuth(handler);
        progress.BeginScrape(1);

        using var limiter = new AdaptiveConcurrencyLimiter(1, 1, 10,
            Substitute.For<ILogger>());
        await limiter.WaitAsync(CancellationToken.None);
        var results = new List<LeaderboardPopulationItem>();
        var resultsLock = new object();

        await worker.QueryWithAdaptiveLimiter(
            "s", "i", limiter, results, resultsLock, CancellationToken.None);

        Assert.Empty(results);
        var snap = progress.GetProgressResponse();
        Assert.Equal(1, snap.Entries!.Skipped);
    }

    [Fact]
    public async Task QueryWithAdaptiveLimiter_failed_when_zero_percentile()
    {
        var json = """
        {
            "entries": [{
                "teamAccountIds": ["test-acct"],
                "rank": 1, "score": 200, "percentile": 0.0
            }]
        }
        """;
        var handler = MockHttpHandler.WithJsonResponse(json);
        var (worker, progress) = CreateWorkerWithAuth(handler);
        progress.BeginScrape(1);

        using var limiter = new AdaptiveConcurrencyLimiter(1, 1, 10,
            Substitute.For<ILogger>());
        await limiter.WaitAsync(CancellationToken.None);
        var results = new List<LeaderboardPopulationItem>();
        var resultsLock = new object();

        await worker.QueryWithAdaptiveLimiter(
            "s", "i", limiter, results, resultsLock, CancellationToken.None);

        Assert.Empty(results);
        var snap = progress.GetProgressResponse();
        Assert.Equal(1, snap.Entries!.Failed);
    }

    [Fact]
    public async Task RunScrapeAsync_with_entries_queries_and_posts()
    {
        // FstClient returns 2 entries
        var profileJson = """
        {
            "scores": [
                { "songId": "s1", "instrument": "Solo_Guitar", "score": 500 },
                { "songId": "s2", "instrument": "Solo_Drums", "score": 300 }
            ]
        }
        """;

        // V1 API returns percentile data for each
        var v1Json = """
        {
            "entries": [
                {
                    "teamAccountIds": ["test-acct"],
                    "rank": 10,
                    "score": 500,
                    "percentile": 0.0001
                }
            ]
        }
        """;

        var postResponse = """{ "upserted": 2 }""";

        var fstHandler = new MockHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(postResponse, System.Text.Encoding.UTF8, "application/json"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(profileJson, System.Text.Encoding.UTF8, "application/json"),
            });
        });

        var v1Handler = MockHttpHandler.WithJsonResponse(v1Json);

        var opts = Options.Create(new PercentileOptions
        {
            AccountId = "test-acct",
            TokenPath = Path.Combine(Path.GetTempPath(), $"rs-{Guid.NewGuid():N}.json"),
            FstBaseUrl = "http://localhost:9999",
            FstApiKey = "key",
            DegreeOfParallelism = 2,
            StartingDegreeOfParallelism = 2,
            MaxDegreeOfParallelism = 4,
            MinDegreeOfParallelism = 1,
        });

        // Create token manager with auth
        var tokenJson = """
        {
            "access_token": "tok", "refresh_token": "ref",
            "displayName": "U", "account_id": "test-acct", "expires_in": 9999
        }
        """;
        var tokenHandler = MockHttpHandler.WithJsonResponse(tokenJson);
        var tokenMgr = new EpicTokenManager(
            new HttpClient(tokenHandler),
            Substitute.For<ILogger<EpicTokenManager>>(), opts);

        // Seed and authenticate
        await SeedCredentials(opts.Value.TokenPath, "test-acct", "U", "r");
        await tokenMgr.EnsureAuthenticatedAsync(CancellationToken.None);

        var querier = new LeaderboardQuerier(
            new HttpClient(v1Handler),
            Substitute.For<ILogger<LeaderboardQuerier>>());

        var fstClient = new FstClient(
            new HttpClient(fstHandler), opts,
            Substitute.For<ILogger<FstClient>>());

        var progress = new PercentileScrapeProgressTracker();
        var worker = new PercentileScrapeWorker(
            tokenMgr, querier, fstClient, opts,
            Substitute.For<ILogger<PercentileScrapeWorker>>(),
            progress);

        await worker.RunScrapeAsync(CancellationToken.None);

        // Verify V1 queries were made
        Assert.Equal(2, v1Handler.Requests.Count);

        // Verify POST was made to FstService
        Assert.True(fstHandler.Requests.Any(r => r.Method == HttpMethod.Post));

        // Verify progress was tracked
        Assert.False(progress.IsRunning);
        var snap = progress.GetProgressResponse();
        Assert.Equal(2, snap.Entries!.Succeeded);

        // Cleanup
        try { File.Delete(opts.Value.TokenPath); } catch { }
    }

    [Fact]
    public async Task RunScrapeAsync_skips_when_no_entries()
    {
        var profileJson = """{ "scores": [] }""";
        var fstHandler = MockHttpHandler.WithJsonResponse(profileJson);
        var v1Handler = MockHttpHandler.WithJsonResponse("{}");

        var opts = Options.Create(new PercentileOptions
        {
            AccountId = "test-acct",
            TokenPath = Path.Combine(Path.GetTempPath(), $"rs2-{Guid.NewGuid():N}.json"),
            FstBaseUrl = "http://localhost:9999",
            FstApiKey = "key",
            DegreeOfParallelism = 1,
            StartingDegreeOfParallelism = 1,
            MaxDegreeOfParallelism = 2,
            MinDegreeOfParallelism = 1,
        });

        var tokenJson = """
        {
            "access_token": "t", "refresh_token": "r",
            "displayName": "U", "account_id": "test-acct", "expires_in": 9999
        }
        """;
        var tokenHandler = MockHttpHandler.WithJsonResponse(tokenJson);
        var tokenMgr = new EpicTokenManager(
            new HttpClient(tokenHandler),
            Substitute.For<ILogger<EpicTokenManager>>(), opts);

        await SeedCredentials(opts.Value.TokenPath, "test-acct", "U", "r");
        await tokenMgr.EnsureAuthenticatedAsync(CancellationToken.None);

        var querier = new LeaderboardQuerier(
            new HttpClient(v1Handler),
            Substitute.For<ILogger<LeaderboardQuerier>>());
        var fstClient = new FstClient(
            new HttpClient(fstHandler), opts,
            Substitute.For<ILogger<FstClient>>());

        var progress = new PercentileScrapeProgressTracker();
        var worker = new PercentileScrapeWorker(
            tokenMgr, querier, fstClient, opts,
            Substitute.For<ILogger<PercentileScrapeWorker>>(),
            progress);

        await worker.RunScrapeAsync(CancellationToken.None);

        // No V1 queries should have been made
        Assert.Empty(v1Handler.Requests);

        // Progress should not have been started (no entries)
        Assert.False(progress.IsRunning);

        // Cleanup
        try { File.Delete(opts.Value.TokenPath); } catch { }
    }

    [Fact]
    public async Task RunScrapeAsync_handles_null_and_failed_results()
    {
        var profileJson = """
        {
            "scores": [
                { "songId": "s1", "instrument": "Solo_Guitar", "score": 100 },
                { "songId": "s2", "instrument": "Solo_Drums", "score": 200 },
                { "songId": "s3", "instrument": "Solo_Bass", "score": 300 }
            ]
        }
        """;

        int v1Call = 0;
        var v1Handler = new MockHttpHandler(req =>
        {
            int call = Interlocked.Increment(ref v1Call);
            return call switch
            {
                1 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)), // null
                2 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) // percentile=0 → totalEntries=-1
                {
                    Content = new StringContent("""
                    {
                        "entries": [{
                            "teamAccountIds": ["test-acct"],
                            "rank": 1, "score": 200, "percentile": 0.0
                        }]
                    }
                    """, System.Text.Encoding.UTF8, "application/json"),
                }),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                        "entries": [{
                            "teamAccountIds": ["test-acct"],
                            "rank": 5, "score": 300, "percentile": 0.001
                        }]
                    }
                    """, System.Text.Encoding.UTF8, "application/json"),
                }),
            };
        });

        var fstHandler = new MockHttpHandler(req =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    req.Method == HttpMethod.Post ? """{"upserted":1}""" : profileJson,
                    System.Text.Encoding.UTF8, "application/json"),
            });
        });

        var opts = Options.Create(new PercentileOptions
        {
            AccountId = "test-acct",
            TokenPath = Path.Combine(Path.GetTempPath(), $"rs3-{Guid.NewGuid():N}.json"),
            FstBaseUrl = "http://localhost:9999",
            FstApiKey = "key",
            DegreeOfParallelism = 1, // Sequential for predictable ordering
            StartingDegreeOfParallelism = 1,
            MaxDegreeOfParallelism = 2,
            MinDegreeOfParallelism = 1,
        });

        var tokenJson = """
        {
            "access_token": "t", "refresh_token": "r",
            "displayName": "U", "account_id": "test-acct", "expires_in": 9999
        }
        """;
        var tokenHandler = MockHttpHandler.WithJsonResponse(tokenJson);
        var tokenMgr = new EpicTokenManager(
            new HttpClient(tokenHandler),
            Substitute.For<ILogger<EpicTokenManager>>(), opts);

        await SeedCredentials(opts.Value.TokenPath, "test-acct", "U", "r");
        await tokenMgr.EnsureAuthenticatedAsync(CancellationToken.None);

        var querier = new LeaderboardQuerier(
            new HttpClient(v1Handler),
            Substitute.For<ILogger<LeaderboardQuerier>>());
        var fstClient = new FstClient(
            new HttpClient(fstHandler), opts,
            Substitute.For<ILogger<FstClient>>());

        var progress = new PercentileScrapeProgressTracker();
        var worker = new PercentileScrapeWorker(
            tokenMgr, querier, fstClient, opts,
            Substitute.For<ILogger<PercentileScrapeWorker>>(),
            progress);

        await worker.RunScrapeAsync(CancellationToken.None);

        // 3 V1 queries: 1 null (404), 1 failed (percentile=0), 1 succeeded
        Assert.Equal(3, v1Handler.Requests.Count);

        // Only 1 entry should be posted (the one with valid percentile)
        var postReqs = fstHandler.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.Single(postReqs);

        // Verify progress tracking
        var snap = progress.GetProgressResponse();
        Assert.Equal(1, snap.Entries!.Succeeded);
        Assert.Equal(1, snap.Entries!.Failed);
        Assert.Equal(1, snap.Entries!.Skipped);

        // Cleanup
        try { File.Delete(opts.Value.TokenPath); } catch { }
    }

    // ─── Helpers ────────────────────────────────────────────────

    private static PercentileScrapeWorker CreateWorker(Action<PercentileOptions>? configure = null)
    {
        return CreateWorkerCore(MockHttpHandler.WithJsonResponse("{}"), configure);
    }

    private static (PercentileScrapeWorker worker, PercentileScrapeProgressTracker progress) CreateWorkerPair(Action<PercentileOptions>? configure = null)
    {
        var handler = MockHttpHandler.WithJsonResponse("{}");
        return CreateWorkerCorePair(handler, configure);
    }

    private static (PercentileScrapeWorker worker, PercentileScrapeProgressTracker progress) CreateWorkerWithAuth(MockHttpHandler v1Handler)
    {
        var opts = Options.Create(new PercentileOptions
        {
            AccountId = "test-acct",
            ScrapeTimeOfDay = "03:30",
            ScrapeTimeZone = "UTC",
            DegreeOfParallelism = 1,
            StartingDegreeOfParallelism = 1,
            MaxDegreeOfParallelism = 10,
            MinDegreeOfParallelism = 1,
            FstBaseUrl = "http://localhost:9999",
        });

        // Create a token manager and inject a token manually via ApplyTokenResponse
        var tokenMgr = new EpicTokenManager(
            new HttpClient(MockHttpHandler.WithJsonResponse("{}")),
            Substitute.For<ILogger<EpicTokenManager>>(), opts);
        tokenMgr.ApplyTokenResponse("""
        {
            "access_token": "tok", "refresh_token": "ref",
            "displayName": "Test", "account_id": "test-acct", "expires_in": 9999
        }
        """);

        var querier = new LeaderboardQuerier(
            new HttpClient(v1Handler),
            Substitute.For<ILogger<LeaderboardQuerier>>());

        var fstLog = Substitute.For<ILogger<FstClient>>();
        var fstClient = new FstClient(
            new HttpClient(MockHttpHandler.WithJsonResponse("{}")), opts, fstLog);

        var progress = new PercentileScrapeProgressTracker();
        var worker = new PercentileScrapeWorker(
            tokenMgr, querier, fstClient, opts,
            Substitute.For<ILogger<PercentileScrapeWorker>>(),
            progress);

        return (worker, progress);
    }

    private static PercentileScrapeWorker CreateWorkerCore(MockHttpHandler handler, Action<PercentileOptions>? configure = null)
    {
        return CreateWorkerCorePair(handler, configure).worker;
    }

    private static (PercentileScrapeWorker worker, PercentileScrapeProgressTracker progress) CreateWorkerCorePair(
        MockHttpHandler handler, Action<PercentileOptions>? configure = null)
    {
        var opts = new PercentileOptions
        {
            AccountId = "test-acct",
            ScrapeTimeOfDay = "03:30",
            ScrapeTimeZone = "UTC",
            DegreeOfParallelism = 1,
            StartingDegreeOfParallelism = 1,
            MaxDegreeOfParallelism = 10,
            MinDegreeOfParallelism = 1,
            FstBaseUrl = "http://localhost:9999",
        };
        configure?.Invoke(opts);
        var optionsWrapper = Options.Create(opts);

        var tokenMgr = new EpicTokenManager(
            new HttpClient(handler),
            Substitute.For<ILogger<EpicTokenManager>>(), optionsWrapper);
        var querier = new LeaderboardQuerier(
            new HttpClient(handler),
            Substitute.For<ILogger<LeaderboardQuerier>>());
        var fstLog = Substitute.For<ILogger<FstClient>>();
        var fstClient = new FstClient(
            new HttpClient(handler), optionsWrapper, fstLog);
        var workerLog = Substitute.For<ILogger<PercentileScrapeWorker>>();
        var progress = new PercentileScrapeProgressTracker();

        var worker = new PercentileScrapeWorker(tokenMgr, querier, fstClient, optionsWrapper, workerLog, progress);
        return (worker, progress);
    }

    private static async Task SeedCredentials(string path, string accountId, string displayName, string refreshToken)
    {
        var creds = new StoredPercentileCredentials
        {
            AccountId = accountId,
            DisplayName = displayName,
            RefreshToken = refreshToken,
            SavedAt = DateTimeOffset.UtcNow.ToString("o"),
        };
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path,
            System.Text.Json.JsonSerializer.Serialize(creds));
    }
}
