using System.Net;
using System.Text.Json;
using PercentileService.Tests.Helpers;

namespace PercentileService.Tests;

public sealed class EpicTokenManagerTests : IDisposable
{
    private readonly string _tokenPath;

    public EpicTokenManagerTests()
    {
        _tokenPath = Path.Combine(Path.GetTempPath(), $"percentile-test-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try { File.Delete(_tokenPath); } catch { }
    }

    private EpicTokenManager CreateManager(MockHttpHandler handler)
    {
        return TestFactory.CreateTokenManager(handler, o => o.TokenPath = _tokenPath);
    }

    // ─── Refresh flow ───────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_with_no_token_does_not_throw()
    {
        // No refresh token set → logs warning but doesn't crash
        var handler = MockHttpHandler.WithJsonResponse("{}");
        var mgr = CreateManager(handler);

        await mgr.RefreshAsync(CancellationToken.None);

        Assert.False(mgr.IsAuthenticated);
        Assert.Empty(handler.Requests); // never calls Epic
    }

    [Fact]
    public async Task EnsureAuthenticated_skips_if_already_authenticated()
    {
        // First: set up stored credentials that will successfully refresh
        var tokenJson = MakeTokenJson("test-access", "test-refresh", "TestUser", "acct123", 3600);
        var handler = MockHttpHandler.WithJsonResponse(tokenJson);
        var mgr = CreateManager(handler);

        // Seed credentials file
        await SeedCredentials("acct123", "TestUser", "valid-refresh");

        // First call triggers refresh
        await mgr.EnsureAuthenticatedAsync(CancellationToken.None);
        Assert.True(mgr.IsAuthenticated);
        Assert.Single(handler.Requests);

        // Second call should skip (already authenticated)
        await mgr.EnsureAuthenticatedAsync(CancellationToken.None);
        Assert.Single(handler.Requests); // No additional requests
    }

    [Fact]
    public async Task EnsureAuthenticated_loads_and_refreshes_stored_credentials()
    {
        var tokenJson = MakeTokenJson("new-access", "new-refresh", "Player1", "acct123", 7200);
        var handler = MockHttpHandler.WithJsonResponse(tokenJson);
        var mgr = CreateManager(handler);

        await SeedCredentials("acct123", "Player1", "old-refresh-token");

        await mgr.EnsureAuthenticatedAsync(CancellationToken.None);

        Assert.True(mgr.IsAuthenticated);
        Assert.Equal("new-access", mgr.AccessToken);
        Assert.Equal("acct123", mgr.AccountId);
        Assert.Equal("Player1", mgr.DisplayName);

        // Verify the refresh request was sent with the stored token
        Assert.Single(handler.Requests);
        var content = await handler.Requests[0].Content!.ReadAsStringAsync();
        Assert.Contains("refresh_token", content);
        Assert.Contains("old-refresh-token", content);
    }

    [Fact]
    public async Task EnsureAuthenticated_saves_credentials_after_refresh()
    {
        var tokenJson = MakeTokenJson("access1", "refresh1", "User", "acct1", 3600);
        var handler = MockHttpHandler.WithJsonResponse(tokenJson);
        var mgr = CreateManager(handler);

        await SeedCredentials("acct1", "User", "old-refresh");

        await mgr.EnsureAuthenticatedAsync(CancellationToken.None);

        // Verify credentials file was updated
        Assert.True(File.Exists(_tokenPath));
        var saved = JsonSerializer.Deserialize<StoredPercentileCredentials>(
            await File.ReadAllTextAsync(_tokenPath));
        Assert.NotNull(saved);
        Assert.Equal("refresh1", saved.RefreshToken);
        Assert.Equal("acct1", saved.AccountId);
    }

    [Fact]
    public async Task RefreshAsync_clears_token_on_failure()
    {
        var successJson = MakeTokenJson("good-token", "good-refresh", "U", "a", 3600);
        var failResp = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"invalid_grant"}"""),
        };

        // First response succeeds (for EnsureAuthenticated), second fails (for RefreshAsync)
        var handler = MockHttpHandler.WithSequence(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(successJson, System.Text.Encoding.UTF8, "application/json"),
            },
            failResp);

        var mgr = CreateManager(handler);
        await SeedCredentials("a", "U", "r");

        await mgr.EnsureAuthenticatedAsync(CancellationToken.None);
        Assert.True(mgr.IsAuthenticated);

        await mgr.RefreshAsync(CancellationToken.None);
        Assert.Null(mgr.AccessToken);
    }

    [Fact]
    public async Task EnsureAuthenticated_returns_empty_when_no_creds_file()
    {
        // No creds file exists → will try device_code flow
        // We simulate device_code flow failing (client_credentials returns error)
        var handler = MockHttpHandler.WithJsonResponse(
            """{"error":"test"}""", HttpStatusCode.BadRequest);
        var mgr = CreateManager(handler);

        // Should throw since device_code flow fails
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mgr.EnsureAuthenticatedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAuthenticated_handles_corrupt_creds_file()
    {
        // Write a corrupt file
        await File.WriteAllTextAsync(_tokenPath, "not-valid-json!!!");

        // Will fail to parse, fall through to device_code, which also fails
        var handler = MockHttpHandler.WithJsonResponse(
            """{"error":"test"}""", HttpStatusCode.BadRequest);
        var mgr = CreateManager(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mgr.EnsureAuthenticatedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAuthenticated_handles_creds_with_empty_refresh_token()
    {
        await SeedCredentials("a", "U", ""); // Empty refresh token

        var handler = MockHttpHandler.WithJsonResponse(
            """{"error":"test"}""", HttpStatusCode.BadRequest);
        var mgr = CreateManager(handler);

        // Empty refresh treated as no creds → device_code
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mgr.EnsureAuthenticatedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAuthenticated_falls_back_to_device_code_on_refresh_failure()
    {
        await SeedCredentials("a", "U", "stale-refresh");

        // Refresh will fail (401), then device_code client_credentials also fails
        var handler = MockHttpHandler.WithSequence(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"invalid_grant"}"""),
            },
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"cc_failed"}"""),
            });

        var mgr = CreateManager(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mgr.EnsureAuthenticatedAsync(CancellationToken.None));

        // Should have made 2 requests: 1 refresh attempt + 1 client_credentials attempt
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void Initial_state_is_not_authenticated()
    {
        var handler = MockHttpHandler.WithJsonResponse("{}");
        var mgr = CreateManager(handler);

        Assert.False(mgr.IsAuthenticated);
        Assert.Null(mgr.AccessToken);
        Assert.Null(mgr.AccountId);
        Assert.Null(mgr.DisplayName);
    }

    // ─── StartDeviceCodeFlowAsync ───────────────────────────────

    [Fact]
    public async Task StartDeviceCodeFlowAsync_returns_device_code_info()
    {
        int callCount = 0;
        var handler = new MockHttpHandler(req =>
        {
            callCount++;
            if (callCount == 1) // client_credentials
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{ "access_token": "cc-token", "expires_in": 3600 }""",
                        System.Text.Encoding.UTF8, "application/json"),
                });
            }
            // deviceAuthorization
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                    "device_code": "dc_abc",
                    "user_code": "XYZ789",
                    "verification_uri": "https://epicgames.com/activate",
                    "verification_uri_complete": "https://epicgames.com/activate?code=XYZ789",
                    "expires_in": 600,
                    "interval": 5
                }
                """, System.Text.Encoding.UTF8, "application/json"),
            });
        });

        var mgr = CreateManager(handler);
        var info = await mgr.StartDeviceCodeFlowAsync(CancellationToken.None);

        Assert.Equal("dc_abc", info.DeviceCode);
        Assert.Equal("XYZ789", info.UserCode);
        Assert.Equal("https://epicgames.com/activate", info.VerificationUri);
        Assert.Equal("https://epicgames.com/activate?code=XYZ789", info.VerificationUriComplete);
        Assert.Equal(600, info.ExpiresIn);
        Assert.Equal(5, info.Interval);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task StartDeviceCodeFlowAsync_throws_on_client_credentials_failure()
    {
        var handler = MockHttpHandler.WithJsonResponse(
            """{"error":"bad_client"}""", HttpStatusCode.Unauthorized);

        var mgr = CreateManager(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mgr.StartDeviceCodeFlowAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartDeviceCodeFlowAsync_throws_on_device_authorization_failure()
    {
        int callCount = 0;
        var handler = new MockHttpHandler(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{ "access_token": "cc", "expires_in": 3600 }""",
                        System.Text.Encoding.UTF8, "application/json"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"error":"forbidden"}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        });

        var mgr = CreateManager(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mgr.StartDeviceCodeFlowAsync(CancellationToken.None));
    }

    // ─── PollDeviceCodeAsync ────────────────────────────────────

    [Fact]
    public async Task PollDeviceCodeAsync_applies_token_on_success()
    {
        int callCount = 0;
        var handler = new MockHttpHandler(req =>
        {
            callCount++;
            if (callCount == 1) // First poll: pending
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"errorCode":"authorization_pending"}""",
                        System.Text.Encoding.UTF8, "application/json"),
                });
            }
            // Second poll: success
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    MakeTokenJson("poll-access", "poll-refresh", "Poller", "acct-poll", 3600),
                    System.Text.Encoding.UTF8, "application/json"),
            });
        });

        var mgr = CreateManager(handler);

        var info = new DeviceCodeInfo
        {
            DeviceCode = "dc_test",
            UserCode = "U",
            VerificationUriComplete = "https://x",
            ExpiresIn = 600,
            Interval = 0, // Use 0 for fast test (clamped to 5 internally but we need to keep test fast)
        };

        await mgr.PollDeviceCodeAsync(info, CancellationToken.None);

        Assert.True(mgr.IsAuthenticated);
        Assert.Equal("poll-access", mgr.AccessToken);
        Assert.Equal("acct-poll", mgr.AccountId);
        Assert.Equal("Poller", mgr.DisplayName);

        // Verify credentials file was written
        Assert.True(File.Exists(_tokenPath));
        var saved = JsonSerializer.Deserialize<StoredPercentileCredentials>(
            await File.ReadAllTextAsync(_tokenPath));
        Assert.NotNull(saved);
        Assert.Equal("poll-refresh", saved!.RefreshToken);
    }

    [Fact]
    public async Task PollDeviceCodeAsync_throws_on_non_pending_error()
    {
        var handler = MockHttpHandler.WithJsonResponse(
            """{"errorCode":"expired_token"}""", HttpStatusCode.BadRequest);

        var mgr = CreateManager(handler);

        var info = new DeviceCodeInfo
        {
            DeviceCode = "dc",
            UserCode = "U",
            VerificationUriComplete = "https://x",
            ExpiresIn = 600,
            Interval = 0,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mgr.PollDeviceCodeAsync(info, CancellationToken.None));
    }

    [Fact]
    public async Task PollDeviceCodeAsync_throws_timeout_when_expired()
    {
        // Always return pending — but expires_in=0 so deadline is already passed
        var handler = MockHttpHandler.WithJsonResponse(
            """{"errorCode":"authorization_pending"}""", HttpStatusCode.BadRequest);

        var mgr = CreateManager(handler);

        var info = new DeviceCodeInfo
        {
            DeviceCode = "dc",
            UserCode = "U",
            VerificationUriComplete = "https://x",
            ExpiresIn = 0, // Already expired
            Interval = 0,
        };

        await Assert.ThrowsAsync<TimeoutException>(() =>
            mgr.PollDeviceCodeAsync(info, CancellationToken.None));
    }

    // ─── DeviceCodeInfo ─────────────────────────────────────────

    [Fact]
    public void DeviceCodeInfo_has_sensible_defaults()
    {
        var info = new DeviceCodeInfo();
        Assert.Equal("", info.DeviceCode);
        Assert.Equal("", info.UserCode);
        Assert.Equal("", info.VerificationUri);
        Assert.Equal("", info.VerificationUriComplete);
        Assert.Equal(0, info.ExpiresIn);
        Assert.Equal(0, info.Interval);
    }

    // ─── Helpers ────────────────────────────────────────────────

    private static string MakeTokenJson(string access, string refresh, string display, string accountId, int expiresIn)
    {
        return $$"""
        {
            "access_token": "{{access}}",
            "refresh_token": "{{refresh}}",
            "displayName": "{{display}}",
            "account_id": "{{accountId}}",
            "expires_in": {{expiresIn}}
        }
        """;
    }

    private async Task SeedCredentials(string accountId, string displayName, string refreshToken)
    {
        var creds = new StoredPercentileCredentials
        {
            AccountId = accountId,
            DisplayName = displayName,
            RefreshToken = refreshToken,
            SavedAt = DateTimeOffset.UtcNow.ToString("o"),
        };
        var dir = Path.GetDirectoryName(_tokenPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(_tokenPath,
            JsonSerializer.Serialize(creds, new JsonSerializerOptions { WriteIndented = true }));
    }
}
