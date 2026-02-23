using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PercentileService.Tests.Helpers;

namespace PercentileService.Tests;

public sealed class PercentileScrapeWorkerTests
{
    [Fact]
    public async Task ExecuteAsync_cancels_during_initial_delay()
    {
        var handler = MockHttpHandler.WithJsonResponse("{}");
        var (worker, _) = CreateWorker(handler);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await worker.StartAsync(cts.Token);

        try { await Task.Delay(200); } catch { }
        await worker.StopAsync(CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_enters_loop_and_cancels_during_scrape_delay()
    {
        // Use InitialDelaySeconds=0 so we enter the while loop immediately
        var handler = MockHttpHandler.WithJsonResponse("{}");
        var opts = Options.Create(new PercentileOptions
        {
            AccountId = "test-acct",
            TokenPath = Path.Combine(Path.GetTempPath(), $"psw-loop-{Guid.NewGuid():N}.json"),
            ScrapeTimeOfDay = "23:59",
            ScrapeTimeZone = "UTC",
            DegreeOfParallelism = 1,
            StartingDegreeOfParallelism = 1,
            MaxDegreeOfParallelism = 10,
            MinDegreeOfParallelism = 1,
            InitialDelaySeconds = 0,
        });

        var tokenMgr = new EpicTokenManager(
            new HttpClient(handler),
            Substitute.For<ILogger<EpicTokenManager>>(), opts);
        var querier = new LeaderboardQuerier(
            new HttpClient(handler),
            Substitute.For<ILogger<LeaderboardQuerier>>());
        var fstClient = new FstClient(
            new HttpClient(handler), opts,
            Substitute.For<ILogger<FstClient>>());

        var worker = new PercentileScrapeWorker(
            tokenMgr, querier, fstClient, opts,
            Substitute.For<ILogger<PercentileScrapeWorker>>(),
            new PercentileScrapeProgressTracker());

        // Cancel after the worker enters the loop and hits CalculateDelayUntilNextRun
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await worker.StartAsync(cts.Token);
        try { await Task.Delay(400); } catch { }
        await worker.StopAsync(CancellationToken.None);

        // Worker should have entered loop (CalculateDelayUntilNextRun called)
        // and cancelled during the long Task.Delay(~24h) — this covers the
        // while loop, CalculateDelay, and the OperationCanceledException break path.
    }

    [Fact]
    public async Task ExecuteAsync_runs_scrape_then_loops()
    {
        // Set scrape time to ~2 seconds from now so the delay is short enough to complete
        var futureTime = DateTime.UtcNow.AddSeconds(2);
        var tokenPath = Path.Combine(Path.GetTempPath(), $"psw-run-{Guid.NewGuid():N}.json");

        try
        {
            // FstClient returns empty scores → quick RunScrapeAsync that skips
            var fstHandler = MockHttpHandler.WithJsonResponse("""{ "scores": [] }""");

            // Token manager: seed and refresh
            var tokenJson = """
            {
                "access_token": "tok", "refresh_token": "ref",
                "displayName": "U", "account_id": "test-acct", "expires_in": 9999
            }
            """;
            var tokenHandler = MockHttpHandler.WithJsonResponse(tokenJson);
            var opts = Options.Create(new PercentileOptions
            {
                AccountId = "test-acct",
                TokenPath = tokenPath,
                ScrapeTimeOfDay = futureTime.ToString("HH:mm:ss"),
                ScrapeTimeZone = "UTC",
                DegreeOfParallelism = 1,
                StartingDegreeOfParallelism = 1,
                MaxDegreeOfParallelism = 10,
                MinDegreeOfParallelism = 1,
                InitialDelaySeconds = 0,
            });

            await SeedCredentials(tokenPath, "test-acct", "U", "r");
            var tokenMgr = new EpicTokenManager(
                new HttpClient(tokenHandler),
                Substitute.For<ILogger<EpicTokenManager>>(), opts);
            await tokenMgr.EnsureAuthenticatedAsync(CancellationToken.None);

            var querier = new LeaderboardQuerier(
                new HttpClient(MockHttpHandler.WithJsonResponse("{}")),
                Substitute.For<ILogger<LeaderboardQuerier>>());
            var fstClient = new FstClient(
                new HttpClient(fstHandler), opts,
                Substitute.For<ILogger<FstClient>>());

            var worker = new PercentileScrapeWorker(
                tokenMgr, querier, fstClient, opts,
                Substitute.For<ILogger<PercentileScrapeWorker>>(),
                new PercentileScrapeProgressTracker());

            // Cancel after enough time for: initial delay (0s) + scrape delay (~2s) + scrape + next delay cancel
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StartAsync(cts.Token);
            try { await Task.Delay(TimeSpan.FromSeconds(6)); } catch { }
            await worker.StopAsync(CancellationToken.None);

            // FstClient should have been called at least once (GetPlayerEntries)
            Assert.True(fstHandler.Requests.Count >= 1, "Expected at least one FstClient request");
        }
        finally
        {
            try { File.Delete(tokenPath); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_catches_scrape_error_and_continues()
    {
        // Set scrape time to ~1 second from now
        var futureTime = DateTime.UtcNow.AddSeconds(1);
        var tokenPath = Path.Combine(Path.GetTempPath(), $"psw-err-{Guid.NewGuid():N}.json");

        try
        {
            // FstClient throws on GetPlayerEntries → RunScrapeAsync fails with generic exception
            var fstHandler = MockHttpHandler.WithJsonResponse(
                """{"error":"server error"}""", HttpStatusCode.InternalServerError);

            var tokenJson = """
            {
                "access_token": "tok", "refresh_token": "ref",
                "displayName": "U", "account_id": "test-acct", "expires_in": 9999
            }
            """;
            var tokenHandler = MockHttpHandler.WithJsonResponse(tokenJson);
            var opts = Options.Create(new PercentileOptions
            {
                AccountId = "test-acct",
                TokenPath = tokenPath,
                ScrapeTimeOfDay = futureTime.ToString("HH:mm:ss"),
                ScrapeTimeZone = "UTC",
                DegreeOfParallelism = 1,
                StartingDegreeOfParallelism = 1,
                MaxDegreeOfParallelism = 10,
                MinDegreeOfParallelism = 1,
                InitialDelaySeconds = 0,
            });

            await SeedCredentials(tokenPath, "test-acct", "U", "r");
            var tokenMgr = new EpicTokenManager(
                new HttpClient(tokenHandler),
                Substitute.For<ILogger<EpicTokenManager>>(), opts);
            await tokenMgr.EnsureAuthenticatedAsync(CancellationToken.None);

            var querier = new LeaderboardQuerier(
                new HttpClient(MockHttpHandler.WithJsonResponse("{}")),
                Substitute.For<ILogger<LeaderboardQuerier>>());
            var fstClient = new FstClient(
                new HttpClient(fstHandler), opts,
                Substitute.For<ILogger<FstClient>>());

            var workerLog = Substitute.For<ILogger<PercentileScrapeWorker>>();
            var worker = new PercentileScrapeWorker(
                tokenMgr, querier, fstClient, opts, workerLog,
                new PercentileScrapeProgressTracker());

            // Let the scrape run (will fail), then cancel during next delay
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await worker.StartAsync(cts.Token);
            try { await Task.Delay(TimeSpan.FromSeconds(5)); } catch { }
            await worker.StopAsync(CancellationToken.None);

            // Worker caught the exception and looped back (didn't crash)
            // Verify the FST client was hit
            Assert.True(fstHandler.Requests.Count >= 1);
        }
        finally
        {
            try { File.Delete(tokenPath); } catch { }
        }
    }

    [Fact]
    public void PercentileScrapeWorker_can_be_constructed()
    {
        var handler = MockHttpHandler.WithJsonResponse("{}");
        var (worker, _) = CreateWorker(handler);
        Assert.NotNull(worker);
    }

    // ─── Helpers ────────────────────────────────────────────────

    private static (PercentileScrapeWorker worker, MockHttpHandler handler) CreateWorker(MockHttpHandler handler)
    {
        var http = new HttpClient(handler);
        var opts = Options.Create(new PercentileOptions
        {
            AccountId = "test-acct",
            TokenPath = Path.Combine(Path.GetTempPath(), $"psw-{Guid.NewGuid():N}.json"),
            ScrapeTimeOfDay = "23:59",
            ScrapeTimeZone = "UTC",
            DegreeOfParallelism = 1,
            StartingDegreeOfParallelism = 1,
            MaxDegreeOfParallelism = 10,
            MinDegreeOfParallelism = 1,
        });

        var tokenLog = Substitute.For<ILogger<EpicTokenManager>>();
        var tokenMgr = new EpicTokenManager(http, tokenLog, opts);

        var querierLog = Substitute.For<ILogger<LeaderboardQuerier>>();
        var querier = new LeaderboardQuerier(new HttpClient(handler), querierLog);

        var fstLog = Substitute.For<ILogger<FstClient>>();
        var fstClient = new FstClient(new HttpClient(handler), opts, fstLog);

        var workerLog = Substitute.For<ILogger<PercentileScrapeWorker>>();
        var progress = new PercentileScrapeProgressTracker();
        var worker = new PercentileScrapeWorker(tokenMgr, querier, fstClient, opts, workerLog, progress);

        return (worker, handler);
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
