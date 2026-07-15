using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;

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
    private readonly ConcurrentDictionary<(string SongId, string BandType), ScopeState> _scopeStates = new();

    private sealed class ScopeState
    {
        public ConcurrentDictionary<int, GlobalLeaderboardScraper.FetchStatus> PageStatuses { get; } = new();
        public ConcurrentDictionary<int, string> PageFingerprints { get; } = new();
        public int ReportedTotalPages = -1;
        public long EntryCount;
    }

    public IReadOnlyList<ScopeCompletenessRecord> ScopeManifests { get; private set; } = [];

    public BandPageFetcher(
        ResilientHttpExecutor executor,
        SharedDopPool pool,
        SpoolWriter<BandLeaderboardEntry> spool,
        ScrapeProgressTracker progress,
        ILogger log,
        ScrapeAccessTokenProvider? accessTokenProvider = null)
        : base(executor, pool, progress, log, accessTokenProvider)
    {
        _spool = spool;
    }

    protected override string BuildUrl(string songId, string type, int page, string accountId) =>
        $"{EventsBase}/api/v1/leaderboards/FNFestival/alltime_{songId}_{type}" +
        $"/alltime/{accountId}?page={page}&rank=0&appId=Fortnite&showLiveSessions=false";

    protected override async Task<IParsedPage<BandLeaderboardEntry>?> ParseResponseAsync(Stream stream, CancellationToken ct) =>
        await GlobalLeaderboardScraper.ParseBandPageAsync(stream, ct);

    protected override IParsedPage<BandLeaderboardEntry> CreateEmptyPage(int page) =>
        new GlobalLeaderboardScraper.ParsedBandPage
        {
            Page = page,
            TotalPages = 0,
            Entries = [],
        };

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
            var scopeState = _scopeStates.GetOrAdd(
                (item.SongId, item.BandType),
                static _ => new ScopeState());
            var (parsed, bodyLen, status) = await FetchPageWithResilienceAsync(
                item.SongId, item.BandType, 0, accessToken, accountId, innerCt);

            Interlocked.Increment(ref TotalRequests);
            Interlocked.Add(ref TotalBytes, bodyLen);
            Progress.ReportPageFetched(bodyLen);
            scopeState.PageStatuses[0] = parsed is not null
                ? GlobalLeaderboardScraper.FetchStatus.Success
                : status;

            long completed = Interlocked.Increment(ref page0Completed);

            if (parsed is null)
            {
                // Still report progress even for empty pages
                Progress.SetBandFetchProgress("page0_discovery",
                    completed, totalCombos, SongsWithData, Interlocked.Read(ref TotalRetries));
                return;
            }

            Interlocked.Exchange(ref scopeState.ReportedTotalPages, parsed.TotalPages);
            scopeState.PageFingerprints[0] = ComputePageFingerprint(parsed.Entries);
            Interlocked.Add(ref scopeState.EntryCount, parsed.Entries.Count);
            if (parsed.Entries.Count > 0)
            {
                ProcessEntries(item.SongId, item.BandType, parsed);
                Interlocked.Increment(ref TotalPages);
                Interlocked.Add(ref TotalEntries, parsed.Entries.Count);
                TrackSongWithData(item.SongId);
            }

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
            ScopeManifests = BuildScopeManifests(songIds, bandTypes, maxPages);
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
            var scopeState = _scopeStates.GetOrAdd(
                (item.SongId, item.BandType),
                static _ => new ScopeState());
            var (parsed, bodyLen, status) = await FetchPageWithResilienceAsync(
                item.SongId, item.BandType, item.Page,
                accessToken, accountId, innerCt);
            Interlocked.Increment(ref TotalRequests);
            Interlocked.Add(ref TotalBytes, bodyLen);
            Progress.ReportPageFetched(bodyLen);
            scopeState.PageStatuses[item.Page] = parsed is not null
                ? GlobalLeaderboardScraper.FetchStatus.Success
                : status;

            if (parsed is not null)
            {
                scopeState.PageFingerprints[item.Page] = ComputePageFingerprint(parsed.Entries);
                Interlocked.Add(ref scopeState.EntryCount, parsed.Entries.Count);
                if (parsed.Entries.Count > 0)
                {
                    ProcessEntries(item.SongId, item.BandType, parsed);
                    Interlocked.Increment(ref TotalPages);
                    Interlocked.Add(ref TotalEntries, parsed.Entries.Count);
                    TrackSongWithData(item.SongId);
                }
            }

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
        ScopeManifests = BuildScopeManifests(songIds, bandTypes, maxPages);

        Log.LogInformation(
            "BandPageFetcher complete in {Elapsed:F1}s: {Pages:N0} pages, {Entries:N0} entries, " +
            "{Requests:N0} requests, {Retries:N0} retries, {Songs} songs with data.",
            phase2Sw.Elapsed.TotalSeconds,
            Interlocked.Read(ref TotalPages), Interlocked.Read(ref TotalEntries),
            Interlocked.Read(ref TotalRequests), Interlocked.Read(ref TotalRetries), SongsWithData);
    }

    private IReadOnlyList<ScopeCompletenessRecord> BuildScopeManifests(
        IReadOnlyList<string> songIds,
        IReadOnlyList<string> bandTypes,
        int maxPages)
    {
        var manifests = new List<ScopeCompletenessRecord>(songIds.Count * bandTypes.Count);
        foreach (var songId in songIds)
        {
            foreach (var bandType in bandTypes)
            {
                var state = _scopeStates.GetOrAdd(
                    (songId, bandType),
                    static _ => new ScopeState());
                var reportedPages = Math.Max(0, Volatile.Read(ref state.ReportedTotalPages));
                var page0Succeeded = state.PageStatuses.TryGetValue(
                    0,
                    out var page0Status)
                    && page0Status == GlobalLeaderboardScraper.FetchStatus.Success;
                var expectedLastPage = page0Succeeded
                    ? Math.Max(
                            0,
                            Math.Min(
                                reportedPages,
                                maxPages > 0 ? maxPages : int.MaxValue) - 1)
                    : 0;
                var forbiddenPages = state.PageStatuses
                    .Where(static pair =>
                        pair.Value == GlobalLeaderboardScraper.FetchStatus.Forbidden)
                    .Select(static pair => pair.Key)
                    .Order()
                    .ToArray();
                var terminalBoundary = reportedPages == 0
                    && page0Succeeded
                        ? ScopeTerminalBoundaryKind.EpicEmpty
                        : forbiddenPages.Length >= 3
                            ? ScopeTerminalBoundaryKind.EpicForbidden
                            : ScopeTerminalBoundaryKind.None;
                var contentFingerprint = ComputeScopeFingerprint(state.PageFingerprints);
                var entryCount = Interlocked.Read(ref state.EntryCount);

                manifests.Add(new ScopeCompletenessRecord(
                    songId,
                    bandType,
                    ScopeCompletenessManifest.Create(
                        0,
                        expectedLastPage,
                        state.PageStatuses,
                        [],
                        reportedPages,
                        terminalBoundary,
                        forbiddenPages.Length >= 3 ? forbiddenPages[0] : null,
                        contentFingerprintOverride: contentFingerprint,
                        reportedTotalEntriesOverride: reportedPages > 100
                            ? (long)reportedPages * 100
                            : entryCount)));
            }
        }

        return manifests;
    }

    private static string ComputePageFingerprint(
        IReadOnlyList<BandLeaderboardEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries
                     .OrderBy(static entry => entry.TeamKey, StringComparer.Ordinal)
                     .ThenBy(static entry => entry.InstrumentCombo, StringComparer.Ordinal)
                     .ThenByDescending(static entry => entry.Score))
        {
            Append(hash, entry.TeamKey);
            foreach (var member in entry.TeamMembers.OrderBy(
                         static member => member,
                         StringComparer.Ordinal))
                Append(hash, member);
            Append(hash, entry.InstrumentCombo);
            Append(hash, entry.Score);
            Append(hash, entry.BaseScore);
            Append(hash, entry.InstrumentBonus);
            Append(hash, entry.OverdriveBonus);
            Append(hash, entry.Accuracy);
            Append(hash, entry.IsFullCombo);
            Append(hash, entry.Stars);
            Append(hash, entry.Difficulty);
            Append(hash, entry.Season);
            Append(hash, entry.Rank);
            Append(hash, entry.Percentile);
            Append(hash, entry.EndTime);
            Append(hash, entry.Source);
            Append(hash, entry.IsOverThreshold);
            foreach (var member in entry.MemberStats
                         .OrderBy(static member => member.MemberIndex)
                         .ThenBy(static member => member.AccountId, StringComparer.Ordinal))
            {
                Append(hash, member.MemberIndex);
                Append(hash, member.AccountId);
                Append(hash, member.InstrumentId);
                Append(hash, member.Score);
                Append(hash, member.Accuracy);
                Append(hash, member.IsFullCombo);
                Append(hash, member.Stars);
                Append(hash, member.Difficulty);
            }
            Append(hash, null);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeScopeFingerprint(
        IReadOnlyDictionary<int, string> pageFingerprints)
    {
        var value = string.Join(
            '\u001f',
            pageFingerprints
                .OrderBy(static pair => pair.Key)
                .Select(static pair => $"{pair.Key}:{pair.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, object? value)
    {
        var text = value switch
        {
            null => "",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        hash.AppendData(Encoding.UTF8.GetBytes(text));
        hash.AppendData([0x1f]);
    }
}
