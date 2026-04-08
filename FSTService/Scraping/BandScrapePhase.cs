using FortniteFestival.Core;
using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

/// <summary>
/// Scrape phase that fetches Band_Duets, Band_Trios, and Band_Quad leaderboards
/// from the V1 alltime API for all songs.
///
/// Runs as a separate phase AFTER solo scraping completes (in ScraperWorker).
/// For each (song, bandType), pages are fetched until <c>BandValidEntryTarget</c>
/// valid entries are collected or pages are exhausted.
///
/// Per-member CHOpt validation: each member's individual score is checked against
/// the CHOpt max for their instrument. Over-threshold entries are flagged but still
/// persisted (client-side filter controls visibility).
/// </summary>
public sealed class BandScrapePhase
{
    private readonly GlobalLeaderboardScraper _scraper;
    private readonly BandLeaderboardPersistence _persistence;
    private readonly IPathDataStore _pathDataStore;
    private readonly ScrapeProgressTracker _progress;
    private readonly IOptions<ScraperOptions> _options;
    private readonly ILogger<BandScrapePhase> _log;

    public BandScrapePhase(
        GlobalLeaderboardScraper scraper,
        BandLeaderboardPersistence persistence,
        IPathDataStore pathDataStore,
        ScrapeProgressTracker progress,
        IOptions<ScraperOptions> options,
        ILogger<BandScrapePhase> log)
    {
        _scraper = scraper;
        _persistence = persistence;
        _pathDataStore = pathDataStore;
        _progress = progress;
        _options = options;
        _log = log;
    }

    /// <summary>
    /// Scrape all band leaderboards for the given songs.
    /// Returns the total number of band entries persisted.
    /// </summary>
    public async Task<BandScrapeResult> ExecuteAsync(
        IReadOnlyList<Song> songs,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter,
        CancellationToken ct)
    {
        var opts = _options.Value;
        if (!opts.EnableBandScraping)
        {
            _log.LogDebug("Band scraping disabled. Skipping.");
            return new BandScrapeResult();
        }

        var allMaxScores = _pathDataStore.GetAllMaxScores();
        var bandTypes = BandInstrumentMapping.AllBandTypes;
        var chartedSongs = songs.Where(s => s.track?.su is not null).ToList();

        int totalCombos = chartedSongs.Count * bandTypes.Count;
        _log.LogInformation("Band scrape: {Songs} songs × {Types} band types = {Total} leaderboards.",
            chartedSongs.Count, bandTypes.Count, totalCombos);

        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.BandScraping);
        _progress.BeginPhaseProgress(totalCombos);

        int totalEntries = 0;
        int totalRequests = 0;
        long totalBytes = 0;
        int songsWithData = 0;

        foreach (var song in chartedSongs)
        {
            ct.ThrowIfCancellationRequested();
            var songId = song.track!.su!;
            var songMaxScores = allMaxScores.TryGetValue(songId, out var ms) ? ms : null;
            bool songHasData = false;

            foreach (var bandType in bandTypes)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var result = await ScrapeBandLeaderboardAsync(
                        songId, bandType, accessToken, callerAccountId,
                        songMaxScores, limiter, opts, ct);

                    if (result.Entries.Count > 0)
                    {
                        var persisted = _persistence.UpsertBandEntries(songId, bandType, result.Entries);
                        Interlocked.Add(ref totalEntries, persisted);
                        _progress.ReportPhaseEntryUpdated(persisted);
                        songHasData = true;
                    }

                    Interlocked.Add(ref totalRequests, result.Requests);
                    Interlocked.Add(ref totalBytes, result.BytesReceived);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to scrape band leaderboard {Song}/{BandType}.", songId, bandType);
                }

                _progress.ReportPhaseItemComplete();
            }

            if (songHasData)
                Interlocked.Increment(ref songsWithData);
        }

        _log.LogInformation(
            "Band scrape complete: {Entries:N0} entries across {Songs} songs. " +
            "{Requests:N0} requests, {Bytes:N0} bytes.",
            totalEntries, songsWithData, totalRequests, totalBytes);

        return new BandScrapeResult
        {
            TotalEntries = totalEntries,
            TotalRequests = totalRequests,
            TotalBytes = totalBytes,
            SongsWithData = songsWithData,
        };
    }

    /// <summary>
    /// Scrape one band leaderboard (one song + one band type).
    /// Fetches pages sequentially until the valid entry target is met or pages are exhausted.
    /// </summary>
    private async Task<BandLeaderboardScrapeResult> ScrapeBandLeaderboardAsync(
        string songId, string bandType,
        string accessToken, string callerAccountId,
        SongMaxScores? maxScores,
        AdaptiveConcurrencyLimiter? limiter,
        ScraperOptions opts,
        CancellationToken ct)
    {
        var allEntries = new List<BandLeaderboardEntry>();
        int validCount = 0;
        int requests = 0;
        long bytesReceived = 0;
        int maxPages = opts.BandMaxPagesPerLeaderboard;
        int validTarget = opts.BandValidEntryTarget;

        // Fetch page 0 to discover totalPages
        var page0 = await _scraper.FetchBandPageAsync(songId, bandType, 0, accessToken, callerAccountId, limiter, ct);
        requests++;
        _progress.ReportPhaseRequest();

        if (page0 is null || page0.Entries.Count == 0)
            return new BandLeaderboardScrapeResult { Requests = requests };

        int totalPages = page0.TotalPages;
        int pagesToFetch = maxPages > 0 ? Math.Min(totalPages, maxPages) : totalPages;

        // Process page 0 entries
        foreach (var entry in page0.Entries)
        {
            ApplyChOptValidation(entry, maxScores);
            allEntries.Add(entry);
            if (!entry.IsOverThreshold)
                validCount++;
        }

        // Fetch remaining pages until valid target met
        for (int page = 1; page < pagesToFetch && (validTarget == 0 || validCount < validTarget); page++)
        {
            ct.ThrowIfCancellationRequested();

            var parsed = await _scraper.FetchBandPageAsync(songId, bandType, page, accessToken, callerAccountId, limiter, ct);
            requests++;
            _progress.ReportPhaseRequest();

            if (parsed is null || parsed.Entries.Count == 0)
                break;

            foreach (var entry in parsed.Entries)
            {
                ApplyChOptValidation(entry, maxScores);
                allEntries.Add(entry);
                if (!entry.IsOverThreshold)
                    validCount++;
            }

            // If we're past page 0 and page returned fewer than expected, we've exhausted
            if (parsed.Entries.Count < 25)
                break;
        }

        // Extend pagination beyond maxPages if valid target not yet met (like solo deep scrape)
        if (validTarget > 0 && validCount < validTarget && pagesToFetch < totalPages)
        {
            int batchSize = opts.OverThresholdExtraPages > 0 ? opts.OverThresholdExtraPages : 100;
            for (int page = pagesToFetch; page < totalPages && validCount < validTarget; page++)
            {
                ct.ThrowIfCancellationRequested();

                var parsed = await _scraper.FetchBandPageAsync(songId, bandType, page, accessToken, callerAccountId, limiter, ct);
                requests++;
                _progress.ReportPhaseRequest();

                if (parsed is null || parsed.Entries.Count == 0)
                    break;

                foreach (var entry in parsed.Entries)
                {
                    ApplyChOptValidation(entry, maxScores);
                    allEntries.Add(entry);
                    if (!entry.IsOverThreshold)
                        validCount++;
                }

                if (parsed.Entries.Count < 25)
                    break;
            }
        }

        // Deduplicate by team_key (same team can appear on multiple pages)
        var deduped = allEntries
            .GroupBy(e => e.TeamKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.Score).First())
            .ToList();

        return new BandLeaderboardScrapeResult
        {
            Entries = deduped,
            Requests = requests,
            BytesReceived = bytesReceived,
            ValidCount = validCount,
            TotalPages = totalPages,
        };
    }

    /// <summary>
    /// Check each member's individual score against the CHOpt max for their instrument.
    /// Sets <see cref="BandLeaderboardEntry.IsOverThreshold"/> if any member exceeds 0.95× CHOpt max.
    /// </summary>
    private static void ApplyChOptValidation(BandLeaderboardEntry entry, SongMaxScores? maxScores)
    {
        if (maxScores is null || entry.MemberStats.Count == 0)
            return;

        foreach (var member in entry.MemberStats)
        {
            var leaderboardType = BandInstrumentMapping.ToLeaderboardType(member.InstrumentId);
            if (leaderboardType is null)
                continue;

            var choptMax = maxScores.GetByInstrument(leaderboardType);
            if (choptMax is null or <= 0)
                continue;

            // 0.95× threshold matches solo ValidCutoffMultiplier default
            if (member.Score > (int)(choptMax.Value * 0.95))
            {
                entry.IsOverThreshold = true;
                return;
            }
        }
    }
}

/// <summary>Result of scraping one band leaderboard (one song + one band type).</summary>
internal sealed class BandLeaderboardScrapeResult
{
    public List<BandLeaderboardEntry> Entries { get; init; } = [];
    public int Requests { get; init; }
    public long BytesReceived { get; init; }
    public int ValidCount { get; init; }
    public int TotalPages { get; init; }
}

/// <summary>Aggregate result of the band scrape phase.</summary>
public sealed class BandScrapeResult
{
    public int TotalEntries { get; init; }
    public int TotalRequests { get; init; }
    public long TotalBytes { get; init; }
    public int SongsWithData { get; init; }
}
