using FSTService.Persistence;
using FSTService.Scraping;

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
    private readonly ILogger<UserAuthService> _log;

    public UserAuthService(
        JwtTokenService jwt,
        MetaDatabase metaDb,
        PersonalDbBuilder personalDbBuilder,
        BackfillQueue backfillQueue,
        ILogger<UserAuthService> log)
    {
        _jwt = jwt;
        _metaDb = metaDb;
        _personalDbBuilder = personalDbBuilder;
        _backfillQueue = backfillQueue;
        _log = log;
    }

    /// <summary>
    /// Authenticate a user by username + deviceId. Creates/updates registration,
    /// generates tokens, and builds a personal DB if possible.
    /// </summary>
    public LoginResult Login(string username, string deviceId, string? platform)
    {
        // 1. Look up the Epic account ID for this username
        var accountId = _metaDb.GetAccountIdForUsername(username);

        // 2. The identifier stored in RegisteredUsers — use the Epic account ID if
        //    known, otherwise store the username as a placeholder.
        var registrationId = accountId ?? username;

        // 3. Register/update the user+device pair
        var isNew = _metaDb.RegisterOrUpdateUser(deviceId, registrationId, username, platform);

        // 4. Build personal DB if account ID is known
        string? dbPath = null;
        if (accountId is not null)
        {
            try
            {
                dbPath = _personalDbBuilder.Build(deviceId, accountId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to build personal DB for {Username} on login.", username);
            }

            // 5. Enqueue a backfill to fill in scores below the top 60K
            _backfillQueue.Enqueue(new BackfillRequest(accountId));
        }

        // 5. Generate tokens
        var accessToken = _jwt.GenerateAccessToken(username, deviceId);
        var refreshToken = _jwt.GenerateRefreshToken();
        var refreshHash = JwtTokenService.HashRefreshToken(refreshToken);

        // 6. Create session
        var expiresAt = _jwt.RefreshTokenExpiry;
        _metaDb.InsertSession(username, deviceId, refreshHash, platform, expiresAt);

        _log.LogInformation(
            "User {Username} logged in from device {DeviceId} (platform={Platform}, accountId={AccountId}, isNew={IsNew})",
            username, deviceId, platform ?? "unknown", accountId ?? "pending", isNew);

        return new LoginResult
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresIn = _jwt.AccessTokenLifetimeSeconds,
            AccountId = accountId,
            DisplayName = username,
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
    /// Log out by revoking the refresh token. Best-effort — does not fail if token not found.
    /// </summary>
    public void Logout(string refreshToken)
    {
        var hash = JwtTokenService.HashRefreshToken(refreshToken);
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
