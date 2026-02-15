namespace FSTService.Auth;

/// <summary>
/// Configuration for JWT access token and opaque refresh token issuance.
/// Bound from the "Jwt" section of appsettings.json.
/// </summary>
public sealed class JwtSettings
{
    public const string Section = "Jwt";

    /// <summary>
    /// HMAC-SHA256 symmetric key (at least 32 bytes / 256 bits).
    /// Override via environment variable <c>Jwt__Secret</c> in production.
    /// </summary>
    public string Secret { get; set; } = "";

    /// <summary>Issuer claim written into access tokens.</summary>
    public string Issuer { get; set; } = "FSTService";

    /// <summary>Access token lifetime in minutes (default: 60).</summary>
    public int AccessTokenLifetimeMinutes { get; set; } = 60;

    /// <summary>Refresh token lifetime in days (default: 30).</summary>
    public int RefreshTokenLifetimeDays { get; set; } = 30;
}
