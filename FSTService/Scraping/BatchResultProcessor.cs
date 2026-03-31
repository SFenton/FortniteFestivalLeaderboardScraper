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
    /// Batches all DB writes to reduce lock contention.
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

        // ── Collect score changes and prepare entries for batch upsert ──
        var scoreChanges = new List<ScoreChangeRecord>();
        var entriesToUpsert = new List<LeaderboardEntry>();
        long maxRank = 0;

        foreach (var entry in entries)
        {
            bool isStale = accountsWithExistingEntries.Contains(entry.AccountId);

            if (isStale)
            {
                var existing = instrumentDb.GetEntry(songId, entry.AccountId);
                if (existing is not null && existing.Score != entry.Score)
                {
                    scoreChanges.Add(new ScoreChangeRecord
                    {
                        SongId = songId, Instrument = instrument, AccountId = entry.AccountId,
                        OldScore = existing.Score, NewScore = entry.Score,
                        OldRank = existing.Rank, NewRank = entry.Rank,
                        Accuracy = entry.Accuracy, IsFullCombo = entry.IsFullCombo,
                        Stars = entry.Stars, Percentile = entry.Percentile,
                        Season = entry.Season, ScoreAchievedAt = entry.EndTime,
                        AllTimeRank = entry.Rank, Difficulty = entry.Difficulty,
                    });
                }
            }
            else
            {
                scoreChanges.Add(new ScoreChangeRecord
                {
                    SongId = songId, Instrument = instrument, AccountId = entry.AccountId,
                    OldScore = null, NewScore = entry.Score,
                    OldRank = null, NewRank = entry.Rank,
                    Accuracy = entry.Accuracy, IsFullCombo = entry.IsFullCombo,
                    Stars = entry.Stars, Percentile = entry.Percentile,
                    Season = entry.Season, ScoreAchievedAt = entry.EndTime,
                    AllTimeRank = entry.Rank, Difficulty = entry.Difficulty,
                });
            }

            entry.ApiRank = entry.Rank;
            entry.Source = "backfill";
            entriesToUpsert.Add(entry);

            if (entry.Rank > maxRank)
                maxRank = entry.Rank;
        }

        // ── Batch writes: 1 lock acquisition each instead of N ──
        if (scoreChanges.Count > 0)
            _metaDb.InsertScoreChanges(scoreChanges);

        instrumentDb.UpsertEntries(songId, entriesToUpsert);

        if (maxRank > 0)
            _metaDb.RaiseLeaderboardPopulationFloor(songId, instrument, maxRank);

        return entriesToUpsert.Count;
    }

    /// <summary>
    /// Process seasonal session results for one song/instrument.
    /// Inserts each session into ScoreHistory (deduplication handled by the
    /// unique index via ON CONFLICT / COALESCE).
    /// Batches all DB writes to reduce lock contention.
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

        // ── Collect score changes for batch insert ──
        var scoreChanges = new List<ScoreChangeRecord>(sessions.Count);

        foreach (var session in sessions)
        {
            var existing = instrumentDb.GetEntry(songId, session.AccountId);
            int? oldScore = existing?.Score;

            scoreChanges.Add(new ScoreChangeRecord
            {
                SongId = songId, Instrument = instrument, AccountId = session.AccountId,
                OldScore = oldScore, NewScore = session.Score,
                OldRank = existing?.Rank, NewRank = session.Rank,
                Accuracy = session.Accuracy, IsFullCombo = session.IsFullCombo,
                Stars = session.Stars, Percentile = session.Percentile,
                Season = season, ScoreAchievedAt = session.EndTime,
                SeasonRank = session.Rank, Difficulty = session.Difficulty,
            });
        }

        // ── Single batch write ──
        if (scoreChanges.Count > 0)
            _metaDb.InsertScoreChanges(scoreChanges);

        return scoreChanges.Count;
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
