using System.Collections.Concurrent;
using System.Diagnostics;
using FSTService.Api;

namespace FSTService.Scraping;

/// <summary>
/// Per-user, in-memory progress tracker for score sync operations (backfill, history
/// reconstruction, rivals computation). Pushes real-time updates via WebSocket through
/// <see cref="NotificationService"/>, throttled to at most one message per 500 ms per account.
///
/// <para>Independent of <see cref="ScrapeProgressTracker"/> — that singleton tracks global
/// scrape phases and would be clobbered if web-triggered backfills wrote to it.</para>
/// </summary>
public sealed class UserSyncProgressTracker
{
    /// <summary>Max WebSocket push rate per account.</summary>
    private const int ThrottleMs = 500;

    /// <summary>How long after completion/error before auto-cleanup.</summary>
    private static readonly TimeSpan CleanupDelay = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, UserSyncProgress> _progress = new(StringComparer.OrdinalIgnoreCase);
    private readonly NotificationService _notifications;
    private readonly ILogger<UserSyncProgressTracker> _log;

    /// <summary>
    /// Optional provider for next-scrape timing, used to estimate when global ranks
    /// will be recalculated after a user sync completes.
    /// </summary>
    private Func<DateTime?>? _nextRankUpdateProvider;

    public UserSyncProgressTracker(NotificationService notifications, ILogger<UserSyncProgressTracker> log)
    {
        _notifications = notifications;
        _log = log;
    }

    /// <summary>
    /// Register a callback that returns the estimated UTC time of the next global ranking pass.
    /// Called when building the completion payload to populate <c>estimatedRankUpdateMinutes</c>.
    /// </summary>
    public void SetNextRankUpdateProvider(Func<DateTime?> provider) => _nextRankUpdateProvider = provider;

    // ─── Begin phase ────────────────────────────────────────

    public void BeginBackfill(string accountId, int totalItems)
    {
        var p = GetOrCreate(accountId);
        p.Phase = SyncProgressPhase.Backfill;
        p.ItemsCompleted = 0;
        p.TotalItems = totalItems;
        p.EntriesFound = 0;
        p.CurrentSongName = null;
        p.StartedAtUtc = DateTime.UtcNow;
        p.Stopwatch.Restart();
        PushProgress(accountId, p);
    }

    public void BeginHistory(string accountId, int totalItems)
    {
        var p = GetOrCreate(accountId);
        p.Phase = SyncProgressPhase.History;
        p.ItemsCompleted = 0;
        p.TotalItems = totalItems;
        p.EntriesFound = 0;
        p.SeasonsQueried = 0;
        p.CurrentSongName = null;
        p.Stopwatch.Restart();
        PushProgress(accountId, p);
    }

    public void BeginRivals(string accountId, int totalCombos)
    {
        var p = GetOrCreate(accountId);
        p.Phase = SyncProgressPhase.Rivals;
        p.ItemsCompleted = 0;
        p.TotalItems = totalCombos;
        p.RivalsFound = 0;
        p.CurrentSongName = null;
        p.Stopwatch.Restart();
        PushProgress(accountId, p);
    }

    public void BeginPostScrape(string accountId, int totalItems)
    {
        var p = GetOrCreate(accountId);
        p.Phase = SyncProgressPhase.PostScrape;
        p.ItemsCompleted = 0;
        p.TotalItems = totalItems;
        p.EntriesFound = 0;
        p.CurrentSongName = null;
        p.StartedAtUtc = DateTime.UtcNow;
        p.Stopwatch.Restart();
        PushProgress(accountId, p);
    }

    // ─── Report item completion ─────────────────────────────

    public void ReportBackfillItem(string accountId, bool entryFound, string? currentSongName = null)
    {
        if (!_progress.TryGetValue(accountId, out var p)) return;
        Interlocked.Increment(ref p.ItemsCompleted);
        if (entryFound) Interlocked.Increment(ref p.EntriesFound);
        if (currentSongName is not null) p.CurrentSongName = currentSongName;
        PushProgressIfThrottled(accountId, p);
    }

    public void ReportHistoryItem(string accountId, int seasonsQueried, int entriesFound, string? currentSongName = null)
    {
        if (!_progress.TryGetValue(accountId, out var p)) return;
        Interlocked.Increment(ref p.ItemsCompleted);
        Interlocked.Add(ref p.SeasonsQueried, seasonsQueried);
        if (entriesFound > 0) Interlocked.Add(ref p.EntriesFound, entriesFound);
        if (currentSongName is not null) p.CurrentSongName = currentSongName;
        PushProgressIfThrottled(accountId, p);
    }

    public void ReportRivalsItem(string accountId, int combosCompleted, int rivalsFound)
    {
        if (!_progress.TryGetValue(accountId, out var p)) return;
        Volatile.Write(ref p.ItemsCompleted, combosCompleted);
        Volatile.Write(ref p.RivalsFound, rivalsFound);
        PushProgressIfThrottled(accountId, p);
    }

    public void ReportPostScrapeWork(string accountId, int completedUnits, int entriesFound, string? currentSongName = null)
    {
        if (!_progress.TryGetValue(accountId, out var p)) return;
        Interlocked.Add(ref p.ItemsCompleted, completedUnits);
        if (entriesFound > 0) Interlocked.Add(ref p.EntriesFound, entriesFound);
        if (currentSongName is not null) p.CurrentSongName = currentSongName;
        PushProgressIfThrottled(accountId, p);
    }

    // ─── Throttle state ─────────────────────────────────────

    /// <summary>
    /// Report CDN throttle state for an account. Called by <see cref="CyclicalSongMachine"/>
    /// when the adaptive limiter significantly reduces DOP.
    /// </summary>
    public void ReportThrottleState(string accountId, bool isThrottled, string? statusKey = null)
    {
        if (!_progress.TryGetValue(accountId, out var p)) return;
        p.IsThrottled = isThrottled;
        p.ThrottleStatusKey = isThrottled ? statusKey : null;
        PushProgressIfThrottled(accountId, p);
    }

    /// <summary>
    /// Returns true if the account has an active phase that takes precedence over PostScrape
    /// (Backfill, History, or Rivals). Used to avoid clobbering registration sync state.
    /// </summary>
    public bool IsActiveHigherPriority(string accountId)
    {
        if (!_progress.TryGetValue(accountId, out var p)) return false;
        var phase = p.Phase;
        return phase is SyncProgressPhase.Backfill or SyncProgressPhase.History or SyncProgressPhase.Rivals;
    }

    // ─── Terminal states ────────────────────────────────────

    public void Complete(string accountId)
    {
        if (!_progress.TryGetValue(accountId, out var p)) return;
        p.Phase = SyncProgressPhase.Complete;
        PushProgress(accountId, p);
        ScheduleCleanup(accountId);
    }

    public void Error(string accountId, string message)
    {
        if (!_progress.TryGetValue(accountId, out var p)) return;
        p.Phase = SyncProgressPhase.Error;
        p.ErrorMessage = message;
        PushProgress(accountId, p);
        ScheduleCleanup(accountId);
    }

    // ─── Read ───────────────────────────────────────────────

    /// <summary>
    /// Get current in-memory progress for an account, or null if not tracked.
    /// </summary>
    public UserSyncProgress? GetProgress(string accountId)
    {
        return _progress.TryGetValue(accountId, out var p) ? p : null;
    }

    // ─── Internal ───────────────────────────────────────────

    private UserSyncProgress GetOrCreate(string accountId)
    {
        return _progress.GetOrAdd(accountId, _ => new UserSyncProgress());
    }

    /// <summary>Push immediately (used for phase transitions).</summary>
    private void PushProgress(string accountId, UserSyncProgress p)
    {
        p.LastPushTicks = Environment.TickCount64;
        _ = SendAsync(accountId, p);
    }

    /// <summary>Push only if at least <see cref="ThrottleMs"/> ms have elapsed since the last push.</summary>
    private void PushProgressIfThrottled(string accountId, UserSyncProgress p)
    {
        var now = Environment.TickCount64;
        var last = Volatile.Read(ref p.LastPushTicks);
        if (now - last < ThrottleMs) return;

        // CAS to avoid duplicate pushes from parallel tasks
        if (Interlocked.CompareExchange(ref p.LastPushTicks, now, last) != last) return;

        _ = SendAsync(accountId, p);
    }

    private async Task SendAsync(string accountId, UserSyncProgress p)
    {
        try
        {
            await _notifications.NotifySyncProgressAsync(accountId, BuildPayload(accountId, p));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to push sync progress for {AccountId}.", accountId);
        }
    }

    private object BuildPayload(string accountId, UserSyncProgress p)
    {
        var isComplete = p.Phase == SyncProgressPhase.Complete;
        int? estimatedRankMinutes = null;
        if (isComplete)
        {
            var nextUpdate = _nextRankUpdateProvider?.Invoke();
            if (nextUpdate.HasValue)
            {
                var remaining = nextUpdate.Value - DateTime.UtcNow;
                estimatedRankMinutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            }
        }

        return new
        {
            type = "sync_progress",
            accountId,
            phase = p.Phase.ToString().ToLowerInvariant(),
            itemsCompleted = Volatile.Read(ref p.ItemsCompleted),
            totalItems = p.TotalItems,
            entriesFound = Volatile.Read(ref p.EntriesFound),
            currentSongName = p.CurrentSongName,
            seasonsQueried = Volatile.Read(ref p.SeasonsQueried),
            rivalsFound = Volatile.Read(ref p.RivalsFound),
            elapsedSeconds = Math.Round(p.Stopwatch.Elapsed.TotalSeconds, 1),
            isThrottled = p.IsThrottled,
            throttleStatusKey = p.ThrottleStatusKey,
            pendingRankUpdate = isComplete ? true : (bool?)null,
            estimatedRankUpdateMinutes = estimatedRankMinutes,
        };
    }

    private void ScheduleCleanup(string accountId)
    {
        _ = Task.Delay(CleanupDelay).ContinueWith(__ =>
        {
            _progress.TryRemove(accountId, out UserSyncProgress? _);
        });
    }
}

/// <summary>Phase of per-user sync progress.</summary>
public enum SyncProgressPhase
{
    Idle,
    Backfill,
    History,
    Rivals,
    PostScrape,
    Complete,
    Error,
}

/// <summary>
/// Mutable in-memory state for a single user's sync progress.
/// Fields accessed via Interlocked/Volatile from parallel tasks.
/// </summary>
public sealed class UserSyncProgress
{
    public volatile SyncProgressPhase Phase = SyncProgressPhase.Idle;
    public int ItemsCompleted;
    public int TotalItems;
    public int EntriesFound;
    public int SeasonsQueried;
    public int RivalsFound;
    public volatile string? CurrentSongName;
    public volatile string? ErrorMessage;
    public DateTime StartedAtUtc = DateTime.UtcNow;
    public readonly Stopwatch Stopwatch = new();

    /// <summary>Whether the adaptive limiter has significantly reduced DOP (CDN throttle).</summary>
    public volatile bool IsThrottled;

    /// <summary>Status key for throttle reason (e.g. "throttle_cdn_busy"). Frontend translates locally.</summary>
    public volatile string? ThrottleStatusKey;

    /// <summary>Tick count of last WebSocket push (for throttling).</summary>
    public long LastPushTicks;
}
