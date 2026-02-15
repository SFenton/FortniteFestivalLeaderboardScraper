using System.Collections.Concurrent;
using System.Diagnostics;

namespace FSTService.Scraping;

/// <summary>
/// Thread-safe singleton that tracks the live progress of the current scrape pass.
/// Written to by <see cref="ScraperWorker"/>, <see cref="GlobalLeaderboardScraper"/>,
/// and <see cref="AccountNameResolver"/>; read by the <c>/api/progress</c> endpoint.
///
/// The API returns <c>{ current, completedOperations[] }</c> so the caller always
/// sees the active operation first, with finished operations preserved for reference.
/// </summary>
public sealed class ScrapeProgressTracker
{
    // ─── Phase ──────────────────────────────────────────────

    /// <summary>High-level phase of the scrape lifecycle.</summary>
    public enum ScrapePhase
    {
        Idle,
        Initializing,
        Scraping,
        ResolvingNames,
        RebuildingPersonalDbs,
        RefreshingRegisteredUsers,
        BackfillingScores,
        ReconstructingHistory,
    }

    private volatile ScrapePhase _phase = ScrapePhase.Idle;
    public ScrapePhase Phase => _phase;

    // ─── Completed operations history ───────────────────────

    private readonly List<OperationSnapshot> _completedOperations = new();

    // ─── Scraping counters ──────────────────────────────────

    private int _totalLeaderboards;
    private int _completedLeaderboards;
    private int _estimatedTotalPages;
    private int _cachedTotalPages;
    private int _leaderboardsWithKnownPages;
    private int _pagesFetched;
    private long _bytesReceived;
    private int _requestsMade;
    private int _retriesMade;
    private int _totalSongs;
    private int _completedSongs;
    private readonly ConcurrentDictionary<string, int> _completedByInstrument = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _totalByInstrument = new(StringComparer.OrdinalIgnoreCase);

    // ─── Name resolution counters ───────────────────────────

    private int _nameResTotal;
    private int _nameResCompleted;
    private int _nameResResolved;
    private int _nameResFailed;

    // ─── Adaptive concurrency ───────────────────────────────

    private AdaptiveConcurrencyLimiter? _adaptiveLimiter;

    /// <summary>Register the active limiter so the snapshot can report current DOP.</summary>
    public void SetAdaptiveLimiter(AdaptiveConcurrencyLimiter? limiter) => _adaptiveLimiter = limiter;

    // ─── Timing ─────────────────────────────────────────────

    private readonly Stopwatch _phaseStopwatch = new();
    private readonly Stopwatch _passStopwatch = new();
    private DateTime _startedAtUtc;
    private DateTime _phaseStartedAtUtc;

    // ─── Lifecycle ──────────────────────────────────────────

    /// <summary>Begin a new scrape pass. Resets all counters and history.</summary>
    public void BeginPass(int totalLeaderboards, int totalSongs, int cachedTotalPages)
    {
        _totalLeaderboards = totalLeaderboards;
        _totalSongs = totalSongs;
        _cachedTotalPages = cachedTotalPages;
        _completedLeaderboards = 0;
        _completedSongs = 0;
        _estimatedTotalPages = 0;
        _leaderboardsWithKnownPages = 0;
        _pagesFetched = 0;
        _bytesReceived = 0;
        _requestsMade = 0;
        _retriesMade = 0;
        _completedByInstrument.Clear();
        _totalByInstrument.Clear();
        _adaptiveLimiter = null;
        _completedOperations.Clear();
        _nameResTotal = 0;
        _nameResCompleted = 0;
        _nameResResolved = 0;
        _nameResFailed = 0;
        _startedAtUtc = DateTime.UtcNow;
        _phaseStartedAtUtc = _startedAtUtc;
        _passStopwatch.Restart();
        _phaseStopwatch.Restart();
        _phase = ScrapePhase.Scraping;
    }

    // ─── Scraping reporters ─────────────────────────────────

    public void ReportPage0(int totalPagesForLeaderboard)
    {
        Interlocked.Add(ref _estimatedTotalPages, totalPagesForLeaderboard);
        Interlocked.Increment(ref _leaderboardsWithKnownPages);
    }

    public void ReportPageFetched(int bodyLength)
    {
        Interlocked.Increment(ref _pagesFetched);
        Interlocked.Increment(ref _requestsMade);
        Interlocked.Add(ref _bytesReceived, bodyLength);
    }

    public void ReportRetry()
    {
        Interlocked.Increment(ref _retriesMade);
        Interlocked.Increment(ref _requestsMade);
    }

    public void ReportLeaderboardComplete(string instrument)
    {
        Interlocked.Increment(ref _completedLeaderboards);
        _completedByInstrument.AddOrUpdate(instrument, 1, (_, v) => v + 1);
    }

    public void SetInstrumentTotals(IReadOnlyDictionary<string, int> totals)
    {
        foreach (var (instrument, count) in totals)
            _totalByInstrument[instrument] = count;
    }

    public void ReportSongComplete()
    {
        Interlocked.Increment(ref _completedSongs);
    }

    // ─── Name resolution reporters ──────────────────────────

    /// <summary>Set total batches at the start of name resolution.</summary>
    public void BeginNameResolution(int totalBatches, int newAccountCount)
    {
        _nameResTotal = totalBatches;
        _nameResCompleted = 0;
        _nameResResolved = 0;
        _nameResFailed = 0;
    }

    /// <summary>Report one batch completed.</summary>
    public void ReportNameBatchComplete(int resolvedInBatch, bool success)
    {
        Interlocked.Increment(ref _nameResCompleted);
        if (success)
            Interlocked.Add(ref _nameResResolved, resolvedInBatch);
        else
            Interlocked.Increment(ref _nameResFailed);
    }

    // ─── Phase transitions ──────────────────────────────────

    /// <summary>
    /// Transition to a new phase, snapshotting the current operation into history.
    /// </summary>
    public void SetPhase(ScrapePhase phase)
    {
        // Snapshot the finishing operation before switching
        var currentOp = BuildCurrentOperationSnapshot();
        if (currentOp is not null)
            _completedOperations.Add(currentOp);

        _phaseStartedAtUtc = DateTime.UtcNow;
        _phaseStopwatch.Restart();
        _phase = phase;
    }

    /// <summary>Mark the pass as complete and stop the timer.</summary>
    public void EndPass()
    {
        // Snapshot the final operation
        var currentOp = BuildCurrentOperationSnapshot();
        if (currentOp is not null)
            _completedOperations.Add(currentOp);

        _passStopwatch.Stop();
        _phaseStopwatch.Stop();
        _phase = ScrapePhase.Idle;
    }

    // ─── Snapshot for API ───────────────────────────────────

    /// <summary>
    /// Build the full progress response: current operation + completed history.
    /// </summary>
    public ProgressResponse GetProgressResponse()
    {
        return new ProgressResponse
        {
            Current = BuildCurrentOperationSnapshot(),
            CompletedOperations = _completedOperations.ToList(), // defensive copy
            PassElapsedSeconds = Math.Round(_passStopwatch.Elapsed.TotalSeconds, 1),
        };
    }

    /// <summary>Build a snapshot of the currently active operation, or null if idle.</summary>
    private OperationSnapshot? BuildCurrentOperationSnapshot()
    {
        var phase = _phase;
        if (phase == ScrapePhase.Idle) return null;

        var elapsed = _phaseStopwatch.Elapsed;

        return phase switch
        {
            ScrapePhase.Scraping => BuildScrapingSnapshot(elapsed),
            ScrapePhase.ResolvingNames => BuildNameResolutionSnapshot(elapsed),
            ScrapePhase.RebuildingPersonalDbs => new OperationSnapshot
            {
                Operation = "RebuildingPersonalDbs",
                StartedAtUtc = _phaseStartedAtUtc,
                ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
            },
            ScrapePhase.Initializing => new OperationSnapshot
            {
                Operation = "Initializing",
                StartedAtUtc = _phaseStartedAtUtc,
                ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
            },
            _ => null,
        };
    }

    private OperationSnapshot BuildScrapingSnapshot(TimeSpan elapsed)
    {
        var totalLb = _totalLeaderboards;
        var completedLb = _completedLeaderboards;
        var knownPages = _leaderboardsWithKnownPages;
        var discoveredTotal = _estimatedTotalPages;
        var cached = _cachedTotalPages;
        var fetched = _pagesFetched;

        int bestEstimateTotalPages;
        if (knownPages >= totalLb && totalLb > 0)
            bestEstimateTotalPages = discoveredTotal;
        else if (knownPages > 0)
            bestEstimateTotalPages = (int)((double)discoveredTotal / knownPages * totalLb);
        else
            bestEstimateTotalPages = cached > 0 ? cached : totalLb;

        double progressPercent = bestEstimateTotalPages > 0
            ? Math.Min(100.0, (double)fetched / bestEstimateTotalPages * 100.0)
            : 0;

        TimeSpan? estimatedRemaining = null;
        if (fetched > 0 && progressPercent is > 0 and < 100)
        {
            var totalEstimatedTime = elapsed / (progressPercent / 100.0);
            estimatedRemaining = totalEstimatedTime - elapsed;
            if (estimatedRemaining < TimeSpan.Zero)
                estimatedRemaining = TimeSpan.Zero;
        }

        return new OperationSnapshot
        {
            Operation = "Scraping",
            StartedAtUtc = _phaseStartedAtUtc,
            ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
            EstimatedRemainingSeconds = estimatedRemaining.HasValue
                ? Math.Round(estimatedRemaining.Value.TotalSeconds, 0) : null,
            ProgressPercent = Math.Round(progressPercent, 1),
            Songs = new ProgressCounter { Completed = _completedSongs, Total = _totalSongs },
            Leaderboards = new ProgressCounter { Completed = completedLb, Total = totalLb },
            LeaderboardsByInstrument = BuildInstrumentBreakdown(),
            Pages = new PageProgress
            {
                Fetched = fetched,
                EstimatedTotal = bestEstimateTotalPages,
                DiscoveredTotal = discoveredTotal,
                DiscoveryComplete = knownPages >= totalLb && totalLb > 0,
                LeaderboardsDiscovered = knownPages,
            },
            Requests = _requestsMade,
            Retries = _retriesMade,
            BytesReceived = _bytesReceived,
            CurrentDop = _adaptiveLimiter?.CurrentDop,
        };
    }

    private OperationSnapshot BuildNameResolutionSnapshot(TimeSpan elapsed)
    {
        var total = _nameResTotal;
        var completed = _nameResCompleted;
        double progressPercent = total > 0
            ? Math.Min(100.0, (double)completed / total * 100.0)
            : 0;

        TimeSpan? estimatedRemaining = null;
        if (completed > 0 && progressPercent is > 0 and < 100)
        {
            var totalEstimatedTime = elapsed / (progressPercent / 100.0);
            estimatedRemaining = totalEstimatedTime - elapsed;
            if (estimatedRemaining < TimeSpan.Zero)
                estimatedRemaining = TimeSpan.Zero;
        }

        return new OperationSnapshot
        {
            Operation = "ResolvingNames",
            StartedAtUtc = _phaseStartedAtUtc,
            ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
            EstimatedRemainingSeconds = estimatedRemaining.HasValue
                ? Math.Round(estimatedRemaining.Value.TotalSeconds, 0) : null,
            ProgressPercent = Math.Round(progressPercent, 1),
            Batches = new ProgressCounter { Completed = completed, Total = total },
            AccountsResolved = _nameResResolved,
            FailedBatches = _nameResFailed,
        };
    }

    private Dictionary<string, ProgressCounter> BuildInstrumentBreakdown()
    {
        var result = new Dictionary<string, ProgressCounter>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instrument, total) in _totalByInstrument)
        {
            _completedByInstrument.TryGetValue(instrument, out var completed);
            result[instrument] = new ProgressCounter { Completed = completed, Total = total };
        }
        return result;
    }
}

// ─── Snapshot DTOs ──────────────────────────────────────────

/// <summary>Top-level response from /api/progress.</summary>
public sealed class ProgressResponse
{
    /// <summary>Currently active operation, or null if idle.</summary>
    public OperationSnapshot? Current { get; init; }
    /// <summary>Previously completed operations in this pass (oldest first).</summary>
    public List<OperationSnapshot> CompletedOperations { get; init; } = new();
    /// <summary>Total wall-clock seconds since the pass started.</summary>
    public double PassElapsedSeconds { get; init; }
}

/// <summary>
/// Snapshot of a single operation. All fields are nullable so that scraping-specific
/// fields (pages, leaderboards, etc.) don't appear in name-resolution snapshots and vice versa.
/// </summary>
public sealed class OperationSnapshot
{
    public string Operation { get; init; } = "";
    public DateTime? StartedAtUtc { get; init; }
    public double ElapsedSeconds { get; init; }
    public double? EstimatedRemainingSeconds { get; init; }
    public double? ProgressPercent { get; init; }

    // ── Scraping-specific ──
    public ProgressCounter? Songs { get; init; }
    public ProgressCounter? Leaderboards { get; init; }
    public Dictionary<string, ProgressCounter>? LeaderboardsByInstrument { get; init; }
    public PageProgress? Pages { get; init; }
    public int? Requests { get; init; }
    public int? Retries { get; init; }
    public long? BytesReceived { get; init; }
    public int? CurrentDop { get; init; }

    // ── Name resolution-specific ──
    public ProgressCounter? Batches { get; init; }
    public int? AccountsResolved { get; init; }
    public int? FailedBatches { get; init; }
}

public sealed class ProgressCounter
{
    public int Completed { get; init; }
    public int Total { get; init; }
}

public sealed class PageProgress
{
    public int Fetched { get; init; }
    public int EstimatedTotal { get; init; }
    public int DiscoveredTotal { get; init; }
    public bool DiscoveryComplete { get; init; }
    public int LeaderboardsDiscovered { get; init; }
}
