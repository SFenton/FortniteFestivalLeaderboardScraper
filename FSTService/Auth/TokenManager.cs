using System.Text.Json;

namespace FSTService.Auth;

/// <summary>
/// Manages the lifecycle of an Epic access token:
/// – Holds the current token in memory
/// – Refreshes automatically before expiry
/// – Persists refresh tokens to disk for cross-restart persistence
/// – Falls back to device-code flow when no credentials exist
/// </summary>
public sealed class TokenManager
{
    private readonly EpicAuthService _auth;
    private readonly ICredentialStore _store;
    private readonly ILogger<TokenManager> _log;

    private EpicTokenResponse? _currentToken;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Fires when the user needs to complete a device code login (initial setup or re-auth).
    /// The string is the <c>verification_uri_complete</c> URL.
    /// </summary>
    public event Action<string>? DeviceCodeLoginRequired;

    public TokenManager(EpicAuthService auth, ICredentialStore store, ILogger<TokenManager> log)
    {
        _auth = auth;
        _store = store;
        _log = log;
    }

    /// <summary>
    /// The currently authenticated Epic account ID, or null if not authenticated.
    /// </summary>
    public string? AccountId => _currentToken?.AccountId;

    /// <summary>
    /// Get a valid access token, refreshing or re-authenticating as needed.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // If we have a token that isn't close to expiry, return it
            if (_currentToken is not null &&
                _currentToken.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return _currentToken.AccessToken;
            }

            // Try refresh if we have an in-memory refresh token
            if (_currentToken is not null && !string.IsNullOrEmpty(_currentToken.RefreshToken))
            {
                var refreshed = await TryRefreshAsync(_currentToken.RefreshToken, ct);
                if (refreshed is not null) return refreshed;
            }

            // Try loading persisted refresh token from disk
            var stored = await _store.LoadAsync(ct);
            if (stored is not null && !string.IsNullOrEmpty(stored.RefreshToken))
            {
                _log.LogInformation("Loaded stored credentials for account {AccountId}. Attempting refresh...",
                    stored.AccountId);
                var refreshed = await TryRefreshAsync(stored.RefreshToken, ct);
                if (refreshed is not null) return refreshed;

                _log.LogWarning("Stored refresh token expired or invalid. Re-run with --setup.");
            }

            // No valid auth — need interactive device code login
            _log.LogWarning("No valid authentication available. Device code login required.");
            _currentToken = null;
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Perform the interactive device code setup flow.
    /// Blocks until the user completes login in their browser.
    /// </summary>
    public async Task<bool> PerformDeviceCodeSetupAsync(CancellationToken ct = default)
    {
        _log.LogInformation("Starting device code login flow...");

        var deviceAuth = await _auth.StartDeviceCodeFlowAsync(ct);

        _log.LogInformation("=== DEVICE CODE LOGIN ===");
        _log.LogInformation("Open this URL in your browser:");
        _log.LogInformation("{Url}", deviceAuth.VerificationUriComplete);
        _log.LogInformation("Or go to {Uri} and enter code: {Code}",
            deviceAuth.VerificationUri, deviceAuth.UserCode);
        _log.LogInformation("Waiting for login (expires in {Seconds}s)...", deviceAuth.ExpiresIn);

        // Notify any listeners
        DeviceCodeLoginRequired?.Invoke(deviceAuth.VerificationUriComplete);

        try
        {
            var token = await _auth.PollDeviceCodeAsync(deviceAuth, ct);
            _currentToken = token;

            _log.LogInformation("Login successful! Welcome, {DisplayName}.", token.DisplayName);

            // Persist the refresh token for future unattended use
            await PersistRefreshTokenAsync(token, ct);
            _log.LogInformation("Credentials saved. Future logins will be automatic (refresh within ~8 h).");

            return true;
        }
        catch (TimeoutException)
        {
            _log.LogError("Device code login timed out.");
            return false;
        }
    }

    // ─── Helpers ──────────────────────────────────

    private async Task<string?> TryRefreshAsync(string refreshToken, CancellationToken ct)
    {
        _log.LogInformation("Refreshing access token...");
        try
        {
            var refreshed = await _auth.RefreshTokenAsync(refreshToken, ct);
            if (refreshed is not null)
            {
                _currentToken = refreshed;
                _log.LogInformation("Token refreshed for {DisplayName}. Expires at {ExpiresAt}",
                    refreshed.DisplayName, refreshed.ExpiresAt);

                // Persist the new refresh token (they roll with each refresh)
                await PersistRefreshTokenAsync(refreshed, ct);
                return refreshed.AccessToken;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Refresh failed");
        }
        return null;
    }

    private async Task PersistRefreshTokenAsync(EpicTokenResponse token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token.RefreshToken)) return;

        var creds = new StoredCredentials
        {
            AccountId = token.AccountId,
            RefreshToken = token.RefreshToken,
            DisplayName = token.DisplayName,
            SavedAt = DateTimeOffset.UtcNow,
        };
        await _store.SaveAsync(creds, ct);
    }
}
