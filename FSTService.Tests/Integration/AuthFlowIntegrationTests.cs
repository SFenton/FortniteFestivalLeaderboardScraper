using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FSTService.Tests.Integration;

/// <summary>
/// End-to-end integration tests for the authentication flow, using real SQLite databases
/// and real JWT token generation/validation.
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

    private readonly JwtTokenService _jwt;
    private readonly BackfillQueue _backfillQueue = new();
    private readonly UserAuthService _authService;

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

        _authService = new UserAuthService(
            _jwt, MetaDb, personalDbBuilder, _backfillQueue,
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
        var login = _authService.Login("TestPlayer", "device_1", "iOS");
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
    public void Multi_device_concurrent_sessions()
    {
        var login1 = _authService.Login("TestPlayer", "device_1", "iOS");
        var login2 = _authService.Login("TestPlayer", "device_2", "Android");

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
    /// When a known player logs in, backfill is enqueued and registration tracks their account ID.
    /// </summary>
    [Fact]
    public void Known_player_gets_backfill_and_registration()
    {
        MetaDb.InsertAccountNames([("epic_acct_abc", "KnownPlayer")]);

        var login = _authService.Login("KnownPlayer", "device_1", null);

        Assert.Equal("epic_acct_abc", login.AccountId);
        Assert.True(_backfillQueue.HasPending);

        var registered = MetaDb.GetRegisteredAccountIds();
        Assert.Contains("epic_acct_abc", registered);
    }

    /// <summary>
    /// Session cleanup removes expired sessions without affecting active ones.
    /// </summary>
    [Fact]
    public void Session_cleanup_preserves_active_sessions()
    {
        var login = _authService.Login("TestPlayer", "device_1", null);

        // Active session should survive cleanup
        var cleaned = MetaDb.CleanupExpiredSessions(DateTime.UtcNow);
        Assert.Equal(0, cleaned);

        // Session should still work
        var refresh = _authService.Refresh(login.RefreshToken);
        Assert.NotNull(refresh);
    }

    /// <summary>
    /// Login creates a user registration even when the player is unknown.
    /// </summary>
    [Fact]
    public void Unknown_player_is_registered_with_username()
    {
        var login = _authService.Login("NewPlayer", "device_1", "iOS");

        Assert.Null(login.AccountId);
        Assert.True(MetaDb.IsDeviceRegistered("device_1"));
    }
}
