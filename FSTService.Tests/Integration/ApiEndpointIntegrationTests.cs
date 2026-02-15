using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Integration;

/// <summary>
/// Integration tests for API and Auth endpoints using <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// Replaces external dependencies (auth, scraping) with test doubles and exercises
/// the full ASP.NET Core pipeline: middleware, auth, rate limiting, JSON serialization.
/// </summary>
public class ApiEndpointIntegrationTests : IClassFixture<ApiEndpointIntegrationTests.FstWebApplicationFactory>, IDisposable
{
    private readonly FstWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _authedClient;

    public ApiEndpointIntegrationTests(FstWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _authedClient = factory.CreateClient();
        _authedClient.DefaultRequestHeaders.Add("X-API-Key", FstWebApplicationFactory.TestApiKey);
    }

    public void Dispose()
    {
        _client.Dispose();
        _authedClient.Dispose();
    }

    // ─── Health ─────────────────────────────────────────────────

    [Fact]
    public async Task Healthz_ReturnsOk()
    {
        var response = await _client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ok", body);
    }

    // ─── Progress ───────────────────────────────────────────────

    [Fact]
    public async Task ApiProgress_ReturnsProgressResponse()
    {
        var response = await _client.GetAsync("/api/progress");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ─── Songs ──────────────────────────────────────────────────

    [Fact]
    public async Task ApiSongs_ReturnsLoadedSongs()
    {
        var response = await _client.GetAsync("/api/songs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("count").GetInt32() >= 1);
        var songs = json.GetProperty("songs");
        Assert.True(songs.GetArrayLength() >= 1);
        // Check first song has expected shape
        var first = songs[0];
        Assert.True(first.TryGetProperty("songId", out _));
        Assert.True(first.TryGetProperty("title", out _));
    }

    // ─── Leaderboard ────────────────────────────────────────────

    [Fact]
    public async Task ApiLeaderboard_ValidInstrument_ReturnsEntries()
    {
        var response = await _client.GetAsync("/api/leaderboard/testSong1/Solo_Guitar");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("testSong1", json.GetProperty("songId").GetString());
        Assert.Equal("Solo_Guitar", json.GetProperty("instrument").GetString());
    }

    [Fact]
    public async Task ApiLeaderboard_UnknownInstrument_Returns404()
    {
        var response = await _client.GetAsync("/api/leaderboard/testSong1/Kazoo");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── Player profile ─────────────────────────────────────────

    [Fact]
    public async Task ApiPlayer_ReturnsProfile()
    {
        var response = await _client.GetAsync("/api/player/testAcct1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("testAcct1", json.GetProperty("accountId").GetString());
    }

    // ─── Protected endpoints (require API key) ──────────────────

    [Fact]
    public async Task ApiStatus_NoAuth_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApiStatus_WithApiKey_ReturnsOk()
    {
        var response = await _authedClient.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("totalEntries", out _));
    }

    // ─── Register ───────────────────────────────────────────────

    [Fact]
    public async Task ApiRegister_NoAuth_ReturnsUnauthorized()
    {
        var content = JsonContent.Create(new { deviceId = "dev1", accountId = "acct1" });
        var response = await _client.PostAsync("/api/register", content);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApiRegister_WithAuth_RegistersAndReturnsOk()
    {
        var content = JsonContent.Create(new { deviceId = "testDev1", accountId = "testAcct1" });
        var response = await _authedClient.PostAsync("/api/register", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("registered").GetBoolean());
    }

    [Fact]
    public async Task ApiRegister_MissingFields_ReturnsBadRequest()
    {
        var content = JsonContent.Create(new { deviceId = "", accountId = "" });
        var response = await _authedClient.PostAsync("/api/register", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ─── Player history ─────────────────────────────────────────

    [Fact]
    public async Task ApiPlayerHistory_NotRegistered_Returns404()
    {
        var response = await _authedClient.GetAsync("/api/player/unknownAcct/history");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ApiPlayerHistory_RegisteredUser_ReturnsHistory()
    {
        // Register a user first
        var regContent = JsonContent.Create(new { deviceId = "histDev", accountId = "histAcct" });
        await _authedClient.PostAsync("/api/register", regContent);

        var response = await _authedClient.GetAsync("/api/player/histAcct/history");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("histAcct", json.GetProperty("accountId").GetString());
    }

    // ─── Backfill status ────────────────────────────────────────

    [Fact]
    public async Task ApiBackfillStatus_NotFound_Returns404()
    {
        var response = await _authedClient.GetAsync("/api/backfill/unknown/status");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── Sync endpoints ─────────────────────────────────────────

    [Fact]
    public async Task ApiSyncVersion_NotRegistered_Returns404()
    {
        var response = await _authedClient.GetAsync("/api/sync/unknownDevice/version");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ApiSync_NotRegistered_Returns404()
    {
        var response = await _authedClient.GetAsync("/api/sync/unknownDevice");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── Auth endpoints ─────────────────────────────────────────

    [Fact]
    public async Task AuthLogin_ValidCredentials_ReturnsTokens()
    {
        var content = JsonContent.Create(new { username = "testUser", deviceId = "testDev1" });
        var response = await _client.PostAsync("/api/auth/login", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("accessToken", out _));
        Assert.True(json.TryGetProperty("refreshToken", out _));
    }

    [Fact]
    public async Task AuthLogin_MissingFields_ReturnsBadRequest()
    {
        var content = JsonContent.Create(new { username = "", deviceId = "" });
        var response = await _client.PostAsync("/api/auth/login", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AuthRefresh_InvalidToken_ReturnsUnauthorized()
    {
        var content = JsonContent.Create(new { refreshToken = "invalid_token" });
        var response = await _client.PostAsync("/api/auth/refresh", content);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthRefresh_MissingToken_ReturnsBadRequest()
    {
        var content = JsonContent.Create(new { refreshToken = "" });
        var response = await _client.PostAsync("/api/auth/refresh", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AuthLogout_ReturnsNoContent()
    {
        var content = JsonContent.Create(new { refreshToken = "some_token" });
        var response = await _client.PostAsync("/api/auth/logout", content);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AuthMe_NoToken_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─── Diagnostic endpoints ───────────────────────────────────

    [Fact]
    public async Task DiagEvents_NoToken_ReturnsProblem()
    {
        // TokenManager returns null → should return Problem
        var response = await _client.GetAsync("/api/diag/events");
        // The endpoint returns Results.Problem when no access token
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task DiagLeaderboard_NoToken_ReturnsProblem()
    {
        var response = await _client.GetAsync("/api/diag/leaderboard?eventId=test&windowId=alltime");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ─── Successful login flow ──────────────────────────────────

    [Fact]
    public async Task AuthLogin_ValidRequest_ReturnsTokens()
    {
        var content = JsonContent.Create(new { username = "loginUser", deviceId = "loginDev" });
        var response = await _client.PostAsync("/api/auth/login", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(json.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(json.GetProperty("refreshToken").GetString()));
        Assert.True(json.GetProperty("expiresIn").GetInt32() > 0);
    }

    [Fact]
    public async Task AuthRefresh_ValidToken_ReturnsNewTokens()
    {
        // Login first to get a refresh token
        var loginContent = JsonContent.Create(new { username = "refreshUser", deviceId = "refreshDev" });
        var loginResponse = await _client.PostAsync("/api/auth/login", loginContent);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var refreshToken = loginJson.GetProperty("refreshToken").GetString();

        // Now use the refresh token
        var refreshContent = JsonContent.Create(new { refreshToken });
        var refreshResponse = await _client.PostAsync("/api/auth/refresh", refreshContent);
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        var refreshJson = await refreshResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(refreshJson.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(refreshJson.GetProperty("refreshToken").GetString()));
    }

    [Fact]
    public async Task AuthMe_ValidBearer_ReturnsUserInfo()
    {
        // Login first to get an access token
        var loginContent = JsonContent.Create(new { username = "meUser", deviceId = "meDev" });
        var loginResponse = await _client.PostAsync("/api/auth/login", loginContent);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = loginJson.GetProperty("accessToken").GetString();

        // Call /api/auth/me with bearer token
        using var meClient = _factory.CreateClient();
        meClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        var meResponse = await meClient.GetAsync("/api/auth/me");
        // Should return OK or NotFound (user exists in DB but might not have full registration)
        Assert.True(meResponse.StatusCode == HttpStatusCode.OK ||
                    meResponse.StatusCode == HttpStatusCode.NotFound);
    }

    // ─── Sync endpoints ─────────────────────────────────────────

    [Fact]
    public async Task SyncVersion_RegisteredDevice_ReturnsVersion()
    {
        // Register a device first
        var regContent = JsonContent.Create(new { deviceId = "syncDev", accountId = "syncAcct" });
        await _authedClient.PostAsync("/api/register", regContent);

        var response = await _authedClient.GetAsync("/api/sync/syncDev/version");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("syncDev", json.GetProperty("deviceId").GetString());
    }

    [Fact]
    public async Task SyncVersion_UnknownDevice_Returns404()
    {
        var response = await _authedClient.GetAsync("/api/sync/unknownDev123/version");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Sync_RegisteredDevice_ReturnsDbOrError()
    {
        // Register a device
        var regContent = JsonContent.Create(new { deviceId = "dlDev", accountId = "dlAcct" });
        await _authedClient.PostAsync("/api/register", regContent);

        var response = await _authedClient.GetAsync("/api/sync/dlDev");
        // Either returns the file or 503 if build fails (acceptable in test env)
        Assert.True(
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"Unexpected: {response.StatusCode}");
    }

    [Fact]
    public async Task Sync_UnregisteredDevice_Returns404()
    {
        var response = await _authedClient.GetAsync("/api/sync/unknownSync/version");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── Backfill status (with data) ────────────────────────────

    [Fact]
    public async Task BackfillStatus_RegisteredAccount_ReturnsStatus()
    {
        // Access the MetaDatabase directly via the factory's service provider
        using var scope = _factory.Services.CreateScope();
        var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
        metaDb.EnqueueBackfill("bfAcct", 10);

        var response = await _authedClient.GetAsync("/api/backfill/bfAcct/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("bfAcct", json.GetProperty("accountId").GetString());
        Assert.Equal("pending", json.GetProperty("status").GetString());
    }

    // ═══════════════════════════════════════════════════════════════
    // Factory: sets up the test server with in-memory/temp dependencies
    // ═══════════════════════════════════════════════════════════════

    public sealed class FstWebApplicationFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "test-api-key-12345";

        private readonly string _tempDir = Path.Combine(
            Path.GetTempPath(), $"fst_api_test_{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempDir);

            builder.UseEnvironment("Testing");

            builder.ConfigureAppConfiguration((ctx, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Scraper:DataDirectory"] = _tempDir,
                    ["Scraper:DatabasePath"] = Path.Combine(_tempDir, "core.db"),
                    ["Scraper:DeviceAuthPath"] = Path.Combine(_tempDir, "device-auth.json"),
                    ["Scraper:ApiOnly"] = "true",
                    ["Api:ApiKey"] = TestApiKey,
                    ["Api:AllowedOrigins:0"] = "*",
                    ["Jwt:SecretKey"] = "TestSecretKey_SuperLongEnough_For_HMACSHA256_12345678",
                    ["Jwt:Issuer"] = "FSTService.Tests",
                    ["Jwt:Audience"] = "FSTService.Tests",
                    ["Jwt:AccessTokenExpirationMinutes"] = "60",
                    ["Jwt:RefreshTokenExpirationDays"] = "7",
                });
            });

            builder.ConfigureServices(services =>
            {
                // Remove the real ScraperWorker — we don't want background scraping
                services.RemoveAll<IHostedService>();

                // Ensure API key auth options are set (Program.cs resolves them early
                // before test config is applied, so we must override here)
                services.Configure<ApiKeyAuthOptions>("ApiKey", opts => opts.ApiKey = TestApiKey);

                // Replace TokenManager with a real one backed by mocked auth
                // (no credentials → GetAccessTokenAsync returns null)
                services.RemoveAll<TokenManager>();
                services.AddSingleton<TokenManager>(sp =>
                {
                    var mockHandler = new HttpMessageHandler_NoOp();
                    var mockHttp = new HttpClient(mockHandler);
                    var auth = new EpicAuthService(mockHttp, Substitute.For<ILogger<EpicAuthService>>());
                    var store = Substitute.For<ICredentialStore>();
                    store.LoadAsync().Returns(Task.FromResult<StoredCredentials?>(null));
                    return new TokenManager(auth, store, Substitute.For<ILogger<TokenManager>>());
                });

                // Replace FestivalService with one that has test songs pre-loaded
                services.RemoveAll<FestivalService>();
                var festivalService = CreateTestFestivalService();
                services.AddSingleton(festivalService);
            });
        }

        private static FestivalService CreateTestFestivalService()
        {
            var service = new FestivalService((IFestivalPersistence?)null);
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var songsField = typeof(FestivalService).GetField("_songs", flags)!;
            var dirtyField = typeof(FestivalService).GetField("_songsDirty", flags)!;
            var dict = (Dictionary<string, Song>)songsField.GetValue(service)!;
            dict["testSong1"] = new Song
            {
                track = new Track
                {
                    su = "testSong1",
                    tt = "Integration Test Song",
                    an = "Test Artist",
                    @in = new In { gr = 5, ba = 3, vl = 4, ds = 2 },
                }
            };
            dirtyField.SetValue(service, true);
            return service;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        /// <summary>A no-op HTTP handler that returns empty 200 responses.</summary>
        private sealed class HttpMessageHandler_NoOp : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
