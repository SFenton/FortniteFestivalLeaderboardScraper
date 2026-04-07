using System.Net;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class AccountNameResolverTests
{
    private readonly ILogger<AccountNameResolver> _log = Substitute.For<ILogger<AccountNameResolver>>();

    /// <summary>
    /// Creates a resolver with a mock HTTP handler, a real temp MetaDatabase,
    /// a mock TokenManager (backed by a mock EpicAuthService), and a real progress tracker.
    /// Returns everything needed for assertions.
    /// </summary>
    private (AccountNameResolver resolver, MockHttpMessageHandler handler, InMemoryMetaDatabase metaDb) CreateResolver(
        string? accessToken = "test_token")
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var metaDb = new InMemoryMetaDatabase();

        // Build a TokenManager that returns our fixed token
        var authHandler = new MockHttpMessageHandler();
        if (accessToken != null)
        {
            // Enqueue a large number of token refreshes (resolver calls GetAccessTokenAsync often)
            for (int i = 0; i < 100; i++)
            {
                authHandler.EnqueueJsonOk($$"""
                {
                    "access_token": "{{accessToken}}",
                    "expires_in": 7200,
                    "expires_at": "2099-12-31T23:59:59.000Z",
                    "token_type": "bearer",
                    "refresh_token": "rt",
                    "account_id": "caller"
                }
                """);
            }
        }
        var authHttp = new HttpClient(authHandler);
        var authService = new EpicAuthService(authHttp, Substitute.For<ILogger<EpicAuthService>>());

        var store = Substitute.For<ICredentialStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(accessToken != null
                ? new StoredCredentials { AccountId = "caller", RefreshToken = "rt" }
                : (StoredCredentials?)null);

        var tokenManager = new TokenManager(authService, store, Substitute.For<ILogger<TokenManager>>());

        var progress = new ScrapeProgressTracker();
        var resolver = new AccountNameResolver(httpClient, metaDb.Db, tokenManager, progress, _log);

        return (resolver, handler, metaDb);
    }

    // ─── No unresolved accounts ─────────────────────────

    [Fact]
    public async Task ResolveNewAccountsAsync_NoUnresolved_Returns0()
    {
        var (resolver, handler, metaDb) = CreateResolver();

        var result = await resolver.ResolveNewAccountsAsync();

        Assert.Equal(0, result);
        Assert.Empty(handler.Requests);
    }

    // ─── Basic resolution ───────────────────────────────

    [Fact]
    public async Task ResolveNewAccountsAsync_ResolvesAndPersists()
    {
        var (resolver, handler, metaDb) = CreateResolver();

        // Insert unresolved accounts
        metaDb.Db.InsertAccountIds(new[] { "acct1", "acct2" });

        // API returns display names
        handler.EnqueueJsonOk("""
        [
            { "id": "acct1", "displayName": "Player One" },
            { "id": "acct2", "displayName": "Player Two" }
        ]
        """);

        var result = await resolver.ResolveNewAccountsAsync();

        Assert.Equal(2, result);
        Assert.Equal("Player One", metaDb.Db.GetDisplayName("acct1"));
        Assert.Equal("Player Two", metaDb.Db.GetDisplayName("acct2"));
    }

    // ─── No access token → returns 0 ───────────────────

    [Fact]
    public async Task ResolveNewAccountsAsync_NoToken_Returns0()
    {
        var (resolver, handler, metaDb) = CreateResolver(accessToken: null);

        metaDb.Db.InsertAccountIds(new[] { "acct1" });

        var result = await resolver.ResolveNewAccountsAsync();

        Assert.Equal(0, result);
        Assert.Empty(handler.Requests);
    }

    // ─── HTTP error → null batch, retried ───────────────

    [Fact]
    public async Task ResolveNewAccountsAsync_HttpError_StillCompletes()
    {
        var (resolver, handler, metaDb) = CreateResolver();

        metaDb.Db.InsertAccountIds(new[] { "acct1" });

        // Enqueue 4 errors (maxRetries=3 means 4 total attempts)
        handler.EnqueueError(HttpStatusCode.InternalServerError, "error");
        handler.EnqueueError(HttpStatusCode.InternalServerError, "error");
        handler.EnqueueError(HttpStatusCode.InternalServerError, "error");
        handler.EnqueueError(HttpStatusCode.InternalServerError, "error");

        var result = await resolver.ResolveNewAccountsAsync();

        // Failed batch → 0 resolved
        Assert.Equal(0, result);
    }

    // ─── Sends bearer auth ──────────────────────────────

    [Fact]
    public async Task ResolveNewAccountsAsync_SendsBearerAuth()
    {
        var (resolver, handler, metaDb) = CreateResolver("my_bearer");

        metaDb.Db.InsertAccountIds(new[] { "acct1" });

        handler.EnqueueJsonOk("""[{"id":"acct1","displayName":"P1"}]""");

        await resolver.ResolveNewAccountsAsync();

        Assert.NotEmpty(handler.Requests);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
    }

    // ─── 403 retry path ─────────────────────────────────

    [Fact]
    public async Task ResolveNewAccountsAsync_403ThenSuccess_ResolvesOnRetry()
    {
        var (resolver, handler, metaDb) = CreateResolver();

        metaDb.Db.InsertAccountIds(new[] { "acct1" });

        // First attempt: JSON 403 (triggers token refresh + retry; JSON body avoids CDN detection)
        handler.EnqueueJsonResponse(HttpStatusCode.Forbidden, """{"errorCode":"token_expired"}""");
        // Second attempt: success
        handler.EnqueueJsonOk("""[{"id":"acct1","displayName":"RetryPlayer"}]""");

        var result = await resolver.ResolveNewAccountsAsync();

        Assert.Equal(1, result);
        Assert.Equal("RetryPlayer", metaDb.Db.GetDisplayName("acct1"));
    }

    // ─── 429 rate limit path ────────────────────────────

    [Fact]
    public async Task ResolveNewAccountsAsync_429WithRetryAfter_WaitsAndRetries()
    {
        var (resolver, handler, metaDb) = CreateResolver();

        metaDb.Db.InsertAccountIds(new[] { "acct1" });

        // First attempt: 429 with short retry-after
        handler.Enqueue429(TimeSpan.FromMilliseconds(50));
        // Second attempt: success
        handler.EnqueueJsonOk("""[{"id":"acct1","displayName":"RateLimitedPlayer"}]""");

        var result = await resolver.ResolveNewAccountsAsync();

        Assert.Equal(1, result);
        Assert.Equal("RateLimitedPlayer", metaDb.Db.GetDisplayName("acct1"));
    }

    // ─── HttpRequestException retry path ────────────────

    [Fact]
    public async Task ResolveNewAccountsAsync_NetworkError_RetriesAndRecovers()
    {
        var (resolver, handler, metaDb) = CreateResolver();

        metaDb.Db.InsertAccountIds(new[] { "acct1" });

        // First attempt: network error
        handler.EnqueueException(new HttpRequestException("DNS resolution failed"));
        // Second attempt: success
        handler.EnqueueJsonOk("""[{"id":"acct1","displayName":"NetworkPlayer"}]""");

        var result = await resolver.ResolveNewAccountsAsync();

        Assert.Equal(1, result);
        Assert.Equal("NetworkPlayer", metaDb.Db.GetDisplayName("acct1"));
    }

    // ─── Partial resolution with unresolvable marking ───

    [Fact]
    public async Task ResolveNewAccountsAsync_PartialResolution_MarksUnresolvable()
    {
        var (resolver, handler, metaDb) = CreateResolver();

        // Insert 2 accounts, but API only returns 1
        metaDb.Db.InsertAccountIds(new[] { "acct1", "acct2" });

        handler.EnqueueJsonOk("""[{"id":"acct1","displayName":"KnownPlayer"}]""");

        var result = await resolver.ResolveNewAccountsAsync();

        // Only 1 inserted (for acct1)
        Assert.Equal(1, result);
        Assert.Equal("KnownPlayer", metaDb.Db.GetDisplayName("acct1"));
        // acct2 should be marked as null (unresolvable) so it doesn't retry
    }

    // ─── Non-retryable error ────────────────────────────

    [Fact]
    public async Task ResolveNewAccountsAsync_400Error_NoRetry()
    {
        var (resolver, handler, metaDb) = CreateResolver();

        metaDb.Db.InsertAccountIds(new[] { "acct1" });

        // 400 is not retryable
        handler.EnqueueError(HttpStatusCode.BadRequest, "bad request");

        var result = await resolver.ResolveNewAccountsAsync();

        Assert.Equal(0, result);
        // Should only have made 1 request (no retries for 400)
        Assert.Single(handler.Requests);
    }
}
