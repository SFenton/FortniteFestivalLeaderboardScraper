using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

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

    private static readonly EpicOAuthSettings TestOAuthSettings = new()
    {
        ClientId = "test-client-id",
        ClientSecret = "test-client-secret",
        RedirectUri = "https://example.com/api/auth/epiccallback",
        AppDeepLink = "festscoretracker://auth/callback",
    };

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
            glp, festivalService, _metaFixture.Db, _dataDir,
            NullLogger<PersonalDbBuilder>.Instance);
    }

    public void Dispose()
    {
        _metaFixture.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

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

    private TokenVault CreateTestVault(EpicAuthService? epic = null) =>
        new(MetaDb, epic ?? CreateMockEpic(),
            Options.Create(TestOAuthSettings),
            NullLogger<TokenVault>.Instance);

    private UserAuthService CreateService(JwtTokenService? jwt = null, EpicAuthService? epic = null) =>
        new(jwt ?? CreateJwt(), MetaDb, _personalDbBuilder, _backfillQueue,
            epic ?? CreateMockEpic(),
            Options.Create(TestOAuthSettings),
            CreateTestVault(epic),
            NullLogger<UserAuthService>.Instance);

    // ═══ Login ══════════════════════════════════════════════════

    [Fact]
    public async Task Login_returns_tokens_and_accountId()
    {
        var svc = CreateService();
        var result = await svc.LoginAsync("fake_code", "device_1", "iOS");

        Assert.False(string.IsNullOrEmpty(result.AccessToken));
        Assert.False(string.IsNullOrEmpty(result.RefreshToken));
        Assert.Equal("epic_acct_123", result.AccountId);
        Assert.Equal("TestPlayer", result.DisplayName);
        Assert.True(result.ExpiresIn > 0);
    }

    [Fact]
    public async Task Login_creates_user_registration()
    {
        var svc = CreateService();
        await svc.LoginAsync("fake_code", "device_1", "Android");

        Assert.True(MetaDb.IsDeviceRegistered("device_1"));
    }

    [Fact]
    public async Task Login_enqueues_backfill()
    {
        var svc = CreateService();
        await svc.LoginAsync("fake_code", "device_1", null);

        Assert.True(_backfillQueue.HasPending);
        var items = _backfillQueue.DrainAll();
        Assert.Single(items);
        Assert.Equal("epic_acct_123", items[0].AccountId);
    }

    [Fact]
    public async Task Login_creates_session()
    {
        var jwt = CreateJwt();
        var svc = CreateService(jwt);
        var result = await svc.LoginAsync("fake_code", "device_1", null);

        var hash = JwtTokenService.HashRefreshToken(result.RefreshToken);
        var session = MetaDb.GetActiveSession(hash);
        Assert.NotNull(session);
        Assert.Equal("TestPlayer", session.Username);
        Assert.Equal("device_1", session.DeviceId);
    }

    [Fact]
    public async Task Login_stores_display_name_in_AccountNames()
    {
        var svc = CreateService();
        await svc.LoginAsync("fake_code", "device_1", null);

        // The account name should be stored in AccountNames
        var accountId = MetaDb.GetAccountIdForUsername("TestPlayer");
        Assert.Equal("epic_acct_123", accountId);
    }

    // ═══ Refresh ════════════════════════════════════════════════

    [Fact]
    public async Task Refresh_with_valid_token_returns_new_tokens()
    {
        var jwt = CreateJwt();
        var svc = CreateService(jwt);
        var login = await svc.LoginAsync("fake_code", "device_1", null);

        var refresh = svc.Refresh(login.RefreshToken);

        Assert.NotNull(refresh);
        Assert.False(string.IsNullOrEmpty(refresh.AccessToken));
        Assert.False(string.IsNullOrEmpty(refresh.RefreshToken));
        Assert.NotEqual(login.AccessToken, refresh.AccessToken);
        Assert.NotEqual(login.RefreshToken, refresh.RefreshToken);
    }

    [Fact]
    public async Task Refresh_revokes_old_session()
    {
        var jwt = CreateJwt();
        var svc = CreateService(jwt);
        var login = await svc.LoginAsync("fake_code", "device_1", null);

        var oldHash = JwtTokenService.HashRefreshToken(login.RefreshToken);
        svc.Refresh(login.RefreshToken);

        // Old session should be revoked
        Assert.Null(MetaDb.GetActiveSession(oldHash));
    }

    [Fact]
    public async Task Refresh_creates_new_session()
    {
        var jwt = CreateJwt();
        var svc = CreateService(jwt);
        var login = await svc.LoginAsync("fake_code", "device_1", null);

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
    public async Task Refresh_returns_null_for_already_used_token()
    {
        var jwt = CreateJwt();
        var svc = CreateService(jwt);
        var login = await svc.LoginAsync("fake_code", "device_1", null);

        // First refresh succeeds
        svc.Refresh(login.RefreshToken);

        // Second use of same token returns null (rotation)
        var second = svc.Refresh(login.RefreshToken);
        Assert.Null(second);
    }

    // ═══ Logout ═════════════════════════════════════════════════

    [Fact]
    public async Task Logout_revokes_session()
    {
        var jwt = CreateJwt();
        var svc = CreateService(jwt);
        var login = await svc.LoginAsync("fake_code", "device_1", null);

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

    // ═══ Login edge cases ═══════════════════════════════════════

    [Fact]
    public async Task Login_throws_when_accountId_is_empty()
    {
        var epic = CreateMockEpic(accountId: "", displayName: "SomePlayer");
        var svc = CreateService(epic: epic);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.LoginAsync("code", "device_1", "iOS"));
    }

    [Fact]
    public async Task Login_uses_accountId_as_displayName_when_displayName_is_empty()
    {
        var epic = CreateMockEpic(accountId: "epic_acct_456", displayName: "");
        var svc = CreateService(epic: epic);

        var result = await svc.LoginAsync("code", "device_1", "iOS");

        // When displayName is empty, it should fall back to accountId
        Assert.Equal("epic_acct_456", result.DisplayName);
        Assert.Equal("epic_acct_456", result.AccountId);
    }
}
