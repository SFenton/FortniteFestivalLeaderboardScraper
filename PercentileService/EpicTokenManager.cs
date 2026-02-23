using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PercentileService;

/// <summary>
/// Manages Epic Games authentication using the Switch client.
/// Performs device_code flow on first run, then keeps the token alive
/// via refresh_token grants. Thread-safe.
/// </summary>
public sealed class EpicTokenManager
{
    private const string SwitchClientId = "98f7e42c2e3a4f86a74eb43fbb41ed39";
    private const string SwitchClientSecret = "0a2449a2-001a-451e-afec-3e812901c4d7";
    private static readonly string BasicAuth =
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{SwitchClientId}:{SwitchClientSecret}"));

    private const string AccountBase = "https://account-public-service-prod.ol.epicgames.com";

    private readonly HttpClient _http;
    private readonly ILogger<EpicTokenManager> _log;
    private readonly PercentileOptions _options;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _accessToken;
    private string? _refreshToken;
    private string? _accountId;
    private string? _displayName;
    private DateTimeOffset _tokenExpiresAt;

    public string? AccessToken => _accessToken;
    public string? AccountId => _accountId;
    public string? DisplayName => _displayName;
    public bool IsAuthenticated => _accessToken is not null && _tokenExpiresAt > DateTimeOffset.UtcNow;

    public EpicTokenManager(HttpClient http, ILogger<EpicTokenManager> log, IOptions<PercentileOptions> options)
    {
        _http = http;
        _log = log;
        _options = options.Value;
    }

    /// <summary>
    /// Ensure we have a valid token. Loads from disk, refreshes if needed,
    /// or starts device_code flow if no credentials exist.
    /// </summary>
    public async Task EnsureAuthenticatedAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (IsAuthenticated) return;

            // Try loading from disk
            var creds = await LoadCredentialsAsync(ct);
            if (creds is not null)
            {
                _log.LogInformation("Loaded stored credentials for {DisplayName} ({AccountId})",
                    creds.DisplayName, creds.AccountId);

                var refreshed = await RefreshTokenAsync(creds.RefreshToken, ct);
                if (refreshed)
                {
                    _log.LogInformation("Token refreshed successfully for {DisplayName}", _displayName);
                    return;
                }

                _log.LogWarning("Stored refresh token is invalid. Starting device_code flow...");
            }

            // No valid credentials — run device_code flow
            await RunDeviceCodeFlowAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Refresh the token proactively. Called by TokenRefreshWorker on a schedule.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_refreshToken is null)
            {
                _log.LogWarning("No refresh token available. Cannot refresh.");
                return;
            }

            var success = await RefreshTokenAsync(_refreshToken, ct);
            if (!success)
            {
                _log.LogError("Token refresh failed. Will need re-authentication via device_code.");
                _accessToken = null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // ─── Device Code Flow ───────────────────────────────────

    /// <summary>
    /// Start the device code flow and return the verification details.
    /// The caller should direct the user to the verification URL, then call
    /// <see cref="PollDeviceCodeAsync"/> to complete authentication.
    /// </summary>
    public async Task<DeviceCodeInfo> StartDeviceCodeFlowAsync(CancellationToken ct = default)
    {
        // Step 1: client_credentials token
        _log.LogInformation("Getting client_credentials token...");
        var ccReq = new HttpRequestMessage(HttpMethod.Post, $"{AccountBase}/account/api/oauth/token");
        ccReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth);
        ccReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
        });

        var ccResp = await _http.SendAsync(ccReq, ct);
        var ccBody = await ccResp.Content.ReadAsStringAsync(ct);
        if (!ccResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"client_credentials failed: {ccResp.StatusCode} {ccBody}");

        var ccToken = JsonDocument.Parse(ccBody).RootElement
            .GetProperty("access_token").GetString()!;

        // Step 2: Request device authorization
        _log.LogInformation("Starting device_code flow...");
        var dcReq = new HttpRequestMessage(HttpMethod.Post,
            $"{AccountBase}/account/api/oauth/deviceAuthorization");
        dcReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ccToken);
        dcReq.Content = new FormUrlEncodedContent([]);

        var dcResp = await _http.SendAsync(dcReq, ct);
        var dcBody = await dcResp.Content.ReadAsStringAsync(ct);
        if (!dcResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"deviceAuthorization failed: {dcResp.StatusCode} {dcBody}");

        var dcDoc = JsonDocument.Parse(dcBody);
        var info = new DeviceCodeInfo
        {
            DeviceCode = dcDoc.RootElement.GetProperty("device_code").GetString()!,
            UserCode = dcDoc.RootElement.GetProperty("user_code").GetString()!,
            VerificationUri = dcDoc.RootElement.TryGetProperty("verification_uri", out var vu)
                ? vu.GetString()! : "",
            VerificationUriComplete = dcDoc.RootElement.GetProperty("verification_uri_complete").GetString()!,
            ExpiresIn = dcDoc.RootElement.GetProperty("expires_in").GetInt32(),
            Interval = dcDoc.RootElement.GetProperty("interval").GetInt32(),
        };

        _log.LogWarning(
            "ACTION REQUIRED: Go to {VerifyUri} and enter code {UserCode}. Expires in {ExpiresIn}s.",
            info.VerificationUriComplete, info.UserCode, info.ExpiresIn);

        return info;
    }

    /// <summary>
    /// Poll until the user completes the device code login, then apply and persist the token.
    /// </summary>
    public async Task PollDeviceCodeAsync(DeviceCodeInfo deviceCode, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(deviceCode.ExpiresIn);
        var pollInterval = TimeSpan.FromSeconds(Math.Max(deviceCode.Interval, 5));

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(pollInterval, ct);

            var pollReq = new HttpRequestMessage(HttpMethod.Post,
                $"{AccountBase}/account/api/oauth/token");
            pollReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth);
            pollReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "device_code",
                ["device_code"] = deviceCode.DeviceCode,
            });

            var pollResp = await _http.SendAsync(pollReq, ct);
            var pollBody = await pollResp.Content.ReadAsStringAsync(ct);

            if (pollResp.IsSuccessStatusCode)
            {
                ApplyTokenResponse(pollBody);
                await SaveCredentialsAsync(ct);
                _log.LogInformation("Authorized as {DisplayName} ({AccountId})",
                    _displayName, _accountId);
                return;
            }

            if (pollBody.Contains("authorization_pending")) continue;

            throw new InvalidOperationException(
                $"device_code poll error: {pollResp.StatusCode} {pollBody}");
        }

        throw new TimeoutException("Device code authorization timed out.");
    }

    private async Task RunDeviceCodeFlowAsync(CancellationToken ct)
    {
        var info = await StartDeviceCodeFlowAsync(ct);
        await PollDeviceCodeAsync(info, ct);
    }

    // ─── Token Refresh ──────────────────────────────────────

    private async Task<bool> RefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{AccountBase}/account/api/oauth/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        });

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _log.LogWarning("Token refresh failed: {Status} {Body}", resp.StatusCode, body);
            return false;
        }

        var body2 = await resp.Content.ReadAsStringAsync(ct);
        ApplyTokenResponse(body2);
        await SaveCredentialsAsync(ct);
        return true;
    }

    internal void ApplyTokenResponse(string json)
    {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _accessToken = root.GetProperty("access_token").GetString()!;
        _refreshToken = root.GetProperty("refresh_token").GetString()!;
        _accountId = root.GetProperty("account_id").GetString()!;
        _displayName = root.TryGetProperty("displayName", out var dn)
            ? dn.GetString() : _displayName;

        var expiresIn = root.GetProperty("expires_in").GetInt32();
        _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
    }

    // ─── Credential Persistence ─────────────────────────────

    private async Task<StoredPercentileCredentials?> LoadCredentialsAsync(CancellationToken ct)
    {
        if (!File.Exists(_options.TokenPath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(_options.TokenPath, ct);
            var creds = JsonSerializer.Deserialize<StoredPercentileCredentials>(json);
            if (creds is null || string.IsNullOrEmpty(creds.RefreshToken))
                return null;
            return creds;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load credentials from {Path}", _options.TokenPath);
            return null;
        }
    }

    private async Task SaveCredentialsAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_options.TokenPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var creds = new StoredPercentileCredentials
        {
            AccountId = _accountId!,
            DisplayName = _displayName ?? "",
            RefreshToken = _refreshToken!,
            SavedAt = DateTimeOffset.UtcNow.ToString("o"),
        };

        var json = JsonSerializer.Serialize(creds, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_options.TokenPath, json, ct);
    }
}

/// <summary>
/// Contains the details returned by Epic's device authorization endpoint.
/// Used to direct the user to the verification URL and to poll for completion.
/// </summary>
public sealed class DeviceCodeInfo
{
    public string DeviceCode { get; set; } = "";
    public string UserCode { get; set; } = "";
    public string VerificationUri { get; set; } = "";
    public string VerificationUriComplete { get; set; } = "";
    public int ExpiresIn { get; set; }
    public int Interval { get; set; }
}

internal sealed class StoredPercentileCredentials
{
    public string AccountId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string SavedAt { get; set; } = "";
}
