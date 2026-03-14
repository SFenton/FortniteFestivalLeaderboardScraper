using System.Collections.Concurrent;
using System.Diagnostics;

namespace PercentileService;

/// <summary>
/// Thread-safe tracker for percentile scrape progress.
/// Written to by <see cref="PercentileScrapeWorker"/> during scrape;
/// read by the <c>GET /api/progress</c> endpoint.
/// </summary>
public sealed class PercentileScrapeProgressTracker
{
    private volatile bool _isRunning;
    private int _totalEntries;
    private int _completedEntries;
    private int _succeeded;
    private int _failed;
    private int _skipped;
    private DateTime _startedAtUtc;
    private readonly Stopwatch _stopwatch = new();

    // ── Last-run snapshot (persisted after EndScrape) ──
    private PercentileLastRunSummary? _lastRun;

    // ── Failure details (capped) ──
    private const int MaxFailureDetails = 50;
    private ConcurrentQueue<PercentileFailureDetail> _failures = new();

    // ── Adaptive concurrency ──
    private AdaptiveConcurrencyLimiter? _limiter;

    /// <summary>Whether a scrape is currently in progress.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Register the adaptive limiter so the snapshot can report current DOP.</summary>
    public void SetAdaptiveLimiter(AdaptiveConcurrencyLimiter? limiter) => _limiter = limiter;

    /// <summary>Begin a new scrape pass. Resets all counters.</summary>
    public void BeginScrape(int totalEntries)
    {
        _totalEntries = totalEntries;
        _completedEntries = 0;
        _succeeded = 0;
        _failed = 0;
        _skipped = 0;
        _limiter = null;
        _failures = new ConcurrentQueue<PercentileFailureDetail>();
        _startedAtUtc = DateTime.UtcNow;
        _stopwatch.Restart();
        _isRunning = true;
    }

    /// <summary>Report a successful V1 query (returned valid population).</summary>
    public void ReportSuccess()
    {
        Interlocked.Increment(ref _succeeded);
        Interlocked.Increment(ref _completedEntries);
    }

    /// <summary>Report a failed V1 query with details.</summary>
    public void ReportFailed(string? songId = null, string? instrument = null, string? reason = null)
    {
        Interlocked.Increment(ref _failed);
        Interlocked.Increment(ref _completedEntries);

        if (songId is not null && _failures.Count < MaxFailureDetails)
        {
            _failures.Enqueue(new PercentileFailureDetail
            {
                SongId = songId,
                Instrument = instrument,
                Reason = reason,
            });
        }
    }

    /// <summary>Report a skipped V1 query (returned null / no score).</summary>
    public void ReportSkipped()
    {
        Interlocked.Increment(ref _skipped);
        Interlocked.Increment(ref _completedEntries);
    }

    /// <summary>Mark the scrape as complete and snapshot the run summary.</summary>
    public void EndScrape()
    {
        _stopwatch.Stop();

        _lastRun = new PercentileLastRunSummary
        {
            StartedAtUtc = _startedAtUtc,
            CompletedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = Math.Round(_stopwatch.Elapsed.TotalSeconds, 1),
            Entries = new PercentileProgressCounter
            {
                Total = _totalEntries,
                Completed = Volatile.Read(ref _completedEntries),
                Succeeded = Volatile.Read(ref _succeeded),
                Failed = Volatile.Read(ref _failed),
                Skipped = Volatile.Read(ref _skipped),
            },
            Failures = _failures.ToArray(),
        };

        _isRunning = false;
    }

    /// <summary>Build a snapshot of the current scrape progress for the API.</summary>
    public PercentileProgressResponse GetProgressResponse()
    {
        var total = _totalEntries;
        var completed = Volatile.Read(ref _completedEntries);
        var elapsed = _stopwatch.Elapsed;

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

        return new PercentileProgressResponse
        {
            IsRunning = _isRunning,
            StartedAtUtc = _isRunning ? _startedAtUtc : null,
            ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
            EstimatedRemainingSeconds = estimatedRemaining.HasValue
                ? Math.Round(estimatedRemaining.Value.TotalSeconds, 0) : null,
            ProgressPercent = Math.Round(progressPercent, 1),
            Entries = new PercentileProgressCounter
            {
                Total = total,
                Completed = completed,
                Succeeded = Volatile.Read(ref _succeeded),
                Failed = Volatile.Read(ref _failed),
                Skipped = Volatile.Read(ref _skipped),
            },
            CurrentDop = _limiter?.CurrentDop,
            Failures = _isRunning ? _failures.ToArray() : null,
            LastRun = _isRunning ? null : _lastRun,
        };
    }
}

// ─── DTOs ───────────────────────────────────────────────────

/// <summary>Response from <c>GET /api/progress</c>.</summary>
public sealed class PercentileProgressResponse
{
    public bool IsRunning { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public double ElapsedSeconds { get; init; }
    public double? EstimatedRemainingSeconds { get; init; }
    public double? ProgressPercent { get; init; }
    public PercentileProgressCounter? Entries { get; init; }
    public int? CurrentDop { get; init; }
    public PercentileFailureDetail[]? Failures { get; init; }
    public PercentileLastRunSummary? LastRun { get; init; }
}

public sealed class PercentileProgressCounter
{
    public int Total { get; init; }
    public int Completed { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
}

/// <summary>Summary of the last completed scrape run.</summary>
public sealed class PercentileLastRunSummary
{
    public DateTime StartedAtUtc { get; init; }
    public DateTime CompletedAtUtc { get; init; }
    public double ElapsedSeconds { get; init; }
    public PercentileProgressCounter? Entries { get; init; }
    public PercentileFailureDetail[]? Failures { get; init; }
}

/// <summary>Details of a single failed V1 query.</summary>
public sealed class PercentileFailureDetail
{
    public string? SongId { get; init; }
    public string? Instrument { get; init; }
    public string? Reason { get; init; }
}
