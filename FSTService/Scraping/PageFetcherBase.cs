using System.Collections.Concurrent;
using System.Net.Http.Headers;
using FortniteFestival.Core.Scraping;

namespace FSTService.Scraping;

/// <summary>
/// Shared base for leaderboard page fetchers.  Encapsulates DOP slot acquisition
/// (via <see cref="SharedDopPool"/>), rate-token gating, CDN resilience, retry
/// logic, and progress reporting.  Subclasses provide only the URL, parser, and
/// entry-processing logic.
///
/// <para>Both solo and band fetchers inherit this class, making it structurally
/// impossible for either to bypass concurrency or rate controls.</para>
/// </summary>
public abstract class PageFetcherBase<TEntry>
{
    private const string EventsBase = "https://events-public-service-live.ol.epicgames.com";
    private const int MaxRetries = 3;

    protected readonly ResilientHttpExecutor Executor;
    protected readonly SharedDopPool Pool;
    protected readonly ScrapeProgressTracker Progress;
    protected readonly ILogger Log;

    // ── Live counters (thread-safe) ──────────────────────────

    public long TotalPages;
    public long TotalEntries;
    public long TotalRequests;
    public long TotalRetries;
    public long TotalBytes;

    private readonly ConcurrentDictionary<string, byte> _songsWithData = new(StringComparer.OrdinalIgnoreCase);
    public int SongsWithData => _songsWithData.Count;

    /// <summary>Record that a song has at least one entry with data.</summary>
    protected void TrackSongWithData(string songId) => _songsWithData.TryAdd(songId, 0);

    protected PageFetcherBase(
        ResilientHttpExecutor executor,
        SharedDopPool pool,
        ScrapeProgressTracker progress,
        ILogger log)
    {
        Executor = executor;
        Pool = pool;
        Progress = progress;
        Log = log;
    }

    // ── Abstract: subclass-specific behaviour ────────────────

    /// <summary>Build the V1 leaderboard URL for one page.</summary>
    protected abstract string BuildUrl(string songId, string type, int page, string accountId);

    /// <summary>Parse the HTTP response stream into a typed page.</summary>
    protected abstract Task<IParsedPage<TEntry>?> ParseResponseAsync(Stream stream, CancellationToken ct);

    /// <summary>Validate entries and enqueue them for persistence.</summary>
    protected abstract void ProcessEntries(string songId, string type, IParsedPage<TEntry> page);

    // ── Concrete: shared fetch-with-resilience cycle ─────────

    /// <summary>
    /// Fetch a single page with full DOP gating, rate-token acquisition,
    /// CDN resilience, and automatic retry.  This is the only method
    /// subclasses should call to fetch pages — it guarantees the
    /// acquire → fetch → release cycle is always correct.
    /// </summary>
    /// <returns>
    /// The parsed page (or null on failure), body length, and fetch status.
    /// </returns>
    public async Task<(IParsedPage<TEntry>? Page, int BodyLength, GlobalLeaderboardScraper.FetchStatus Status)>
        FetchPageWithResilienceAsync(
            string songId,
            string type,
            int page,
            string accessToken,
            string accountId,
            CancellationToken ct)
    {
        // Capture the low-priority token across the acquire/release callbacks.
        // WithCdnResilienceAsync manages the acquire→work→release lifecycle and
        // handles CdnBlockedException (release, wait for clear, re-acquire, retry).
        LowPriorityToken currentToken = default;

        return await Executor.WithCdnResilienceAsync(
            work: () => FetchPageRawAsync(songId, type, page, accessToken, accountId, ct),
            ct,
            acquireSlot: async () =>
            {
                currentToken = await Pool.AcquireLowAsync(ct);
            },
            releaseSlot: () =>
            {
                Pool.ReleaseLow(currentToken);
                currentToken = default;
            });
    }

    /// <summary>
    /// Inner fetch: build URL, send via executor, parse, report progress.
    /// Called inside the CDN-resilience wrapper which manages the DOP slot.
    /// </summary>
    protected async Task<(IParsedPage<TEntry>? Page, int BodyLength, GlobalLeaderboardScraper.FetchStatus Status)>
        FetchPageRawAsync(
            string songId,
            string type,
            int page,
            string accessToken,
            string accountId,
            CancellationToken ct)
    {
        var url = BuildUrl(songId, type, page, accountId);
        var label = $"{songId}/{type}/page({page})";

        HttpRequestMessage CreateRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return req;
        }

        // Outer loop: retries on transient 403 and 500 with escalating backoff.
        for (int fetchAttempt = 0; fetchAttempt < 2; fetchAttempt++)
        {
            if (fetchAttempt > 0)
            {
                Interlocked.Increment(ref TotalRetries);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }

            HttpResponseMessage res;
            try
            {
                res = await Executor.SendAsync(CreateRequest, Pool.Limiter, label, MaxRetries, ct);
            }
            catch (HttpRequestException ex)
            {
                Log.LogWarning("HTTP error for {Song}/{Type} page {Page}: {Error}",
                    songId, type, page, ex.Message);
                Interlocked.Increment(ref TotalRetries);
                Progress.ReportRetry();
                return (null, 0, GlobalLeaderboardScraper.FetchStatus.OtherFailure);
            }

            using (res)
            {
                if (res.IsSuccessStatusCode)
                {
                    await using var stream = await res.Content.ReadAsStreamAsync(ct);
                    var contentLength = (int)(res.Content.Headers.ContentLength ?? 0);
                    var parsed = await ParseResponseAsync(stream, ct);

                    if (parsed is not null)
                    {
                        int bodyLen = contentLength > 0 ? contentLength : parsed.EstimatedBytes;
                        return (parsed, bodyLen, GlobalLeaderboardScraper.FetchStatus.Success);
                    }

                    Log.LogWarning("Failed to parse {Song}/{Type} page {Page}: ContentLength={CL}",
                        songId, type, page, contentLength);
                    return (null, 0, GlobalLeaderboardScraper.FetchStatus.OtherFailure);
                }

                var statusCode = (int)res.StatusCode;

                if (statusCode == 403 || statusCode == 500)
                {
                    Progress.ReportRetry();
                    continue;
                }

                string errorBody = "";
                try
                {
                    var body = await res.Content.ReadAsStringAsync(ct);
                    errorBody = body.Length > 200 ? body[..200] : body;
                }
                catch { }

                Log.LogWarning("Leaderboard request failed for {Song}/{Type} page {Page}: {StatusCode} {Body}",
                    songId, type, page, statusCode, errorBody);
                return (null, 0, GlobalLeaderboardScraper.FetchStatus.OtherFailure);
            }
        }

        Log.LogWarning("Exhausted all retry attempts for {Song}/{Type} page {Page}.", songId, type, page);
        Interlocked.Increment(ref TotalRetries);
        return (null, 0, GlobalLeaderboardScraper.FetchStatus.OtherFailure);
    }

    // ── Helpers for subclass orchestration methods ────────────

    /// <summary>
    /// Fetch a page, update shared counters, process entries, and track the song.
    /// This is the standard "fetch + bookkeep" unit of work used by orchestration loops.
    /// </summary>
    protected async Task FetchAndProcessPageAsync(
        string songId, string type, int page,
        string accessToken, string accountId,
        CancellationToken ct)
    {
        var (parsed, bodyLen, status) = await FetchPageWithResilienceAsync(
            songId, type, page, accessToken, accountId, ct);

        Interlocked.Increment(ref TotalRequests);
        Interlocked.Add(ref TotalBytes, bodyLen);
        Progress.ReportPageFetched(bodyLen);

        if (parsed is null || parsed.Entries.Count == 0)
            return;

        ProcessEntries(songId, type, parsed);

        Interlocked.Increment(ref TotalPages);
        Interlocked.Add(ref TotalEntries, parsed.Entries.Count);
        TrackSongWithData(songId);
    }
}
