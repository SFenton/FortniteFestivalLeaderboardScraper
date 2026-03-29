using System.Collections.Concurrent;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Callback interface for machine lifecycle events.
/// </summary>
public interface IWorkCompletionHandler
{
    /// <summary>All original post-scrape users have completed their first full pass.</summary>
    void OnPostScrapeComplete(IReadOnlySet<string> accountIds);

    /// <summary>A single user has completed all their required work (backfill + recon).</summary>
    void OnUserBackfillComplete(string accountId);

    /// <summary>The machine has no more work to do and is about to exit.</summary>
    void OnMachineIdle();
}

/// <summary>
/// A song-first batch processing machine that iterates through all songs,
/// batching registered users into V2 API calls to perform alltime lookups
/// and seasonal session queries in a single unified pass.
///
/// Replaces the separate <see cref="PostScrapeRefresher"/>,
/// <see cref="ScoreBackfiller"/>, and <see cref="HistoryReconstructor"/>
/// with a single O(songs × instruments × seasons × ceil(users/batchSize)) system.
/// </summary>
public class SongProcessingMachine
{
    private readonly ILeaderboardQuerier _scraper;
    private readonly BatchResultProcessor _resultProcessor;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger<SongProcessingMachine> _log;

    /// <summary>Active roster of users being processed.</summary>
    private readonly List<UserWorkItem> _roster = [];

    /// <summary>Thread-safe queue for hot-adding users mid-run.</summary>
    private readonly UserWorkQueue _hotAddQueue = new();

    /// <summary>Thread-safe queue for hot-adding songs mid-run.</summary>
    private readonly ConcurrentQueue<string> _hotAddSongQueue = new();

    /// <summary>Current song index the machine is processing.</summary>
    private int _currentSongIndex;

    /// <summary>Songs processed so far in the current pass (for progress reporting).</summary>
    private int _songsProcessed;

    public SongProcessingMachine(
        ILeaderboardQuerier scraper,
        BatchResultProcessor resultProcessor,
        GlobalLeaderboardPersistence persistence,
        ScrapeProgressTracker progress,
        ILogger<SongProcessingMachine> log)
    {
        _scraper = scraper;
        _resultProcessor = resultProcessor;
        _persistence = persistence;
        _progress = progress;
        _log = log;
    }

    /// <summary>Enqueue a user for processing before the machine starts.</summary>
    public void EnqueueUser(UserWorkItem item) => _roster.Add(item);

    /// <summary>Hot-add a user while the machine is running. Thread-safe.</summary>
    public void HotAddUser(UserWorkItem item) => _hotAddQueue.Enqueue(item);

    /// <summary>Hot-add a new song while the machine is running. Thread-safe.</summary>
    public void HotAddSong(string songId) => _hotAddSongQueue.Enqueue(songId);

    /// <summary>Number of users pending in the hot-add queue (for progress).</summary>
    public int HotAddQueueDepth => _hotAddQueue.Count;

    /// <summary>
    /// Run the machine to completion. Iterates through all songs, processing all
    /// users' work requirements via batched V2 API calls. Loops back for hot-added
    /// users/songs until no work remains.
    /// </summary>
    public virtual async Task<MachineResult> RunAsync(
        IReadOnlyList<string> songIds,
        IReadOnlyList<SeasonWindowInfo> seasonWindows,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter limiter,
        int batchSize = 500,
        IWorkCompletionHandler? completionHandler = null,
        CancellationToken ct = default)
    {
        var songList = new List<string>(songIds);
        var instruments = GlobalLeaderboardScraper.AllInstruments;

        // Build season prefix lookup
        var seasonPrefixMap = new Dictionary<int, string>();
        foreach (var w in seasonWindows)
            seasonPrefixMap[w.SeasonNumber] = HistoryReconstructor.GetSeasonPrefix(w.SeasonNumber);

        // Set total songs needed for each user
        foreach (var user in _roster)
            user.TotalSongsNeeded = songList.Count;

        // Track which accounts started as post-scrape users
        var postScrapeAccountIds = new HashSet<string>(
            _roster.Where(u => u.Purposes.HasFlag(WorkPurpose.PostScrape)).Select(u => u.AccountId),
            StringComparer.OrdinalIgnoreCase);

        int totalUpdated = 0;
        int totalSessions = 0;
        int totalApiCalls = 0;
        bool postScrapeEmitted = false;

        _progress.SetAdaptiveLimiter(limiter);
        _progress.BeginPhaseProgress(songList.Count);
        _progress.SetPhaseAccounts(_roster.Count);

        // Early exit: no songs or no users with real work
        if (songList.Count == 0 || _roster.Count == 0)
        {
            _progress.SetAdaptiveLimiter(null);
            completionHandler?.OnMachineIdle();
            return new MachineResult();
        }

        _log.LogInformation(
            "SongProcessingMachine starting: {Songs} songs, {Users} users, batch size {BatchSize}, DOP={Dop}.",
            songList.Count, _roster.Count, batchSize, limiter.CurrentDop);

        // ─── Main song loop ─────────────────────────────────────
        bool hasMoreWork = true;
        while (hasMoreWork)
        {
            hasMoreWork = false;

            for (_currentSongIndex = 0; _currentSongIndex < songList.Count; _currentSongIndex++)
            {
                ct.ThrowIfCancellationRequested();

                // Drain hot-add queues
                DrainHotAddQueues(songList);

                var songId = songList[_currentSongIndex];

                // Determine which users need work at this song index
                var activeUsers = _roster.Where(u => !u.IsComplete && u.NeedsWorkAtSongIndex(_currentSongIndex)).ToList();
                if (activeUsers.Count == 0)
                {
                    Interlocked.Increment(ref _songsProcessed);
                    _progress.ReportPhaseItemComplete();
                    continue;
                }

                // ─── Process this song across all instruments ─────────
                var songResult = await ProcessSongAsync(
                    songId, instruments, activeUsers, seasonPrefixMap,
                    accessToken, callerAccountId, limiter, batchSize, ct);

                totalUpdated += songResult.EntriesUpdated;
                totalSessions += songResult.SessionsInserted;
                totalApiCalls += songResult.ApiCalls;

                // Mark song complete for each active user
                foreach (var user in activeUsers)
                {
                    if (!user.IsAlreadyChecked(songId, ""))
                        Interlocked.Increment(ref user.CompletedSongCount);
                }

                Interlocked.Increment(ref _songsProcessed);
                _progress.ReportPhaseItemComplete();

                // Check for newly completed users
                CheckCompletions(completionHandler);
            }

            // End of song list: emit post-scrape completion (first pass only)
            if (!postScrapeEmitted && postScrapeAccountIds.Count > 0)
            {
                completionHandler?.OnPostScrapeComplete(postScrapeAccountIds);
                postScrapeEmitted = true;
            }

            // Check if any hot-added users still need earlier songs (loop back)
            DrainHotAddQueues(songList);
            var incomplete = _roster.Where(u => !u.IsComplete).ToList();
            if (incomplete.Count > 0)
            {
                // Reset song progress for a new pass over the song list
                _songsProcessed = 0;
                _progress.BeginPhaseProgress(songList.Count);
                _progress.SetPhaseAccounts(incomplete.Count);

                _log.LogInformation(
                    "SongProcessingMachine looping back: {Users} users still need work.",
                    incomplete.Count);

                // For hot-added users, clear the starting index restriction
                // so they are eligible for ALL songs on subsequent passes.
                foreach (var user in incomplete)
                {
                    user.TotalSongsNeeded = songList.Count;
                    user.StartingSongIndex = -1;
                }

                hasMoreWork = true;
            }
        }

        _progress.SetAdaptiveLimiter(null);
        completionHandler?.OnMachineIdle();

        _log.LogInformation(
            "SongProcessingMachine complete: {Updated} entries updated, {Sessions} sessions inserted, {ApiCalls} API calls.",
            totalUpdated, totalSessions, totalApiCalls);

        return new MachineResult
        {
            EntriesUpdated = totalUpdated,
            SessionsInserted = totalSessions,
            ApiCalls = totalApiCalls,
            UsersProcessed = _roster.Count(u => u.IsComplete),
        };
    }

    /// <summary>
    /// Process one song across all instruments for all active users.
    /// Fires parallel batch V2 calls per instrument, then per season.
    /// </summary>
    private async Task<SongStepResult> ProcessSongAsync(
        string songId,
        IReadOnlyList<string> instruments,
        IReadOnlyList<UserWorkItem> activeUsers,
        IReadOnlyDictionary<int, string> seasonPrefixMap,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter limiter,
        int batchSize,
        CancellationToken ct)
    {
        int entriesUpdated = 0;
        int sessionsInserted = 0;
        int apiCalls = 0;

        // Process all instruments in parallel (each instrument's work acquires limiter slots)
        var instrumentTasks = instruments.Select(async instrument =>
        {
            var result = await ProcessSongInstrumentAsync(
                songId, instrument, activeUsers, seasonPrefixMap,
                accessToken, callerAccountId, limiter, batchSize, ct);

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
    /// Process one song + one instrument for all active users.
    /// Performs alltime batch lookup, then seasonal session lookups for each required season.
    /// </summary>
    private async Task<SongStepResult> ProcessSongInstrumentAsync(
        string songId,
        string instrument,
        IReadOnlyList<UserWorkItem> activeUsers,
        IReadOnlyDictionary<int, string> seasonPrefixMap,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter limiter,
        int batchSize,
        CancellationToken ct)
    {
        int entriesUpdated = 0;
        int sessionsInserted = 0;
        int apiCalls = 0;

        // ─── Alltime lookups ──────────────────────────────────
        var alltimeUsers = activeUsers
            .Where(u => u.AllTimeNeeded && !u.IsAlreadyChecked(songId, instrument))
            .ToList();

        if (alltimeUsers.Count > 0)
        {
            var existingSet = BuildExistingEntrySet(songId, instrument, alltimeUsers);

            for (int offset = 0; offset < alltimeUsers.Count; offset += batchSize)
            {
                var chunk = alltimeUsers.GetRange(offset, Math.Min(batchSize, alltimeUsers.Count - offset));
                var targetIds = chunk.Select(u => u.AccountId).ToList();

                await limiter.WaitAsync(ct);
                try
                {
                    _progress.ReportPhaseRequest();
                    Interlocked.Increment(ref apiCalls);

                    List<LeaderboardEntry> entries;
                    try
                    {
                        entries = await _scraper.LookupMultipleAccountsAsync(
                            songId, instrument, targetIds,
                            accessToken, callerAccountId, limiter, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogDebug(ex, "Alltime batch failed for {Song}/{Instrument}.", songId, instrument);
                        continue;
                    }

                    var count = _resultProcessor.ProcessAlltimeResults(songId, instrument, entries, existingSet);
                    Interlocked.Add(ref entriesUpdated, count);
                    if (count > 0)
                        _progress.ReportPhaseEntryUpdated(count);

                    // Mark backfill checked for each user in the chunk
                    foreach (var user in chunk)
                    {
                        if (user.Purposes.HasFlag(WorkPurpose.Backfill))
                        {
                            bool found = entries.Any(e => e.AccountId.Equals(user.AccountId, StringComparison.OrdinalIgnoreCase));
                            _resultProcessor.MarkBackfillChecked(user.AccountId, songId, instrument, found);
                        }
                    }
                }
                finally
                {
                    limiter.Release();
                }
            }
        }

        // ─── Seasonal session lookups ─────────────────────────
        // Group users by which seasons they need, to avoid querying seasons no one needs.
        var seasonUserGroups = new Dictionary<int, List<UserWorkItem>>();
        foreach (var user in activeUsers)
        {
            if (user.SeasonsNeeded.Count == 0) continue;
            if (user.IsAlreadyChecked(songId, instrument)) continue;

            foreach (var season in user.SeasonsNeeded)
            {
                if (!seasonPrefixMap.ContainsKey(season)) continue;

                if (!seasonUserGroups.TryGetValue(season, out var list))
                {
                    list = [];
                    seasonUserGroups[season] = list;
                }
                list.Add(user);
            }
        }

        foreach (var (season, seasonUsers) in seasonUserGroups)
        {
            var seasonPrefix = seasonPrefixMap[season];

            for (int offset = 0; offset < seasonUsers.Count; offset += batchSize)
            {
                var chunk = seasonUsers.GetRange(offset, Math.Min(batchSize, seasonUsers.Count - offset));
                var targetIds = chunk.Select(u => u.AccountId).ToList();

                await limiter.WaitAsync(ct);
                try
                {
                    _progress.ReportPhaseRequest();
                    Interlocked.Increment(ref apiCalls);

                    List<SessionHistoryEntry> sessions;
                    try
                    {
                        sessions = await _scraper.LookupMultipleAccountSessionsAsync(
                            songId, instrument, seasonPrefix, targetIds,
                            accessToken, callerAccountId, limiter, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogDebug(ex, "Seasonal batch failed for {Song}/{Instrument}/{Season}.",
                            songId, instrument, seasonPrefix);
                        continue;
                    }

                    var count = _resultProcessor.ProcessSeasonalSessions(songId, instrument, season, sessions);
                    Interlocked.Add(ref sessionsInserted, count);
                    if (count > 0)
                        _progress.ReportPhaseEntryUpdated(count);
                }
                finally
                {
                    limiter.Release();
                }
            }
        }

        // Mark history recon processed for users doing that work
        foreach (var user in activeUsers)
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
    /// Build a set of account IDs that already have entries in the instrument DB
    /// for the given song/instrument. Used to detect stale vs new entries.
    /// </summary>
    private HashSet<string> BuildExistingEntrySet(
        string songId, string instrument, IReadOnlyList<UserWorkItem> users)
    {
        var instrumentDb = _persistence.GetOrCreateInstrumentDb(instrument);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in users)
        {
            var entry = instrumentDb.GetEntry(songId, user.AccountId);
            if (entry is not null)
                existing.Add(user.AccountId);
        }

        return existing;
    }

    /// <summary>Drain hot-add queues and integrate new users/songs into the active roster.</summary>
    private void DrainHotAddQueues(List<string> songList)
    {
        // Drain new users
        var newUsers = _hotAddQueue.DrainAll();
        foreach (var user in newUsers)
        {
            user.TotalSongsNeeded = songList.Count;
            _roster.Add(user);
            _log.LogInformation("Hot-added user {AccountId} at song index {Index}.",
                user.AccountId, _currentSongIndex);
        }
        if (newUsers.Count > 0)
            _progress.SetPhaseAccounts(_roster.Count);

        // Drain new songs
        while (_hotAddSongQueue.TryDequeue(out var newSongId))
        {
            if (!songList.Contains(newSongId))
            {
                songList.Add(newSongId);
                // All users need this new song
                foreach (var user in _roster)
                    user.TotalSongsNeeded = songList.Count;

                _progress.AddPhaseItems(1);
                _log.LogInformation("Hot-added song {SongId}. Total songs now {Count}.", newSongId, songList.Count);
            }
        }
    }

    /// <summary>Check for users who have completed all their work and emit events.</summary>
    private void CheckCompletions(IWorkCompletionHandler? handler)
    {
        if (handler is null) return;

        foreach (var user in _roster)
        {
            if (!user.IsComplete) continue;

            if (user.Purposes.HasFlag(WorkPurpose.Backfill))
            {
                handler.OnUserBackfillComplete(user.AccountId);
                // Clear the flag so we don't emit again
                user.Purposes &= ~WorkPurpose.Backfill;
            }
        }
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
    private sealed class SongStepResult
    {
        public int EntriesUpdated { get; init; }
        public int SessionsInserted { get; init; }
        public int ApiCalls { get; init; }
    }
}
