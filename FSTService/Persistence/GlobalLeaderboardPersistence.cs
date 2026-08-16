using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using FSTService.Scraping;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

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
    private volatile bool _useValidatedCurrentProjectionForWorkerReaders;
    private int _maxScoreMaintenancePublishedReadPass;
    private readonly IMetaDatabase _metaDb;
    private readonly ILogger<GlobalLeaderboardPersistence> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly NpgsqlDataSource _pgDataSource;
    private readonly FeatureOptions _features;
    private readonly ConcurrentDictionary<(long ScrapeId, string SongId, string Instrument), ScopeCompletenessManifest>
        _snapshotReuseManifests = new();

    /// <summary>The meta database (ScrapeLog, ScoreHistory, etc.).</summary>
    public IMetaDatabase Meta => _metaDb;

    /// <summary>
    /// True when scrape flushes should maintain the legacy mutable leaderboard_entries table.
    /// Snapshot current-state rows are written independently of this rollback flag.
    /// </summary>
    public bool WriteLegacyLiveLeaderboardDuringScrape => _features.WriteLegacyLiveLeaderboardDuringScrape;

    /// <summary>
    /// True when scrape flushes should compute observe-only scope fingerprints.
    /// Existing snapshot/current-state rows remain authoritative.
    /// </summary>
    public bool UseLeaderboardScopeFingerprints => _features.UseLeaderboardScopeFingerprints;

    /// <summary>
    /// True when unchanged all-time solo scopes should keep using their validated
    /// published physical source instead of writing duplicate rows for the current
    /// scrape. Complete manifests, fingerprints, and published-source writes are
    /// required because they form the correctness and rollback gate.
    /// </summary>
    public bool SkipUnchangedPhysicalLeaderboardSnapshots =>
        _features.SkipUnchangedPhysicalLeaderboardSnapshots
        && UseLeaderboardScopeFingerprints
        && WritePublishedScopeSources
        && EnforceScopeCompletenessManifests;

    /// <summary>
    /// True when the worker should build and atomically promote per-scope
    /// published-source mappings.
    /// </summary>
    public bool WritePublishedScopeSources => _features.WritePublishedScopeSources;
    public bool EnforceScopeCompletenessManifests =>
        _features.EnforceScopeCompletenessManifests;
    public bool RequireSuccessfulScrapeWriters =>
        _features.RequireSuccessfulScrapeWriters;
    public bool EnforcePublicationCriticalPhases =>
        _features.EnforcePublicationCriticalPhases;

    /// <summary>
    /// True when service-side current reads should resolve only through the
    /// per-scope source selected by the current published scrape.
    /// </summary>
    public bool UsePublishedScopeSources => _features.UsePublishedScopeSources;

    /// <summary>
    /// True only for the worker-side snapshot/overlay reader rollout. Published
    /// API processes continue to use their mapped publication source.
    /// </summary>
    public bool UseSnapshotOverlayWorkerReaders =>
        !UsePublishedScopeSources && _features.UseSnapshotOverlayWorkerReaders;

    public bool UseValidatedCurrentProjectionForWorkerReaders =>
        UseSnapshotOverlayWorkerReaders
        && _useValidatedCurrentProjectionForWorkerReaders;

    private bool UsePublishedScopeSourcesForCurrentRead =>
        UsePublishedScopeSources
        || Volatile.Read(
            ref _maxScoreMaintenancePublishedReadPass) != 0;

    private bool UseSnapshotOverlayWorkerReadersForCurrentRead =>
        Volatile.Read(
            ref _maxScoreMaintenancePublishedReadPass) == 0
        && UseSnapshotOverlayWorkerReaders;

    internal bool IsMaxScoreMaintenancePublishedReadPassActive =>
        Volatile.Read(
            ref _maxScoreMaintenancePublishedReadPass) != 0;

    public void SetValidatedCurrentProjectionForWorkerReaders(bool enabled)
    {
        if (enabled && !UseSnapshotOverlayWorkerReaders)
        {
            throw new InvalidOperationException(
                "Validated worker projection reads require snapshot/overlay worker readers.");
        }

        _useValidatedCurrentProjectionForWorkerReaders = enabled;
        foreach (var database in _instrumentDbs.Values.Cast<InstrumentDatabase>())
        {
            database.UseValidatedCurrentProjectionForWorkerReaders = enabled;
        }
    }

    internal IDisposable BeginValidatedCurrentProjectionReadPass()
    {
        SetValidatedCurrentProjectionForWorkerReaders(false);
        return new ValidatedCurrentProjectionReadPass(this);
    }

    internal IDisposable
        BeginMaxScoreMaintenancePublishedReadPass()
    {
        if (Interlocked.CompareExchange(
                ref _maxScoreMaintenancePublishedReadPass,
                1,
                0) != 0)
        {
            throw new InvalidOperationException(
                "A max-score authoritative published read pass is already active.");
        }

        try
        {
            foreach (var instrument in
                     GlobalLeaderboardScraper.AllInstruments)
            {
                GetOrCreateInstrumentDb(instrument);
            }
            foreach (var database in _instrumentDbs.Values
                         .Cast<InstrumentDatabase>())
            {
                ConfigureInstrumentDatabaseCurrentRead(database);
            }
            return new MaxScoreMaintenancePublishedReadPass(this);
        }
        catch
        {
            Interlocked.Exchange(
                ref _maxScoreMaintenancePublishedReadPass,
                0);
            throw;
        }
    }

    private sealed class ValidatedCurrentProjectionReadPass(
        GlobalLeaderboardPersistence owner) : IDisposable
    {
        private GlobalLeaderboardPersistence? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)
                ?.SetValidatedCurrentProjectionForWorkerReaders(false);
        }
    }

    private sealed class MaxScoreMaintenancePublishedReadPass(
        GlobalLeaderboardPersistence owner) : IDisposable
    {
        private GlobalLeaderboardPersistence? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
                return;

            Interlocked.Exchange(
                ref owner._maxScoreMaintenancePublishedReadPass,
                0);
            foreach (var database in owner._instrumentDbs.Values
                         .Cast<InstrumentDatabase>())
            {
                owner.ConfigureInstrumentDatabaseCurrentRead(
                    database);
            }
        }
    }

    public void RegisterSnapshotReuseManifest(
        long scrapeId,
        string songId,
        string instrument,
        ScopeCompletenessManifest? manifest)
    {
        if (!SkipUnchangedPhysicalLeaderboardSnapshots
            || scrapeId <= 0
            || string.IsNullOrWhiteSpace(songId)
            || string.IsNullOrWhiteSpace(instrument)
            || manifest is null)
        {
            return;
        }

        _snapshotReuseManifests[(scrapeId, songId, instrument)] = manifest;
    }

    internal IReadOnlyDictionary<string, ScopeCompletenessManifest> GetSnapshotReuseManifests(
        long scrapeId,
        string instrument,
        IEnumerable<string> songIds)
    {
        if (!SkipUnchangedPhysicalLeaderboardSnapshots)
            return new Dictionary<string, ScopeCompletenessManifest>(StringComparer.OrdinalIgnoreCase);

        var manifests = new Dictionary<string, ScopeCompletenessManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var songId in songIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_snapshotReuseManifests.TryGetValue((scrapeId, songId, instrument), out var manifest))
                manifests[songId] = manifest;
        }
        return manifests;
    }

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
    public void Initialize() => InitializeCore(allowStartupWrites: true);

    /// <summary>
    /// Opens existing instrument readers without schema creation or startup
    /// published-source backfills.
    /// </summary>
    public void InitializeReadOnly() => InitializeCore(allowStartupWrites: false);

    private void InitializeCore(bool allowStartupWrites)
    {
        if (allowStartupWrites)
            _metaDb.EnsureSchema();

        var instruments = GlobalLeaderboardScraper.AllInstruments;

        foreach (var instrument in instruments)
        {
            var db = new InstrumentDatabase(
                instrument, _pgDataSource,
                _loggerFactory.CreateLogger<InstrumentDatabase>());
            ConfigureInstrumentDatabaseCurrentRead(db);
            _instrumentDbs[instrument] = db;
            _log.LogDebug("Opened PG instrument DB: {Instrument}", instrument);
        }

        // Purge any phantom instrument entries created by previous runs
        // (e.g. from unvalidated API requests before the guard was added).
        var phantoms = _instrumentDbs.Keys
            .Where(k => !CanonicalInstrumentKeys.ContainsKey(k))
            .ToList();
        foreach (var key in phantoms)
        {
            _instrumentDbs.Remove(key);
            _log.LogWarning("Removed phantom instrument DB: {Instrument}", key);
        }

        if (allowStartupWrites && WritePublishedScopeSources)
        {
            var backfillStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var backfill = BackfillCurrentPublishedScopeSources();
            backfillStopwatch.Stop();
            _log.LogInformation(
                "Published scope-source startup backfill status={Status}, publishedScrape={PublishedScrapeId}, expected={ExpectedScopes:N0}, mapped={MappedScopes:N0}, applied={Applied}, elapsed={Elapsed}.",
                backfill.Status,
                backfill.PublishedScrapeId,
                backfill.ExpectedScopeCount,
                backfill.MappedScopeCount,
                backfill.Applied,
                backfillStopwatch.Elapsed);
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
            if (UsePublishedScopeSources && !HasCompletePublishedScopeSourceMapping())
                return false;

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

    private bool HasCompletePublishedScopeSourceMapping()
    {
        using var conn = _pgDataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) > 0 AND BOOL_AND(source.is_complete)
            FROM leaderboard_published_scope_source source
            JOIN scrape_publication_state publication
              ON publication.id = TRUE
             AND publication.published_scrape_id = source.published_scrape_id
            """;
        return Convert.ToBoolean(cmd.ExecuteScalar());
    }

    /// <summary>Valid instrument keys accepted by <see cref="GetOrCreateInstrumentDb"/>.</summary>
    private static readonly Dictionary<string, string> CanonicalInstrumentKeys =
        ComboIds.CanonicalOrder.ToDictionary(
            static instrument => instrument,
            static instrument => instrument,
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns true when <paramref name="instrument"/> is a recognised instrument key.</summary>
    public static bool IsValidInstrument(string instrument) =>
        CanonicalInstrumentKeys.ContainsKey(instrument);

    public static bool TryGetCanonicalInstrument(
        string instrument,
        out string canonicalInstrument) =>
        CanonicalInstrumentKeys.TryGetValue(
            instrument,
            out canonicalInstrument!);

    /// <summary>
    /// Get (or create on first access) the <see cref="IInstrumentDatabase"/>
    /// for a given instrument key (e.g. "Solo_Guitar").
    /// Rejects unknown instrument names to prevent rogue DB instances.
    /// </summary>
    public IInstrumentDatabase GetOrCreateInstrumentDb(string instrument)
    {
        if (_instrumentDbs.TryGetValue(instrument, out var db))
            return db;

        if (!CanonicalInstrumentKeys.ContainsKey(instrument))
            throw new ArgumentException($"Unknown instrument key: '{instrument}'. Valid keys: {string.Join(", ", ComboIds.CanonicalOrder)}");

        var instrumentDatabase = new InstrumentDatabase(
            instrument, _pgDataSource,
            _loggerFactory.CreateLogger<InstrumentDatabase>());
        ConfigureInstrumentDatabaseCurrentRead(
            instrumentDatabase);

        _instrumentDbs[instrument] = instrumentDatabase;
        return instrumentDatabase;
    }

    private void ConfigureInstrumentDatabaseCurrentRead(
        InstrumentDatabase database)
    {
        database.UsePublishedScopeSources =
            UsePublishedScopeSourcesForCurrentRead;
        database.UseSnapshotOverlayWorkerReaders =
            UseSnapshotOverlayWorkerReadersForCurrentRead;
        database.UseValidatedCurrentProjectionForWorkerReaders =
            UseSnapshotOverlayWorkerReadersForCurrentRead
            && _useValidatedCurrentProjectionForWorkerReaders;
        database.BypassCurrentProjectionForMaintenance =
            Volatile.Read(
                ref _maxScoreMaintenancePublishedReadPass) != 0;
        database.UseStoredProjectionRanksForFilteredReads =
            _features.UseStoredSoloProjectionRanksForFilteredReads;
        database.WriteLegacyLiveLeaderboardSupplementalRows =
            _features.WriteLegacyLiveLeaderboardSupplementalRows;
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
        private int _soloLeaderboardsWithData;
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
        public int SoloLeaderboardsWithData => _soloLeaderboardsWithData;
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
        public void IncrementSoloLeaderboardsWithData() => Interlocked.Increment(ref _soloLeaderboardsWithData);
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
        string? replayBaseDirectory = null,
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
                ct,
                replayBaseDirectory);
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

    public async Task<WriterDrainResult> DrainOnlineSoloWriterAsync()
    {
        OnlineBoundedPageWriter<LeaderboardEntry>? writer;
        lock (_activeWriterLock)
        {
            writer = _onlineSoloWriter;
            _onlineSoloWriter = null;
        }

        if (writer is null) return WriterDrainResult.Empty("solo-online");

        try
        {
            return await writer.CompleteAndDrainAsync().ConfigureAwait(false);
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
            _snapshotReuseManifests.Clear();
        }
    }

    /// <summary>
    /// Signal the spool writer that no more data will arrive, then flush
    /// all spool data to PG in bulk. Index management is handled externally
    /// by the orchestrator to coordinate with the band spool.
    /// </summary>
    public async Task<WriterDrainResult> FlushSpoolAsync(ScrapeProgressTracker? progress = null)
    {
        SpoolWriter<LeaderboardEntry>? spool;
        lock (_activeWriterLock)
        {
            spool = _spoolWriter;
            _spoolWriter = null;
        }

        if (spool is null) return WriterDrainResult.Empty("solo");

        try
        {
            spool.Complete();

            _log.LogInformation("Flushing solo spool: {Records:N0} pages, {Entries:N0} entries...",
                spool.RecordCount, spool.EntryCount);
            return await Task.Run(() => spool.FlushAll(
                maxBatchPages: 64,
                onProgress: p => ReportSpoolFlushProgress(progress, p)));
        }
        finally
        {
            await spool.DisposeAsync();
            _snapshotReuseManifests.Clear();
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

        _snapshotReuseManifests.Clear();
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
        var skipUnchangedPhysicalSnapshots = SkipUnchangedPhysicalLeaderboardSnapshots;
        using var conn = _pgDataSource!.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH expected_pairs AS (
                SELECT DISTINCT pair.song_id, pair.instrument
                FROM unnest(@expectedSongIds::text[], @expectedInstruments::text[]) AS pair(song_id, instrument)
            ), snapshot_pairs AS (
                SELECT DISTINCT song_id, instrument, TRUE AS has_snapshot_rows
                FROM leaderboard_entries_snapshot
                WHERE snapshot_id = @scrapeId
            ), activation_pairs AS (
                SELECT song_id, instrument, has_snapshot_rows FROM snapshot_pairs
                UNION
                SELECT song_id, instrument, FALSE AS has_snapshot_rows FROM expected_pairs
            ), activation_scopes AS (
                SELECT song_id,
                       instrument,
                       BOOL_OR(has_snapshot_rows) AS has_snapshot_rows
                FROM activation_pairs
                GROUP BY song_id, instrument
            ), desired_state AS (
                SELECT activation_scopes.song_id,
                       activation_scopes.instrument,
                       CASE
                           WHEN @skipUnchangedPhysicalSnapshots
                            AND NOT activation_scopes.has_snapshot_rows
                            AND scope_fingerprint.last_seen_scrape_id = @scrapeId
                            AND scope_fingerprint.fingerprint_version >= 2
                            AND scope_fingerprint.is_complete
                            AND published_source.source_kind = 'snapshot'
                            AND published_source.source_snapshot_id IS NOT NULL
                            AND published_source.is_complete
                            AND published_source.row_count = scope_fingerprint.entry_count
                            AND published_source.content_fingerprint IS NOT DISTINCT FROM scope_fingerprint.content_fingerprint
                            AND (
                                published_source.coverage_fingerprint IS NOT DISTINCT FROM scope_fingerprint.coverage_fingerprint
                                OR (
                                    length(published_source.coverage_fingerprint) = 32
                                    AND length(scope_fingerprint.coverage_fingerprint) = 64
                                )
                            )
                               THEN published_source.source_snapshot_id
                           ELSE @scrapeId
                       END AS active_snapshot_id
                FROM activation_scopes
                LEFT JOIN leaderboard_scope_fingerprints scope_fingerprint
                  ON scope_fingerprint.song_id = activation_scopes.song_id
                 AND scope_fingerprint.instrument = activation_scopes.instrument
                 AND scope_fingerprint.scope_kind = 'alltime'
                LEFT JOIN scrape_publication_state publication
                  ON publication.id = TRUE
                LEFT JOIN leaderboard_published_scope_source published_source
                  ON published_source.published_scrape_id = publication.published_scrape_id
                 AND published_source.song_id = activation_scopes.song_id
                 AND published_source.instrument = activation_scopes.instrument
                 AND published_source.scope_kind = 'alltime'
            ), upserted AS (
                INSERT INTO leaderboard_snapshot_state
                (song_id, instrument, active_snapshot_id, scrape_id, is_finalized, {finalizedColumn}, updated_at)
                SELECT song_id, instrument, active_snapshot_id, active_snapshot_id, TRUE, @now, @now
                FROM desired_state
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
        cmd.Parameters.AddWithValue("skipUnchangedPhysicalSnapshots", skipUnchangedPhysicalSnapshots);
        cmd.Parameters.AddWithValue("expectedSongIds", expectedPairArray.Select(pair => pair.SongId).ToArray());
        cmd.Parameters.AddWithValue("expectedInstruments", expectedPairArray.Select(pair => pair.Instrument).ToArray());
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public LeaderboardScopeCoverageResult RecordLeaderboardScopeCoverage(
        long scrapeId,
        IEnumerable<GlobalLeaderboardResult> results,
        IEnumerable<(string SongId, string Instrument)> expectedPairs)
    {
        if (scrapeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(scrapeId));

        var expectedPairArray = NormalizeExpectedPairs(expectedPairs);
        var expectedPairSet = expectedPairArray.ToHashSet();
        var observed = results
            .Where(result => expectedPairSet.Contains((result.SongId, result.Instrument)))
            .GroupBy(result => (result.SongId, result.Instrument))
            .ToDictionary(group => group.Key, group => group.Last());

        var coverageRows = expectedPairArray
            .Where(observed.ContainsKey)
            .Select(pair =>
            {
                var result = observed[pair];
                var reportedEntries = result.ReportedTotalPages <= 100
                    ? result.EntriesCount
                    : (long)result.ReportedTotalPages * 100;
                var isComplete =
                    (result.Requests == 0
                        && result.EntriesCount == 0
                        && result.TotalPages == 0
                        && result.ReportedTotalPages == 0)
                    || (result.PagesScraped > 0
                        && result.PagesScraped >= Math.Max(1, result.TotalPages));
                var manifest = result.CompletenessManifest;
                if (EnforceScopeCompletenessManifests)
                    isComplete = manifest?.IsComplete == true;

                return new
                {
                    pair.SongId,
                    pair.Instrument,
                    RowCount = result.EntriesCount,
                    ReportedTotalEntries = Math.Max(
                        manifest?.ReportedTotalEntries ?? reportedEntries,
                        result.EntriesCount),
                    ReportedTotalPages =
                        manifest?.ReportedTotalPages ?? result.ReportedTotalPages,
                    IsComplete = isComplete,
                    Manifest = manifest,
                };
            })
            .ToArray();

        var persistedCount = 0;
        if (coverageRows.Length > 0)
        {
            using var conn = _pgDataSource.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                WITH coverage AS (
                    SELECT *
                    FROM unnest(
                        @songIds::text[],
                        @instruments::text[],
                        @rowCounts::integer[],
                        @reportedEntries::bigint[],
                        @reportedPages::integer[],
                        @isComplete::boolean[],
                        @manifestCoverageFingerprints::text[]
                    ) AS source(
                        song_id,
                        instrument,
                        row_count,
                        reported_total_entries,
                        reported_total_pages,
                        is_complete,
                        manifest_coverage_fingerprint)
                ), updated AS (
                    UPDATE leaderboard_scope_fingerprints fingerprint
                    SET fingerprint_version = 2,
                        coverage_fingerprint = COALESCE(
                            NULLIF(coverage.manifest_coverage_fingerprint, ''),
                            md5(concat_ws(E'\x1f',
                                fingerprint.entry_count::text,
                                COALESCE(fingerprint.min_rank::text, ''),
                                COALESCE(fingerprint.max_rank::text, ''),
                                coverage.reported_total_entries::text,
                                coverage.reported_total_pages::text,
                                coverage.is_complete::text
                            ))),
                        reported_total_entries = coverage.reported_total_entries,
                        reported_total_pages = coverage.reported_total_pages,
                        is_complete = coverage.is_complete,
                        seen_at = @now
                    FROM coverage
                    WHERE coverage.row_count > 0
                      AND fingerprint.song_id = coverage.song_id
                      AND fingerprint.instrument = coverage.instrument
                      AND fingerprint.scope_kind = 'alltime'
                      AND fingerprint.last_seen_scrape_id = @scrapeId
                    RETURNING fingerprint.song_id, fingerprint.instrument
                ), empty_upserted AS (
                    INSERT INTO leaderboard_scope_fingerprints (
                        song_id,
                        instrument,
                        scope_kind,
                        fingerprint_version,
                        content_fingerprint,
                        coverage_fingerprint,
                        entry_count,
                        reported_total_entries,
                        reported_total_pages,
                        is_complete,
                        min_rank,
                        max_rank,
                        source_scrape_id,
                        published_scrape_id,
                        first_seen_scrape_id,
                        last_changed_scrape_id,
                        last_seen_scrape_id,
                        changed_at,
                        seen_at)
                    SELECT
                        coverage.song_id,
                        coverage.instrument,
                        'alltime',
                        2,
                        md5(''),
                        COALESCE(
                            NULLIF(coverage.manifest_coverage_fingerprint, ''),
                            md5(concat_ws(E'\x1f',
                                '0',
                                '',
                                '',
                                coverage.reported_total_entries::text,
                                coverage.reported_total_pages::text,
                                coverage.is_complete::text
                            ))),
                        0,
                        coverage.reported_total_entries,
                        coverage.reported_total_pages,
                        coverage.is_complete,
                        NULL,
                        NULL,
                        @scrapeId,
                        NULL,
                        @scrapeId,
                        @scrapeId,
                        @scrapeId,
                        @now,
                        @now
                    FROM coverage
                    WHERE coverage.row_count = 0
                    ON CONFLICT (song_id, instrument, scope_kind) DO UPDATE SET
                        fingerprint_version = EXCLUDED.fingerprint_version,
                        content_fingerprint = EXCLUDED.content_fingerprint,
                        coverage_fingerprint = EXCLUDED.coverage_fingerprint,
                        entry_count = EXCLUDED.entry_count,
                        reported_total_entries = EXCLUDED.reported_total_entries,
                        reported_total_pages = EXCLUDED.reported_total_pages,
                        is_complete = EXCLUDED.is_complete,
                        min_rank = NULL,
                        max_rank = NULL,
                        source_scrape_id = EXCLUDED.source_scrape_id,
                        last_changed_scrape_id = CASE
                            WHEN leaderboard_scope_fingerprints.entry_count = 0
                             AND leaderboard_scope_fingerprints.content_fingerprint = EXCLUDED.content_fingerprint
                                THEN leaderboard_scope_fingerprints.last_changed_scrape_id
                            ELSE EXCLUDED.last_changed_scrape_id
                        END,
                        last_seen_scrape_id = EXCLUDED.last_seen_scrape_id,
                        changed_at = CASE
                            WHEN leaderboard_scope_fingerprints.entry_count = 0
                             AND leaderboard_scope_fingerprints.content_fingerprint = EXCLUDED.content_fingerprint
                                THEN leaderboard_scope_fingerprints.changed_at
                            ELSE EXCLUDED.changed_at
                        END,
                        seen_at = EXCLUDED.seen_at
                    RETURNING song_id, instrument
                ), applied AS (
                    SELECT song_id, instrument FROM updated
                    UNION ALL
                    SELECT song_id, instrument FROM empty_upserted
                )
                SELECT COUNT(*)::int FROM applied
                """;
            cmd.Parameters.AddWithValue("scrapeId", scrapeId);
            cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("songIds", coverageRows.Select(row => row.SongId).ToArray());
            cmd.Parameters.AddWithValue("instruments", coverageRows.Select(row => row.Instrument).ToArray());
            cmd.Parameters.AddWithValue("rowCounts", coverageRows.Select(row => row.RowCount).ToArray());
            cmd.Parameters.AddWithValue("reportedEntries", coverageRows.Select(row => row.ReportedTotalEntries).ToArray());
            cmd.Parameters.AddWithValue("reportedPages", coverageRows.Select(row => row.ReportedTotalPages).ToArray());
            cmd.Parameters.AddWithValue("isComplete", coverageRows.Select(row => row.IsComplete).ToArray());
            cmd.Parameters.Add(
                "manifestCoverageFingerprints",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value = coverageRows
                    .Select(row => row.Manifest?.CoverageFingerprint ?? "")
                    .ToArray();
            persistedCount = Convert.ToInt32(cmd.ExecuteScalar());
        }

        RecordScopeCompletenessManifests(
            scrapeId,
            coverageRows
            .Where(static row => row.Manifest is not null)
            .Select(row => new ScopeCompletenessRecord(
                row.SongId,
                row.Instrument,
                row.Manifest!))
            .ToArray(),
            expectedPairArray);

        return new LeaderboardScopeCoverageResult(
            scrapeId,
            expectedPairArray.Length,
            coverageRows.Length,
            persistedCount,
            expectedPairArray.Length - coverageRows.Length,
            coverageRows.Count(row => !row.IsComplete));
    }

    public ScopeManifestPersistenceResult RecordScopeCompletenessManifests(
        long scrapeId,
        IReadOnlyList<ScopeCompletenessRecord> rows,
        IEnumerable<(string SongId, string Instrument)> expectedPairs)
    {
        if (scrapeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(scrapeId));

        var expectedPairArray = NormalizeExpectedPairs(expectedPairs);
        var expectedPairSet = expectedPairArray.ToHashSet();
        var normalizedRows = rows
            .Where(row => expectedPairSet.Contains((row.SongId, row.Instrument)))
            .GroupBy(static row => (row.SongId, row.Instrument))
            .Select(static group => group.Last())
            .ToArray();

        using var conn = _pgDataSource.OpenConnection();
        using var tx = conn.BeginTransaction();
        if (expectedPairArray.Length > 0)
        {
            using var clear = conn.CreateCommand();
            clear.Transaction = tx;
            clear.CommandText = """
                DELETE FROM leaderboard_scope_manifests manifest
                USING unnest(
                    @expectedSongIds::text[],
                    @expectedInstruments::text[]
                ) AS expected(song_id, instrument)
                WHERE manifest.scrape_id = @scrapeId
                  AND manifest.scope_kind = 'alltime'
                  AND manifest.song_id = expected.song_id
                  AND manifest.instrument = expected.instrument
                """;
            clear.Parameters.AddWithValue("scrapeId", scrapeId);
            clear.Parameters.AddWithValue(
                "expectedSongIds",
                expectedPairArray.Select(static pair => pair.SongId).ToArray());
            clear.Parameters.AddWithValue(
                "expectedInstruments",
                expectedPairArray.Select(static pair => pair.Instrument).ToArray());
            clear.ExecuteNonQuery();
        }

        if (normalizedRows.Length > 0)
        {
            using var writer = conn.BeginBinaryImport(
                """
                COPY leaderboard_scope_manifests (
                    scrape_id, song_id, instrument, scope_kind,
                    expected_first_page, expected_last_page, received_pages,
                    page_statuses, terminal_boundary, terminal_boundary_page,
                    parse_status, retry_exhausted, reported_total_entries,
                    reported_total_pages, deep_start_page, deep_end_page,
                    content_fingerprint, coverage_fingerprint, is_complete,
                    failure_reason, created_at, updated_at)
                FROM STDIN (FORMAT BINARY)
                """);
            var now = DateTime.UtcNow;
            foreach (var row in normalizedRows)
            {
                var songId = row.SongId;
                var instrument = row.Instrument;
                var manifest = row.Manifest;
                writer.StartRow();
                writer.Write(scrapeId, NpgsqlDbType.Bigint);
                writer.Write(songId, NpgsqlDbType.Text);
                writer.Write(instrument, NpgsqlDbType.Text);
                writer.Write("alltime", NpgsqlDbType.Text);
                writer.Write(manifest.ExpectedFirstPage, NpgsqlDbType.Integer);
                writer.Write(manifest.ExpectedLastPage, NpgsqlDbType.Integer);
                writer.Write(manifest.ReceivedPages.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Integer);
                writer.Write(
                    JsonSerializer.Serialize(manifest.PageStatuses),
                    NpgsqlDbType.Jsonb);
                writer.Write(
                    manifest.TerminalBoundary.ToString().ToLowerInvariant(),
                    NpgsqlDbType.Text);
                if (manifest.TerminalBoundaryPage.HasValue)
                    writer.Write(manifest.TerminalBoundaryPage.Value, NpgsqlDbType.Integer);
                else
                    writer.WriteNull();
                writer.Write(manifest.ParseStatus, NpgsqlDbType.Text);
                writer.Write(manifest.RetryExhausted, NpgsqlDbType.Boolean);
                writer.Write(manifest.ReportedTotalEntries, NpgsqlDbType.Bigint);
                writer.Write(manifest.ReportedTotalPages, NpgsqlDbType.Integer);
                if (manifest.DeepStartPage.HasValue)
                    writer.Write(manifest.DeepStartPage.Value, NpgsqlDbType.Integer);
                else
                    writer.WriteNull();
                if (manifest.DeepEndPage.HasValue)
                    writer.Write(manifest.DeepEndPage.Value, NpgsqlDbType.Integer);
                else
                    writer.WriteNull();
                writer.Write(manifest.ContentFingerprint, NpgsqlDbType.Text);
                writer.Write(manifest.CoverageFingerprint, NpgsqlDbType.Text);
                writer.Write(manifest.IsComplete, NpgsqlDbType.Boolean);
                if (manifest.FailureReason is not null)
                    writer.Write(manifest.FailureReason, NpgsqlDbType.Text);
                else
                    writer.WriteNull();
                writer.Write(now, NpgsqlDbType.TimestampTz);
                writer.Write(now, NpgsqlDbType.TimestampTz);
            }
            writer.Complete();
        }

        tx.Commit();
        return new ScopeManifestPersistenceResult(
            scrapeId,
            expectedPairArray.Length,
            normalizedRows.Length,
            normalizedRows.Length,
            expectedPairArray.Length - normalizedRows.Length,
            normalizedRows.Count(static row => !row.Manifest.IsComplete));
    }

    public PublishedScopeSourceBackfillResult BackfillCurrentPublishedScopeSources()
    {
        using var conn = _pgDataSource.OpenConnection();
        long? publishedScrapeId;
        bool frozen;
        bool cleanBoundary;
        int expectedScopeCount;
        int existingMappingCount;
        int publishedFingerprintCount;

        using (var state = conn.CreateCommand())
        {
            state.CommandText = """
                WITH publication AS (
                    SELECT published_scrape_id, public_reads_frozen
                    FROM scrape_publication_state
                    WHERE id = TRUE
                )
                SELECT
                    publication.published_scrape_id,
                    publication.public_reads_frozen,
                    publication.published_scrape_id IS NOT NULL
                        AND NOT publication.public_reads_frozen
                        AND NOT EXISTS (
                            SELECT 1 FROM scrape_log
                            WHERE id > publication.published_scrape_id
                        )
                        AND NOT EXISTS (
                            SELECT 1
                            FROM leaderboard_snapshot_state snapshot_state
                            WHERE snapshot_state.is_finalized = TRUE
                              AND snapshot_state.active_snapshot_id > publication.published_scrape_id
                        ) AS clean_boundary,
                    (
                        SELECT COUNT(*)::int
                        FROM leaderboard_snapshot_state snapshot_state
                        WHERE snapshot_state.is_finalized = TRUE
                    ) AS expected_scope_count,
                    (
                        SELECT COUNT(*)::int
                        FROM leaderboard_published_scope_source source
                        WHERE source.published_scrape_id = publication.published_scrape_id
                    ) AS mapping_count,
                    (
                        SELECT COUNT(*)::int
                        FROM leaderboard_published_scope_source source
                        JOIN leaderboard_scope_fingerprints fingerprint
                          ON fingerprint.song_id = source.song_id
                         AND fingerprint.instrument = source.instrument
                         AND fingerprint.scope_kind = source.scope_kind
                        WHERE source.published_scrape_id = publication.published_scrape_id
                          AND fingerprint.published_scrape_id = publication.published_scrape_id
                    ) AS published_fingerprint_count
                FROM publication
                """;
            using var reader = state.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0))
                return new PublishedScopeSourceBackfillResult(null, 0, 0, false, "no-published-scrape");

            publishedScrapeId = reader.GetInt64(0);
            frozen = reader.GetBoolean(1);
            cleanBoundary = reader.GetBoolean(2);
            expectedScopeCount = reader.GetInt32(3);
            existingMappingCount = reader.GetInt32(4);
            publishedFingerprintCount = reader.GetInt32(5);
        }

        if (existingMappingCount > 0
            && publishedFingerprintCount == existingMappingCount)
        {
            expectedScopeCount = existingMappingCount;
        }

        if (existingMappingCount == expectedScopeCount && expectedScopeCount > 0)
        {
            var applied = false;
            if (publishedFingerprintCount != expectedScopeCount)
            {
                if (frozen || !cleanBoundary)
                {
                    return new PublishedScopeSourceBackfillResult(
                        publishedScrapeId,
                        expectedScopeCount,
                        existingMappingCount,
                        false,
                        "mapping-complete-fingerprint-mark-deferred");
                }

                MarkPublishedScopeFingerprints(conn, publishedScrapeId.Value, expectedScopeCount);
                applied = true;
            }

            var repairedPopulationCount = 0;
            if (cleanBoundary)
            {
                repairedPopulationCount = RepairPublishedScopePopulationTotals(
                    conn,
                    publishedScrapeId.Value);
                applied |= repairedPopulationCount > 0;
            }

            return new PublishedScopeSourceBackfillResult(
                publishedScrapeId,
                expectedScopeCount,
                existingMappingCount,
                applied,
                repairedPopulationCount > 0
                    ? $"already-complete-population-repaired:{repairedPopulationCount}"
                    : "already-complete");
        }

        if (existingMappingCount > 0)
        {
            throw new InvalidOperationException(
                $"Published scrape {publishedScrapeId} has a partial per-scope source mapping " +
                $"({existingMappingCount}/{expectedScopeCount}).");
        }

        if (frozen || !cleanBoundary)
        {
            return new PublishedScopeSourceBackfillResult(
                publishedScrapeId,
                expectedScopeCount,
                0,
                false,
                "deferred-until-clean-publication-boundary");
        }

        using (var tx = conn.BeginTransaction())
        {
            using var coverage = conn.CreateCommand();
            coverage.Transaction = tx;
            coverage.CommandTimeout = 0;
            coverage.CommandText = """
                WITH source_state AS (
                    SELECT
                        state.song_id,
                        state.instrument,
                        state.active_snapshot_id
                    FROM leaderboard_snapshot_state state
                    WHERE state.is_finalized = TRUE
                ), physical_raw AS (
                    SELECT
                        source_state.song_id,
                        source_state.instrument,
                        md5(string_agg(
                            concat_ws(E'\x1f',
                                snapshot.account_id,
                                snapshot.score::text,
                                COALESCE(snapshot.accuracy::text, ''),
                                COALESCE(snapshot.is_full_combo::text, ''),
                                COALESCE(snapshot.stars::text, ''),
                                COALESCE(snapshot.season::text, ''),
                                COALESCE(snapshot.difficulty::text, ''),
                                COALESCE(snapshot.percentile::text, ''),
                                COALESCE(snapshot.rank::text, ''),
                                COALESCE(snapshot.api_rank::text, ''),
                                COALESCE(snapshot.end_time, ''),
                                COALESCE(snapshot.source, ''),
                                COALESCE(snapshot.band_members_json::text, ''),
                                COALESCE(snapshot.band_score::text, ''),
                                COALESCE(snapshot.base_score::text, ''),
                                COALESCE(snapshot.instrument_bonus::text, ''),
                                COALESCE(snapshot.overdrive_bonus::text, ''),
                                COALESCE(snapshot.instrument_combo, '')
                            ),
                            E'\x1e'
                            ORDER BY snapshot.account_id
                        )) AS content_fingerprint,
                        COUNT(*)::int AS entry_count,
                        MIN(NULLIF(snapshot.rank, 0))::int AS min_rank,
                        MAX(NULLIF(snapshot.rank, 0))::int AS max_rank,
                        GREATEST(population.total_entries::bigint, COUNT(*)::bigint) AS reported_total_entries
                    FROM source_state
                    JOIN leaderboard_entries_snapshot snapshot
                      ON snapshot.snapshot_id = source_state.active_snapshot_id
                     AND snapshot.song_id = source_state.song_id
                     AND snapshot.instrument = source_state.instrument
                    JOIN leaderboard_population population
                      ON population.song_id = source_state.song_id
                     AND population.instrument = source_state.instrument
                    GROUP BY source_state.song_id, source_state.instrument, population.total_entries
                ), physical_scopes AS (
                    SELECT
                        physical_raw.*,
                        ((reported_total_entries + 99) / 100)::int AS reported_total_pages,
                        md5(concat_ws(E'\x1f',
                            entry_count::text,
                            COALESCE(min_rank::text, ''),
                            COALESCE(max_rank::text, ''),
                            reported_total_entries::text,
                            ((reported_total_entries + 99) / 100)::text,
                            'true'
                        )) AS coverage_fingerprint
                    FROM physical_raw
                ), physical_upserted AS (
                    INSERT INTO leaderboard_scope_fingerprints (
                        song_id,
                        instrument,
                        scope_kind,
                        fingerprint_version,
                        content_fingerprint,
                        coverage_fingerprint,
                        entry_count,
                        reported_total_entries,
                        reported_total_pages,
                        is_complete,
                        min_rank,
                        max_rank,
                        source_scrape_id,
                        published_scrape_id,
                        first_seen_scrape_id,
                        last_changed_scrape_id,
                        last_seen_scrape_id,
                        changed_at,
                        seen_at)
                    SELECT
                        song_id,
                        instrument,
                        'alltime',
                        2,
                        content_fingerprint,
                        coverage_fingerprint,
                        entry_count,
                        reported_total_entries,
                        reported_total_pages,
                        TRUE,
                        min_rank,
                        max_rank,
                        @publishedScrapeId,
                        NULL,
                        @publishedScrapeId,
                        @publishedScrapeId,
                        @publishedScrapeId,
                        @now,
                        @now
                    FROM physical_scopes
                    ON CONFLICT (song_id, instrument, scope_kind) DO UPDATE SET
                        fingerprint_version = EXCLUDED.fingerprint_version,
                        content_fingerprint = EXCLUDED.content_fingerprint,
                        coverage_fingerprint = EXCLUDED.coverage_fingerprint,
                        entry_count = EXCLUDED.entry_count,
                        reported_total_entries = EXCLUDED.reported_total_entries,
                        reported_total_pages = EXCLUDED.reported_total_pages,
                        is_complete = EXCLUDED.is_complete,
                        min_rank = EXCLUDED.min_rank,
                        max_rank = EXCLUDED.max_rank,
                        source_scrape_id = EXCLUDED.source_scrape_id,
                        last_seen_scrape_id = EXCLUDED.last_seen_scrape_id,
                        seen_at = EXCLUDED.seen_at
                    RETURNING song_id, instrument
                )
                INSERT INTO leaderboard_scope_fingerprints (
                    song_id,
                    instrument,
                    scope_kind,
                    fingerprint_version,
                    content_fingerprint,
                    coverage_fingerprint,
                    entry_count,
                    reported_total_entries,
                    reported_total_pages,
                    is_complete,
                    min_rank,
                    max_rank,
                    source_scrape_id,
                    published_scrape_id,
                    first_seen_scrape_id,
                    last_changed_scrape_id,
                    last_seen_scrape_id,
                    changed_at,
                    seen_at)
                SELECT
                    source_state.song_id,
                    source_state.instrument,
                    'alltime',
                    2,
                    md5(''),
                    md5(concat_ws(E'\x1f', '0', '', '', '0', '0', 'true')),
                    0,
                    0,
                    0,
                    TRUE,
                    NULL,
                    NULL,
                    @publishedScrapeId,
                    NULL,
                    @publishedScrapeId,
                    @publishedScrapeId,
                    @publishedScrapeId,
                    @now,
                    @now
                FROM source_state
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM physical_scopes physical
                    WHERE physical.song_id = source_state.song_id
                      AND physical.instrument = source_state.instrument
                )
                ON CONFLICT (song_id, instrument, scope_kind) DO UPDATE SET
                    fingerprint_version = EXCLUDED.fingerprint_version,
                    content_fingerprint = EXCLUDED.content_fingerprint,
                    coverage_fingerprint = EXCLUDED.coverage_fingerprint,
                    entry_count = EXCLUDED.entry_count,
                    reported_total_entries = EXCLUDED.reported_total_entries,
                    reported_total_pages = EXCLUDED.reported_total_pages,
                    is_complete = EXCLUDED.is_complete,
                    min_rank = NULL,
                    max_rank = NULL,
                    source_scrape_id = EXCLUDED.source_scrape_id,
                    last_changed_scrape_id = CASE
                        WHEN leaderboard_scope_fingerprints.entry_count = 0
                         AND leaderboard_scope_fingerprints.content_fingerprint = EXCLUDED.content_fingerprint
                            THEN leaderboard_scope_fingerprints.last_changed_scrape_id
                        ELSE EXCLUDED.last_changed_scrape_id
                    END,
                    last_seen_scrape_id = EXCLUDED.last_seen_scrape_id,
                    changed_at = CASE
                        WHEN leaderboard_scope_fingerprints.entry_count = 0
                         AND leaderboard_scope_fingerprints.content_fingerprint = EXCLUDED.content_fingerprint
                            THEN leaderboard_scope_fingerprints.changed_at
                        ELSE EXCLUDED.changed_at
                    END,
                    seen_at = EXCLUDED.seen_at
                """;
            coverage.Parameters.AddWithValue("publishedScrapeId", publishedScrapeId.Value);
            coverage.Parameters.AddWithValue("now", DateTime.UtcNow);
            coverage.ExecuteNonQuery();
            tx.Commit();
        }

        (string SongId, string Instrument)[] expectedPairs;
        using (var expected = conn.CreateCommand())
        {
            expected.CommandText = """
                SELECT song_id, instrument
                FROM leaderboard_snapshot_state
                WHERE is_finalized = TRUE
                ORDER BY instrument, song_id
                """;
            using var reader = expected.ExecuteReader();
            var pairs = new List<(string SongId, string Instrument)>();
            while (reader.Read())
                pairs.Add((reader.GetString(0), reader.GetString(1)));
            expectedPairs = pairs.ToArray();
        }

        var build = BuildPublishedScopeSourceCandidate(publishedScrapeId.Value, expectedPairs);
        if (!build.IsComplete)
        {
            throw new InvalidOperationException(
                $"Published source backfill for scrape {publishedScrapeId} validated " +
                $"{build.ValidatedScopeCount}/{build.ExpectedScopeCount} scopes.");
        }

        MarkPublishedScopeFingerprints(conn, publishedScrapeId.Value, build.ExpectedScopeCount);

        return new PublishedScopeSourceBackfillResult(
            publishedScrapeId,
            build.ExpectedScopeCount,
            build.MappedScopeCount,
            true,
            "backfilled");
    }

    private static void MarkPublishedScopeFingerprints(
        NpgsqlConnection conn,
        long publishedScrapeId,
        int expectedScopeCount)
    {
        using var publishFingerprints = conn.CreateCommand();
        publishFingerprints.CommandText = """
            UPDATE leaderboard_scope_fingerprints fingerprint
            SET published_scrape_id = @publishedScrapeId
            FROM leaderboard_published_scope_source source
            WHERE source.published_scrape_id = @publishedScrapeId
              AND fingerprint.song_id = source.song_id
              AND fingerprint.instrument = source.instrument
              AND fingerprint.scope_kind = source.scope_kind
              AND fingerprint.last_seen_scrape_id = @publishedScrapeId
              AND fingerprint.is_complete
            """;
        publishFingerprints.Parameters.AddWithValue("publishedScrapeId", publishedScrapeId);
        var publishedFingerprints = publishFingerprints.ExecuteNonQuery();
        if (publishedFingerprints != expectedScopeCount)
        {
            throw new InvalidOperationException(
                $"Published source backfill for scrape {publishedScrapeId} marked " +
                $"{publishedFingerprints}/{expectedScopeCount} fingerprints as published.");
        }
    }

    private static int RepairPublishedScopePopulationTotals(
        NpgsqlConnection conn,
        long publishedScrapeId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE leaderboard_published_scope_source source
            SET reported_total_entries = GREATEST(
                    source.reported_total_entries,
                    population.total_entries::bigint,
                    source.row_count),
                validated_at = @now
            FROM leaderboard_population population
            WHERE source.published_scrape_id = @publishedScrapeId
              AND source.scope_kind = 'alltime'
              AND source.source_kind = 'snapshot'
              AND population.song_id = source.song_id
              AND population.instrument = source.instrument
              AND population.total_entries::bigint > source.reported_total_entries
            """;
        cmd.Parameters.AddWithValue("publishedScrapeId", publishedScrapeId);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        return cmd.ExecuteNonQuery();
    }

    public PublishedScopeSourceBuildResult BuildPublishedScopeSourceCandidate(
        long scrapeId,
        IEnumerable<(string SongId, string Instrument)> expectedPairs)
    {
        if (scrapeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(scrapeId));

        var expectedPairArray = NormalizeExpectedPairs(expectedPairs);
        using var conn = _pgDataSource.OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var clear = conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = """
                DELETE FROM leaderboard_published_scope_source
                WHERE published_scrape_id = @scrapeId
                """;
            clear.Parameters.AddWithValue("scrapeId", scrapeId);
            clear.ExecuteNonQuery();
        }

        if (expectedPairArray.Length == 0)
        {
            tx.Commit();
            return new PublishedScopeSourceBuildResult(scrapeId, 0, 0, 0, 0);
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        cmd.CommandText = """
            WITH expected_pairs AS (
                SELECT DISTINCT pair.song_id, pair.instrument
                FROM unnest(@expectedSongIds::text[], @expectedInstruments::text[]) AS pair(song_id, instrument)
            ), candidate_sources AS (
                SELECT
                    @scrapeId::bigint AS published_scrape_id,
                    expected.song_id,
                    expected.instrument,
                    fingerprint.scope_kind,
                    CASE WHEN fingerprint.entry_count = 0 THEN 'empty' ELSE 'snapshot' END AS source_kind,
                    CASE WHEN fingerprint.entry_count = 0 THEN NULL ELSE state.active_snapshot_id END AS source_snapshot_id,
                    CASE
                        WHEN fingerprint.entry_count = 0
                         AND prior_source.source_kind = 'empty'
                         AND prior_source.is_complete
                         AND prior_source.row_count = 0
                         AND prior_source.content_fingerprint IS NOT DISTINCT FROM fingerprint.content_fingerprint
                         AND (
                             prior_source.coverage_fingerprint IS NOT DISTINCT FROM fingerprint.coverage_fingerprint
                             OR (
                                 length(prior_source.coverage_fingerprint) = 32
                                 AND length(fingerprint.coverage_fingerprint) = 64
                             )
                         )
                            THEN prior_source.source_scrape_id
                        WHEN fingerprint.entry_count = 0 THEN @scrapeId
                        ELSE state.active_snapshot_id
                    END AS source_scrape_id,
                    fingerprint.entry_count::bigint AS row_count,
                    fingerprint.content_fingerprint,
                    fingerprint.coverage_fingerprint,
                    CASE
                        WHEN fingerprint.entry_count = 0 THEN 0::bigint
                        ELSE GREATEST(
                            fingerprint.reported_total_entries,
                            COALESCE(population.total_entries::bigint, 0),
                            fingerprint.entry_count::bigint)
                    END AS reported_total_entries,
                    fingerprint.reported_total_pages,
                    fingerprint.is_complete,
                    manifest.coverage_fingerprint AS manifest_coverage_fingerprint,
                    manifest.is_complete AS manifest_is_complete,
                    physical.row_count AS physical_row_count,
                    @now AS created_at,
                    @now AS validated_at
                FROM expected_pairs expected
                JOIN leaderboard_snapshot_state state
                  ON state.song_id = expected.song_id
                 AND state.instrument = expected.instrument
                 AND state.is_finalized = TRUE
                JOIN leaderboard_scope_fingerprints fingerprint
                  ON fingerprint.song_id = expected.song_id
                 AND fingerprint.instrument = expected.instrument
                 AND fingerprint.scope_kind = 'alltime'
                 AND fingerprint.last_seen_scrape_id = @scrapeId
                LEFT JOIN scrape_publication_state publication
                  ON publication.id = TRUE
                LEFT JOIN leaderboard_published_scope_source prior_source
                  ON prior_source.published_scrape_id = publication.published_scrape_id
                 AND prior_source.song_id = expected.song_id
                 AND prior_source.instrument = expected.instrument
                 AND prior_source.scope_kind = 'alltime'
                LEFT JOIN leaderboard_population population
                  ON population.song_id = expected.song_id
                 AND population.instrument = expected.instrument
                LEFT JOIN leaderboard_scope_manifests manifest
                  ON manifest.scrape_id = @scrapeId
                 AND manifest.song_id = expected.song_id
                 AND manifest.instrument = expected.instrument
                 AND manifest.scope_kind = 'alltime'
                LEFT JOIN LATERAL (
                    SELECT COUNT(*)::bigint AS row_count
                    FROM leaderboard_entries_snapshot snapshot
                    WHERE fingerprint.entry_count > 0
                      AND snapshot.snapshot_id = state.active_snapshot_id
                      AND snapshot.song_id = expected.song_id
                      AND snapshot.instrument = expected.instrument
                ) physical ON TRUE
            ), valid_sources AS (
                SELECT *
                FROM candidate_sources
                WHERE is_complete
                  AND (
                      NOT @enforceScopeCompletenessManifests
                      OR (
                          manifest_is_complete
                          AND manifest_coverage_fingerprint = coverage_fingerprint
                      )
                  )
                  AND reported_total_entries IS NOT NULL
                  AND reported_total_pages IS NOT NULL
                  AND reported_total_entries >= row_count
                  AND source_scrape_id > 0
                  AND source_scrape_id <= published_scrape_id
                  AND (
                      (source_kind = 'empty'
                          AND source_snapshot_id IS NULL
                          AND row_count = 0
                          AND reported_total_entries = 0
                          AND reported_total_pages = 0
                          AND physical_row_count = 0)
                      OR
                      (source_kind = 'snapshot'
                          AND source_snapshot_id IS NOT NULL
                          AND source_snapshot_id = source_scrape_id
                          AND row_count > 0
                          AND reported_total_pages > 0
                          AND physical_row_count = row_count)
                  )
            ), validation AS (
                SELECT
                    (SELECT COUNT(*)::int FROM expected_pairs) AS expected_count,
                    (SELECT COUNT(*)::int FROM valid_sources) AS validated_count
            ), upserted AS (
                INSERT INTO leaderboard_published_scope_source (
                    published_scrape_id,
                    song_id,
                    instrument,
                    scope_kind,
                    source_kind,
                    source_snapshot_id,
                    source_scrape_id,
                    row_count,
                    content_fingerprint,
                    coverage_fingerprint,
                    reported_total_entries,
                    reported_total_pages,
                    is_complete,
                    created_at,
                    validated_at
                )
                SELECT
                    published_scrape_id,
                    song_id,
                    instrument,
                    scope_kind,
                    source_kind,
                    source_snapshot_id,
                    source_scrape_id,
                    row_count,
                    content_fingerprint,
                    coverage_fingerprint,
                    reported_total_entries,
                    reported_total_pages,
                    TRUE,
                    created_at,
                    validated_at
                FROM valid_sources
                WHERE (SELECT validated_count = expected_count FROM validation)
                ON CONFLICT (published_scrape_id, instrument, song_id, scope_kind) DO UPDATE SET
                    source_kind = EXCLUDED.source_kind,
                    source_snapshot_id = EXCLUDED.source_snapshot_id,
                    source_scrape_id = EXCLUDED.source_scrape_id,
                    row_count = EXCLUDED.row_count,
                    content_fingerprint = EXCLUDED.content_fingerprint,
                    coverage_fingerprint = EXCLUDED.coverage_fingerprint,
                    reported_total_entries = EXCLUDED.reported_total_entries,
                    reported_total_pages = EXCLUDED.reported_total_pages,
                    is_complete = EXCLUDED.is_complete,
                    validated_at = EXCLUDED.validated_at
                RETURNING 1
            )
            SELECT
                validation.expected_count,
                validation.validated_count,
                (SELECT COUNT(*)::int FROM upserted) AS mapped_count,
                validation.expected_count - validation.validated_count AS missing_count
            FROM validation
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue(
            "enforceScopeCompletenessManifests",
            EnforceScopeCompletenessManifests);
        cmd.Parameters.AddWithValue("expectedSongIds", expectedPairArray.Select(pair => pair.SongId).ToArray());
        cmd.Parameters.AddWithValue("expectedInstruments", expectedPairArray.Select(pair => pair.Instrument).ToArray());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException($"Published scope source build for scrape {scrapeId} returned no result.");

        var result = new PublishedScopeSourceBuildResult(
            scrapeId,
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
        reader.Close();
        tx.Commit();
        return result;
    }

    public IReadOnlyList<PublishedScopeSource> GetPublishedScopeSources(long publishedScrapeId)
    {
        using var conn = _pgDataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                published_scrape_id,
                song_id,
                instrument,
                scope_kind,
                source_kind,
                source_snapshot_id,
                source_scrape_id,
                row_count,
                content_fingerprint,
                coverage_fingerprint,
                reported_total_entries,
                reported_total_pages,
                is_complete,
                created_at,
                validated_at
            FROM leaderboard_published_scope_source
            WHERE published_scrape_id = @publishedScrapeId
            ORDER BY instrument, song_id, scope_kind
            """;
        cmd.Parameters.AddWithValue("publishedScrapeId", publishedScrapeId);
        using var reader = cmd.ExecuteReader();
        var sources = new List<PublishedScopeSource>();
        while (reader.Read())
        {
            sources.Add(new PublishedScopeSource(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetInt32(11),
                reader.GetBoolean(12),
                reader.GetDateTime(13),
                reader.GetDateTime(14)));
        }
        return sources;
    }

    public int DeletePublishedScopeSourceCandidate(long scrapeId)
    {
        using var conn = _pgDataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM leaderboard_published_scope_source
            WHERE published_scrape_id = @scrapeId
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        return cmd.ExecuteNonQuery();
    }

    private static (string SongId, string Instrument)[] NormalizeExpectedPairs(
        IEnumerable<(string SongId, string Instrument)> expectedPairs) =>
        expectedPairs
            .Where(pair =>
                !string.IsNullOrWhiteSpace(pair.SongId)
                && !string.IsNullOrWhiteSpace(pair.Instrument))
            .Select(pair => (pair.SongId.Trim(), pair.Instrument.Trim()))
            .Distinct()
            .ToArray();

    internal void ObserveLeaderboardScopeFingerprints(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long scrapeId,
        string instrument,
        IReadOnlyDictionary<string, ScopeCompletenessManifest>? scopeManifests = null)
    {
        if (!UseLeaderboardScopeFingerprints || scrapeId <= 0)
            return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        cmd.CommandText = """
            WITH provided_manifests AS (
                SELECT *
                FROM unnest(
                    @manifestSongIds::text[],
                    @manifestCoverageFingerprints::text[],
                    @manifestReportedEntries::bigint[],
                    @manifestReportedPages::integer[],
                    @manifestIsComplete::boolean[]
                ) AS manifest(
                    song_id,
                    coverage_fingerprint,
                    reported_total_entries,
                    reported_total_pages,
                    is_complete)
            ),
            desired_rows AS (
                SELECT DISTINCT ON (song_id, instrument, account_id)
                    song_id,
                    instrument,
                    account_id,
                    score,
                    accuracy,
                    is_full_combo,
                    stars,
                    season,
                    difficulty,
                    percentile,
                    rank,
                    api_rank,
                    end_time,
                    source,
                    band_members_json,
                    band_score,
                    base_score,
                    instrument_bonus,
                    overdrive_bonus,
                    instrument_combo
                FROM _le_staging
                WHERE instrument = @instrument
                ORDER BY song_id, instrument, account_id, score DESC, ts DESC
            ),
            scope_rows AS (
                SELECT
                    song_id,
                    instrument,
                    md5(string_agg(
                        concat_ws(E'\x1f',
                            account_id,
                            score::text,
                            COALESCE(accuracy::text, ''),
                            COALESCE(is_full_combo::text, ''),
                            COALESCE(stars::text, ''),
                            COALESCE(season::text, ''),
                            COALESCE(difficulty::text, ''),
                            COALESCE(percentile::text, ''),
                            COALESCE(rank::text, ''),
                            COALESCE(api_rank::text, ''),
                            COALESCE(end_time, ''),
                            COALESCE(source, ''),
                            COALESCE(band_members_json::text, ''),
                            COALESCE(band_score::text, ''),
                            COALESCE(base_score::text, ''),
                            COALESCE(instrument_bonus::text, ''),
                            COALESCE(overdrive_bonus::text, ''),
                            COALESCE(instrument_combo, '')
                        ),
                        E'\x1e'
                        ORDER BY account_id
                    )) AS content_fingerprint,
                    md5(concat_ws(E'\x1f',
                        COUNT(*)::text,
                        COALESCE(MIN(rank)::text, ''),
                        COALESCE(MAX(rank)::text, ''),
                        COALESCE(MIN(api_rank)::text, ''),
                        COALESCE(MAX(api_rank)::text, '')
                    )) AS coverage_fingerprint,
                    COUNT(*)::int AS entry_count,
                    MIN(NULLIF(rank, 0))::int AS min_rank,
                    MAX(NULLIF(rank, 0))::int AS max_rank
                FROM desired_rows
                GROUP BY song_id, instrument
            ),
            observed AS (
                SELECT
                    scope_rows.song_id,
                    scope_rows.instrument,
                    CASE
                        WHEN provided_manifest.song_id IS NOT NULL
                          OR persisted_manifest.scrape_id IS NOT NULL
                            THEN 2
                        ELSE 1
                    END AS fingerprint_version,
                    scope_rows.content_fingerprint,
                    COALESCE(
                        provided_manifest.coverage_fingerprint,
                        persisted_manifest.coverage_fingerprint,
                        scope_rows.coverage_fingerprint) AS coverage_fingerprint,
                    scope_rows.entry_count,
                    COALESCE(
                        provided_manifest.reported_total_entries,
                        persisted_manifest.reported_total_entries) AS reported_total_entries,
                    COALESCE(
                        provided_manifest.reported_total_pages,
                        persisted_manifest.reported_total_pages) AS reported_total_pages,
                    COALESCE(
                        provided_manifest.is_complete,
                        persisted_manifest.is_complete,
                        FALSE) AS is_complete,
                    scope_rows.min_rank,
                    scope_rows.max_rank
                FROM scope_rows
                LEFT JOIN provided_manifests provided_manifest
                  ON provided_manifest.song_id = scope_rows.song_id
                LEFT JOIN leaderboard_scope_manifests persisted_manifest
                  ON persisted_manifest.scrape_id = @scrapeId
                 AND persisted_manifest.song_id = scope_rows.song_id
                 AND persisted_manifest.instrument = scope_rows.instrument
                 AND persisted_manifest.scope_kind = 'alltime'
            ),
            classified AS (
                SELECT
                    observed.*,
                    existing.first_seen_scrape_id AS existing_first_seen_scrape_id,
                    existing.last_changed_scrape_id AS existing_last_changed_scrape_id,
                    existing.changed_at AS existing_changed_at,
                    CASE
                        WHEN existing.song_id IS NULL THEN 'new'
                        WHEN existing.fingerprint_version = observed.fingerprint_version
                         AND existing.content_fingerprint = observed.content_fingerprint
                         AND existing.coverage_fingerprint = observed.coverage_fingerprint THEN 'unchanged'
                        ELSE 'changed'
                    END AS change_kind
                FROM observed
                LEFT JOIN leaderboard_scope_fingerprints existing
                  ON existing.song_id = observed.song_id
                 AND existing.instrument = observed.instrument
                 AND existing.scope_kind = 'alltime'
            ),
            upserted AS (
                INSERT INTO leaderboard_scope_fingerprints (
                    song_id,
                    instrument,
                    scope_kind,
                    fingerprint_version,
                    content_fingerprint,
                    coverage_fingerprint,
                    entry_count,
                    reported_total_entries,
                    reported_total_pages,
                    is_complete,
                    min_rank,
                    max_rank,
                    source_scrape_id,
                    published_scrape_id,
                    first_seen_scrape_id,
                    last_changed_scrape_id,
                    last_seen_scrape_id,
                    changed_at,
                    seen_at)
                SELECT
                    song_id,
                    instrument,
                    'alltime',
                    fingerprint_version,
                    content_fingerprint,
                    coverage_fingerprint,
                    entry_count,
                    reported_total_entries,
                    reported_total_pages,
                    is_complete,
                    min_rank,
                    max_rank,
                    @scrapeId,
                    NULL,
                    COALESCE(existing_first_seen_scrape_id, @scrapeId),
                    CASE WHEN change_kind = 'unchanged'
                        THEN COALESCE(existing_last_changed_scrape_id, @scrapeId)
                        ELSE @scrapeId
                    END,
                    @scrapeId,
                    CASE WHEN change_kind = 'unchanged'
                        THEN COALESCE(existing_changed_at, @now)
                        ELSE @now
                    END,
                    @now
                FROM classified
                ON CONFLICT (song_id, instrument, scope_kind) DO UPDATE SET
                    fingerprint_version = EXCLUDED.fingerprint_version,
                    content_fingerprint = EXCLUDED.content_fingerprint,
                    coverage_fingerprint = EXCLUDED.coverage_fingerprint,
                    entry_count = EXCLUDED.entry_count,
                    reported_total_entries = EXCLUDED.reported_total_entries,
                    reported_total_pages = EXCLUDED.reported_total_pages,
                    is_complete = EXCLUDED.is_complete,
                    min_rank = EXCLUDED.min_rank,
                    max_rank = EXCLUDED.max_rank,
                    source_scrape_id = EXCLUDED.source_scrape_id,
                    last_changed_scrape_id = EXCLUDED.last_changed_scrape_id,
                    last_seen_scrape_id = EXCLUDED.last_seen_scrape_id,
                    changed_at = EXCLUDED.changed_at,
                    seen_at = EXCLUDED.seen_at
                RETURNING 1
            ),
            applied AS (
                SELECT COUNT(*) FROM upserted
            )
            SELECT change_kind, COUNT(*)::int, COALESCE(SUM(entry_count), 0)::bigint
            FROM classified, applied
            GROUP BY change_kind
            """;
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        var manifestRows = (scopeManifests ?? new Dictionary<string, ScopeCompletenessManifest>())
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        cmd.Parameters.Add(
            "manifestSongIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            manifestRows.Select(static pair => pair.Key).ToArray();
        cmd.Parameters.Add(
            "manifestCoverageFingerprints",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            manifestRows.Select(static pair => pair.Value.CoverageFingerprint).ToArray();
        cmd.Parameters.Add(
            "manifestReportedEntries",
            NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value =
            manifestRows.Select(static pair => pair.Value.ReportedTotalEntries).ToArray();
        cmd.Parameters.Add(
            "manifestReportedPages",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            manifestRows.Select(static pair => pair.Value.ReportedTotalPages).ToArray();
        cmd.Parameters.Add(
            "manifestIsComplete",
            NpgsqlDbType.Array | NpgsqlDbType.Boolean).Value =
            manifestRows.Select(static pair => pair.Value.IsComplete).ToArray();

        var newScopes = 0;
        var changedScopes = 0;
        var unchangedScopes = 0;
        long newRows = 0;
        long changedRows = 0;
        long unchangedRows = 0;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var kind = reader.GetString(0);
            var scopeCount = reader.GetInt32(1);
            var rowCount = reader.GetInt64(2);
            switch (kind)
            {
                case "new":
                    newScopes = scopeCount;
                    newRows = rowCount;
                    break;
                case "changed":
                    changedScopes = scopeCount;
                    changedRows = rowCount;
                    break;
                case "unchanged":
                    unchangedScopes = scopeCount;
                    unchangedRows = rowCount;
                    break;
            }
        }

        _log.LogInformation(
            "Leaderboard scope fingerprints observed for {Instrument} scrape {ScrapeId}: new={NewScopes:N0} ({NewRows:N0} rows), changed={ChangedScopes:N0} ({ChangedRows:N0} rows), unchanged={UnchangedScopes:N0} ({UnchangedRows:N0} rows).",
            instrument,
            scrapeId,
            newScopes,
            newRows,
            changedScopes,
            changedRows,
            unchangedScopes,
            unchangedRows);
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
    /// Delete all staging data, deep-scrape jobs, and abandoned incomplete scrape logs for scrape IDs older than the given one.
    /// Call at scrape start and on startup.
    /// </summary>
    public int CleanupAbandonedStaging(long currentScrapeId)
    {
        var deleted = _metaDb.CleanupAbandonedStaging(currentScrapeId);
        if (deleted > 0)
            _log.LogInformation("Cleaned up {Deleted} abandoned staging/log rows from incomplete scrape runs.", deleted);
        return deleted;
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
    /// Get a player's scores across all instruments from the finalized current leaderboard projection.
    /// </summary>
    public List<PlayerScoreDto> GetCurrentStatePlayerProfile(string accountId, string? songId = null, HashSet<string>? instruments = null)
    {
        var profiles = GetCurrentStatePlayerProfiles([accountId], songId, instruments);
        return profiles.TryGetValue(accountId, out var scores) ? scores : [];
    }

    public List<PlayerScoreDto> GetCurrentStatePlayerProfileWithFallback(
        string accountId,
        string? songId = null,
        HashSet<string>? instruments = null)
    {
        var current = GetCurrentStatePlayerProfile(accountId, songId, instruments);
        if (UsePublishedScopeSourcesForCurrentRead)
            return current;

        var resolved = GetResolvedCurrentStatePlayerProfile(accountId, songId, instruments);
        var fallback = MergeCurrentStateProfileWithFallback(resolved, GetPlayerProfile(accountId, songId, instruments));
        return MergeCurrentStateProfileWithFallback(current, fallback);
    }

    public Dictionary<string, List<PlayerScoreDto>> GetCurrentStatePlayerProfilesWithFallback(
        IReadOnlyCollection<string> accountIds,
        string? songId = null,
        HashSet<string>? instruments = null)
    {
        var currentProfiles = GetCurrentStatePlayerProfiles(accountIds, songId, instruments);
        if (UsePublishedScopeSourcesForCurrentRead)
            return currentProfiles;

        var result = new Dictionary<string, List<PlayerScoreDto>>(currentProfiles, StringComparer.OrdinalIgnoreCase);

        foreach (var accountId in accountIds.Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var current = currentProfiles.TryGetValue(accountId, out var scores) ? scores : [];
            var resolved = GetResolvedCurrentStatePlayerProfile(accountId, songId, instruments);
            var fallback = MergeCurrentStateProfileWithFallback(resolved, GetPlayerProfile(accountId, songId, instruments));
            var merged = MergeCurrentStateProfileWithFallback(current, fallback);
            if (merged.Count > 0)
                result[accountId] = merged;
        }

        return result;
    }

    private static List<PlayerScoreDto> MergeCurrentStateProfileWithFallback(
        List<PlayerScoreDto> current,
        List<PlayerScoreDto> fallback)
    {
        if (fallback.Count == 0)
            return current;
        if (current.Count == 0)
            return fallback;

        var seen = new HashSet<(string SongId, string Instrument)>();
        var merged = new List<PlayerScoreDto>(current.Count + fallback.Count);
        foreach (var score in current)
        {
            if (seen.Add((score.SongId, score.Instrument)))
                merged.Add(score);
        }

        foreach (var score in fallback)
        {
            if (seen.Add((score.SongId, score.Instrument)))
                merged.Add(score);
        }

        return merged;
    }

    private List<PlayerScoreDto> GetResolvedCurrentStatePlayerProfile(
        string accountId,
        string? songId,
        HashSet<string>? instruments)
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

    private Dictionary<string, List<PlayerScoreDto>> GetPublishedScopePlayerProfiles(
        string[] accountIds,
        string? songId,
        HashSet<string>? instruments)
    {
        using var conn = _pgDataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        var songFilter = songId is not null ? "AND source.song_id = @songId" : string.Empty;
        var instrumentFilter = instruments is { Count: > 0 }
            ? "AND source.instrument = ANY(@instruments)"
            : string.Empty;
        cmd.CommandText = $"""
            WITH {PublishedSoloScopeSql.CurrentSourcesCte},
            eligible_sources AS (
                SELECT source.*
                FROM published_sources source
                WHERE TRUE
                  {songFilter}
                  {instrumentFilter}
            ), ready_projection_sources AS (
                SELECT source.*, scope.projection_generation
                FROM eligible_sources source
                JOIN solo_current_projection_scope scope
                  ON scope.song_id = source.song_id
                 AND scope.instrument = source.instrument
                 AND scope.status = 'ready'
                 AND scope.source_snapshot_id IS NOT DISTINCT FROM source.projection_source_snapshot_id
                 AND (
                     scope.row_count = 0
                     OR EXISTS (
                         SELECT 1
                         FROM current_leaderboard_entries projection
                         WHERE projection.song_id = scope.song_id
                           AND projection.instrument = scope.instrument
                           AND projection.projection_generation = scope.projection_generation
                     )
                 )
            ), projected_rows AS (
                SELECT
                    projection.account_id,
                    projection.song_id,
                    projection.instrument,
                    projection.score,
                    projection.accuracy,
                    projection.is_full_combo,
                    projection.stars,
                    projection.season,
                    projection.difficulty,
                    projection.percentile,
                    projection.end_time,
                    projection.rank,
                    projection.api_rank
                FROM current_leaderboard_entries projection
                JOIN ready_projection_sources source
                  ON source.song_id = projection.song_id
                 AND source.instrument = projection.instrument
                 AND source.projection_generation = projection.projection_generation
                WHERE projection.account_id = ANY(@accountIds)
            ), fallback_sources AS (
                SELECT source.*
                FROM eligible_sources source
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM ready_projection_sources ready
                    WHERE ready.song_id = source.song_id
                      AND ready.instrument = source.instrument
                )
            ), fallback_candidates AS (
                SELECT
                    snapshot.account_id,
                    snapshot.song_id,
                    snapshot.instrument,
                    snapshot.score,
                    snapshot.accuracy,
                    snapshot.is_full_combo,
                    snapshot.stars,
                    snapshot.season,
                    snapshot.difficulty,
                    snapshot.percentile,
                    snapshot.end_time,
                    snapshot.rank,
                    snapshot.api_rank,
                    1 AS origin_precedence,
                    0 AS source_priority
                FROM leaderboard_entries_snapshot snapshot
                JOIN fallback_sources source
                  ON source.song_id = snapshot.song_id
                 AND source.instrument = snapshot.instrument
                 AND source.source_kind = 'snapshot'
                 AND source.source_snapshot_id = snapshot.snapshot_id
                WHERE snapshot.account_id = ANY(@accountIds)
                UNION ALL
                SELECT
                    overlay.account_id,
                    overlay.song_id,
                    overlay.instrument,
                    overlay.score,
                    overlay.accuracy,
                    overlay.is_full_combo,
                    overlay.stars,
                    overlay.season,
                    overlay.difficulty,
                    overlay.percentile,
                    overlay.end_time,
                    overlay.rank,
                    overlay.api_rank,
                    0 AS origin_precedence,
                    overlay.source_priority
                FROM leaderboard_entries_overlay overlay
                JOIN fallback_sources source
                  ON source.song_id = overlay.song_id
                 AND source.instrument = overlay.instrument
                WHERE overlay.account_id = ANY(@accountIds)
            ), fallback_rows AS (
                SELECT DISTINCT ON (account_id, song_id, instrument)
                    account_id,
                    song_id,
                    instrument,
                    score,
                    accuracy,
                    is_full_combo,
                    stars,
                    season,
                    difficulty,
                    percentile,
                    end_time,
                    rank,
                    api_rank
                FROM fallback_candidates
                ORDER BY account_id, song_id, instrument, origin_precedence ASC, source_priority DESC
            )
            SELECT * FROM projected_rows
            UNION ALL
            SELECT * FROM fallback_rows
            ORDER BY account_id, instrument, song_id
            """;
        cmd.CommandTimeout = 0;
        cmd.Parameters.Add("accountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = accountIds;
        if (songId is not null)
            cmd.Parameters.AddWithValue("songId", songId);
        if (instruments is { Count: > 0 })
            cmd.Parameters.Add("instruments", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = instruments.ToArray();

        var result = new Dictionary<string, List<PlayerScoreDto>>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var accountId = reader.GetString(0);
            if (!result.TryGetValue(accountId, out var scores))
            {
                scores = [];
                result[accountId] = scores;
            }

            scores.Add(new PlayerScoreDto
            {
                SongId = reader.GetString(1),
                Instrument = reader.GetString(2),
                Score = reader.GetInt32(3),
                Accuracy = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                IsFullCombo = !reader.IsDBNull(5) && reader.GetBoolean(5),
                Stars = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                Season = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                Difficulty = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                Percentile = reader.IsDBNull(9) ? 0 : reader.GetDouble(9),
                EndTime = reader.IsDBNull(10) ? null : reader.GetString(10),
                Rank = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                ApiRank = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
            });
        }

        return result;
    }

    /// <summary>
    /// Get current-state player scores for many accounts from the finalized current leaderboard projection.
    /// </summary>
    public Dictionary<string, List<PlayerScoreDto>> GetCurrentStatePlayerProfiles(
        IReadOnlyCollection<string> accountIds,
        string? songId = null,
        HashSet<string>? instruments = null,
        bool preferValidatedProjection = false)
    {
        var normalizedAccountIds = accountIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedAccountIds.Length == 0)
            return new Dictionary<string, List<PlayerScoreDto>>(StringComparer.OrdinalIgnoreCase);

        var maintenancePublishedReadPass =
            Volatile.Read(
                ref _maxScoreMaintenancePublishedReadPass) != 0;
        if (!maintenancePublishedReadPass
            && UsePublishedScopeSourcesForCurrentRead)
            return GetPublishedScopePlayerProfiles(normalizedAccountIds, songId, instruments);

        if (maintenancePublishedReadPass
            || UseSnapshotOverlayWorkerReadersForCurrentRead
               && !UseValidatedCurrentProjectionForWorkerReaders
               && !preferValidatedProjection)
        {
            var dbs = instruments is null
                ? _instrumentDbs.ToArray()
                : _instrumentDbs.Where(pair => instruments.Contains(pair.Key)).ToArray();
            var profilesByInstrument =
                new Dictionary<string, List<PlayerScoreDto>>[dbs.Length];

            Parallel.For(0, dbs.Length, index =>
            {
                profilesByInstrument[index] =
                    ((InstrumentDatabase)dbs[index].Value)
                    .GetCurrentStatePlayerScoresForAccounts(normalizedAccountIds, songId);
            });

            var resolvedProfiles =
                new Dictionary<string, List<PlayerScoreDto>>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < dbs.Length; index++)
            {
                foreach (var (accountId, instrumentScores) in profilesByInstrument[index])
                {
                    if (!resolvedProfiles.TryGetValue(accountId, out var scores))
                    {
                        scores = [];
                        resolvedProfiles[accountId] = scores;
                    }
                    scores.AddRange(instrumentScores);
                }
            }

            foreach (var scores in resolvedProfiles.Values)
            {
                scores.Sort(static (left, right) =>
                {
                    var instrumentOrder = string.Compare(
                        left.Instrument,
                        right.Instrument,
                        StringComparison.Ordinal);
                    return instrumentOrder != 0
                        ? instrumentOrder
                        : string.Compare(left.SongId, right.SongId, StringComparison.Ordinal);
                });
            }

            return resolvedProfiles;
        }

        using var conn = _pgDataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        var songFilter = songId is not null ? "\n  AND projection.song_id = @songId" : string.Empty;
        var instrumentFilter = instruments is { Count: > 0 } ? "\n  AND projection.instrument = ANY(@instruments)" : string.Empty;
        var projectionSourceJoin = UseSnapshotOverlayWorkerReadersForCurrentRead
            ? """
              JOIN solo_current_projection_scope scope
                ON scope.song_id = projection.song_id
               AND scope.instrument = projection.instrument
               AND scope.projection_generation = projection.projection_generation
               AND scope.status = 'ready'
              """
            : string.Empty;
        var projectionSourceFilter = UseSnapshotOverlayWorkerReadersForCurrentRead
            ? """
                AND (
                        (
                            scope.source_kind = 'snapshot'
                            AND EXISTS (
                                SELECT 1
                                FROM leaderboard_snapshot_state state
                                WHERE state.song_id = scope.song_id
                                  AND state.instrument = scope.instrument
                                  AND state.is_finalized = TRUE
                                  AND state.active_snapshot_id IS NOT NULL
                                  AND state.active_snapshot_id IS NOT DISTINCT FROM scope.source_snapshot_id
                            )
                        )
                        OR (
                            scope.source_kind = 'overlay'
                            AND scope.source_snapshot_id IS NULL
                            AND EXISTS (
                                SELECT 1
                                FROM leaderboard_entries_overlay overlay
                                WHERE overlay.song_id = scope.song_id
                                  AND overlay.instrument = scope.instrument
                            )
                        )
                    )
              """
            : string.Empty;
        cmd.CommandText = $"""
            SELECT projection.account_id, projection.song_id, projection.instrument, projection.score,
                   projection.accuracy, projection.is_full_combo, projection.stars, projection.season,
                   projection.difficulty, projection.percentile, projection.end_time, projection.rank,
                   projection.api_rank
            FROM current_leaderboard_entries projection
            {projectionSourceJoin}
            WHERE projection.account_id = ANY(@accountIds){songFilter}{instrumentFilter}
              {projectionSourceFilter}
            ORDER BY projection.account_id, projection.instrument, projection.song_id
            """;
        cmd.CommandTimeout = 0;
        cmd.Parameters.Add("accountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = normalizedAccountIds;
        if (songId is not null)
            cmd.Parameters.AddWithValue("songId", songId);
        if (instruments is { Count: > 0 })
            cmd.Parameters.Add("instruments", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = instruments.ToArray();

        var result = new Dictionary<string, List<PlayerScoreDto>>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var accountId = reader.GetString(0);
            if (!result.TryGetValue(accountId, out var scores))
            {
                scores = [];
                result[accountId] = scores;
            }

            scores.Add(new PlayerScoreDto
            {
                SongId = reader.GetString(1),
                Instrument = reader.GetString(2),
                Score = reader.GetInt32(3),
                Accuracy = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                IsFullCombo = !reader.IsDBNull(5) && reader.GetBoolean(5),
                Stars = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                Season = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                Difficulty = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                Percentile = reader.IsDBNull(9) ? 0 : reader.GetDouble(9),
                EndTime = reader.IsDBNull(10) ? null : reader.GetString(10),
                Rank = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                ApiRank = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
            });
        }

        return result;
    }

    /// <summary>
    /// Get song IDs that satisfy all requested member solo-score presence conditions.
    /// </summary>
    public List<string> GetCurrentStateSongIdsForMemberScoreFilter(
        IReadOnlyCollection<string> hasScoreAccountIds,
        IReadOnlyCollection<string> missingScoreAccountIds,
        IReadOnlyCollection<string> instruments,
        double? leeway = null)
    {
        var normalizedHasAccountIds = hasScoreAccountIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedMissingAccountIds = missingScoreAccountIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedInstruments = instruments
            .Where(IsValidInstrument)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedInstruments.Length == 0 || (normalizedHasAccountIds.Length == 0 && normalizedMissingAccountIds.Length == 0))
            return [];

        var allAccountIds = normalizedHasAccountIds
            .Concat(normalizedMissingAccountIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var publishedSourceCtes = UsePublishedScopeSourcesForCurrentRead
            ? $"""
                {PublishedSoloScopeSql.CurrentSourcesCte},
                eligible_sources AS (
                    SELECT source.song_id, source.instrument, source.source_kind, source.source_snapshot_id
                    FROM published_sources source
                    WHERE source.instrument = ANY(@instruments)
                ),
                candidate_scores AS (
                    SELECT snapshot.account_id, snapshot.song_id, snapshot.instrument, snapshot.score,
                           1 AS origin_precedence, 0 AS source_priority
                    FROM leaderboard_entries_snapshot snapshot
                    JOIN eligible_sources source
                      ON source.song_id = snapshot.song_id
                     AND source.instrument = snapshot.instrument
                     AND source.source_kind = 'snapshot'
                     AND source.source_snapshot_id = snapshot.snapshot_id
                    WHERE snapshot.account_id = ANY(@allAccountIds)
                    UNION ALL
                    SELECT overlay.account_id, overlay.song_id, overlay.instrument, overlay.score,
                           0 AS origin_precedence, overlay.source_priority
                    FROM leaderboard_entries_overlay overlay
                    JOIN eligible_sources source
                      ON source.song_id = overlay.song_id
                     AND source.instrument = overlay.instrument
                    WHERE overlay.account_id = ANY(@allAccountIds)
                ),
                resolved_scores AS (
                    SELECT DISTINCT ON (account_id, song_id, instrument)
                        account_id, song_id, instrument, score
                    FROM candidate_scores
                    ORDER BY account_id, song_id, instrument, origin_precedence ASC, source_priority DESC
                ),
                """
            : string.Empty;
        var currentScoreSource = UsePublishedScopeSourcesForCurrentRead
            ? "resolved_scores cle"
            : "current_leaderboard_entries cle";

        using var conn = _pgDataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH {publishedSourceCtes}eligible_songs AS (
                SELECT song_id
                FROM songs
                WHERE {BuildChartedInstrumentPredicate(normalizedInstruments)}
            ), valid_scores AS (
                SELECT DISTINCT cle.account_id, cle.song_id
                FROM {currentScoreSource}
                LEFT JOIN song_stats ss
                  ON ss.song_id = cle.song_id
                 AND ss.instrument = cle.instrument
                WHERE cle.account_id = ANY(@allAccountIds)
                  AND cle.instrument = ANY(@instruments)
                  AND (
                        @leeway IS NULL
                        OR ss.max_score IS NULL
                        OR ss.max_score <= 0
                        OR cle.score <= CAST(ss.max_score * (1.0 + @leeway / 100.0) AS INTEGER)
                        OR EXISTS (
                            SELECT 1
                            FROM score_history sh
                            WHERE sh.account_id = cle.account_id
                              AND sh.song_id = cle.song_id
                              AND sh.instrument = cle.instrument
                              AND sh.new_score <= CAST(ss.max_score * (1.0 + @leeway / 100.0) AS INTEGER)
                        )
                  )
            )
            SELECT es.song_id
            FROM eligible_songs es
            WHERE NOT EXISTS (
                    SELECT 1
                    FROM unnest(@hasAccountIds) AS required(account_id)
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM valid_scores vs
                        WHERE vs.account_id = required.account_id
                          AND vs.song_id = es.song_id
                    )
                )
              AND NOT EXISTS (
                    SELECT 1
                    FROM unnest(@missingAccountIds) AS required(account_id)
                    WHERE EXISTS (
                        SELECT 1
                        FROM valid_scores vs
                        WHERE vs.account_id = required.account_id
                          AND vs.song_id = es.song_id
                    )
                )
            ORDER BY es.song_id
            """;
        cmd.CommandTimeout = 0;
        cmd.Parameters.Add("allAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = allAccountIds;
        cmd.Parameters.Add("hasAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = normalizedHasAccountIds;
        cmd.Parameters.Add("missingAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = normalizedMissingAccountIds;
        cmd.Parameters.Add("instruments", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = normalizedInstruments;
        cmd.Parameters.Add("leeway", NpgsqlDbType.Double).Value = leeway.HasValue ? leeway.Value : DBNull.Value;

        var songIds = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            songIds.Add(reader.GetString(0));
        return songIds;
    }

    private static string BuildChartedInstrumentPredicate(IReadOnlyCollection<string> instruments)
    {
        var predicates = new List<string>();
        foreach (var instrument in instruments)
        {
            var column = instrument switch
            {
                "Solo_Guitar" => "lead_diff",
                "Solo_Bass" => "bass_diff",
                "Solo_Drums" => "drums_diff",
                "Solo_Vocals" => "vocals_diff",
                "Solo_PeripheralGuitar" => "plastic_guitar_diff",
                "Solo_PeripheralBass" => "plastic_bass_diff",
                "Solo_PeripheralVocals" => "pro_vocals_diff",
                "Solo_PeripheralCymbals" => "plastic_drums_diff",
                "Solo_PeripheralDrums" => "plastic_drums_diff",
                _ => null,
            };
            if (column is not null)
                predicates.Add($"({column} IS NOT NULL AND {column} >= 0 AND {column} <> 99)");
        }

        return predicates.Count > 0 ? string.Join(" OR ", predicates.Distinct(StringComparer.Ordinal)) : "FALSE";
    }

    /// <summary>
    /// Get grouped unique band summaries for one player, deduped by team members.
    /// </summary>
    public PlayerBandsDto GetPlayerBands(string accountId, int previewCount = 6)
    {
        using var conn = _pgDataSource.OpenConnection();
        if (!HasBandSearchProjection(conn))
            return CreateEmptyPlayerBands();

        return new PlayerBandsDto
        {
            All = GetPlayerBandProjectionGroup(conn, accountId, null, previewCount),
            Duos = GetPlayerBandProjectionGroup(conn, accountId, "Band_Duets", previewCount),
            Trios = GetPlayerBandProjectionGroup(conn, accountId, "Band_Trios", previewCount),
            Quads = GetPlayerBandProjectionGroup(conn, accountId, "Band_Quad", previewCount),
        };
    }

    /// <summary>
    /// Get all unique bands for one player within a single band type, with optional combo filtering.
    /// </summary>
    public PlayerBandTypeResponseDto GetPlayerBandsByType(string accountId, string bandType, string? comboId = null)
    {
        using var conn = _pgDataSource.OpenConnection();
        var page = HasBandSearchProjection(conn)
            ? GetPlayerBandProjectionPage(conn, accountId, bandType, comboId, page: 1, pageSize: null)
            : new PlayerBandProjectionPage(0, []);

        return new PlayerBandTypeResponseDto
        {
            AccountId = accountId,
            BandType = bandType,
            ComboId = comboId,
            TotalCount = page.TotalCount,
            Entries = page.Entries,
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

        using var conn = _pgDataSource.OpenConnection();
        var projectionPage = HasBandSearchProjection(conn)
            ? GetPlayerBandProjectionPage(conn, accountId, bandTypeFilter, comboIdFilter: null, page, pageSize)
            : new PlayerBandProjectionPage(0, []);

        return new PlayerBandListResponseDto
        {
            AccountId = accountId,
            Group = group,
            TotalCount = projectionPage.TotalCount,
            Entries = projectionPage.Entries,
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

    public List<BandConfigurationDto> GetBandConfigurations(string bandType, string teamKey)
    {
        using var conn = _pgDataSource.OpenConnection();
        EnsureBandTeamConfigurations(conn, bandType, teamKey);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT instrument_combo, assignment_key, appearance_count, member_assignments_json::text
            FROM {BandLeaderboardPersistence.BandTeamConfigurationTable}
            WHERE band_type = @bandType AND team_key = @teamKey
            ORDER BY appearance_count DESC, instrument_combo, assignment_key
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);

        var configurations = new List<BandConfigurationDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rawCombo = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var comboId = BandComboIds.FromEpicRawCombo(rawCombo);
            configurations.Add(new BandConfigurationDto
            {
                RawInstrumentCombo = rawCombo,
                ComboId = comboId,
                Instruments = BandComboIds.ToInstruments(comboId).ToList(),
                AssignmentKey = reader.GetString(1),
                AppearanceCount = reader.GetInt32(2),
                MemberInstruments = ParseMemberAssignmentJson(reader.IsDBNull(3) ? "{}" : reader.GetString(3)),
            });
        }

        return configurations;
    }

    private void EnsureBandTeamConfigurations(NpgsqlConnection conn, string bandType, string teamKey)
    {
        using (var existsCmd = conn.CreateCommand())
        {
            existsCmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {BandLeaderboardPersistence.BandTeamConfigurationTable} WHERE band_type = @bandType AND team_key = @teamKey)";
            existsCmd.Parameters.AddWithValue("bandType", bandType);
            existsCmd.Parameters.AddWithValue("teamKey", teamKey);
            if (Convert.ToBoolean(existsCmd.ExecuteScalar()))
                return;
        }

        using var tx = conn.BeginTransaction();
        BandLeaderboardPersistence.RebuildBandTeamConfigurationsForTeams(conn, tx, bandType, [teamKey]);
        tx.Commit();
    }

    private PlayerBandGroupDto GetPlayerBandProjectionGroup(NpgsqlConnection conn, string accountId, string? bandTypeFilter, int previewCount)
    {
        var page = GetPlayerBandProjectionPage(conn, accountId, bandTypeFilter, comboIdFilter: null, page: 1, pageSize: previewCount);
        return new PlayerBandGroupDto
        {
            TotalCount = page.TotalCount,
            Entries = page.Entries,
        };
    }

    private PlayerBandProjectionPage GetPlayerBandProjectionPage(
        NpgsqlConnection conn,
        string accountId,
        string? bandTypeFilter,
        string? comboIdFilter,
        int page,
        int? pageSize)
    {
        var rawComboFilter = comboIdFilter is null
            ? null
            : BandComboIds.ToEpicRawComboCandidates(comboIdFilter).ToArray();

        if (comboIdFilter is not null && rawComboFilter is { Length: 0 })
            return new PlayerBandProjectionPage(0, []);

        var totalCount = CountPlayerBandProjectionRows(conn, accountId, bandTypeFilter, rawComboFilter);
        if (totalCount == 0)
            return new PlayerBandProjectionPage(0, []);

        var rows = GetPlayerBandProjectionRows(conn, accountId, bandTypeFilter, rawComboFilter, page, pageSize);
        if (rows.Count == 0)
            return new PlayerBandProjectionPage(totalCount, []);

        var allMemberAccountIds = rows
            .SelectMany(row => row.MemberAccountIds.Length > 0
                ? (IEnumerable<string>)row.MemberAccountIds
                : SplitTeamKey(row.TeamKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var displayNames = _metaDb.GetDisplayNames(allMemberAccountIds);

        var entries = rows
            .Select(row => BuildPlayerBandProjectionEntry(row, displayNames, rawComboFilter))
            .Where(static entry => entry is not null)
            .Select(static entry => entry!)
            .ToList();

        return new PlayerBandProjectionPage(totalCount, entries);
    }

    private static int CountPlayerBandProjectionRows(
        NpgsqlConnection conn,
        string accountId,
        string? bandTypeFilter,
        string[]? rawComboFilter)
    {
        using var cmd = conn.CreateCommand();
        var bandTypePredicate = bandTypeFilter is null ? string.Empty : "\n  AND member_projection.band_type = @bandType";
        var comboPredicate = rawComboFilter is null ? string.Empty : "\n  AND member_projection.instrument_combos && @rawCombos";
        cmd.CommandText = $"""
            SELECT COUNT(*)
            FROM {BandSearchProjectionBuilder.MemberProjectionTable} member_projection
            WHERE member_projection.account_id = @accountId{bandTypePredicate}{comboPredicate}
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        if (bandTypeFilter is not null)
            cmd.Parameters.AddWithValue("bandType", bandTypeFilter);
        if (rawComboFilter is not null)
            cmd.Parameters.AddWithValue("rawCombos", rawComboFilter);

        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static List<PlayerBandProjectionRow> GetPlayerBandProjectionRows(
        NpgsqlConnection conn,
        string accountId,
        string? bandTypeFilter,
        string[]? rawComboFilter,
        int page,
        int? pageSize)
    {
        using var cmd = conn.CreateCommand();
        var bandTypePredicate = bandTypeFilter is null ? string.Empty : "\n      AND member_projection.band_type = @bandType";
        var comboPredicate = rawComboFilter is null ? string.Empty : "\n      AND member_projection.instrument_combos && @rawCombos";
        var appearanceExpression = rawComboFilter is null
            ? "member_projection.team_appearance_count"
            : """
              COALESCE((
                  SELECT SUM(combo_count.value::integer)::integer
                  FROM jsonb_each_text(team_projection.combo_appearances_json) combo_count
                  WHERE combo_count.key = ANY(@rawCombos)
              ), 0)
              """;
        var limitClause = pageSize is > 0 ? "\n            LIMIT @limit OFFSET @offset" : string.Empty;

        cmd.CommandText = $"""
            WITH candidate_rows AS (
                SELECT
                    member_projection.band_type,
                    member_projection.team_key,
                    {appearanceExpression} AS effective_appearance_count
                FROM {BandSearchProjectionBuilder.MemberProjectionTable} member_projection
                JOIN {BandSearchProjectionBuilder.TeamProjectionTable} team_projection
                  ON team_projection.band_type = member_projection.band_type
                 AND team_projection.team_key = member_projection.team_key
                WHERE member_projection.account_id = @accountId{bandTypePredicate}{comboPredicate}
                ORDER BY effective_appearance_count DESC, member_projection.team_key{limitClause}
            )
            SELECT
                team_projection.band_type,
                team_projection.team_key,
                team_projection.band_id,
                team_projection.appearance_count,
                candidate_rows.effective_appearance_count,
                team_projection.member_account_ids,
                team_projection.member_instruments_json::text,
                team_projection.combo_appearances_json::text
            FROM candidate_rows
            JOIN {BandSearchProjectionBuilder.TeamProjectionTable} team_projection
              ON team_projection.band_type = candidate_rows.band_type
             AND team_projection.team_key = candidate_rows.team_key
            ORDER BY candidate_rows.effective_appearance_count DESC, candidate_rows.team_key
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        if (bandTypeFilter is not null)
            cmd.Parameters.AddWithValue("bandType", bandTypeFilter);
        if (rawComboFilter is not null)
            cmd.Parameters.AddWithValue("rawCombos", rawComboFilter);
        if (pageSize is > 0)
        {
            cmd.Parameters.AddWithValue("limit", pageSize.Value);
            cmd.Parameters.AddWithValue("offset", (Math.Max(1, page) - 1) * pageSize.Value);
        }

        var rows = new List<PlayerBandProjectionRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PlayerBandProjectionRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetFieldValue<string[]>(5),
                ParseMemberInstrumentsJson(reader.IsDBNull(6) ? "{}" : reader.GetString(6)),
                ParseComboAppearancesJson(reader.IsDBNull(7) ? "{}" : reader.GetString(7))));
        }

        return rows;
    }

    private static PlayerBandEntryDto? BuildPlayerBandProjectionEntry(
        PlayerBandProjectionRow row,
        IReadOnlyDictionary<string, string> displayNames,
        string[]? rawComboFilter)
    {
        var memberAccountIds = row.MemberAccountIds.Length > 0
            ? row.MemberAccountIds.ToList()
            : SplitTeamKey(row.TeamKey);
        var memberInstruments = rawComboFilter is null
            ? row.MemberInstruments
            : BuildMemberInstrumentsForRawCombos(memberAccountIds, row.ComboAppearances.Keys.Where(rawComboFilter.Contains));

        var appearanceCount = rawComboFilter is null
            ? row.AppearanceCount
            : rawComboFilter.Sum(rawCombo => row.ComboAppearances.GetValueOrDefault(rawCombo));
        if (appearanceCount <= 0)
            return null;

        var bandId = string.IsNullOrWhiteSpace(row.BandId)
            ? BandIdentity.CreateBandId(row.BandType, row.TeamKey)
            : row.BandId;

        return new PlayerBandEntryDto
        {
            BandId = bandId,
            TeamKey = row.TeamKey,
            BandType = row.BandType,
            AppearanceCount = appearanceCount,
            Members = memberAccountIds
                .Select(memberAccountId => new PlayerBandMemberDto
                {
                    AccountId = memberAccountId,
                    DisplayName = displayNames.GetValueOrDefault(memberAccountId),
                    Instruments = memberInstruments.GetValueOrDefault(memberAccountId, []),
                })
                .ToList(),
        };
    }

    private static Dictionary<string, List<string>> BuildMemberInstrumentsForRawCombos(
        IReadOnlyList<string> memberAccountIds,
        IEnumerable<string> rawCombos)
    {
        var instrumentsByMember = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawCombo in rawCombos)
        {
            var rawInstrumentIds = rawCombo.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var index = 0; index < memberAccountIds.Count && index < rawInstrumentIds.Length; index++)
            {
                if (!int.TryParse(rawInstrumentIds[index], out var instrumentId))
                    continue;

                var instrument = BandInstrumentMapping.ToLeaderboardType(instrumentId);
                if (instrument is null)
                    continue;

                if (!instrumentsByMember.TryGetValue(memberAccountIds[index], out var memberInstruments))
                {
                    memberInstruments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    instrumentsByMember[memberAccountIds[index]] = memberInstruments;
                }

                memberInstruments.Add(instrument);
            }
        }

        return instrumentsByMember.ToDictionary(
            static kvp => kvp.Key,
            static kvp => BandComboIds.ToInstruments(BandComboIds.FromInstruments(kvp.Value)).ToList(),
            StringComparer.OrdinalIgnoreCase);
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

    private static Dictionary<string, int> ParseComboAppearancesJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var count))
                result[property.Name] = count;
            else if (property.Value.ValueKind == JsonValueKind.String && int.TryParse(property.Value.GetString(), out var parsedCount))
                result[property.Name] = parsedCount;
        }

        return result;
    }

    private static Dictionary<string, string> ParseMemberAssignmentJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            var instrument = property.Value.GetString();
            if (!string.IsNullOrWhiteSpace(instrument))
                result[property.Name] = instrument;
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
        var identity = ResolveBandIdentityFromIdentityTable(conn, bandId);
        if (identity is not null)
        {
            var projected = ResolveBandIdentityFromProjectionTeam(conn, identity.BandType, identity.TeamKey);
            return projected ?? identity;
        }

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

    private static BandIdentityLookup? ResolveBandIdentityFromIdentityTable(NpgsqlConnection conn, string bandId)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT band_type, team_key, appearance_count, member_account_ids
                FROM {BandIdentityPersistence.TableName}
                WHERE band_id = @bandId
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("bandId", bandId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            return new BandIdentityLookup(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetFieldValue<string[]>(3).ToList(),
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null;
        }
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

    public Dictionary<(string SongId, string Instrument), long> GetCurrentStateLeaderboardPopulation()
    {
        if (!UsePublishedScopeSourcesForCurrentRead)
            return _metaDb.GetAllLeaderboardPopulation();

        using var conn = _pgDataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH {PublishedSoloScopeSql.CurrentSourcesCte}
            SELECT source.song_id, source.instrument, source.reported_total_entries
            FROM published_sources source
            """;
        var result = new Dictionary<(string SongId, string Instrument), long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[(reader.GetString(0), reader.GetString(1))] = reader.GetInt64(2);
        return result;
    }

    public long GetCurrentStateLeaderboardPopulation(string songId, string instrument)
    {
        if (!UsePublishedScopeSourcesForCurrentRead)
            return _metaDb.GetLeaderboardPopulation(songId, instrument);

        using var conn = _pgDataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH {PublishedSoloScopeSql.CurrentSourcesCte}
            SELECT source.reported_total_entries
            FROM published_sources source
            WHERE source.song_id = @songId
              AND source.instrument = @instrument
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
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

        var knownDbs = _instrumentDbs.Where(kvp => CanonicalInstrumentKeys.ContainsKey(kvp.Key)).ToList();

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

        var knownDbs = _instrumentDbs.Where(kvp => CanonicalInstrumentKeys.ContainsKey(kvp.Key)).ToList();

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
    private sealed record PlayerBandProjectionPage(int TotalCount, List<PlayerBandEntryDto> Entries);
    private sealed record PlayerBandProjectionRow(
        string BandType,
        string TeamKey,
        string BandId,
        int AppearanceCount,
        int EffectiveAppearanceCount,
        string[] MemberAccountIds,
        Dictionary<string, List<string>> MemberInstruments,
        Dictionary<string, int> ComboAppearances);
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
