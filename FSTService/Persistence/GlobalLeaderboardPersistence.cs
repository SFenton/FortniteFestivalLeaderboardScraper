using System.Collections.Concurrent;
using System.Text.Json;
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
    private const string LeaderboardStagingTable = MetaDatabase.LeaderboardStagingTable;
    private const int MaxBandSearchQueryLength = 200;
    private const int MaxBandSearchTokens = 5;
    private const int MaxBandSearchCandidatesPerTerm = 8;
    private const int MaxBandSearchInterpretations = 12;
    private const int MaxBandSearchCandidateAccounts = 32;
    private readonly Dictionary<string, IInstrumentDatabase> _instrumentDbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMetaDatabase _metaDb;
    private readonly ILogger<GlobalLeaderboardPersistence> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly NpgsqlDataSource _pgDataSource;
    private readonly FeatureOptions _features;

    /// <summary>The meta database (ScrapeLog, ScoreHistory, etc.).</summary>
    public IMetaDatabase Meta => _metaDb;

    /// <summary>
    /// True when scrape flushes should maintain the legacy mutable leaderboard_entries table.
    /// Snapshot current-state rows are written independently of this rollback flag.
    /// </summary>
    public bool WriteLegacyLiveLeaderboardDuringScrape => _features.WriteLegacyLiveLeaderboardDuringScrape;

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

        // Purge any phantom instrument entries created by previous runs
        // (e.g. from unvalidated API requests before the guard was added).
        var phantoms = _instrumentDbs.Keys
            .Where(k => !ValidInstrumentKeys.Contains(k))
            .ToList();
        foreach (var key in phantoms)
        {
            _instrumentDbs.Remove(key);
            _log.LogWarning("Removed phantom instrument DB: {Instrument}", key);
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

    /// <summary>Valid instrument keys accepted by <see cref="GetOrCreateInstrumentDb"/>.</summary>
    private static readonly HashSet<string> ValidInstrumentKeys = new(ComboIds.CanonicalOrder, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns true when <paramref name="instrument"/> is a recognised instrument key.</summary>
    public static bool IsValidInstrument(string instrument) => ValidInstrumentKeys.Contains(instrument);

    /// <summary>
    /// Get (or create on first access) the <see cref="IInstrumentDatabase"/>
    /// for a given instrument key (e.g. "Solo_Guitar").
    /// Rejects unknown instrument names to prevent rogue DB instances.
    /// </summary>
    public IInstrumentDatabase GetOrCreateInstrumentDb(string instrument)
    {
        if (_instrumentDbs.TryGetValue(instrument, out var db))
            return db;

        if (!ValidInstrumentKeys.Contains(instrument))
            throw new ArgumentException($"Unknown instrument key: '{instrument}'. Valid keys: {string.Join(", ", ComboIds.CanonicalOrder)}");

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
    /// for registered users and flags tracked users whose rivals neighborhood changed.
    /// </summary>
    /// <returns>
    /// The number of rows affected and the set of account IDs seen in this result.
    /// </returns>
    public PersistResult PersistResult(GlobalLeaderboardResult result,
                                       IReadOnlySet<string>? registeredAccountIds = null,
                                       (NpgsqlConnection Conn, NpgsqlTransaction Tx)? pgConnTx = null)
    {
        var db = GetOrCreateInstrumentDb(result.Instrument);

        // ── Pre-UPSERT: snapshot current state for accounts present in this result ──
        Dictionary<string, LeaderboardEntry>? previousState = null;
        if (registeredAccountIds is { Count: > 0 })
        {
            var relevantIds = result.Entries
                .Select(entry => entry.AccountId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

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

        // ── Post-UPSERT: detect score changes for registered users and flag affected rivals ──
        var changedAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirtyRivalSongs = new Dictionary<string, RivalDirtySongRow>(StringComparer.OrdinalIgnoreCase);
        var scoreChanges = new List<ScoreChangeRecord>();
        var detectedAt = DateTime.UtcNow.ToString("o");
        if (previousState is not null)
        {
            var affectedRankIntervals = new List<(int Lo, int Hi)>();

            foreach (var entry in result.Entries)
            {
                previousState.TryGetValue(entry.AccountId, out var prev);
                var hadPreviousEntry = prev is not null;
                var oldRank = hadPreviousEntry ? ResolveRank(prev!) : 0;
                var newRank = ResolveRank(entry);
                var scoreChanged = hadPreviousEntry ? entry.Score != prev!.Score : true;
                var rankChanged = hadPreviousEntry && oldRank > 0 && newRank > 0 && oldRank != newRank;
                var intervalLowSeed = oldRank > 0 && newRank > 0 ? Math.Min(oldRank, newRank) : Math.Max(oldRank, newRank);
                var intervalHighSeed = Math.Max(oldRank, newRank);

                if ((scoreChanged || rankChanged) && intervalHighSeed > 0)
                {
                    var lo = Math.Max(1, intervalLowSeed - RivalsCalculator.NeighborhoodRadius);
                    var hi = Math.Max(1, intervalHighSeed + RivalsCalculator.NeighborhoodRadius);
                    affectedRankIntervals.Add((lo, hi));
                }

                if (!registeredAccountIds!.Contains(entry.AccountId))
                    continue;

                if (hadPreviousEntry)
                {
                    // Existing entry — check if score actually changed
                    if (scoreChanged)
                    {
                        scoreChanges.Add(new ScoreChangeRecord
                        {
                            SongId = result.SongId, Instrument = result.Instrument,
                            AccountId = entry.AccountId,
                            OldScore = prev!.Score, NewScore = entry.Score,
                            OldRank = prev.Rank, NewRank = entry.Rank,
                            Accuracy = entry.Accuracy, IsFullCombo = entry.IsFullCombo,
                            Stars = entry.Stars, Percentile = entry.Percentile,
                            Season = entry.Season, ScoreAchievedAt = entry.EndTime,
                            AllTimeRank = entry.Rank, Difficulty = entry.Difficulty,
                        });
                        changedAccountIds.Add(entry.AccountId);
                        dirtyRivalSongs[entry.AccountId] = new RivalDirtySongRow
                        {
                            AccountId = entry.AccountId,
                            Instrument = result.Instrument,
                            SongId = result.SongId,
                            DirtyReason = RivalsDirtyReason.SelfScoreChange,
                            DetectedAt = detectedAt,
                        };
                    }
                    else if (rankChanged)
                    {
                        changedAccountIds.Add(entry.AccountId);
                        dirtyRivalSongs[entry.AccountId] = new RivalDirtySongRow
                        {
                            AccountId = entry.AccountId,
                            Instrument = result.Instrument,
                            SongId = result.SongId,
                            DirtyReason = RivalsDirtyReason.SelfRankChange,
                            DetectedAt = detectedAt,
                        };
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
                    dirtyRivalSongs[entry.AccountId] = new RivalDirtySongRow
                    {
                        AccountId = entry.AccountId,
                        Instrument = result.Instrument,
                        SongId = result.SongId,
                        DirtyReason = RivalsDirtyReason.SelfScoreChange,
                        DetectedAt = detectedAt,
                    };
                }
            }

            // Batch-insert all score changes in one transaction
            if (scoreChanges.Count > 0)
                _metaDb.InsertScoreChanges(scoreChanges);

            if (registeredAccountIds is { Count: > 0 } && affectedRankIntervals.Count > 0)
            {
                foreach (var (lo, hi) in MergeRankIntervals(affectedRankIntervals))
                {
                    foreach (var accountId in db.GetAccountsInRankRange(result.SongId, lo, hi))
                    {
                        if (registeredAccountIds.Contains(accountId))
                        {
                            changedAccountIds.Add(accountId);
                            dirtyRivalSongs.TryAdd(accountId, new RivalDirtySongRow
                            {
                                AccountId = accountId,
                                Instrument = result.Instrument,
                                SongId = result.SongId,
                                DirtyReason = RivalsDirtyReason.NeighborWindowChange,
                                DetectedAt = detectedAt,
                            });
                        }
                    }
                }
            }
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
            DirtyRivalSongs = dirtyRivalSongs.Values.ToList(),
            HasNewEntries = hasNewEntries,
        };
    }

    private static IEnumerable<(int Lo, int Hi)> MergeRankIntervals(List<(int Lo, int Hi)> intervals)
    {
        if (intervals.Count == 0)
            yield break;

        intervals.Sort(static (left, right) => left.Lo.CompareTo(right.Lo));
        var current = intervals[0];

        for (var index = 1; index < intervals.Count; index++)
        {
            var next = intervals[index];
            if (next.Lo <= current.Hi + 1)
            {
                current = (current.Lo, Math.Max(current.Hi, next.Hi));
                continue;
            }

            yield return current;
            current = next;
        }

        yield return current;
    }

    private static int ResolveRank(LeaderboardEntry entry) => entry.Rank > 0 ? entry.Rank : entry.ApiRank;

    private static int ResolveRank(LeaderboardEntryDto entry) => entry.Rank > 0 ? entry.Rank : entry.ApiRank;

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
        private readonly ConcurrentDictionary<(string AccountId, string SongId, string Instrument), RivalDirtySongRow>
            _dirtyRivalSongs = new();
        private readonly ConcurrentDictionary<(string AccountId, string SongId, string Instrument), byte>
            _seenRegisteredEntries = new();
        private readonly ConcurrentDictionary<string, byte> _deferredAccountIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _changedSongIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _rankChangedSongIds = new(StringComparer.OrdinalIgnoreCase);

        public int TotalEntries => _totalEntries;
        public int TotalChanges => _totalChanges;
        public int SongsWithData => _songsWithData;
        public IReadOnlyCollection<string> ChangedAccountIds => _changedAccountIds;
        public IReadOnlyCollection<RivalDirtySongRow> DirtyRivalSongs => _dirtyRivalSongs.Values.ToArray();

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

        public void AddDirtyRivalSongs(IEnumerable<RivalDirtySongRow> rows)
        {
            foreach (var row in rows)
                _dirtyRivalSongs[(row.AccountId, row.SongId, row.Instrument)] = row;
        }

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
        agg.AddDirtyRivalSongs(persistResult.DirtyRivalSongs);
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

    // ─── Per-page write channel ────────────────────────────────────

    private record struct PageWorkItem(string SongId, string Instrument, IReadOnlyList<LeaderboardEntry> Entries);

    private Dictionary<string, Channel<PageWorkItem>>? _pageChannels;
    private List<Task>? _pageWriterTasks;

    /// <summary>
    /// Start per-instrument page-writer tasks. Each instrument gets a bounded
    /// channel and a dedicated writer task that batches page upserts into single
    /// PG transactions to amortize commit overhead and limit open connections.
    /// Bounded channels cap memory usage — backpressure from full channels
    /// briefly pauses the scraper but prevents OOM crashes.
    /// </summary>
    public void StartPageWriters(int channelCapacity = 4, int writeBatchSize = 2, CancellationToken ct = default)
    {
        _pageChannels = new Dictionary<string, Channel<PageWorkItem>>(StringComparer.OrdinalIgnoreCase);
        _pageWriterTasks = new List<Task>();

        foreach (var instrument in _instrumentDbs.Keys)
        {
            var channel = Channel.CreateBounded<PageWorkItem>(new BoundedChannelOptions(channelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _pageChannels[instrument] = channel;

            var db = (InstrumentDatabase)_instrumentDbs[instrument];
            var task = Task.Run(async () =>
            {
                await RunPageWriterAsync(channel.Reader, db, writeBatchSize, ct);
            }, ct);
            _pageWriterTasks.Add(task);
        }

        _log.LogInformation("Started {Count} per-instrument page writers (capacity {Cap}, batch {Batch}).",
            _pageWriterTasks.Count, channelCapacity, writeBatchSize);
    }

    /// <summary>
    /// Enqueue a page of entries for asynchronous batched persistence.
    /// Applies back-pressure when the per-instrument channel is full.
    /// </summary>
    public async ValueTask EnqueuePageAsync(string songId, string instrument,
                                             IReadOnlyList<LeaderboardEntry> entries,
                                             CancellationToken ct = default)
    {
        if (_pageChannels is null)
            throw new InvalidOperationException("Page writers not started. Call StartPageWriters() first.");

        if (!_pageChannels.TryGetValue(instrument, out var channel))
        {
            _log.LogWarning("No page writer channel for instrument {Instrument}. Dropping page.", instrument);
            return;
        }

        await channel.Writer.WriteAsync(new PageWorkItem(songId, instrument, entries), ct);
    }

    /// <summary>
    /// Signal all page writers that no more items will arrive, then wait for
    /// them to drain. Call after the scrape loop completes.
    /// </summary>
    public async Task DrainPageWritersAsync()
    {
        if (_pageChannels is null || _pageWriterTasks is null) return;

        foreach (var channel in _pageChannels.Values)
            channel.Writer.TryComplete();

        await Task.WhenAll(_pageWriterTasks);

        _log.LogInformation("All per-instrument page writers drained.");
        _pageChannels = null;
        _pageWriterTasks = null;
    }

    private async Task RunPageWriterAsync(ChannelReader<PageWorkItem> reader,
                                           InstrumentDatabase db, int batchSize,
                                           CancellationToken ct)
    {
        var batch = new List<PageWorkItem>(batchSize);

        while (await reader.WaitToReadAsync(ct))
        {
            batch.Clear();
            while (batch.Count < batchSize && reader.TryRead(out var item))
                batch.Add(item);

            if (batch.Count == 0) continue;

            try
            {
                using var conn = db.DataSource.OpenConnection();
                using var tx = conn.BeginTransaction();
                using (var sc = conn.CreateCommand()) { sc.Transaction = tx; sc.CommandText = "SET LOCAL synchronous_commit = off"; sc.ExecuteNonQuery(); }

                foreach (var item in batch)
                    db.UpsertEntries(item.SongId, item.Entries, conn, tx);

                tx.Commit();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Page writer batch commit failed for {Instrument} ({Count} pages). Data will be retried next pass.",
                    db.Instrument, batch.Count);
            }
        }
    }

    // ─── Disk-spool persistence ───────────────────────────────────

    private SpoolWriter<LeaderboardEntry>? _spoolWriter;
    private OnlineBoundedPageWriter<LeaderboardEntry>? _onlineSoloWriter;
    private readonly object _activeWriterLock = new();

    /// <summary>
    /// Start a disk-spool writer that appends fetched pages to per-instrument
    /// files on disk.  No consumers run during fetch — data is flushed in bulk
    /// after the network phase completes via <see cref="FlushSpoolAsync"/>.
    /// </summary>
    public SpoolWriter<LeaderboardEntry> StartSpoolWriter(long scrapeId, string? spoolDirectory = null)
    {
        SpoolWriter<LeaderboardEntry> writer;
        lock (_activeWriterLock)
        {
            if (_onlineSoloWriter is not null)
                throw new InvalidOperationException("Online solo writer is already active.");
            if (_spoolWriter is not null)
                throw new InvalidOperationException("Disk spool writer is already active.");

            writer = LeaderboardSpoolWriterFactory.Create(_log, this, scrapeId, spoolDirectory);
            _spoolWriter = writer;
        }

        _log.LogInformation("Started disk-spool writer (post-fetch flush mode).");
        return writer;
    }

    /// <summary>
    /// Append a page of entries to the spool file.  Non-blocking — never
    /// applies back-pressure to the scraper.
    /// </summary>
    public void EnqueueSpoolPage(string songId, string instrument, IReadOnlyList<LeaderboardEntry> entries)
    {
        if (_spoolWriter is null)
            throw new InvalidOperationException("Spool writer not started. Call StartSpoolWriter() first.");
        _spoolWriter.Enqueue(songId, instrument, entries);
    }

    /// <summary>
    /// Start an experimental bounded online solo writer that persists pages during
    /// fetch using small COPY/merge batches. Producers are backpressured when the
    /// bounded channel is full, preventing unbounded RAM growth.
    /// </summary>
    public OnlineBoundedPageWriter<LeaderboardEntry> StartOnlineSoloWriter(
        long scrapeId,
        int channelCapacity,
        int maxBatchPages,
        int writerCount,
        CancellationToken ct = default)
    {
        OnlineBoundedPageWriter<LeaderboardEntry> writer;
        lock (_activeWriterLock)
        {
            if (_spoolWriter is not null)
                throw new InvalidOperationException("Disk spool writer is already active.");
            if (_onlineSoloWriter is not null)
                throw new InvalidOperationException("Online solo writer is already active.");

            writer = new OnlineBoundedPageWriter<LeaderboardEntry>(
                _log,
                "solo-online",
                (instrument, batch) => LeaderboardSpoolWriterFactory.FlushSoloBatch(_log, this, scrapeId, instrument, batch),
                Math.Max(1, channelCapacity),
                Math.Max(1, maxBatchPages),
                Math.Clamp(writerCount, 1, 16),
                ct);
            _onlineSoloWriter = writer;
        }

        return writer;
    }

    public ValueTask EnqueueOnlineSoloPageAsync(
        string songId,
        string instrument,
        IReadOnlyList<LeaderboardEntry> entries,
        CancellationToken ct = default)
    {
        if (_onlineSoloWriter is null)
            throw new InvalidOperationException("Online solo writer not started. Call StartOnlineSoloWriter() first.");

        return _onlineSoloWriter.EnqueueAsync(songId, instrument, entries, ct);
    }

    public async Task DrainOnlineSoloWriterAsync()
    {
        OnlineBoundedPageWriter<LeaderboardEntry>? writer;
        lock (_activeWriterLock)
        {
            writer = _onlineSoloWriter;
            _onlineSoloWriter = null;
        }

        if (writer is null) return;

        try
        {
            await writer.CompleteAndDrainAsync().ConfigureAwait(false);
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Signal the spool writer that no more data will arrive, then flush
    /// all spool data to PG in bulk. Index management is handled externally
    /// by the orchestrator to coordinate with the band spool.
    /// </summary>
    public async Task FlushSpoolAsync(ScrapeProgressTracker? progress = null)
    {
        SpoolWriter<LeaderboardEntry>? spool;
        lock (_activeWriterLock)
        {
            spool = _spoolWriter;
            _spoolWriter = null;
        }

        if (spool is null) return;

        try
        {
            spool.Complete();

            _log.LogInformation("Flushing solo spool: {Records:N0} pages, {Entries:N0} entries...",
                spool.RecordCount, spool.EntryCount);
            await Task.Run(() => spool.FlushAll(
                maxBatchPages: 64,
                onProgress: p => ReportSpoolFlushProgress(progress, p)));
        }
        finally
        {
            await spool.DisposeAsync();
        }
    }

    public async ValueTask CleanupActiveScrapeWritersAsync()
    {
        SpoolWriter<LeaderboardEntry>? spool;
        OnlineBoundedPageWriter<LeaderboardEntry>? onlineWriter;
        lock (_activeWriterLock)
        {
            spool = _spoolWriter;
            _spoolWriter = null;
            onlineWriter = _onlineSoloWriter;
            _onlineSoloWriter = null;
        }

        if (onlineWriter is not null)
        {
            _log.LogInformation("Best-effort cleanup disposing active online solo writer.");
            try
            {
                await onlineWriter.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Best-effort cleanup failed while disposing active online solo writer.");
            }
        }

        if (spool is not null)
        {
            _log.LogInformation("Best-effort cleanup disposing active solo spool writer.");
            try
            {
                await spool.DisposeAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Best-effort cleanup failed while disposing active solo spool writer.");
            }
        }
    }

    private static void ReportSpoolFlushProgress(
        ScrapeProgressTracker? progress,
        SpoolWriter<LeaderboardEntry>.FlushProgress flushProgress)
    {
        progress?.ReportFlushProgress(
            flushProgress.Label,
            flushProgress.Instrument,
            flushProgress.InstrumentsCompleted,
            flushProgress.InstrumentsTotal,
            flushProgress.PagesFlushed,
            flushProgress.PagesTotal,
            flushProgress.EntriesFlushed,
            flushProgress.EntriesTotal,
            flushProgress.InstrumentPagesFlushed,
            flushProgress.InstrumentPagesTotal,
            flushProgress.InstrumentEntriesFlushed,
            flushProgress.InstrumentEntriesTotal,
            flushProgress.ChunkIndex,
            flushProgress.ChunkTotal,
            flushProgress.ChunkPages,
            flushProgress.ChunkEntries,
            flushProgress.State,
            flushProgress.ActiveChunkElapsedSeconds,
            flushProgress.UpdatedAtUtc);
    }

    public int FinalizeShadowSnapshots(
        long scrapeId,
        int wave = 1,
        IEnumerable<(string SongId, string Instrument)>? expectedPairs = null)
    {
        if (scrapeId <= 0)
            return 0;

        var expectedPairArray = expectedPairs?
            .Where(pair => !string.IsNullOrWhiteSpace(pair.SongId) && !string.IsNullOrWhiteSpace(pair.Instrument))
            .Distinct()
            .ToArray()
            ?? [];

        var finalizedColumn = wave == 2 ? "wave2_finalized_at" : "wave1_finalized_at";
        using var conn = _pgDataSource!.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH expected_pairs AS (
                SELECT DISTINCT pair.song_id, pair.instrument
                FROM unnest(@expectedSongIds::text[], @expectedInstruments::text[]) AS pair(song_id, instrument)
            ), snapshot_pairs AS (
                SELECT DISTINCT song_id, instrument
                FROM leaderboard_entries_snapshot
                WHERE snapshot_id = @scrapeId
            ), activation_pairs AS (
                SELECT song_id, instrument FROM snapshot_pairs
                UNION
                SELECT song_id, instrument FROM expected_pairs
            ), upserted AS (
                INSERT INTO leaderboard_snapshot_state
                (song_id, instrument, active_snapshot_id, scrape_id, is_finalized, {finalizedColumn}, updated_at)
                SELECT song_id, instrument, @scrapeId, @scrapeId, TRUE, @now, @now
                FROM activation_pairs
                ON CONFLICT (song_id, instrument) DO UPDATE SET
                    active_snapshot_id = EXCLUDED.active_snapshot_id,
                    scrape_id = EXCLUDED.scrape_id,
                    is_finalized = EXCLUDED.is_finalized,
                    {finalizedColumn} = EXCLUDED.{finalizedColumn},
                    updated_at = EXCLUDED.updated_at
                RETURNING 1
            )
            SELECT COUNT(*) FROM upserted
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("expectedSongIds", expectedPairArray.Select(pair => pair.SongId).ToArray());
        cmd.Parameters.AddWithValue("expectedInstruments", expectedPairArray.Select(pair => pair.Instrument).ToArray());
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ─── Scrape-time index management ──────────────────────────────

    /// <summary>Solo secondary indexes (leaderboard_entries table).</summary>
    private static readonly string[] SoloDroppableIndexes =
    [
        "ix_le_song_score",
        "ix_le_song_rank",
        "ix_le_account",
        "ix_le_account_song",
        "ix_le_song_source",
        "ix_le_band_members",
    ];

    private static readonly string[] SoloIndexDefinitions =
    [
        "CREATE INDEX ix_le_song_score ON leaderboard_entries (song_id, instrument, score DESC)",
        "CREATE INDEX ix_le_song_rank ON leaderboard_entries (song_id, instrument, rank)",
        "CREATE INDEX ix_le_account ON leaderboard_entries (account_id, instrument)",
        "CREATE INDEX ix_le_account_song ON leaderboard_entries (account_id, song_id, instrument)",
        "CREATE INDEX ix_le_song_source ON leaderboard_entries (song_id, instrument, source)",
        "CREATE INDEX ix_le_band_members ON leaderboard_entries (song_id, instrument) WHERE (band_members_json IS NOT NULL)",
    ];

    /// <summary>Band secondary indexes (band_entries, band_member_stats, band_members tables).</summary>
    private static readonly string[] BandDroppableIndexes =
    [
        "ix_be_combo",
        "ix_be_song_rank",
        "ix_be_song_score",
        "ix_bms_account",
        "ix_bm_song_type",
    ];

    private static readonly string[] BandIndexDefinitions =
    [
        "CREATE INDEX ix_be_combo ON band_entries (song_id, band_type, instrument_combo)",
        "CREATE INDEX ix_be_song_rank ON band_entries (song_id, band_type, rank)",
        "CREATE INDEX ix_be_song_score ON band_entries (song_id, band_type, score DESC)",
        "CREATE INDEX ix_bms_account ON band_member_stats (account_id)",
        "CREATE INDEX ix_bm_song_type ON band_members (song_id, band_type)",
    ];

    /// <summary>All secondary indexes combined (for backward compat).</summary>
    private static readonly string[] ScrapeDroppableIndexes = [.. SoloDroppableIndexes, .. BandDroppableIndexes];
    private static readonly string[] ScrapeIndexDefinitions = [.. SoloIndexDefinitions, .. BandIndexDefinitions];

    /// <summary>
    /// Drop secondary indexes on scrape-target tables to speed up bulk writes.
    /// Call before the scrape loop begins (after cache freeze).
    /// </summary>
    public void DropScrapeIndexes(ScrapeProgressTracker? progress = null)
    {
        using var conn = _pgDataSource.OpenConnection();
        int dropped = 0;
        int total = ScrapeDroppableIndexes.Length;
        foreach (var idx in ScrapeDroppableIndexes)
        {
            progress?.ReportIndexProgress("dropping", idx, dropped, total);
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DROP INDEX IF EXISTS {idx}";
                cmd.ExecuteNonQuery();
                dropped++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to drop index {Index}.", idx);
            }
        }
        _log.LogInformation("Dropped {Count}/{Total} secondary indexes for scrape.", dropped, total);
    }

    /// <summary>
    /// Recreate secondary indexes after scrape + drain completes.
    /// Uses non-concurrent CREATE INDEX (faster, but blocks writes —
    /// acceptable since scrape writes are finished).
    /// </summary>
    public void CreateScrapeIndexes(ScrapeProgressTracker? progress = null)
    {
        using var conn = _pgDataSource.OpenConnection();
        int created = 0;
        int total = ScrapeIndexDefinitions.Length;
        foreach (var def in ScrapeIndexDefinitions)
        {
            // Extract index name from "CREATE INDEX ix_name ON ..."
            var indexName = def.Split(' ') is { Length: >= 3 } parts ? parts[2] : def;
            progress?.ReportIndexProgress("creating", indexName, created, total);
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = def;
                cmd.CommandTimeout = 0;
                cmd.ExecuteNonQuery();
                created++;
                _log.LogDebug("Created index: {Def}", def);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to create index: {Def}", def);
            }
        }
        _log.LogInformation("Recreated {Count}/{Total} secondary indexes after scrape.", created, total);
    }

    /// <summary>Drop only solo (leaderboard_entries) secondary indexes.</summary>
    public void DropSoloIndexes()
    {
        if (!WriteLegacyLiveLeaderboardDuringScrape)
        {
            _log.LogInformation("Skipping solo leaderboard_entries index drop because legacy live scrape writes are disabled.");
            return;
        }

        DropIndexes(SoloDroppableIndexes, "solo");
    }

    /// <summary>Recreate only solo (leaderboard_entries) secondary indexes.</summary>
    public void CreateSoloIndexes()
    {
        if (!WriteLegacyLiveLeaderboardDuringScrape)
        {
            _log.LogInformation("Skipping solo leaderboard_entries index recreation because legacy live scrape writes are disabled.");
            return;
        }

        CreateIndexes(SoloIndexDefinitions, "solo");
    }

    /// <summary>Drop only band (band_entries, band_member_stats, band_members) secondary indexes.</summary>
    public void DropBandIndexes() => DropIndexes(BandDroppableIndexes, "band");

    /// <summary>Recreate only band secondary indexes.</summary>
    public void CreateBandIndexes() => CreateIndexes(BandIndexDefinitions, "band");

    private void DropIndexes(string[] indexes, string label)
    {
        int dropped = 0;
        Parallel.ForEach(indexes, new ParallelOptions { MaxDegreeOfParallelism = 4 }, idx =>
        {
            try
            {
                using var conn = _pgDataSource.OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DROP INDEX IF EXISTS {idx}";
                cmd.ExecuteNonQuery();
                Interlocked.Increment(ref dropped);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to drop index {Index}.", idx);
            }
        });
        _log.LogInformation("Dropped {Count}/{Total} {Label} secondary indexes.", dropped, indexes.Length, label);
    }

    private void CreateIndexes(string[] definitions, string label)
    {
        int created = 0;
        Parallel.ForEach(definitions, new ParallelOptions { MaxDegreeOfParallelism = 4 }, def =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var conn = _pgDataSource.OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = def;
                cmd.CommandTimeout = 0;
                cmd.ExecuteNonQuery();
                Interlocked.Increment(ref created);
                sw.Stop();
                _log.LogDebug("Created index in {Elapsed:F1}s: {Def}", sw.Elapsed.TotalSeconds, def);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to create index: {Def}", def);
            }
        });
        _log.LogInformation("Recreated {Count}/{Total} {Label} secondary indexes.", created, definitions.Length, label);
    }

    /// <summary>
    /// Run WAL checkpoints on all instrument databases and the meta database.

    // ─── Direct-to-live persistence ────────────────────────────────

    /// <summary>
    /// Upsert a page of leaderboard entries directly into the live table.
    /// Called per-page during scraping, eliminating the staging table.
    /// Returns the number of rows inserted or updated.
    /// </summary>
    public int UpsertPageEntries(string songId, string instrument, IReadOnlyList<LeaderboardEntry> entries)
    {
        if (entries.Count == 0) return 0;
        var db = GetOrCreateInstrumentDb(instrument);
        return db.UpsertEntries(songId, entries);
    }

    /// <summary>
    /// Take a snapshot of all entries for the given account IDs across all instruments.
    /// Used for score change detection: snapshot before scrape, diff after.
    /// </summary>
    public ConcurrentDictionary<(string SongId, string Instrument, string AccountId), LeaderboardEntry>
        SnapshotRegisteredUsers(IReadOnlyCollection<string> accountIds)
    {
        var result = new ConcurrentDictionary<(string SongId, string Instrument, string AccountId), LeaderboardEntry>();
        if (accountIds.Count == 0) return result;

        foreach (var (instrument, db) in _instrumentDbs)
        {
            var pgDb = (InstrumentDatabase)db;
            var entries = pgDb.GetAllEntriesForAccounts(accountIds);
            foreach (var ((songId, accountId), entry) in entries)
                result[(songId, instrument, accountId)] = entry;
        }

        return result;
    }

    /// <summary>
    /// Compare current entries for registered users against a pre-scrape snapshot
    /// and return all detected score changes (improvements and new entries).
    /// </summary>
    public List<ScoreChangeRecord> DetectScoreChanges(
        ConcurrentDictionary<(string SongId, string Instrument, string AccountId), LeaderboardEntry> previousState,
        IReadOnlyCollection<string> accountIds)
    {
        var changes = new List<ScoreChangeRecord>();
        if (accountIds.Count == 0) return changes;

        var currentState = SnapshotRegisteredUsers(accountIds);

        // Detect score changes on existing entries
        foreach (var ((songId, instrument, accountId), prev) in previousState)
        {
            if (!currentState.TryGetValue((songId, instrument, accountId), out var current)) continue;
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

        // Detect new entries (accounts in current that weren't in previous)
        foreach (var ((songId, instrument, accountId), current) in currentState)
        {
            if (previousState.ContainsKey((songId, instrument, accountId))) continue;

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

        return changes;
    }

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
        var stagingReadSource = MetaDatabase.GetLeaderboardStagingReadSource("staging");

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
                    $"SELECT DISTINCT account_id FROM {stagingReadSource} " +
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
                $"SELECT DISTINCT song_id FROM {stagingReadSource} " +
                "WHERE scrape_id = @scrapeId AND instrument = @instrument";
            cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
            cmd.Parameters.AddWithValue("instrument", instrument);
            stagedSongIds = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) stagedSongIds.Add(r.GetString(0));
        }

        var mergeSql =
            "INSERT INTO leaderboard_entries (song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, first_seen_at, last_updated_at) " +
            "SELECT DISTINCT ON (song_id, instrument, account_id) song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, staged_at, staged_at " +
            $"FROM {stagingReadSource} " +
            "WHERE scrape_id = @scrapeId AND instrument = @instrument AND song_id = ANY(@songIds) " +
            "ORDER BY song_id, instrument, account_id, score DESC, staged_at DESC " +
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
                cmd.CommandTimeout = 0;
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
        CancellationToken ct = default)
    {
        if (accountIds.Count == 0 || _instrumentDbs.Count == 0) return;

        await Task.Run(() => PreWarmRankingsCache(accountIds), ct);
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
    /// Get a player's scores across all instruments using finalized snapshot plus overlay current state.
    /// </summary>
    public List<PlayerScoreDto> GetCurrentStatePlayerProfile(string accountId, string? songId = null, HashSet<string>? instruments = null)
    {
        var dbs = instruments is null
            ? _instrumentDbs.Values.ToArray()
            : _instrumentDbs.Where(kv => instruments.Contains(kv.Key)).Select(kv => kv.Value).ToArray();

        var results = new List<PlayerScoreDto>[dbs.Length];
        Parallel.For(0, dbs.Length, i =>
        {
            results[i] = dbs[i].GetCurrentStatePlayerScores(accountId, songId);
        });

        var allScores = new List<PlayerScoreDto>();
        foreach (var result in results)
            allScores.AddRange(result);
        return allScores;
    }

    /// <summary>
    /// Get grouped unique band summaries for one player, deduped by team members.
    /// </summary>
    public PlayerBandsDto GetPlayerBands(string accountId, int previewCount = 6)
    {
        var allEntries = GetPlayerBandEntries(accountId);

        if (allEntries.Count == 0)
        {
            return CreateEmptyPlayerBands();
        }

        var duos = allEntries
            .Where(entry => string.Equals(entry.BandType, "Band_Duets", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var trios = allEntries
            .Where(entry => string.Equals(entry.BandType, "Band_Trios", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var quads = allEntries
            .Where(entry => string.Equals(entry.BandType, "Band_Quad", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new PlayerBandsDto
        {
            All = BuildAllBandGroup(accountId, allEntries, previewCount),
            Duos = BuildBandGroup(duos, previewCount),
            Trios = BuildBandGroup(trios, previewCount),
            Quads = BuildBandGroup(quads, previewCount),
        };
    }

    /// <summary>
    /// Get all unique bands for one player within a single band type, with optional combo filtering.
    /// </summary>
    public PlayerBandTypeResponseDto GetPlayerBandsByType(string accountId, string bandType, string? comboId = null)
    {
        var entries = GetPlayerBandEntries(accountId, bandType, comboId);

        return new PlayerBandTypeResponseDto
        {
            AccountId = accountId,
            BandType = bandType,
            ComboId = comboId,
            TotalCount = entries.Count,
            Entries = entries,
        };
    }

    public PlayerBandListResponseDto GetPlayerBandsList(string accountId, string group = "all", int page = 1, int? pageSize = null)
    {
        var bandTypeFilter = group switch
        {
            "all" => null,
            "duos" => "Band_Duets",
            "trios" => "Band_Trios",
            "quads" => "Band_Quad",
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown band group."),
        };

        var entries = GetPlayerBandEntries(accountId, bandTypeFilter);
        var totalCount = entries.Count;
        if (pageSize is > 0)
        {
            var skip = (Math.Max(1, page) - 1) * pageSize.Value;
            entries = entries.Skip(skip).Take(pageSize.Value).ToList();
        }

        return new PlayerBandListResponseDto
        {
            AccountId = accountId,
            Group = group,
            TotalCount = totalCount,
            Entries = entries,
        };
    }

    public BandSearchResponseDto SearchBands(
        string? query,
        IReadOnlyCollection<string>? explicitAccountIds,
        string? bandTypeFilter = null,
        string? comboIdFilter = null,
        string rankBy = "appearance",
        int page = 1,
        int pageSize = 25)
    {
        var normalizedQuery = NormalizeBandSearchQuery(query);
        var effectivePage = Math.Max(1, page);
        var effectivePageSize = Math.Clamp(pageSize, 1, 100);
        var effectiveRankBy = NormalizeBandSearchRankBy(rankBy);
        var queryForResponse = query?.Trim() ?? string.Empty;

        var interpretations = BuildBandSearchInterpretations(normalizedQuery, explicitAccountIds);
        if (interpretations.Count == 0)
        {
            return CreateBandSearchResponse(
                queryForResponse,
                normalizedQuery,
                bandTypeFilter,
                comboIdFilter,
                effectiveRankBy,
                effectivePage,
                effectivePageSize,
                needsDisambiguation: false,
                interpretations,
                [],
                totalCount: 0);
        }

        var candidateAccountIds = interpretations
            .SelectMany(static interpretation => interpretation.Terms)
            .SelectMany(static term => term.Candidates)
            .Select(static candidate => candidate.AccountId)
            .Where(static accountId => !string.IsNullOrWhiteSpace(accountId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidateAccountIds.Count > MaxBandSearchCandidateAccounts)
        {
            return CreateBandSearchResponse(
                queryForResponse,
                normalizedQuery,
                bandTypeFilter,
                comboIdFilter,
                effectiveRankBy,
                effectivePage,
                effectivePageSize,
                needsDisambiguation: true,
                interpretations,
                [],
                totalCount: 0);
        }

        using var conn = _pgDataSource.OpenConnection();
        if (HasBandSearchProjection(conn))
        {
            return SearchBandsFromProjection(
                conn,
                queryForResponse,
                normalizedQuery,
                bandTypeFilter,
                comboIdFilter,
                effectiveRankBy,
                effectivePage,
                effectivePageSize,
                interpretations,
                candidateAccountIds);
        }

        foreach (var accountId in candidateAccountIds)
            EnsureBandTeamMembershipSummary(conn, accountId);

        var candidateRows = GetBandSearchCandidateMembershipRows(conn, candidateAccountIds, bandTypeFilter);
        if (comboIdFilter is not null)
        {
            candidateRows = candidateRows
                .Where(row => string.Equals(
                    BandComboIds.FromEpicRawCombo(row.InstrumentCombo),
                    comboIdFilter,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var matchedTeams = MatchBandSearchTeams(candidateRows, interpretations);
        if (matchedTeams.Count == 0)
        {
            return CreateBandSearchResponse(
                queryForResponse,
                normalizedQuery,
                bandTypeFilter,
                comboIdFilter,
                effectiveRankBy,
                effectivePage,
                effectivePageSize,
                needsDisambiguation: false,
                interpretations,
                [],
                totalCount: 0);
        }

        var teamRows = GetBandSearchTeamMembershipRows(conn, matchedTeams.Keys.ToList());
        var allMemberAccountIds = matchedTeams.Keys
            .SelectMany(static key => SplitTeamKey(key.TeamKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var displayNames = _metaDb.GetDisplayNames(allMemberAccountIds);
        var rankingLookup = effectiveRankBy == "appearance"
            ? new Dictionary<BandSearchTeamKey, BandTeamRankingDto>()
            : GetBandSearchRankingsForTeams(matchedTeams.Keys.ToList(), comboIdFilter);

        var results = matchedTeams.Keys
            .Select(teamKey => BuildBandSearchResult(teamKey, matchedTeams[teamKey], teamRows, displayNames, rankingLookup))
            .ToList();

        results = OrderBandSearchResults(results, effectiveRankBy).ToList();

        var totalCount = results.Count;
        var pagedResults = results
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToList();

        return CreateBandSearchResponse(
            queryForResponse,
            normalizedQuery,
            bandTypeFilter,
            comboIdFilter,
            effectiveRankBy,
            effectivePage,
            effectivePageSize,
            needsDisambiguation: false,
            interpretations,
            pagedResults,
            totalCount);
    }

    private BandSearchResponseDto SearchBandsFromProjection(
        NpgsqlConnection conn,
        string queryForResponse,
        string normalizedQuery,
        string? bandTypeFilter,
        string? comboIdFilter,
        string effectiveRankBy,
        int effectivePage,
        int effectivePageSize,
        IReadOnlyList<BandSearchInternalInterpretation> interpretations,
        IReadOnlyCollection<string> candidateAccountIds)
    {
        var candidateRows = GetBandSearchProjectionMemberRows(conn, candidateAccountIds, bandTypeFilter);
        if (comboIdFilter is not null)
        {
            candidateRows = candidateRows
                .Where(row => BandSearchProjectionRowMatchesCombo(row, comboIdFilter))
                .ToList();
        }

        var matchedTeams = MatchBandSearchProjectionTeams(candidateRows, interpretations);
        if (matchedTeams.Count == 0)
        {
            return CreateBandSearchResponse(
                queryForResponse,
                normalizedQuery,
                bandTypeFilter,
                comboIdFilter,
                effectiveRankBy,
                effectivePage,
                effectivePageSize,
                needsDisambiguation: false,
                interpretations,
                [],
                totalCount: 0);
        }

        var appearanceLookup = candidateRows
            .GroupBy(static row => new BandSearchTeamKey(row.BandType, row.TeamKey))
            .ToDictionary(
                static group => group.Key,
                static group => group.Max(static row => row.TeamAppearanceCount));

        var rankingLookup = effectiveRankBy == "appearance"
            ? new Dictionary<BandSearchTeamKey, BandTeamRankingDto>()
            : GetBandSearchRankingsForTeams(matchedTeams.Keys.ToList(), comboIdFilter);

        var orderedTeamKeys = OrderBandSearchTeamKeys(matchedTeams.Keys, effectiveRankBy, appearanceLookup, rankingLookup)
            .ToList();
        var totalCount = orderedTeamKeys.Count;
        var pageTeamKeys = orderedTeamKeys
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToList();

        var teamRows = GetBandSearchProjectedTeamRows(conn, pageTeamKeys);
        var allMemberAccountIds = pageTeamKeys
            .SelectMany(key => teamRows.TryGetValue(key, out var row)
                ? (IEnumerable<string>)row.MemberAccountIds
                : SplitTeamKey(key.TeamKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var displayNames = _metaDb.GetDisplayNames(allMemberAccountIds);

        var results = pageTeamKeys
            .Select(teamKey => BuildBandSearchProjectionResult(
                teamKey,
                matchedTeams[teamKey],
                teamRows,
                displayNames,
                rankingLookup,
                appearanceLookup.GetValueOrDefault(teamKey)))
            .Where(static result => result is not null)
            .Select(static result => result!)
            .ToList();

        return CreateBandSearchResponse(
            queryForResponse,
            normalizedQuery,
            bandTypeFilter,
            comboIdFilter,
            effectiveRankBy,
            effectivePage,
            effectivePageSize,
            needsDisambiguation: false,
            interpretations,
            results,
            totalCount);
    }

    public PlayerBandEntryDto? GetBandById(string bandId)
    {
        using var conn = _pgDataSource.OpenConnection();
        var bandIdentity = ResolveBandIdentity(conn, bandId);
        if (bandIdentity is null)
            return null;

        var memberAccountIds = bandIdentity.MemberAccountIds.Count > 0
            ? bandIdentity.MemberAccountIds
            : SplitTeamKey(bandIdentity.TeamKey);
        var displayNames = _metaDb.GetDisplayNames(memberAccountIds);
        var instrumentsByMember = bandIdentity.MemberInstruments;

        return new PlayerBandEntryDto
        {
            BandId = bandId,
            TeamKey = bandIdentity.TeamKey,
            BandType = bandIdentity.BandType,
            AppearanceCount = bandIdentity.AppearanceCount ?? 0,
            Members = memberAccountIds
                .Select(memberAccountId => new PlayerBandMemberDto
                {
                    AccountId = memberAccountId,
                    DisplayName = displayNames.GetValueOrDefault(memberAccountId),
                    Instruments = instrumentsByMember.GetValueOrDefault(memberAccountId, []),
                })
                .ToList(),
        };
    }

    private List<PlayerBandEntryDto> GetPlayerBandEntries(string accountId, string? bandTypeFilter = null, string? comboIdFilter = null)
    {
        using var conn = _pgDataSource.OpenConnection();

        EnsureBandTeamMembershipSummary(conn, accountId);

        var membershipRows = GetPlayerBandMembershipRows(conn, accountId, bandTypeFilter);
        if (comboIdFilter is not null)
        {
            membershipRows = membershipRows
                .Where(row => string.Equals(
                    BandComboIds.FromEpicRawCombo(row.InstrumentCombo),
                    comboIdFilter,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (membershipRows.Count == 0)
            return [];

        var allMemberAccountIds = membershipRows
            .SelectMany(row => SplitTeamKey(row.TeamKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var displayNames = _metaDb.GetDisplayNames(allMemberAccountIds);

        return membershipRows
            .GroupBy(static row => (row.BandType, row.TeamKey))
            .Select(group =>
            {
                var memberInstruments = BuildMemberInstrumentsForSummaryGroup(group);
                return new PlayerBandEntryDto
                {
                    BandId = BandIdentity.CreateBandId(group.Key.BandType, group.Key.TeamKey),
                    TeamKey = group.Key.TeamKey,
                    BandType = group.Key.BandType,
                    AppearanceCount = group.Sum(static row => row.AppearanceCount),
                    Members = SplitTeamKey(group.Key.TeamKey)
                        .Select(memberAccountId => new PlayerBandMemberDto
                        {
                            AccountId = memberAccountId,
                            DisplayName = displayNames.GetValueOrDefault(memberAccountId),
                            Instruments = memberInstruments.GetValueOrDefault(memberAccountId, []),
                        })
                        .ToList(),
                };
            })
            .OrderByDescending(entry => entry.AppearanceCount)
            .ThenBy(entry => entry.TeamKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void EnsureBandTeamMembershipSummary(NpgsqlConnection conn, string accountId)
    {
        if (HasBandTeamMembershipState(conn, accountId))
            return;

        using var tx = conn.BeginTransaction();
        BandLeaderboardPersistence.RebuildBandTeamMembershipForAccount(conn, tx, accountId);

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO {BandLeaderboardPersistence.BandTeamMembershipStateTable} (account_id, rebuilt_at)
            VALUES (@accountId, @rebuiltAt)
            ON CONFLICT (account_id) DO UPDATE SET rebuilt_at = EXCLUDED.rebuilt_at
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("rebuiltAt", DateTime.UtcNow);
        cmd.ExecuteNonQuery();

        tx.Commit();
    }

    private static bool HasBandTeamMembershipState(NpgsqlConnection conn, string accountId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {BandLeaderboardPersistence.BandTeamMembershipStateTable} WHERE account_id = @accountId)";
        cmd.Parameters.AddWithValue("accountId", accountId);
        return Convert.ToBoolean(cmd.ExecuteScalar());
    }

    private static bool HasBandSearchProjection(NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT EXISTS(
                SELECT 1
                FROM {BandSearchProjectionBuilder.StateTable}
                WHERE id = TRUE
                  AND team_rows > 0
                  AND member_rows > 0)
            """;
        return Convert.ToBoolean(cmd.ExecuteScalar());
    }

    private static List<BandSearchProjectionMemberRow> GetBandSearchProjectionMemberRows(
        NpgsqlConnection conn,
        IReadOnlyCollection<string> accountIds,
        string? bandTypeFilter)
    {
        if (accountIds.Count == 0)
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = bandTypeFilter is null
            ? $"""
                SELECT account_id, band_type, team_key, appearance_count, team_appearance_count, instrument_combos
                FROM {BandSearchProjectionBuilder.MemberProjectionTable}
                WHERE account_id = ANY(@accountIds)
                ORDER BY account_id, band_type, team_appearance_count DESC, team_key
                """
            : $"""
                SELECT account_id, band_type, team_key, appearance_count, team_appearance_count, instrument_combos
                FROM {BandSearchProjectionBuilder.MemberProjectionTable}
                WHERE account_id = ANY(@accountIds)
                  AND band_type = @bandType
                ORDER BY account_id, band_type, team_appearance_count DESC, team_key
                """;
        cmd.Parameters.AddWithValue("accountIds", accountIds.ToArray());
        if (bandTypeFilter is not null)
            cmd.Parameters.AddWithValue("bandType", bandTypeFilter);

        var rows = new List<BandSearchProjectionMemberRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new BandSearchProjectionMemberRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetFieldValue<string[]>(5)));
        }

        return rows;
    }

    private static bool BandSearchProjectionRowMatchesCombo(BandSearchProjectionMemberRow row, string comboId) =>
        row.InstrumentCombos.Any(rawCombo => string.Equals(
            BandComboIds.FromEpicRawCombo(rawCombo),
            comboId,
            StringComparison.OrdinalIgnoreCase));

    private static Dictionary<BandSearchTeamKey, BandSearchTeamMatch> MatchBandSearchProjectionTeams(
        IReadOnlyList<BandSearchProjectionMemberRow> candidateRows,
        IReadOnlyList<BandSearchInternalInterpretation> interpretations)
    {
        var matches = new Dictionary<BandSearchTeamKey, BandSearchTeamMatch>();

        foreach (var teamGroup in candidateRows.GroupBy(static row => new BandSearchTeamKey(row.BandType, row.TeamKey)))
        {
            var teamAccounts = teamGroup
                .Select(static row => row.AccountId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var interpretation in interpretations)
            {
                var matchedAccountsForInterpretation = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allTermsMatched = true;

                foreach (var term in interpretation.Terms)
                {
                    var termMatches = term.Candidates
                        .Select(static candidate => candidate.AccountId)
                        .Where(teamAccounts.Contains)
                        .ToList();

                    if (termMatches.Count == 0)
                    {
                        allTermsMatched = false;
                        break;
                    }

                    matchedAccountsForInterpretation.UnionWith(termMatches);
                }

                if (!allTermsMatched)
                    continue;

                if (!matches.TryGetValue(teamGroup.Key, out var match))
                {
                    match = new BandSearchTeamMatch();
                    matches[teamGroup.Key] = match;
                }

                match.InterpretationIds.Add(interpretation.Id);
                match.AccountIds.UnionWith(matchedAccountsForInterpretation);
            }
        }

        return matches;
    }

    private static IEnumerable<BandSearchTeamKey> OrderBandSearchTeamKeys(
        IEnumerable<BandSearchTeamKey> teamKeys,
        string rankBy,
        IReadOnlyDictionary<BandSearchTeamKey, int> appearanceLookup,
        IReadOnlyDictionary<BandSearchTeamKey, BandTeamRankingDto> rankingLookup) => rankBy switch
    {
        "adjusted" => teamKeys
            .OrderBy(key => rankingLookup.GetValueOrDefault(key)?.AdjustedSkillRank ?? int.MaxValue)
            .ThenByDescending(key => appearanceLookup.GetValueOrDefault(key))
            .ThenBy(key => key.TeamKey, StringComparer.OrdinalIgnoreCase),
        "weighted" => teamKeys
            .OrderBy(key => rankingLookup.GetValueOrDefault(key)?.WeightedRank ?? int.MaxValue)
            .ThenByDescending(key => appearanceLookup.GetValueOrDefault(key))
            .ThenBy(key => key.TeamKey, StringComparer.OrdinalIgnoreCase),
        "fcrate" => teamKeys
            .OrderBy(key => rankingLookup.GetValueOrDefault(key)?.FcRateRank ?? int.MaxValue)
            .ThenByDescending(key => appearanceLookup.GetValueOrDefault(key))
            .ThenBy(key => key.TeamKey, StringComparer.OrdinalIgnoreCase),
        "totalscore" => teamKeys
            .OrderBy(key => rankingLookup.GetValueOrDefault(key)?.TotalScoreRank ?? int.MaxValue)
            .ThenByDescending(key => appearanceLookup.GetValueOrDefault(key))
            .ThenBy(key => key.TeamKey, StringComparer.OrdinalIgnoreCase),
        _ => teamKeys
            .OrderByDescending(key => appearanceLookup.GetValueOrDefault(key))
            .ThenBy(key => key.TeamKey, StringComparer.OrdinalIgnoreCase),
    };

    private static Dictionary<BandSearchTeamKey, BandSearchProjectionTeamRow> GetBandSearchProjectedTeamRows(
        NpgsqlConnection conn,
        IReadOnlyCollection<BandSearchTeamKey> teamKeys)
    {
        var result = new Dictionary<BandSearchTeamKey, BandSearchProjectionTeamRow>();
        foreach (var teamGroup in teamKeys.GroupBy(static key => key.BandType, StringComparer.OrdinalIgnoreCase))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT band_type, team_key, band_id, appearance_count, member_account_ids, member_instruments_json
                FROM {BandSearchProjectionBuilder.TeamProjectionTable}
                WHERE band_type = @bandType
                  AND team_key = ANY(@teamKeys)
                """;
            cmd.Parameters.AddWithValue("bandType", teamGroup.Key);
            cmd.Parameters.AddWithValue("teamKeys", teamGroup.Select(static key => key.TeamKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new BandSearchProjectionTeamRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetFieldValue<string[]>(4),
                    ParseMemberInstrumentsJson(reader.IsDBNull(5) ? "{}" : reader.GetString(5)));
                result[new BandSearchTeamKey(row.BandType, row.TeamKey)] = row;
            }
        }

        return result;
    }

    private static BandSearchResultDto? BuildBandSearchProjectionResult(
        BandSearchTeamKey teamKey,
        BandSearchTeamMatch match,
        IReadOnlyDictionary<BandSearchTeamKey, BandSearchProjectionTeamRow> teamRows,
        IReadOnlyDictionary<string, string> displayNames,
        IReadOnlyDictionary<BandSearchTeamKey, BandTeamRankingDto> rankingLookup,
        int fallbackAppearanceCount)
    {
        if (!teamRows.TryGetValue(teamKey, out var row))
            return null;

        var bandId = string.IsNullOrWhiteSpace(row.BandId)
            ? BandIdentity.CreateBandId(teamKey.BandType, teamKey.TeamKey)
            : row.BandId;

        return new BandSearchResultDto
        {
            BandId = bandId,
            TeamKey = teamKey.TeamKey,
            BandType = teamKey.BandType,
            AppearanceCount = row.AppearanceCount > 0 ? row.AppearanceCount : fallbackAppearanceCount,
            Members = row.MemberAccountIds
                .Select(memberAccountId => new PlayerBandMemberDto
                {
                    AccountId = memberAccountId,
                    DisplayName = displayNames.GetValueOrDefault(memberAccountId),
                    Instruments = row.MemberInstruments.GetValueOrDefault(memberAccountId, []),
                })
                .ToList(),
            Ranking = rankingLookup.GetValueOrDefault(teamKey),
            MatchedInterpretationIds = match.InterpretationIds.Order().ToList(),
            MatchedAccountIds = match.AccountIds.Order(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private List<BandSearchInternalInterpretation> BuildBandSearchInterpretations(
        string normalizedQuery,
        IReadOnlyCollection<string>? explicitAccountIds)
    {
        var explicitIds = explicitAccountIds?
            .Where(static accountId => !string.IsNullOrWhiteSpace(accountId))
            .Select(static accountId => accountId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        if (explicitIds.Count > 0)
            return [BuildExplicitBandSearchInterpretation(explicitIds)];

        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return [];

        var tokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || tokens.Length > MaxBandSearchTokens)
            return [];

        var termCache = new Dictionary<(int Start, int End), BandSearchInternalTerm?>();
        BandSearchInternalTerm? ResolveSpan(int start, int end)
        {
            var key = (start, end);
            if (termCache.TryGetValue(key, out var cached))
                return cached;

            var text = string.Join(' ', tokens[start..end]);
            var term = ResolveBandSearchTerm(text);
            termCache[key] = term;
            return term;
        }

        var results = new List<BandSearchInternalInterpretation>();
        var terms = new List<BandSearchInternalTerm>();

        void Recurse(int index, double score)
        {
            if (index == tokens.Length)
            {
                if (terms.Count is > 0 and <= 4)
                {
                    results.Add(new BandSearchInternalInterpretation(
                        Id: results.Count + 1,
                        Score: Math.Round(score, 3),
                        IsExplicit: false,
                        Terms: terms.ToList()));
                }

                return;
            }

            if (terms.Count >= 4)
                return;

            for (var end = tokens.Length; end > index; end--)
            {
                var term = ResolveSpan(index, end);
                if (term is null)
                    continue;

                terms.Add(term);
                Recurse(end, score + ScoreBandSearchTerm(term, end - index));
                terms.RemoveAt(terms.Count - 1);
            }
        }

        Recurse(0, 0);

        return results
            .OrderByDescending(static interpretation => interpretation.Score)
            .ThenBy(static interpretation => interpretation.Terms.Count)
            .Take(MaxBandSearchInterpretations)
            .Select((interpretation, index) => interpretation with { Id = index + 1 })
            .ToList();
    }

    private BandSearchInternalInterpretation BuildExplicitBandSearchInterpretation(IReadOnlyList<string> accountIds)
    {
        var displayNames = _metaDb.GetDisplayNames(accountIds);
        var terms = accountIds
            .Select(accountId => new BandSearchInternalTerm(
                Text: displayNames.GetValueOrDefault(accountId) ?? accountId,
                MatchKind: "explicit",
                Candidates:
                [
                    new BandSearchCandidateDto
                    {
                        AccountId = accountId,
                        DisplayName = displayNames.GetValueOrDefault(accountId),
                    }
                ]))
            .ToList();

        return new BandSearchInternalInterpretation(
            Id: 1,
            Score: terms.Count * 250,
            IsExplicit: true,
            Terms: terms);
    }

    private BandSearchInternalTerm? ResolveBandSearchTerm(string text)
    {
        var matches = _metaDb.SearchAccountNames(text, Math.Max(50, MaxBandSearchCandidatesPerTerm * 4));
        if (matches.Count == 0)
            return null;

        var normalizedText = text.Trim().ToLowerInvariant();
        var exact = matches
            .Where(match => string.Equals(match.DisplayName, text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var prefix = matches
            .Where(match => match.DisplayName.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var selected = exact.Count > 0
            ? exact
            : prefix.Count > 0
                ? prefix
                : matches.Where(match => match.DisplayName.ToLowerInvariant().Contains(normalizedText, StringComparison.Ordinal)).ToList();

        if (selected.Count == 0)
            return null;

        var matchKind = exact.Count > 0 ? "exact" : prefix.Count > 0 ? "prefix" : "contains";
        var candidates = selected
            .GroupBy(static match => match.AccountId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(MaxBandSearchCandidatesPerTerm)
            .Select(static match => new BandSearchCandidateDto
            {
                AccountId = match.AccountId,
                DisplayName = match.DisplayName,
            })
            .ToList();

        return candidates.Count == 0
            ? null
            : new BandSearchInternalTerm(text, matchKind, candidates);
    }

    private static double ScoreBandSearchTerm(BandSearchInternalTerm term, int tokenCount)
    {
        var baseScore = term.MatchKind switch
        {
            "explicit" => 250,
            "exact" => 200,
            "prefix" => 100,
            _ => 50,
        };

        return baseScore + (tokenCount * 20) - ((term.Candidates.Count - 1) * 2);
    }

    private static Dictionary<BandSearchTeamKey, BandSearchTeamMatch> MatchBandSearchTeams(
        IReadOnlyList<PlayerBandMembershipSummaryRow> candidateRows,
        IReadOnlyList<BandSearchInternalInterpretation> interpretations)
    {
        var matches = new Dictionary<BandSearchTeamKey, BandSearchTeamMatch>();

        foreach (var teamGroup in candidateRows.GroupBy(static row => new BandSearchTeamKey(row.BandType, row.TeamKey)))
        {
            var teamAccounts = teamGroup
                .Select(static row => row.AccountId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var interpretation in interpretations)
            {
                var matchedAccountsForInterpretation = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allTermsMatched = true;

                foreach (var term in interpretation.Terms)
                {
                    var termMatches = term.Candidates
                        .Select(static candidate => candidate.AccountId)
                        .Where(teamAccounts.Contains)
                        .ToList();

                    if (termMatches.Count == 0)
                    {
                        allTermsMatched = false;
                        break;
                    }

                    matchedAccountsForInterpretation.UnionWith(termMatches);
                }

                if (!allTermsMatched)
                    continue;

                if (!matches.TryGetValue(teamGroup.Key, out var match))
                {
                    match = new BandSearchTeamMatch();
                    matches[teamGroup.Key] = match;
                }

                match.InterpretationIds.Add(interpretation.Id);
                match.AccountIds.UnionWith(matchedAccountsForInterpretation);
            }
        }

        return matches;
    }

    private static List<PlayerBandMembershipSummaryRow> GetBandSearchCandidateMembershipRows(
        NpgsqlConnection conn,
        IReadOnlyCollection<string> accountIds,
        string? bandTypeFilter)
    {
        if (accountIds.Count == 0)
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = bandTypeFilter is null
            ? $"""
                SELECT account_id, band_type, team_key, instrument_combo, appearance_count, member_instruments_json
                FROM {BandLeaderboardPersistence.BandTeamMembershipTable}
                WHERE account_id = ANY(@accountIds)
                ORDER BY account_id, band_type, team_key, instrument_combo
                """
            : $"""
                SELECT account_id, band_type, team_key, instrument_combo, appearance_count, member_instruments_json
                FROM {BandLeaderboardPersistence.BandTeamMembershipTable}
                WHERE account_id = ANY(@accountIds)
                  AND band_type = @bandType
                ORDER BY account_id, band_type, team_key, instrument_combo
                """;
        cmd.Parameters.AddWithValue("accountIds", accountIds.ToArray());
        if (bandTypeFilter is not null)
            cmd.Parameters.AddWithValue("bandType", bandTypeFilter);

        return ReadBandMembershipSummaryRows(cmd);
    }

    private static Dictionary<BandSearchTeamKey, List<PlayerBandMembershipSummaryRow>> GetBandSearchTeamMembershipRows(
        NpgsqlConnection conn,
        IReadOnlyCollection<BandSearchTeamKey> teamKeys)
    {
        var result = new Dictionary<BandSearchTeamKey, List<PlayerBandMembershipSummaryRow>>();
        foreach (var teamGroup in teamKeys.GroupBy(static key => key.BandType, StringComparer.OrdinalIgnoreCase))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT account_id, band_type, team_key, instrument_combo, appearance_count, member_instruments_json
                FROM {BandLeaderboardPersistence.BandTeamMembershipTable}
                WHERE band_type = @bandType
                  AND team_key = ANY(@teamKeys)
                ORDER BY band_type, team_key, instrument_combo, account_id
                """;
            cmd.Parameters.AddWithValue("bandType", teamGroup.Key);
            cmd.Parameters.AddWithValue("teamKeys", teamGroup.Select(static key => key.TeamKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

            foreach (var row in ReadBandMembershipSummaryRows(cmd))
            {
                var key = new BandSearchTeamKey(row.BandType, row.TeamKey);
                if (!result.TryGetValue(key, out var rows))
                {
                    rows = [];
                    result[key] = rows;
                }

                rows.Add(row);
            }
        }

        return result;
    }

    private static List<PlayerBandMembershipSummaryRow> ReadBandMembershipSummaryRows(NpgsqlCommand cmd)
    {
        var rows = new List<PlayerBandMembershipSummaryRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PlayerBandMembershipSummaryRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.GetInt32(4),
                ParseMemberInstrumentsJson(reader.IsDBNull(5) ? "{}" : reader.GetString(5))));
        }

        return rows;
    }

    private BandSearchResultDto BuildBandSearchResult(
        BandSearchTeamKey teamKey,
        BandSearchTeamMatch match,
        IReadOnlyDictionary<BandSearchTeamKey, List<PlayerBandMembershipSummaryRow>> teamRows,
        IReadOnlyDictionary<string, string> displayNames,
        IReadOnlyDictionary<BandSearchTeamKey, BandTeamRankingDto> rankingLookup)
    {
        var rows = teamRows.GetValueOrDefault(teamKey) ?? [];
        var memberInstruments = BuildMemberInstrumentsForSummaryGroup(rows);
        var appearanceCount = rows
            .GroupBy(static row => row.InstrumentCombo, StringComparer.OrdinalIgnoreCase)
            .Sum(static group => group.Max(static row => row.AppearanceCount));

        return new BandSearchResultDto
        {
            BandId = BandIdentity.CreateBandId(teamKey.BandType, teamKey.TeamKey),
            TeamKey = teamKey.TeamKey,
            BandType = teamKey.BandType,
            AppearanceCount = appearanceCount,
            Members = SplitTeamKey(teamKey.TeamKey)
                .Select(memberAccountId => new PlayerBandMemberDto
                {
                    AccountId = memberAccountId,
                    DisplayName = displayNames.GetValueOrDefault(memberAccountId),
                    Instruments = memberInstruments.GetValueOrDefault(memberAccountId, []),
                })
                .ToList(),
            Ranking = rankingLookup.GetValueOrDefault(teamKey),
            MatchedInterpretationIds = match.InterpretationIds.Order().ToList(),
            MatchedAccountIds = match.AccountIds.Order(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private Dictionary<BandSearchTeamKey, BandTeamRankingDto> GetBandSearchRankingsForTeams(
        IReadOnlyCollection<BandSearchTeamKey> teamKeys,
        string? comboIdFilter)
    {
        var result = new Dictionary<BandSearchTeamKey, BandTeamRankingDto>();
        if (teamKeys.Count == 0)
            return result;

        using var conn = _pgDataSource.OpenConnection();
        var rankingScope = string.IsNullOrWhiteSpace(comboIdFilter) ? "overall" : "combo";
        var normalizedComboId = comboIdFilter ?? string.Empty;

        foreach (var teamGroup in teamKeys.GroupBy(static key => key.BandType, StringComparer.OrdinalIgnoreCase))
        {
            var bandType = teamGroup.Key;
            var totalRankedTeams = GetBandSearchTotalRankedTeams(conn, bandType, rankingScope, normalizedComboId);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT
                    band_type, combo_id, team_key, team_members, songs_played, total_charted_songs,
                    coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank,
                    weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score,
                    total_score_rank, avg_accuracy, full_combo_count, avg_stars, best_rank,
                    avg_rank, raw_weighted_rating, computed_at
                FROM {BandRankingStorageNames.QuoteIdentifier(BandRankingStorageNames.GetCurrentRankingTable(bandType))}
                WHERE band_type = @bandType
                  AND ranking_scope = @scope
                  AND combo_id = @comboId
                  AND team_key = ANY(@teamKeys)";
            cmd.Parameters.AddWithValue("bandType", bandType);
            cmd.Parameters.AddWithValue("scope", rankingScope);
            cmd.Parameters.AddWithValue("comboId", normalizedComboId);
            cmd.Parameters.AddWithValue("teamKeys", teamGroup.Select(static key => key.TeamKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var ranking = ReadBandSearchRanking(reader, totalRankedTeams);
                result[new BandSearchTeamKey(ranking.BandType, ranking.TeamKey)] = ranking;
            }
        }

        return result;
    }

    private static int GetBandSearchTotalRankedTeams(NpgsqlConnection conn, string bandType, string rankingScope, string comboId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT total_teams
            FROM {BandRankingStorageNames.QuoteIdentifier(BandRankingStorageNames.GetCurrentStatsTable(bandType))}
            WHERE band_type = @bandType AND ranking_scope = @scope AND combo_id = @comboId";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", comboId);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? 0 : Convert.ToInt32(result);
    }

    private static BandTeamRankingDto ReadBandSearchRanking(NpgsqlDataReader r, int totalRankedTeams) => new()
    {
        BandId = BandIdentity.CreateBandId(r.GetString(0), r.GetString(2)),
        BandType = r.GetString(0),
        ComboId = string.IsNullOrEmpty(r.GetString(1)) ? null : r.GetString(1),
        TeamKey = r.GetString(2),
        TeamMembers = r.GetFieldValue<string[]>(3),
        SongsPlayed = r.GetInt32(4),
        TotalChartedSongs = r.GetInt32(5),
        Coverage = r.GetDouble(6),
        RawSkillRating = r.GetDouble(7),
        AdjustedSkillRating = r.GetDouble(8),
        AdjustedSkillRank = r.GetInt32(9),
        WeightedRating = r.GetDouble(10),
        WeightedRank = r.GetInt32(11),
        FcRate = r.GetDouble(12),
        FcRateRank = r.GetInt32(13),
        TotalScore = r.GetInt64(14),
        TotalScoreRank = r.GetInt32(15),
        AvgAccuracy = r.GetDouble(16),
        FullComboCount = r.GetInt32(17),
        AvgStars = r.GetDouble(18),
        BestRank = r.GetInt32(19),
        AvgRank = r.GetDouble(20),
        RawWeightedRating = r.IsDBNull(21) ? null : r.GetDouble(21),
        ComputedAt = r.GetDateTime(22).ToString("o"),
        TotalRankedTeams = totalRankedTeams,
    };

    private static IEnumerable<BandSearchResultDto> OrderBandSearchResults(
        IEnumerable<BandSearchResultDto> results,
        string rankBy) => rankBy switch
    {
        "adjusted" => results
            .OrderBy(static result => result.Ranking?.AdjustedSkillRank ?? int.MaxValue)
            .ThenByDescending(static result => result.AppearanceCount)
            .ThenBy(static result => result.TeamKey, StringComparer.OrdinalIgnoreCase),
        "weighted" => results
            .OrderBy(static result => result.Ranking?.WeightedRank ?? int.MaxValue)
            .ThenByDescending(static result => result.AppearanceCount)
            .ThenBy(static result => result.TeamKey, StringComparer.OrdinalIgnoreCase),
        "fcrate" => results
            .OrderBy(static result => result.Ranking?.FcRateRank ?? int.MaxValue)
            .ThenByDescending(static result => result.AppearanceCount)
            .ThenBy(static result => result.TeamKey, StringComparer.OrdinalIgnoreCase),
        "totalscore" => results
            .OrderBy(static result => result.Ranking?.TotalScoreRank ?? int.MaxValue)
            .ThenByDescending(static result => result.AppearanceCount)
            .ThenBy(static result => result.TeamKey, StringComparer.OrdinalIgnoreCase),
        _ => results
            .OrderByDescending(static result => result.AppearanceCount)
            .ThenBy(static result => result.TeamKey, StringComparer.OrdinalIgnoreCase),
    };

    private static BandSearchResponseDto CreateBandSearchResponse(
        string query,
        string normalizedQuery,
        string? bandType,
        string? comboId,
        string rankBy,
        int page,
        int pageSize,
        bool needsDisambiguation,
        IReadOnlyList<BandSearchInternalInterpretation> interpretations,
        IReadOnlyList<BandSearchResultDto> results,
        int totalCount)
    {
        var matchCounts = results
            .SelectMany(static result => result.MatchedInterpretationIds)
            .GroupBy(static id => id)
            .ToDictionary(static group => group.Key, static group => group.Count());

        return new BandSearchResponseDto
        {
            Query = query,
            NormalizedQuery = normalizedQuery,
            BandType = bandType,
            ComboId = comboId,
            RankBy = rankBy,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            IsAmbiguous = interpretations.Count > 1 || interpretations.Any(static interpretation => interpretation.Terms.Any(static term => term.Candidates.Count > 1)),
            NeedsDisambiguation = needsDisambiguation,
            Interpretations = interpretations
                .Select(interpretation => new BandSearchInterpretationDto
                {
                    Id = interpretation.Id,
                    Score = interpretation.Score,
                    IsExplicit = interpretation.IsExplicit,
                    MatchCount = matchCounts.GetValueOrDefault(interpretation.Id),
                    Terms = interpretation.Terms
                        .Select(static term => new BandSearchTermDto
                        {
                            Text = term.Text,
                            MatchKind = term.MatchKind,
                            Candidates = term.Candidates,
                        })
                        .ToList(),
                })
                .ToList(),
            Results = results.ToList(),
        };
    }

    private static string NormalizeBandSearchQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var normalized = string.Join(' ', query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= MaxBandSearchQueryLength
            ? normalized
            : normalized[..MaxBandSearchQueryLength];
    }

    private static string NormalizeBandSearchRankBy(string? rankBy) => rankBy?.Trim().ToLowerInvariant() switch
    {
        "adjusted" => "adjusted",
        "weighted" => "weighted",
        "fcrate" => "fcrate",
        "totalscore" => "totalscore",
        _ => "appearance",
    };

    private static List<PlayerBandMembershipSummaryRow> GetPlayerBandMembershipRows(
        NpgsqlConnection conn,
        string accountId,
        string? bandTypeFilter)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = bandTypeFilter is null
            ? $"""
                SELECT account_id, band_type, team_key, instrument_combo, appearance_count, member_instruments_json
                FROM {BandLeaderboardPersistence.BandTeamMembershipTable}
                WHERE account_id = @accountId
                ORDER BY band_type, team_key, instrument_combo
                """
            : $"""
                SELECT account_id, band_type, team_key, instrument_combo, appearance_count, member_instruments_json
                FROM {BandLeaderboardPersistence.BandTeamMembershipTable}
                WHERE account_id = @accountId
                  AND band_type = @bandType
                ORDER BY band_type, team_key, instrument_combo
                """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        if (bandTypeFilter is not null)
            cmd.Parameters.AddWithValue("bandType", bandTypeFilter);

        var rows = new List<PlayerBandMembershipSummaryRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PlayerBandMembershipSummaryRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.GetInt32(4),
                ParseMemberInstrumentsJson(reader.IsDBNull(5) ? "{}" : reader.GetString(5))));
        }

        return rows;
    }

    private static Dictionary<string, List<string>> BuildMemberInstrumentsForSummaryGroup(
        IEnumerable<PlayerBandMembershipSummaryRow> rows)
    {
        var instrumentsByMember = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            foreach (var (memberAccountId, instruments) in row.MemberInstruments)
            {
                if (!instrumentsByMember.TryGetValue(memberAccountId, out var memberInstruments))
                {
                    memberInstruments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    instrumentsByMember[memberAccountId] = memberInstruments;
                }

                memberInstruments.UnionWith(instruments);
            }
        }

        return instrumentsByMember.ToDictionary(
            static kvp => kvp.Key,
            static kvp => BandComboIds.ToInstruments(BandComboIds.FromInstruments(kvp.Value)).ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, List<string>> ParseMemberInstrumentsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var instruments = property.Value.ValueKind == JsonValueKind.Array
                ? property.Value.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString())
                    .Where(static instrument => !string.IsNullOrWhiteSpace(instrument))
                    .Select(static instrument => instrument!)
                    .ToList()
                : [];

            result[property.Name] = instruments;
        }

        return result;
    }

    private static List<PlayerBandTeamSummary> GetPlayerBandTeams(
        NpgsqlConnection conn,
        string accountId,
        string? bandTypeFilter,
        string? comboIdFilter)
    {
        var appearanceCounts = new Dictionary<(string BandType, string TeamKey), int>();

        using var teamCmd = conn.CreateCommand();
        teamCmd.CommandText = bandTypeFilter is null
            ? """
                SELECT DISTINCT band_type, team_key, song_id, instrument_combo
                FROM band_members
                WHERE account_id = @accountId
                ORDER BY band_type, team_key, song_id, instrument_combo
                """
            : """
                SELECT DISTINCT band_type, team_key, song_id, instrument_combo
                FROM band_members
                WHERE account_id = @accountId
                  AND band_type = @bandType
                ORDER BY band_type, team_key, song_id, instrument_combo
                """;
        teamCmd.Parameters.AddWithValue("accountId", accountId);
        if (bandTypeFilter is not null)
            teamCmd.Parameters.AddWithValue("bandType", bandTypeFilter);

        using var reader = teamCmd.ExecuteReader();
        while (reader.Read())
        {
            var bandType = reader.GetString(0);
            var teamKey = reader.GetString(1);
            var rawCombo = reader.IsDBNull(3) ? null : reader.GetString(3);

            if (comboIdFilter is not null)
            {
                var normalizedComboId = BandComboIds.FromEpicRawCombo(rawCombo);
                if (!string.Equals(normalizedComboId, comboIdFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var dedupeKey = (bandType, teamKey);
            appearanceCounts[dedupeKey] = appearanceCounts.GetValueOrDefault(dedupeKey) + 1;
        }

        return appearanceCounts
            .Select(kvp => new PlayerBandTeamSummary(kvp.Key.BandType, kvp.Key.TeamKey, kvp.Value))
            .OrderByDescending(team => team.AppearanceCount)
            .ThenBy(team => team.TeamKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<(string BandType, string TeamKey, string AccountId), List<string>> GetPlayerBandInstrumentsByMember(
        NpgsqlConnection conn,
        string accountId,
        List<PlayerBandTeamSummary> teams,
        string? bandTypeFilter,
        string? comboIdFilter)
    {
        var selectedTeams = new HashSet<string>(teams.Select(static team => $"{team.BandType}:{team.TeamKey}"), StringComparer.OrdinalIgnoreCase);
        var instrumentsByMember = new Dictionary<(string BandType, string TeamKey, string AccountId), HashSet<string>>();

        using var memberCmd = conn.CreateCommand();
        memberCmd.CommandText = bandTypeFilter is null
            ? """
                WITH player_teams AS (
                    SELECT DISTINCT band_type, team_key
                    FROM band_members
                    WHERE account_id = @accountId
                )
                SELECT pt.band_type,
                       pt.team_key,
                       bms.account_id,
                       bms.instrument_combo,
                       bms.instrument_id
                FROM player_teams pt
                JOIN band_member_stats bms
                  ON pt.band_type = bms.band_type
                 AND pt.team_key = bms.team_key
                ORDER BY pt.band_type, pt.team_key, bms.account_id, bms.instrument_combo, bms.instrument_id
                """
            : """
                WITH player_teams AS (
                    SELECT DISTINCT band_type, team_key
                    FROM band_members
                    WHERE account_id = @accountId
                      AND band_type = @bandType
                )
                SELECT pt.band_type,
                       pt.team_key,
                       bms.account_id,
                       bms.instrument_combo,
                       bms.instrument_id
                FROM player_teams pt
                JOIN band_member_stats bms
                  ON pt.band_type = bms.band_type
                 AND pt.team_key = bms.team_key
                ORDER BY pt.band_type, pt.team_key, bms.account_id, bms.instrument_combo, bms.instrument_id
                """;
        memberCmd.Parameters.AddWithValue("accountId", accountId);
        if (bandTypeFilter is not null)
            memberCmd.Parameters.AddWithValue("bandType", bandTypeFilter);

        using var reader = memberCmd.ExecuteReader();
        while (reader.Read())
        {
            var bandType = reader.GetString(0);
            var teamKey = reader.GetString(1);
            var teamLookupKey = $"{bandType}:{teamKey}";
            if (!selectedTeams.Contains(teamLookupKey))
                continue;

            var rawCombo = reader.IsDBNull(3) ? null : reader.GetString(3);
            if (comboIdFilter is not null)
            {
                var rowComboId = BandComboIds.FromEpicRawCombo(rawCombo);
                if (!string.Equals(rowComboId, comboIdFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (reader.IsDBNull(4))
                continue;

            var instrument = BandInstrumentMapping.ToLeaderboardType(reader.GetInt32(4));
            if (string.IsNullOrWhiteSpace(instrument))
                continue;

            var memberKey = (bandType, teamKey, reader.GetString(2));
            if (!instrumentsByMember.TryGetValue(memberKey, out var instruments))
            {
                instruments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                instrumentsByMember[memberKey] = instruments;
            }

            instruments.Add(instrument);
        }

        return instrumentsByMember.ToDictionary(
            kvp => kvp.Key,
            kvp => BandComboIds.ToInstruments(BandComboIds.FromInstruments(kvp.Value)).ToList());
    }

    private static BandIdentityLookup? ResolveBandIdentity(NpgsqlConnection conn, string bandId)
    {
        using (var projection = conn.CreateCommand())
        {
            projection.CommandText = $"""
                SELECT band_type, team_key, appearance_count, member_account_ids, member_instruments_json::text
                FROM {BandSearchProjectionBuilder.TeamProjectionTable}
                WHERE band_id = @bandId
                LIMIT 1
                """;
            projection.Parameters.AddWithValue("bandId", bandId);

            using var reader = projection.ExecuteReader();
            if (reader.Read())
            {
                return new BandIdentityLookup(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetFieldValue<string[]>(3).ToList(),
                    ParseMemberInstrumentsJson(reader.IsDBNull(4) ? "{}" : reader.GetString(4)));
            }
        }

        string? registeredBandType = null;
        string? registeredTeamKey = null;
        using (var registered = conn.CreateCommand())
        {
            registered.CommandText = """
                SELECT band_type, team_key
                FROM registered_bands
                WHERE band_id = @bandId
                ORDER BY last_activity_at DESC NULLS LAST, registered_at DESC
                LIMIT 1
                """;
            registered.Parameters.AddWithValue("bandId", bandId);

            using var reader = registered.ExecuteReader();
            if (reader.Read())
            {
                registeredBandType = reader.GetString(0);
                registeredTeamKey = reader.GetString(1);
            }
        }

        if (registeredBandType is not null && registeredTeamKey is not null)
        {
            var projected = ResolveBandIdentityFromProjectionTeam(conn, registeredBandType, registeredTeamKey);
            return projected ?? new BandIdentityLookup(
                registeredBandType,
                registeredTeamKey,
                null,
                SplitTeamKey(registeredTeamKey),
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase));
        }

        return null;
    }

    private static BandIdentityLookup? ResolveBandIdentityFromProjectionTeam(NpgsqlConnection conn, string bandType, string teamKey)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT band_type, team_key, appearance_count, member_account_ids, member_instruments_json::text
            FROM {BandSearchProjectionBuilder.TeamProjectionTable}
            WHERE band_type = @bandType
              AND team_key = @teamKey
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new BandIdentityLookup(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetFieldValue<string[]>(3).ToList(),
            ParseMemberInstrumentsJson(reader.IsDBNull(4) ? "{}" : reader.GetString(4)));
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

    public Dictionary<(string SongId, string Instrument), int> GetCurrentStatePlayerRankings(string accountId, string? songId = null, HashSet<string>? instruments = null)
    {
        var kvps = instruments is null
            ? _instrumentDbs.ToArray()
            : _instrumentDbs.Where(kv => instruments.Contains(kv.Key)).ToArray();

        var perInstrument = new Dictionary<string, int>[kvps.Length];
        var instrumentKeys = new string[kvps.Length];
        Parallel.For(0, kvps.Length, i =>
        {
            instrumentKeys[i] = kvps[i].Key;
            perInstrument[i] = kvps[i].Value.GetCurrentStatePlayerRankings(accountId, songId);
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

    public Dictionary<(string SongId, string Instrument), int> GetCurrentStatePlayerRankingsFiltered(
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
                perInstrument[i] = kvps[i].Value.GetCurrentStatePlayerRankingsFiltered(accountId, thresholds, songId);
            else
                perInstrument[i] = kvps[i].Value.GetCurrentStatePlayerRankings(accountId, songId);
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

    public int GetCurrentStateRankForScore(string instrument, string songId, int score, int? maxScore = null)
    {
        if (!_instrumentDbs.TryGetValue(instrument, out var db))
            return 0;
        return db.GetCurrentStateRankForScore(songId, score, maxScore);
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

    public Dictionary<(string SongId, string Instrument), int> GetCurrentStateFilteredPopulation(
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
                perInstrument[i] = kvps[i].Value.GetCurrentStateFilteredEntryCounts(thresholds);
            else
                perInstrument[i] = kvps[i].Value.GetCurrentStateAllSongCounts();
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
    /// Get the current-state leaderboard for a specific song + instrument,
    /// resolving snapshot + overlay precedence when a finalized snapshot exists.
    /// Returns null if the instrument is unknown.
    /// </summary>
    public List<LeaderboardEntryDto>? GetCurrentStateLeaderboard(string songId, string instrument, int? top = null, int offset = 0)
    {
        if (!_instrumentDbs.TryGetValue(instrument, out var db))
            return null;
        return db.GetCurrentStateLeaderboard(songId, top, offset);
    }

    /// <summary>
    /// Get the current-state leaderboard with count in a single query.
    /// Returns null if the instrument is unknown.
    /// </summary>
    public (List<LeaderboardEntryDto> Entries, int TotalCount)? GetCurrentStateLeaderboardWithCount(
        string songId, string instrument, int? top = null, int offset = 0, int? maxScore = null)
    {
        if (!_instrumentDbs.TryGetValue(instrument, out var db))
            return null;
        return db.GetCurrentStateLeaderboardWithCount(songId, top, offset, maxScore);
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

        var knownDbs = _instrumentDbs.Where(kvp => ValidInstrumentKeys.Contains(kvp.Key)).ToList();

        if (_pgDataSource is not null)
        {
            // PostgreSQL mode: limited parallelism — each instrument partition is
            // independent, but too many concurrent massive UPDATEs contend for WAL
            // writer. DOP=2 balances throughput vs contention.
            Parallel.ForEach(knownDbs, new ParallelOptions { MaxDegreeOfParallelism = 2 }, kvp =>
            {
                var updated = kvp.Value.RecomputeAllRanks();
                results[kvp.Key] = updated;
            });
        }
        else
        {
            // SQLite mode: each instrument has its own DB file — run in parallel.
            Parallel.ForEach(knownDbs, kvp =>
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
        if (!WriteLegacyLiveLeaderboardDuringScrape)
        {
            _log.LogInformation("Skipping legacy live leaderboard rank recompute for {SongCount} songs because legacy live scrape writes are disabled.",
                songIds.Count);
            return 0;
        }

        if (songIds.Count == 0)
            return RecomputeAllRanks();

        var results = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var knownDbs = _instrumentDbs.Where(kvp => ValidInstrumentKeys.Contains(kvp.Key)).ToList();

        if (_pgDataSource is not null)
        {
            // PostgreSQL mode: limited parallelism — bulk query per instrument is
            // a single UPDATE, so WAL contention is manageable at DOP=2.
            Parallel.ForEach(knownDbs, new ParallelOptions { MaxDegreeOfParallelism = 2 }, kvp =>
            {
                var updated = kvp.Value.RecomputeRanksForSongs(songIds);
                results[kvp.Key] = updated;
            });
        }
        else
        {
            Parallel.ForEach(knownDbs, kvp =>
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

    private static PlayerBandsDto CreateEmptyPlayerBands() => new();

    private static PlayerBandGroupDto BuildBandGroup(List<PlayerBandEntryDto> entries, int previewCount)
    {
        return new PlayerBandGroupDto
        {
            TotalCount = entries.Count,
            Entries = entries.Take(previewCount).ToList(),
        };
    }

    private static PlayerBandGroupDto BuildAllBandGroup(string accountId, List<PlayerBandEntryDto> entries, int previewCount)
    {
        return new PlayerBandGroupDto
        {
            TotalCount = entries.Count,
            Entries = entries.Take(previewCount).ToList(),
        };
    }

    private static List<string> SplitTeamKey(string teamKey)
    {
        return teamKey
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private sealed record BandIdentityLookup(
        string BandType,
        string TeamKey,
        int? AppearanceCount,
        List<string> MemberAccountIds,
        Dictionary<string, List<string>> MemberInstruments);
    private sealed record PlayerBandMembershipSummaryRow(
        string AccountId,
        string BandType,
        string TeamKey,
        string InstrumentCombo,
        int AppearanceCount,
        Dictionary<string, List<string>> MemberInstruments);
    private sealed record BandSearchProjectionMemberRow(
        string AccountId,
        string BandType,
        string TeamKey,
        int AppearanceCount,
        int TeamAppearanceCount,
        string[] InstrumentCombos);
    private sealed record BandSearchProjectionTeamRow(
        string BandType,
        string TeamKey,
        string BandId,
        int AppearanceCount,
        string[] MemberAccountIds,
        Dictionary<string, List<string>> MemberInstruments);
    private sealed record PlayerBandTeamSummary(string BandType, string TeamKey, int AppearanceCount);
    private sealed record BandSearchTeamKey(string BandType, string TeamKey);
    private sealed record BandSearchInternalTerm(string Text, string MatchKind, List<BandSearchCandidateDto> Candidates);
    private sealed record BandSearchInternalInterpretation(int Id, double Score, bool IsExplicit, List<BandSearchInternalTerm> Terms);
    private sealed class BandSearchTeamMatch
    {
        public HashSet<int> InterpretationIds { get; } = [];
        public HashSet<string> AccountIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

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

        if (!WriteLegacyLiveLeaderboardDuringScrape)
        {
            _log.LogInformation("Skipping legacy live leaderboard excess prune because legacy live scrape writes are disabled.");
            return 0;
        }

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
        CleanupActiveScrapeWritersAsync().AsTask().GetAwaiter().GetResult();
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
    /// Account IDs of registered users whose own score changed or whose song-rivals
    /// neighborhood changed in this result.
    /// Used to flag stale rivals for refresh.
    /// </summary>
    public HashSet<string> ChangedAccountIds { get; init; } = [];

    /// <summary>
    /// Song-level dirty evidence used to decide whether rivals actually need recomputation.
    /// </summary>
    public IReadOnlyList<RivalDirtySongRow> DirtyRivalSongs { get; init; } = Array.Empty<RivalDirtySongRow>();
}
