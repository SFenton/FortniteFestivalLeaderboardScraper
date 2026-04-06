using System.Collections.Concurrent;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// A song-parallel batch processing machine that fires all songs concurrently,
/// batching registered users into V2 API calls to perform alltime lookups
/// and seasonal session queries.
///
/// Each instance is <b>discrete and single-use</b>: one caller (post-scrape,
/// registration backfill, etc.) creates a machine, runs it, and discards it.
/// All instances share a <see cref="SharedDopPool"/> that governs total
/// concurrent API calls with priority-aware slot allocation.
/// </summary>
public class SongProcessingMachine
{
    private readonly ILeaderboardQuerier _scraper;
    private readonly BatchResultProcessor _resultProcessor;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly ScrapeProgressTracker _progress;
    private readonly UserSyncProgressTracker _syncTracker;
    private readonly ILogger<SongProcessingMachine> _log;
    private readonly ResilientHttpExecutor? _executor;

    public SongProcessingMachine(
        ILeaderboardQuerier scraper,
        BatchResultProcessor resultProcessor,
        GlobalLeaderboardPersistence persistence,
        ScrapeProgressTracker progress,
        UserSyncProgressTracker syncTracker,
        ILogger<SongProcessingMachine> log,
        ResilientHttpExecutor? executor = null)
    {
        _scraper = scraper;
        _resultProcessor = resultProcessor;
        _persistence = persistence;
        _progress = progress;
        _syncTracker = syncTracker;
        _log = log;
        _executor = executor ?? (scraper as GlobalLeaderboardScraper)?.Executor;
    }

    /// <summary>
    /// Fallback for when no <see cref="ResilientHttpExecutor"/> is available (e.g. tests
    /// with a mock <see cref="ILeaderboardQuerier"/>). Acquires a slot, runs work,
    /// and releases the slot — no CDN resilience.
    /// </summary>
    private static async Task<T> FallbackAcquireAndRunAsync<T>(
        Func<Task> acquireSlot, Func<Task<T>> work, Action releaseSlot)
    {
        await acquireSlot();
        try
        {
            return await work();
        }
        finally
        {
            releaseSlot();
        }
    }

    /// <summary>
    /// Run the machine to completion. Fires all songs in parallel, bounded only
    /// by the shared DOP pool. Each song fans out 6 instruments in parallel,
    /// each instrument performs alltime + seasonal batch lookups.
    /// </summary>
    /// <param name="songIds">All charted song IDs to process.</param>
    /// <param name="users">Users to process (all songs processed for all users).</param>
    /// <param name="seasonWindows">Discovered season windows for seasonal queries.</param>
    /// <param name="accessToken">Epic access token.</param>
    /// <param name="callerAccountId">Caller's Epic account ID.</param>
    /// <param name="pool">Shared DOP pool for slot acquisition.</param>
    /// <param name="isHighPriority">True for post-scrape, false for backfill.</param>
    /// <param name="batchSize">Max accounts per V2 batch call.</param>
    /// <param name="reportProgress">Whether to report to ScrapeProgressTracker (only post-scrape machine should).</param>
    /// <param name="maxConcurrentSongs">Max songs processed concurrently. 0 = unlimited.</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task<MachineResult> RunAsync(
        IReadOnlyList<string> songIds,
        IReadOnlyList<UserWorkItem> users,
        IReadOnlyList<SeasonWindowInfo> seasonWindows,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        bool isHighPriority = true,
        int batchSize = 500,
        bool reportProgress = true,
        int maxConcurrentSongs = 0,
        CancellationToken ct = default)
    {
        if (songIds.Count == 0 || users.Count == 0)
            return new MachineResult();

        var instruments = GlobalLeaderboardScraper.AllInstruments;

        // Build season prefix lookup
        var seasonPrefixMap = new Dictionary<int, string>();
        foreach (var w in seasonWindows)
            seasonPrefixMap[w.SeasonNumber] = HistoryReconstructor.GetSeasonPrefix(w.SeasonNumber);

        int totalUpdated = 0;
        int totalSessions = 0;
        int totalApiCalls = 0;
        int songsCompleted = 0;

        // Users doing history recon get per-song progress via WebSocket
        var historyReconUsers = users
            .Where(u => u.Purposes.HasFlag(WorkPurpose.HistoryRecon))
            .ToList();

        if (reportProgress)
        {
            _progress.SetAdaptiveLimiter(pool.Limiter);
            _progress.BeginPhaseProgress(songIds.Count);
            _progress.SetPhaseAccounts(users.Count);
        }

        _log.LogInformation(
            "SongProcessingMachine starting: {Songs} songs, {Users} users, batch={BatchSize}, priority={Priority}, DOP={Dop}, songConcurrency={SongConcurrency}.",
            songIds.Count, users.Count, batchSize, isHighPriority ? "high" : "low", pool.CurrentDop,
            maxConcurrentSongs > 0 ? maxConcurrentSongs : songIds.Count);

        // Gate song-level parallelism to avoid saturating the DOP pool with
        // heavy V2 POST requests, which can trigger CDN blocks.
        SemaphoreSlim? songGate = maxConcurrentSongs > 0
            ? new SemaphoreSlim(maxConcurrentSongs, maxConcurrentSongs)
            : null;

        try
        {
        // ─── Fire ALL songs in parallel ─────────────────────────
        var songTasks = songIds.Select(async songId =>
        {
            ct.ThrowIfCancellationRequested();

            if (songGate is not null)
                await songGate.WaitAsync(ct);

            try
            {
            var result = await ProcessSongAsync(
                songId, instruments, users, seasonPrefixMap,
                accessToken, callerAccountId, pool, isHighPriority, batchSize, ct);

            Interlocked.Add(ref totalUpdated, result.EntriesUpdated);
            Interlocked.Add(ref totalSessions, result.SessionsInserted);
            Interlocked.Add(ref totalApiCalls, result.ApiCalls);
            Interlocked.Increment(ref songsCompleted);

            // Report per-user progress for history recon users
            foreach (var user in historyReconUsers)
            {
                _syncTracker.ReportHistoryItem(
                    user.AccountId,
                    seasonsQueried: seasonPrefixMap.Count,
                    entriesFound: result.SessionsInserted);
            }

            if (reportProgress)
                _progress.ReportPhaseItemComplete();
            }
            finally
            {
                songGate?.Release();
            }
        }).ToList();

        await Task.WhenAll(songTasks);
        }
        finally
        {
            songGate?.Dispose();
        }

        if (reportProgress)
            _progress.SetAdaptiveLimiter(null);

        _log.LogInformation(
            "SongProcessingMachine complete: {Updated} entries, {Sessions} sessions, {ApiCalls} API calls across {Songs} songs.",
            totalUpdated, totalSessions, totalApiCalls, songIds.Count);

        return new MachineResult
        {
            EntriesUpdated = totalUpdated,
            SessionsInserted = totalSessions,
            ApiCalls = totalApiCalls,
            UsersProcessed = users.Count,
        };
    }

    /// <summary>
    /// Process one song across all instruments for the given users.
    /// Fires 6 instrument tasks in parallel. Called by <see cref="CyclicalSongMachine"/>
    /// for each song in the cycle.
    /// </summary>
    public async Task<SongStepResult> ProcessSongForUsersAsync(
        string songId,
        IReadOnlyList<string> instruments,
        IReadOnlyList<UserWorkItem> users,
        IReadOnlyDictionary<int, string> seasonPrefixMap,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        bool isHighPriority,
        int batchSize,
        CancellationToken ct)
        => await ProcessSongAsync(songId, instruments, users, seasonPrefixMap,
            accessToken, callerAccountId, pool, isHighPriority, batchSize, ct);

    /// <summary>
    /// Process one song across all instruments for all users.
    /// Fires 6 instrument tasks in parallel.
    /// </summary>
    private async Task<SongStepResult> ProcessSongAsync(
        string songId,
        IReadOnlyList<string> instruments,
        IReadOnlyList<UserWorkItem> users,
        IReadOnlyDictionary<int, string> seasonPrefixMap,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        bool isHighPriority,
        int batchSize,
        CancellationToken ct)
    {
        int entriesUpdated = 0;
        int sessionsInserted = 0;
        int apiCalls = 0;

        // Shared across all instruments: when a pad instrument discovers
        // that a season returns no data (BadRequest or empty), record it
        // so other instruments can skip that season and earlier ones.
        var missingSeasonsForSong = new ConcurrentDictionary<int, bool>();

        var instrumentTasks = instruments.Select(async instrument =>
        {
            var result = await ProcessSongInstrumentAsync(
                songId, instrument, users, seasonPrefixMap,
                accessToken, callerAccountId, pool, isHighPriority, batchSize, ct,
                missingSeasonsForSong);

            Interlocked.Add(ref entriesUpdated, result.EntriesUpdated);
            Interlocked.Add(ref sessionsInserted, result.SessionsInserted);
            Interlocked.Add(ref apiCalls, result.ApiCalls);
        }).ToList();

        await Task.WhenAll(instrumentTasks);

        return new SongStepResult
        {
            EntriesUpdated = entriesUpdated,
            SessionsInserted = sessionsInserted,
            ApiCalls = apiCalls,
        };
    }

    /// <summary>
    /// Process one song + one instrument for all users.
    /// Performs alltime batch lookups and seasonal session lookups in parallel,
    /// since they target independent API endpoints and write to separate tables.
    /// </summary>
    private async Task<SongStepResult> ProcessSongInstrumentAsync(
        string songId,
        string instrument,
        IReadOnlyList<UserWorkItem> users,
        IReadOnlyDictionary<int, string> seasonPrefixMap,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        bool isHighPriority,
        int batchSize,
        CancellationToken ct,
        ConcurrentDictionary<int, bool>? missingSeasonsForSong = null)
    {
        int entriesUpdated = 0;
        int sessionsInserted = 0;
        int apiCalls = 0;

        // ─── Alltime lookups (async task) ─────────────────────
        var alltimeTask = RunAlltimeLookups(
            songId, instrument, users, accessToken, callerAccountId,
            pool, isHighPriority, batchSize, ct);

        // ─── Seasonal session lookups (async task) ────────────
        // Pro Lead/Bass didn't exist before Season 3
        int songFirstSeason = 1;
        if (instrument is "Solo_PeripheralGuitar" or "Solo_PeripheralBass")
            songFirstSeason = 3;

        var seasonalTask = RunSeasonalLookups(
            songId, instrument, users, seasonPrefixMap, accessToken, callerAccountId,
            pool, isHighPriority, batchSize, ct, songFirstSeason, missingSeasonsForSong);

        // Both phases use the SharedDopPool for backpressure — total API
        // concurrency remains bounded regardless of in-flight overlap.
        var results = await Task.WhenAll(alltimeTask, seasonalTask);

        foreach (var r in results)
        {
            Interlocked.Add(ref entriesUpdated, r.EntriesUpdated);
            Interlocked.Add(ref sessionsInserted, r.SessionsInserted);
            Interlocked.Add(ref apiCalls, r.ApiCalls);
        }

        // Mark history recon processed for users doing that work
        foreach (var user in users)
        {
            if (user.Purposes.HasFlag(WorkPurpose.HistoryRecon) && !user.IsAlreadyChecked(songId, instrument))
                _resultProcessor.MarkHistoryReconProcessed(user.AccountId, songId, instrument);
        }

        return new SongStepResult
        {
            EntriesUpdated = entriesUpdated,
            SessionsInserted = sessionsInserted,
            ApiCalls = apiCalls,
        };
    }

    /// <summary>
    /// Run alltime batch lookups for all users needing them on this song/instrument.
    /// </summary>
    private async Task<SongStepResult> RunAlltimeLookups(
        string songId,
        string instrument,
        IReadOnlyList<UserWorkItem> users,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        bool isHighPriority,
        int batchSize,
        CancellationToken ct)
    {
        int entriesUpdated = 0;
        int apiCalls = 0;

        var alltimeUsers = users
            .Where(u => u.AllTimeNeeded && !u.IsAlreadyChecked(songId, instrument))
            .ToList();

        if (alltimeUsers.Count > 0)
        {
            var existingSet = BuildExistingEntrySet(songId, instrument, alltimeUsers);

            for (int offset = 0; offset < alltimeUsers.Count; offset += batchSize)
            {
                var chunk = alltimeUsers.GetRange(offset, Math.Min(batchSize, alltimeUsers.Count - offset));
                var targetIds = chunk.Select(u => u.AccountId).ToList();

                // Acquire DOP slot only for the HTTP call, release immediately after.
                // WithCdnResilienceAsync handles pre-wait, CDN catch/release/retry.
                List<LeaderboardEntry> entries;
                LowPriorityToken lowToken = default;

                Func<Task> acquireSlot = async () =>
                {
                    if (isHighPriority) await pool.AcquireHighAsync(ct);
                    else lowToken = await pool.AcquireLowAsync(ct);
                };
                Action releaseSlot = () =>
                {
                    if (isHighPriority) pool.ReleaseHigh();
                    else pool.ReleaseLow(lowToken);
                };
                Func<Task<List<LeaderboardEntry>>> work = () =>
                {
                    _progress.ReportPhaseRequest();
                    Interlocked.Increment(ref apiCalls);
                    return _scraper.LookupMultipleAccountsAsync(
                        songId, instrument, targetIds,
                        accessToken, callerAccountId, limiter: pool.Limiter, ct);
                };

                try
                {
                    entries = _executor is not null
                        ? await _executor.WithCdnResilienceAsync(work, ct, acquireSlot, releaseSlot)
                        : await FallbackAcquireAndRunAsync(acquireSlot, work, releaseSlot);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogDebug(ex, "Alltime batch failed for {Song}/{Instrument}.", songId, instrument);
                    _progress.ReportPhaseRetry();
                    continue;
                }

                // DB processing runs outside the DOP slot — no API concurrency needed
                var count = _resultProcessor.ProcessAlltimeResults(songId, instrument, entries, existingSet);
                Interlocked.Add(ref entriesUpdated, count);
                if (count > 0)
                    _progress.ReportPhaseEntryUpdated(count);

                foreach (var user in chunk)
                {
                    if (user.Purposes.HasFlag(WorkPurpose.Backfill))
                    {
                        bool found = entries.Any(e => e.AccountId.Equals(user.AccountId, StringComparison.OrdinalIgnoreCase));
                        _resultProcessor.MarkBackfillChecked(user.AccountId, songId, instrument, found);
                    }
                }
            }
        }

        return new SongStepResult { EntriesUpdated = entriesUpdated, ApiCalls = apiCalls };
    }

    /// <summary>
    /// Run seasonal session lookups for all users needing them on this song/instrument.
    /// </summary>
    private async Task<SongStepResult> RunSeasonalLookups(
        string songId,
        string instrument,
        IReadOnlyList<UserWorkItem> users,
        IReadOnlyDictionary<int, string> seasonPrefixMap,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        bool isHighPriority,
        int batchSize,
        CancellationToken ct,
        int songFirstSeason = 1,
        ConcurrentDictionary<int, bool>? missingSeasonsForSong = null)
    {
        int sessionsInserted = 0;
        int apiCalls = 0;

        // Identify which accounts need history recon (chronological OldScore across all seasons)
        var historyReconAccounts = new HashSet<string>(
            users.Where(u => u.Purposes.HasFlag(WorkPurpose.HistoryRecon))
                 .Select(u => u.AccountId),
            StringComparer.OrdinalIgnoreCase);
        Dictionary<int, List<SessionHistoryEntry>>? historyReconSessions =
            historyReconAccounts.Count > 0 ? [] : null;

        var seasonUserGroups = new Dictionary<int, List<UserWorkItem>>();
        int skippedSeasonCombos = 0;
        foreach (var user in users)
        {
            if (user.SeasonsNeeded.Count == 0) continue;
            if (user.IsAlreadyChecked(songId, instrument)) continue;

            foreach (var season in user.SeasonsNeeded)
            {
                if (!seasonPrefixMap.ContainsKey(season)) continue;

                // Optimization A: skip seasons before the song was charted
                if (season < songFirstSeason)
                {
                    skippedSeasonCombos++;
                    continue;
                }

                if (!seasonUserGroups.TryGetValue(season, out var list))
                {
                    list = [];
                    seasonUserGroups[season] = list;
                }
                list.Add(user);
            }
        }

        foreach (var (season, seasonUsers) in seasonUserGroups.OrderByDescending(kv => kv.Key))
        {
            var seasonPrefix = seasonPrefixMap[season];

            // Check if another instrument already discovered this season (or later) is missing
            if (missingSeasonsForSong is not null &&
                missingSeasonsForSong.Any(kv => kv.Key >= season))
            {
                continue; // Song didn't exist in this season — skip
            }

            bool firstBatchEmpty = false;

            for (int offset = 0; offset < seasonUsers.Count; offset += batchSize)
            {
                var chunk = seasonUsers.GetRange(offset, Math.Min(batchSize, seasonUsers.Count - offset));
                var targetIds = chunk.Select(u => u.AccountId).ToList();

                // Acquire DOP slot only for the HTTP call, release immediately after.
                // WithCdnResilienceAsync handles pre-wait, CDN catch/release/retry.
                List<SessionHistoryEntry> sessions;
                LowPriorityToken lowToken = default;

                Func<Task> acquireSlot = async () =>
                {
                    if (isHighPriority) await pool.AcquireHighAsync(ct);
                    else lowToken = await pool.AcquireLowAsync(ct);
                };
                Action releaseSlot = () =>
                {
                    if (isHighPriority) pool.ReleaseHigh();
                    else pool.ReleaseLow(lowToken);
                };
                Func<Task<List<SessionHistoryEntry>>> work = () =>
                {
                    _progress.ReportPhaseRequest();
                    Interlocked.Increment(ref apiCalls);
                    return _scraper.LookupMultipleAccountSessionsAsync(
                        songId, instrument, seasonPrefix, targetIds,
                        accessToken, callerAccountId, limiter: pool.Limiter, ct);
                };

                try
                {
                    sessions = _executor is not null
                        ? await _executor.WithCdnResilienceAsync(work, ct, acquireSlot, releaseSlot)
                        : await FallbackAcquireAndRunAsync(acquireSlot, work, releaseSlot);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogDebug(ex, "Seasonal batch failed for {Song}/{Instrument}/{Season}.",
                        songId, instrument, seasonPrefix);
                    _progress.ReportPhaseRetry();

                    // If the first batch fails (BadRequest = song not charted in this season),
                    // mark so other instruments can skip this season and earlier ones.
                    if (offset == 0)
                        missingSeasonsForSong?.TryAdd(season, true);

                    continue;
                }

                // If the first batch for this season returned zero sessions,
                // the song likely didn't exist in this season (BadRequest or genuinely empty).
                // However, we can't distinguish BadRequest from "no users played this season"
                // at this level (the scraper swallows BadRequest as empty list).
                // Only break out of the batch loop for this season — do NOT mark
                // missingSeasonsForSong, as users may have scores in earlier seasons.
                if (offset == 0 && sessions.Count == 0)
                    break; // No point sending more batches for this season

                // DB processing runs outside the DOP slot.
                // Split: HistoryRecon users accumulate for cross-season OldScore,
                // non-HistoryRecon users process immediately per-season.
                if (historyReconSessions is not null)
                {
                    var normalSessions = new List<SessionHistoryEntry>();
                    var reconSessions = new List<SessionHistoryEntry>();

                    foreach (var s in sessions)
                    {
                        if (historyReconAccounts.Contains(s.AccountId))
                            reconSessions.Add(s);
                        else
                            normalSessions.Add(s);
                    }

                    if (normalSessions.Count > 0)
                    {
                        var count = _resultProcessor.ProcessSeasonalSessions(songId, instrument, season, normalSessions);
                        Interlocked.Add(ref sessionsInserted, count);
                        if (count > 0)
                            _progress.ReportPhaseEntryUpdated(count);
                    }

                    if (reconSessions.Count > 0)
                    {
                        if (!historyReconSessions.TryGetValue(season, out var reconList))
                        {
                            reconList = [];
                            historyReconSessions[season] = reconList;
                        }
                        reconList.AddRange(reconSessions);
                    }
                }
                else
                {
                    var count = _resultProcessor.ProcessSeasonalSessions(songId, instrument, season, sessions);
                    Interlocked.Add(ref sessionsInserted, count);
                    if (count > 0)
                        _progress.ReportPhaseEntryUpdated(count);
                }
            }
        }

        // Process accumulated history recon sessions with chronological OldScore
        if (historyReconSessions is not null && historyReconSessions.Count > 0)
        {
            var reconCount = _resultProcessor.ProcessAllSeasonalSessions(songId, instrument, historyReconSessions);
            Interlocked.Add(ref sessionsInserted, reconCount);
            if (reconCount > 0)
                _progress.ReportPhaseEntryUpdated(reconCount);
        }

        return new SongStepResult { SessionsInserted = sessionsInserted, ApiCalls = apiCalls };
    }

    /// <summary>
    /// Build a set of account IDs that already have entries in the instrument DB
    /// for the given song/instrument. Uses a single batch query instead of N individual lookups.
    /// </summary>
    private HashSet<string> BuildExistingEntrySet(
        string songId, string instrument, IReadOnlyList<UserWorkItem> users)
    {
        var instrumentDb = _persistence.GetOrCreateInstrumentDb(instrument);
        var accountIds = users.Select(u => u.AccountId).ToList();
        var entries = instrumentDb.GetEntriesForAccounts(songId, accountIds);
        return new HashSet<string>(entries.Keys, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Result of one complete machine run.</summary>
    public sealed class MachineResult
    {
        public int EntriesUpdated { get; init; }
        public int SessionsInserted { get; init; }
        public int ApiCalls { get; init; }
        public int UsersProcessed { get; init; }
    }

    /// <summary>Result of processing one song step.</summary>
    public sealed class SongStepResult
    {
        public int EntriesUpdated { get; init; }
        public int SessionsInserted { get; init; }
        public int ApiCalls { get; init; }
    }
}
