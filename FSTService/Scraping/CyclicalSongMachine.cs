using System.Collections.Concurrent;
using FortniteFestival.Core.Services;
using FSTService.Auth;
using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

/// <summary>
/// A persistent, cyclical song-processing loop that callers <b>attach to</b> mid-cycle.
/// Songs iterate in a fixed, deterministic order (sorted by song ID). Late-arriving
/// callers join the current cycle, ride it to completion, then loop back through any
/// songs they missed. The machine goes idle when all callers' work is done.
///
/// <para>Replaces the transient <see cref="SongProcessingMachine"/> as the primary
/// entry point for post-scrape refresh, backfill, and history reconstruction.</para>
/// </summary>
public class CyclicalSongMachine
{
    private readonly SongProcessingMachine _inner;
    private readonly HistoryReconstructor _historyReconstructor;
    private readonly TokenManager _tokenManager;
    private readonly SharedDopPool _pool;
    private readonly ScrapeProgressTracker _progress;
    private readonly UserSyncProgressTracker _syncTracker;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IOptions<ScraperOptions> _options;
    private readonly ILogger<CyclicalSongMachine> _log;

    /// <summary>Lock protecting mutable state: _attachments, _currentCycleTask, _cycleSongList, _cycleSongIndex.</summary>
    private readonly object _lock = new();

    /// <summary>Active attachments keyed by caller ID.</summary>
    private readonly ConcurrentDictionary<string, MachineAttachment> _attachments = new(StringComparer.Ordinal);

    /// <summary>Signaled when a new attachment is added while the machine is idle.</summary>
    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);

    /// <summary>The currently running cycle task, or null if idle.</summary>
    private Task? _currentCycleTask;

    /// <summary>CTS for the current cycle (shutdown).</summary>
    private CancellationTokenSource? _cycleCts;

    /// <summary>Global CTS for machine lifetime (disposed on shutdown).</summary>
    private CancellationTokenSource? _lifetimeCts;

    /// <summary>The sorted song list for the current cycle. Null when idle.</summary>
    private IReadOnlyList<string>? _cycleSongList;

    /// <summary>Current song index in the cycle (0-based). -1 when idle.</summary>
    private volatile int _cycleSongIndex = -1;

    /// <summary>Season windows discovered for the current cycle.</summary>
    private IReadOnlyList<SeasonWindowInfo>? _cycleSeasonWindows;

    private int _attachmentCounter;

    public CyclicalSongMachine(
        SongProcessingMachine inner,
        HistoryReconstructor historyReconstructor,
        TokenManager tokenManager,
        SharedDopPool pool,
        ScrapeProgressTracker progress,
        UserSyncProgressTracker syncTracker,
        GlobalLeaderboardPersistence persistence,
        IOptions<ScraperOptions> options,
        ILogger<CyclicalSongMachine> log)
    {
        _inner = inner;
        _historyReconstructor = historyReconstructor;
        _tokenManager = tokenManager;
        _pool = pool;
        _progress = progress;
        _syncTracker = syncTracker;
        _persistence = persistence;
        _options = options;
        _log = log;
    }

    /// <summary>Protected parameterless constructor for test mocking.</summary>
    protected CyclicalSongMachine() { _log = null!; _inner = null!; _historyReconstructor = null!; _tokenManager = null!; _pool = null!; _progress = null!; _syncTracker = null!; _persistence = null!; _options = null!; }

    // ─── Public API ──────────────────────────────────────────

    /// <summary>
    /// Attach a set of users to the cyclical machine. Returns a task that completes
    /// when all users in this attachment have been processed for ALL songs (including
    /// loop-back for songs missed if the caller joined mid-cycle).
    /// </summary>
    /// <param name="users">Users to process.</param>
    /// <param name="songIds">
    /// Charted song IDs for this caller. If the machine is idle, these become the cycle's
    /// song list. If the machine is mid-cycle, the caller's songs must be a subset or
    /// superset — the cycle song list is not modified. New songs are picked up next cycle.
    /// </param>
    /// <param name="seasonWindows">Season windows for seasonal queries.</param>
    /// <param name="isHighPriority">True for post-scrape, false for backfill.</param>
    /// <param name="ct">Cancellation token for this caller.</param>
    /// <returns>Aggregated result for this caller's users when all songs are processed.</returns>
    public virtual Task<SongProcessingMachine.MachineResult> AttachAsync(
        IReadOnlyList<UserWorkItem> users,
        IReadOnlyList<string> songIds,
        IReadOnlyList<SeasonWindowInfo> seasonWindows,
        bool isHighPriority,
        CancellationToken ct = default)
    {
        if (users.Count == 0)
            return Task.FromResult(new SongProcessingMachine.MachineResult());

        var callerId = $"attach-{Interlocked.Increment(ref _attachmentCounter)}";
        var attachment = new MachineAttachment(callerId, users, songIds, seasonWindows, isHighPriority, ct);

        _attachments[callerId] = attachment;

        _log.LogInformation(
            "Attachment {CallerId} added: {Users} users, {Songs} songs, priority={Priority}.",
            callerId, users.Count, songIds.Count, isHighPriority ? "high" : "low");

        EnsureCycleRunning();

        // When the caller's CT fires, we don't remove the attachment (the cycle handles it)
        // but we do cancel the TCS.
        ct.Register(() =>
        {
            if (_attachments.TryRemove(callerId, out var removed))
            {
                removed.TryCancel();
                _log.LogDebug("Attachment {CallerId} cancelled by caller.", callerId);
            }
        });

        return attachment.Completion.Task;
    }

    /// <summary>Whether the machine is currently cycling (not idle).</summary>
    public bool IsActive => _currentCycleTask is not null && !_currentCycleTask.IsCompleted;

    /// <summary>Current song index in the active cycle, or -1 if idle.</summary>
    public int CurrentSongIndex => _cycleSongIndex;

    /// <summary>Total songs in the active cycle, or 0 if idle.</summary>
    public int CurrentCycleSongCount => _cycleSongList?.Count ?? 0;

    /// <summary>Number of currently attached callers.</summary>
    public int AttachedCallerCount => _attachments.Count;

    /// <summary>
    /// Start the background cycle loop. Called once at application startup.
    /// </summary>
    public void Start(CancellationToken appLifetime)
    {
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(appLifetime);
    }

    /// <summary>
    /// Stop the machine gracefully. Cancels any in-progress cycle and completes
    /// all attachments with <see cref="OperationCanceledException"/>.
    /// </summary>
    public void Stop()
    {
        _lifetimeCts?.Cancel();

        foreach (var (_, attachment) in _attachments)
            attachment.TryCancel();

        _attachments.Clear();
    }

    // ─── Cycle orchestration ────────────────────────────────

    private void EnsureCycleRunning()
    {
        lock (_lock)
        {
            if (_currentCycleTask is not null && !_currentCycleTask.IsCompleted)
                return; // Already running — new attachment will be picked up

            var cts = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCts?.Token ?? CancellationToken.None);
            _cycleCts = cts;

            _currentCycleTask = Task.Run(() => RunCycleLoopAsync(cts.Token));
        }
    }

    /// <summary>
    /// The main cycle loop. Runs until all attachments are satisfied and no new ones arrive.
    /// </summary>
    private async Task RunCycleLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _attachments.Count > 0)
            {
                await RunOneCycleAsync(ct);

                // After a cycle, check if any attachments still need loop-back.
                // Attachments that joined mid-cycle need songs 0..joinIndex-1.
                var needsLoopBack = false;
                foreach (var (_, att) in _attachments)
                {
                    if (att.IsCompleted) continue;
                    if (att.NeedsLoopBack)
                    {
                        needsLoopBack = true;
                        break;
                    }
                }

                if (!needsLoopBack)
                {
                    // Complete all remaining attachments
                    CompleteFinishedAttachments();

                    if (_attachments.Count == 0)
                        break; // Go idle
                }
                // else: loop-back cycle will handle remaining songs
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogInformation("CyclicalSongMachine cycle cancelled.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CyclicalSongMachine cycle failed unexpectedly.");
        }
        finally
        {
            _cycleSongIndex = -1;
            _cycleSongList = null;
            _cycleSeasonWindows = null;

            // Complete any stragglers with what we have
            CompleteFinishedAttachments();

            _log.LogInformation("CyclicalSongMachine going idle. {Remaining} attachments remain.",
                _attachments.Count);
        }
    }

    /// <summary>
    /// Run one cycle: iterate all songs in order, processing users from all active attachments.
    /// </summary>
    private async Task RunOneCycleAsync(CancellationToken ct)
    {
        // ── Build the sorted song list (snapshot for this cycle) ──
        var songList = BuildSortedSongList();
        if (songList.Count == 0)
        {
            _log.LogWarning("No charted songs available. Skipping cycle.");
            return;
        }

        // ── Discover season windows ──
        var seasonWindows = await DiscoverSeasonWindowsAsync(ct);

        lock (_lock)
        {
            _cycleSongList = songList;
            _cycleSeasonWindows = seasonWindows;
        }

        // ── Stamp join index on attachments that haven't been stamped yet ──
        StampJoinIndices(startIndex: 0);

        var seasonPrefixMap = new Dictionary<int, string>();
        foreach (var w in seasonWindows)
            seasonPrefixMap[w.SeasonNumber] = HistoryReconstructor.GetSeasonPrefix(w.SeasonNumber);

        var instruments = GlobalLeaderboardScraper.AllInstruments;

        // Get access token for the cycle
        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is null)
        {
            _log.LogWarning("No access token available for song machine cycle.");
            return;
        }
        var callerAccountId = _tokenManager.AccountId!;

        var opts = _options.Value;

        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.SongMachine);
        _progress.SetAdaptiveLimiter(_pool.Limiter);

        // Determine which songs to process this cycle.
        // If we have loop-back attachments, process only the missed songs.
        var songsToProcess = DetermineSongsToProcess(songList);

        _progress.BeginPhaseProgress(songsToProcess.Count);
        _progress.SetPhaseAccounts(GetTotalUserCount());

        _log.LogInformation(
            "CyclicalSongMachine cycle starting: {Songs} songs (of {Total}), {Attachments} attachments, {Users} total users.",
            songsToProcess.Count, songList.Count, _attachments.Count, GetTotalUserCount());

        // ── Song gate for concurrency ──
        int maxConcurrentSongs = opts.SongMachineDop;
        SemaphoreSlim? songGate = maxConcurrentSongs > 0
            ? new SemaphoreSlim(maxConcurrentSongs, maxConcurrentSongs)
            : null;

        try
        {
            var songTasks = songsToProcess.Select(async songEntry =>
            {
                ct.ThrowIfCancellationRequested();

                if (songGate is not null)
                    await songGate.WaitAsync(ct);

                try
                {
                    // Gather users from all active (non-completed) attachments for this song
                    var (users, highPriority) = GatherUsersForSong(songEntry.SongId);
                    if (users.Count == 0) return;

                    var result = await _inner.ProcessSongForUsersAsync(
                        songEntry.SongId, instruments, users, seasonPrefixMap,
                        accessToken, callerAccountId, _pool, highPriority,
                        opts.LookupBatchSize, ct);

                    // Update per-attachment results
                    foreach (var (_, att) in _attachments)
                    {
                        if (att.IsCompleted) continue;
                        att.RecordSongResult(songEntry.GlobalIndex, result);
                    }

                    // Report per-user history recon progress
                    foreach (var user in users)
                    {
                        if (user.Purposes.HasFlag(WorkPurpose.HistoryRecon))
                        {
                            _syncTracker.ReportHistoryItem(
                                user.AccountId,
                                seasonsQueried: seasonPrefixMap.Count,
                                entriesFound: result.SessionsInserted);
                        }
                    }

                    _progress.ReportPhaseItemComplete();
                }
                finally
                {
                    songGate?.Release();
                }

                // Update global song index for status reporting
                Interlocked.Exchange(ref _cycleSongIndex, songEntry.GlobalIndex);

                // Stamp join indices for any new attachments that arrived mid-cycle
                StampJoinIndices(startIndex: songEntry.GlobalIndex + 1);

            }).ToList();

            await Task.WhenAll(songTasks);
        }
        finally
        {
            songGate?.Dispose();
        }

        _progress.SetAdaptiveLimiter(null);

        // Mark loop-back as done for attachments that completed their missed range
        foreach (var (_, att) in _attachments)
        {
            if (att.IsCompleted) continue;
            att.MarkCyclePassComplete();
        }

        CompleteFinishedAttachments();

        _log.LogInformation(
            "CyclicalSongMachine cycle complete. {Remaining} attachments still active.",
            _attachments.Count);
    }

    // ─── Song list building ─────────────────────────────────

    /// <summary>
    /// Build a deterministically sorted song list from all attachments' song IDs.
    /// Uses the union of all provided song IDs, sorted for deterministic ordering.
    /// </summary>
    private List<string> BuildSortedSongList()
    {
        var allSongIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, att) in _attachments)
        {
            foreach (var songId in att.SongIds)
                allSongIds.Add(songId);
        }

        var sorted = allSongIds.ToList();
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }

    /// <summary>
    /// Determine which songs to process in this cycle pass. On the first pass,
    /// process all songs. On loop-back passes, process only the missed range
    /// for attachments that joined mid-cycle.
    /// </summary>
    private List<SongCycleEntry> DetermineSongsToProcess(IReadOnlyList<string> fullSongList)
    {
        var result = new List<SongCycleEntry>();
        var neededIndices = new HashSet<int>();

        foreach (var (_, att) in _attachments)
        {
            if (att.IsCompleted) continue;

            foreach (int idx in att.GetMissingSongIndices(fullSongList.Count))
                neededIndices.Add(idx);
        }

        // If no specific indices needed (all attachments joined at 0), process everything
        if (neededIndices.Count == 0)
        {
            for (int i = 0; i < fullSongList.Count; i++)
                result.Add(new SongCycleEntry(fullSongList[i], i));
        }
        else
        {
            foreach (int idx in neededIndices.OrderBy(i => i))
                result.Add(new SongCycleEntry(fullSongList[idx], idx));
        }

        return result;
    }

    // ─── User gathering ─────────────────────────────────────

    /// <summary>
    /// Gather all users across all active attachments for a given song.
    /// Returns the merged user list and whether any attachment is high priority.
    /// </summary>
    private (List<UserWorkItem> Users, bool HighPriority) GatherUsersForSong(string songId)
    {
        var users = new List<UserWorkItem>();
        bool highPriority = false;

        foreach (var (_, att) in _attachments)
        {
            if (att.IsCompleted) continue;
            // Only include users from attachments that need this song
            if (!att.SongIds.Contains(songId)) continue;

            users.AddRange(att.Users);
            if (att.IsHighPriority)
                highPriority = true;
        }

        // Deduplicate by AccountId (a user may appear in multiple attachments)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduplicated = new List<UserWorkItem>();
        foreach (var user in users)
        {
            if (seen.Add(user.AccountId))
                deduplicated.Add(user);
        }

        return (deduplicated, highPriority);
    }

    private int GetTotalUserCount()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, att) in _attachments)
        {
            if (att.IsCompleted) continue;
            foreach (var user in att.Users)
                seen.Add(user.AccountId);
        }
        return seen.Count;
    }

    // ─── Attachment lifecycle ────────────────────────────────

    /// <summary>
    /// Stamp the join index on any attachment that hasn't been stamped yet.
    /// </summary>
    private void StampJoinIndices(int startIndex)
    {
        foreach (var (_, att) in _attachments)
        {
            if (att.JoinedAtSongIndex < 0)
                att.StampJoinIndex(startIndex);
        }
    }

    /// <summary>
    /// Complete attachments that have processed all songs (including loop-back).
    /// </summary>
    private void CompleteFinishedAttachments()
    {
        foreach (var (callerId, att) in _attachments)
        {
            if (att.IsFullyComplete)
            {
                att.Complete();
                _attachments.TryRemove(callerId, out _);

                _log.LogInformation(
                    "Attachment {CallerId} completed: {Updated} entries, {Sessions} sessions, {ApiCalls} API calls.",
                    callerId, att.TotalEntriesUpdated, att.TotalSessionsInserted, att.TotalApiCalls);
            }
        }
    }

    // ─── Season windows ─────────────────────────────────────

    /// <summary>
    /// Discover season windows. Reuses previously discovered windows if available,
    /// merging with any new windows from attachments.
    /// </summary>
    private async Task<IReadOnlyList<SeasonWindowInfo>> DiscoverSeasonWindowsAsync(CancellationToken ct)
    {
        // Use windows from attachments if any provided
        foreach (var (_, att) in _attachments)
        {
            if (att.SeasonWindows.Count > 0)
                return att.SeasonWindows;
        }

        // Otherwise discover fresh
        try
        {
            var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
            if (accessToken is null) return [];

            return await _historyReconstructor.DiscoverSeasonWindowsAsync(
                accessToken, _tokenManager.AccountId!, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Season window discovery failed. Using empty season list.");
            return [];
        }
    }

    // ─── Inner types ────────────────────────────────────────

    private readonly record struct SongCycleEntry(string SongId, int GlobalIndex);

    /// <summary>
    /// Represents one caller's attachment to the cyclical machine.
    /// Tracks join point, processed songs, and aggregated results.
    /// </summary>
    internal sealed class MachineAttachment
    {
        public string CallerId { get; }
        public IReadOnlyList<UserWorkItem> Users { get; }
        public IReadOnlyList<string> SongIds { get; }
        public IReadOnlyList<SeasonWindowInfo> SeasonWindows { get; }
        public bool IsHighPriority { get; }
        public TaskCompletionSource<SongProcessingMachine.MachineResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The song index at which this attachment joined the cycle. -1 = not yet stamped.</summary>
        public int JoinedAtSongIndex { get; private set; } = -1;

        /// <summary>Whether the first pass of the cycle is complete for this attachment.</summary>
        private bool _firstPassComplete;

        /// <summary>Whether the loop-back pass (songs before join index) is complete.</summary>
        private bool _loopBackComplete;

        /// <summary>Whether the attachment has been completed (TCS set).</summary>
        public bool IsCompleted => Completion.Task.IsCompleted;

        // Aggregated results
        public int TotalEntriesUpdated;
        public int TotalSessionsInserted;
        public int TotalApiCalls;

        private readonly HashSet<int> _processedSongIndices = [];
        private readonly CancellationToken _callerCt;
        private readonly HashSet<string> _songIdSet;

        public MachineAttachment(
            string callerId,
            IReadOnlyList<UserWorkItem> users,
            IReadOnlyList<string> songIds,
            IReadOnlyList<SeasonWindowInfo> seasonWindows,
            bool isHighPriority,
            CancellationToken callerCt)
        {
            CallerId = callerId;
            Users = users;
            SongIds = songIds;
            SeasonWindows = seasonWindows;
            IsHighPriority = isHighPriority;
            _callerCt = callerCt;
            _songIdSet = new HashSet<string>(songIds, StringComparer.Ordinal);
        }

        public void StampJoinIndex(int index)
        {
            if (JoinedAtSongIndex < 0)
                JoinedAtSongIndex = index;
        }

        /// <summary>Whether this attachment needs a loop-back pass for missed songs.</summary>
        public bool NeedsLoopBack => _firstPassComplete && !_loopBackComplete && JoinedAtSongIndex > 0;

        /// <summary>Whether this attachment is fully done (all songs processed).</summary>
        public bool IsFullyComplete
        {
            get
            {
                if (IsCompleted) return true;
                if (!_firstPassComplete) return false;
                if (JoinedAtSongIndex == 0) return true; // Joined at start — no loop-back needed
                return _loopBackComplete;
            }
        }

        /// <summary>
        /// Get the song indices that this attachment still needs processed.
        /// On the first pass, returns everything from joinIndex..totalSongs-1.
        /// On loop-back, returns 0..joinIndex-1.
        /// </summary>
        public IEnumerable<int> GetMissingSongIndices(int totalSongs)
        {
            if (IsCompleted) yield break;

            if (!_firstPassComplete)
            {
                // First pass: all songs (joined at 0) or from joinIndex onward
                for (int i = JoinedAtSongIndex; i < totalSongs; i++)
                {
                    if (!_processedSongIndices.Contains(i))
                        yield return i;
                }
            }
            else if (NeedsLoopBack)
            {
                // Loop-back: songs before join index
                for (int i = 0; i < JoinedAtSongIndex; i++)
                {
                    if (!_processedSongIndices.Contains(i))
                        yield return i;
                }
            }
        }

        public void RecordSongResult(int songIndex, SongProcessingMachine.SongStepResult result)
        {
            _processedSongIndices.Add(songIndex);
            Interlocked.Add(ref TotalEntriesUpdated, result.EntriesUpdated);
            Interlocked.Add(ref TotalSessionsInserted, result.SessionsInserted);
            Interlocked.Add(ref TotalApiCalls, result.ApiCalls);
        }

        public void MarkCyclePassComplete()
        {
            if (!_firstPassComplete)
                _firstPassComplete = true;
            else if (NeedsLoopBack)
                _loopBackComplete = true;
        }

        public void Complete()
        {
            Completion.TrySetResult(new SongProcessingMachine.MachineResult
            {
                EntriesUpdated = TotalEntriesUpdated,
                SessionsInserted = TotalSessionsInserted,
                ApiCalls = TotalApiCalls,
                UsersProcessed = Users.Count,
            });
        }

        public void TryCancel()
        {
            Completion.TrySetCanceled();
        }
    }
}
