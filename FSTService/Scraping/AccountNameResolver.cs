using System.Net.Http.Headers;
using System.Text.Json;
using FSTService.Auth;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Resolves Epic account IDs to display names via Epic's bulk account lookup API.
///
/// After a scrape pass, all account IDs are collected and deduplicated. IDs already
/// present in MetaDatabase.AccountNames are skipped. New IDs are batched (100 per request)
/// and resolved. Results are persisted to AccountNames for future lookups.
///
/// This is best-effort: if the API is down or an account is unresolvable, the scrape
/// is not affected. Unresolved accounts are retried on the next pass.
/// </summary>
public sealed class AccountNameResolver
{
    private const string AccountBase = "https://account-public-service-prod.ol.epicgames.com";

    /// <summary>Epic's bulk lookup supports up to 100 account IDs per request.</summary>
    private const int BatchSize = 100;

    private readonly HttpClient _http;
    private readonly MetaDatabase _metaDb;
    private readonly TokenManager _tokenManager;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger<AccountNameResolver> _log;

    public AccountNameResolver(
        HttpClient http,
        MetaDatabase metaDb,
        TokenManager tokenManager,
        ScrapeProgressTracker progress,
        ILogger<AccountNameResolver> log)
    {
        _http = http;
        _metaDb = metaDb;
        _tokenManager = tokenManager;
        _progress = progress;
        _log = log;
    }

    /// <summary>
    /// Resolve display names for any account IDs in the AccountNames table that
    /// haven't been attempted yet (LastResolved IS NULL).  These are account IDs
    /// that were persisted during scraping.
    /// </summary>
    /// <param name="maxConcurrency">Max parallel API requests (default 4).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of newly resolved accounts.</returns>
    public async Task<int> ResolveNewAccountsAsync(
        int maxConcurrency = 4,
        CancellationToken ct = default)
    {
        // Query meta DB for account IDs that have never been resolved
        var newIds = _metaDb.GetUnresolvedAccountIds();

        if (newIds.Count == 0)
        {
            _log.LogInformation("Account name resolution: no unresolved accounts.");
            return 0;
        }

        _log.LogInformation(
            "Account name resolution: {New} unresolved accounts to resolve. " +
            "{Batches} API requests needed (batch size {BatchSize}).",
            newIds.Count,
            (newIds.Count + BatchSize - 1) / BatchSize, BatchSize);

        // Verify we can get an access token before starting
        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is null)
        {
            _log.LogWarning("Cannot resolve account names: no access token available.");
            return 0;
        }

        // Batch into groups of 100 and resolve with limited concurrency
        var batches = newIds
            .Chunk(BatchSize)
            .ToList();

        _progress.BeginNameResolution(batches.Count, newIds.Count);

        var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        int totalInserted = 0;
        int failedBatches = 0;
        var allResolvedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var tasks = batches.Select(async (batch, index) =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var resolved = await FetchAccountNamesAsync(batch, ct);
                if (resolved is not null)
                {
                    // Persist immediately so progress survives crashes
                    var inserted = _metaDb.InsertAccountNames(resolved);
                    Interlocked.Add(ref totalInserted, inserted);
                    lock (allResolvedIds)
                    {
                        foreach (var (id, _) in resolved)
                            allResolvedIds.Add(id);
                    }
                    _progress.ReportNameBatchComplete(resolved.Count, success: true);
                }
                else
                {
                    Interlocked.Increment(ref failedBatches);
                    _progress.ReportNameBatchComplete(0, success: false);
                }

                if ((index + 1) % 50 == 0)
                    _log.LogInformation("Account name resolution progress: {Done}/{Total} batches",
                        index + 1, batches.Count);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        // Mark unresolved IDs (in successful batches) as NULL so we don't retry every pass.
        // IDs from failed batches are NOT marked — they'll be retried next pass.
        var unresolvedFromSuccessfulBatches = newIds
            .Where(id => !allResolvedIds.Contains(id))
            .Select(id => (id, (string?)null))
            .ToList();
        if (failedBatches == 0 && unresolvedFromSuccessfulBatches.Count > 0)
        {
            _metaDb.InsertAccountNames(unresolvedFromSuccessfulBatches);
            _log.LogDebug("{Count} accounts marked as unresolvable (deleted/banned).",
                unresolvedFromSuccessfulBatches.Count);
        }

        _log.LogInformation(
            "Account name resolution complete. {Resolved} resolved, {Inserted} newly stored, " +
            "{Failed} failed batches out of {TotalBatches}.",
            allResolvedIds.Count, totalInserted, failedBatches, batches.Count);

        return totalInserted;
    }

    /// <summary>
    /// Call Epic's bulk account lookup for a batch of up to 100 account IDs.
    /// Gets a fresh access token from TokenManager on each call (cheap if cached).
    /// Retries on transient failures (429, 403, 5xx, network errors).
    /// On 403, forces a token refresh before retrying.
    /// Returns list of (AccountId, DisplayName) pairs, or null on permanent failure.
    /// </summary>
    private async Task<List<(string AccountId, string? DisplayName)>?> FetchAccountNamesAsync(
        string[] accountIds, CancellationToken ct)
    {
        const int maxRetries = 3;

        // Build URL: /account/api/public/account?accountId=X&accountId=Y&...
        var queryParams = string.Join("&", accountIds.Select(id => $"accountId={id}"));
        var url = $"{AccountBase}/account/api/public/account?{queryParams}";

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var backoff = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
                await Task.Delay(backoff, ct);
            }

            // Get a fresh token each attempt (TokenManager caches and refreshes as needed)
            var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
            if (accessToken is null)
            {
                _log.LogWarning("Cannot obtain access token for account name lookup.");
                return null;
            }

            HttpResponseMessage res;
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                res = await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException ex) when (attempt < maxRetries)
            {
                _log.LogWarning("Account name lookup HTTP error (attempt {Attempt}): {Error}",
                    attempt + 1, ex.Message);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < maxRetries)
            {
                _log.LogWarning("Account name lookup timeout (attempt {Attempt})", attempt + 1);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Account name lookup request failed.");
                return null;
            }

            if (res.IsSuccessStatusCode)
            {
                try
                {
                    await using var stream = await res.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                    var results = new List<(string, string?)>();

                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var element in doc.RootElement.EnumerateArray())
                        {
                            var id = element.TryGetProperty("id", out var idProp)
                                ? idProp.GetString()
                                : null;
                            var displayName = element.TryGetProperty("displayName", out var dnProp)
                                ? dnProp.GetString()
                                : null;

                            if (id is not null)
                                results.Add((id, displayName));
                        }
                    }

                    return results;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (attempt < maxRetries)
                    {
                        _log.LogWarning("Account name parse failure (attempt {Attempt}), retrying", attempt + 1);
                        continue;
                    }
                    _log.LogWarning(ex, "Account name lookup parse failed after {Attempts} attempts.", maxRetries + 1);
                    return null;
                }
            }

            var statusCode = (int)res.StatusCode;
            bool retryable = statusCode == 403 || statusCode == 429 || statusCode >= 500;

            if (retryable && attempt < maxRetries)
            {
                if (statusCode == 403)
                {
                    _log.LogWarning("Account name lookup got 403 (token likely expired), refreshing token and retrying (attempt {Attempt})",
                        attempt + 1);
                    res.Dispose();
                    continue;
                }
                if (statusCode == 429 && res.Headers.RetryAfter?.Delta is TimeSpan retryAfter)
                {
                    _log.LogWarning("Account name lookup rate-limited, waiting {Delay:F1}s", retryAfter.TotalSeconds);
                    res.Dispose();
                    await Task.Delay(retryAfter, ct);
                    continue;
                }
                _log.LogWarning("Account name lookup {Status} (attempt {Attempt}), retrying",
                    res.StatusCode, attempt + 1);
                res.Dispose();
                continue;
            }

            _log.LogWarning("Account name lookup failed: {Status}", res.StatusCode);
            res.Dispose();
            return null;
        }

        return null;
    }
}
