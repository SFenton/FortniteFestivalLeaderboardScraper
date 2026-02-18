using System.Security.Cryptography;
using System.Text;
using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Auth;

/// <summary>
/// Secure server-side vault for Epic Games user tokens.
///
/// Stores tokens encrypted with AES-256-GCM in the <c>EpicUserTokens</c> table.
/// Provides transparent lazy-refresh: when a consumer requests a user's access token,
/// the vault checks expiry and silently refreshes via Epic's OAuth API if needed.
///
/// <para><b>Security properties:</b></para>
/// <list type="bullet">
///   <item>Tokens are encrypted at rest — the DB alone is not sufficient to recover them.</item>
///   <item>Each upsert generates a fresh 12-byte nonce (IV), so identical tokens produce different ciphertext.</item>
///   <item>The encryption key is loaded from config (env var / secret manager), never stored in the DB.</item>
///   <item>AES-GCM provides authenticated encryption — tampering is detected on decryption.</item>
/// </list>
/// </summary>
public sealed class TokenVault
{
    // AES-GCM constants
    private const int NonceBytes = 12;   // 96-bit nonce (NIST recommended)
    private const int TagBytes = 16;     // 128-bit authentication tag

    private readonly MetaDatabase _metaDb;
    private readonly EpicAuthService _epic;
    private readonly EpicOAuthSettings _settings;
    private readonly byte[] _encryptionKey;
    private readonly ILogger<TokenVault> _log;

    public TokenVault(
        MetaDatabase metaDb,
        EpicAuthService epic,
        IOptions<EpicOAuthSettings> settings,
        ILogger<TokenVault> log)
    {
        _metaDb = metaDb;
        _epic = epic;
        _settings = settings.Value;
        _log = log;

        // Decode the base64 encryption key. Allow empty for dev/testing
        // (callers should check HasEncryptionKey before storing).
        if (!string.IsNullOrWhiteSpace(_settings.TokenEncryptionKey)
            && !_settings.TokenEncryptionKey.StartsWith("CHANGE-ME"))
        {
            _encryptionKey = Convert.FromBase64String(_settings.TokenEncryptionKey);
            if (_encryptionKey.Length != 32)
                throw new InvalidOperationException(
                    $"EpicOAuth:TokenEncryptionKey must be exactly 32 bytes (256 bits). Got {_encryptionKey.Length} bytes.");
        }
        else
        {
            _encryptionKey = [];
            log.LogWarning(
                "EpicOAuth:TokenEncryptionKey is not configured — Epic user tokens will NOT be stored. " +
                "Generate a key with: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))");
        }
    }

    /// <summary>
    /// Whether the vault has a valid encryption key and can store/retrieve tokens.
    /// When false, <see cref="StoreAsync"/> is a no-op and <see cref="GetAccessTokenAsync"/> returns null.
    /// </summary>
    public bool HasEncryptionKey => _encryptionKey.Length == 32;

    // ─── Store ──────────────────────────────────────────────────

    /// <summary>
    /// Encrypt and persist an Epic token pair for a user.
    /// Silently no-ops if encryption is not configured.
    /// </summary>
    public void Store(string accountId, EpicTokenResponse epicToken)
    {
        if (!HasEncryptionKey)
        {
            _log.LogDebug("Token vault has no encryption key; skipping store for {AccountId}.", accountId);
            return;
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var encAccess = Encrypt(epicToken.AccessToken, nonce);
        var encRefresh = Encrypt(epicToken.RefreshToken, nonce);

        _metaDb.UpsertEpicUserToken(
            accountId,
            encAccess,
            encRefresh,
            epicToken.ExpiresAt,
            epicToken.RefreshExpiresAt,
            nonce);

        _log.LogInformation(
            "Stored Epic tokens for {AccountId} (access expires {ExpiresAt}, refresh expires {RefreshExpiresAt}).",
            accountId, epicToken.ExpiresAt, epicToken.RefreshExpiresAt);
    }

    // ─── Retrieve (with lazy refresh) ───────────────────────────

    /// <summary>
    /// Get a valid Epic access token for the given account.
    /// If the stored access token is expired but the refresh token is still valid,
    /// automatically refreshes and updates the vault.
    /// </summary>
    /// <returns>
    /// A valid Epic access token, or <c>null</c> if:
    /// <list type="bullet">
    ///   <item>No encryption key is configured</item>
    ///   <item>No tokens are stored for this account</item>
    ///   <item>Both the access and refresh tokens have expired (user must re-authenticate)</item>
    /// </list>
    /// </returns>
    public async Task<string?> GetAccessTokenAsync(string accountId, CancellationToken ct = default)
    {
        if (!HasEncryptionKey) return null;

        var stored = _metaDb.GetEpicUserToken(accountId);
        if (stored is null)
        {
            _log.LogDebug("No stored Epic tokens for {AccountId}.", accountId);
            return null;
        }

        // If the refresh token has expired, the user must re-authenticate
        if (stored.RefreshExpiresAt <= DateTimeOffset.UtcNow)
        {
            _log.LogWarning(
                "Epic refresh token expired for {AccountId} (expired {ExpiresAt}). User must re-authenticate.",
                accountId, stored.RefreshExpiresAt);
            _metaDb.DeleteEpicUserToken(accountId);
            return null;
        }

        // If the access token is still valid (with 60s buffer), return it
        if (stored.TokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
        {
            return Decrypt(stored.EncryptedAccessToken, stored.Nonce);
        }

        // Access token expired — refresh it
        _log.LogDebug("Epic access token expired for {AccountId}, refreshing...", accountId);
        var refreshToken = Decrypt(stored.EncryptedRefreshToken, stored.Nonce);

        try
        {
            var newToken = await _epic.RefreshUserTokenAsync(
                refreshToken, _settings.ClientId, _settings.ClientSecret, ct);

            // Store the new tokens
            Store(accountId, newToken);

            _log.LogInformation("Refreshed Epic token for {AccountId}.", accountId);
            return newToken.AccessToken;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to refresh Epic token for {AccountId}. User may need to re-authenticate.", accountId);
            // Don't delete — the refresh token might still be valid on retry
            return null;
        }
    }

    // ─── Revoke ─────────────────────────────────────────────────

    /// <summary>
    /// Delete all stored Epic tokens for a user. Called on logout.
    /// Does not call Epic's token kill endpoint (best-effort — the token
    /// will expire naturally within a few hours).
    /// </summary>
    public void Revoke(string accountId)
    {
        _metaDb.DeleteEpicUserToken(accountId);
        _log.LogInformation("Revoked stored Epic tokens for {AccountId}.", accountId);
    }

    // ─── Encryption helpers ─────────────────────────────────────

    /// <summary>
    /// Encrypt plaintext using AES-256-GCM.
    /// Returns <c>ciphertext + tag</c> (tag appended at the end).
    /// </summary>
    internal byte[] Encrypt(string plaintext, byte[] nonce)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(_encryptionKey, TagBytes);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Return ciphertext + tag concatenated
        var result = new byte[ciphertext.Length + TagBytes];
        Buffer.BlockCopy(ciphertext, 0, result, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, ciphertext.Length, TagBytes);
        return result;
    }

    /// <summary>
    /// Decrypt ciphertext+tag produced by <see cref="Encrypt"/>.
    /// </summary>
    internal string Decrypt(byte[] ciphertextWithTag, byte[] nonce)
    {
        var ciphertextLength = ciphertextWithTag.Length - TagBytes;
        var ciphertext = ciphertextWithTag.AsSpan(0, ciphertextLength);
        var tag = ciphertextWithTag.AsSpan(ciphertextLength, TagBytes);
        var plaintext = new byte[ciphertextLength];

        using var aes = new AesGcm(_encryptionKey, TagBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }
}
