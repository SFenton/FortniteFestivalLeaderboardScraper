using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FSTService.Auth;

/// <summary>
/// Handles all Epic Games OAuth interactions:
/// – Device Code flow (initial interactive setup)
/// – Refresh Token (session persistence across restarts)
/// – Token verification
///
/// Uses fortniteNewSwitchGameClient which supports device_code, refresh_token,
/// and client_credentials. The access token works for all Epic APIs regardless
/// of which client issued it.
///
/// Auth strategy:
///   1. First run: device_code flow → user approves in browser → access + refresh token
///   2. Persist refresh token to disk
///   3. Subsequent runs: refresh_token → new access + new refresh token → persist
///   4. If refresh fails (offline >8 h): re-run --setup
/// </summary>
public class EpicAuthService
{
    private const string DefaultClientId = "98f7e42c2e3a4f86a74eb43fbb41ed39";
    private const string DefaultClientSecret = "0a2449a2-001a-451e-afec-3e812901c4d7";

    private const string AccountBase = "https://account-public-service-prod.ol.epicgames.com";

    private readonly HttpClient _http;
    private readonly ILogger<EpicAuthService> _log;
    private readonly string _basicAuth;

    public EpicAuthService(HttpClient http, ILogger<EpicAuthService> log)
    {
        _http = http;
        _log = log;

        var clientId = Environment.GetEnvironmentVariable("EPIC_CLIENT_ID") ?? DefaultClientId;
        var clientSecret = Environment.GetEnvironmentVariable("EPIC_CLIENT_SECRET") ?? DefaultClientSecret;
        _basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
    }

    // ──────────────────────────────────────────────
    //  Device Code flow (interactive, one-time setup)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Step 1: Request a device authorization code from Epic.
    /// Returns a URL the user must visit in their browser.
    /// </summary>
    public async Task<DeviceAuthorizationResponse> StartDeviceCodeFlowAsync(CancellationToken ct = default)
    {
        // First, get a client_credentials token (anonymous, no user context)
        var ccToken = await GetClientCredentialsTokenAsync(ct);

        var req = new HttpRequestMessage(HttpMethod.Post,
            $"{AccountBase}/account/api/oauth/deviceAuthorization");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ccToken);
        req.Content = new FormUrlEncodedContent([]);

        var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Device authorization request failed ({res.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        return new DeviceAuthorizationResponse
        {
            UserCode = root.GetProperty("user_code").GetString()!,
            DeviceCode = root.GetProperty("device_code").GetString()!,
            VerificationUri = root.GetProperty("verification_uri").GetString()!,
            VerificationUriComplete = root.GetProperty("verification_uri_complete").GetString()!,
            ExpiresIn = root.GetProperty("expires_in").GetInt32(),
            Interval = root.GetProperty("interval").GetInt32(),
        };
    }

    /// <summary>
    /// Step 2: Poll until the user completes the browser login.
    /// Returns a full OAuth token once authorized.
    /// </summary>
    public async Task<EpicTokenResponse> PollDeviceCodeAsync(
        DeviceAuthorizationResponse deviceAuth,
        CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(deviceAuth.ExpiresIn);
        var interval = TimeSpan.FromSeconds(Math.Max(deviceAuth.Interval, 5));

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(interval, ct);

            // Poll with the Switch client (which supports device_code)
            var token = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "device_code",
                ["device_code"] = deviceAuth.DeviceCode,
            }, ct);

            if (token is not null)
                return token;

            // null means authorization_pending — keep polling
        }

        throw new TimeoutException("Device code login expired. The user did not complete login in time.");
    }

    // ──────────────────────────────────────────────
    //  Refresh Token
    // ──────────────────────────────────────────────

    /// <summary>
    /// Use a refresh token to obtain a fresh access token without re-authenticating.
    /// Also returns a new refresh token — persist it for the next restart.
    /// </summary>
    public Task<EpicTokenResponse?> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        return RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["token_type"] = "eg1",
        }, ct);
    }

    // ──────────────────────────────────────────────
    //  Authorization Code exchange (user-facing OAuth)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Exchange an authorization code obtained by the mobile app for an Epic
    /// access token. Uses the <em>EOS registered application</em> credentials
    /// (separate from the Switch client used for scraping).
    /// </summary>
    /// <param name="code">Authorization code from Epic's redirect.</param>
    /// <param name="clientId">EOS application client ID.</param>
    /// <param name="clientSecret">EOS application client secret.</param>
    /// <param name="redirectUri">The redirect URI that was used in the authorize request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// An <see cref="EpicTokenResponse"/> containing the user's account ID,
    /// display name, access token, and refresh token.
    /// </returns>
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    public virtual async Task<EpicTokenResponse> ExchangeAuthorizationCodeAsync(
        string code, string clientId, string clientSecret, string redirectUri,
        CancellationToken ct = default)
    {
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

        var req = new HttpRequestMessage(HttpMethod.Post, $"{AccountBase}/account/api/oauth/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        });

        var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            _log.LogWarning("Authorization code exchange failed ({Status}): {Body}", res.StatusCode, body);
            throw new InvalidOperationException($"Authorization code exchange failed ({res.StatusCode}): {body}");
        }

        return ParseTokenResponse(body);
    }

    // ──────────────────────────────────────────────
    //  Token verification
    // ──────────────────────────────────────────────

    /// <summary>
    /// Refresh a user's Epic token using arbitrary client credentials (e.g. the
    /// EOS application client, not the Switch client). Used by <see cref="TokenVault"/>
    /// to transparently refresh stored user tokens.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    public virtual async Task<EpicTokenResponse> RefreshUserTokenAsync(
        string refreshToken, string clientId, string clientSecret,
        CancellationToken ct = default)
    {
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

        var req = new HttpRequestMessage(HttpMethod.Post, $"{AccountBase}/account/api/oauth/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        });

        var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            _log.LogWarning("User token refresh failed ({Status}): {Body}", res.StatusCode, body);
            throw new InvalidOperationException($"User token refresh failed ({res.StatusCode}): {body}");
        }

        return ParseTokenResponse(body);
    }

    /// <summary>
    /// Verify that an access token is still valid.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    public async Task<bool> VerifyTokenAsync(string accessToken, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{AccountBase}/account/api/oauth/verify");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            var res = await _http.SendAsync(req, ct);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ──────────────────────────────────────────────
    //  Internals
    // ──────────────────────────────────────────────

    private async Task<string> GetClientCredentialsTokenAsync(CancellationToken ct)
    {
        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
        }, ct);

        if (token is null)
            throw new InvalidOperationException("Failed to obtain client credentials token.");

        return token.AccessToken;
    }

    /// <summary>
    /// Core token request. Returns null if the error is <c>authorization_pending</c>
    /// (used during device-code polling). Throws on other errors.
    /// </summary>
    private async Task<EpicTokenResponse?> RequestTokenAsync(
        Dictionary<string, string> formData,
        CancellationToken ct)
    {
        // Request eg1 (JWT) token format — required for leaderboard API access.
        // See: https://github.com/FNLookup/data/blob/main/festival/docs/README.md
        formData.TryAdd("token_type", "eg1");

        var req = new HttpRequestMessage(HttpMethod.Post, $"{AccountBase}/account/api/oauth/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", _basicAuth);
        req.Content = new FormUrlEncodedContent(formData);

        var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            // During device_code polling, authorization_pending is expected
            if (body.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogTrace("Device code authorization pending...");
                return null;
            }

            _log.LogWarning("Token request failed ({Status}): {Body}", res.StatusCode, body);
            throw new InvalidOperationException($"Token request failed ({res.StatusCode}): {body}");
        }

        return ParseTokenResponse(body);
    }

    private static EpicTokenResponse ParseTokenResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new EpicTokenResponse
        {
            AccessToken = root.GetProperty("access_token").GetString()!,
            ExpiresIn = root.GetProperty("expires_in").GetInt32(),
            ExpiresAt = root.TryGetProperty("expires_at", out var ea)
                ? DateTimeOffset.Parse(ea.GetString()!)
                : DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()),
            TokenType = root.GetProperty("token_type").GetString()!,
            RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "",
            RefreshExpires = root.TryGetProperty("refresh_expires", out var re) ? re.GetInt32() : 0,
            RefreshExpiresAt = root.TryGetProperty("refresh_expires_at", out var rea)
                ? DateTimeOffset.Parse(rea.GetString()!)
                : DateTimeOffset.UtcNow,
            AccountId = root.TryGetProperty("account_id", out var aid) ? aid.GetString() ?? "" : "",
            ClientId = root.TryGetProperty("client_id", out var cid) ? cid.GetString() ?? "" : "",
            DisplayName = root.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
        };
    }
}
