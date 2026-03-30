using System.Collections.Concurrent;
using System.Threading.Channels;
using FSTService.Scraping;

namespace FSTService.Persistence;

/// <summary>
/// Coordinates the per-instrument sharded databases and the central meta DB.
/// This is the single entry point that <see cref="ScraperWorker"/> uses to
/// persist global leaderboard results.
///
/// During a scrape pass, persistence is fully pipelined via per-instrument
/// <see cref="Channel{T}"/> writers.  Each of the 6 instruments has its own
/// dedicated writer task that drains work items and writes to its own SQLite
/// file — zero cross-instrument contention.
///
/// File layout (all under the configured data directory):
///   fst-meta.db                  ← ScrapeLog, ScoreHistory, AccountNames, RegisteredUsers
///   fst-Solo_Guitar.db           ← LeaderboardEntries for Guitar
///   fst-Solo_Bass.db             ← LeaderboardEntries for Bass
///   …one per instrument…
/// </summary>
public sealed class GlobalLeaderboardPersistence : IDisposable
{
    private readonly Dictionary<string, InstrumentDatabase> _instrumentDbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly MetaDatabase _metaDb;
    private readonly ILogger<GlobalLeaderboardPersistence> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _dataDir;

    /// <summary>The meta database (ScrapeLog, ScoreHistory, etc.).</summary>
    public MetaDatabase Meta => _metaDb;

    public GlobalLeaderboardPersistence(string dataDir, MetaDatabase metaDb,
                                        ILoggerFactory loggerFactory,
                                        ILogger<GlobalLeaderboardPersistence> log)
    {
        _dataDir = dataDir;
        _metaDb = metaDb;
        _loggerFactory = loggerFactory;
        _log = log;

        if (!Directory.Exists(dataDir))
            Directory.CreateDirectory(dataDir);
    }

    /// <summary>
    /// Ensure all schemas exist (meta DB + one instrument DB per known instrument).
    /// Call once at startup before the first scrape pass.
    /// </summary>
    public void Initialize()
    {
        _metaDb.EnsureSchema();

        // Initialize all 6 instrument DBs in parallel (each has its own file, zero contention)
        var instruments = GlobalLeaderboardScraper.AllInstruments;
        var dbs = new InstrumentDatabase[instruments.Count];
        Parallel.For(0, instruments.Count, i =>
        {
            var instrument = instruments[i];
            var dbPath = Path.Combine(_dataDir, $"fst-{instrument}.db");
            var db = new InstrumentDatabase(
                instrument, dbPath,
                _loggerFactory.CreateLogger<InstrumentDatabase>());
            db.EnsureSchema();
            dbs[i] = db;
            _log.LogDebug("Opened instrument DB: {Instrument} \u2192 {Path}", instrument, dbPath);
        });

        // Add to dictionary after parallel init completes (single-threaded)
        foreach (var db in dbs)
            _instrumentDbs[db.Instrument] = db;

        _log.LogInformation("GlobalLeaderboardPersistence initialized. " +
                            "{InstrumentCount} instrument DBs in {DataDir}",
                            _instrumentDbs.Count, _dataDir);
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
    /// Get (or create on first access) the <see cref="InstrumentDatabase"/>
    /// for a given instrument key (e.g. "Solo_Guitar").
    /// New instruments added in the future are automatically handled.
    /// </summary>
    public InstrumentDatabase GetOrCreateInstrumentDb(string instrument)
    {
        if (_instrumentDbs.TryGetValue(instrument, out var db))
            return db;

        var dbPath = Path.Combine(_dataDir, $"fst-{instrument}.db");
        db = new InstrumentDatabase(
            instrument, dbPath,
            _loggerFactory.CreateLogger<InstrumentDatabase>());
        db.EnsureSchema();
        _instrumentDbs[instrument] = db;

        _log.LogDebug("Opened instrument DB: {Instrument} → {Path}", instrument, dbPath);
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
                                       IReadOnlySet<string>? registeredAccountIds = null)
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

        // ── UPSERT all entries in one transaction ──
        var rowsAffected = db.UpsertEntries(result.SongId, result.Entries);

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
        _metaDb.InsertAccountIds(result.Entries.Select(e => e.AccountId));

        return new PersistResult
        {
            RowsAffected = rowsAffected,
            ScoreChangesDetected = scoreChanges.Count,
            ChangedAccountIds = changedAccountIds,
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

        public void AddEntries(int count) => Interlocked.Add(ref _totalEntries, count);
        public void AddChanges(int count) => Interlocked.Add(ref _totalChanges, count);
        public void IncrementSongsWithData() => Interlocked.Increment(ref _songsWithData);
        public void AddChangedAccountIds(IEnumerable<string> ids) => _changedAccountIds.AddRange(ids);

        /// <summary>Record which registered user entries were seen in this pass.</summary>
        public void AddSeenRegisteredEntries(IEnumerable<(string, string, string)> entries)
        {
            foreach (var e in entries) _seenRegisteredEntries.TryAdd(e, 0);
        }

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
    /// </summary>
    /// <param name="channelCapacity">Per-instrument channel capacity (default 32).</param>
    public PipelineAggregates StartWriters(int channelCapacity = 32, CancellationToken ct = default)
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
                await foreach (var item in channel.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        var persistResult = PersistResult(item.Result, item.RegisteredAccountIds);
                        agg.AddChangedAccountIds(persistResult.ChangedAccountIds);
                        agg.AddEntries(item.Result.Entries.Count);
                        agg.AddChanges(persistResult.ScoreChangesDetected);

                        // Track which registered users were seen in this result
                        if (item.RegisteredAccountIds is { Count: > 0 })
                        {
                            var seen = item.Result.Entries
                                .Where(e => item.RegisteredAccountIds.Contains(e.AccountId))
                                .Select(e => (e.AccountId, item.Result.SongId, item.Result.Instrument));
                            agg.AddSeenRegisteredEntries(seen);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Writer error for {Instrument}/{SongId}",
                            item.Result.Instrument, item.Result.SongId);
                    }
                }
            }, ct);

            _writerTasks.Add(task);
        }

        _log.LogInformation("Started {Count} per-instrument writer tasks.", _writerTasks.Count);
        return _aggregates;
    }

    /// <summary>
    /// Enqueue a single instrument result for asynchronous persistence.
    /// Non-blocking unless the channel is full (capacity 32), in which case
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
    /// Run WAL checkpoints on all instrument databases and the meta database.
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
    /// <see cref="MetaDatabase.GetAllLeaderboardPopulation"/> instead.
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
        // Each instrument has its own DB file and _writeLock — run all 6 in parallel.
        var results = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(_instrumentDbs, kvp =>
        {
            var updated = kvp.Value.RecomputeAllRanks();
            results[kvp.Key] = updated;
        });

        int total = 0;
        foreach (var (instrument, updated) in results)
        {
            _log.LogInformation("Recomputed ranks for {Instrument}: {Updated} entries.", instrument, updated);
            total += updated;
        }
        return total;
    }

    /// <summary>
    /// Get a list of all known instrument keys.
    /// </summary>
    public IReadOnlyList<string> GetInstrumentKeys()
        => _instrumentDbs.Keys.ToList();

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

    /// <summary>
    /// Account IDs of registered users whose scores changed in this result.
    /// Used to flag personal DB rebuilds.
    /// </summary>
    public HashSet<string> ChangedAccountIds { get; init; } = [];
}
