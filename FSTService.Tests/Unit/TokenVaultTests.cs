using System.Security.Cryptography;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="TokenVault"/> — encrypted Epic token storage with lazy refresh.
/// </summary>
public sealed class TokenVaultTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaDb = new();
    private readonly EpicAuthService _epic;
    private readonly ILogger<TokenVault> _log = Substitute.For<ILogger<TokenVault>>();
    private readonly string _encryptionKeyBase64;

    public TokenVaultTests()
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler);
        _epic = Substitute.For<EpicAuthService>(http, Substitute.For<ILogger<EpicAuthService>>());
        _encryptionKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    public void Dispose() => _metaDb.Dispose();

    private TokenVault CreateVault(string? keyOverride = null)
    {
        var settings = new EpicOAuthSettings
        {
            TokenEncryptionKey = keyOverride ?? _encryptionKeyBase64,
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret",
        };
        return new TokenVault(_metaDb.Db, _epic, Options.Create(settings), _log);
    }

    private static EpicTokenResponse CreateToken(
        string accessToken = "access_token_value",
        string refreshToken = "refresh_token_value",
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? refreshExpiresAt = null)
    {
        return new EpicTokenResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(2),
            RefreshExpiresAt = refreshExpiresAt ?? DateTimeOffset.UtcNow.AddDays(7),
            ExpiresIn = 7200,
            TokenType = "bearer",
            AccountId = "acct_123",
            DisplayName = "TestUser",
        };
    }

    // ─── Constructor ────────────────────────────────────────────

    [Fact]
    public void Constructor_ValidKey_SetsHasEncryptionKeyTrue()
    {
        var vault = CreateVault();
        Assert.True(vault.HasEncryptionKey);
    }

    [Fact]
    public void Constructor_EmptyKey_SetsHasEncryptionKeyFalse()
    {
        var vault = CreateVault("");
        Assert.False(vault.HasEncryptionKey);
    }

    [Fact]
    public void Constructor_ChangeMe_SetsHasEncryptionKeyFalse()
    {
        var vault = CreateVault("CHANGE-ME-please");
        Assert.False(vault.HasEncryptionKey);
    }

    [Fact]
    public void Constructor_WrongKeyLength_Throws()
    {
        var shortKey = Convert.ToBase64String(new byte[16]); // 16 bytes, not 32
        Assert.Throws<InvalidOperationException>(() => CreateVault(shortKey));
    }

    // ─── Store ──────────────────────────────────────────────────

    [Fact]
    public void Store_WithValidKey_PersistsEncryptedToken()
    {
        var vault = CreateVault();
        var token = CreateToken();

        vault.Store("acct_123", token);

        var stored = _metaDb.Db.GetEpicUserToken("acct_123");
        Assert.NotNull(stored);
        Assert.Equal("acct_123", stored.AccountId);
        // Encrypted bytes should NOT match the plaintext
        Assert.NotEmpty(stored.EncryptedAccessToken);
        Assert.NotEmpty(stored.EncryptedRefreshToken);
        Assert.NotEmpty(stored.Nonce);
    }

    [Fact]
    public void Store_WithoutKey_IsNoOp()
    {
        var vault = CreateVault("");
        var token = CreateToken();

        vault.Store("acct_123", token);

        var stored = _metaDb.Db.GetEpicUserToken("acct_123");
        Assert.Null(stored);
    }

    // ─── Encrypt/Decrypt roundtrip ──────────────────────────────

    [Fact]
    public void EncryptDecrypt_Roundtrips()
    {
        var vault = CreateVault();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = "hello world of encryption";

        var ciphertext = vault.Encrypt(plaintext, nonce);
        var decrypted = vault.Decrypt(ciphertext, nonce);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesDifferentOutputForSameInput_WithDifferentNonces()
    {
        var vault = CreateVault();
        var nonce1 = RandomNumberGenerator.GetBytes(12);
        var nonce2 = RandomNumberGenerator.GetBytes(12);

        var ct1 = vault.Encrypt("same_input", nonce1);
        var ct2 = vault.Encrypt("same_input", nonce2);

        Assert.NotEqual(ct1, ct2);
    }

    // ─── GetAccessTokenAsync ────────────────────────────────────

    [Fact]
    public async Task GetAccessTokenAsync_NoKey_ReturnsNull()
    {
        var vault = CreateVault("");
        var result = await vault.GetAccessTokenAsync("acct_123");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoStoredToken_ReturnsNull()
    {
        var vault = CreateVault();
        var result = await vault.GetAccessTokenAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAccessTokenAsync_AccessTokenValid_ReturnsIt()
    {
        var vault = CreateVault();
        var token = CreateToken(
            accessToken: "my_access_token",
            expiresAt: DateTimeOffset.UtcNow.AddHours(2),
            refreshExpiresAt: DateTimeOffset.UtcNow.AddDays(7));
        vault.Store("acct_123", token);

        var result = await vault.GetAccessTokenAsync("acct_123");
        Assert.Equal("my_access_token", result);
    }

    [Fact]
    public async Task GetAccessTokenAsync_RefreshExpired_DeletesAndReturnsNull()
    {
        var vault = CreateVault();
        var token = CreateToken(
            expiresAt: DateTimeOffset.UtcNow.AddHours(-1),
            refreshExpiresAt: DateTimeOffset.UtcNow.AddHours(-1));
        vault.Store("acct_123", token);

        var result = await vault.GetAccessTokenAsync("acct_123");
        Assert.Null(result);
        // Token should be deleted
        Assert.Null(_metaDb.Db.GetEpicUserToken("acct_123"));
    }

    [Fact]
    public async Task GetAccessTokenAsync_AccessExpired_RefreshSucceeds_ReturnsNewToken()
    {
        var vault = CreateVault();
        var token = CreateToken(
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-10), // expired
            refreshExpiresAt: DateTimeOffset.UtcNow.AddDays(7));
        vault.Store("acct_123", token);

        var newToken = new EpicTokenResponse
        {
            AccessToken = "refreshed_access_token",
            RefreshToken = "refreshed_refresh_token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            ExpiresIn = 7200,
            TokenType = "bearer",
            AccountId = "acct_123",
        };

        _epic.RefreshUserTokenAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(newToken));

        var result = await vault.GetAccessTokenAsync("acct_123");
        Assert.Equal("refreshed_access_token", result);
    }

    [Fact]
    public async Task GetAccessTokenAsync_AccessExpired_RefreshFails_ReturnsNull()
    {
        var vault = CreateVault();
        var token = CreateToken(
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-10),
            refreshExpiresAt: DateTimeOffset.UtcNow.AddDays(7));
        vault.Store("acct_123", token);

        _epic.RefreshUserTokenAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<EpicTokenResponse>(x => throw new InvalidOperationException("Refresh failed"));

        var result = await vault.GetAccessTokenAsync("acct_123");
        Assert.Null(result);
        // Token should NOT be deleted (refresh token might still be valid on retry)
        Assert.NotNull(_metaDb.Db.GetEpicUserToken("acct_123"));
    }

    // ─── Revoke ─────────────────────────────────────────────────

    [Fact]
    public void Revoke_DeletesStoredTokens()
    {
        var vault = CreateVault();
        vault.Store("acct_123", CreateToken());
        Assert.NotNull(_metaDb.Db.GetEpicUserToken("acct_123"));

        vault.Revoke("acct_123");
        Assert.Null(_metaDb.Db.GetEpicUserToken("acct_123"));
    }
}
