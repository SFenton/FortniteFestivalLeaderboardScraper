using FSTService.Auth;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class TokenManagerTests
{
    private readonly EpicAuthService _auth;
    private readonly ICredentialStore _store;
    private readonly ILogger<TokenManager> _log = Substitute.For<ILogger<TokenManager>>();

    public TokenManagerTests()
    {
        // EpicAuthService is sealed, so we can't substitute it directly.
        // Instead we create a real instance backed by a MockHttpMessageHandler.
        var handler = new Helpers.MockHttpMessageHandler();
        var http = new HttpClient(handler);
        _auth = new EpicAuthService(http, Substitute.For<ILogger<EpicAuthService>>());
        _store = Substitute.For<ICredentialStore>();
    }

    private TokenManager CreateManager(EpicAuthService? auth = null, ICredentialStore? store = null)
        => new(auth ?? _auth, store ?? _store, _log);

    // ─── AccountId ──────────────────────────────────────

    [Fact]
    public void AccountId_NoToken_ReturnsNull()
    {
        var mgr = CreateManager();
        Assert.Null(mgr.AccountId);
    }

    // ─── GetAccessTokenAsync: cached token ──────────────

    [Fact]
    public async Task GetAccessTokenAsync_CachedTokenValid_ReturnsCached()
    {
        // Supply stored refresh → auth refreshes → caches token
        var handler = new Helpers.MockHttpMessageHandler();
        // Enqueue a successful refresh response
        handler.EnqueueJsonOk(MakeTokenJson("cached_access", "rt_new", "acct1", hoursFromNow: 2));
        var http = new HttpClient(handler);
        var auth = new EpicAuthService(http, Substitute.For<ILogger<EpicAuthService>>());

        _store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new StoredCredentials { AccountId = "acct1", RefreshToken = "rt_old" });

        var mgr = new TokenManager(auth, _store, _log);

        // First call: loads stored + refreshes
        var token1 = await mgr.GetAccessTokenAsync();
        Assert.Equal("cached_access", token1);
        Assert.Equal("acct1", mgr.AccountId);

        // Second call: uses cached (no more HTTP requests)
        var token2 = await mgr.GetAccessTokenAsync();
        Assert.Equal("cached_access", token2);
        // Only 1 HTTP request was made total
        Assert.Single(handler.Requests);
    }

    // ─── GetAccessTokenAsync: token near expiry triggers refresh ──

    [Fact]
    public async Task GetAccessTokenAsync_TokenExpiringSoon_Refreshes()
    {
        var handler = new Helpers.MockHttpMessageHandler();

        // First refresh: returns a token that expires very soon (within 5 min buffer)
        handler.EnqueueJsonOk(MakeTokenJson("at_expiring", "rt1", "acct1", hoursFromNow: 0.05));
        // Second refresh: returns a fresh token
        handler.EnqueueJsonOk(MakeTokenJson("at_fresh", "rt2", "acct1", hoursFromNow: 2));

        var http = new HttpClient(handler);
        var auth = new EpicAuthService(http, Substitute.For<ILogger<EpicAuthService>>());

        _store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new StoredCredentials { AccountId = "acct1", RefreshToken = "rt_stored" });

        var mgr = new TokenManager(auth, _store, _log);

        // First call: stored creds → refresh → gets expiring token
        var t1 = await mgr.GetAccessTokenAsync();
        Assert.Equal("at_expiring", t1);

        // Second call: token is near expiry → refreshes again
        var t2 = await mgr.GetAccessTokenAsync();
        Assert.Equal("at_fresh", t2);
        Assert.Equal(2, handler.Requests.Count);
    }

    // ─── GetAccessTokenAsync: no creds at all → null ────

    [Fact]
    public async Task GetAccessTokenAsync_NoCreds_ReturnsNull()
    {
        _store.LoadAsync(Arg.Any<CancellationToken>()).Returns((StoredCredentials?)null);

        var mgr = CreateManager();
        var result = await mgr.GetAccessTokenAsync();

        Assert.Null(result);
        Assert.Null(mgr.AccountId);
    }

    // ─── GetAccessTokenAsync: stored creds with expired refresh ─

    [Fact]
    public async Task GetAccessTokenAsync_StoredRefreshExpired_ReturnsNull()
    {
        var handler = new Helpers.MockHttpMessageHandler();
        // Refresh fails with 400 (bad grant)
        handler.EnqueueJsonResponse(System.Net.HttpStatusCode.BadRequest,
            """{"error":"invalid_grant"}""");
        var http = new HttpClient(handler);
        var auth = new EpicAuthService(http, Substitute.For<ILogger<EpicAuthService>>());

        _store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new StoredCredentials { AccountId = "acct1", RefreshToken = "expired_rt" });

        var mgr = new TokenManager(auth, _store, _log);
        var result = await mgr.GetAccessTokenAsync();

        Assert.Null(result);
    }

    // ─── GetAccessTokenAsync: in-memory refresh works ───

    [Fact]
    public async Task GetAccessTokenAsync_InMemoryRefreshUsed_BeforeStoredCreds()
    {
        var handler = new Helpers.MockHttpMessageHandler();
        // Initial: stored creds refresh → token with short life
        handler.EnqueueJsonOk(MakeTokenJson("at1", "rt_inmemory", "acct1", hoursFromNow: 0.01));
        // Second: in-memory refresh_token used → fresh token
        handler.EnqueueJsonOk(MakeTokenJson("at2", "rt_inmemory2", "acct1", hoursFromNow: 2));

        var http = new HttpClient(handler);
        var auth = new EpicAuthService(http, Substitute.For<ILogger<EpicAuthService>>());

        _store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new StoredCredentials { AccountId = "acct1", RefreshToken = "rt_stored" });

        var mgr = new TokenManager(auth, _store, _log);

        await mgr.GetAccessTokenAsync(); // loads stored, refreshes
        var t2 = await mgr.GetAccessTokenAsync(); // uses in-memory rt_inmemory, not stored

        Assert.Equal("at2", t2);
        Assert.Equal(2, handler.Requests.Count);
        // Store.LoadAsync should only be called once (first time)
        await _store.Received(1).LoadAsync(Arg.Any<CancellationToken>());
    }

    // ─── PerformDeviceCodeSetupAsync ────────────────────

    [Fact]
    public async Task PerformDeviceCodeSetupAsync_Success_PersistsToken()
    {
        var handler = new Helpers.MockHttpMessageHandler();

        // 1. client_credentials token
        handler.EnqueueJsonOk("""
        {
            "access_token": "cc_token",
            "expires_in": 3600,
            "token_type": "bearer"
        }
        """);

        // 2. device authorization
        handler.EnqueueJsonOk("""
        {
            "user_code": "ABC",
            "device_code": "dc_xyz",
            "verification_uri": "https://example.com/activate",
            "verification_uri_complete": "https://example.com/activate?code=ABC",
            "expires_in": 600,
            "interval": 0
        }
        """);

        // 3. device_code poll → success immediately
        handler.EnqueueJsonOk(MakeTokenJson("at_device", "rt_device", "user1", hoursFromNow: 2));

        var http = new HttpClient(handler);
        var auth = new EpicAuthService(http, Substitute.For<ILogger<EpicAuthService>>());

        var mgr = new TokenManager(auth, _store, _log);

        var ok = await mgr.PerformDeviceCodeSetupAsync();

        Assert.True(ok);
        Assert.Equal("user1", mgr.AccountId);
        // Verify credentials were persisted
        await _store.Received(1).SaveAsync(
            Arg.Is<StoredCredentials>(c => c.AccountId == "user1" && c.RefreshToken == "rt_device"),
            Arg.Any<CancellationToken>());
    }

    // ─── DeviceCodeLoginRequired event ──────────────────

    [Fact]
    public async Task PerformDeviceCodeSetupAsync_FiresEvent()
    {
        var handler = new Helpers.MockHttpMessageHandler();
        handler.EnqueueJsonOk("""{"access_token":"cc","expires_in":3600,"token_type":"bearer"}""");
        handler.EnqueueJsonOk("""
        {
            "user_code":"X","device_code":"dc","verification_uri":"https://e.com",
            "verification_uri_complete":"https://e.com?code=X","expires_in":600,"interval":0
        }
        """);
        handler.EnqueueJsonOk(MakeTokenJson("at", "rt", "a1", hoursFromNow: 2));

        var http = new HttpClient(handler);
        var auth = new EpicAuthService(http, Substitute.For<ILogger<EpicAuthService>>());
        var mgr = new TokenManager(auth, _store, _log);

        string? firedUrl = null;
        mgr.DeviceCodeLoginRequired += url => firedUrl = url;

        await mgr.PerformDeviceCodeSetupAsync();

        Assert.Equal("https://e.com?code=X", firedUrl);
    }

    // ─── PersistRefreshTokenAsync: skips empty refresh token ──

    [Fact]
    public async Task GetAccessTokenAsync_PersistsNewRefreshToken()
    {
        var handler = new Helpers.MockHttpMessageHandler();
        handler.EnqueueJsonOk(MakeTokenJson("at", "rt_new", "acct1", hoursFromNow: 2));

        var http = new HttpClient(handler);
        var auth = new EpicAuthService(http, Substitute.For<ILogger<EpicAuthService>>());

        _store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new StoredCredentials { AccountId = "acct1", RefreshToken = "rt_old" });

        var mgr = new TokenManager(auth, _store, _log);
        await mgr.GetAccessTokenAsync();

        // Verify the new refresh token was persisted
        await _store.Received(1).SaveAsync(
            Arg.Is<StoredCredentials>(c => c.RefreshToken == "rt_new"),
            Arg.Any<CancellationToken>());
    }

    // ─── Thread safety: concurrent calls ────────────────

    [Fact]
    public async Task GetAccessTokenAsync_ConcurrentCalls_OnlyOneRefresh()
    {
        var handler = new Helpers.MockHttpMessageHandler();
        handler.EnqueueJsonOk(MakeTokenJson("at_shared", "rt_shared", "acct1", hoursFromNow: 2));

        var http = new HttpClient(handler);
        var auth = new EpicAuthService(http, Substitute.For<ILogger<EpicAuthService>>());

        _store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new StoredCredentials { AccountId = "acct1", RefreshToken = "rt_old" });

        var mgr = new TokenManager(auth, _store, _log);

        var tasks = Enumerable.Range(0, 5).Select(_ => mgr.GetAccessTokenAsync()).ToArray();
        var results = await Task.WhenAll(tasks);

        // All should get the same token
        Assert.All(results, t => Assert.Equal("at_shared", t));
        // Only 1 HTTP request should have been made (semaphore)
        Assert.Single(handler.Requests);
    }

    // ─── Helpers ────────────────────────────────────────

    private static string MakeTokenJson(string accessToken, string refreshToken, string accountId, double hoursFromNow)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(hoursFromNow).ToString("o");
        return $$"""
        {
            "access_token": "{{accessToken}}",
            "expires_in": {{(int)(hoursFromNow * 3600)}},
            "expires_at": "{{expiresAt}}",
            "token_type": "bearer",
            "refresh_token": "{{refreshToken}}",
            "refresh_expires": 28800,
            "refresh_expires_at": "2099-12-31T23:59:59.000Z",
            "account_id": "{{accountId}}",
            "client_id": "test_client",
            "displayName": "TestUser"
        }
        """;
    }
}
