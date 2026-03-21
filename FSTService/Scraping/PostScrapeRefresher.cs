using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// After each scrape pass, refreshes registered users' leaderboard entries that
/// were NOT seen in the scraped pages (stale or missing entries).
///
/// Two categories of entries are re-queried:
/// <list type="bullet">
///   <item><b>Gap entries</b> — songs the user has no entry for (scored below top 60K).</item>
///   <item><b>Stale entries</b> — songs the user HAS an entry for, but it wasn't
///         refreshed in this scrape pass (they fell out of the top 60K, so their
///         rank/score may be outdated).</item>
/// </list>
///
/// Uses the "seen set" from <see cref="GlobalLeaderboardPersistence.PipelineAggregates"/>
/// to efficiently determine which entries need re-querying.
/// </summary>
public class PostScrapeRefresher
{
    private readonly ILeaderboardQuerier _scraper;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly MetaDatabase _metaDb;
    private readonly ILogger<PostScrapeRefresher> _log;

    public PostScrapeRefresher(
        ILeaderboardQuerier scraper,
        GlobalLeaderboardPersistence persistence,
        ILogger<PostScrapeRefresher> log)
    {
        _scraper = scraper;
        _persistence = persistence;
        _metaDb = persistence.Meta;
        _log = log;
    }

    /// <summary>
    /// Refresh stale and missing entries for all registered users.
    /// </summary>
    /// <param name="registeredAccountIds">The set of registered account IDs.</param>
    /// <param name="seenEntries">Entries seen during this scrape pass.</param>
    /// <param name="chartedSongIds">All charted song IDs.</param>
    /// <param name="accessToken">Epic API access token.</param>
    /// <param name="callerAccountId">Caller's account ID for API requests.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total entries added or updated across all users.</returns>
    public virtual async Task<int> RefreshAllAsync(
        IReadOnlySet<string> registeredAccountIds,
        HashSet<(string AccountId, string SongId, string Instrument)> seenEntries,
        IReadOnlyList<string> chartedSongIds,
        string accessToken,
        string callerAccountId,
        int maxConcurrency = 10,
        CancellationToken ct = default)
    {
        if (registeredAccountIds.Count == 0) return 0;

        var instruments = GlobalLeaderboardScraper.AllInstruments;
        int totalUpdated = 0;

        int initialDop = Math.Max(1, maxConcurrency / 2);
        int maxDop = maxConcurrency;
        using var limiter = new AdaptiveConcurrencyLimiter(
            initialDop, minDop: 2, maxDop: maxDop, _log);

        _log.LogInformation(
            "Post-scrape refresh using adaptive concurrency: initial DOP={InitialDop}, max={MaxDop}.",
            initialDop, maxDop);

        foreach (var accountId in registeredAccountIds)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var updated = await RefreshAccountAsync(
                    accountId, seenEntries, chartedSongIds, instruments,
                    accessToken, callerAccountId, limiter, ct);
                totalUpdated += updated;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Post-scrape refresh failed for {AccountId}. Will retry next pass.", accountId);
            }
        }

        return totalUpdated;
    }

    /// <summary>
    /// Refresh stale/missing entries for a single account.
    /// </summary>
    private async Task<int> RefreshAccountAsync(
        string accountId,
        HashSet<(string AccountId, string SongId, string Instrument)> seenEntries,
        IReadOnlyList<string> chartedSongIds,
        IReadOnlyList<string> instruments,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter limiter,
        CancellationToken ct)
    {
        // Build the work list: pairs that are either missing or stale
        var workItems = new List<(string SongId, string Instrument, bool IsStale)>();

        foreach (var songId in chartedSongIds)
        {
            foreach (var instrument in instruments)
            {
                bool wasSeen = seenEntries.Contains((accountId, songId, instrument));
                if (wasSeen)
                    continue; // Entry was refreshed in the scrape — nothing to do

                var instrumentDb = _persistence.GetOrCreateInstrumentDb(instrument);
                var existing = instrumentDb.GetEntry(songId, accountId);

                if (existing is null)
                {
                    // Gap: user has no entry at all. Only query if the backfill
                    // previously found a score (BackfillProgress.EntryFound = 1),
                    // or if backfill hasn't run yet. Skip confirmed "no score" entries.
                    workItems.Add((songId, instrument, false));
                }
                else
                {
                    // Stale: we have an entry but it wasn't in the scrape pages
                    workItems.Add((songId, instrument, true));
                }
            }
        }

        if (workItems.Count == 0) return 0;

        _log.LogDebug(
            "Post-scrape refresh for {AccountId}: {Count} pairs to check ({Stale} stale, {Gap} gap).",
            accountId, workItems.Count,
            workItems.Count(w => w.IsStale),
            workItems.Count(w => !w.IsStale));

        int updated = 0;

        var tasks = workItems.Select(async item =>
        {
            await limiter.WaitAsync(ct);
            try
            {
                return await RefreshSingleEntryAsync(
                    accountId, item.SongId, item.Instrument, item.IsStale,
                    accessToken, callerAccountId, limiter, ct);
            }
            finally
            {
                limiter.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        updated += results.Count(r => r);

        if (updated > 0)
        {
            _log.LogInformation(
                "Post-scrape refresh for {AccountId}: updated {Updated} entries out of {Total} checked.",
                accountId, updated, workItems.Count);
        }

        return updated;
    }

    /// <summary>
    /// Re-query a single song/instrument for an account.
    /// Returns true if the entry was inserted or updated.
    /// </summary>
    private async Task<bool> RefreshSingleEntryAsync(
        string accountId,
        string songId,
        string instrument,
        bool isStale,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter limiter,
        CancellationToken ct)
    {
        LeaderboardEntry? entry;
        try
        {
            entry = await _scraper.LookupAccountAsync(
                songId, instrument, accountId, accessToken, callerAccountId, limiter, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Refresh lookup failed for {Account}/{Song}/{Instrument}.",
                accountId, songId, instrument);
            return false;
        }

        var instrumentDb = _persistence.GetOrCreateInstrumentDb(instrument);

        if (entry is null)
        {
            if (isStale)
            {
                // Entry was stale and now not found — very rare (ban, data reset)
                _log.LogWarning(
                    "Stale entry for {Account}/{Song}/{Instrument} returned null from API. Entry may be stale but keeping it.",
                    accountId, songId, instrument);
            }
            // Gap entry with no score — nothing to do
            return false;
        }

        // Check if the score changed (for stale entries)
        if (isStale)
        {
            var existing = instrumentDb.GetEntry(songId, accountId);
            if (existing is not null && existing.Score != entry.Score)
            {
                // Score actually changed while out of the scraped range
                _metaDb.InsertScoreChange(
                    songId, instrument, accountId,
                    existing.Score, entry.Score, existing.Rank, entry.Rank,
                    entry.Accuracy, entry.IsFullCombo, entry.Stars,
                    entry.Percentile, entry.Season, entry.EndTime,
                    allTimeRank: entry.Rank);
            }
        }
        else
        {
            // New entry discovered during gap check
            _metaDb.InsertScoreChange(
                songId, instrument, accountId,
                null, entry.Score, null, entry.Rank,
                entry.Accuracy, entry.IsFullCombo, entry.Stars,
                entry.Percentile, entry.Season, entry.EndTime,
                allTimeRank: entry.Rank);
        }

        // UPSERT the entry — ApiRank preserves the real Epic API rank
        entry.ApiRank = entry.Rank;
        instrumentDb.UpsertEntries(songId, [entry]);

        // Rank is a guaranteed population floor — if the user is ranked N,
        // there are at least N entries on this leaderboard.
        if (entry.Rank > 0)
            _metaDb.RaiseLeaderboardPopulationFloor(songId, instrument, entry.Rank);

        return true;
    }
}
