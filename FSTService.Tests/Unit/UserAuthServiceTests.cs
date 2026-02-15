using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FSTService.Tests.Unit;

public sealed class UserAuthServiceTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaFixture = new();
    private MetaDatabase MetaDb => _metaFixture.Db;
    private readonly string _dataDir;

    private readonly JwtSettings _jwtSettings = new()
    {
        Secret = "ThisIsATestSecretKeyThatIs32Chars!",
        Issuer = "FSTService.Tests",
        AccessTokenLifetimeMinutes = 60,
        RefreshTokenLifetimeDays = 30,
    };

    private JwtTokenService CreateJwt() => new(Options.Create(_jwtSettings));

    private readonly PersonalDbBuilder _personalDbBuilder;
    private readonly BackfillQueue _backfillQueue = new();

    public UserAuthServiceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_auth_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        // Create real instances — PersonalDbBuilder.Build() returns null when
        // no songs are loaded, which is the correct behavior for tests.
        var loggerFactory = new NullLoggerFactory();
        var glp = new GlobalLeaderboardPersistence(
            _dataDir, MetaDb, loggerFactory,
            NullLogger<GlobalLeaderboardPersistence>.Instance);
        glp.Initialize();

        var festivalService = new FortniteFestival.Core.Services.FestivalService();

        _personalDbBuilder = new PersonalDbBuilder(
            glp, festivalService, _dataDir,
            NullLogger<PersonalDbBuilder>.Instance);
    }

    public void Dispose()
    {
        _metaFixture.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private UserAuthService CreateService(JwtTokenService? jwt = null) =>
        new(jwt ?? CreateJwt(), MetaDb, _personalDbBuilder, _backfillQueue,
            NullLogger<UserAuthService>.Instance);

    // ═══ Login ══════════════════════════════════════════════════

    [Fact]
    public void Login_known_user_returns_tokens_and_accountId()
    {
        // Pre-register the username → accountId mapping
        MetaDb.InsertAccountNames([("epic_acct_123", "TestPlayer")]);

        var svc = CreateService();
        var result = svc.Login("TestPlayer", "device_1", "iOS");

        Assert.False(string.IsNullOrEmpty(result.AccessToken));
        Assert.False(string.IsNullOrEmpty(result.RefreshToken));
        Assert.Equal("epic_acct_123", result.AccountId);
        Assert.Equal("TestPlayer", result.DisplayName);
        Assert.True(result.ExpiresIn > 0);
    }

    [Fact]
    public void Login_unknown_user_returns_null_accountId()
    {
        var svc = CreateService();
        var result = svc.Login("UnknownPlayer", "device_1", null);

        Assert.Null(result.AccountId);
        Assert.Equal("UnknownPlayer", result.DisplayName);
        Assert.False(string.IsNullOrEmpty(result.AccessToken));
    }

    [Fact]
    public void Login_creates_user_registration()
    {
        var svc = CreateService();
        svc.Login("TestPlayer", "device_1", "Android");

        Assert.True(MetaDb.IsDeviceRegistered("device_1"));
    }

    [Fact]
    public void Login_enqueues_backfill_for_known_user()
    {
        MetaDb.InsertAccountNames([("epic_acct_123", "TestPlayer")]);

        var svc = CreateService();
        svc.Login("TestPlayer", "device_1", null);

        Assert.True(_backfillQueue.HasPending);
        var items = _backfillQueue.DrainAll();
        Assert.Single(items);
        Assert.Equal("epic_acct_123", items[0].AccountId);
    }

    [Fact]
    public void Login_does_not_enqueue_backfill_for_unknown_user()
    {
        var svc = CreateService();
        svc.Login("UnknownPlayer", "device_1", null);

        Assert.False(_backfillQueue.HasPending);
    }

    [Fact]
    public void Login_creates_session()
    {
        var jwt = CreateJwt();
        var svc = CreateService(jwt);
        var result = svc.Login("TestPlayer", "device_1", null);

        var hash = JwtTokenService.HashRefreshToken(result.RefreshToken);
        var session = MetaDb.GetActiveSession(hash);
        Assert.NotNull(session);
        Assert.Equal("TestPlayer", session.Username);
        Assert.Equal("device_1", session.DeviceId);
    }

    // ═══ Refresh ════════════════════════════════════════════════

    [Fact]
    public void Refresh_with_valid_token_returns_new_tokens()
    {
        var jwt = CreateJwt();
        var svc = CreateService(jwt);
        var login = svc.Login("TestPlayer", "device_1", null);

        var refresh = svc.Refresh(login.RefreshToken);

        Assert.NotNull(refresh);
        Assert.False(string.IsNullOrEmpty(refresh.AccessToken));
        Assert.False(string.IsNullOrEmpty(refresh.RefreshToken));
        Assert.NotEqual(login.AccessToken, refresh.AccessToken);
        Assert.NotEqual(login.RefreshToken, refresh.RefreshToken);
    }

    [Fact]
    public void Refresh_revokes_old_session()
    {
        var jwt = CreateJwt();
        var svc = CreateService(jwt);
        var login = svc.Login("TestPlayer", "device_1", null);

        var oldHash = JwtTokenService.HashRefreshToken(login.RefreshToken);
        svc.Refresh(login.RefreshToken);

        // Old session should be revoked
        Assert.Null(MetaDb.GetActiveSession(oldHash));
    }

    [Fact]
    public void Refresh_creates_new_session()
    {
        var jwt = CreateJwt();
        var svc = CreateService(jwt);
        var login = svc.Login("TestPlayer", "device_1", null);

        var refresh = svc.Refresh(login.RefreshToken);

        var newHash = JwtTokenService.HashRefreshToken(refresh!.RefreshToken);
        var session = MetaDb.GetActiveSession(newHash);
        Assert.NotNull(session);
        Assert.Equal("TestPlayer", session.Username);
    }

    [Fact]
    public void Refresh_returns_null_for_invalid_token()
    {
        var svc = CreateService();
        var result = svc.Refresh("bogus_token");
        Assert.Null(result);
    }

    [Fact]
    public void Refresh_returns_null_for_already_used_token()
    {
        var jwt = CreateJwt();
        var svc = CreateService(jwt);
        var login = svc.Login("TestPlayer", "device_1", null);

        // First refresh succeeds
        svc.Refresh(login.RefreshToken);

        // Second use of same token returns null (rotation)
        var second = svc.Refresh(login.RefreshToken);
        Assert.Null(second);
    }

    // ═══ Logout ═════════════════════════════════════════════════

    [Fact]
    public void Logout_revokes_session()
    {
        var jwt = CreateJwt();
        var svc = CreateService(jwt);
        var login = svc.Login("TestPlayer", "device_1", null);

        svc.Logout(login.RefreshToken);

        var hash = JwtTokenService.HashRefreshToken(login.RefreshToken);
        Assert.Null(MetaDb.GetActiveSession(hash));
    }

    [Fact]
    public void Logout_does_not_throw_for_invalid_token()
    {
        var svc = CreateService();
        var exception = Record.Exception(() => svc.Logout("bogus_token"));
        Assert.Null(exception);
    }
}
