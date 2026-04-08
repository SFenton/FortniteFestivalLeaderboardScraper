using FortniteFestival.Core.Services;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Fills in missing leaderboard entries for a registered user by querying the
/// Epic API directly for songs/instruments where the user is below the top 60K
/// (and therefore absent from the global scrape).
///
/// Supports resumption — progress is tracked per song/instrument in
/// <c>BackfillProgress</c> so an interrupted run can pick up where it left off.
/// </summary>
public class ScoreBackfiller
{
    private readonly ILeaderboardQuerier _scraper;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IMetaDatabase _metaDb;
    private readonly ScrapeProgressTracker _progress;
    private readonly UserSyncProgressTracker _syncTracker;
    private readonly ILogger<ScoreBackfiller> _log;

    /// <summary>Save progress counters to the DB every N songs checked.</summary>
    private const int ProgressFlushInterval = 25;

    public ScoreBackfiller(
        ILeaderboardQuerier scraper,
        GlobalLeaderboardPersistence persistence,
        ScrapeProgressTracker progress,
        UserSyncProgressTracker syncTracker,
        ILogger<ScoreBackfiller> log)
    {
        _scraper = scraper;
        _persistence = persistence;
        _metaDb = persistence.Meta;
        _progress = progress;
        _syncTracker = syncTracker;
        _log = log;
    }

    /// <summary>
    /// Run a backfill for one account. Queries every charted song/instrument that
    /// the user doesn't already have an entry for (or that was previously checked
    /// and found empty). Resumes from where a prior interrupted run left off.
    /// </summary>
    /// <returns>Number of new entries found and inserted.</returns>
    public virtual async Task<int> BackfillAccountAsync(
        string accountId,
        FestivalService festivalService,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        int maxConcurrency = 10,
        CancellationToken ct = default)
    {
        // Gather the set of all charted songs
        var songIds = festivalService.Songs
            .Where(s => s.track?.su is not null)
            .Select(s => s.track.su!)
            .ToList();

        // Build song name lookup for progress display
        var songNames = festivalService.Songs
            .Where(s => s.track?.su is not null)
            .ToDictionary(s => s.track.su!, s => s.track.tt ?? "Unknown");

        var instruments = GlobalLeaderboardScraper.AllInstruments;

        // Total possible song/instrument pairs
        int totalPairs = songIds.Count * instruments.Count;

        // Ensure a BackfillStatus row exists
        _metaDb.EnqueueBackfill(accountId, totalPairs);
        _metaDb.StartBackfill(accountId);

        // Load already-checked pairs (for resumption)
        var alreadyChecked = _metaDb.GetCheckedBackfillPairs(accountId);

        // Build the work list: song/instrument pairs that still need checking
        var workItems = new List<(string SongId, string Instrument)>();
        foreach (var songId in songIds)
        {
            foreach (var instrument in instruments)
            {
                if (!alreadyChecked.Contains((songId, instrument)))
                    workItems.Add((songId, instrument));
            }
        }

        int songsChecked = alreadyChecked.Count;
        int entriesFound = 0;

        // Count existing entries found in prior partial runs
        var status = _metaDb.GetBackfillStatus(accountId);
        if (status is not null)
            entriesFound = status.EntriesFound;

        _log.LogInformation(
            "Backfill for {AccountId}: {Remaining} pairs remaining ({AlreadyDone} already checked, {Total} total).",
            accountId, workItems.Count, alreadyChecked.Count, totalPairs);

        if (workItems.Count == 0)
        {
            _metaDb.CompleteBackfill(accountId);
            _log.LogInformation("Backfill for {AccountId} already complete — nothing to do.", accountId);
            return 0;
        }

        _progress.AddPhaseItems(workItems.Count);
        _syncTracker.BeginBackfill(accountId, totalPairs);
        int newEntriesThisRun = 0;

        try
        {
            _progress.SetAdaptiveLimiter(pool.Limiter);

            var tasks = workItems.Select(async item =>
            {
                var lowToken = await pool.AcquireLowAsync(ct);
                try
                {
                    _progress.ReportPhaseRequest();
                    var found = await ProcessSingleLookupAsync(
                        accountId, item.SongId, item.Instrument,
                        accessToken, callerAccountId, pool.Limiter, ct);

                    // Per-item real-time progress via WebSocket
                    songNames.TryGetValue(item.SongId, out var songName);
                    _syncTracker.ReportBackfillItem(accountId, item.SongId, item.Instrument, found, songName);
                    return found;
                }
                finally
                {
                    pool.ReleaseLow(lowToken);
                    _progress.ReportPhaseItemComplete();
                }
            }).ToList();

            var results = await Task.WhenAll(tasks);

            foreach (var found in results)
            {
                songsChecked++;
                if (found)
                {
                    entriesFound++;
                    newEntriesThisRun++;
                    _progress.ReportPhaseEntryUpdated();
                }

                // Flush progress to DB periodically (crash recovery)
                if (songsChecked % ProgressFlushInterval == 0)
                {
                    _metaDb.UpdateBackfillProgress(accountId, songsChecked, entriesFound);
                }
            }

            _metaDb.UpdateBackfillProgress(accountId, songsChecked, entriesFound);
            _metaDb.CompleteBackfill(accountId);

            _log.LogInformation(
                "Backfill for {AccountId} complete. Checked {Checked}/{Total} pairs, found {Found} new entries.",
                accountId, songsChecked, totalPairs, newEntriesThisRun);
        }
        catch (OperationCanceledException)
        {
            _metaDb.UpdateBackfillProgress(accountId, songsChecked, entriesFound);
            _syncTracker.Error(accountId, "Backfill cancelled");
            _log.LogWarning("Backfill for {AccountId} cancelled at {Checked}/{Total}. Will resume next pass.",
                accountId, songsChecked, totalPairs);
            throw;
        }
        catch (Exception ex)
        {
            _metaDb.UpdateBackfillProgress(accountId, songsChecked, entriesFound);
            _metaDb.FailBackfill(accountId, ex.Message);
            _syncTracker.Error(accountId, ex.Message);
            _log.LogError(ex, "Backfill for {AccountId} failed at {Checked}/{Total}.",
                accountId, songsChecked, totalPairs);
            throw;
        }

        return newEntriesThisRun;
    }

    /// <summary>
    /// Look up a single song/instrument for an account. If the user has no
    /// existing entry in the instrument DB, queries the API. UPSERTs if found.
    /// </summary>
    private async Task<bool> ProcessSingleLookupAsync(
        string accountId,
        string songId,
        string instrument,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter limiter,
        CancellationToken ct)
    {
        // Check if we already have an entry in the instrument DB
        var instrumentDb = _persistence.GetOrCreateInstrumentDb(instrument);
        var existing = instrumentDb.GetEntry(songId, accountId);

        if (existing is not null && existing.Rank > 0 && existing.Percentile > 0)
        {
            // Already have an entry with valid rank AND percentile — no need to re-query
            _metaDb.MarkBackfillSongChecked(accountId, songId, instrument, entryFound: true);
            return false; // Not a "new" entry
        }

        // No entry, or existing entry lacks rank/percentile — query the V2 API + V1 neighborhood
        LeaderboardEntry? entry;
        List<LeaderboardEntry> neighbors;
        try
        {
            (entry, neighbors) = await _scraper.LookupAccountWithNeighborsAsync(
                songId, instrument, accountId, accessToken, callerAccountId,
                neighborRadius: 50, limiter, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "V2+V1 lookup failed for {Account}/{Song}/{Instrument}. Skipping.",
                accountId, songId, instrument);
            // Don't mark as checked so it retries next run
            return false;
        }

        if (entry is not null)
        {
            _log.LogDebug("V2 lookup result for {Account}/{Song}/{Instrument}: Rank={Rank}, Percentile={Percentile}, Score={Score}, Neighbors={NeighborCount}",
                accountId, songId, instrument, entry.Rank, entry.Percentile, entry.Score, neighbors.Count);
        }

        if (entry is null)
        {
            // User has no score on this song/instrument
            _metaDb.MarkBackfillSongChecked(accountId, songId, instrument, entryFound: existing is null ? false : true);
            return false;
        }

        // UPSERT the target entry (Source=backfill, ApiRank set by LookupAccountWithNeighborsAsync)
        entry.ApiRank = entry.Rank;
        entry.Source = "backfill";
        instrumentDb.UpsertEntries(songId, [entry]);

        // Persist neighbor entries (Source=neighbor, ApiRank set by FetchNeighborhoodPageAsync)
        if (neighbors.Count > 0)
            instrumentDb.UpsertEntries(songId, neighbors);

        // Rank is a guaranteed population floor — if the user is ranked N,
        // there are at least N entries on this leaderboard.
        // Also raise floor from the highest-ranked neighbor.
        int maxRank = entry.Rank;
        if (neighbors.Count > 0)
        {
            int maxNeighborRank = neighbors.Max(n => n.Rank);
            if (maxNeighborRank > maxRank) maxRank = maxNeighborRank;
        }
        if (maxRank > 0)
            _metaDb.RaiseLeaderboardPopulationFloor(songId, instrument, maxRank);

        if (existing is null)
        {
            // Truly new entry — record in ScoreHistory
            _metaDb.InsertScoreChange(
                songId, instrument, accountId,
                oldScore: null, newScore: entry.Score,
                oldRank: null, newRank: entry.Rank,
                accuracy: entry.Accuracy,
                isFullCombo: entry.IsFullCombo,
                stars: entry.Stars,
                percentile: entry.Percentile,
                season: entry.Season,
                scoreAchievedAt: entry.EndTime,
                allTimeRank: entry.Rank,
                difficulty: entry.Difficulty);
        }
        else if (entry.Difficulty >= 0)
        {
            // Backfill difficulty on existing ScoreHistory rows that are missing it.
            _metaDb.BackfillScoreHistoryDifficulty(
                accountId, songId, instrument, entry.Score, entry.Difficulty);
        }

        _metaDb.MarkBackfillSongChecked(accountId, songId, instrument, entryFound: true);

        return existing is null; // Only count truly new entries
    }
}
