using FSTService.Auth;

namespace FSTService.Scraping;

/// <summary>
/// Scrape-scoped access token holder. Requests read the current token without
/// taking the TokenManager refresh lock; only a 401 forces a coordinated refresh.
/// </summary>
public sealed class ScrapeAccessTokenProvider
{
    private readonly TokenManager _tokenManager;
    private readonly ILogger _log;
    private string _accessToken;
    private int _refreshCount;

    public ScrapeAccessTokenProvider(TokenManager tokenManager, string accessToken, ILogger log)
    {
        _tokenManager = tokenManager;
        _accessToken = accessToken;
        _log = log;
    }

    public string CurrentAccessToken => Volatile.Read(ref _accessToken);

    public int RefreshCount => Volatile.Read(ref _refreshCount);

    public async Task<string?> RefreshAfterUnauthorizedAsync(string rejectedAccessToken, string operation, CancellationToken ct)
    {
        var current = CurrentAccessToken;
        if (!string.Equals(current, rejectedAccessToken, StringComparison.Ordinal))
            return current;

        _log.LogWarning("Access token was rejected with 401 during {Operation}. Refreshing and retrying once.", operation);

        var refreshed = await _tokenManager.ForceRefreshAccessTokenAsync(rejectedAccessToken, ct);
        if (string.IsNullOrWhiteSpace(refreshed))
            return null;

        var previous = Interlocked.Exchange(ref _accessToken, refreshed);
        if (!string.Equals(previous, refreshed, StringComparison.Ordinal))
        {
            var count = Interlocked.Increment(ref _refreshCount);
            _log.LogInformation("Scrape access token refreshed after 401 during {Operation}. RefreshCount={RefreshCount}.",
                operation, count);
        }

        return refreshed;
    }
}