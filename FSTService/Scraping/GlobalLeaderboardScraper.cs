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
public sealed class GlobalLeaderboardScraper
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

    /// <summary>
    /// Maps instrument API key → song difficulty accessor.
    /// If the accessor returns 0, the instrument is not charted for that song.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Func<Song, int>> InstrumentDifficultyMap =
        new Dictionary<string, Func<Song, int>>
        {
            ["Solo_Guitar"]          = s => s.track?.@in?.gr ?? 0,
            ["Solo_Bass"]            = s => s.track?.@in?.ba ?? 0,
            ["Solo_Vocals"]          = s => s.track?.@in?.vl ?? 0,
            ["Solo_Drums"]           = s => s.track?.@in?.ds ?? 0,
            ["Solo_PeripheralGuitar"]= s => s.track?.@in?.pg ?? 0,
            ["Solo_PeripheralBass"]  = s => s.track?.@in?.pb ?? 0,
        };

    private readonly HttpClient _http;
    private readonly ILogger<GlobalLeaderboardScraper> _log;
    private readonly ScrapeProgressTracker _progress;

    public GlobalLeaderboardScraper(HttpClient http, ScrapeProgressTracker progress, ILogger<GlobalLeaderboardScraper> log)
    {
        _http = http;
        _progress = progress;
        _log = log;
    }

    /// <summary>
    /// Returns only the instruments that are actually charted for the given song
    /// (difficulty > 0 in the catalog metadata). Use this to avoid wasting
    /// requests on instruments that will always return empty leaderboards.
    /// </summary>
    public static IReadOnlyList<string> GetAvailableInstruments(Song song)
    {
        return AllInstruments
            .Where(inst => InstrumentDifficultyMap.TryGetValue(inst, out var getDiff)
                           && getDiff(song) > 0)
            .ToList();
    }

    // ─── Targeted account lookup ─────────────────────

    /// <summary>
    /// Fetch a specific player's leaderboard entry for one song + instrument
    /// by including <c>teamAccountIds</c> in the request. This jumps directly
    /// to the page containing that player — a single HTTP request regardless
    /// of leaderboard size.  Returns null if the player has no entry.
    /// </summary>
    public async Task<LeaderboardEntry?> LookupAccountAsync(
        string songId,
        string instrument,
        string targetAccountId,
        string accessToken,
        string callerAccountId,
        CancellationToken ct = default)
    {
        var url = $"{EventsBase}/api/v1/leaderboards/FNFestival/alltime_{songId}_{instrument}" +
                  $"/alltime/{callerAccountId}?page=0&rank=0" +
                  $"&teamAccountIds={targetAccountId}&appId=Fortnite&showLiveSessions=false";

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            _log.LogWarning("Account lookup failed for {Account} on {Song}/{Instrument}: {Status}",
                targetAccountId, songId, instrument, res.StatusCode);
            return null;
        }

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        var parsed = await ParsePageAsync(stream, ct);
        if (parsed is null) return null;

        // Find the specific account in the returned page
        return parsed.Entries.FirstOrDefault(e =>
            e.AccountId.Equals(targetAccountId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Look up a specific player's entries across all (or specified) instruments
    /// for a given song. One HTTP request per instrument. Returns a dictionary
    /// of instrument → entry (null entries are omitted).
    /// </summary>
    public async Task<Dictionary<string, LeaderboardEntry>> LookupAccountAllInstrumentsAsync(
        string songId,
        string targetAccountId,
        string accessToken,
        string callerAccountId,
        IEnumerable<string>? instruments = null,
        CancellationToken ct = default)
    {
        var instList = (instruments ?? AllInstruments).ToList();
        var tasks = instList.Select(async inst =>
        {
            var entry = await LookupAccountAsync(
                songId, inst, targetAccountId, accessToken, callerAccountId, ct);
            return (Instrument: inst, Entry: entry);
        }).ToList();

        var results = await Task.WhenAll(tasks);

        return results
            .Where(r => r.Entry is not null)
            .ToDictionary(r => r.Instrument, r => r.Entry!);
    }

    // ─── Single page fetch (with retry) ─────────────

    /// <summary>Max retries for transient HTTP failures (429, 5xx, timeouts).</summary>
    private const int MaxRetries = 3;

    /// <summary>
    /// Fetch and parse a single leaderboard page with automatic retry on
    /// transient failures (429, 5xx, network errors, timeouts).
    /// Thread-safe (no shared mutable state).
    /// </summary>
    private async Task<(ParsedPage? Page, int BodyLength)> FetchPageAsync(
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
                    return (parsed, contentLength > 0 ? contentLength : parsed.EstimatedBytes);
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
                return (null, 0);
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

            _log.LogWarning("Leaderboard request failed for {Song}/{Instrument} page {Page}: {StatusCode} {Body}",
                songId, instrument, page, statusCode, errorBody);
            res.Dispose();
            return (null, 0);
        }

        return (null, 0);
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
        CancellationToken ct = default)
    {
        // ── Page 0: discover totalPages ──
        if (limiter is not null) await limiter.WaitAsync(ct);
        (ParsedPage? firstPage, int firstLen) page0;
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

        var allEntries = new ConcurrentBag<(int Page, List<LeaderboardEntry> Entries)>();
        allEntries.Add((0, page0.firstPage.Entries));

        int requestCount = 1;
        long totalBytes = page0.firstLen;

        if (totalPages > 1)
        {
            // ── Pages 1…N in parallel (no Task.Run — pure async I/O) ──
            var tasks = new List<Task>(totalPages - 1);
            for (int p = 1; p < totalPages; p++)
            {
                int pageNum = p; // capture
                tasks.Add(FetchAndCollectPageAsync(pageNum));
            }

            async Task FetchAndCollectPageAsync(int pageNum)
            {
                if (limiter is not null) await limiter.WaitAsync(ct);
                try
                {
                    var (parsed, bodyLen) = await FetchPageAsync(
                        songId, instrument, pageNum, accessToken, accountId, limiter, ct);
                    Interlocked.Increment(ref requestCount);
                    Interlocked.Add(ref totalBytes, bodyLen);
                    _progress.ReportPageFetched(bodyLen);
                    if (parsed is not null)
                        allEntries.Add((pageNum, parsed.Entries));
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
        CancellationToken ct = default)
    {
        var instList = (instruments ?? AllInstruments).ToList();
        var limiter = sharedLimiter ?? new AdaptiveConcurrencyLimiter(
            maxConcurrency, 256, 2048,
            _log);

        // Launch all instruments in parallel
        var tasks = instList.Select(inst =>
        {
            return ScrapeLeaderboardAsync(songId, inst, accessToken, accountId, limiter, ct);
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
    public async Task<Dictionary<string, List<GlobalLeaderboardResult>>> ScrapeManySongsAsync(
        IReadOnlyList<SongScrapeRequest> requests,
        string accessToken,
        string accountId,
        int maxConcurrency = DefaultMaxConcurrency,
        Func<string, List<GlobalLeaderboardResult>, ValueTask>? onSongComplete = null,
        CancellationToken ct = default)
    {
        using var limiter = new AdaptiveConcurrencyLimiter(
            maxConcurrency, minDop: 256, maxDop: 2048,
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
                ct: ct);

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
                    var entry = new LeaderboardEntry();

                    // Account ID (team_id or teamId)
                    if (e.TryGetProperty("teamId", out var tid) && tid.ValueKind == JsonValueKind.String)
                        entry.AccountId = tid.GetString()!;
                    else if (e.TryGetProperty("team_id", out var tid2) && tid2.ValueKind == JsonValueKind.String)
                        entry.AccountId = tid2.GetString()!;

                    if (e.TryGetProperty("rank", out var rk) && rk.ValueKind == JsonValueKind.Number)
                        entry.Rank = rk.GetInt32();
                    if (e.TryGetProperty("percentile", out var pct) && pct.ValueKind == JsonValueKind.Number)
                        entry.Percentile = pct.GetDouble();

                    // Parse sessionHistory → trackedStats for the best score
                    if (e.TryGetProperty("sessionHistory", out var sh) && sh.ValueKind == JsonValueKind.Array)
                    {
                        int bestScore = 0;
                        int bestAccuracy = 0;
                        int bestFullCombo = 0;
                        int bestStars = 0;
                        int bestSeason = 0;

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
                            }
                        }

                        entry.Score = bestScore;
                        entry.Accuracy = bestAccuracy;
                        entry.IsFullCombo = bestFullCombo == 1;
                        entry.Stars = bestStars;
                        entry.Season = bestSeason;
                    }

                    entries.Add(entry);
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
