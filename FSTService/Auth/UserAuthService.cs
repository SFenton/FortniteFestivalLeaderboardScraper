using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService.Auth;

/// <summary>
/// Orchestrates user authentication flows: login, refresh, logout.
/// </summary>
public sealed class UserAuthService
{
    private readonly JwtTokenService _jwt;
    private readonly MetaDatabase _metaDb;
    private readonly PersonalDbBuilder _personalDbBuilder;
    private readonly BackfillQueue _backfillQueue;
    private readonly EpicAuthService _epic;
    private readonly EpicOAuthSettings _epicOAuth;
    private readonly TokenVault _tokenVault;
    private readonly ILogger<UserAuthService> _log;

    public UserAuthService(
        JwtTokenService jwt,
        MetaDatabase metaDb,
        PersonalDbBuilder personalDbBuilder,
        BackfillQueue backfillQueue,
        EpicAuthService epic,
        IOptions<EpicOAuthSettings> epicOAuth,
        TokenVault tokenVault,
        ILogger<UserAuthService> log)
    {
        _jwt = jwt;
        _metaDb = metaDb;
        _personalDbBuilder = personalDbBuilder;
        _backfillQueue = backfillQueue;
        _epic = epic;
        _epicOAuth = epicOAuth.Value;
        _tokenVault = tokenVault;
        _log = log;
    }

    /// <summary>
    /// Authenticate a user by exchanging an Epic authorization code.
    /// Exchanges the code server-side for an Epic token, extracts the
    /// account ID and display name, then registers the user and issues JWT tokens.
    /// </summary>
    public async Task<LoginResult> LoginAsync(string code, string deviceId, string? platform)
    {
        // 1. Exchange the authorization code with Epic for an access token
        var epicToken = await _epic.ExchangeAuthorizationCodeAsync(
            code, _epicOAuth.ClientId, _epicOAuth.ClientSecret, _epicOAuth.RedirectUri);

        var accountId = epicToken.AccountId;
        var displayName = epicToken.DisplayName;

        if (string.IsNullOrEmpty(accountId))
            throw new InvalidOperationException("Epic token exchange did not return an account ID.");

        // Fallback display name to the account ID if Epic didn't return one
        if (string.IsNullOrEmpty(displayName))
            displayName = accountId;

        _log.LogInformation(
            "Epic code exchange succeeded: accountId={AccountId}, displayName={DisplayName}",
            accountId, displayName);

        // 2. Store Epic tokens in the vault (encrypted at rest) for later API calls
        _tokenVault.Store(accountId, epicToken);

        // 3. Store/update the display name in AccountNames so the scraper knows this user
        _metaDb.InsertAccountNames([(accountId, displayName)]);

        // 4. Register/update the user+device pair
        var isNew = _metaDb.RegisterOrUpdateUser(deviceId, accountId, displayName, platform);

        // 5. Build personal DB
        string? dbPath = null;
        try
        {
            dbPath = _personalDbBuilder.Build(deviceId, accountId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to build personal DB for {AccountId} on login.", accountId);
        }

        // 6. Enqueue a backfill to fill in scores below the top 60K
        _backfillQueue.Enqueue(new BackfillRequest(accountId));

        // 7. Generate JWT tokens (displayName as subject for session continuity)
        var accessToken = _jwt.GenerateAccessToken(displayName, deviceId);
        var refreshToken = _jwt.GenerateRefreshToken();
        var refreshHash = JwtTokenService.HashRefreshToken(refreshToken);

        // 8. Create session
        var expiresAt = _jwt.RefreshTokenExpiry;
        _metaDb.InsertSession(displayName, deviceId, refreshHash, platform, expiresAt);

        _log.LogInformation(
            "User {DisplayName} logged in from device {DeviceId} (platform={Platform}, accountId={AccountId}, isNew={IsNew})",
            displayName, deviceId, platform ?? "unknown", accountId, isNew);

        return new LoginResult
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresIn = _jwt.AccessTokenLifetimeSeconds,
            AccountId = accountId,
            DisplayName = displayName,
            PersonalDbReady = dbPath is not null,
        };
    }

    /// <summary>
    /// Refresh an expired access token using a valid refresh token.
    /// Implements refresh token rotation (old token is revoked).
    /// </summary>
    public RefreshResult? Refresh(string refreshToken)
    {
        var hash = JwtTokenService.HashRefreshToken(refreshToken);
        var session = _metaDb.GetActiveSession(hash);

        if (session is null)
        {
            _log.LogWarning("Refresh attempt with invalid/expired token.");
            return null;
        }

        // Revoke the old session
        _metaDb.RevokeSession(hash);

        // Generate new tokens
        var newAccessToken = _jwt.GenerateAccessToken(session.Username, session.DeviceId);
        var newRefreshToken = _jwt.GenerateRefreshToken();
        var newHash = JwtTokenService.HashRefreshToken(newRefreshToken);

        // Create new session (rotation)
        var expiresAt = _jwt.RefreshTokenExpiry;
        _metaDb.InsertSession(session.Username, session.DeviceId, newHash, session.Platform, expiresAt);

        _log.LogDebug("Refreshed session for {Username} on device {DeviceId}.", session.Username, session.DeviceId);

        return new RefreshResult
        {
            AccessToken = newAccessToken,
            RefreshToken = newRefreshToken,
            ExpiresIn = _jwt.AccessTokenLifetimeSeconds,
        };
    }

    /// <summary>
    /// Log out by revoking the refresh token and deleting stored Epic tokens.
    /// Best-effort — does not fail if token not found.
    /// </summary>
    public void Logout(string refreshToken)
    {
        var hash = JwtTokenService.HashRefreshToken(refreshToken);

        // Look up the session to find the account for token revocation
        var session = _metaDb.GetActiveSession(hash);
        if (session is not null)
        {
            var accountId = _metaDb.GetAccountIdForUsername(session.Username);
            if (accountId is not null)
                _tokenVault.Revoke(accountId);
        }

        _metaDb.RevokeSession(hash);
        _log.LogDebug("Logout: revoked session for refresh token.");
    }
}

// ─── Result DTOs ────────────────────────────────────────────

public sealed class LoginResult
{
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public int ExpiresIn { get; init; }
    public string? AccountId { get; init; }
    public string DisplayName { get; init; } = "";
    public bool PersonalDbReady { get; init; }
}

public sealed class RefreshResult
{
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public int ExpiresIn { get; init; }
}
