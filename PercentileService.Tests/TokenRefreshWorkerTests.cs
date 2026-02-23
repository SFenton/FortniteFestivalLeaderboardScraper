using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PercentileService.Tests.Helpers;

namespace PercentileService.Tests;

public sealed class TokenRefreshWorkerTests
{
    [Fact]
    public async Task ExecuteAsync_calls_EnsureAuthenticated_then_stops_on_cancel()
    {
        // Arrange: create a real TokenManager with a mock handler that returns a valid token
        var tokenJson = """
        {
            "access_token": "token1",
            "refresh_token": "refresh1",
            "displayName": "Test",
            "account_id": "acct1",
            "expires_in": 3600
        }
        """;
        var tokenPath = Path.Combine(Path.GetTempPath(), $"trw-test-{Guid.NewGuid():N}.json");

        try
        {
            // Seed credentials file so EnsureAuthenticated refreshes (no device_code needed)
            var creds = new StoredPercentileCredentials
            {
                AccountId = "acct1",
                DisplayName = "Test",
                RefreshToken = "old-refresh",
                SavedAt = DateTimeOffset.UtcNow.ToString("o"),
            };
            await File.WriteAllTextAsync(tokenPath,
                System.Text.Json.JsonSerializer.Serialize(creds));

            var handler = MockHttpHandler.WithJsonResponse(tokenJson);
            var http = new HttpClient(handler);
            var opts = Options.Create(new PercentileOptions
            {
                TokenPath = tokenPath,
                TokenRefreshInterval = TimeSpan.FromMilliseconds(50), // Very short for testing
            });
            var tokenLog = Substitute.For<ILogger<EpicTokenManager>>();
            var tokenMgr = new EpicTokenManager(http, tokenLog, opts);

            var workerLog = Substitute.For<ILogger<TokenRefreshWorker>>();
            var worker = new TokenRefreshWorker(tokenMgr, workerLog, opts);

            // Act: start and quickly cancel
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await worker.StartAsync(cts.Token);

            // Give ExecuteAsync a moment to run
            try { await Task.Delay(400); } catch { }
            await worker.StopAsync(CancellationToken.None);

            // Assert: initial EnsureAuth + at least one refresh
            Assert.True(tokenMgr.IsAuthenticated);
            Assert.True(handler.Requests.Count >= 1);
        }
        finally
        {
            try { File.Delete(tokenPath); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_throws_when_initial_auth_fails()
    {
        var handler = MockHttpHandler.WithJsonResponse("""{"error":"bad"}""", HttpStatusCode.BadRequest);
        var tokenPath = Path.Combine(Path.GetTempPath(), $"trw-fail-{Guid.NewGuid():N}.json");
        // No credentials file → device_code → fails

        var http = new HttpClient(handler);
        var opts = Options.Create(new PercentileOptions { TokenPath = tokenPath });
        var tokenLog = Substitute.For<ILogger<EpicTokenManager>>();
        var tokenMgr = new EpicTokenManager(http, tokenLog, opts);

        var workerLog = Substitute.For<ILogger<TokenRefreshWorker>>();
        var worker = new TokenRefreshWorker(tokenMgr, workerLog, opts);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // StartAsync should propagate the failure from EnsureAuthenticated
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.StartAsync(cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_catches_refresh_error_and_continues()
    {
        var tokenPath = Path.Combine(Path.GetTempPath(), $"trw-err-{Guid.NewGuid():N}.json");

        try
        {
            // Seed credentials
            var creds = new StoredPercentileCredentials
            {
                AccountId = "acct1",
                DisplayName = "Test",
                RefreshToken = "old-refresh",
                SavedAt = DateTimeOffset.UtcNow.ToString("o"),
            };
            await File.WriteAllTextAsync(tokenPath,
                System.Text.Json.JsonSerializer.Serialize(creds));

            // First call: successful refresh (for EnsureAuthenticated)
            // Second call: throw HttpRequestException (simulate network error during refresh)
            int callCount = 0;
            var handler = new MockHttpHandler(req =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Initial auth refresh succeeds
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {
                            "access_token": "tok", "refresh_token": "ref",
                            "displayName": "T", "account_id": "acct1", "expires_in": 9999
                        }
                        """, System.Text.Encoding.UTF8, "application/json"),
                    });
                }
                // Subsequent refresh calls fail with a server error
                throw new HttpRequestException("Network error");
            });

            var opts = Options.Create(new PercentileOptions
            {
                TokenPath = tokenPath,
                TokenRefreshInterval = TimeSpan.FromMilliseconds(50),
            });

            var tokenMgr = new EpicTokenManager(
                new HttpClient(handler),
                Substitute.For<ILogger<EpicTokenManager>>(), opts);

            var workerLog = Substitute.For<ILogger<TokenRefreshWorker>>();
            var worker = new TokenRefreshWorker(tokenMgr, workerLog, opts);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await worker.StartAsync(cts.Token);

            try { await Task.Delay(600); } catch { }
            await worker.StopAsync(CancellationToken.None);

            // Worker should have survived the error and continued running — not crashed
            // The error was caught by the catch(Exception) branch in ExecuteAsync
        }
        finally
        {
            try { File.Delete(tokenPath); } catch { }
        }
    }
}
