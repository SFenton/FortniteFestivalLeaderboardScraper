using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using FortniteFestival.Core;

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

        var teamsJson = $"{{\"teams\":[[\"{targetAccountId}\"]]}}";
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
    /// transient failures (429, 5xx, network errors, timeouts).
    /// Thread-safe (no shared mutable state).
    /// </summary>
    private async Task<(ParsedPage? Page, int BodyLength, FetchStatus Status)> FetchPageAsync(
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

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                // Exponential backoff: 500ms, 1s, 2s
                var backoff = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
                await Task.Delay(backoff, ct);
            }

            HttpResponseMessage res;
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                res = await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                _log.LogWarning("HTTP error for {Song}/{Instrument} page {Page} (attempt {Attempt}): {Error}",
                    songId, instrument, page, attempt + 1, ex.Message);
                _progress.ReportRetry();
                limiter?.ReportFailure();
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxRetries)
            {
                _log.LogWarning("Timeout for {Song}/{Instrument} page {Page} (attempt {Attempt})",
                    songId, instrument, page, attempt + 1);
                _progress.ReportRetry();
                limiter?.ReportFailure();
                continue;
            }

            if (res.IsSuccessStatusCode)
            {
                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                var contentLength = (int)(res.Content.Headers.ContentLength ?? 0);
                var parsed = await ParsePageAsync(stream, ct);

                if (parsed is not null)
                {
                    limiter?.ReportSuccess();
                    return (parsed, contentLength > 0 ? contentLength : parsed.EstimatedBytes, FetchStatus.Success);
                }

                // Parse failure on a 200 — read body for diagnostics
                string parseBody = "";
                try
                {
                    // Stream already consumed; re-request to peek at body
                    // (stream is not seekable, so we can't rewind)
                    parseBody = $"ContentLength={contentLength}, ContentType={res.Content.Headers.ContentType}";
                }
                catch { }

                if (attempt < MaxRetries)
                {
                    _log.LogWarning("Parse failure for {Song}/{Instrument} page {Page} (attempt {Attempt}): {Detail}",
                        songId, instrument, page, attempt + 1, parseBody);
                    _progress.ReportRetry();
                    limiter?.ReportFailure();
                    continue;
                }

                _log.LogWarning("Failed to parse {Song}/{Instrument} page {Page} after {Attempts} attempts: {Detail}",
                    songId, instrument, page, MaxRetries + 1, parseBody);
                return (null, 0, FetchStatus.OtherFailure);
            }

            var statusCode = (int)res.StatusCode;
            bool retryable = statusCode == 429 || statusCode >= 500;

            // Read a snippet of the error response body for diagnostics
            string errorBody = "";
            try
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                errorBody = body.Length > 200 ? body[..200] : body;
            }
            catch { }

            if (retryable && attempt < MaxRetries)
            {
                // Honor Retry-After header on 429
                if (statusCode == 429 && res.Headers.RetryAfter?.Delta is TimeSpan retryAfter)
                {
                    _log.LogWarning("Rate-limited on {Song}/{Instrument} page {Page}, waiting {Delay:F1}s: {Body}",
                        songId, instrument, page, retryAfter.TotalSeconds, errorBody);
                    _progress.ReportRetry();
                    limiter?.ReportFailure();
                    res.Dispose();
                    await Task.Delay(retryAfter, ct);
                    continue;
                }

                _log.LogWarning("{StatusCode} for {Song}/{Instrument} page {Page} (attempt {Attempt}): {Body}",
                    statusCode, songId, instrument, page, attempt + 1, errorBody);
                _progress.ReportRetry();
                limiter?.ReportFailure();
                res.Dispose();
                continue;
            }

            var failStatus = statusCode == 403 ? FetchStatus.Forbidden : FetchStatus.OtherFailure;

            // Only log non-403 failures — 403s are expected at Epic's access boundary
            // and handled in bulk by the caller via consecutive-403 detection.
            if (failStatus != FetchStatus.Forbidden)
            {
                _log.LogWarning("Leaderboard request failed for {Song}/{Instrument} page {Page}: {StatusCode} {Body}",
                    songId, instrument, page, statusCode, errorBody);
            }

            res.Dispose();
            return (null, 0, failStatus);
        }

        return (null, 0, FetchStatus.OtherFailure);
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
        string? label = null)
    {
        // ── Page 0: discover totalPages ──
        if (limiter is not null) await limiter.WaitAsync(ct);
        (ParsedPage? firstPage, int firstLen, FetchStatus firstStatus) page0;
        try
        {
            page0 = await FetchPageAsync(songId, instrument, 0, accessToken, accountId, limiter, ct);
        }
        finally
        {
            limiter?.Release();
        }

        // Report page-0 to progress tracker (even if empty)
        if (page0.firstPage is not null)
        {
            _progress.ReportPage0(page0.firstPage.TotalPages > 0 ? page0.firstPage.TotalPages : 1);
            _progress.ReportPageFetched(page0.firstLen);
        }

        if (page0.firstPage is null || page0.firstPage.TotalPages == 0)
        {
            _progress.ReportLeaderboardComplete(instrument);
            return new GlobalLeaderboardResult
            {
                SongId = songId,
                Instrument = instrument,
                Entries = page0.firstPage?.Entries ?? [],
                TotalPages = page0.firstPage?.TotalPages ?? 0,
                PagesScraped = page0.firstPage is not null ? 1 : 0,
                Requests = 1,
                BytesReceived = page0.firstLen,
            };
        }

        int totalPages = page0.firstPage.TotalPages;
        int entriesPerPage = page0.firstPage.Entries.Count;

        if (totalPages > 1)
        {
            _log.LogDebug(
                "Scraping {Label} ({Song}/{Instrument}): {EntriesPerPage} entries/page × {TotalPages:N0} pages (~{EstEntries:N0} entries reported by Epic).",
                label ?? songId, songId, instrument, entriesPerPage, totalPages, (long)entriesPerPage * totalPages);
        }

        var allEntries = new ConcurrentBag<(int Page, List<LeaderboardEntry> Entries)>();
        allEntries.Add((0, page0.firstPage.Entries));

        int requestCount = 1;
        long totalBytes = page0.firstLen;

        if (totalPages > 1)
        {
            // Track consecutive 403s to detect Epic's access boundary.
            // Once we hit ForbiddenThreshold consecutive 403s, cancel remaining pages.
            int consecutive403s = 0;
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
                if (limiter is not null) await limiter.WaitAsync(pageCts.Token);
                try
                {
                    var (parsed, bodyLen, status) = await FetchPageAsync(
                        songId, instrument, pageNum, accessToken, accountId, limiter, pageCts.Token);
                    Interlocked.Increment(ref requestCount);
                    Interlocked.Add(ref totalBytes, bodyLen);
                    _progress.ReportPageFetched(bodyLen);

                    if (parsed is not null)
                    {
                        allEntries.Add((pageNum, parsed.Entries));
                        Interlocked.Exchange(ref consecutive403s, 0); // reset on success
                    }
                    else if (status == FetchStatus.Forbidden)
                    {
                        var count = Interlocked.Increment(ref consecutive403s);
                        if (count >= ForbiddenThreshold)
                        {
                            var entryCount = allEntries.Sum(e => e.Entries.Count);
                            _log.LogInformation(
                                "Hit access boundary for {Label} ({Song}/{Instrument}) at page {Page}. " +
                                "Epic reported {TotalPages} pages but served {Fetched} pages ({Entries:N0} entries).",
                                label ?? songId, songId, instrument, pageNum,
                                totalPages, allEntries.Count, entryCount);
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
                    limiter?.Release();
                }
            }

            await Task.WhenAll(tasks);
        }

        // Reassemble entries in page order
        var ordered = allEntries
            .OrderBy(x => x.Page)
            .SelectMany(x => x.Entries)
            .ToList();

        _progress.ReportLeaderboardComplete(instrument);

        return new GlobalLeaderboardResult
        {
            SongId = songId,
            Instrument = instrument,
            Entries = ordered,
            TotalPages = totalPages,
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
        string? label = null)
    {
        var instList = (instruments ?? AllInstruments).ToList();
        var limiter = sharedLimiter ?? new AdaptiveConcurrencyLimiter(
            maxConcurrency, 256, Math.Max(2048, maxConcurrency),
            _log);

        // Launch all instruments in parallel
        var tasks = instList.Select(inst =>
        {
            return ScrapeLeaderboardAsync(songId, inst, accessToken, accountId, limiter, ct, label);
        }).ToList();

        var resultsArr = await Task.WhenAll(tasks);
        return resultsArr.ToList();
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
        CancellationToken ct = default)
    {
        using var limiter = new AdaptiveConcurrencyLimiter(
            maxConcurrency, minDop: 256, maxDop: Math.Max(2048, maxConcurrency),
            _log);
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
                label: req.Label);

            results[req.SongId] = songResults;

            // Fire callback so caller can persist immediately (pipelined).
            // Report song-level progress AFTER the callback so that the
            // progress counter doesn't race ahead of actual persistence.
            if (onSongComplete is not null)
                await onSongComplete(req.SongId, songResults);
            _progress.ReportSongComplete();
        }).ToList();

        await Task.WhenAll(tasks);
        _progress.SetAdaptiveLimiter(null);

        return new Dictionary<string, List<GlobalLeaderboardResult>>(results);
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

    private sealed class ParsedPage
    {
        public int Page { get; init; }
        public int TotalPages { get; init; }
        public List<LeaderboardEntry> Entries { get; init; } = [];
        public int EstimatedBytes { get; init; }
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
                    bestEndTime = session.TryGetProperty("endTime", out var et) && et.ValueKind == JsonValueKind.String
                        ? et.GetString() : null;
                }
            }

            entry.Score = bestScore;
            entry.Accuracy = bestAccuracy;
            entry.IsFullCombo = bestFullCombo == 1;
            entry.Stars = bestStars;
            entry.Season = bestSeason;
            entry.EndTime = bestEndTime;
        }

        return entry;
    }
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
    /// <summary>ISO 8601 timestamp when the best session ended (from API's endTime field). Null when not available.</summary>
    public string? EndTime { get; set; }
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
    /// <summary>ISO 8601 timestamp when the session ended.</summary>
    public string? EndTime { get; set; }
}

/// <summary>
/// Result of scraping one song+instrument leaderboard.
/// </summary>
public sealed class GlobalLeaderboardResult
{
    public string SongId { get; init; } = "";
    public string Instrument { get; init; } = "";
    public List<LeaderboardEntry> Entries { get; init; } = [];
    public int TotalPages { get; init; }
    public int PagesScraped { get; init; }
    public int Requests { get; init; }
    public long BytesReceived { get; init; }
}
