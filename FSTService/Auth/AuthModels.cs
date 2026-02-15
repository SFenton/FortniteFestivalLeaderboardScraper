namespace FSTService.Auth;

/// <summary>
/// Persistent credentials that survive restarts.
/// Stores a refresh token obtained via the Device Code flow.
/// Each refresh yields a new refresh token (~8 h lifetime), so the service
/// saves the latest one after every successful refresh.
/// If the token expires (service offline >8 h), re-run <c>--setup</c>.
/// </summary>
public sealed class StoredCredentials
{
    public required string AccountId { get; init; }
    public required string RefreshToken { get; init; }
    public string DisplayName { get; init; } = "";
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Response from the device authorization endpoint.
/// The user visits <see cref="VerificationUriComplete"/> in their browser
/// to approve the login, while the service polls with <see cref="DeviceCode"/>.
/// </summary>
public sealed class DeviceAuthorizationResponse
{
    public required string UserCode { get; init; }
    public required string DeviceCode { get; init; }
    public required string VerificationUri { get; init; }
    public required string VerificationUriComplete { get; init; }
    public required int ExpiresIn { get; init; }
    public required int Interval { get; init; }
}

/// <summary>
/// OAuth token response returned by Epic's /account/api/oauth/token endpoint
/// for any grant type (device_code, refresh_token, etc.).
/// </summary>
public sealed class EpicTokenResponse
{
    public string AccessToken { get; init; } = "";
    public int ExpiresIn { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string TokenType { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public int RefreshExpires { get; init; }
    public DateTimeOffset RefreshExpiresAt { get; init; }
    public string AccountId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string DisplayName { get; init; } = "";
}
