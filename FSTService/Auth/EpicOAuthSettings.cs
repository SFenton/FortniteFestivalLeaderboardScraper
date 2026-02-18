namespace FSTService.Auth;

/// <summary>
/// Configuration for the user-facing Epic Games OAuth flow (EOS registered application).
/// These credentials are separate from the Switch client used for scraping
/// (<see cref="EpicAuthService"/>). The client secret is kept server-side and
/// used to exchange authorization codes obtained by the mobile app.
/// </summary>
public sealed class EpicOAuthSettings
{
    public const string Section = "EpicOAuth";

    /// <summary>EOS application client ID (matches the one in the mobile app's epicOAuth.ts).</summary>
    public string ClientId { get; set; } = "";

    /// <summary>EOS application client secret (confidential, server-only).</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// The redirect URI registered in Epic's Developer Portal.
    /// Must match the redirect URL used by the mobile app during authorization.
    /// Example: <c>https://festivalscoretracker.com/api/auth/epiccallback</c>
    /// </summary>
    public string RedirectUri { get; set; } = "";

    /// <summary>
    /// The deep-link URI that the epiccallback endpoint 302-redirects to,
    /// bouncing the authorization code back into the mobile app.
    /// Example: <c>festscoretracker://auth/callback</c>
    /// </summary>
    public string AppDeepLink { get; set; } = "festscoretracker://auth/callback";

    /// <summary>
    /// Base64-encoded 256-bit key used to encrypt Epic user tokens at rest in the database.
    /// Generate with: <c>Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))</c>.
    /// Must be kept secret and separate from the JWT signing key.
    /// </summary>
    public string TokenEncryptionKey { get; set; } = "";
}
