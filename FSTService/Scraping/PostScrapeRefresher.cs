using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// After each scrape pass, refreshes registered users' leaderboard entries that
/// were NOT seen in the scraped pages (stale or missing entries).
///
/// Uses batched V2 POST lookups: one request per (song, instrument) serves all
/// registered users at once, with pagination for large batches. This reduces
/// API calls from O(users × songs × instruments) to O(songs × instruments × batches).
/// </summary>
public class PostScrapeRefresher
{
    private readonly ILeaderboardQuerier _scraper;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly MetaDatabase _metaDb;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger<PostScrapeRefresher> _log;

    public PostScrapeRefresher(
        ILeaderboardQuerier scraper,
        GlobalLeaderboardPersistence persistence,
        ScrapeProgressTracker progress,
        ILogger<PostScrapeRefresher> log)
    {
        _scraper = scraper;
        _persistence = persistence;
        _metaDb = persistence.Meta;
        _progress = progress;
        _log = log;
    }

    /// <summary>
    /// Refresh stale and missing entries for all registered users using batched lookups.
    /// Iterates per (song, instrument) and batches all registered users into a single
    /// V2 POST request (chunked if more than <paramref name="lookupBatchSize"/> users).
    /// </summary>
    public virtual async Task<int> RefreshAllAsync(
        IReadOnlySet<string> registeredAccountIds,
        HashSet<(string AccountId, string SongId, string Instrument)> seenEntries,
        IReadOnlyList<string> chartedSongIds,
        string accessToken,
        string callerAccountId,
        int maxConcurrency = 10,
        int lookupBatchSize = 500,
        CancellationToken ct = default)
    {
        if (registeredAccountIds.Count == 0) return 0;

        var instruments = GlobalLeaderboardScraper.AllInstruments;
        int totalLeaderboards = chartedSongIds.Count * instruments.Count;

        int initialDop = Math.Max(1, maxConcurrency / 2);
        int maxDop = maxConcurrency;
        using var limiter = new AdaptiveConcurrencyLimiter(
            initialDop, minDop: 2, maxDop: maxDop, _log);
        _progress.SetAdaptiveLimiter(limiter);
        _progress.BeginPhaseProgress(totalLeaderboards);

        _log.LogInformation(
            "Post-scrape refresh: {Songs} songs × {Instruments} instruments = {Total} leaderboards, "
            + "{Users} registered users, batch size {BatchSize}, DOP={InitialDop}→{MaxDop}.",
            chartedSongIds.Count, instruments.Count, totalLeaderboards,
            registeredAccountIds.Count, lookupBatchSize, initialDop, maxDop);

        int totalUpdated = 0;

        // Process each (song, instrument) leaderboard
        var tasks = new List<Task<int>>();

        foreach (var songId in chartedSongIds)
        {
            foreach (var instrument in instruments)
            {
                ct.ThrowIfCancellationRequested();

                tasks.Add(RefreshLeaderboardAsync(
                    songId, instrument, registeredAccountIds, seenEntries,
                    accessToken, callerAccountId, lookupBatchSize, limiter, ct));
            }
        }

        // Execute with concurrency control via the adaptive limiter
        // (each task acquires/releases the limiter internally)
        var results = await Task.WhenAll(tasks);
        totalUpdated = results.Sum();

        _progress.SetAdaptiveLimiter(null);

        if (totalUpdated > 0)
            _log.LogInformation("Post-scrape refresh complete: {Updated} entries updated.", totalUpdated);

        return totalUpdated;
    }

    /// <summary>
    /// Refresh all registered users for a single (song, instrument) leaderboard.
    /// Chunks users into batches and calls the batched V2 lookup for each chunk.
    /// </summary>
    private async Task<int> RefreshLeaderboardAsync(
        string songId,
        string instrument,
        IReadOnlySet<string> registeredAccountIds,
        HashSet<(string AccountId, string SongId, string Instrument)> seenEntries,
        string accessToken,
        string callerAccountId,
        int batchSize,
        AdaptiveConcurrencyLimiter limiter,
        CancellationToken ct)
    {
        // Collect users who need refresh for this (song, instrument)
        var needsRefresh = new List<(string AccountId, bool IsStale)>();
        foreach (var accountId in registeredAccountIds)
        {
            if (seenEntries.Contains((accountId, songId, instrument)))
                continue; // Was in the scrape pages — already fresh

            var instrumentDb = _persistence.GetOrCreateInstrumentDb(instrument);
            var existing = instrumentDb.GetEntry(songId, accountId);
            needsRefresh.Add((accountId, existing is not null));
        }

        if (needsRefresh.Count == 0)
        {
            _progress.ReportPhaseItemComplete();
            return 0;
        }

        int updated = 0;

        // Chunk into batches
        for (int offset = 0; offset < needsRefresh.Count; offset += batchSize)
        {
            var chunk = needsRefresh.GetRange(offset, Math.Min(batchSize, needsRefresh.Count - offset));
            var targetIds = chunk.Select(c => c.AccountId).ToList();
            var staleSet = new HashSet<string>(
                chunk.Where(c => c.IsStale).Select(c => c.AccountId),
                StringComparer.OrdinalIgnoreCase);

            await limiter.WaitAsync(ct);
            try
            {
                _progress.ReportPhaseRequest();

                List<LeaderboardEntry> entries;
                try
                {
                    entries = await _scraper.LookupMultipleAccountsAsync(
                        songId, instrument, targetIds,
                        accessToken, callerAccountId, limiter, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogDebug(ex, "Batch refresh failed for {Song}/{Instrument}.", songId, instrument);
                    continue;
                }

                // Process each returned entry
                foreach (var entry in entries)
                {
                    bool isStale = staleSet.Contains(entry.AccountId);
                    if (ProcessEntry(songId, instrument, entry, isStale))
                        updated++;
                }
            }
            finally
            {
                limiter.Release();
            }
        }

        _progress.ReportPhaseItemComplete();
        if (updated > 0)
            _progress.ReportPhaseEntryUpdated(updated);

        return updated;
    }

    /// <summary>
    /// Process a single returned entry: detect changes, record score history, upsert.
    /// Returns true if the entry was inserted or updated.
    /// </summary>
    private bool ProcessEntry(string songId, string instrument, LeaderboardEntry entry, bool isStale)
    {
        var instrumentDb = _persistence.GetOrCreateInstrumentDb(instrument);

        if (isStale)
        {
            var existing = instrumentDb.GetEntry(songId, entry.AccountId);
            if (existing is not null && existing.Score != entry.Score)
            {
                _metaDb.InsertScoreChange(
                    songId, instrument, entry.AccountId,
                    existing.Score, entry.Score, existing.Rank, entry.Rank,
                    entry.Accuracy, entry.IsFullCombo, entry.Stars,
                    entry.Percentile, entry.Season, entry.EndTime,
                    allTimeRank: entry.Rank);
            }
        }
        else
        {
            // New entry discovered
            _metaDb.InsertScoreChange(
                songId, instrument, entry.AccountId,
                null, entry.Score, null, entry.Rank,
                entry.Accuracy, entry.IsFullCombo, entry.Stars,
                entry.Percentile, entry.Season, entry.EndTime,
                allTimeRank: entry.Rank);
        }

        entry.ApiRank = entry.Rank;
        instrumentDb.UpsertEntries(songId, [entry]);

        if (entry.Rank > 0)
            _metaDb.RaiseLeaderboardPopulationFloor(songId, instrument, entry.Rank);

        return true;
    }

    // ─── Current-season session refresh ─────────────────────────

    /// <summary>
    /// Query the current season for all registered users to capture sub-optimal sessions
    /// (plays that didn't beat the all-time best) for the score history chart.
    /// Uses the same batched V2 POST approach as the alltime refresh.
    /// </summary>
    /// <returns>Number of new session history entries inserted.</returns>
    public virtual async Task<int> RefreshCurrentSeasonSessionsAsync(
        IReadOnlySet<string> registeredAccountIds,
        IReadOnlyList<string> chartedSongIds,
        string seasonPrefix,
        string accessToken,
        string callerAccountId,
        int maxConcurrency = 10,
        int lookupBatchSize = 500,
        CancellationToken ct = default)
    {
        if (registeredAccountIds.Count == 0) return 0;

        var instruments = GlobalLeaderboardScraper.AllInstruments;
        int totalLeaderboards = chartedSongIds.Count * instruments.Count;

        int initialDop = Math.Max(1, maxConcurrency / 2);
        int maxDop = maxConcurrency;
        using var limiter = new AdaptiveConcurrencyLimiter(
            initialDop, minDop: 2, maxDop: maxDop, _log);
        _progress.SetAdaptiveLimiter(limiter);
        _progress.BeginPhaseProgress(totalLeaderboards);

        _log.LogInformation(
            "Current-season session refresh ({Season}): {Total} leaderboards, {Users} users, batch {BatchSize}.",
            seasonPrefix, totalLeaderboards, registeredAccountIds.Count, lookupBatchSize);

        int totalNewSessions = 0;
        var targetIds = registeredAccountIds.ToList();

        var tasks = new List<Task<int>>();
        foreach (var songId in chartedSongIds)
        {
            foreach (var instrument in instruments)
            {
                ct.ThrowIfCancellationRequested();

                tasks.Add(RefreshSeasonalSessionsForLeaderboardAsync(
                    songId, instrument, seasonPrefix, targetIds,
                    accessToken, callerAccountId, lookupBatchSize, limiter, ct));
            }
        }

        var results = await Task.WhenAll(tasks);
        totalNewSessions = results.Sum();

        _progress.SetAdaptiveLimiter(null);

        if (totalNewSessions > 0)
            _log.LogInformation("Current-season session refresh: {New} new history entries.", totalNewSessions);

        return totalNewSessions;
    }

    /// <summary>
    /// Fetch all sessions for registered users on one (song, instrument) in the current season.
    /// Inserts new sessions into ScoreHistory, deduplicating by endTime.
    /// </summary>
    private async Task<int> RefreshSeasonalSessionsForLeaderboardAsync(
        string songId,
        string instrument,
        string seasonPrefix,
        IReadOnlyList<string> targetIds,
        string accessToken,
        string callerAccountId,
        int batchSize,
        AdaptiveConcurrencyLimiter limiter,
        CancellationToken ct)
    {
        int newSessions = 0;

        for (int offset = 0; offset < targetIds.Count; offset += batchSize)
        {
            var chunk = targetIds.Skip(offset).Take(batchSize).ToList();

            await limiter.WaitAsync(ct);
            try
            {
                _progress.ReportPhaseRequest();

                List<SessionHistoryEntry> sessions;
                try
                {
                    sessions = await _scraper.LookupMultipleAccountSessionsAsync(
                        songId, instrument, seasonPrefix, chunk,
                        accessToken, callerAccountId, limiter, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogDebug(ex, "Season session lookup failed for {Song}/{Instrument}/{Season}.",
                        songId, instrument, seasonPrefix);
                    continue;
                }

                // Insert new sessions into ScoreHistory
                foreach (var session in sessions)
                {
                    // ScoreHistory deduplicates via unique index on
                    // (AccountId, SongId, Instrument, NewScore, ScoreAchievedAt)
                    // so duplicate inserts are harmless (UPSERT via ON CONFLICT).
                    var instrumentDb = _persistence.GetOrCreateInstrumentDb(instrument);
                    var existing = instrumentDb.GetEntry(songId, session.AccountId);
                    int? oldScore = existing?.Score;

                    _metaDb.InsertScoreChange(
                        songId, instrument, session.AccountId,
                        oldScore, session.Score,
                        existing?.Rank, session.Rank,
                        session.Accuracy, session.IsFullCombo, session.Stars,
                        session.Percentile, session.Season, session.EndTime,
                        seasonRank: session.Rank);

                    newSessions++;
                }
            }
            finally
            {
                limiter.Release();
            }
        }

        _progress.ReportPhaseItemComplete();
        if (newSessions > 0)
            _progress.ReportPhaseEntryUpdated(newSessions);

        return newSessions;
    }
}
