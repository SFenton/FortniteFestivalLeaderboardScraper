using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text.Json;
using FortniteFestival.Core;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Fetches *all* entries from a Fortnite Festival V1 leaderboard by paging
/// through every page (page 0 … totalPages-1).
///
/// Unlike the Core FestivalService (which fetches only the authenticated
/// user's personal entry via teamAccountIds), this scrapes the full global
/// leaderboard for a given song + instrument combination.
///
/// Both instrument-level and page-level requests are parallelised.
/// </summary>
public class GlobalLeaderboardScraper : ILeaderboardQuerier
{
    private const string EventsBase = "https://events-public-service-live.ol.epicgames.com";

    /// <summary>Default max concurrent HTTP requests across ALL instruments for one song.</summary>
    private const int DefaultMaxConcurrency = 16;

    /// <summary>
    /// The 6 instrument API keys used in the leaderboard URL.
    /// </summary>
    public static readonly IReadOnlyList<string> AllInstruments = new[]
    {
        "Solo_Guitar",
        "Solo_Bass",
        "Solo_Vocals",
        "Solo_Drums",
        "Solo_PeripheralGuitar",
        "Solo_PeripheralBass",
    };

    private readonly HttpClient _http;
    private readonly ILogger<GlobalLeaderboardScraper> _log;
    private readonly ScrapeProgressTracker _progress;
    private readonly ResilientHttpExecutor _executor;
    private readonly int _maxLookupRetries;

    /// <summary>Exposes the underlying HTTP executor for diagnostics (CDN wire counters).</summary>
    public ResilientHttpExecutor Executor => _executor;

    public GlobalLeaderboardScraper(
        HttpClient http,
        ScrapeProgressTracker progress,
        ILogger<GlobalLeaderboardScraper> log,
        int maxLookupRetries = ResilientHttpExecutor.DefaultMaxRetries)
    {
        _http = http;
        _progress = progress;
        _log = log;
        _maxLookupRetries = maxLookupRetries;
        _executor = new ResilientHttpExecutor(http, log);
    }

    /// <summary>
    /// Reset CDN cooldown state on the underlying executor. Call at the start of
    /// each scrape pass to prevent stale state from a previous CDN block.
    /// </summary>
    public void ResetCdnState() => _executor.ResetCdnState();

    // ─── Targeted account lookup ─────────────────────

    /// <summary>
    /// Fetch a specific player's leaderboard entry for one song + instrument
    /// using the V2 POST API with <c>teams</c> body. Returns the player's entry
    /// or null if the player has no score. Throws on API errors.
    /// </summary>
    public async Task<LeaderboardEntry?> LookupAccountAsync(
        string songId,
        string instrument,
        string targetAccountId,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter = null,
        CancellationToken ct = default)
    {
        return await LookupAccountInWindowAsync(
            songId, instrument, "alltime", targetAccountId,
            accessToken, callerAccountId, limiter, ct);
    }

    /// <summary>
    /// Fetch a player's entry plus up to ±<paramref name="neighborRadius"/> rank neighbors
    /// on a single song/instrument. Uses V2 POST (1 call) to get the target's rank,
    /// then V1 GET with <c>rank=</c> (1 call) to fetch the page containing that rank.
    /// Returns the target entry (Source=backfill) and neighbor entries (Source=neighbor).
    /// </summary>
    /// <returns>
    /// A tuple of (target entry or null, list of neighbor entries). If the target has
    /// no score on this song/instrument, returns (null, empty list).
    /// </returns>
    public async Task<(LeaderboardEntry? Target, List<LeaderboardEntry> Neighbors)> LookupAccountWithNeighborsAsync(
        string songId,
        string instrument,
        string targetAccountId,
        string accessToken,
        string callerAccountId,
        int neighborRadius = 50,
        AdaptiveConcurrencyLimiter? limiter = null,
        CancellationToken ct = default)
    {
        // Step 1: V2 lookup to get the target's rank + score
        var target = await LookupAccountAsync(songId, instrument, targetAccountId, accessToken, callerAccountId, limiter, ct);
        if (target is null || target.Rank <= 0)
            return (target, []);

        target.Source = "backfill";
        target.ApiRank = target.Rank;

        // Step 2: V1 GET to fetch the page containing the target's rank
        List<LeaderboardEntry> neighbors;
        try
        {
            neighbors = await FetchNeighborhoodPageAsync(
                songId, instrument, target.Rank, targetAccountId, accessToken, callerAccountId,
                neighborRadius, limiter, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Neighborhood V1 fetch failed for {Account}/{Song}/{Instrument} rank {Rank}. Returning target only.",
                targetAccountId, songId, instrument, target.Rank);
            return (target, []);
        }

        return (target, neighbors);
    }

    /// <summary>
    /// Fetch a single V1 leaderboard page containing the given rank and extract
    /// up to ±<paramref name="neighborRadius"/> entries around that rank.
    /// Sets Source=neighbor and ApiRank on each returned entry.
    /// </summary>
    internal async Task<List<LeaderboardEntry>> FetchNeighborhoodPageAsync(
        string songId,
        string instrument,
        int targetRank,
        string targetAccountId,
        string accessToken,
        string callerAccountId,
        int neighborRadius,
        AdaptiveConcurrencyLimiter? limiter,
        CancellationToken ct)
    {
        var url = $"{EventsBase}/api/v1/leaderboards/FNFestival/alltime_{songId}_{instrument}" +
                  $"/alltime/{callerAccountId}?page=0&rank={targetRank}&appId=Fortnite&showLiveSessions=false";

        var label = $"{songId}/{instrument}/neighborhood(rank={targetRank})";

        HttpRequestMessage CreateRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return req;
        }

        using var res = await _executor.SendAsync(CreateRequest, limiter, label, _maxLookupRetries, ct);

        if (!res.IsSuccessStatusCode)
        {
            _log.LogDebug("Neighborhood fetch returned {Status} for {Label}", res.StatusCode, label);
            return [];
        }

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        var parsed = await ParsePageAsync(stream, ct);
        if (parsed is null || parsed.Entries.Count == 0)
            return [];

        // Find the target's index on the page to determine the ± window
        int targetIdx = -1;
        for (int i = 0; i < parsed.Entries.Count; i++)
        {
            if (parsed.Entries[i].AccountId.Equals(targetAccountId, StringComparison.OrdinalIgnoreCase))
            {
                targetIdx = i;
                break;
            }
        }

        // If target not found on page (shouldn't happen), use rank-based position
        if (targetIdx < 0)
        {
            // Estimate index from rank offset within the page
            int pageFirstRank = parsed.Entries[0].Rank;
            targetIdx = Math.Clamp(targetRank - pageFirstRank, 0, parsed.Entries.Count - 1);
        }

        // Collect up to neighborRadius above and below
        int startIdx = Math.Max(0, targetIdx - neighborRadius);
        int endIdx = Math.Min(parsed.Entries.Count - 1, targetIdx + neighborRadius);

        var neighbors = new List<LeaderboardEntry>();
        long totalEntries = (long)parsed.TotalPages * 100;

        for (int i = startIdx; i <= endIdx; i++)
        {
            var entry = parsed.Entries[i];
            // Skip the target account itself — it's returned separately
            if (entry.AccountId.Equals(targetAccountId, StringComparison.OrdinalIgnoreCase))
                continue;

            entry.Source = "neighbor";
            entry.ApiRank = entry.Rank;
            // V1 returns percentile = -1 beyond capped range; compute from rank/totalEntries
            if (entry.Percentile < 0 && entry.Rank > 0 && totalEntries > 0)
                entry.Percentile = (double)entry.Rank / totalEntries;

            neighbors.Add(entry);
        }

        _log.LogDebug(
            "Neighborhood for {Account}/{Song}/{Instrument}: rank {Rank}, page had {PageEntries} entries, " +
            "captured {NeighborCount} neighbors (idx {Start}-{End} around target idx {TargetIdx})",
            targetAccountId, songId, instrument, targetRank, parsed.Entries.Count,
            neighbors.Count, startIdx, endIdx, targetIdx);

        return neighbors;
    }

    /// <summary>
    /// Fetch alltime entries for multiple accounts on one song/instrument in a single
    /// batched V2 POST. The <c>teams</c> body includes the caller plus all targets.
    /// Pages through the response (25 entries per page) until all targets are collected
    /// or no more results. Filters out the caller's own entry from results.
    /// </summary>
    public async Task<List<LeaderboardEntry>> LookupMultipleAccountsAsync(
        string songId,
        string instrument,
        IReadOnlyList<string> targetAccountIds,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter = null,
        CancellationToken ct = default)
    {
        if (targetAccountIds.Count == 0) return [];

        var eventId = $"alltime_{songId}_{instrument}";
        var baseUrl = $"{EventsBase}/api/v2/games/FNFestival/leaderboards/{eventId}/alltime/scores" +
                      $"?accountId={callerAccountId}";

        // Build teams body: [["callerAccountId"], ["target1"], ["target2"], ...]
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"teams\":[[\"").Append(callerAccountId).Append("\"]");
        foreach (var targetId in targetAccountIds)
            sb.Append(",[\"").Append(targetId).Append("\"]");
        sb.Append("]}");
        var teamsJson = sb.ToString();
        var label = $"{songId}/{instrument}/batch({targetAccountIds.Count})";

        // Collect target IDs for fast lookup
        var targetSet = new HashSet<string>(targetAccountIds, StringComparer.OrdinalIgnoreCase);
        var results = new List<LeaderboardEntry>();

        // Page through response (25 entries per page)
        const int pageSize = 25;
        for (int fromIndex = 0; ; fromIndex += pageSize)
        {
            ct.ThrowIfCancellationRequested();

            // First page uses the rate token consumed on DOP slot acquisition.
            // Subsequent pages must acquire their own token to stay within RPS.
            if (fromIndex > 0)
                await (limiter?.AcquireRateTokenAsync(ct) ?? Task.CompletedTask);

            var url = $"{baseUrl}&fromIndex={fromIndex}";

            HttpRequestMessage CreateRequest()
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                req.Content = new StringContent(teamsJson, System.Text.Encoding.UTF8, "application/json");
                return req;
            }

            HttpResponseMessage res;
            try
            {
                // Pages 2+ pass limiter:null so AIMD doesn't count each page as
                // a separate success/failure (rate token already acquired above).
                var pageLimiter = fromIndex == 0 ? limiter : null;
                res = await _executor.SendAsync(CreateRequest, pageLimiter, label, _maxLookupRetries, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not CdnBlockedException)
            {
                _log.LogWarning(ex, "Batch lookup failed for {Label} at fromIndex={FromIndex}.",
                    label, fromIndex);
                break;
            }

            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                if (body.Contains("no_score_found", StringComparison.Ordinal))
                {
                    res.Dispose();
                    break; // No more results
                }

                _log.LogWarning("Batch lookup returned {Status} for {Label}: {Body}",
                    res.StatusCode, label, body);
                res.Dispose();
                break;
            }

            List<LeaderboardEntry>? pageEntries;
            await using (var stream = await res.Content.ReadAsStreamAsync(ct))
            {
                pageEntries = await ParseV2ResponseAsync(stream, ct);
            }
            res.Dispose();

            if (pageEntries is null || pageEntries.Count == 0)
                break; // No more results

            // Filter to only requested targets (exclude caller's own entry)
            foreach (var entry in pageEntries)
            {
                if (targetSet.Contains(entry.AccountId))
                    results.Add(entry);
            }

            // If we got fewer than a full page, there are no more results
            if (pageEntries.Count < pageSize)
                break;

            // If we've found all targets, no need to page further
            if (results.Count >= targetAccountIds.Count)
                break;
        }

        return results;
    }

    /// <summary>
    /// Fetch all sessions for multiple accounts on one song/instrument in a seasonal window.
    /// Returns a flat list of <see cref="SessionHistoryEntry"/> across all accounts.
    /// Pages through the V2 response (25 entries per page).
    /// </summary>
    public async Task<List<SessionHistoryEntry>> LookupMultipleAccountSessionsAsync(
        string songId,
        string instrument,
        string seasonPrefix,
        IReadOnlyList<string> targetAccountIds,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter = null,
        CancellationToken ct = default)
    {
        if (targetAccountIds.Count == 0) return [];

        var eventId = $"{seasonPrefix}_{songId}";
        var windowId = $"{songId}_{instrument}";
        var baseUrl = $"{EventsBase}/api/v2/games/FNFestival/leaderboards/{eventId}/{windowId}/scores" +
                      $"?accountId={callerAccountId}";

        var sb = new System.Text.StringBuilder();
        sb.Append("{\"teams\":[[\"").Append(callerAccountId).Append("\"]");
        foreach (var targetId in targetAccountIds)
            sb.Append(",[\"").Append(targetId).Append("\"]");
        sb.Append("]}");
        var teamsJson = sb.ToString();
        var label = $"{songId}/{instrument}/{seasonPrefix}/sessions({targetAccountIds.Count})";

        var targetSet = new HashSet<string>(targetAccountIds, StringComparer.OrdinalIgnoreCase);
        var allSessions = new List<SessionHistoryEntry>();

        const int pageSize = 25;
        for (int fromIndex = 0; ; fromIndex += pageSize)
        {
            ct.ThrowIfCancellationRequested();

            // First page uses the rate token consumed on DOP slot acquisition.
            // Subsequent pages must acquire their own token to stay within RPS.
            if (fromIndex > 0)
                await (limiter?.AcquireRateTokenAsync(ct) ?? Task.CompletedTask);

            var url = $"{baseUrl}&fromIndex={fromIndex}";

            HttpRequestMessage CreateRequest()
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                req.Content = new StringContent(teamsJson, System.Text.Encoding.UTF8, "application/json");
                return req;
            }

            HttpResponseMessage res;
            try
            {
                // Pages 2+ pass limiter:null so AIMD doesn't count each page as
                // a separate success/failure (rate token already acquired above).
                var pageLimiter = fromIndex == 0 ? limiter : null;
                res = await _executor.SendAsync(CreateRequest, pageLimiter, label, _maxLookupRetries, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not CdnBlockedException)
            {
                _log.LogWarning(ex, "Batch session lookup failed for {Label} at fromIndex={FromIndex}.",
                    label, fromIndex);
                break;
            }

            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                var statusCode = res.StatusCode;
                res.Dispose();
                if (body.Contains("no_score_found", StringComparison.Ordinal))
                    break;
                _log.LogWarning("Batch session lookup returned {Status} for {Label}: {Body}",
                    statusCode, label, body);

                // BadRequest on the first page means the leaderboard doesn't exist
                // for this season (song not charted). Throw so callers can detect
                // and skip earlier seasons for other instruments.
                if ((int)statusCode == 400 && fromIndex == 0)
                    throw new HttpRequestException(
                        $"Seasonal leaderboard not found: {label} returned {(int)statusCode}",
                        null, statusCode);

                break;
            }

            int entriesOnPage = 0;
            await using (var stream = await res.Content.ReadAsStreamAsync(ct))
            {
                // Parse all entries on this page, extracting sessions for target accounts
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Array)
                    break;

                foreach (var e in root.EnumerateArray())
                {
                    entriesOnPage++;
                    string accountId = "";
                    if (e.TryGetProperty("teamId", out var tid) && tid.ValueKind == System.Text.Json.JsonValueKind.String)
                        accountId = tid.GetString()!;

                    if (!targetSet.Contains(accountId))
                        continue;

                    int rank = e.TryGetProperty("rank", out var rk) && rk.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? rk.GetInt32() : 0;
                    double percentile = e.TryGetProperty("percentile", out var pct) && pct.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? pct.GetDouble() : 0;

                    var sessions = ParseAllSessionsFromEntry(e, accountId, rank, percentile);
                    allSessions.AddRange(sessions);
                }
            }
            res.Dispose();

            if (entriesOnPage < pageSize)
                break;
        }

        return allSessions;
    }

    /// <summary>
    /// Fetch a specific player's leaderboard entry for one song + instrument
    /// in a specific seasonal window. Returns null if the player has no entry
    /// in that season.
    /// </summary>
    public async Task<LeaderboardEntry?> LookupSeasonalAsync(
        string songId,
        string instrument,
        string windowId,
        string targetAccountId,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter = null,
        CancellationToken ct = default)
    {
        return await LookupAccountInWindowAsync(
            songId, instrument, windowId, targetAccountId,
            accessToken, callerAccountId, limiter, ct);
    }

    /// <summary>
    /// Fetch ALL sessions from a player's <c>sessionHistory</c> for one song + instrument
    /// in a specific seasonal window. Returns every individual run/play, not just the best.
    /// Returns null if the player has no entry in that season.
    /// </summary>
    public async Task<List<SessionHistoryEntry>?> LookupSeasonalSessionsAsync(
        string songId,
        string instrument,
        string windowId,
        string targetAccountId,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter = null,
        CancellationToken ct = default)
    {
        return await LookupAllSessionsInWindowAsync(
            songId, instrument, windowId, targetAccountId,
            accessToken, callerAccountId, limiter, ct);
    }

    /// <summary>
    /// Internal helper: fetch a player's entry in a specific window (alltime or seasonal)
    /// using the V2 POST API with <c>teams</c> body to jump directly to the player's entry.
    /// Uses <see cref="ResilientHttpExecutor"/> for automatic retry on transient failures.
    /// Returns null only when the player genuinely has no entry.
    /// </summary>
    private async Task<LeaderboardEntry?> LookupAccountInWindowAsync(
        string songId,
        string instrument,
        string windowId,
        string targetAccountId,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter,
        CancellationToken ct)
    {
        using var res = await SendV2LookupWithRetryAsync(
            songId, instrument, windowId, targetAccountId,
            accessToken, callerAccountId, limiter, ct);

        if (res is null) return null; // no_score_found

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        var entries = await ParseV2ResponseAsync(stream, ct);
        if (entries is null || entries.Count == 0) return null;

        // Find the specific account in the returned entries
        return entries.FirstOrDefault(e =>
            e.AccountId.Equals(targetAccountId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Internal helper: fetch ALL sessions from a player's <c>sessionHistory</c>
    /// in a specific window. Same HTTP call as <see cref="LookupAccountInWindowAsync"/>
    /// but returns every session, not just the best.
    /// Uses <see cref="ResilientHttpExecutor"/> for automatic retry on transient failures.
    /// </summary>
    private async Task<List<SessionHistoryEntry>?> LookupAllSessionsInWindowAsync(
        string songId,
        string instrument,
        string windowId,
        string targetAccountId,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter,
        CancellationToken ct)
    {
        using var res = await SendV2LookupWithRetryAsync(
            songId, instrument, windowId, targetAccountId,
            accessToken, callerAccountId, limiter, ct);

        if (res is null) return null; // no_score_found

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        return await ParseV2AllSessionsResponseAsync(stream, targetAccountId, ct);
    }

    /// <summary>
    /// Shared V2 lookup: builds URL, sends POST with retry, handles no_score_found.
    /// Returns the <see cref="HttpResponseMessage"/> on success, null on no_score_found,
    /// or throws on non-retryable errors after retries are exhausted.
    /// </summary>
    private async Task<HttpResponseMessage?> SendV2LookupWithRetryAsync(
        string songId,
        string instrument,
        string windowId,
        string targetAccountId,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter,
        CancellationToken ct)
    {
        // V2 URL format: /api/v2/games/{gameId}/leaderboards/{eventId}/{windowId}/scores
        var eventId = windowId == "alltime"
            ? $"alltime_{songId}_{instrument}"        // alltime event format
            : $"{windowId}_{songId}";                  // seasonal: "season_N_{su}"
        var v2WindowId = windowId == "alltime"
            ? "alltime"                                // alltime window is just "alltime"
            : $"{songId}_{instrument}";                // seasonal: "{su}_{type}"

        var url = $"{EventsBase}/api/v2/games/FNFestival/leaderboards/{eventId}/{v2WindowId}/scores" +
                  $"?accountId={callerAccountId}&fromIndex=0";

        var teamsJson = $"{{\"teams\":[[\"{callerAccountId}\"],[\"{targetAccountId}\"]]}}";
        var label = $"{songId}/{instrument}/{windowId}";

        HttpRequestMessage CreateRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Content = new StringContent(teamsJson, System.Text.Encoding.UTF8, "application/json");
            return req;
        }

        var res = await _executor.SendAsync(CreateRequest, limiter, label, _maxLookupRetries, ct);

        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);

            // "no_score_found" means the leaderboard exists but the player has
            // no entry — that's a valid "not found" result, not an error.
            if (body.Contains("no_score_found", StringComparison.Ordinal))
            {
                _log.LogDebug("No score for {Account} on {Song}/{Instrument} (no_score_found).",
                    targetAccountId, songId, instrument);
                res.Dispose();
                return null;
            }

            _log.LogWarning("Account lookup failed for {Account} on {Song}/{Instrument}: {Status} {Body}",
                targetAccountId, songId, instrument, res.StatusCode, body);
            res.Dispose();
            throw new HttpRequestException(
                $"Account lookup returned {res.StatusCode} for {songId}/{instrument}",
                null, res.StatusCode);
        }

        return res;
    }

    /// <summary>
    /// Parse a V2 response and extract ALL individual sessions from the target account's
    /// <c>sessionHistory</c> array. Each session becomes a separate <see cref="SessionHistoryEntry"/>.
    /// </summary>
    internal static async Task<List<SessionHistoryEntry>?> ParseV2AllSessionsResponseAsync(
        Stream stream, string targetAccountId, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var e in root.EnumerateArray())
            {
                // Find the target account
                string accountId = "";
                if (e.TryGetProperty("teamId", out var tid) && tid.ValueKind == JsonValueKind.String)
                    accountId = tid.GetString()!;
                else if (e.TryGetProperty("team_id", out var tid2) && tid2.ValueKind == JsonValueKind.String)
                    accountId = tid2.GetString()!;

                if (!accountId.Equals(targetAccountId, StringComparison.OrdinalIgnoreCase))
                    continue;

                int rank = 0;
                double percentile = 0;
                if (e.TryGetProperty("rank", out var rk) && rk.ValueKind == JsonValueKind.Number)
                    rank = rk.GetInt32();
                if (e.TryGetProperty("percentile", out var pct) && pct.ValueKind == JsonValueKind.Number)
                    percentile = pct.GetDouble();

                return ParseAllSessionsFromEntry(e, accountId, rank, percentile);
            }

            return null; // target account not found
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extract every session from a single entry's <c>sessionHistory</c> array.
    /// </summary>
    internal static List<SessionHistoryEntry> ParseAllSessionsFromEntry(
        JsonElement entry, string accountId, int rank, double percentile)
    {
        var sessions = new List<SessionHistoryEntry>();

        if (!entry.TryGetProperty("sessionHistory", out var sh) || sh.ValueKind != JsonValueKind.Array)
            return sessions;

        foreach (var session in sh.EnumerateArray())
        {
            if (!session.TryGetProperty("trackedStats", out var ts) || ts.ValueKind != JsonValueKind.Object)
                continue;

            int score = ts.TryGetProperty("SCORE", out var sc) && sc.ValueKind == JsonValueKind.Number
                ? sc.GetInt32() : 0;
            int accuracy = ts.TryGetProperty("ACCURACY", out var ac) && ac.ValueKind == JsonValueKind.Number
                ? ac.GetInt32() : 0;
            int fullCombo = ts.TryGetProperty("FULL_COMBO", out var fc) && fc.ValueKind == JsonValueKind.Number
                ? fc.GetInt32() : 0;
            int stars = ts.TryGetProperty("STARS_EARNED", out var se) && se.ValueKind == JsonValueKind.Number
                ? se.GetInt32() : 0;
            int season = ts.TryGetProperty("SEASON", out var sn) && sn.ValueKind == JsonValueKind.Number
                ? sn.GetInt32() : 0;
            int difficulty = ts.TryGetProperty("DIFFICULTY", out var df) && df.ValueKind == JsonValueKind.Number
                ? df.GetInt32() : 0;
            string? endTime = session.TryGetProperty("endTime", out var et) && et.ValueKind == JsonValueKind.String
                ? et.GetString() : null;

            sessions.Add(new SessionHistoryEntry
            {
                AccountId = accountId,
                Rank = rank,
                Percentile = percentile,
                Score = score,
                Accuracy = accuracy,
                IsFullCombo = fullCombo == 1,
                Stars = stars,
                Season = season,
                Difficulty = difficulty,
                EndTime = endTime,
            });
        }

        return sessions;
    }

    // ─── Single page fetch (with retry) ─────────────

    /// <summary>Max retries for transient HTTP failures (429, 5xx, timeouts).</summary>
    private const int MaxRetries = 3;

    /// <summary>Consecutive 403 Forbidden responses before cancelling remaining pages.</summary>
    private const int ForbiddenThreshold = 3;

    /// <summary>Outcome of a single page fetch.</summary>
    internal enum FetchStatus { Success, Forbidden, OtherFailure }

    /// <summary>
    /// Fetch and parse a single leaderboard page with automatic retry on
    /// transient failures (429, 5xx, network errors, timeouts) and CDN blocks.
    /// Delegates all retry/CDN logic to <see cref="ResilientHttpExecutor"/>.
    /// Thread-safe (no shared mutable state).
    /// </summary>
    internal async Task<(ParsedPage? Page, int BodyLength, FetchStatus Status)> FetchPageAsync(
        string songId,
        string instrument,
        int page,
        string accessToken,
        string accountId,
        AdaptiveConcurrencyLimiter? limiter,
        CancellationToken ct)
    {
        var url = $"{EventsBase}/api/v1/leaderboards/FNFestival/alltime_{songId}_{instrument}" +
                  $"/alltime/{accountId}?page={page}&rank=0&appId=Fortnite&showLiveSessions=false";

        var label = $"{songId}/{instrument}/page({page})";

        HttpRequestMessage CreateRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return req;
        }

        // Outer loop: allows one retry on JSON 403 (Epic access boundary can be transient)
        for (int fetchAttempt = 0; fetchAttempt <= 1; fetchAttempt++)
        {
            if (fetchAttempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(5), ct);

            HttpResponseMessage res;
            try
            {
                res = await _executor.SendAsync(CreateRequest, limiter, label, MaxRetries, ct);
            }
            catch (HttpRequestException ex)
            {
                _log.LogWarning("HTTP error for {Song}/{Instrument} page {Page}: {Error}",
                    songId, instrument, page, ex.Message);
                _progress.ReportRetry();
                return (null, 0, FetchStatus.OtherFailure);
            }

            using (res)
            {
                if (res.IsSuccessStatusCode)
                {
                    await using var stream = await res.Content.ReadAsStreamAsync(ct);
                    var contentLength = (int)(res.Content.Headers.ContentLength ?? 0);
                    var parsed = await ParsePageAsync(stream, ct);

                    if (parsed is not null)
                        return (parsed, contentLength > 0 ? contentLength : parsed.EstimatedBytes, FetchStatus.Success);

                    _log.LogWarning("Failed to parse {Song}/{Instrument} page {Page}: ContentLength={CL}, ContentType={CT}",
                        songId, instrument, page, contentLength, res.Content.Headers.ContentType);
                    return (null, 0, FetchStatus.OtherFailure);
                }

                var statusCode = (int)res.StatusCode;

                if (statusCode == 403)
                {
                    // JSON 403 from executor (CDN 403s are already retried infinitely).
                    // Give it one more try with 5 s backoff before returning Forbidden.
                    if (fetchAttempt == 0)
                    {
                        _progress.ReportRetry();
                        continue;
                    }

                    return (null, 0, FetchStatus.Forbidden);
                }

                // Non-retryable / exhausted retries
                string errorBody = "";
                try
                {
                    var body = await res.Content.ReadAsStringAsync(ct);
                    errorBody = body.Length > 200 ? body[..200] : body;
                }
                catch { }

                _log.LogWarning("Leaderboard request failed for {Song}/{Instrument} page {Page}: {StatusCode} {Body}",
                    songId, instrument, page, statusCode, errorBody);
                return (null, 0, FetchStatus.OtherFailure);
            }
        }

        return (null, 0, FetchStatus.Forbidden);
    }

    // ─── Single instrument (parallel pages) ─────────

    /// <summary>
    /// Scrape all pages of a single leaderboard (one song + one instrument).
    /// Page 0 is fetched first to discover totalPages, then pages 1…N are
    /// fetched concurrently, throttled by <paramref name="limiter"/>.
    /// </summary>
    public async Task<GlobalLeaderboardResult> ScrapeLeaderboardAsync(
        string songId,
        string instrument,
        string accessToken,
        string accountId,
        AdaptiveConcurrencyLimiter? limiter = null,
        CancellationToken ct = default,
        string? label = null,
        int maxPages = 0,
        int? choptMaxScore = null,
        double overThresholdMultiplier = 1.05,
        int overThresholdExtraPages = 100,
        int validEntryTarget = 0,
        bool deferDeepScrape = false,
        double validCutoffMultiplier = 0.95)
    {
        // ── Page 0: discover totalPages ──
        bool page0Acquired = false;
        if (limiter is not null) { await limiter.WaitAsync(ct); page0Acquired = true; }
        (ParsedPage? firstPage, int firstLen, FetchStatus firstStatus) page0;
        try
        {
            page0 = await FetchPageAsync(songId, instrument, 0, accessToken, accountId, limiter, ct);
        }
        finally
        {
            if (page0Acquired) limiter?.Release();
        }

        // Report page-0 to progress tracker (even if empty)
        if (page0.firstPage is not null)
        {
            _progress.ReportPageFetched(page0.firstLen);
        }

        if (page0.firstPage is null || page0.firstPage.TotalPages == 0)
        {
            _progress.ReportPage0(1);
            _progress.ReportLeaderboardComplete(instrument);
            var entries = page0.firstPage?.Entries ?? [];
            return new GlobalLeaderboardResult
            {
                SongId = songId,
                Instrument = instrument,
                Entries = entries,
                EntriesCount = entries.Count,
                TotalPages = page0.firstPage?.TotalPages ?? 0,
                ReportedTotalPages = page0.firstPage?.TotalPages ?? 0,
                PagesScraped = page0.firstPage is not null ? 1 : 0,
                Requests = 1,
                BytesReceived = page0.firstLen,
            };
        }

        int totalPages = page0.firstPage.TotalPages;
        int entriesPerPage = page0.firstPage.Entries.Count;

        // Clamp to configured max to avoid spawning millions of tasks
        int reportedPages = totalPages;
        if (maxPages > 0 && totalPages > maxPages)
            totalPages = maxPages;

        // ── Deep scrape detection: check if page 0's top score exceeds CHOpt threshold ──
        // overThreshold = trigger threshold (CHOpt × multiplier) — used only to decide whether to deep scrape.
        // validCutoff   = CHOpt max × cutoff multiplier — entries above this are "over-threshold" (exploited/cheated);
        //                 valid entry counting and pruning use this value.
        bool deepScrapeTriggered = false;
        int overThreshold = 0;
        int validCutoff = 0;
        if (choptMaxScore.HasValue && page0.firstPage.Entries.Count > 0)
        {
            overThreshold = (int)(choptMaxScore.Value * overThresholdMultiplier);
            validCutoff = (int)(choptMaxScore.Value * validCutoffMultiplier);
            int topScore = page0.firstPage.Entries.Max(e => e.Score);
            if (topScore > overThreshold)
            {
                deepScrapeTriggered = true;
                _log.LogInformation(
                    "Deep scrape triggered for {Label} ({Song}/{Instrument}): top score {TopScore:N0} exceeds " +
                    "{Multiplier:P0} CHOpt max {ChoptMax:N0} (trigger {Threshold:N0}, valid cutoff {Cutoff:N0}).",
                    label ?? songId, songId, instrument, topScore,
                    overThresholdMultiplier, choptMaxScore.Value, overThreshold, validCutoff);
            }
        }

        // Report capped page count for progress tracking (after clamp)
        _progress.ReportPage0(totalPages);

        if (totalPages > 1)
        {
            _log.LogDebug(
                "Scraping {Label} ({Song}/{Instrument}): {EntriesPerPage} entries/page × {TotalPages:N0} pages (of {ReportedPages:N0} reported, ~{EstEntries:N0} entries reported by Epic).",
                label ?? songId, songId, instrument, entriesPerPage, totalPages, reportedPages, (long)entriesPerPage * reportedPages);
        }

        var allEntries = new ConcurrentDictionary<int, List<LeaderboardEntry>>();
        allEntries[0] = page0.firstPage.Entries;

        int requestCount = 1;
        long totalBytes = page0.firstLen;

        if (totalPages > 1)
        {
            // Track consecutive 403s to detect Epic's access boundary.
            // Once we hit ForbiddenThreshold consecutive 403s, cancel remaining pages.
            int consecutive403s = 0;
            int boundaryLogged = 0; // 0 = not logged, 1 = logged (for atomic once-only)
            using var pageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // ── Pages 1…N in parallel (no Task.Run — pure async I/O) ──
            var tasks = new List<Task>(totalPages - 1);
            for (int p = 1; p < totalPages; p++)
            {
                int pageNum = p; // capture
                tasks.Add(FetchAndCollectPageAsync(pageNum));
            }

            async Task FetchAndCollectPageAsync(int pageNum)
            {
                if (pageCts.IsCancellationRequested) return;
                try
                {
                    var (parsed, bodyLen, status) = await _executor.WithCdnResilienceAsync(
                        work: () => FetchPageAsync(
                            songId, instrument, pageNum, accessToken, accountId, limiter, pageCts.Token),
                        ct,
                        acquireSlot: limiter is not null ? () => limiter.WaitAsync(pageCts.Token) : null,
                        releaseSlot: limiter is not null ? limiter.Release : null);
                    Interlocked.Increment(ref requestCount);
                    Interlocked.Add(ref totalBytes, bodyLen);
                    _progress.ReportPageFetched(bodyLen);

                    if (parsed is not null)
                    {
                        allEntries[pageNum] = parsed.Entries;
                        Interlocked.Exchange(ref consecutive403s, 0); // reset on success
                    }
                    else if (status == FetchStatus.Forbidden)
                    {
                        var count = Interlocked.Increment(ref consecutive403s);
                        if (count >= ForbiddenThreshold &&
                            Interlocked.CompareExchange(ref boundaryLogged, 1, 0) == 0)
                        {
                            var entryCount = allEntries.Values.Sum(e => e.Count);
                            _log.LogInformation(
                                "Hit access boundary for {Label} ({Song}/{Instrument}) at page {Page}. " +
                                "Epic reported {ReportedPages:N0} pages but served {Fetched} pages ({Entries:N0} entries).",
                                label ?? songId, songId, instrument, pageNum,
                                reportedPages, allEntries.Count, entryCount);
                            try { pageCts.Cancel(); } catch { }
                        }
                        else if (!pageCts.IsCancellationRequested && count >= ForbiddenThreshold)
                        {
                            // Already logged — just ensure cancellation
                            try { pageCts.Cancel(); } catch { }
                        }
                    }
                }
                catch (OperationCanceledException) when (pageCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // Cancelled due to access boundary — not an error
                }
            }

            await Task.WhenAll(tasks);
        }

        // ── Wave 2: deep scrape extension ──
        // If deep scrape was triggered, fetch additional pages beyond the normal cap.
        // When deferDeepScrape is true and target-driven mode is active, skip wave 2
        // and return metadata so a DeepScrapeCoordinator can run it breadth-first.
        // When validEntryTarget == 0 (legacy): fetch a fixed OverThresholdExtraPages block inline.
        if (deepScrapeTriggered && totalPages < reportedPages)
        {
            int wave2Start = totalPages;

            if (deferDeepScrape && validEntryTarget > 0)
            {
                // ── Deferred mode: return metadata for coordinated deep scrape ──
                int validCount = allEntries.Values.Sum(page => page.Count(e => e.Score <= validCutoff));
                _log.LogInformation(
                    "Deferring deep scrape for {Label} ({Song}/{Instrument}): " +
                    "{ValidCount:N0} valid entries from wave 1, wave 2 starts at page {Wave2Start} (of {ReportedPages:N0} reported).",
                    label ?? songId, songId, instrument, validCount, wave2Start, reportedPages);

                var wave1Ordered = allEntries
                    .OrderBy(x => x.Key)
                    .SelectMany(x => x.Value)
                    .ToList();

                _progress.ReportLeaderboardComplete(instrument);

                return new GlobalLeaderboardResult
                {
                    SongId = songId,
                    Instrument = instrument,
                    Entries = wave1Ordered,
                    EntriesCount = wave1Ordered.Count,
                    TotalPages = totalPages,
                    ReportedTotalPages = reportedPages,
                    PagesScraped = allEntries.Count,
                    Requests = requestCount,
                    BytesReceived = totalBytes,
                    DeferredDeepScrape = new DeepScrapeMetadata
                    {
                        SongId = songId,
                        Instrument = instrument,
                        Label = label,
                        ValidCutoff = validCutoff,
                        Wave2Start = wave2Start,
                        ReportedPages = reportedPages,
                        InitialValidCount = validCount,
                    },
                };
            }

            if (validEntryTarget > 0)
            {
                // ── Target-driven mode (inline): fetch in batches until valid entry target is met ──
                // Valid entries are those at or below the cutoff (CHOpt max × cutoff multiplier), not the trigger threshold.
                int validCount = allEntries.Values.Sum(page => page.Count(e => e.Score <= validCutoff));
                int nextPage = wave2Start;

                while (validCount < validEntryTarget && nextPage < reportedPages)
                {
                    int batchEnd = Math.Min(nextPage + overThresholdExtraPages, reportedPages);
                    int batchSize = batchEnd - nextPage;

                    _log.LogInformation(
                        "Deep scrape for {Label} ({Song}/{Instrument}): fetching pages {Start}–{End} " +
                        "({BatchSize} pages, {ValidCount:N0}/{Target:N0} valid entries so far).",
                        label ?? songId, songId, instrument, nextPage, batchEnd - 1,
                        batchSize, validCount, validEntryTarget);

                    int batchConsecutive403s = 0;
                    int batchBoundaryLogged = 0;
                    bool hitBoundary = false;
                    using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    var batchTasks = new List<Task>(batchSize);
                    for (int p = nextPage; p < batchEnd; p++)
                    {
                        int pageNum = p;
                        batchTasks.Add(FetchDeepScrapeBatchPageAsync(pageNum));
                    }

                    async Task FetchDeepScrapeBatchPageAsync(int pageNum)
                    {
                        if (batchCts.IsCancellationRequested) return;
                        bool acquired = false;
                        try
                        {
                            if (limiter is not null)
                            {
                                await limiter.WaitAsync(batchCts.Token);
                                acquired = true;
                            }

                            var (parsed, bodyLen, status) = await FetchPageAsync(
                                songId, instrument, pageNum, accessToken, accountId, limiter, batchCts.Token);
                            Interlocked.Increment(ref requestCount);
                            Interlocked.Add(ref totalBytes, bodyLen);
                            _progress.ReportPageFetched(bodyLen);

                            if (parsed is not null)
                            {
                                allEntries[pageNum] = parsed.Entries;
                                Interlocked.Exchange(ref batchConsecutive403s, 0);
                            }
                            else if (status == FetchStatus.Forbidden)
                            {
                                var count = Interlocked.Increment(ref batchConsecutive403s);
                                if (count >= ForbiddenThreshold &&
                                    Interlocked.CompareExchange(ref batchBoundaryLogged, 1, 0) == 0)
                                {
                                    _log.LogInformation(
                                        "Hit access boundary during deep scrape for {Label} ({Song}/{Instrument}) at page {Page}.",
                                        label ?? songId, songId, instrument, pageNum);
                                    hitBoundary = true;
                                    try { batchCts.Cancel(); } catch { }
                                }
                                else if (!batchCts.IsCancellationRequested && count >= ForbiddenThreshold)
                                {
                                    hitBoundary = true;
                                    try { batchCts.Cancel(); } catch { }
                                }
                            }
                        }
                        catch (OperationCanceledException) when (batchCts.IsCancellationRequested && !ct.IsCancellationRequested)
                        {
                            // Cancelled due to access boundary — not an error
                        }
                        finally
                        {
                            if (acquired) limiter?.Release();
                        }
                    }

                    await Task.WhenAll(batchTasks);

                    // Count new valid entries from this batch
                    for (int p = nextPage; p < batchEnd; p++)
                    {
                        if (allEntries.TryGetValue(p, out var pageEntries))
                            validCount += pageEntries.Count(e => e.Score <= validCutoff);
                    }

                    nextPage = batchEnd;

                    if (hitBoundary) break;
                }

                if (validCount < validEntryTarget && nextPage >= reportedPages)
                {
                    _log.LogWarning(
                        "Deep scrape for {Label} ({Song}/{Instrument}): leaderboard exhausted with only " +
                        "{ValidCount:N0}/{Target:N0} valid entries ({TotalPages:N0} pages scraped).",
                        label ?? songId, songId, instrument, validCount, validEntryTarget, allEntries.Count);
                }
                else if (validCount >= validEntryTarget)
                {
                    _log.LogInformation(
                        "Deep scrape for {Label} ({Song}/{Instrument}): target met with " +
                        "{ValidCount:N0}/{Target:N0} valid entries ({TotalPages:N0} pages scraped).",
                        label ?? songId, songId, instrument, validCount, validEntryTarget, allEntries.Count);
                }
            }
            else
            {
                // ── Legacy mode: fixed page range ──
                int lastOverThresholdPage = 0;
                foreach (var (pageNum, pageEntries) in allEntries)
                {
                    if (pageEntries.Any(e => e.Score > validCutoff))
                        lastOverThresholdPage = Math.Max(lastOverThresholdPage, pageNum);
                }

                int wave2End = Math.Min(
                    Math.Max(lastOverThresholdPage + overThresholdExtraPages + 1, totalPages + overThresholdExtraPages),
                    reportedPages);

                if (wave2End > wave2Start)
                {
                    int wave2PageCount = wave2End - wave2Start;
                    _log.LogInformation(
                        "Deep scrape wave 2 for {Label} ({Song}/{Instrument}): fetching pages {Start}–{End} " +
                        "({PageCount} pages, last over-threshold entry on page {LastPage}).",
                        label ?? songId, songId, instrument, wave2Start, wave2End - 1,
                        wave2PageCount, lastOverThresholdPage);

                    int wave2Consecutive403s = 0;
                    int wave2BoundaryLogged = 0;
                    using var wave2Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    var wave2Tasks = new List<Task>(wave2PageCount);
                    for (int p = wave2Start; p < wave2End; p++)
                    {
                        int pageNum = p;
                        wave2Tasks.Add(FetchWave2PageAsync(pageNum));
                    }

                    async Task FetchWave2PageAsync(int pageNum)
                    {
                        if (wave2Cts.IsCancellationRequested) return;
                        bool acquired = false;
                        try
                        {
                            if (limiter is not null)
                            {
                                await limiter.WaitAsync(wave2Cts.Token);
                                acquired = true;
                            }

                            var (parsed, bodyLen, status) = await FetchPageAsync(
                                songId, instrument, pageNum, accessToken, accountId, limiter, wave2Cts.Token);
                            Interlocked.Increment(ref requestCount);
                            Interlocked.Add(ref totalBytes, bodyLen);
                            _progress.ReportPageFetched(bodyLen);

                            if (parsed is not null)
                            {
                                allEntries[pageNum] = parsed.Entries;
                                Interlocked.Exchange(ref wave2Consecutive403s, 0);
                            }
                            else if (status == FetchStatus.Forbidden)
                            {
                                var count = Interlocked.Increment(ref wave2Consecutive403s);
                                if (count >= ForbiddenThreshold &&
                                    Interlocked.CompareExchange(ref wave2BoundaryLogged, 1, 0) == 0)
                                {
                                    _log.LogInformation(
                                        "Hit access boundary during deep scrape wave 2 for {Label} ({Song}/{Instrument}) at page {Page}.",
                                        label ?? songId, songId, instrument, pageNum);
                                    try { wave2Cts.Cancel(); } catch { }
                                }
                                else if (!wave2Cts.IsCancellationRequested && count >= ForbiddenThreshold)
                                {
                                    try { wave2Cts.Cancel(); } catch { }
                                }
                            }
                        }
                        catch (OperationCanceledException) when (wave2Cts.IsCancellationRequested && !ct.IsCancellationRequested)
                        {
                            // Cancelled due to access boundary — not an error
                        }
                        finally
                        {
                            if (acquired) limiter?.Release();
                        }
                    }

                    await Task.WhenAll(wave2Tasks);

                    // Warn if over-threshold entries extend beyond wave 2
                    int wave2LastOverThreshold = 0;
                    for (int p = wave2Start; p < wave2End; p++)
                    {
                        if (allEntries.TryGetValue(p, out var entries) && entries.Any(e => e.Score > validCutoff))
                            wave2LastOverThreshold = p;
                    }
                    if (wave2LastOverThreshold >= wave2End - 1)
                    {
                        _log.LogWarning(
                            "Over-threshold entries may extend beyond deep scrape range for {Label} ({Song}/{Instrument}). " +
                            "Last over-threshold entry found on page {Page} (wave 2 end = {Wave2End}).",
                            label ?? songId, songId, instrument, wave2LastOverThreshold, wave2End - 1);
                    }
                }
            }
        }

        // Reassemble entries in page order
        var ordered = allEntries
            .OrderBy(x => x.Key)
            .SelectMany(x => x.Value)
            .ToList();

        _progress.ReportLeaderboardComplete(instrument);

        return new GlobalLeaderboardResult
        {
            SongId = songId,
            Instrument = instrument,
            Entries = ordered,
            EntriesCount = ordered.Count,
            TotalPages = totalPages,
            ReportedTotalPages = reportedPages,
            PagesScraped = allEntries.Count,
            Requests = requestCount,
            BytesReceived = totalBytes,
        };
    }

    /// <summary>
    /// Scrape all instruments for a single song, all in parallel.
    /// When called standalone, creates its own limiter; when called from
    /// <see cref="ScrapeManySongsAsync"/>, shares the cross-song adaptive limiter.
    /// </summary>
    public async Task<List<GlobalLeaderboardResult>> ScrapeSongAsync(
        string songId,
        string accessToken,
        string accountId,
        IEnumerable<string>? instruments = null,
        int maxConcurrency = DefaultMaxConcurrency,
        AdaptiveConcurrencyLimiter? sharedLimiter = null,
        CancellationToken ct = default,
        string? label = null,
        int maxPages = 0,
        SongMaxScores? maxScores = null,
        double overThresholdMultiplier = 1.05,
        int overThresholdExtraPages = 100,
        int validEntryTarget = 0,
        bool deferDeepScrape = false,
        double validCutoffMultiplier = 0.95)
    {
        var instList = (instruments ?? AllInstruments).ToList();
        var limiter = sharedLimiter ?? new AdaptiveConcurrencyLimiter(
            maxConcurrency, 4, maxConcurrency,
            _log);

        // Launch all instruments in parallel
        var tasks = instList.Select(inst =>
        {
            int? choptMax = maxScores?.GetByInstrument(inst);
            return ScrapeLeaderboardAsync(songId, inst, accessToken, accountId, limiter, ct, label, maxPages,
                choptMaxScore: choptMax, overThresholdMultiplier: overThresholdMultiplier,
                overThresholdExtraPages: overThresholdExtraPages,
                validEntryTarget: validEntryTarget,
                deferDeepScrape: deferDeepScrape,
                validCutoffMultiplier: validCutoffMultiplier);
        }).ToList();

        var resultsArr = await Task.WhenAll(tasks);
        var resultsList = resultsArr.ToList();

        // Deep scrape metadata is propagated via DeferredDeepScrape on each result.
        // The caller (ScrapeManySongsAsync) runs a single breadth-first coordinator
        // across ALL songs, avoiding per-song coordinators that starve the DOP pool.

        return resultsList;
    }

    /// <summary>
    /// Represents one song to scrape along with the instruments to query.
    /// </summary>
    public sealed class SongScrapeRequest
    {
        public required string SongId { get; init; }
        public required IReadOnlyList<string> Instruments { get; init; }
        /// <summary>Optional display label for logging (e.g. song title).</summary>
        public string? Label { get; init; }
        /// <summary>Optional CHOpt max scores for this song, used to trigger deep scrape when entries exceed the theoretical max.</summary>
        public SongMaxScores? MaxScores { get; init; }
    }

    /// <summary>
    /// Scrape many songs in parallel, sharing a single concurrency-limiting
    /// semaphore across ALL songs × instruments × pages.  This keeps the
    /// Epic API at a steady request rate without idle gaps between songs.
    /// </summary>
    /// <param name="onSongComplete">
    /// Optional callback invoked as each song completes, enabling pipelined
    /// persistence (disk I/O overlaps with remaining network I/O).
    /// </param>
    /// <returns>Dictionary keyed by songId → list of per-instrument results.</returns>
    public virtual async Task<Dictionary<string, List<GlobalLeaderboardResult>>> ScrapeManySongsAsync(
        IReadOnlyList<SongScrapeRequest> requests,
        string accessToken,
        string accountId,
        int maxConcurrency = DefaultMaxConcurrency,
        Func<string, List<GlobalLeaderboardResult>, ValueTask>? onSongComplete = null,
        CancellationToken ct = default,
        int maxPages = 0,
        bool sequential = false,
        int pageConcurrency = 10,
        int songConcurrency = 1,
        int maxRequestsPerSecond = 0,
        double overThresholdMultiplier = 1.05,
        int overThresholdExtraPages = 100,
        int validEntryTarget = 0,
        AdaptiveConcurrencyLimiter? sharedLimiter = null,
        bool deferDeepScrape = false,
        double validCutoffMultiplier = 0.95)
    {
        if (sequential)
            return await ScrapeManySongsSequentialAsync(
                requests, accessToken, accountId, onSongComplete, ct, maxPages, pageConcurrency, songConcurrency,
                maxRequestsPerSecond, overThresholdMultiplier, overThresholdExtraPages, validEntryTarget,
                sharedLimiter, validCutoffMultiplier: validCutoffMultiplier);

        var ownsLimiter = sharedLimiter is null;
        var limiter = sharedLimiter ?? new AdaptiveConcurrencyLimiter(
            maxConcurrency, minDop: 4, maxDop: maxConcurrency,
            _log, maxRequestsPerSecond);
        try
        {
        _progress.SetAdaptiveLimiter(limiter);
        var results = new ConcurrentDictionary<string, List<GlobalLeaderboardResult>>();

        _log.LogInformation("Starting multi-song scrape: {SongCount} songs, DOP={MaxConcurrency} (adaptive)",
            requests.Count, maxConcurrency);

        var tasks = requests.Select(async req =>
        {
            var songResults = await ScrapeSongAsync(
                req.SongId, accessToken, accountId,
                instruments: req.Instruments,
                sharedLimiter: limiter,
                ct: ct,
                label: req.Label,
                maxPages: maxPages,
                maxScores: req.MaxScores,
                overThresholdMultiplier: overThresholdMultiplier,
                overThresholdExtraPages: overThresholdExtraPages,
                validEntryTarget: validEntryTarget,
                deferDeepScrape: deferDeepScrape,
                validCutoffMultiplier: validCutoffMultiplier);

            results[req.SongId] = songResults;

            // Fire callback so caller can persist immediately (pipelined).
            // Report song-level progress AFTER the callback so that the
            // progress counter doesn't race ahead of actual persistence.
            if (onSongComplete is not null)
                await onSongComplete(req.SongId, songResults);

            // Release entry data now that persistence has consumed it.
            // The result shells remain in the dictionary for population updates
            // (which only need EntriesCount and ReportedTotalPages).
            foreach (var r in songResults)
            {
                r.Entries = [];
                r.DeferredDeepScrape = null;
            }

            _progress.ReportSongComplete();
        }).ToList();

        await Task.WhenAll(tasks);

        // ── Phase 2: Coordinated deep scrape ──
        // Collect deferred deep scrape metadata from wave 1 results.
        // Run them breadth-first through the coordinator so lowest pages
        // across all combos are fetched first, keeping the DOP saturated.
        if (deferDeepScrape && validEntryTarget > 0)
        {
            var deferredMetadata = results.Values
                .SelectMany(songResults => songResults)
                .Where(r => r.DeferredDeepScrape is not null)
                .Select(r => r.DeferredDeepScrape!)
                .ToList();

            if (deferredMetadata.Count > 0)
            {
                _log.LogInformation(
                    "Starting coordinated deep scrape: {Count} combos deferred from wave 1.",
                    deferredMetadata.Count);

                _progress.SetSubOperation("deep_scraping");

                var coordinator = new DeepScrapeCoordinator(this, _progress, _log);
                var deepJobs = DeepScrapeCoordinator.BuildJobs(deferredMetadata, validEntryTarget);

                var deepResults = await coordinator.RunAsync(
                    deepJobs, limiter, accessToken, accountId,
                    seedBatch: overThresholdExtraPages,
                    onJobComplete: async deepResult =>
                    {
                        if (deepResult.Entries.Count == 0) return;

                        // Fire callback with deep scrape results for persistence (upsert handles merges)
                        if (onSongComplete is not null)
                            await onSongComplete(deepResult.SongId, [deepResult]);

                        // Release entry data after persistence callback
                        deepResult.Entries = [];
                    },
                    ct);
            }
        }

        _progress.SetAdaptiveLimiter(null);

        return new Dictionary<string, List<GlobalLeaderboardResult>>(results);
        }
        finally
        {
            if (ownsLimiter) limiter.Dispose();
        }
    }

    /// <summary>
    /// Sequential scrape: one song at a time, instruments in parallel, pages with bounded concurrency.
    /// Max concurrent requests = ~6 instruments × pageConcurrency.
    /// </summary>
    [ExcludeFromCodeCoverage(Justification = "Requires real HTTP with 5s retry delays; tested manually via --once mode.")]
    private async Task<Dictionary<string, List<GlobalLeaderboardResult>>> ScrapeManySongsSequentialAsync(
        IReadOnlyList<SongScrapeRequest> requests,
        string accessToken,
        string accountId,
        Func<string, List<GlobalLeaderboardResult>, ValueTask>? onSongComplete,
        CancellationToken ct,
        int maxPages,
        int pageConcurrency,
        int songConcurrency,
        int maxRequestsPerSecond = 0,
        double overThresholdMultiplier = 1.05,
        int overThresholdExtraPages = 100,
        int validEntryTarget = 0,
        AdaptiveConcurrencyLimiter? sharedLimiter = null,
        double validCutoffMultiplier = 0.95)
    {
        var results = new ConcurrentDictionary<string, List<GlobalLeaderboardResult>>();
        int effectiveSongConcurrency = Math.Max(1, songConcurrency);
        int maxDop = effectiveSongConcurrency * 6 * Math.Max(1, pageConcurrency);
        int initialDop = Math.Max(1, maxDop / 2);

        _log.LogInformation(
            "Starting sequential scrape: {SongCount} songs ({SongDop} at a time, ~{MaxConcurrent} max concurrent requests, adaptive)",
            requests.Count, effectiveSongConcurrency, maxDop);

        var ownsLimiter = sharedLimiter is null;
        var limiter = sharedLimiter ?? new AdaptiveConcurrencyLimiter(initialDop, minDop: 2, maxDop: maxDop, _log,
            maxRequestsPerSecond);
        try
        {
        _progress.SetAdaptiveLimiter(limiter);

        using var songSemaphore = new SemaphoreSlim(effectiveSongConcurrency, effectiveSongConcurrency);

        var tasks = requests.Select(async req =>
        {
            await songSemaphore.WaitAsync(ct);
            try
            {
                var instTasks = req.Instruments.Select(inst =>
                {
                    int? choptMax = req.MaxScores?.GetByInstrument(inst);
                    return ScrapeLeaderboardSequentialAsync(
                        req.SongId, inst, accessToken, accountId, ct, req.Label, maxPages, limiter,
                        choptMaxScore: choptMax, overThresholdMultiplier: overThresholdMultiplier,
                        overThresholdExtraPages: overThresholdExtraPages,
                        validEntryTarget: validEntryTarget,
                        validCutoffMultiplier: validCutoffMultiplier);
                }).ToList();

                var songResults = (await Task.WhenAll(instTasks)).ToList();
                results[req.SongId] = songResults;

                if (onSongComplete is not null)
                    await onSongComplete(req.SongId, songResults);

                // Release entry data after persistence callback
                foreach (var r in songResults)
                {
                    r.Entries = [];
                    r.DeferredDeepScrape = null;
                }

                _progress.ReportSongComplete();
            }
            finally
            {
                songSemaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        _progress.SetAdaptiveLimiter(null);

        return new Dictionary<string, List<GlobalLeaderboardResult>>(results);
        }
        finally
        {
            if (ownsLimiter) limiter.Dispose();
        }
    }

    /// <summary>
    /// Page fetching for one song/instrument with bounded concurrency.
    /// pageConcurrency=1 → fully sequential. pageConcurrency=10 → 10 pages at a time.
    /// </summary>
    [ExcludeFromCodeCoverage(Justification = "Requires real HTTP with 5s retry delays; tested manually via --once mode.")]
    private async Task<GlobalLeaderboardResult> ScrapeLeaderboardSequentialAsync(
        string songId,
        string instrument,
        string accessToken,
        string accountId,
        CancellationToken ct,
        string? label = null,
        int maxPages = 0,
        AdaptiveConcurrencyLimiter? limiter = null,
        int? choptMaxScore = null,
        double overThresholdMultiplier = 1.05,
        int overThresholdExtraPages = 100,
        int validEntryTarget = 0,
        double validCutoffMultiplier = 0.95)
    {
        // ── Page 0 ──
        var page0 = await FetchPageAsync(songId, instrument, 0, accessToken, accountId, limiter, ct);

        if (page0.Page is not null)
        {
            _progress.ReportPageFetched(page0.BodyLength);
        }

        if (page0.Page is null || page0.Page.TotalPages == 0)
        {
            _progress.ReportPage0(1);
            _progress.ReportLeaderboardComplete(instrument);
            var entries = page0.Page?.Entries ?? [];
            return new GlobalLeaderboardResult
            {
                SongId = songId,
                Instrument = instrument,
                Entries = entries,
                EntriesCount = entries.Count,
                TotalPages = page0.Page?.TotalPages ?? 0,
                ReportedTotalPages = page0.Page?.TotalPages ?? 0,
                PagesScraped = page0.Page is not null ? 1 : 0,
                Requests = 1,
                BytesReceived = page0.BodyLength,
            };
        }

        int reportedPages = page0.Page.TotalPages;
        int totalPages = reportedPages;
        if (maxPages > 0 && totalPages > maxPages)
            totalPages = maxPages;

        _progress.ReportPage0(totalPages);

        var allEntries = new ConcurrentDictionary<int, List<LeaderboardEntry>>();
        allEntries[0] = page0.Page.Entries;
        int requestCount = 1;
        long totalBytes = page0.BodyLength;
        int consecutive403s = 0;
        int boundaryLogged = 0;

        if (totalPages > 1)
        {
            using var pageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var tasks = new List<Task>(totalPages - 1);
            for (int p = 1; p < totalPages; p++)
            {
                int pageNum = p;
                tasks.Add(FetchPageWithLimiterAsync(pageNum));
            }

            async Task FetchPageWithLimiterAsync(int pageNum)
            {
                if (pageCts.IsCancellationRequested) return;
                bool acquired = false;
                try
                {
                    if (limiter is not null)
                    {
                        await limiter.WaitAsync(pageCts.Token);
                        acquired = true;
                    }

                    var (parsed, bodyLen, status) = await FetchPageAsync(
                        songId, instrument, pageNum, accessToken, accountId, limiter, pageCts.Token);
                    Interlocked.Increment(ref requestCount);
                    Interlocked.Add(ref totalBytes, bodyLen);
                    _progress.ReportPageFetched(bodyLen);

                    if (parsed is not null)
                    {
                        allEntries[pageNum] = parsed.Entries;
                        Interlocked.Exchange(ref consecutive403s, 0);
                    }
                    else if (status == FetchStatus.Forbidden)
                    {
                        var count = Interlocked.Increment(ref consecutive403s);
                        if (count >= ForbiddenThreshold &&
                            Interlocked.CompareExchange(ref boundaryLogged, 1, 0) == 0)
                        {
                            var entryCount = allEntries.Values.Sum(e => e.Count);
                            _log.LogInformation(
                                "Hit access boundary for {Label} ({Song}/{Instrument}) at page {Page}. " +
                                "Epic reported {ReportedPages:N0} pages but served {Fetched} pages ({Entries:N0} entries).",
                                label ?? songId, songId, instrument, pageNum,
                                reportedPages, allEntries.Count, entryCount);
                            try { pageCts.Cancel(); } catch { }
                        }
                        else if (!pageCts.IsCancellationRequested && count >= ForbiddenThreshold)
                        {
                            try { pageCts.Cancel(); } catch { }
                        }
                    }
                }
                catch (OperationCanceledException) when (pageCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // Cancelled due to access boundary — not an error
                }
                finally
                {
                    if (acquired) limiter?.Release();
                }
            }

            await Task.WhenAll(tasks);
        }

        var ordered = allEntries
            .OrderBy(x => x.Key)
            .SelectMany(x => x.Value)
            .ToList();

        _progress.ReportLeaderboardComplete(instrument);

        return new GlobalLeaderboardResult
        {
            SongId = songId,
            Instrument = instrument,
            Entries = ordered,
            EntriesCount = ordered.Count,
            TotalPages = totalPages,
            ReportedTotalPages = reportedPages,
            PagesScraped = allEntries.Count,
            Requests = requestCount,
            BytesReceived = totalBytes,
        };
    }

    // ─── Parsing ────────────────────────────────────

    private static async Task<ParsedPage?> ParsePageAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var page = root.TryGetProperty("page", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt32() : 0;
            var totalPages = root.TryGetProperty("totalPages", out var tp) && tp.ValueKind == JsonValueKind.Number
                ? tp.GetInt32() : 0;

            var entries = new List<LeaderboardEntry>();

            if (root.TryGetProperty("entries", out var entArr) && entArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in entArr.EnumerateArray())
                {
                    entries.Add(ParseEntryElement(e));
                }
            }

            return new ParsedPage { Page = page, TotalPages = totalPages, Entries = entries,
                EstimatedBytes = entries.Count * 350 /* rough per-entry estimate */ };
        }
        catch
        {
            return null;
        }
    }

    internal sealed class ParsedPage
    {
        public int Page { get; init; }
        public int TotalPages { get; init; }
        public List<LeaderboardEntry> Entries { get; init; } = [];
        public int EstimatedBytes { get; init; }
    }

    /// <summary>
    /// Parsed result of a band leaderboard page.
    /// </summary>
    internal sealed class ParsedBandPage
    {
        public int Page { get; init; }
        public int TotalPages { get; init; }
        public List<BandLeaderboardEntry> Entries { get; init; } = [];
    }

    /// <summary>
    /// Parse a V1 band leaderboard page response, extracting <see cref="BandLeaderboardEntry"/>
    /// entries with per-member stats from <c>trackedStats</c>.
    /// </summary>
    internal static async Task<ParsedBandPage?> ParseBandPageAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var page = root.TryGetProperty("page", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt32() : 0;
            var totalPages = root.TryGetProperty("totalPages", out var tp) && tp.ValueKind == JsonValueKind.Number
                ? tp.GetInt32() : 0;

            var entries = new List<BandLeaderboardEntry>();

            if (root.TryGetProperty("entries", out var entArr) && entArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in entArr.EnumerateArray())
                {
                    var bandEntry = ParseBandEntryElement(e);
                    if (bandEntry is not null)
                        entries.Add(bandEntry);
                }
            }

            return new ParsedBandPage { Page = page, TotalPages = totalPages, Entries = entries };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fetch a single page of a band leaderboard (V1 GET).
    /// Returns the parsed band page or null on failure.
    /// </summary>
    internal async Task<ParsedBandPage?> FetchBandPageAsync(
        string songId,
        string bandType,
        int page,
        string accessToken,
        string accountId,
        AdaptiveConcurrencyLimiter? limiter,
        CancellationToken ct)
    {
        var url = $"{EventsBase}/api/v1/leaderboards/FNFestival/alltime_{songId}_{bandType}" +
                  $"/alltime/{accountId}?page={page}&rank=0&appId=Fortnite&showLiveSessions=false";

        var label = $"{songId}/{bandType}/page({page})";

        HttpRequestMessage CreateRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            return req;
        }

        HttpResponseMessage res;
        try
        {
            res = await _executor.SendAsync(CreateRequest, limiter, label, MaxRetries, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }

        using (res)
        {
            if (!res.IsSuccessStatusCode)
                return null;

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            return await ParseBandPageAsync(stream, ct);
        }
    }

    /// <summary>
    /// Parse a V2 leaderboard response, which is a flat JSON array of entry objects
    /// (unlike V1 which wraps entries in <c>{"entries": [...]}</c>).
    /// </summary>
    private static async Task<List<LeaderboardEntry>?> ParseV2ResponseAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
                return null;

            var entries = new List<LeaderboardEntry>();
            foreach (var e in root.EnumerateArray())
            {
                var entry = ParseEntryElement(e);
                entries.Add(entry);
            }
            return entries;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parse a single leaderboard entry JSON element (shared between V1 and V2 parsers).
    /// </summary>
    private static LeaderboardEntry ParseEntryElement(JsonElement e)
    {
        var entry = new LeaderboardEntry();

        if (e.TryGetProperty("teamId", out var tid) && tid.ValueKind == JsonValueKind.String)
            entry.AccountId = tid.GetString()!;
        else if (e.TryGetProperty("team_id", out var tid2) && tid2.ValueKind == JsonValueKind.String)
            entry.AccountId = tid2.GetString()!;

        if (e.TryGetProperty("rank", out var rk) && rk.ValueKind == JsonValueKind.Number)
            entry.Rank = rk.GetInt32();
        if (e.TryGetProperty("percentile", out var pct) && pct.ValueKind == JsonValueKind.Number)
            entry.Percentile = pct.GetDouble();

        if (e.TryGetProperty("sessionHistory", out var sh) && sh.ValueKind == JsonValueKind.Array)
        {
            int bestScore = 0;
            int bestAccuracy = 0;
            int bestFullCombo = 0;
            int bestStars = 0;
            int bestSeason = 0;
            int bestDifficulty = 0;
            string? bestEndTime = null;

            foreach (var session in sh.EnumerateArray())
            {
                if (!session.TryGetProperty("trackedStats", out var ts) || ts.ValueKind != JsonValueKind.Object)
                    continue;

                int score = ts.TryGetProperty("SCORE", out var sc) && sc.ValueKind == JsonValueKind.Number
                    ? sc.GetInt32() : 0;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestAccuracy = ts.TryGetProperty("ACCURACY", out var ac) && ac.ValueKind == JsonValueKind.Number
                        ? ac.GetInt32() : 0;
                    bestFullCombo = ts.TryGetProperty("FULL_COMBO", out var fc) && fc.ValueKind == JsonValueKind.Number
                        ? fc.GetInt32() : 0;
                    bestStars = ts.TryGetProperty("STARS_EARNED", out var se) && se.ValueKind == JsonValueKind.Number
                        ? se.GetInt32() : 0;
                    bestSeason = ts.TryGetProperty("SEASON", out var sn) && sn.ValueKind == JsonValueKind.Number
                        ? sn.GetInt32() : 0;
                    bestDifficulty = ts.TryGetProperty("DIFFICULTY", out var df) && df.ValueKind == JsonValueKind.Number
                        ? df.GetInt32() : 0;
                    bestEndTime = session.TryGetProperty("endTime", out var et) && et.ValueKind == JsonValueKind.String
                        ? et.GetString() : null;
                }
            }

            entry.Score = bestScore;
            entry.Accuracy = bestAccuracy;
            entry.IsFullCombo = bestFullCombo == 1;
            entry.Stars = bestStars;
            entry.Season = bestSeason;
            entry.Difficulty = bestDifficulty;
            entry.EndTime = bestEndTime;
        }

        return entry;
    }

    // ─── Band entry parsing ─────────────────────────────

    /// <summary>
    /// Parse a band leaderboard entry from a V1 or V2 JSON element.
    /// Extracts <c>teamAccountIds</c>, sorts them to form a deterministic <c>TeamKey</c>,
    /// and parses <c>trackedStats</c> for per-member instrument/score data.
    /// Returns null if the entry has empty/invalid member IDs.
    /// </summary>
    internal static BandLeaderboardEntry? ParseBandEntryElement(JsonElement e)
    {
        // Extract team member IDs
        if (!e.TryGetProperty("teamAccountIds", out var taIds) || taIds.ValueKind != JsonValueKind.Array)
            return null;

        var members = new List<string>();
        foreach (var id in taIds.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.String)
                continue;
            var accountId = id.GetString();
            if (string.IsNullOrWhiteSpace(accountId))
                continue;
            members.Add(accountId);
        }

        if (members.Count < 2)
            return null; // Need at least 2 members for a band

        // Sort lexicographically for deterministic team_key (Epic's ordering is NOT stable)
        var sorted = members.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        var teamKey = string.Join(':', sorted);

        var entry = new BandLeaderboardEntry
        {
            TeamKey = teamKey,
            TeamMembers = members.ToArray(),
        };

        if (e.TryGetProperty("rank", out var rk) && rk.ValueKind == JsonValueKind.Number)
            entry.Rank = rk.GetInt32();
        if (e.TryGetProperty("percentile", out var pct) && pct.ValueKind == JsonValueKind.Number)
            entry.Percentile = pct.GetDouble();

        // Parse trackedStats from the best session (highest SCORE)
        if (e.TryGetProperty("sessionHistory", out var sh) && sh.ValueKind == JsonValueKind.Array)
        {
            ParseBandBestSession(entry, sh);
        }
        else
        {
            // V1 may have score at top level without sessionHistory
            if (e.TryGetProperty("score", out var topScore) && topScore.ValueKind == JsonValueKind.Number)
                entry.Score = topScore.GetInt32();
            if (e.TryGetProperty("pointsEarned", out var pts) && pts.ValueKind == JsonValueKind.Number && entry.Score == 0)
                entry.Score = pts.GetInt32();
        }

        return entry;
    }

    /// <summary>
    /// Parse the best session from a band entry's <c>sessionHistory</c> array.
    /// Extracts band-level stats (<c>B_*</c>) and per-member stats (<c>M_{i}_*</c>).
    /// </summary>
    private static void ParseBandBestSession(BandLeaderboardEntry entry, JsonElement sessionHistory)
    {
        int bestScore = 0;
        JsonElement bestStats = default;
        string? bestEndTime = null;

        foreach (var session in sessionHistory.EnumerateArray())
        {
            if (!session.TryGetProperty("trackedStats", out var ts) || ts.ValueKind != JsonValueKind.Object)
                continue;

            int score = ts.TryGetProperty("SCORE", out var sc) && sc.ValueKind == JsonValueKind.Number
                ? sc.GetInt32() : 0;

            if (score > bestScore)
            {
                bestScore = score;
                bestStats = ts;
                bestEndTime = session.TryGetProperty("endTime", out var et) && et.ValueKind == JsonValueKind.String
                    ? et.GetString() : null;
            }
        }

        if (bestScore == 0)
            return;

        entry.Score = bestScore;
        entry.EndTime = bestEndTime;

        // Band-level stats
        entry.Accuracy = GetIntStat(bestStats, "ACCURACY");
        entry.IsFullCombo = GetIntStat(bestStats, "FULL_COMBO") == 1;
        entry.Stars = GetIntStat(bestStats, "STARS_EARNED");
        entry.Difficulty = GetIntStat(bestStats, "DIFFICULTY");
        entry.Season = GetIntStat(bestStats, "SEASON");
        entry.BaseScore = GetNullableIntStat(bestStats, "B_BASESCORE");
        entry.InstrumentBonus = GetNullableIntStat(bestStats, "B_INSTRUMENT_BONUS");
        entry.OverdriveBonus = GetNullableIntStat(bestStats, "B_OVERDRIVE_BONUS");

        // Per-member stats: parse M_{i}_* fields
        ParseBandMemberStats(entry, bestStats);

        // Build canonical instrument combo from member stats (sorted instrument IDs, colon-joined)
        if (entry.MemberStats.Count > 0)
        {
            entry.InstrumentCombo = string.Join(':',
                entry.MemberStats.Select(m => m.InstrumentId).OrderBy(id => id));
        }
    }

    /// <summary>
    /// Extract per-member stats from <c>trackedStats</c> using the <c>M_{i}_*</c> field pattern.
    /// Maps member index → account ID via <c>M_{i}_ID_{accountId}</c> keys.
    /// </summary>
    private static void ParseBandMemberStats(BandLeaderboardEntry entry, JsonElement stats)
    {
        // Discover member count from INSTRUMENT_{i} fields
        var memberStats = new Dictionary<int, BandMemberStats>();

        foreach (var prop in stats.EnumerateObject())
        {
            var name = prop.Name;

            // M_{i}_ID_{accountId} → maps index to account
            if (name.StartsWith("M_") && name.Contains("_ID_"))
            {
                var parts = name.Split('_', 4); // M, {i}, ID, {accountId}
                if (parts.Length >= 4 && int.TryParse(parts[1], out var idx))
                {
                    var ms = GetOrCreateMemberStats(memberStats, idx);
                    ms.AccountId = parts[3];
                }
            }
            // M_{i}_INSTRUMENT
            else if (name.StartsWith("M_") && name.EndsWith("_INSTRUMENT"))
            {
                var parts = name.Split('_', 3);
                if (parts.Length >= 3 && int.TryParse(parts[1], out var idx) && prop.Value.ValueKind == JsonValueKind.Number)
                {
                    GetOrCreateMemberStats(memberStats, idx).InstrumentId = prop.Value.GetInt32();
                }
            }
            // M_{i}_SCORE
            else if (name.StartsWith("M_") && name.EndsWith("_SCORE"))
            {
                var parts = name.Split('_', 3);
                if (parts.Length >= 3 && int.TryParse(parts[1], out var idx) && prop.Value.ValueKind == JsonValueKind.Number)
                {
                    GetOrCreateMemberStats(memberStats, idx).Score = prop.Value.GetInt32();
                }
            }
            // M_{i}_ACCURACY
            else if (name.StartsWith("M_") && name.EndsWith("_ACCURACY"))
            {
                var parts = name.Split('_', 3);
                if (parts.Length >= 3 && int.TryParse(parts[1], out var idx) && prop.Value.ValueKind == JsonValueKind.Number)
                {
                    GetOrCreateMemberStats(memberStats, idx).Accuracy = prop.Value.GetInt32();
                }
            }
            // M_{i}_FULL_COMBO
            else if (name.StartsWith("M_") && name.EndsWith("_FULL_COMBO"))
            {
                var parts = name.Split('_', 3);
                if (parts.Length >= 3 && int.TryParse(parts[1], out var idx) && prop.Value.ValueKind == JsonValueKind.Number)
                {
                    GetOrCreateMemberStats(memberStats, idx).IsFullCombo = prop.Value.GetInt32() == 1;
                }
            }
            // M_{i}_STARS_EARNED
            else if (name.StartsWith("M_") && name.EndsWith("_STARS_EARNED"))
            {
                var parts = name.Split('_', 3);
                if (parts.Length >= 3 && int.TryParse(parts[1], out var idx) && prop.Value.ValueKind == JsonValueKind.Number)
                {
                    GetOrCreateMemberStats(memberStats, idx).Stars = prop.Value.GetInt32();
                }
            }
            // M_{i}_DIFFICULTY
            else if (name.StartsWith("M_") && name.EndsWith("_DIFFICULTY"))
            {
                var parts = name.Split('_', 3);
                if (parts.Length >= 3 && int.TryParse(parts[1], out var idx) && prop.Value.ValueKind == JsonValueKind.Number)
                {
                    GetOrCreateMemberStats(memberStats, idx).Difficulty = prop.Value.GetInt32();
                }
            }
        }

        entry.MemberStats = memberStats
            .OrderBy(kv => kv.Key)
            .Select(kv => kv.Value)
            .ToList();
    }

    private static BandMemberStats GetOrCreateMemberStats(Dictionary<int, BandMemberStats> dict, int index)
    {
        if (!dict.TryGetValue(index, out var ms))
        {
            ms = new BandMemberStats { MemberIndex = index };
            dict[index] = ms;
        }
        return ms;
    }

    private static int GetIntStat(JsonElement stats, string key) =>
        stats.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static int? GetNullableIntStat(JsonElement stats, string key) =>
        stats.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}

// ─── Models ──────────────────────────────────────

/// <summary>
/// A single player's entry on a global leaderboard.
/// </summary>
public sealed class LeaderboardEntry
{
    public string AccountId { get; set; } = "";
    public int Rank { get; set; }
    public double Percentile { get; set; }
    public int Score { get; set; }
    public int Accuracy { get; set; }
    public bool IsFullCombo { get; set; }
    public int Stars { get; set; }
    public int Season { get; set; }
    /// <summary>Epic difficulty level: 0 = Easy, 1 = Medium, 2 = Hard, 3 = Expert.</summary>
    public int Difficulty { get; set; }
    /// <summary>ISO 8601 timestamp when the best session ended (from API's endTime field). Null when not available.</summary>
    public string? EndTime { get; set; }
    /// <summary>Real rank from Epic API (backfill/lookup). Survives RecomputeAllRanks. 0 = not set.</summary>
    public int ApiRank { get; set; }
    /// <summary>Origin of this entry: "scrape" (global scrape), "backfill" (user backfill target), "neighbor" (backfill neighborhood).</summary>
    public string Source { get; set; } = "scrape";
}

/// <summary>
/// A single session from a player's <c>sessionHistory</c> array.
/// Each session represents one play/run of the song in that leaderboard window.
/// </summary>
public sealed class SessionHistoryEntry
{
    public string AccountId { get; set; } = "";
    public int Rank { get; set; }
    public double Percentile { get; set; }
    public int Score { get; set; }
    public int Accuracy { get; set; }
    public bool IsFullCombo { get; set; }
    public int Stars { get; set; }
    public int Season { get; set; }
    /// <summary>Epic difficulty level: 0 = Easy, 1 = Medium, 2 = Hard, 3 = Expert.</summary>
    public int Difficulty { get; set; }
    /// <summary>ISO 8601 timestamp when the session ended.</summary>
    public string? EndTime { get; set; }
}

/// <summary>
/// Metadata needed to run a deferred deep scrape for one song+instrument.
/// Populated when <c>deferDeepScrape</c> is true and the leaderboard triggers deep scraping.
/// </summary>
public sealed class DeepScrapeMetadata
{
    public required string SongId { get; init; }
    public required string Instrument { get; init; }
    public string? Label { get; init; }
    /// <summary>Raw CHOpt max score — entries above this are over-threshold.</summary>
    public required int ValidCutoff { get; init; }
    /// <summary>First page number for wave 2 (= clamped totalPages from wave 1).</summary>
    public required int Wave2Start { get; init; }
    /// <summary>Total pages reported by Epic's API (unclamped).</summary>
    public required int ReportedPages { get; init; }
    /// <summary>Valid entries (≤ ValidCutoff) already captured in wave 1.</summary>
    public required int InitialValidCount { get; init; }
}

/// <summary>
/// Result of scraping one song+instrument leaderboard.
/// </summary>
public sealed class GlobalLeaderboardResult
{
    public string SongId { get; init; } = "";
    public string Instrument { get; init; } = "";
    public List<LeaderboardEntry> Entries { get; set; } = [];
    /// <summary>Snapshot of <c>Entries.Count</c> taken at construction time. Survives after <c>Entries</c> is cleared to free memory.</summary>
    public int EntriesCount { get; init; }
    public int TotalPages { get; init; }
    /// <summary>The uncapped page count reported by Epic's API. Use <c>ReportedTotalPages * 100</c> for population estimate.</summary>
    public int ReportedTotalPages { get; init; }
    public int PagesScraped { get; init; }
    public int Requests { get; init; }
    public long BytesReceived { get; init; }
    /// <summary>
    /// When non-null, deep scraping was triggered but deferred for coordinated execution.
    /// The coordinator should use this metadata to run wave 2 breadth-first across all deferred combos.
    /// </summary>
    public DeepScrapeMetadata? DeferredDeepScrape { get; set; }
}
