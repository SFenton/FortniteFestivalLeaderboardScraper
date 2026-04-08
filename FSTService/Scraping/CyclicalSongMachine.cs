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

    /// <summary>
    /// Whether a caller has set the global progress phase to SongMachine.
    /// When false (e.g. fire-and-forget track backfills), the machine skips
    /// all <see cref="ScrapeProgressTracker"/> writes so it doesn't clobber
    /// the phase that the main scrape loop is reporting.
    /// </summary>
    private bool OwnsProgress => _progress.Phase == ScrapeProgressTracker.ScrapePhase.SongMachine;

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
    /// <returns>Aggregated result for this caller's users when all songs are processed.</returns>
    public virtual Task<SongProcessingMachine.MachineResult> AttachAsync(
        IReadOnlyList<UserWorkItem> users,
        IReadOnlyList<string> songIds,
        IReadOnlyList<SeasonWindowInfo> seasonWindows,
        SongMachineSource source,
        bool isHighPriority,
        CancellationToken ct = default)
    {
        if (users.Count == 0)
            return Task.FromResult(new SongProcessingMachine.MachineResult());

        var callerId = $"attach-{Interlocked.Increment(ref _attachmentCounter)}";
        var attachment = new MachineAttachment(callerId, users, songIds, seasonWindows, source, isHighPriority, ct);

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

        EnsureCycleRunning();

        // When the caller's CT fires, we don't remove the attachment (the cycle handles it)
        // but we do cancel the TCS.
        ct.Register(() =>
        {
            if (_attachments.TryRemove(callerId, out var removed))
            {
                removed.TryCancel();
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
        int currentSeason = seasonWindows.Count > 0
            ? seasonWindows.Max(w => w.SeasonNumber)
            : _persistence.GetMaxSeasonAcrossInstruments() ?? 1;

        // ═══════════════════════════════════════════════════════
        // CORE PASS — alltime + current season for ALL users
        // ═══════════════════════════════════════════════════════
        var coreSongs = DetermineSongsToProcess(songList);

        // Season prefix map limited to current season only
        var coreSeasonPrefixMap = new Dictionary<int, string>();
        if (seasonPrefixMap.TryGetValue(currentSeason, out var curPrefix))
            coreSeasonPrefixMap[currentSeason] = curPrefix;

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
            coreSeasonPrefixMap, accessToken, callerAccountId, opts, ct);

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

        if (anyNeedHistorical && seasonPrefixMap.Count > 1)
        {
            var historicalSeasonPrefixMap = new Dictionary<int, string>(seasonPrefixMap);
            historicalSeasonPrefixMap.Remove(currentSeason);

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
                historicalSongs.Count, historicalSeasonPrefixMap.Count, historicalUserCount);

            await RunSongPassAsync(
                historicalSongs, instruments,
                songId => GatherHistoricalUsersForSong(songId, currentSeason),
                historicalSeasonPrefixMap, accessToken, callerAccountId, opts, ct);

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
        Func<string, (List<UserWorkItem> Users, bool HighPriority)> gatherUsers,
        IReadOnlyDictionary<int, string> seasonPrefixMap,
        string accessToken,
        string callerAccountId,
        ScraperOptions opts,
        CancellationToken ct)
    {
        int maxConcurrentSongs = opts.SongMachineDop;
        SemaphoreSlim? songGate = maxConcurrentSongs > 0
            ? new SemaphoreSlim(maxConcurrentSongs, maxConcurrentSongs)
            : null;

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
                ct.ThrowIfCancellationRequested();

                if (songGate is not null)
                    await songGate.WaitAsync(ct);

                try
                {
                    var (users, highPriority) = gatherUsers(songEntry.SongId);
                    if (users.Count == 0)
                    {
                        if (OwnsProgress)
                            _progress.ReportPhaseItemComplete();
                        return;
                    }

                    var result = await _inner.ProcessSongForUsersAsync(
                        songEntry.SongId, instruments, users, seasonPrefixMap,
                        accessToken, callerAccountId, _pool, highPriority,
                        opts.LookupBatchSize, ct);

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

                    foreach (var (_, att) in _attachments)
                    {
                        if (att.IsCompleted) continue;
                        att.RecordSongResult(songEntry.GlobalIndex, result);
                    }

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
                                seasonsQueried: seasonPrefixMap.Count,
                                entriesFound: result.SessionsInserted);
                        }
                        else if (user.Purposes.HasFlag(WorkPurpose.PostScrape)
                                 && !_syncTracker.IsActiveHigherPriority(user.AccountId))
                        {
                            int units = instruments.Count * ((user.AllTimeNeeded ? 1 : 0) + seasonPrefixMap.Count);
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
                }
                finally
                {
                    songGate?.Release();
                }

                Interlocked.Exchange(ref _cycleSongIndex, songEntry.GlobalIndex);
                StampJoinIndices(startIndex: songEntry.GlobalIndex + 1);

            }).ToList();

            await Task.WhenAll(songTasks);
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
    /// Gather users for the <b>core pass</b> (alltime + current season only).
    /// All users are included, but their <c>SeasonsNeeded</c> is clamped to the current season.
    /// </summary>
    private (List<UserWorkItem> Users, bool HighPriority) GatherCoreUsersForSong(string songId, int currentSeason)
    {
        var users = new List<UserWorkItem>();
        bool highPriority = false;

        foreach (var (_, att) in _attachments)
        {
            if (att.IsCompleted) continue;
            if (!att.SongIds.Contains(songId)) continue;

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
        }

        return (DeduplicateUsers(users), highPriority);
    }

    /// <summary>
    /// Gather users for the <b>historical pass</b> (remaining seasons, no alltime).
    /// Only includes users whose original <c>SeasonsNeeded</c> contains historical seasons.
    /// </summary>
    private (List<UserWorkItem> Users, bool HighPriority) GatherHistoricalUsersForSong(string songId, int currentSeason)
    {
        var users = new List<UserWorkItem>();
        bool highPriority = false;

        foreach (var (_, att) in _attachments)
        {
            if (att.IsCompleted) continue;
            if (!att.SongIds.Contains(songId)) continue;
            if (!AttachmentNeedsHistorical(att, currentSeason)) continue;

            foreach (var user in att.Users)
            {
                var historicalSeasons = new HashSet<int>(user.SeasonsNeeded);
                historicalSeasons.Remove(currentSeason);
                if (historicalSeasons.Count == 0) continue;

                users.Add(new UserWorkItem
                {
                    AccountId = user.AccountId,
                    Purposes = user.Purposes,
                    AllTimeNeeded = false, // Already done in core pass
                    SeasonsNeeded = historicalSeasons,
                    AlreadyChecked = user.AlreadyChecked,
                });
            }

            if (att.IsHighPriority) highPriority = true;
        }

        return (DeduplicateUsers(users), highPriority);
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
        public SongMachineSource Source { get; }
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
            SongMachineSource source,
            bool isHighPriority,
            CancellationToken callerCt)
        {
            CallerId = callerId;
            Users = users;
            SongIds = songIds;
            SeasonWindows = seasonWindows;
            Source = source;
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
