using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Integration;

/// <summary>
/// End-to-end integration tests for the authentication flow, using real SQLite databases
/// and real JWT token generation/validation. Uses a mock EpicAuthService to simulate
/// Epic's authorization code exchange.
/// </summary>
public sealed class AuthFlowIntegrationTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaFixture = new();
    private MetaDatabase MetaDb => _metaFixture.Db;
    private readonly string _dataDir;

    private readonly JwtSettings _jwtSettings = new()
    {
        Secret = "TestSecretKeyThatIsAtLeast32Chars!",
        Issuer = "FSTService.IntegrationTests",
        AccessTokenLifetimeMinutes = 60,
        RefreshTokenLifetimeDays = 30,
    };

    private static readonly EpicOAuthSettings TestOAuthSettings = new()
    {
        ClientId = "test-client-id",
        ClientSecret = "test-client-secret",
        RedirectUri = "https://example.com/api/auth/epiccallback",
        AppDeepLink = "festscoretracker://auth/callback",
    };

    private readonly JwtTokenService _jwt;
    private readonly BackfillQueue _backfillQueue = new();
    private readonly UserAuthService _authService;

    /// <summary>
    /// Creates a mock <see cref="EpicAuthService"/> that returns the given
    /// account ID and display name when <c>ExchangeAuthorizationCodeAsync</c> is called.
    /// </summary>
    private static EpicAuthService CreateMockEpic(string accountId = "epic_acct_123", string displayName = "TestPlayer")
    {
        var mock = Substitute.For<EpicAuthService>(new HttpClient(), NullLogger<EpicAuthService>.Instance);
        mock.ExchangeAuthorizationCodeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EpicTokenResponse
            {
                AccountId = accountId,
                DisplayName = displayName,
                AccessToken = "epic_access_token",
            }));
        return mock;
    }

    public AuthFlowIntegrationTests()
    {
        _jwt = new JwtTokenService(Options.Create(_jwtSettings));

        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_authflow_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        var loggerFactory = new NullLoggerFactory();
        var glp = new GlobalLeaderboardPersistence(
            _dataDir, MetaDb, loggerFactory,
            NullLogger<GlobalLeaderboardPersistence>.Instance);
        glp.Initialize();

        var festivalService = new FortniteFestival.Core.Services.FestivalService();
        var personalDbBuilder = new PersonalDbBuilder(
            glp, festivalService, MetaDb, _dataDir,
            NullLogger<PersonalDbBuilder>.Instance);

        var mockEpic = CreateMockEpic();
        var tokenVault = new TokenVault(
            MetaDb, mockEpic,
            Options.Create(TestOAuthSettings),
            NullLogger<TokenVault>.Instance);

        _authService = new UserAuthService(
            _jwt, MetaDb, personalDbBuilder, _backfillQueue,
            mockEpic,
            Options.Create(TestOAuthSettings),
            tokenVault,
            NullLogger<UserAuthService>.Instance);
    }

    public void Dispose()
    {
        _metaFixture.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    /// <summary>
    /// Full login → refresh → refresh → logout lifecycle test.
    /// Verifies token rotation, session management, and token validation.
    /// </summary>
    [Fact]
    public async Task Full_auth_lifecycle()
    {
        // ── Login ──
        var login = await _authService.LoginAsync("fake_code", "device_1", "iOS");
        Assert.NotEmpty(login.AccessToken);
        Assert.NotEmpty(login.RefreshToken);
        Assert.True(login.ExpiresIn > 0);

        // Access token should be valid
        var principal = await _jwt.ValidateAccessTokenAsync(login.AccessToken);
        Assert.NotNull(principal);
        Assert.Equal("TestPlayer", principal.FindFirst("sub")?.Value);
        Assert.Equal("device_1", principal.FindFirst("deviceId")?.Value);

        // ── First Refresh ──
        var refresh1 = _authService.Refresh(login.RefreshToken);
        Assert.NotNull(refresh1);

        // Old access token is still technically valid (short lifetime, not revoked)
        // But old refresh token should be revoked
        var failedRefresh = _authService.Refresh(login.RefreshToken);
        Assert.Null(failedRefresh);

        // New tokens work
        var principal2 = await _jwt.ValidateAccessTokenAsync(refresh1.AccessToken);
        Assert.NotNull(principal2);
        Assert.Equal("TestPlayer", principal2.FindFirst("sub")?.Value);

        // ── Second Refresh ──
        var refresh2 = _authService.Refresh(refresh1.RefreshToken);
        Assert.NotNull(refresh2);
        Assert.NotEqual(refresh1.RefreshToken, refresh2.RefreshToken);

        // ── Logout ──
        _authService.Logout(refresh2.RefreshToken);

        // Refresh should fail after logout
        var postLogout = _authService.Refresh(refresh2.RefreshToken);
        Assert.Null(postLogout);
    }

    /// <summary>
    /// Multiple devices can be logged in simultaneously for the same user.
    /// </summary>
    [Fact]
    public async Task Multi_device_concurrent_sessions()
    {
        var login1 = await _authService.LoginAsync("fake_code", "device_1", "iOS");
        var login2 = await _authService.LoginAsync("fake_code", "device_2", "Android");

        // Both sessions should work independently
        var refresh1 = _authService.Refresh(login1.RefreshToken);
        var refresh2 = _authService.Refresh(login2.RefreshToken);

        Assert.NotNull(refresh1);
        Assert.NotNull(refresh2);

        // Logout one device doesn't affect the other
        _authService.Logout(refresh1.RefreshToken);

        var refresh2Again = _authService.Refresh(refresh2.RefreshToken);
        Assert.NotNull(refresh2Again);
    }

    /// <summary>
    /// When a player logs in via Epic OAuth, backfill is enqueued and registration
    /// tracks their account ID.
    /// </summary>
    [Fact]
    public async Task Login_gets_backfill_and_registration()
    {
        var login = await _authService.LoginAsync("fake_code", "device_1", null);

        Assert.Equal("epic_acct_123", login.AccountId);
        Assert.True(_backfillQueue.HasPending);

        var registered = MetaDb.GetRegisteredAccountIds();
        Assert.Contains("epic_acct_123", registered);
    }

    /// <summary>
    /// Session cleanup removes expired sessions without affecting active ones.
    /// </summary>
    [Fact]
    public async Task Session_cleanup_preserves_active_sessions()
    {
        var login = await _authService.LoginAsync("fake_code", "device_1", null);

        // Active session should survive cleanup
        var cleaned = MetaDb.CleanupExpiredSessions(DateTime.UtcNow);
        Assert.Equal(0, cleaned);

        // Session should still work
        var refresh = _authService.Refresh(login.RefreshToken);
        Assert.NotNull(refresh);
    }
}
