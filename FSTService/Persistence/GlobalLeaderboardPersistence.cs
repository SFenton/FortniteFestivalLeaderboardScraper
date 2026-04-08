using System.Collections.Concurrent;
using System.Threading.Channels;
using FSTService.Scraping;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Persistence;

/// <summary>
/// Coordinates the per-instrument databases and the central meta DB.
/// This is the single entry point that <see cref="ScraperWorker"/> uses to
/// persist global leaderboard results.
///
/// During a scrape pass, persistence is fully pipelined via per-instrument
/// <see cref="Channel{T}"/> writers.  Each of the 6 instruments has its own
/// dedicated writer task — zero cross-instrument contention.
/// </summary>
public sealed class GlobalLeaderboardPersistence : IDisposable
{
    private readonly Dictionary<string, IInstrumentDatabase> _instrumentDbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMetaDatabase _metaDb;
    private readonly ILogger<GlobalLeaderboardPersistence> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly NpgsqlDataSource _pgDataSource;
    private readonly FeatureOptions _features;

    /// <summary>The meta database (ScrapeLog, ScoreHistory, etc.).</summary>
    public IMetaDatabase Meta => _metaDb;

    public GlobalLeaderboardPersistence(IMetaDatabase metaDb,
                                        ILoggerFactory loggerFactory,
                                        ILogger<GlobalLeaderboardPersistence> log,
                                        NpgsqlDataSource pgDataSource,
                                        IOptions<FeatureOptions> features)
    {
        _metaDb = metaDb;
        _loggerFactory = loggerFactory;
        _log = log;
        _pgDataSource = pgDataSource;
        _features = features.Value;
    }

    /// <summary>
    /// Ensure all schemas exist (meta DB + one instrument DB per known instrument).
    /// Call once at startup before the first scrape pass.
    /// </summary>
    public void Initialize()
    {
        _metaDb.EnsureSchema();

        var instruments = GlobalLeaderboardScraper.AllInstruments;

        foreach (var instrument in instruments)
        {
            var db = new InstrumentDatabase(
                instrument, _pgDataSource,
                _loggerFactory.CreateLogger<InstrumentDatabase>())
            { UseTiers = _features.UseRankingDeltaTiers };
            _instrumentDbs[instrument] = db;
            _log.LogDebug("Opened PG instrument DB: {Instrument}", instrument);
        }

        _log.LogInformation("GlobalLeaderboardPersistence initialized. " +
                            "{InstrumentCount} instruments.",
                            _instrumentDbs.Count);
    }

    /// <summary>
    /// Check if all databases are initialized and queryable.
    /// Used by the /readyz endpoint.
    /// </summary>
    public bool IsReady()
    {
        try
        {
            if (_instrumentDbs.Count == 0) return false;
            // Quick probe: verify each DB can execute a trivial query
            foreach (var db in _instrumentDbs.Values)
                db.GetTotalEntryCount();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get (or create on first access) the <see cref="IInstrumentDatabase"/>
    /// for a given instrument key (e.g. "Solo_Guitar").
    /// New instruments added in the future are automatically handled.
    /// </summary>
    public IInstrumentDatabase GetOrCreateInstrumentDb(string instrument)
    {
        if (_instrumentDbs.TryGetValue(instrument, out var db))
            return db;

        db = new InstrumentDatabase(
            instrument, _pgDataSource,
            _loggerFactory.CreateLogger<InstrumentDatabase>())
        { UseTiers = _features.UseRankingDeltaTiers };

        _instrumentDbs[instrument] = db;
        return db;
    }

    /// <summary>
    /// Persist a single <see cref="GlobalLeaderboardResult"/> (one song + one instrument)
    /// by UPSERTing into the correct instrument DB. Optionally detects score changes
    /// for registered users.
    /// </summary>
    /// <returns>
    /// The number of rows affected and the set of account IDs seen in this result.
    /// </returns>
    public PersistResult PersistResult(GlobalLeaderboardResult result,
                                       IReadOnlySet<string>? registeredAccountIds = null,
                                       (NpgsqlConnection Conn, NpgsqlTransaction Tx)? pgConnTx = null)
    {
        var db = GetOrCreateInstrumentDb(result.Instrument);

        // ── Pre-UPSERT: snapshot registered users' current state for change detection ──
        Dictionary<string, LeaderboardEntry>? previousState = null;
        if (registeredAccountIds is { Count: > 0 })
        {
            // Collect which registered users appear in this result
            var relevantIds = new List<string>();
            foreach (var entry in result.Entries)
            {
                if (registeredAccountIds.Contains(entry.AccountId))
                    relevantIds.Add(entry.AccountId);
            }

            // Single batch query instead of N individual GetEntry() calls
            if (relevantIds.Count > 0)
                previousState = db.GetEntriesForAccounts(result.SongId, relevantIds);
            else
                previousState = new Dictionary<string, LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
        }

        var rowsAffected = pgConnTx is not null && db is InstrumentDatabase pgDb
            ? pgDb.UpsertEntries(result.SongId, result.Entries, pgConnTx.Value.Conn, pgConnTx.Value.Tx)
            : db.UpsertEntries(result.SongId, result.Entries);
        bool hasNewEntries = rowsAffected > 0 && result.Entries.Count > 0;

        // ── Post-UPSERT: detect score changes for registered users ──
        var changedAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scoreChanges = new List<ScoreChangeRecord>();
        if (previousState is not null)
        {
            foreach (var entry in result.Entries)
            {
                if (!registeredAccountIds!.Contains(entry.AccountId))
                    continue;

                if (previousState.TryGetValue(entry.AccountId, out var prev))
                {
                    // Existing entry — check if score actually changed
                    if (entry.Score != prev.Score)
                    {
                        scoreChanges.Add(new ScoreChangeRecord
                        {
                            SongId = result.SongId, Instrument = result.Instrument,
                            AccountId = entry.AccountId,
                            OldScore = prev.Score, NewScore = entry.Score,
                            OldRank = prev.Rank, NewRank = entry.Rank,
                            Accuracy = entry.Accuracy, IsFullCombo = entry.IsFullCombo,
                            Stars = entry.Stars, Percentile = entry.Percentile,
                            Season = entry.Season, ScoreAchievedAt = entry.EndTime,
                            AllTimeRank = entry.Rank, Difficulty = entry.Difficulty,
                        });
                        changedAccountIds.Add(entry.AccountId);
                    }
                }
                else
                {
                    // New entry for a registered user — record as a new score
                    scoreChanges.Add(new ScoreChangeRecord
                    {
                        SongId = result.SongId, Instrument = result.Instrument,
                        AccountId = entry.AccountId,
                        OldScore = null, NewScore = entry.Score,
                        OldRank = null, NewRank = entry.Rank,
                        Accuracy = entry.Accuracy, IsFullCombo = entry.IsFullCombo,
                        Stars = entry.Stars, Percentile = entry.Percentile,
                        Season = entry.Season, ScoreAchievedAt = entry.EndTime,
                        AllTimeRank = entry.Rank, Difficulty = entry.Difficulty,
                    });
                    changedAccountIds.Add(entry.AccountId);
                }
            }

            // Batch-insert all score changes in one transaction
            if (scoreChanges.Count > 0)
                _metaDb.InsertScoreChanges(scoreChanges);
        }

        // Persist account IDs to meta DB so the name resolver can
        // pick them up independently (survives crashes, enables --resolve-only).
        // When running pipelined (writers active), defer to bulk flush after drain.
        if (_aggregates is not null)
            _aggregates.AddDeferredAccountIds(result.Entries.Select(e => e.AccountId));
        else
            _metaDb.InsertAccountIds(result.Entries.Select(e => e.AccountId));

        return new PersistResult
        {
            RowsAffected = rowsAffected,
            ScoreChangesDetected = scoreChanges.Count,
            ChangedAccountIds = changedAccountIds,
            HasNewEntries = hasNewEntries,
        };
    }

    /// <summary>
    /// Get total entry counts across all instrument DBs (for status reporting).
    /// </summary>
    public Dictionary<string, long> GetEntryCounts()
    {
        var counts = new Dictionary<string, long>(_instrumentDbs.Count);
        foreach (var (instrument, db) in _instrumentDbs)
            counts[instrument] = db.GetTotalEntryCount();
        return counts;
    }

    // ─── Channel-based pipelined persistence ────────────────────

    /// <summary>Work item for the per-instrument writer channels.</summary>
    public sealed class PersistWorkItem
    {
        public required GlobalLeaderboardResult Result { get; init; }
        public IReadOnlySet<string>? RegisteredAccountIds { get; init; }
    }

    /// <summary>Aggregate counters collected during a pipelined scrape pass.</summary>
    public sealed class PipelineAggregates
    {
        private int _totalEntries;
        private int _totalChanges;
        private int _songsWithData;
        private readonly ConcurrentHashSet _changedAccountIds = new();
        private readonly ConcurrentDictionary<(string AccountId, string SongId, string Instrument), byte>
            _seenRegisteredEntries = new();
        private readonly ConcurrentDictionary<string, byte> _deferredAccountIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _changedSongIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _rankChangedSongIds = new(StringComparer.OrdinalIgnoreCase);

        public int TotalEntries => _totalEntries;
        public int TotalChanges => _totalChanges;
        public int SongsWithData => _songsWithData;
        public IReadOnlyCollection<string> ChangedAccountIds => _changedAccountIds;

        /// <summary>
        /// All (AccountId, SongId, Instrument) tuples for registered users whose entries
        /// were present in the scraped pages this pass. Used by post-scrape refresh to
        /// identify stale entries that need re-querying.
        /// </summary>
        public IReadOnlyCollection<(string AccountId, string SongId, string Instrument)>
            SeenRegisteredEntries => _seenRegisteredEntries.Keys.ToArray();

        /// <summary>Account IDs accumulated during scrape for bulk flush after drain.</summary>
        public ICollection<string> DeferredAccountIds => _deferredAccountIds.Keys;

        /// <summary>Song IDs where entries were inserted or scores changed during this pass.</summary>
        public IReadOnlyCollection<string> ChangedSongIds => _changedSongIds.Keys.ToArray();

        /// <summary>Song IDs where scores changed, requiring rank recomputation.</summary>
        public IReadOnlyCollection<string> RankChangedSongIds => _rankChangedSongIds.Keys.ToArray();

        public void AddEntries(int count) => Interlocked.Add(ref _totalEntries, count);
        public void AddChanges(int count) => Interlocked.Add(ref _totalChanges, count);
        public void IncrementSongsWithData() => Interlocked.Increment(ref _songsWithData);
        public void AddChangedAccountIds(IEnumerable<string> ids) => _changedAccountIds.AddRange(ids);

        /// <summary>Accumulate account IDs for a deferred bulk write after drain.</summary>
        public void AddDeferredAccountIds(IEnumerable<string> ids)
        {
            foreach (var id in ids) _deferredAccountIds.TryAdd(id, 0);
        }

        /// <summary>Record which registered user entries were seen in this pass.</summary>
        public void AddSeenRegisteredEntries(IEnumerable<(string, string, string)> entries)
        {
            foreach (var e in entries) _seenRegisteredEntries.TryAdd(e, 0);
        }

        /// <summary>Mark a song as having data changes (new entries or score changes).</summary>
        public void AddChangedSongId(string songId) => _changedSongIds.TryAdd(songId, 0);

        /// <summary>Mark a song as having score changes that require rank recomputation.</summary>
        public void AddRankChangedSongId(string songId) => _rankChangedSongIds.TryAdd(songId, 0);

        /// <summary>Thread-safe HashSet built on ConcurrentDictionary.</summary>
        private sealed class ConcurrentHashSet : IReadOnlyCollection<string>
        {
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _dict = new(StringComparer.OrdinalIgnoreCase);
            public int Count => _dict.Count;
            public void AddRange(IEnumerable<string> items) { foreach (var item in items) _dict.TryAdd(item, 0); }
            public IEnumerator<string> GetEnumerator() => _dict.Keys.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    private Dictionary<string, Channel<PersistWorkItem>>? _channels;
    private List<Task>? _writerTasks;
    private PipelineAggregates? _aggregates;

    /// <summary>
    /// Start per-instrument writer tasks.  Call once before the scrape loop begins.
    /// Each instrument gets a bounded channel and a dedicated writer task.
    /// When PostgreSQL is the backend, multiple work items are batched into a single
    /// transaction to amortize commit overhead (biggest throughput multiplier).
    /// </summary>
    /// <param name="channelCapacity">Per-instrument channel capacity (default 128).</param>
    /// <param name="writeBatchSize">Max work items per PG transaction (default 10).</param>
    public PipelineAggregates StartWriters(int channelCapacity = 128, int writeBatchSize = 10, CancellationToken ct = default)
    {
        _aggregates = new PipelineAggregates();
        _channels = new Dictionary<string, Channel<PersistWorkItem>>(StringComparer.OrdinalIgnoreCase);
        _writerTasks = new List<Task>();

        foreach (var instrument in _instrumentDbs.Keys)
        {
            var channel = Channel.CreateBounded<PersistWorkItem>(new BoundedChannelOptions(channelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _channels[instrument] = channel;

            var db = _instrumentDbs[instrument];
            var agg = _aggregates;
            var task = Task.Run(async () =>
            {
                await RunBatchedWriterAsync(channel.Reader, db, agg, writeBatchSize, ct);
            }, ct);

            _writerTasks.Add(task);
        }

        _log.LogInformation("Started {Count} per-instrument writer tasks (batch size: {BatchSize}).",
            _writerTasks.Count, writeBatchSize);
        return _aggregates;
    }

    /// <summary>
    /// Batched writer: drains up to <paramref name="batchSize"/> items from the channel
    /// and processes them in a single PG transaction, amortizing commit overhead.
    /// </summary>
    private async Task RunBatchedWriterAsync(ChannelReader<PersistWorkItem> reader,
                                             IInstrumentDatabase db, PipelineAggregates agg,
                                             int batchSize, CancellationToken ct)
    {
        var pgDb = (InstrumentDatabase)db;
        var batch = new List<PersistWorkItem>(batchSize);

        while (await reader.WaitToReadAsync(ct))
        {
            // Drain up to batchSize items without blocking
            batch.Clear();
            while (batch.Count < batchSize && reader.TryRead(out var item))
                batch.Add(item);

            if (batch.Count == 0) continue;

            try
            {
                using var conn = pgDb.DataSource.OpenConnection();
                using var tx = conn.BeginTransaction();

                // Disable synchronous WAL flush for the entire batch transaction
                using (var sc = conn.CreateCommand()) { sc.Transaction = tx; sc.CommandText = "SET LOCAL synchronous_commit = off"; sc.ExecuteNonQuery(); }

                foreach (var item in batch)
                {
                    try
                    {
                        ProcessWorkItem(item, db, agg, (conn, tx));
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Writer error for {Instrument}/{SongId} (in batch)",
                            item.Result.Instrument, item.Result.SongId);
                    }
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Batch commit failed for {Instrument} ({Count} items). Data will be retried next pass.",
                    db.Instrument, batch.Count);
            }
        }
    }

    /// <summary>Processes a single work item: upsert, change detection, aggregate tracking.</summary>
    private void ProcessWorkItem(PersistWorkItem item, IInstrumentDatabase db,
                                  PipelineAggregates agg,
                                  (NpgsqlConnection Conn, NpgsqlTransaction Tx)? pgConnTx)
    {
        var persistResult = PersistResult(item.Result, item.RegisteredAccountIds, pgConnTx);
        agg.AddChangedAccountIds(persistResult.ChangedAccountIds);
        agg.AddEntries(item.Result.Entries.Count);
        agg.AddChanges(persistResult.ScoreChangesDetected);
        if (persistResult.HasNewEntries || persistResult.ScoreChangesDetected > 0)
            agg.AddChangedSongId(item.Result.SongId);

        // Track which registered users were seen in this result
        if (item.RegisteredAccountIds is { Count: > 0 })
        {
            var seen = item.Result.Entries
                .Where(e => item.RegisteredAccountIds.Contains(e.AccountId))
                .Select(e => (e.AccountId, item.Result.SongId, item.Result.Instrument));
            agg.AddSeenRegisteredEntries(seen);
        }
    }

    /// <summary>
    /// Enqueue a single instrument result for asynchronous persistence.
    /// Non-blocking unless the channel is full (capacity 128), in which case
    /// it applies back-pressure to the caller — naturally throttling the
    /// scraper when persistence can't keep up.
    /// </summary>
    public async ValueTask EnqueueResultAsync(GlobalLeaderboardResult result,
                                               IReadOnlySet<string>? registeredAccountIds,
                                               CancellationToken ct = default)
    {
        if (_channels is null)
            throw new InvalidOperationException("Writers not started. Call StartWriters() first.");

        if (!_channels.TryGetValue(result.Instrument, out var channel))
        {
            _log.LogWarning("No writer channel for instrument {Instrument}. Dropping result.", result.Instrument);
            return;
        }

        await channel.Writer.WriteAsync(new PersistWorkItem
        {
            Result = result,
            RegisteredAccountIds = registeredAccountIds,
        }, ct);
    }

    /// <summary>
    /// Signal all writers that no more items will arrive, then wait for them
    /// to drain.  Call after the scrape loop completes.
    /// </summary>
    public async Task DrainWritersAsync()
    {
        if (_channels is null || _writerTasks is null) return;

        // Signal completion on all channels
        foreach (var channel in _channels.Values)
            channel.Writer.TryComplete();

        // Wait for all writer tasks to finish draining
        await Task.WhenAll(_writerTasks);

        _log.LogInformation("All per-instrument writers drained.");
        _channels = null;
        _writerTasks = null;
    }

    /// <summary>
    /// Flush deferred account IDs accumulated during pipelined persistence.
    /// Call once after <see cref="DrainWritersAsync"/>.
    /// </summary>
    public int FlushDeferredAccountIds()
    {
        if (_aggregates is null) return 0;
        var ids = _aggregates.DeferredAccountIds;
        if (ids.Count == 0) return 0;

        try
        {
            var inserted = _metaDb.InsertAccountIds(ids);
            _log.LogInformation("Flushed {Inserted}/{Total} deferred account IDs to meta DB.", inserted, ids.Count);
            return inserted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to flush {Count} deferred account IDs. They will be re-encountered next scrape pass.", ids.Count);
            return 0;
        }
    }

    /// <summary>
    /// Run WAL checkpoints on all instrument databases and the meta database.

    // ─── Staged leaderboard finalization ────────────────────────────

    /// <summary>
    /// Finalize ALL staged leaderboards for one instrument in a single pass.
    /// Processes all songs at once instead of looping per-song, reducing DB round-trips
    /// from ~9 per song (thousands total) to ~5-7 per instrument (tens total).
    /// </summary>
    /// <returns>Number of rows merged and score changes detected.</returns>
    public (int RowsMerged, int ScoreChanges, IReadOnlySet<string> AffectedSongIds) FinalizeInstrumentFromStaging(
        long scrapeId, string instrument,
        IReadOnlySet<string>? registeredAccountIds = null, int wave = 1)
    {
        var db = GetOrCreateInstrumentDb(instrument);
        var pgDb = (InstrumentDatabase)db;

        // ── Pre-merge: snapshot registered users across ALL songs for this instrument ──
        Dictionary<(string SongId, string AccountId), LeaderboardEntry>? previousState = null;
        List<string>? relevantIds = null;
        if (registeredAccountIds is { Count: > 0 })
        {
            // Find which registered users appear in the staged data for this instrument
            relevantIds = new List<string>();
            using (var conn = _pgDataSource!.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                var paramNames = new string[registeredAccountIds.Count];
                int i = 0;
                foreach (var id in registeredAccountIds)
                {
                    paramNames[i] = $"@a{i}";
                    cmd.Parameters.AddWithValue($"a{i}", id);
                    i++;
                }
                cmd.CommandText =
                    $"SELECT DISTINCT account_id FROM leaderboard_staging " +
                    $"WHERE scrape_id = @scrapeId AND instrument = @instrument " +
                    $"AND account_id IN ({string.Join(",", paramNames)})";
                cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
                cmd.Parameters.AddWithValue("instrument", instrument);
                using var r = cmd.ExecuteReader();
                while (r.Read()) relevantIds.Add(r.GetString(0));
            }

            if (relevantIds.Count > 0)
                previousState = pgDb.GetAllEntriesForAccounts(relevantIds);
            else
                previousState = new();
        }

        // ── Merge staged rows for this instrument into the live table (batched by song) ──
        // Processing all ~5M+ rows in a single INSERT overwhelms PG with WAL writes
        // and exceeds command timeouts. Batching by groups of songs keeps each
        // transaction manageable (~10K rows/song × batch_size).
        int rowsMerged = 0;
        const int songBatchSize = 100;

        List<string> stagedSongIds;
        using (var conn = pgDb.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT DISTINCT song_id FROM leaderboard_staging " +
                "WHERE scrape_id = @scrapeId AND instrument = @instrument";
            cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
            cmd.Parameters.AddWithValue("instrument", instrument);
            stagedSongIds = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) stagedSongIds.Add(r.GetString(0));
        }

        const string mergeSql =
            "INSERT INTO leaderboard_entries (song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, first_seen_at, last_updated_at) " +
            "SELECT DISTINCT ON (song_id, instrument, account_id) song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, staged_at, staged_at " +
            "FROM leaderboard_staging " +
            "WHERE scrape_id = @scrapeId AND instrument = @instrument AND song_id = ANY(@songIds) " +
            "ORDER BY song_id, instrument, account_id, score DESC " +
            "ON CONFLICT(song_id, instrument, account_id) DO UPDATE SET " +
            "score = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.score ELSE leaderboard_entries.score END, " +
            "accuracy = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.accuracy ELSE leaderboard_entries.accuracy END, " +
            "is_full_combo = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.is_full_combo ELSE leaderboard_entries.is_full_combo END, " +
            "stars = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.stars ELSE leaderboard_entries.stars END, " +
            "season = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.season ELSE leaderboard_entries.season END, " +
            "difficulty = CASE WHEN EXCLUDED.difficulty >= 0 AND leaderboard_entries.difficulty < 0 THEN EXCLUDED.difficulty WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.difficulty ELSE leaderboard_entries.difficulty END, " +
            "percentile = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.percentile WHEN EXCLUDED.percentile > 0 AND leaderboard_entries.percentile <= 0 THEN EXCLUDED.percentile ELSE leaderboard_entries.percentile END, " +
            "rank = CASE WHEN EXCLUDED.rank > 0 THEN EXCLUDED.rank ELSE leaderboard_entries.rank END, " +
            "api_rank = CASE WHEN EXCLUDED.api_rank > 0 THEN EXCLUDED.api_rank ELSE leaderboard_entries.api_rank END, " +
            "source = CASE WHEN leaderboard_entries.source = 'scrape' THEN 'scrape' WHEN EXCLUDED.source = 'scrape' THEN 'scrape' WHEN leaderboard_entries.source = 'backfill' THEN 'backfill' WHEN EXCLUDED.source = 'backfill' THEN 'backfill' ELSE EXCLUDED.source END, " +
            "end_time = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.end_time ELSE leaderboard_entries.end_time END, " +
            "last_updated_at = EXCLUDED.last_updated_at " +
            "WHERE EXCLUDED.score != leaderboard_entries.score " +
            "OR (EXCLUDED.rank > 0 AND EXCLUDED.rank != leaderboard_entries.rank) " +
            "OR (EXCLUDED.api_rank > 0 AND EXCLUDED.api_rank != leaderboard_entries.api_rank) " +
            "OR (EXCLUDED.difficulty >= 0 AND leaderboard_entries.difficulty < 0) " +
            "OR (EXCLUDED.percentile > 0 AND leaderboard_entries.percentile <= 0) " +
            "OR (leaderboard_entries.source NOT IN ('scrape','backfill') AND EXCLUDED.source IN ('scrape','backfill')) " +
            "RETURNING song_id";

        var affectedSongIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int batchStart = 0; batchStart < stagedSongIds.Count; batchStart += songBatchSize)
        {
            var batchIds = stagedSongIds.GetRange(batchStart, Math.Min(songBatchSize, stagedSongIds.Count - batchStart));

            using var conn = pgDb.DataSource.OpenConnection();
            using var tx = conn.BeginTransaction();

            using (var sc = conn.CreateCommand()) { sc.Transaction = tx; sc.CommandText = "SET LOCAL synchronous_commit = off"; sc.ExecuteNonQuery(); }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandTimeout = 300; // 5 min — each batch is ~100 songs (~1M rows)
                cmd.CommandText = mergeSql;
                cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
                cmd.Parameters.AddWithValue("instrument", instrument);
                cmd.Parameters.AddWithValue("songIds", batchIds.ToArray());
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    rowsMerged++;
                    affectedSongIds.Add(r.GetString(0));
                }
            }

            tx.Commit();
        }

        // ── Post-merge: detect score changes for registered users ──
        int scoreChanges = 0;
        if (previousState is not null && relevantIds is { Count: > 0 })
        {
            var changes = new List<ScoreChangeRecord>();
            var currentState = pgDb.GetAllEntriesForAccounts(relevantIds);

            foreach (var ((songId, accountId), prev) in previousState)
            {
                if (!currentState.TryGetValue((songId, accountId), out var current)) continue;
                if (current.Score == prev.Score) continue;

                changes.Add(new ScoreChangeRecord
                {
                    SongId = songId, Instrument = instrument, AccountId = accountId,
                    OldScore = prev.Score, NewScore = current.Score,
                    OldRank = prev.Rank, NewRank = current.Rank,
                    Accuracy = current.Accuracy, IsFullCombo = current.IsFullCombo,
                    Stars = current.Stars, Percentile = current.Percentile,
                    Season = current.Season, ScoreAchievedAt = current.EndTime,
                    AllTimeRank = current.Rank, Difficulty = current.Difficulty,
                });
            }

            // Also detect new entries (accounts in staging that weren't in previousState)
            foreach (var ((songId, accountId), current) in currentState)
            {
                if (previousState.ContainsKey((songId, accountId))) continue;

                changes.Add(new ScoreChangeRecord
                {
                    SongId = songId, Instrument = instrument, AccountId = accountId,
                    OldScore = null, NewScore = current.Score,
                    OldRank = null, NewRank = current.Rank,
                    Accuracy = current.Accuracy, IsFullCombo = current.IsFullCombo,
                    Stars = current.Stars, Percentile = current.Percentile,
                    Season = current.Season, ScoreAchievedAt = current.EndTime,
                    AllTimeRank = current.Rank, Difficulty = current.Difficulty,
                });
            }

            if (changes.Count > 0)
            {
                _metaDb.InsertScoreChanges(changes);
                scoreChanges = changes.Count;
            }
        }

        // ── Delete ALL staged rows for this instrument ──
        _metaDb.DeleteStagedEntriesForInstrument(scrapeId, instrument);

        // ── Mark wave as finalized for all songs on this instrument ──
        _metaDb.MarkWaveFinalizedForInstrument(scrapeId, instrument, wave);

        // ── Defer account IDs for name resolution ──
        if (rowsMerged > 0)
        {
            using var conn = _pgDataSource!.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT DISTINCT account_id FROM leaderboard_entries " +
                "WHERE instrument = @instrument";
            cmd.Parameters.AddWithValue("instrument", instrument);
            var ids = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
            if (ids.Count > 0)
            {
                if (_aggregates is not null)
                    _aggregates.AddDeferredAccountIds(ids);
                else
                    _metaDb.InsertAccountIds(ids);
            }
        }

        _log.LogInformation("Finalized wave {Wave} for {Instrument}: {Merged} rows merged, {Changes} score changes, {AffectedSongs} songs affected.",
            wave, instrument, rowsMerged, scoreChanges, affectedSongIds.Count);

        return (rowsMerged, scoreChanges, affectedSongIds);
    }

    /// <summary>
    /// Delete all staging data and deep-scrape jobs for scrape IDs older than the given one.
    /// Call at scrape start and on startup.
    /// </summary>
    public int CleanupAbandonedStaging(long currentScrapeId)
    {
        var deleted = _metaDb.CleanupAbandonedStaging(currentScrapeId);
        if (deleted > 0)
            _log.LogInformation("Cleaned up {Deleted} abandoned staging rows from incomplete scrape runs.", deleted);
        return deleted;
    }

    /// <summary>
    /// Call after heavy write phases (scrape drain, post-scrape enrichment) to keep
    /// WAL files small and prevent auto-checkpoints from firing during API reads.
    /// </summary>
    public void CheckpointAll()
    {
        Parallel.ForEach(_instrumentDbs.Values, db => db.Checkpoint());
        _metaDb.Checkpoint();
        _log.LogDebug("WAL checkpoint completed on all databases.");
    }

    /// <summary>
    /// Pre-warm the per-song rankings cache for all registered users across all instruments.
    /// Call after scrape passes and on service startup so that API requests for
    /// registered users hit the in-memory cache instead of the expensive CTE query.
    /// </summary>
    public void PreWarmRankingsCache(IReadOnlyCollection<string> accountIds)
    {
        if (accountIds.Count == 0 || _instrumentDbs.Count == 0) return;

        _log.LogInformation(
            "Pre-warming rankings cache for {UserCount} user(s) across {InstrumentCount} instrument(s)...",
            accountIds.Count, _instrumentDbs.Count);

        var instruments = _instrumentDbs.Keys.ToList();
        for (int i = 0; i < instruments.Count; i++)
        {
            var instrument = instruments[i];
            var db = _instrumentDbs[instrument];
            db.PreWarmRankingsBatch(accountIds);
            _log.LogDebug("Pre-warmed rankings for {Instrument} ({N}/{Total}).",
                instrument, i + 1, instruments.Count);
        }

        _log.LogInformation(
            "Pre-warmed rankings cache for {UserCount} registered user(s) across {InstrumentCount} instrument(s).",
            accountIds.Count, instruments.Count);
    }

    /// <summary>
    /// Async wrapper that runs <see cref="PreWarmRankingsCache"/> on a thread-pool thread
    /// with a timeout. If the timeout expires, pre-warming is cancelled and the caller
    /// continues — the cache will self-populate on first API requests instead.
    /// </summary>
    public async Task PreWarmRankingsCacheAsync(
        IReadOnlyCollection<string> accountIds,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (accountIds.Count == 0 || _instrumentDbs.Count == 0) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await Task.Run(() => PreWarmRankingsCache(accountIds), cts.Token);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _log.LogWarning(
                "Rankings cache pre-warm timed out after {Timeout}. Cache will populate on demand.",
                timeout);
        }
    }

    /// <summary>
    /// Get a player's scores across all instruments (player profile).
    /// </summary>
    public List<PlayerScoreDto> GetPlayerProfile(string accountId, string? songId = null, HashSet<string>? instruments = null)
    {
        var dbs = instruments is null
            ? _instrumentDbs.Values.ToArray()
            : _instrumentDbs.Where(kv => instruments.Contains(kv.Key)).Select(kv => kv.Value).ToArray();

        var results = new List<PlayerScoreDto>[dbs.Length];
        Parallel.For(0, dbs.Length, i =>
        {
            results[i] = dbs[i].GetPlayerScores(accountId, songId);
        });

        var allScores = new List<PlayerScoreDto>();
        foreach (var r in results)
            allScores.AddRange(r);
        return allScores;
    }

    /// <summary>
    /// Get song entry counts for all instruments relevant to a player's scores.
    /// Returns a dictionary keyed by "SongId:Instrument" with total entry counts.
    /// </summary>
    public Dictionary<(string SongId, string Instrument), int> GetSongCountsForInstruments()
    {
        var kvps = _instrumentDbs.ToArray();
        var perInstrument = new Dictionary<string, int>[kvps.Length];
        var instrumentKeys = new string[kvps.Length];
        Parallel.For(0, kvps.Length, i =>
        {
            instrumentKeys[i] = kvps[i].Key;
            perInstrument[i] = kvps[i].Value.GetAllSongCounts();
        });

        var result = new Dictionary<(string, string), int>();
        for (int i = 0; i < kvps.Length; i++)
        {
            var instrument = instrumentKeys[i];
            foreach (var (songId, count) in perInstrument[i])
                result[(songId, instrument)] = count;
        }
        return result;
    }

    /// <summary>
    /// Compute rank for every song a player has across all instruments.
    /// Uses a window function for efficient rank computation.
    /// TotalEntries is no longer returned here — callers should use
    /// <see cref="IMetaDatabase.GetAllLeaderboardPopulation"/> instead.
    /// </summary>
    public Dictionary<(string SongId, string Instrument), int> GetPlayerRankings(string accountId, string? songId = null, HashSet<string>? instruments = null)
    {
        var kvps = instruments is null
            ? _instrumentDbs.ToArray()
            : _instrumentDbs.Where(kv => instruments.Contains(kv.Key)).ToArray();

        var perInstrument = new Dictionary<string, int>[kvps.Length];
        var instrumentKeys = new string[kvps.Length];
        Parallel.For(0, kvps.Length, i =>
        {
            instrumentKeys[i] = kvps[i].Key;
            perInstrument[i] = kvps[i].Value.GetPlayerRankings(accountId, songId);
        });

        var result = new Dictionary<(string, string), int>();
        for (int i = 0; i < kvps.Length; i++)
        {
            var instrument = instrumentKeys[i];
            foreach (var (sid, rank) in perInstrument[i])
                result[(sid, instrument)] = rank;
        }
        return result;
    }

    /// <summary>
    /// Like <see cref="GetPlayerRankings"/> but filters out entries above per-song max-score thresholds.
    /// <paramref name="maxScoresByInstrument"/> maps instrument DB name → (songId → threshold).
    /// </summary>
    public Dictionary<(string SongId, string Instrument), int> GetPlayerRankingsFiltered(
        string accountId,
        Dictionary<string, Dictionary<string, int>> maxScoresByInstrument,
        string? songId = null,
        HashSet<string>? instruments = null)
    {
        var kvps = instruments is null
            ? _instrumentDbs.ToArray()
            : _instrumentDbs.Where(kv => instruments.Contains(kv.Key)).ToArray();

        var perInstrument = new Dictionary<string, int>[kvps.Length];
        var instrumentKeys = new string[kvps.Length];
        Parallel.For(0, kvps.Length, i =>
        {
            var inst = kvps[i].Key;
            instrumentKeys[i] = inst;
            if (maxScoresByInstrument.TryGetValue(inst, out var thresholds) && thresholds.Count > 0)
                perInstrument[i] = kvps[i].Value.GetPlayerRankingsFiltered(accountId, thresholds, songId);
            else
                perInstrument[i] = kvps[i].Value.GetPlayerRankings(accountId, songId);
        });

        var result = new Dictionary<(string, string), int>();
        for (int i = 0; i < kvps.Length; i++)
        {
            var instrument = instrumentKeys[i];
            foreach (var (sid, rank) in perInstrument[i])
                result[(sid, instrument)] = rank;
        }
        return result;
    }

    /// <summary>
    /// Read the stored Rank column for every song a player has, across all instruments.
    /// Uses the pre-computed rank from <see cref="RecomputeAllRanks"/> — no live CTE.
    /// Returns (SongId, Instrument) → (Rank, Total).
    /// </summary>
    public Dictionary<(string SongId, string Instrument), (int Rank, int Total)> GetPlayerStoredRankings(
        string accountId, string? songId = null, HashSet<string>? instruments = null)
    {
        var kvps = instruments is null
            ? _instrumentDbs.ToArray()
            : _instrumentDbs.Where(kv => instruments.Contains(kv.Key)).ToArray();

        var perInstrument = new Dictionary<string, (int, int)>[kvps.Length];
        var instrumentKeys = new string[kvps.Length];
        Parallel.For(0, kvps.Length, i =>
        {
            instrumentKeys[i] = kvps[i].Key;
            perInstrument[i] = kvps[i].Value.GetPlayerStoredRankings(accountId, songId);
        });

        var result = new Dictionary<(string, string), (int Rank, int Total)>();
        for (int i = 0; i < kvps.Length; i++)
        {
            var instrument = instrumentKeys[i];
            foreach (var (sid, rankTotal) in perInstrument[i])
                result[(sid, instrument)] = rankTotal;
        }
        return result;
    }

    /// <summary>
    /// Compute the rank a specific score would have, filtered by a max-score threshold.
    /// Returns 0 if the instrument is unknown.
    /// </summary>
    public int GetRankForScore(string instrument, string songId, int score, int? maxScore = null)
    {
        if (!_instrumentDbs.TryGetValue(instrument, out var db))
            return 0;
        return db.GetRankForScore(songId, score, maxScore);
    }

    /// <summary>
    /// Count valid (below-threshold) entries per song for each instrument.
    /// <paramref name="maxScoresByInstrument"/> maps instrument DB name → (songId → threshold).
    /// </summary>
    public Dictionary<(string SongId, string Instrument), int> GetFilteredPopulation(
        Dictionary<string, Dictionary<string, int>> maxScoresByInstrument,
        HashSet<string>? instruments = null)
    {
        var kvps = instruments is null
            ? _instrumentDbs.ToArray()
            : _instrumentDbs.Where(kv => instruments.Contains(kv.Key)).ToArray();

        var perInstrument = new Dictionary<string, int>[kvps.Length];
        var instrumentKeys = new string[kvps.Length];
        Parallel.For(0, kvps.Length, i =>
        {
            var inst = kvps[i].Key;
            instrumentKeys[i] = inst;
            if (maxScoresByInstrument.TryGetValue(inst, out var thresholds) && thresholds.Count > 0)
                perInstrument[i] = kvps[i].Value.GetFilteredEntryCounts(thresholds);
            else
                perInstrument[i] = kvps[i].Value.GetAllSongCounts();
        });

        var result = new Dictionary<(string, string), int>();
        for (int i = 0; i < kvps.Length; i++)
        {
            var instrument = instrumentKeys[i];
            foreach (var (sid, count) in perInstrument[i])
                result[(sid, instrument)] = count;
        }
        return result;
    }

    /// <summary>
    /// Get the leaderboard for a specific song + instrument.
    /// </summary>
    public List<LeaderboardEntryDto>? GetLeaderboard(string songId, string instrument, int? top = null, int offset = 0)
    {
        if (!_instrumentDbs.TryGetValue(instrument, out var db))
            return null;
        return db.GetLeaderboard(songId, top, offset);
    }

    /// <summary>
    /// Get the leaderboard with count in a single query (avoids separate COUNT round-trip).
    /// Returns null if the instrument is unknown.
    /// </summary>
    public (List<LeaderboardEntryDto> Entries, int TotalCount)? GetLeaderboardWithCount(
        string songId, string instrument, int? top = null, int offset = 0, int? maxScore = null)
    {
        if (!_instrumentDbs.TryGetValue(instrument, out var db))
            return null;
        return db.GetLeaderboardWithCount(songId, top, offset, maxScore);
    }

    /// <summary>
    /// Get the total number of leaderboard entries for a song on a specific instrument.
    /// Returns null if the instrument is unknown.
    /// </summary>
    public int? GetLeaderboardCount(string songId, string instrument)
    {
        if (!_instrumentDbs.TryGetValue(instrument, out var db))
            return null;
        return db.GetLeaderboardCount(songId);
    }

    /// <summary>
    /// Recompute the stored Rank column across all instrument databases.
    /// Should be called after a scrape pass completes.
    /// </summary>
    /// <returns>Total number of rows updated across all instruments.</returns>
    public int RecomputeAllRanks()
    {
        var results = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (_pgDataSource is not null)
        {
            // PostgreSQL mode: limited parallelism — each instrument partition is
            // independent, but too many concurrent massive UPDATEs contend for WAL
            // writer. DOP=2 balances throughput vs contention.
            Parallel.ForEach(_instrumentDbs, new ParallelOptions { MaxDegreeOfParallelism = 2 }, kvp =>
            {
                var updated = kvp.Value.RecomputeAllRanks();
                results[kvp.Key] = updated;
            });
        }
        else
        {
            // SQLite mode: each instrument has its own DB file — run in parallel.
            Parallel.ForEach(_instrumentDbs, kvp =>
            {
                var updated = kvp.Value.RecomputeAllRanks();
                results[kvp.Key] = updated;
            });
        }

        int total = 0;
        foreach (var (instrument, updated) in results)
        {
            _log.LogInformation("Recomputed ranks for {Instrument}: {Updated} entries.", instrument, updated);
            total += updated;
        }
        return total;
    }

    /// <summary>
    /// Recompute the stored Rank column only for the specified songs across all instrument databases.
    /// Much faster than <see cref="RecomputeAllRanks"/> when only a subset of songs changed.
    /// Falls back to <see cref="RecomputeAllRanks"/> when <paramref name="songIds"/> is empty.
    /// </summary>
    public int RecomputeRanksForSongs(IReadOnlyCollection<string> songIds)
    {
        if (songIds.Count == 0)
            return RecomputeAllRanks();

        var results = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (_pgDataSource is not null)
        {
            // PostgreSQL mode: limited parallelism — bulk query per instrument is
            // a single UPDATE, so WAL contention is manageable at DOP=2.
            Parallel.ForEach(_instrumentDbs, new ParallelOptions { MaxDegreeOfParallelism = 2 }, kvp =>
            {
                var updated = kvp.Value.RecomputeRanksForSongs(songIds);
                results[kvp.Key] = updated;
            });
        }
        else
        {
            Parallel.ForEach(_instrumentDbs, kvp =>
            {
                var updated = kvp.Value.RecomputeRanksForSongs(songIds);
                results[kvp.Key] = updated;
            });
        }

        int total = 0;
        foreach (var (instrument, updated) in results)
        {
            _log.LogInformation("Recomputed ranks for {Instrument}: {Updated} entries across {Songs} changed songs.",
                instrument, updated, songIds.Count);
            total += updated;
        }
        return total;
    }

    /// <summary>
    /// Get a list of all known instrument keys.
    /// </summary>
    public IReadOnlyList<string> GetInstrumentKeys()
        => _instrumentDbs.Keys.ToList();

    private int? _cachedTotalSongCount;

    /// <summary>
    /// Get the total number of distinct songs across all instrument DBs.
    /// Result is cached until <see cref="InvalidateTotalSongCount"/> is called.
    /// </summary>
    public int GetTotalSongCount()
    {
        if (_cachedTotalSongCount is { } cached)
            return cached;

        var songIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var db in _instrumentDbs.Values)
        {
            foreach (var songId in db.GetAllSongCounts().Keys)
                songIds.Add(songId);
        }
        _cachedTotalSongCount = songIds.Count;
        return songIds.Count;
    }

    /// <summary>
    /// Invalidates the cached total song count so it is recomputed on next access.
    /// Call this when the song catalog changes (spark tracks sync).
    /// </summary>
    public void InvalidateTotalSongCount() => _cachedTotalSongCount = null;

    /// <summary>
    /// Prune all instrument DBs: for each song, keep only the top <paramref name="maxEntriesPerSong"/>
    /// entries (by score), plus any entries for accounts in <paramref name="preserveAccountIds"/>.
    /// When <paramref name="thresholdsPerInstrument"/> is provided, entries above the per-song
    /// threshold are exempt from pruning (over-threshold / exploited scores are kept unconditionally).
    /// Returns total rows deleted across all instruments.
    /// </summary>
    public int PruneAllInstruments(int maxEntriesPerSong, IReadOnlySet<string> preserveAccountIds,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? thresholdsPerInstrument = null)
    {
        if (maxEntriesPerSong <= 0) return 0;

        int totalDeleted = 0;
        foreach (var (instrument, db) in _instrumentDbs)
        {
            IReadOnlyDictionary<string, int>? songThresholds = null;
            thresholdsPerInstrument?.TryGetValue(instrument, out songThresholds);

            var deleted = db.PruneAllSongs(maxEntriesPerSong, preserveAccountIds, songThresholds);
            if (deleted > 0)
                _log.LogInformation("Pruned {Deleted:N0} excess entries from {Instrument}.", deleted, instrument);
            totalDeleted += deleted;
        }
        return totalDeleted;
    }

    /// <summary>
    /// Get the minimum Season value for a song across all instrument DBs.
    /// Returns null if no instrument has any entry for this song.
    /// </summary>
    public int? GetMinSeasonAcrossInstruments(string songId)
    {
        var dbs = _instrumentDbs.Values.ToArray();
        var results = new int?[dbs.Length];
        Parallel.For(0, dbs.Length, i => results[i] = dbs[i].GetMinSeason(songId));

        int? globalMin = null;
        foreach (var min in results)
        {
            if (min.HasValue && (!globalMin.HasValue || min.Value < globalMin.Value))
                globalMin = min.Value;
        }
        return globalMin;
    }

    /// <summary>
    /// Get the maximum season number across all instrument databases.
    /// Returns null only if all DBs are empty.
    /// </summary>
    public int? GetMaxSeasonAcrossInstruments()
    {
        var dbs = _instrumentDbs.Values.ToArray();
        var results = new int?[dbs.Length];
        Parallel.For(0, dbs.Length, i => results[i] = dbs[i].GetMaxSeason());

        int? globalMax = null;
        foreach (var max in results)
        {
            if (max.HasValue && (!globalMax.HasValue || max.Value > globalMax.Value))
                globalMax = max.Value;
        }
        return globalMax;
    }

    public void Dispose()
    {
        foreach (var db in _instrumentDbs.Values)
            db.Dispose();
        _metaDb.Dispose();
    }
}

/// <summary>
/// Result of persisting one <see cref="GlobalLeaderboardResult"/>.
/// </summary>
public sealed class PersistResult
{
    /// <summary>Number of rows inserted or updated.</summary>
    public int RowsAffected { get; init; }

    /// <summary>Number of score changes detected for registered users.</summary>
    public int ScoreChangesDetected { get; init; }

    /// <summary>Whether the upsert inserted any brand-new leaderboard entries.</summary>
    public bool HasNewEntries { get; init; }

    /// <summary>
    /// Account IDs of registered users whose scores changed in this result.
    /// Used to flag stale entries for refresh.
    /// </summary>
    public HashSet<string> ChangedAccountIds { get; init; } = [];
}
