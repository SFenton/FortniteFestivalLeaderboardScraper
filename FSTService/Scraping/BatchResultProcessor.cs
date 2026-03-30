using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Processes V2 batch lookup results: detects score changes, upserts entries to
/// instrument databases, inserts ScoreHistory rows, and raises population floors.
///
/// Consolidates logic previously duplicated across <see cref="PostScrapeRefresher"/>
/// and <see cref="HistoryReconstructor"/>.
/// </summary>
public class BatchResultProcessor
{
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IMetaDatabase _metaDb;
    private readonly ILogger<BatchResultProcessor> _log;

    public BatchResultProcessor(
        GlobalLeaderboardPersistence persistence,
        ILogger<BatchResultProcessor> log)
    {
        _persistence = persistence;
        _metaDb = persistence.Meta;
        _log = log;
    }

    /// <summary>
    /// Process alltime batch lookup results for one song/instrument.
    /// For each returned entry: detect changes vs existing data, upsert the
    /// instrument DB, insert ScoreHistory, and raise the population floor.
    /// </summary>
    /// <returns>Number of entries inserted or updated.</returns>
    public int ProcessAlltimeResults(
        string songId,
        string instrument,
        IReadOnlyList<LeaderboardEntry> entries,
        IReadOnlySet<string> accountsWithExistingEntries)
    {
        if (entries.Count == 0) return 0;

        var instrumentDb = _persistence.GetOrCreateInstrumentDb(instrument);
        int updated = 0;

        foreach (var entry in entries)
        {
            bool isStale = accountsWithExistingEntries.Contains(entry.AccountId);

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
                        allTimeRank: entry.Rank, difficulty: entry.Difficulty);
                }
            }
            else
            {
                // New entry discovered via backfill or post-scrape
                _metaDb.InsertScoreChange(
                    songId, instrument, entry.AccountId,
                    null, entry.Score, null, entry.Rank,
                    entry.Accuracy, entry.IsFullCombo, entry.Stars,
                    entry.Percentile, entry.Season, entry.EndTime,
                    allTimeRank: entry.Rank, difficulty: entry.Difficulty);
            }

            entry.ApiRank = entry.Rank;
            entry.Source = "backfill";
            instrumentDb.UpsertEntries(songId, [entry]);

            if (entry.Rank > 0)
                _metaDb.RaiseLeaderboardPopulationFloor(songId, instrument, entry.Rank);

            updated++;
        }

        return updated;
    }

    /// <summary>
    /// Process seasonal session results for one song/instrument.
    /// Inserts each session into ScoreHistory (deduplication handled by the
    /// unique index via ON CONFLICT / COALESCE).
    /// </summary>
    /// <returns>Number of session history entries inserted.</returns>
    public int ProcessSeasonalSessions(
        string songId,
        string instrument,
        int season,
        IReadOnlyList<SessionHistoryEntry> sessions)
    {
        if (sessions.Count == 0) return 0;

        var instrumentDb = _persistence.GetOrCreateInstrumentDb(instrument);
        int inserted = 0;

        foreach (var session in sessions)
        {
            var existing = instrumentDb.GetEntry(songId, session.AccountId);
            int? oldScore = existing?.Score;

            _metaDb.InsertScoreChange(
                songId, instrument, session.AccountId,
                oldScore, session.Score,
                existing?.Rank, session.Rank,
                session.Accuracy, session.IsFullCombo, session.Stars,
                session.Percentile, season, session.EndTime,
                seasonRank: session.Rank, difficulty: session.Difficulty);

            inserted++;
        }

        return inserted;
    }

    /// <summary>
    /// Mark a backfill song/instrument pair as checked for an account.
    /// </summary>
    public void MarkBackfillChecked(string accountId, string songId, string instrument, bool entryFound)
        => _metaDb.MarkBackfillSongChecked(accountId, songId, instrument, entryFound);

    /// <summary>
    /// Mark a history reconstruction song/instrument pair as processed for an account.
    /// </summary>
    public void MarkHistoryReconProcessed(string accountId, string songId, string instrument)
        => _metaDb.MarkHistoryReconSongProcessed(accountId, songId, instrument);
}
