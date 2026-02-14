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

    public GlobalLeaderboardScraper(HttpClient http, ILogger<GlobalLeaderboardScraper> log)
    {
        _http = http;
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

    // ─── Single page fetch ──────────────────────────

    /// <summary>
    /// Fetch and parse a single leaderboard page. Returns null on failure.
    /// Thread-safe (no shared mutable state).
    /// </summary>
    private async Task<(ParsedPage? Page, int BodyLength)> FetchPageAsync(
        string songId,
        string instrument,
        int page,
        string accessToken,
        string accountId,
        CancellationToken ct)
    {
        var url = $"{EventsBase}/api/v1/leaderboards/FNFestival/alltime_{songId}_{instrument}" +
                  $"/alltime/{accountId}?page={page}&rank=0&appId=Fortnite&showLiveSessions=false";

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var res = await _http.SendAsync(req, ct);

        if (!res.IsSuccessStatusCode)
        {
            _log.LogWarning("Leaderboard request failed for {Song}/{Instrument} page {Page}: {Status}",
                songId, instrument, page, res.StatusCode);
            return (null, 0);
        }

        // Stream-parse to avoid large string allocations
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        var contentLength = (int)(res.Content.Headers.ContentLength ?? 0);
        var parsed = await ParsePageAsync(stream, ct);

        if (parsed is null)
        {
            _log.LogWarning("Failed to parse leaderboard page for {Song}/{Instrument} page {Page}",
                songId, instrument, page);
        }

        return (parsed, contentLength > 0 ? contentLength : parsed?.EstimatedBytes ?? 0);
    }

    // ─── Single instrument (parallel pages) ─────────

    /// <summary>
    /// Scrape all pages of a single leaderboard (one song + one instrument).
    /// Page 0 is fetched first to discover totalPages, then pages 1…N are
    /// fetched concurrently, throttled by <paramref name="semaphore"/>.
    /// </summary>
    public async Task<GlobalLeaderboardResult> ScrapeLeaderboardAsync(
        string songId,
        string instrument,
        string accessToken,
        string accountId,
        SemaphoreSlim? semaphore = null,
        CancellationToken ct = default)
    {
        // ── Page 0: discover totalPages ──
        if (semaphore is not null) await semaphore.WaitAsync(ct);
        (ParsedPage? firstPage, int firstLen) page0;
        try
        {
            page0 = await FetchPageAsync(songId, instrument, 0, accessToken, accountId, ct);
        }
        finally
        {
            semaphore?.Release();
        }

        if (page0.firstPage is null || page0.firstPage.TotalPages == 0)
        {
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
            // ── Pages 1…N in parallel ──
            var tasks = new List<Task>();
            for (int p = 1; p < totalPages; p++)
            {
                int pageNum = p; // capture
                tasks.Add(Task.Run(async () =>
                {
                    if (semaphore is not null) await semaphore.WaitAsync(ct);
                    try
                    {
                        var (parsed, bodyLen) = await FetchPageAsync(
                            songId, instrument, pageNum, accessToken, accountId, ct);
                        Interlocked.Increment(ref requestCount);
                        Interlocked.Add(ref totalBytes, bodyLen);
                        if (parsed is not null)
                            allEntries.Add((pageNum, parsed.Entries));
                    }
                    finally
                    {
                        semaphore?.Release();
                    }
                }, ct));
            }

            await Task.WhenAll(tasks);
        }

        // Reassemble entries in page order
        var ordered = allEntries
            .OrderBy(x => x.Page)
            .SelectMany(x => x.Entries)
            .ToList();

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
    /// When called standalone, creates its own semaphore; when called from
    /// <see cref="ScrapeManySongsAsync"/>, shares the cross-song semaphore.
    /// </summary>
    public async Task<List<GlobalLeaderboardResult>> ScrapeSongAsync(
        string songId,
        string accessToken,
        string accountId,
        IEnumerable<string>? instruments = null,
        int maxConcurrency = DefaultMaxConcurrency,
        SemaphoreSlim? sharedSemaphore = null,
        CancellationToken ct = default)
    {
        var instList = (instruments ?? AllInstruments).ToList();
        var semaphore = sharedSemaphore ?? new SemaphoreSlim(maxConcurrency, maxConcurrency);

        // Launch all instruments in parallel
        var tasks = instList.Select(inst =>
            ScrapeLeaderboardAsync(songId, inst, accessToken, accountId, semaphore, ct))
            .ToList();

        var resultsArr = await Task.WhenAll(tasks);
        var results = resultsArr.ToList();

        foreach (var r in results)
        {
            _log.LogInformation("  {Instrument}: {Entries} entries across {Pages}/{TotalPages} pages ({Requests} reqs, {Bytes} bytes)",
                r.Instrument, r.Entries.Count, r.PagesScraped, r.TotalPages,
                r.Requests, r.BytesReceived);
        }

        return results;
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
    /// <returns>Dictionary keyed by songId → list of per-instrument results.</returns>
    public async Task<Dictionary<string, List<GlobalLeaderboardResult>>> ScrapeManySongsAsync(
        IReadOnlyList<SongScrapeRequest> requests,
        string accessToken,
        string accountId,
        int maxConcurrency = DefaultMaxConcurrency,
        CancellationToken ct = default)
    {
        var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var results = new ConcurrentDictionary<string, List<GlobalLeaderboardResult>>();

        _log.LogInformation("Starting multi-song scrape: {SongCount} songs, DOP={MaxConcurrency}",
            requests.Count, maxConcurrency);

        var tasks = requests.Select(async req =>
        {
            var songResults = await ScrapeSongAsync(
                req.SongId, accessToken, accountId,
                instruments: req.Instruments,
                sharedSemaphore: semaphore,
                ct: ct);

            results[req.SongId] = songResults;

            var totalEntries = songResults.Sum(r => r.Entries.Count);
            var totalReqs = songResults.Sum(r => r.Requests);
            _log.LogInformation("[{Label}] done — {Entries} entries, {Reqs} requests",
                req.Label ?? req.SongId, totalEntries, totalReqs);
        }).ToList();

        await Task.WhenAll(tasks);

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
                    if (e.TryGetProperty("pointsEarned", out var pe) && pe.ValueKind == JsonValueKind.Number)
                        entry.PointsEarned = pe.GetInt32();
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
    public int PointsEarned { get; set; }
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
