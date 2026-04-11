using System.Collections.Concurrent;
using System.Diagnostics;

namespace FSTService.Scraping;

/// <summary>
/// Band leaderboard page fetcher.  Inherits DOP gating, rate limiting,
/// CDN resilience, and retry logic from <see cref="PageFetcherBase{TEntry}"/>.
/// Provides band-specific URL pattern, parser, and entry validation.
///
/// Orchestrates a two-phase fetch:
/// <list type="number">
///   <item>Phase 1: fetch page 0 for each (song, bandType) to discover totalPages.</item>
///   <item>Phase 2: fetch all remaining pages as a flat parallel pool.</item>
/// </list>
///
/// All HTTP requests flow through the shared <see cref="SharedDopPool"/> as
/// low-priority work, ensuring band never starves solo scraping.
/// </summary>
public sealed class BandPageFetcher : PageFetcherBase<BandLeaderboardEntry>
{
    private const string EventsBase = "https://events-public-service-live.ol.epicgames.com";

    private readonly SpoolWriter<BandLeaderboardEntry> _spool;

    public BandPageFetcher(
        ResilientHttpExecutor executor,
        SharedDopPool pool,
        SpoolWriter<BandLeaderboardEntry> spool,
        ScrapeProgressTracker progress,
        ILogger log)
        : base(executor, pool, progress, log)
    {
        _spool = spool;
    }

    protected override string BuildUrl(string songId, string type, int page, string accountId) =>
        $"{EventsBase}/api/v1/leaderboards/FNFestival/alltime_{songId}_{type}" +
        $"/alltime/{accountId}?page={page}&rank=0&appId=Fortnite&showLiveSessions=false";

    protected override async Task<IParsedPage<BandLeaderboardEntry>?> ParseResponseAsync(Stream stream, CancellationToken ct) =>
        await GlobalLeaderboardScraper.ParseBandPageAsync(stream, ct);

    protected override void ProcessEntries(string songId, string type, IParsedPage<BandLeaderboardEntry> page)
    {
        foreach (var entry in page.Entries)
            BandScrapePhase.ApplyChOptValidation(entry, null);

        _spool.Enqueue(songId, type, (IReadOnlyList<BandLeaderboardEntry>)page.Entries);
    }

    /// <summary>
    /// Fetch all band leaderboards for the given songs.
    /// Phase 1: fetch page 0 for each (song, bandType) to discover totalPages.
    /// Phase 2: fetch all remaining pages as a flat parallel pool.
    /// All pages go through <see cref="PageFetcherBase{TEntry}.FetchAndProcessPageAsync"/>
    /// which acquires low-priority DOP slots, rate tokens, and handles CDN blocks.
    /// </summary>
    public async Task FetchAllAsync(
        IReadOnlyList<string> songIds,
        IReadOnlyList<string> bandTypes,
        string accessToken,
        string accountId,
        int maxPages,
        CancellationToken ct)
    {
        int totalCombos = songIds.Count * bandTypes.Count;
        Log.LogInformation("BandPageFetcher: {Songs} songs × {Types} band types = {Combos} leaderboards, maxPages={MaxPages}.",
            songIds.Count, bandTypes.Count, totalCombos, maxPages);

        Progress.SetBandFetchProgress("page0_discovery", 0, totalCombos, 0, 0);

        var phase1Sw = Stopwatch.StartNew();

        // Phase 1: fetch page 0 for all (song, bandType) pairs to discover totalPages
        var pageWork = new ConcurrentBag<(string SongId, string BandType, int Page)>();
        var page0Items = songIds.SelectMany(sid => bandTypes.Select(bt => (SongId: sid, BandType: bt))).ToArray();
        long page0Completed = 0;

        await Parallel.ForEachAsync(page0Items, new ParallelOptions
        {
            // DOP is governed by the SharedDopPool, not this cap.
            // Set to a high value so Parallel.ForEachAsync doesn't bottleneck below pool capacity.
            MaxDegreeOfParallelism = 2048,
            CancellationToken = ct,

        }, async (item, innerCt) =>
        {
            var (parsed, bodyLen, status) = await FetchPageWithResilienceAsync(
                item.SongId, item.BandType, 0, accessToken, accountId, innerCt);

            Interlocked.Increment(ref TotalRequests);
            Interlocked.Add(ref TotalBytes, bodyLen);
            Progress.ReportPageFetched(bodyLen);

            long completed = Interlocked.Increment(ref page0Completed);

            if (parsed is null || parsed.Entries.Count == 0)
            {
                // Still report progress even for empty pages
                Progress.SetBandFetchProgress("page0_discovery",
                    completed, totalCombos, SongsWithData, Interlocked.Read(ref TotalRetries));
                return;
            }

            ProcessEntries(item.SongId, item.BandType, parsed);
            Interlocked.Increment(ref TotalPages);
            Interlocked.Add(ref TotalEntries, parsed.Entries.Count);
            TrackSongWithData(item.SongId);

            int totalPages = Math.Min(parsed.TotalPages, maxPages > 0 ? maxPages : int.MaxValue);
            for (int p = 1; p < totalPages; p++)
                pageWork.Add((item.SongId, item.BandType, p));

            Progress.SetBandFetchProgress("page0_discovery",
                completed, totalCombos, SongsWithData, Interlocked.Read(ref TotalRetries));
        });

        phase1Sw.Stop();
        Log.LogInformation("BandPageFetcher phase 1 done in {Elapsed:F1}s: {Page0s} page-0s fetched, {Remaining} remaining pages queued.",
            phase1Sw.Elapsed.TotalSeconds, totalCombos, pageWork.Count);

        // Phase 2: fetch all remaining pages
        if (pageWork.IsEmpty)
        {
            Progress.SetBandFetchProgress("complete",
                Interlocked.Read(ref TotalPages), Interlocked.Read(ref TotalPages),
                SongsWithData, Interlocked.Read(ref TotalRetries));
            Progress.SetBandFetchComplete();
            return;
        }

        var workItems = pageWork.ToArray();
        long totalWorkItems = workItems.Length + Interlocked.Read(ref TotalPages);
        Progress.SetBandFetchProgress("fetching_pages",
            Interlocked.Read(ref TotalPages), totalWorkItems,
            SongsWithData, Interlocked.Read(ref TotalRetries));

        var phase2Sw = Stopwatch.StartNew();

        await Parallel.ForEachAsync(workItems, new ParallelOptions
        {
            MaxDegreeOfParallelism = 2048,
            CancellationToken = ct,
        }, async (item, innerCt) =>
        {
            await FetchAndProcessPageAsync(
                item.SongId, item.BandType, item.Page,
                accessToken, accountId, innerCt);

            // Live progress update on every page
            Progress.SetBandFetchProgress("fetching_pages",
                Interlocked.Read(ref TotalPages), totalWorkItems,
                SongsWithData, Interlocked.Read(ref TotalRetries));
        });

        phase2Sw.Stop();

        Progress.SetBandFetchProgress("complete",
            Interlocked.Read(ref TotalPages), totalWorkItems,
            SongsWithData, Interlocked.Read(ref TotalRetries));
        Progress.SetBandFetchComplete();

        Log.LogInformation(
            "BandPageFetcher complete in {Elapsed:F1}s: {Pages:N0} pages, {Entries:N0} entries, " +
            "{Requests:N0} requests, {Retries:N0} retries, {Songs} songs with data.",
            phase2Sw.Elapsed.TotalSeconds,
            Interlocked.Read(ref TotalPages), Interlocked.Read(ref TotalEntries),
            Interlocked.Read(ref TotalRequests), Interlocked.Read(ref TotalRetries), SongsWithData);
    }
}
