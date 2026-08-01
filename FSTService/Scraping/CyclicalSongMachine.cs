using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using FortniteFestival.Core.Services;
using FSTService.Auth;
using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

/// <summary>
/// A persistent, cyclical song-processing loop that callers <b>attach to</b> mid-cycle.
/// Songs iterate in a deterministic order. Attachments may supply a preferred order;
/// remaining songs are appended by song ID. Late-arriving callers join the current
/// cycle, ride it to completion, then loop back through any songs they missed. The
/// machine goes idle when all callers' work is done.
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

    private long _cycleGeneration;
    private int _activeSongWorkers;
    private long _lastCycleProgressTicks = DateTime.UtcNow.Ticks;

    /// <summary>Global CTS for machine lifetime (disposed on shutdown).</summary>
    private CancellationTokenSource? _lifetimeCts;

    /// <summary>The sorted song list for the current cycle. Null when idle.</summary>
    private IReadOnlyList<string>? _cycleSongList;

    /// <summary>Current song index in the cycle (0-based). -1 when idle.</summary>
    private volatile int _cycleSongIndex = -1;

    /// <summary>Season windows discovered for the current cycle.</summary>
    private IReadOnlyList<SeasonWindowInfo>? _cycleSeasonWindows;

    private int _attachmentCounter;

    /// <summary>
    /// Whether a caller has set the global progress phase to SongMachine.
    /// When false (e.g. fire-and-forget track backfills), the machine skips
    /// all <see cref="ScrapeProgressTracker"/> writes so it doesn't clobber
    /// the phase that the main scrape loop is reporting.
    /// </summary>
    private bool OwnsProgress => _progress.Phase == ScrapeProgressTracker.ScrapePhase.SongMachine;

    internal static bool ShouldClearProgressWhenIdle(
        ScrapeProgressTracker.ScrapePhase phase,
        IEnumerable<MachineAttachment> attachments)
    {
        return phase == ScrapeProgressTracker.ScrapePhase.SongMachine
            && !attachments.Any(static attachment => attachment.PreserveProgressPhaseOnIdle);
    }

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
    /// <param name="source">Which orchestrator is attaching (for progress tracking).</param>
    /// <param name="isHighPriority">True for post-scrape, false for backfill.</param>
    /// <param name="ct">Cancellation token for this caller.</param>
    /// <param name="attachmentOptions">
    /// Optional caller-local ordering and incremental successful-scope callback.
    /// Other attachments remain unchanged when omitted.
    /// </param>
    /// <returns>Aggregated result for this caller's users when all songs are processed.</returns>
    public virtual Task<SongProcessingMachine.MachineResult> AttachAsync(
        IReadOnlyList<UserWorkItem> users,
        IReadOnlyList<string> songIds,
        IReadOnlyList<SeasonWindowInfo> seasonWindows,
        SongMachineSource source,
        bool isHighPriority,
        CancellationToken ct = default,
        bool preserveProgressPhaseOnIdle = false,
        EpicTrafficKind epicTrafficKind = EpicTrafficKind.Background,
        AttachmentOptions? attachmentOptions = null)
    {
        if (users.Count == 0)
            return Task.FromResult(new SongProcessingMachine.MachineResult());

        var attachmentNumber = Interlocked.Increment(ref _attachmentCounter);
        var callerId = $"attach-{attachmentNumber}";
        var attachment = new MachineAttachment(
            attachmentNumber,
            callerId,
            users,
            songIds,
            seasonWindows,
            source,
            isHighPriority,
            preserveProgressPhaseOnIdle,
            epicTrafficKind,
            attachmentOptions,
            ct);

        _attachments[callerId] = attachment;
        _progress.RegisterAttachment(callerId, source, users, songIds.Count);

        // Initialize PostScrape per-user progress for users that don't have a higher-priority phase active
        var instrumentCount = GlobalLeaderboardScraper.AllInstruments.Count;
        foreach (var user in users)
        {
            if (!user.Purposes.HasFlag(WorkPurpose.PostScrape)) continue;
            if (_syncTracker.IsActiveHigherPriority(user.AccountId)) continue;

            int totalUnits = ComputePostScrapeWorkUnits(user, songIds.Count, instrumentCount);
            _syncTracker.BeginPostScrape(user.AccountId, totalUnits);
        }

        _log.LogInformation(
            "Attachment {CallerId} added: {Users} users, {Songs} songs, priority={Priority}.",
            callerId, users.Count, songIds.Count, isHighPriority ? "high" : "low");

        MarkCycleProgress();
        EnsureCycleRunning();

        // Stop admitting new work immediately, but do not complete the caller's
        // cancellation until already-admitted song work has drained.
        ct.Register(() =>
        {
            if (_attachments.TryRemove(callerId, out var removed))
            {
                removed.RequestCancellation();
                _progress.UnregisterAttachment(callerId);
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
        if (_lifetimeCts is not null && !_lifetimeCts.IsCancellationRequested)
            return;

        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(appLifetime);
    }

    /// <summary>
    /// Stop the machine gracefully. Cancels any in-progress cycle and completes
    /// all attachments with <see cref="OperationCanceledException"/>.
    /// </summary>
    public void Stop()
    {
        _lifetimeCts?.Cancel();

        foreach (var (callerId, attachment) in _attachments)
        {
            attachment.TryCancel();
            _progress.UnregisterAttachment(callerId);
        }

        _attachments.Clear();
    }

    // ─── Cycle orchestration ────────────────────────────────

    private void EnsureCycleRunning()
    {
        lock (_lock)
        {
            if (_currentCycleTask is not null && !_currentCycleTask.IsCompleted)
            {
                if (!TryRecoverStaleCycleLocked())
                    return; // Already running — new attachment will be picked up
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCts?.Token ?? CancellationToken.None);
            _cycleCts = cts;
            var generation = ++_cycleGeneration;
            MarkCycleProgress();

            _currentCycleTask = Task.Run(() => RunCycleLoopAsync(generation, cts.Token));
        }
    }

    internal static bool ShouldRestartStaleCycle(
        bool hasActiveCycle,
        bool hasPendingAttachments,
        int activeSongWorkers,
        DateTime lastProgressUtc,
        DateTime nowUtc,
        int staleSeconds)
    {
        if (!hasActiveCycle || !hasPendingAttachments || activeSongWorkers > 0 || staleSeconds <= 0)
            return false;

        return nowUtc - lastProgressUtc >= TimeSpan.FromSeconds(staleSeconds);
    }

    private bool TryRecoverStaleCycleLocked()
    {
        var now = DateTime.UtcNow;
        var staleSeconds = _options?.Value.SongMachineStaleCycleSeconds ?? 180;
        var hasPendingAttachments = _attachments.Values.Any(static attachment => !attachment.IsCompleted);
        var lastProgressUtc = new DateTime(Interlocked.Read(ref _lastCycleProgressTicks), DateTimeKind.Utc);
        if (!ShouldRestartStaleCycle(
                hasActiveCycle: true,
                hasPendingAttachments,
                Volatile.Read(ref _activeSongWorkers),
                lastProgressUtc,
                now,
                staleSeconds))
        {
            return false;
        }

        _log.LogWarning(
            "Restarting stale CyclicalSongMachine cycle: pendingAttachments={PendingAttachments}, activeSongWorkers={ActiveSongWorkers}, lastProgressUtc={LastProgressUtc:o}, staleSeconds={StaleSeconds}.",
            _attachments.Count,
            Volatile.Read(ref _activeSongWorkers),
            lastProgressUtc,
            staleSeconds);

        _cycleCts?.Cancel();
        _cycleCts?.Dispose();
        _cycleCts = null;
        _currentCycleTask = null;
        _cycleSongIndex = -1;
        _cycleSongList = null;
        _cycleSeasonWindows = null;
        Interlocked.Exchange(ref _activeSongWorkers, 0);
        return true;
    }

    private void MarkCycleProgress()
        => Interlocked.Exchange(ref _lastCycleProgressTicks, DateTime.UtcNow.Ticks);

    /// <summary>
    /// The main cycle loop. Runs until all attachments are satisfied and no new ones arrive.
    /// </summary>
    private async Task RunCycleLoopAsync(long generation, CancellationToken ct)
    {
        Exception? cycleFailure = null;
        var cycleCancelled = false;

        try
        {
            while (!ct.IsCancellationRequested && _attachments.Count > 0)
            {
                await RunOneCycleAsync(ct);
                MarkCycleProgress();

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
            cycleCancelled = true;
            _log.LogInformation("CyclicalSongMachine cycle cancelled.");
        }
        catch (Exception ex)
        {
            cycleFailure = ex;
            _log.LogError(ex, "CyclicalSongMachine cycle failed unexpectedly.");
        }
        finally
        {
            var staleGeneration = false;
            lock (_lock)
            {
                if (generation != _cycleGeneration)
                {
                    _log.LogInformation(
                        "Ignoring stale CyclicalSongMachine cycle finalizer for generation {Generation}; current generation is {CurrentGeneration}.",
                        generation,
                        _cycleGeneration);
                    staleGeneration = true;
                }
            }

            if (!staleGeneration)
            {
                var clearProgressWhenIdle = ShouldClearProgressWhenIdle(_progress.Phase, _attachments.Values);

                _cycleSongIndex = -1;
                _cycleSongList = null;
                _cycleSeasonWindows = null;

                if (cycleFailure is not null)
                    FaultRemainingAttachments(cycleFailure);
                else if (cycleCancelled)
                    CancelRemainingAttachments();
                else
                    CompleteFinishedAttachments();

                if (clearProgressWhenIdle && OwnsProgress)
                    _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.Idle);

                _log.LogInformation("CyclicalSongMachine going idle. {Remaining} attachments remain.",
                    _attachments.Count);
            }
        }
    }

    /// <summary>
    /// Run one cycle in two passes:
    /// <list type="number">
    ///   <item><b>Core pass</b> — alltime + current season for ALL users (fast).</item>
    ///   <item><b>Historical pass</b> — remaining seasons for backfill users only
    ///         (slow, skipped when nobody needs it).</item>
    /// </list>
    /// Core-only attachments (post-scrape) complete after the core pass without
    /// waiting for the heavy historical work.
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
        MarkCycleProgress();

        // ── Stamp join index on attachments that haven't been stamped yet ──
        StampJoinIndices(startIndex: 0);

        var seasonLookupIdMap = new Dictionary<int, string>();
        foreach (var w in seasonWindows)
            seasonLookupIdMap[w.SeasonNumber] = HistoryReconstructor.GetSeasonLookupId(w);

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
        int currentSeason = ResolveCurrentSeason(
            seasonWindows,
            _attachments.Values,
            _persistence.GetMaxSeasonAcrossInstruments());

        // ═══════════════════════════════════════════════════════
        // CORE PASS — alltime + current season for ALL users
        // ═══════════════════════════════════════════════════════
        var coreSongs = DetermineSongsToProcess(songList);

        // Season prefix map limited to current season only
        var coreSeasonLookupIdMap = new Dictionary<int, string>();
        if (seasonLookupIdMap.TryGetValue(currentSeason, out var currentLookupId))
            coreSeasonLookupIdMap[currentSeason] = currentLookupId;

        if (OwnsProgress)
        {
            _progress.SetAdaptiveLimiter(_pool.Limiter);
            _progress.BeginPhaseProgress(coreSongs.Count);
            _progress.SetPhaseAccounts(GetTotalUserCount());
        }

        _log.LogInformation(
            "CyclicalSongMachine core pass: {Songs} songs, {Attachments} attachments, {Users} users, season={Season}.",
            coreSongs.Count, _attachments.Count, GetTotalUserCount(), currentSeason);

        await RunSongPassAsync(
            coreSongs, instruments,
            songId => GatherCoreUsersForSong(songId, currentSeason),
            coreSeasonLookupIdMap, accessToken, callerAccountId, opts,
            reportScopeCompletion: true, ct);
        MarkCycleProgress();

        // Flush backfill summary counters so API shows progress mid-cycle
        FlushBackfillSummaryCounters();

        // Mark core pass complete and release core-only attachments
        foreach (var (_, att) in _attachments)
        {
            if (att.IsCompleted) continue;
            att.MarkCyclePassComplete();
        }
        CompleteCoreOnlyAttachments(currentSeason);

        // ═══════════════════════════════════════════════════════
        // HISTORICAL PASS — remaining seasons for backfill users
        // ═══════════════════════════════════════════════════════
        bool anyNeedHistorical = false;
        foreach (var (_, att) in _attachments)
        {
            if (att.IsCompleted) continue;
            if (AttachmentNeedsHistorical(att, currentSeason))
            {
                anyNeedHistorical = true;
                break;
            }
        }

        if (anyNeedHistorical && seasonLookupIdMap.Count > 1)
        {
            var historicalSeasonLookupIdMap = new Dictionary<int, string>(seasonLookupIdMap);
            historicalSeasonLookupIdMap.Remove(currentSeason);

            // Historical pass always covers all songs (backfill users need full coverage)
            var historicalSongs = new List<SongCycleEntry>();
            for (int i = 0; i < songList.Count; i++)
                historicalSongs.Add(new SongCycleEntry(songList[i], i));

            int historicalUserCount = GetHistoricalUserCount(currentSeason);

            if (OwnsProgress)
            {
                _progress.AddPhaseItems(historicalSongs.Count);
                _progress.SetPhaseAccounts(historicalUserCount);
            }

            _log.LogInformation(
                "CyclicalSongMachine historical pass: {Songs} songs, {Seasons} seasons, {Users} backfill users.",
                historicalSongs.Count, historicalSeasonLookupIdMap.Count, historicalUserCount);

            await RunSongPassAsync(
                historicalSongs, instruments,
                songId => GatherHistoricalUsersForSong(songId, currentSeason),
                historicalSeasonLookupIdMap, accessToken, callerAccountId, opts,
                reportScopeCompletion: false, ct);
            MarkCycleProgress();

            foreach (var (_, att) in _attachments)
            {
                if (att.IsCompleted) continue;
                att.MarkCyclePassComplete();
            }
        }

        if (OwnsProgress)
            _progress.SetAdaptiveLimiter(null);

        // Final flush of backfill summary counters before completing attachments
        FlushBackfillSummaryCounters();

        CompleteFinishedAttachments();

        _log.LogInformation(
            "CyclicalSongMachine cycle complete. {Remaining} attachments still active.",
            _attachments.Count);
    }

    /// <summary>
    /// Run a song-parallel pass through the given songs, gathering users via the delegate.
    /// Shared between the core pass and the historical pass.
    /// </summary>
    private async Task RunSongPassAsync(
        IReadOnlyList<SongCycleEntry> songsToProcess,
        IReadOnlyList<string> instruments,
        Func<string, SongPassWork> gatherUsers,
        IReadOnlyDictionary<int, string> seasonLookupIdMap,
        string accessToken,
        string callerAccountId,
        ScraperOptions opts,
        bool reportScopeCompletion,
        CancellationToken ct)
    {
        int maxConcurrentSongs = opts.SongMachineDop;
        SemaphoreSlim? songGate = maxConcurrentSongs > 0
            ? new SemaphoreSlim(maxConcurrentSongs, maxConcurrentSongs)
            : null;
        using var passCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var passCt = passCts.Token;
        ExceptionDispatchInfo? fatalCdnBlock = null;

        try
        {
            // Wire CDN probe callback so probe lifecycle events are pushed
            // to all active syncing users via WebSocket.
            var executor = _inner.Executor;
            if (executor is not null)
            {
                executor.OnCdnProbeEvent = evt =>
                {
                    foreach (var (_, att) in _attachments)
                    {
                        if (att.IsCompleted) continue;
                        foreach (var user in att.Users)
                            _syncTracker.ReportCdnProbe(user.AccountId, evt);
                    }
                };
            }

            var songTasks = songsToProcess.Select(async songEntry =>
            {
                passCt.ThrowIfCancellationRequested();

                if (songGate is not null)
                    await songGate.WaitAsync(passCt);

                try
                {
                    Interlocked.Increment(ref _activeSongWorkers);
                    MarkCycleProgress();
                    var work = gatherUsers(songEntry.SongId);
                    var users = work.Users;
                    var highPriority = work.HighPriority;
                    try
                    {
                        if (users.Count == 0)
                        {
                            if (OwnsProgress)
                                _progress.ReportPhaseItemComplete();
                            return;
                        }

                        var result = await _inner.ProcessSongForUsersAsync(
                            songEntry.SongId, instruments, users, seasonLookupIdMap,
                            accessToken, callerAccountId, _pool, highPriority,
                            opts.LookupBatchSize, work.EpicTrafficKind, passCt,
                            reportScopeCompletion
                                ? CreateScopeCompletionCallback(work.Attachments)
                                : null);

                        // Check CDN throttle state and surface to each user's sync progress.
                        // Throttle when limiter DOP drops below 25% of max.
                        var limiter = _pool.Limiter;
                        bool isThrottled = limiter.ThrottlePercent < 25;
                        foreach (var user in users)
                        {
                            _syncTracker.ReportThrottleState(
                                user.AccountId, isThrottled,
                                isThrottled ? "throttle_cdn_busy" : null);
                        }

                        foreach (var attachment in work.Attachments)
                            attachment.RecordSongResult(songEntry.SongId, result);

                        foreach (var user in users)
                        {
                            if (user.Purposes.HasFlag(WorkPurpose.Backfill))
                            {
                                // Report backfill progress per song (6 instruments checked).
                                // Pairs are deduplicated in the tracker so the historical pass
                                // won't inflate the counter beyond songs × instruments.
                                bool found = result.EntriesUpdated > 0;
                                foreach (var inst in instruments)
                                    _syncTracker.ReportBackfillItem(user.AccountId, songEntry.SongId, inst, found);
                            }

                            if (user.Purposes.HasFlag(WorkPurpose.HistoryRecon))
                            {
                                _syncTracker.ReportHistoryItem(
                                    user.AccountId,
                                    seasonsQueried: seasonLookupIdMap.Count,
                                    entriesFound: result.SessionsInserted);
                            }
                            else if (user.Purposes.HasFlag(WorkPurpose.PostScrape)
                                     && !_syncTracker.IsActiveHigherPriority(user.AccountId))
                            {
                                int units = instruments.Count * ((user.AllTimeNeeded ? 1 : 0) + seasonLookupIdMap.Count);
                                _syncTracker.ReportPostScrapeWork(
                                    user.AccountId,
                                    completedUnits: units,
                                    entriesFound: result.EntriesUpdated);
                            }
                        }

                        // Update attachment user counters from the live sync tracker
                        foreach (var (attCallerId, att) in _attachments)
                        {
                            if (att.IsCompleted) continue;
                            _progress.UpdateAttachmentUserProgress(attCallerId, _syncTracker);
                        }

                        if (OwnsProgress)
                            _progress.ReportPhaseItemComplete();

                        MarkCycleProgress();
                    }
                    finally
                    {
                        foreach (var attachment in work.Attachments)
                            attachment.ReleaseWork();
                    }
                }
                catch (CdnBlockedException ex)
                {
                    if (Interlocked.CompareExchange(
                            ref fatalCdnBlock,
                            ExceptionDispatchInfo.Capture(ex),
                            null) is null)
                    {
                        passCts.Cancel();
                    }

                    throw;
                }
                finally
                {
                    Interlocked.Decrement(ref _activeSongWorkers);
                    songGate?.Release();
                }

                Interlocked.Exchange(ref _cycleSongIndex, songEntry.GlobalIndex);
                StampJoinIndices(startIndex: songEntry.GlobalIndex + 1);
                MarkCycleProgress();

            }).ToList();

            try
            {
                await Task.WhenAll(songTasks);
            }
            catch (Exception) when (fatalCdnBlock is not null)
            {
                fatalCdnBlock.Throw();
            }
        }
        finally
        {
            // Clear CDN probe callback to avoid stale references
            if (_inner.Executor is not null)
                _inner.Executor.OnCdnProbeEvent = null;
            songGate?.Dispose();
        }
    }

    // ─── Song list building ─────────────────────────────────

    /// <summary>
    /// Build a deterministic song list from all attachments' song IDs.
    /// Preferred attachment orders are honored first; remaining songs are sorted.
    /// </summary>
    private List<string> BuildSortedSongList()
        => BuildSongList(_attachments.Values);

    internal static List<string> BuildSongList(IEnumerable<MachineAttachment> attachments)
    {
        var attachmentList = attachments
            .Where(static attachment => !attachment.IsCompleted)
            .OrderBy(static attachment => attachment.AttachmentNumber)
            .ToArray();
        var result = new List<string>();
        var added = new HashSet<string>(StringComparer.Ordinal);

        foreach (var attachment in attachmentList.Where(
                     static attachment => attachment.Options?.PreserveSongOrder == true))
        {
            foreach (var songId in attachment.SongIds)
            {
                if (added.Add(songId))
                    result.Add(songId);
            }
        }

        var remaining = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var attachment in attachmentList)
        {
            foreach (var songId in attachment.SongIds)
            {
                if (!added.Contains(songId))
                    remaining.Add(songId);
            }
        }

        result.AddRange(remaining);
        return result;
    }

    /// <summary>
    /// Determine which songs to process in this cycle pass. On loop-back passes,
    /// process only song IDs still missing for at least one attachment.
    /// </summary>
    private List<SongCycleEntry> DetermineSongsToProcess(IReadOnlyList<string> fullSongList)
    {
        var result = new List<SongCycleEntry>();
        var neededIndices = new HashSet<int>();

        foreach (var (_, att) in _attachments)
        {
            if (att.IsCompleted) continue;

            foreach (int idx in att.GetMissingSongIndices(fullSongList))
                neededIndices.Add(idx);
        }

        foreach (int idx in neededIndices.OrderBy(i => i))
            result.Add(new SongCycleEntry(fullSongList[idx], idx));

        return result;
    }

    // ─── User gathering ─────────────────────────────────────

    /// <summary>
    /// Gather users for the <b>core pass</b> (alltime + current season only).
    /// All users are included, but their <c>SeasonsNeeded</c> is clamped to the current season.
    /// </summary>
    private SongPassWork GatherCoreUsersForSong(string songId, int currentSeason)
    {
        var users = new List<UserWorkItem>();
        var attachments = new List<MachineAttachment>();
        bool highPriority = false;
        var epicTrafficKind = EpicTrafficKind.Background;

        foreach (var (_, att) in _attachments)
        {
            if (att.IsCompleted) continue;
            if (!att.SongIds.Contains(songId)) continue;
            if (!att.TryAcquireWork()) continue;

            attachments.Add(att);

            foreach (var user in att.Users)
            {
                // Clamp to alltime + current season only for the core pass
                var coreSeasons = user.SeasonsNeeded.Contains(currentSeason)
                    ? new HashSet<int> { currentSeason }
                    : new HashSet<int>();

                users.Add(new UserWorkItem
                {
                    AccountId = user.AccountId,
                    Purposes = user.Purposes,
                    AllTimeNeeded = user.AllTimeNeeded,
                    SeasonsNeeded = coreSeasons,
                    AlreadyChecked = user.AlreadyChecked,
                });
            }

            if (att.IsHighPriority) highPriority = true;
            epicTrafficKind = CombineTrafficKind(epicTrafficKind, att.EpicTrafficKind);
        }

        return new SongPassWork(
            DeduplicateUsers(users),
            highPriority,
            epicTrafficKind,
            attachments);
    }

    /// <summary>
    /// Gather users for the <b>historical pass</b> (remaining seasons, no alltime).
    /// Only includes users whose original <c>SeasonsNeeded</c> contains historical seasons.
    /// </summary>
    private SongPassWork GatherHistoricalUsersForSong(string songId, int currentSeason)
    {
        var users = new List<UserWorkItem>();
        var attachments = new List<MachineAttachment>();
        bool highPriority = false;
        var epicTrafficKind = EpicTrafficKind.Background;

        foreach (var (_, att) in _attachments)
        {
            if (att.IsCompleted) continue;
            if (!att.SongIds.Contains(songId)) continue;
            if (!AttachmentNeedsHistorical(att, currentSeason)) continue;

            var attachmentUsers = new List<UserWorkItem>();
            foreach (var user in att.Users)
            {
                var historicalSeasons = new HashSet<int>(user.SeasonsNeeded);
                historicalSeasons.Remove(currentSeason);
                if (historicalSeasons.Count == 0) continue;

                attachmentUsers.Add(new UserWorkItem
                {
                    AccountId = user.AccountId,
                    Purposes = user.Purposes,
                    AllTimeNeeded = false, // Already done in core pass
                    SeasonsNeeded = historicalSeasons,
                    AlreadyChecked = user.AlreadyChecked,
                });
            }

            if (attachmentUsers.Count == 0 || !att.TryAcquireWork())
                continue;

            attachments.Add(att);
            users.AddRange(attachmentUsers);
            if (att.IsHighPriority) highPriority = true;
            epicTrafficKind = CombineTrafficKind(epicTrafficKind, att.EpicTrafficKind);
        }

        return new SongPassWork(
            DeduplicateUsers(users),
            highPriority,
            epicTrafficKind,
            attachments);
    }

    private static EpicTrafficKind CombineTrafficKind(EpicTrafficKind current, EpicTrafficKind next)
        => current == EpicTrafficKind.ForegroundRegistration || next == EpicTrafficKind.ForegroundRegistration
            ? EpicTrafficKind.ForegroundRegistration
            : EpicTrafficKind.Background;

    private static Func<IReadOnlyCollection<SoloCurrentProjectionScopeKey>, ValueTask>? CreateScopeCompletionCallback(
        IReadOnlyList<MachineAttachment> attachments)
    {
        var callbacks = attachments
            .Select(static attachment => attachment.Options?.OnScopesCompleted)
            .Where(static callback => callback is not null)
            .Cast<Func<IReadOnlyCollection<SoloCurrentProjectionScopeKey>, ValueTask>>()
            .ToArray();

        if (callbacks.Length == 0)
            return null;

        return async scopes =>
        {
            foreach (var callback in callbacks)
                await callback(scopes);
        };
    }

    /// <summary>
    /// Deduplicate users by AccountId, merging purposes, alltime requirement, and seasons
    /// so that overlapping PostScrape + Backfill|HistoryRecon work is not dropped.
    /// </summary>
    private static List<UserWorkItem> DeduplicateUsers(List<UserWorkItem> users)
    {
        var merged = new Dictionary<string, UserWorkItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in users)
        {
            if (merged.TryGetValue(user.AccountId, out var existing))
            {
                // Merge: union purposes, OR alltime, union seasons, union already-checked
                var mergedSeasons = new HashSet<int>(existing.SeasonsNeeded);
                mergedSeasons.UnionWith(user.SeasonsNeeded);

                HashSet<(string, string)>? mergedChecked = null;
                if (existing.AlreadyChecked is not null || user.AlreadyChecked is not null)
                {
                    mergedChecked = new HashSet<(string, string)>(existing.AlreadyChecked ?? []);
                    if (user.AlreadyChecked is not null)
                        mergedChecked.UnionWith(user.AlreadyChecked);
                }

                merged[user.AccountId] = new UserWorkItem
                {
                    AccountId = user.AccountId,
                    Purposes = existing.Purposes | user.Purposes,
                    AllTimeNeeded = existing.AllTimeNeeded || user.AllTimeNeeded,
                    SeasonsNeeded = mergedSeasons,
                    AlreadyChecked = mergedChecked,
                };
            }
            else
            {
                merged[user.AccountId] = user;
            }
        }
        return merged.Values.ToList();
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

    private int GetHistoricalUserCount(int currentSeason)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, att) in _attachments)
        {
            if (att.IsCompleted) continue;
            if (!AttachmentNeedsHistorical(att, currentSeason)) continue;
            foreach (var user in att.Users)
            {
                if (user.SeasonsNeeded.Any(s => s != currentSeason))
                    seen.Add(user.AccountId);
            }
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
                CompletePostScrapeUsersForAttachment(att);
                att.Complete();
                _attachments.TryRemove(callerId, out _);
                if (OwnsProgress)
                {
                    _progress.UpdateAttachmentUserProgress(callerId, _syncTracker);
                    for (int i = 0; i < att.Users.Count; i++)
                        _progress.ReportPhaseAccountComplete();
                    _progress.CompleteAttachment(callerId);
                }
                else
                {
                    _progress.UnregisterAttachment(callerId);
                }

                _log.LogInformation(
                    "Attachment {CallerId} completed: {Updated} entries, {Sessions} sessions, {ApiCalls} API calls.",
                    callerId, att.TotalEntriesUpdated, att.TotalSessionsInserted, att.TotalApiCalls);
            }
        }
    }

    /// <summary>
    /// Fault all active attachments after an unexpected cycle failure so callers do not await forever.
    /// </summary>
    private void FaultRemainingAttachments(Exception exception)
    {
        foreach (var (callerId, att) in _attachments)
        {
            att.TryFault(exception);
            _attachments.TryRemove(callerId, out _);
            _progress.UnregisterAttachment(callerId);

            _log.LogWarning(
                exception,
                "Attachment {CallerId} faulted because the CyclicalSongMachine cycle failed.",
                callerId);
        }
    }

    /// <summary>
    /// Cancel all active attachments when the cycle loop is cancelled.
    /// </summary>
    private void CancelRemainingAttachments()
    {
        foreach (var (callerId, att) in _attachments)
        {
            att.TryCancel();
            _attachments.TryRemove(callerId, out _);
            _progress.UnregisterAttachment(callerId);
        }
    }

    /// <summary>
    /// After the core pass, complete attachments whose users only need alltime + current season.
    /// These are typically post-scrape attachments that don't need historical seasons.
    /// </summary>
    private void CompleteCoreOnlyAttachments(int currentSeason)
    {
        foreach (var (callerId, att) in _attachments)
        {
            if (att.IsCompleted) continue;
            if (!att.IsFullyComplete) continue;
            if (AttachmentNeedsHistorical(att, currentSeason)) continue;

            CompletePostScrapeUsersForAttachment(att);
            att.Complete();
            _attachments.TryRemove(callerId, out _);
            if (OwnsProgress)
            {
                _progress.UpdateAttachmentUserProgress(callerId, _syncTracker);
                for (int i = 0; i < att.Users.Count; i++)
                    _progress.ReportPhaseAccountComplete();
                _progress.CompleteAttachment(callerId);
            }
            else
            {
                _progress.UnregisterAttachment(callerId);
            }

            _log.LogInformation(
                "Attachment {CallerId} completed (core-only): {Updated} entries, {Sessions} sessions, {ApiCalls} API calls.",
                callerId, att.TotalEntriesUpdated, att.TotalSessionsInserted, att.TotalApiCalls);
        }
    }

    /// <summary>
    /// Compute total work units for a PostScrape user: per-song, each instrument does
    /// (AllTimeNeeded ? 1 : 0) alltime lookups + SeasonsNeeded.Count seasonal lookups.
    /// </summary>
    private static int ComputePostScrapeWorkUnits(UserWorkItem user, int songCount, int instrumentCount)
    {
        int unitsPerSongInstrument = (user.AllTimeNeeded ? 1 : 0) + user.SeasonsNeeded.Count;
        int total = songCount * instrumentCount * unitsPerSongInstrument;
        if (user.AlreadyChecked is not null)
            total -= user.AlreadyChecked.Count * unitsPerSongInstrument;
        return Math.Max(total, 0);
    }

    /// <summary>
    /// Mark PostScrape-only users as Complete in the sync tracker when an attachment finishes.
    /// Skipped for users that have a higher-priority phase active (Backfill/History/Rivals).
    /// </summary>
    private void CompletePostScrapeUsersForAttachment(MachineAttachment att)
    {
        foreach (var user in att.Users)
        {
            if (!user.Purposes.HasFlag(WorkPurpose.PostScrape)) continue;
            if (_syncTracker.IsActiveHigherPriority(user.AccountId)) continue;

            var p = _syncTracker.GetProgress(user.AccountId);
            if (p is not null && p.Phase == SyncProgressPhase.PostScrape)
                _syncTracker.Complete(user.AccountId);
        }
    }

    /// <summary>Whether an attachment has any user that needs historical seasons (not just current).</summary>
    private static bool AttachmentNeedsHistorical(MachineAttachment att, int currentSeason)
    {
        foreach (var user in att.Users)
        {
            if (user.SeasonsNeeded.Any(s => s != currentSeason))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Flush in-memory backfill progress from <see cref="UserSyncProgressTracker"/> into
    /// the <c>backfill_status</c> summary table so that API consumers see advancing counters.
    /// </summary>
    private void FlushBackfillSummaryCounters()
    {
        foreach (var (_, att) in _attachments)
        {
            if (att.IsCompleted) continue;
            foreach (var user in att.Users)
            {
                if (!user.Purposes.HasFlag(WorkPurpose.Backfill)) continue;
                var p = _syncTracker.GetProgress(user.AccountId);
                if (p is null) continue;
                int checked_ = Volatile.Read(ref p.ItemsCompleted);
                int found = Volatile.Read(ref p.EntriesFound);
                if (checked_ > 0)
                    _persistence.Meta.UpdateBackfillProgress(user.AccountId, checked_, found);
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
        // Merge caller-supplied windows so a newly discovered rollover season from
        // one attachment cannot be hidden by an older concurrent attachment.
        var suppliedWindows = new Dictionary<int, SeasonWindowInfo>();
        foreach (var attachment in _attachments.Values.OrderBy(
                     static attachment => attachment.AttachmentNumber))
        {
            foreach (var window in attachment.SeasonWindows)
            {
                if (!suppliedWindows.TryGetValue(window.SeasonNumber, out var existing)
                    || (string.IsNullOrWhiteSpace(existing.WindowId)
                        && !string.IsNullOrWhiteSpace(window.WindowId)))
                {
                    suppliedWindows[window.SeasonNumber] = window;
                }
            }
        }

        if (suppliedWindows.Count > 0)
            return suppliedWindows.Values.OrderBy(static window => window.SeasonNumber).ToArray();

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

    internal static int ResolveCurrentSeason(
        IReadOnlyList<SeasonWindowInfo> seasonWindows,
        IEnumerable<MachineAttachment> attachments,
        int? instrumentSeasonFallback)
    {
        var declaredSeasons = attachments
            .Where(static attachment => !attachment.IsCompleted)
            .Select(static attachment => attachment.Options?.CurrentSeason)
            .Where(static season => season is > 0)
            .Select(static season => season!.Value)
            .Distinct()
            .ToArray();

        if (declaredSeasons.Length > 1)
        {
            throw new InvalidOperationException(
                $"CyclicalSongMachine attachments disagree on current season: {string.Join(", ", declaredSeasons.Order())}.");
        }

        if (declaredSeasons.Length == 1)
            return declaredSeasons[0];

        if (seasonWindows.Count > 0)
            return seasonWindows.Max(static window => window.SeasonNumber);

        return instrumentSeasonFallback ?? 1;
    }

    // ─── Inner types ────────────────────────────────────────

    private readonly record struct SongCycleEntry(string SongId, int GlobalIndex);

    private readonly record struct SongPassWork(
        List<UserWorkItem> Users,
        bool HighPriority,
        EpicTrafficKind EpicTrafficKind,
        IReadOnlyList<MachineAttachment> Attachments);

    /// <summary>
    /// Represents one caller's attachment to the cyclical machine.
    /// Tracks join point, processed songs, and aggregated results.
    /// </summary>
    internal sealed class MachineAttachment
    {
        public int AttachmentNumber { get; }
        public string CallerId { get; }
        public IReadOnlyList<UserWorkItem> Users { get; }
        public IReadOnlyList<string> SongIds { get; }
        public IReadOnlyList<SeasonWindowInfo> SeasonWindows { get; }
        public SongMachineSource Source { get; }
        public bool IsHighPriority { get; }
        public bool PreserveProgressPhaseOnIdle { get; }
        public EpicTrafficKind EpicTrafficKind { get; }
        public AttachmentOptions? Options { get; }
        public TaskCompletionSource<SongProcessingMachine.MachineResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The song index at which this attachment joined the cycle. -1 = not yet stamped.</summary>
        public int JoinedAtSongIndex { get; private set; } = -1;

        /// <summary>Whether the first pass of the cycle is complete for this attachment.</summary>
        private bool _firstPassComplete;

        /// <summary>Whether the attachment has been completed (TCS set).</summary>
        public bool IsCompleted => Completion.Task.IsCompleted;

        // Aggregated results
        public int TotalEntriesUpdated;
        public int TotalSessionsInserted;
        public int TotalApiCalls;
        public ConcurrentDictionary<SoloCurrentProjectionScopeKey, byte> UpdatedScopes { get; } = [];
        public ConcurrentDictionary<SoloCurrentProjectionScopeKey, byte> CompletedScopes { get; } = [];

        private readonly ConcurrentDictionary<string, byte> _processedSongIds =
            new(StringComparer.Ordinal);
        private readonly CancellationToken _callerCt;
        private readonly HashSet<string> _songIdSet;
        private readonly object _workGate = new();
        private int _inFlightWork;
        private bool _cancellationRequested;

        public MachineAttachment(
            int attachmentNumber,
            string callerId,
            IReadOnlyList<UserWorkItem> users,
            IReadOnlyList<string> songIds,
            IReadOnlyList<SeasonWindowInfo> seasonWindows,
            SongMachineSource source,
            bool isHighPriority,
            bool preserveProgressPhaseOnIdle,
            EpicTrafficKind epicTrafficKind,
            AttachmentOptions? options,
            CancellationToken callerCt)
        {
            AttachmentNumber = attachmentNumber;
            CallerId = callerId;
            Users = users;
            SongIds = songIds;
            SeasonWindows = seasonWindows;
            Source = source;
            IsHighPriority = isHighPriority;
            PreserveProgressPhaseOnIdle = preserveProgressPhaseOnIdle;
            EpicTrafficKind = epicTrafficKind;
            Options = options;
            _callerCt = callerCt;
            _songIdSet = new HashSet<string>(songIds, StringComparer.Ordinal);
        }

        public void StampJoinIndex(int index)
        {
            if (JoinedAtSongIndex < 0)
                JoinedAtSongIndex = index;
        }

        public bool TryAcquireWork()
        {
            lock (_workGate)
            {
                if (_cancellationRequested || IsCompleted)
                    return false;

                _inFlightWork++;
                return true;
            }
        }

        public void ReleaseWork()
        {
            var cancel = false;
            lock (_workGate)
            {
                if (_inFlightWork > 0)
                    _inFlightWork--;
                cancel = _cancellationRequested && _inFlightWork == 0;
            }

            if (cancel)
                TryCancel();
        }

        public void RequestCancellation()
        {
            var cancel = false;
            lock (_workGate)
            {
                _cancellationRequested = true;
                cancel = _inFlightWork == 0;
            }

            if (cancel)
                TryCancel();
        }

        /// <summary>Whether this attachment needs another cycle for missed songs.</summary>
        public bool NeedsLoopBack => _firstPassComplete && !HasProcessedAllSongs;

        /// <summary>Whether this attachment is fully done (all songs processed).</summary>
        public bool IsFullyComplete => IsCompleted || (_firstPassComplete && HasProcessedAllSongs);

        private bool HasProcessedAllSongs =>
            _songIdSet.All(songId => _processedSongIds.ContainsKey(songId));

        /// <summary>
        /// Get the song indices that this attachment still needs processed.
        /// Uses song IDs rather than prior-cycle indices so a preferred ordering may
        /// change between cycles without losing or falsely completing work.
        /// </summary>
        public IEnumerable<int> GetMissingSongIndices(IReadOnlyList<string> fullSongList)
        {
            if (IsCompleted) yield break;

            for (int i = 0; i < fullSongList.Count; i++)
            {
                var songId = fullSongList[i];
                if (_songIdSet.Contains(songId) && !_processedSongIds.ContainsKey(songId))
                    yield return i;
            }
        }

        public void RecordSongResult(string songId, SongProcessingMachine.SongStepResult result)
        {
            if (!_songIdSet.Contains(songId))
                return;

            _processedSongIds.TryAdd(songId, 0);
            Interlocked.Add(ref TotalEntriesUpdated, result.EntriesUpdated);
            Interlocked.Add(ref TotalSessionsInserted, result.SessionsInserted);
            Interlocked.Add(ref TotalApiCalls, result.ApiCalls);
            foreach (var scope in result.UpdatedScopes)
                UpdatedScopes.TryAdd(scope, 0);
            foreach (var scope in result.CompletedScopes)
                CompletedScopes.TryAdd(scope, 0);
        }

        public void MarkCyclePassComplete()
        {
            if (!_firstPassComplete)
                _firstPassComplete = true;
        }

        public void Complete()
        {
            Completion.TrySetResult(new SongProcessingMachine.MachineResult
            {
                EntriesUpdated = TotalEntriesUpdated,
                SessionsInserted = TotalSessionsInserted,
                ApiCalls = TotalApiCalls,
                UsersProcessed = Users.Count,
                UpdatedScopes = UpdatedScopes.Keys.ToArray(),
                CompletedScopes = CompletedScopes.Keys.ToArray(),
            });
        }

        public void TryCancel()
        {
            Completion.TrySetCanceled();
        }

        public void TryFault(Exception exception)
        {
            Completion.TrySetException(exception);
        }
    }

    public sealed record AttachmentOptions(
        bool PreserveSongOrder = false,
        Func<IReadOnlyCollection<SoloCurrentProjectionScopeKey>, ValueTask>? OnScopesCompleted = null,
        int? CurrentSeason = null);
}
