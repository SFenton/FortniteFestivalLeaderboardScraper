using System.Collections.Concurrent;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Processes V2 batch lookup results: detects score changes, upserts entries to
/// instrument databases, inserts ScoreHistory rows, and raises population floors.
///
/// Consolidates logic previously duplicated across <see cref="PostScrapeRefresher"/>
/// and <see cref="HistoryReconstructor"/>.
///
/// When <see cref="SetStagingAccounts"/> is active, writes for designated accounts
/// are buffered in memory instead of hitting the DB. Call <see cref="FlushStagedData"/>
/// per account to atomically persist all buffered writes at once.
/// </summary>
public class BatchResultProcessor
{
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IMetaDatabase _metaDb;
    private readonly ILogger<BatchResultProcessor> _log;

    // ── Staging mode ─────────────────────────────────────────────
    private volatile HashSet<string>? _stagingAccountIds;
    private readonly ConcurrentDictionary<string, ConcurrentBag<(string Instrument, string SongId, LeaderboardEntry Entry)>> _stagedEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentBag<ScoreChangeRecord>> _stagedScoreChanges = new(StringComparer.OrdinalIgnoreCase);
    // Population floor raises are metadata-only (not user-visible); buffer and apply on flush.
    private readonly ConcurrentDictionary<string, ConcurrentBag<(string SongId, string Instrument, long MaxRank)>> _stagedPopulation = new(StringComparer.OrdinalIgnoreCase);

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
        // Split staged vs direct writes
        var directChanges = new List<ScoreChangeRecord>();
        var directEntries = new List<LeaderboardEntry>();
        long directMaxRank = 0;

        foreach (var change in scoreChanges)
        {
            if (IsStaged(change.AccountId))
                StageScoreChanges(change.AccountId, [change]);
            else
                directChanges.Add(change);
        }

        foreach (var entry in entriesToUpsert)
        {
            if (IsStaged(entry.AccountId))
            {
                StageEntry(entry.AccountId, instrument, songId, entry);
            }
            else
            {
                directEntries.Add(entry);
                if (entry.Rank > directMaxRank)
                    directMaxRank = entry.Rank;
            }
        }

        if (directChanges.Count > 0)
            _metaDb.InsertScoreChanges(directChanges);

        if (directEntries.Count > 0)
            instrumentDb.UpsertEntries(songId, directEntries);

        if (directMaxRank > 0)
            _metaDb.RaiseLeaderboardPopulationFloor(songId, instrument, directMaxRank);

        // Stage population floor for staged accounts (max rank across all staged entries)
        var stagedAccountsInBatch = entriesToUpsert
            .Where(e => IsStaged(e.AccountId))
            .GroupBy(e => e.AccountId, StringComparer.OrdinalIgnoreCase);
        foreach (var group in stagedAccountsInBatch)
        {
            var groupMaxRank = group.Max(e => e.Rank);
            if (groupMaxRank > 0)
                StagePopulation(group.Key, songId, instrument, groupMaxRank);
        }

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

        // ── Single batch write (split staged vs direct) ──
        if (scoreChanges.Count > 0)
        {
            var directChanges = new List<ScoreChangeRecord>();
            foreach (var change in scoreChanges)
            {
                if (IsStaged(change.AccountId))
                    StageScoreChanges(change.AccountId, [change]);
                else
                    directChanges.Add(change);
            }
            if (directChanges.Count > 0)
                _metaDb.InsertScoreChanges(directChanges);
        }

        return scoreChanges.Count;
    }

    /// <summary>
    /// Process seasonal sessions from ALL seasons for one song/instrument at once,
    /// computing chronologically correct OldScore (running personal best) per account.
    /// Used when <see cref="WorkPurpose.HistoryRecon"/> is active — unlike
    /// <see cref="ProcessSeasonalSessions"/> which only sees one season at a time, this
    /// method sorts all sessions by EndTime and tracks the running PB before each session.
    /// </summary>
    /// <returns>Number of session history entries inserted.</returns>
    public int ProcessAllSeasonalSessions(
        string songId,
        string instrument,
        Dictionary<int, List<SessionHistoryEntry>> sessionsBySeason)
    {
        if (sessionsBySeason.Count == 0) return 0;

        // Flatten all sessions into (Season, Session) pairs and group by account
        var byAccount = new Dictionary<string, List<(int Season, SessionHistoryEntry Session)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (season, sessions) in sessionsBySeason)
        {
            foreach (var session in sessions)
            {
                if (!byAccount.TryGetValue(session.AccountId, out var list))
                {
                    list = [];
                    byAccount[session.AccountId] = list;
                }
                list.Add((season, session));
            }
        }

        var scoreChanges = new List<ScoreChangeRecord>();

        foreach (var (accountId, entries) in byAccount)
        {
            // Sort by EndTime ascending (fall back to season number if EndTime is null),
            // matching HistoryReconstructor.ReconstructSongHistoryAsync logic exactly.
            entries.Sort((a, b) =>
            {
                if (a.Session.EndTime is not null && b.Session.EndTime is not null)
                    return string.Compare(a.Session.EndTime, b.Session.EndTime, StringComparison.Ordinal);
                return a.Season.CompareTo(b.Season);
            });

            // Track running personal best per account
            int? bestScore = null;
            int? bestRank = null;

            foreach (var (season, session) in entries)
            {
                scoreChanges.Add(new ScoreChangeRecord
                {
                    SongId = songId, Instrument = instrument, AccountId = accountId,
                    OldScore = bestScore, NewScore = session.Score,
                    OldRank = bestRank, NewRank = session.Rank,
                    Accuracy = session.Accuracy, IsFullCombo = session.IsFullCombo,
                    Stars = session.Stars, Percentile = session.Percentile,
                    Season = season, ScoreAchievedAt = session.EndTime,
                    SeasonRank = session.Rank, Difficulty = session.Difficulty,
                });

                if (bestScore is null || session.Score > bestScore)
                {
                    bestScore = session.Score;
                    bestRank = session.Rank;
                }
            }
        }

        if (scoreChanges.Count > 0)
        {
            var directChanges = new List<ScoreChangeRecord>();
            foreach (var change in scoreChanges)
            {
                if (IsStaged(change.AccountId))
                    StageScoreChanges(change.AccountId, [change]);
                else
                    directChanges.Add(change);
            }
            if (directChanges.Count > 0)
                _metaDb.InsertScoreChanges(directChanges);
        }

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

    // ══════════════════════════════════════════════════════════════
    // Staging mode — buffer writes for designated accounts
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Enable staging mode for the given accounts. All DB writes for these
    /// accounts will be buffered in memory until <see cref="FlushStagedData"/>
    /// is called. Thread-safe: concurrent calls to Process* methods are safe.
    /// </summary>
    public void SetStagingAccounts(IReadOnlyCollection<string> accountIds)
    {
        _stagingAccountIds = new HashSet<string>(accountIds, StringComparer.OrdinalIgnoreCase);
        _log.LogInformation("Staging mode enabled for {Count} accounts.", accountIds.Count);
    }

    /// <summary>Disable staging mode. Does NOT flush buffered data.</summary>
    public void ClearStagingAccounts()
    {
        _stagingAccountIds = null;
    }

    /// <summary>Returns true if the account is in staging mode.</summary>
    private bool IsStaged(string accountId) => _stagingAccountIds?.Contains(accountId) == true;

    /// <summary>
    /// Flush all buffered writes for the given account to the database.
    /// Leaderboard entry upserts and score history inserts are written in
    /// rapid succession so the data becomes visible near-atomically.
    /// Clears the buffers for this account afterward.
    /// </summary>
    public void FlushStagedData(string accountId)
    {
        // ── Leaderboard entries ──────────────────────────────────
        if (_stagedEntries.TryRemove(accountId, out var entryBag))
        {
            // Group by (instrument, songId) for batch upsert
            var byKey = new Dictionary<(string Instrument, string SongId), List<LeaderboardEntry>>();
            foreach (var (instrument, songId, entry) in entryBag)
            {
                var key = (instrument, songId);
                if (!byKey.TryGetValue(key, out var list))
                {
                    list = [];
                    byKey[key] = list;
                }
                list.Add(entry);
            }

            foreach (var ((instrument, songId), entries) in byKey)
            {
                var instrumentDb = _persistence.GetOrCreateInstrumentDb(instrument);
                instrumentDb.UpsertEntries(songId, entries);
            }
        }

        // ── Score history ────────────────────────────────────────
        if (_stagedScoreChanges.TryRemove(accountId, out var changeBag))
        {
            var changes = changeBag.ToList();
            if (changes.Count > 0)
                _metaDb.InsertScoreChanges(changes);
        }

        // ── Population floors ────────────────────────────────────
        if (_stagedPopulation.TryRemove(accountId, out var popBag))
        {
            foreach (var (songId, instrument, maxRank) in popBag)
                _metaDb.RaiseLeaderboardPopulationFloor(songId, instrument, maxRank);
        }

        _log.LogDebug("Flushed staged data for account {AccountId}.", accountId);
    }

    // ── Private staging helpers ──────────────────────────────────

    private void StageEntry(string accountId, string instrument, string songId, LeaderboardEntry entry)
    {
        var bag = _stagedEntries.GetOrAdd(accountId, _ => []);
        bag.Add((instrument, songId, entry));
    }

    private void StageScoreChanges(string accountId, IReadOnlyList<ScoreChangeRecord> changes)
    {
        var bag = _stagedScoreChanges.GetOrAdd(accountId, _ => []);
        foreach (var change in changes)
            bag.Add(change);
    }

    private void StagePopulation(string accountId, string songId, string instrument, long maxRank)
    {
        var bag = _stagedPopulation.GetOrAdd(accountId, _ => []);
        bag.Add((songId, instrument, maxRank));
    }
}
