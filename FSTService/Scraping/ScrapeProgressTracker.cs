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
        PostScrapeEnrichment,
        ComputingRankings,
        CalculatingFirstSeen,
        ResolvingNames,
        RefreshingRegisteredUsers,
        ComputingRivals,
        BackfillingScores,
        ReconstructingHistory,
        SongMachine,
        Precomputing,
        Finalizing,
    }

    private volatile ScrapePhase _phase = ScrapePhase.Idle;
    public ScrapePhase Phase => _phase;

    // ─── Response caching (sequence-number gating) ──────────

    /// <summary>Monotonically increasing counter; bumped on every state mutation.</summary>
    private int _changeSequence;

    /// <summary>Cached response + the sequence at which it was built.</summary>
    private sealed record CachedProgressResponse(ProgressResponse Response, int Sequence);
    private volatile CachedProgressResponse? _cachedResponse;

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

    // ─── Generic phase counters ──────────────────────────────
    // Reusable counters that any phase can opt into. Auto-reset on SetPhase().

    private int _phaseTotal;
    private int _phaseCompleted;
    private int _phaseAccountsTotal;
    private int _phaseAccountsCompleted;
    private int _phaseRequests;
    private int _phaseRetries;
    private int _phaseUpdated;

    // ─── Adaptive concurrency ───────────────────────────────

    private AdaptiveConcurrencyLimiter? _adaptiveLimiter;

    /// <summary>Register the active limiter so the snapshot can report current DOP.</summary>
    public void SetAdaptiveLimiter(AdaptiveConcurrencyLimiter? limiter) { _adaptiveLimiter = limiter; Interlocked.Increment(ref _changeSequence); }

    // ─── Sub-operation tracking ──────────────────────────────

    /// <summary>Stable snake_case ID describing the current sub-step within a phase (e.g. "fetching_leaderboards").</summary>
    private volatile string? _subOperation;

    /// <summary>Set the current sub-operation within the active phase. Pass null to clear.</summary>
    public void SetSubOperation(string? id) { _subOperation = id; Interlocked.Increment(ref _changeSequence); }

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
        ResetGenericCounters();
        _startedAtUtc = DateTime.UtcNow;
        _phaseStartedAtUtc = _startedAtUtc;
        _passStopwatch.Restart();
        _phaseStopwatch.Restart();
        _phase = ScrapePhase.Scraping;
        Interlocked.Increment(ref _changeSequence);
    }

    // ─── Scraping reporters ─────────────────────────────────

    public void ReportPage0(int totalPagesForLeaderboard)
    {
        Interlocked.Add(ref _estimatedTotalPages, totalPagesForLeaderboard);
        Interlocked.Increment(ref _leaderboardsWithKnownPages);
        Interlocked.Increment(ref _changeSequence);
    }

    public void ReportPageFetched(int bodyLength)
    {
        Interlocked.Increment(ref _pagesFetched);
        Interlocked.Increment(ref _requestsMade);
        Interlocked.Add(ref _bytesReceived, bodyLength);
        Interlocked.Increment(ref _changeSequence);
    }

    public void ReportRetry()
    {
        Interlocked.Increment(ref _retriesMade);
        Interlocked.Increment(ref _requestsMade);
        Interlocked.Increment(ref _changeSequence);
    }

    public void ReportLeaderboardComplete(string instrument)
    {
        var completed = Interlocked.Increment(ref _completedLeaderboards);
        _completedByInstrument.AddOrUpdate(instrument, 1, (_, v) => v + 1);

        // When all leaderboards have finished fetching but songs are still
        // being persisted via onSongComplete callbacks, transition the
        // sub-operation so the progress API reflects the actual work.
        if (completed == _totalLeaderboards && _subOperation == "fetching_leaderboards")
            _subOperation = "persisting_scores";

        Interlocked.Increment(ref _changeSequence);
    }

    public void SetInstrumentTotals(IReadOnlyDictionary<string, int> totals)
    {
        foreach (var (instrument, count) in totals)
            _totalByInstrument[instrument] = count;
    }

    public void ReportSongComplete()
    {
        Interlocked.Increment(ref _completedSongs);
        Interlocked.Increment(ref _changeSequence);
    }

    // ─── Generic phase reporters ─────────────────────────────

    /// <summary>Initialize generic phase counters. Call after SetPhase().</summary>
    public void BeginPhaseProgress(int totalItems, int totalAccounts = 0)
    {
        _phaseTotal = totalItems;
        _phaseCompleted = 0;
        _phaseAccountsTotal = totalAccounts;
        _phaseAccountsCompleted = 0;
        _phaseRequests = 0;
        _phaseRetries = 0;
        _phaseUpdated = 0;
    }

    /// <summary>Add to the total work item count (for incrementally-discovered work).</summary>
    public void AddPhaseItems(int additional) => Interlocked.Add(ref _phaseTotal, additional);

    /// <summary>Update the total account count mid-phase (e.g. when hot-adding users).</summary>
    public void SetPhaseAccounts(int total) { _phaseAccountsTotal = total; Interlocked.Increment(ref _changeSequence); }

    /// <summary>Report one work item completed.</summary>
    public void ReportPhaseItemComplete() { Interlocked.Increment(ref _phaseCompleted); Interlocked.Increment(ref _changeSequence); }

    /// <summary>Report one account-level unit completed.</summary>
    public void ReportPhaseAccountComplete() { Interlocked.Increment(ref _phaseAccountsCompleted); Interlocked.Increment(ref _changeSequence); }

    /// <summary>Report one HTTP API request made.</summary>
    public void ReportPhaseRequest() { Interlocked.Increment(ref _phaseRequests); Interlocked.Increment(ref _changeSequence); }

    /// <summary>Report one retry attempt.</summary>
    public void ReportPhaseRetry() { Interlocked.Increment(ref _phaseRetries); Interlocked.Increment(ref _changeSequence); }

    /// <summary>Report entries created or updated.</summary>
    public void ReportPhaseEntryUpdated(int count = 1) { Interlocked.Add(ref _phaseUpdated, count); Interlocked.Increment(ref _changeSequence); }

    private void ResetGenericCounters()
    {
        _phaseTotal = 0;
        _phaseCompleted = 0;
        _phaseAccountsTotal = 0;
        _phaseAccountsCompleted = 0;
        _phaseRequests = 0;
        _phaseRetries = 0;
        _phaseUpdated = 0;
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
        Interlocked.Increment(ref _changeSequence);
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

        ResetGenericCounters();
        _subOperation = null;
        _phaseStartedAtUtc = DateTime.UtcNow;
        _phaseStopwatch.Restart();
        _phase = phase;
        Interlocked.Increment(ref _changeSequence);
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
        _subOperation = null;
        _phase = ScrapePhase.Idle;
        Interlocked.Increment(ref _changeSequence);
    }

    // ─── Path generation (runs in parallel with scrape) ─────

    private volatile bool _pathGenRunning;
    private volatile int _pathGenTotal;
    private int _pathGenCompleted;
    private int _pathGenSkipped;
    private int _pathGenFailed;
    private volatile string? _pathGenCurrentSong;
    private DateTime? _pathGenStartedAtUtc;
    private readonly Stopwatch _pathGenStopwatch = new();

    /// <summary>
    /// Start tracking path generation. Returns false if one is already running
    /// (e.g. admin-triggered), in which case the caller should skip its own tracking.
    /// </summary>
    public bool BeginPathGeneration(int totalSongs)
    {
        if (_pathGenRunning)
            return false;

        _pathGenTotal = totalSongs;
        _pathGenCompleted = 0;
        _pathGenSkipped = 0;
        _pathGenFailed = 0;
        _pathGenCurrentSong = null;
        _pathGenStartedAtUtc = DateTime.UtcNow;
        _pathGenStopwatch.Restart();
        _pathGenRunning = true;
        return true;
    }

    public void PathGenProcessing(string songTitle)
    {
        _pathGenCurrentSong = songTitle;
    }

    public void PathGenSongCompleted()
    {
        Interlocked.Increment(ref _pathGenCompleted);
        _pathGenCurrentSong = null;
        Interlocked.Increment(ref _changeSequence);
    }

    public void PathGenSongSkipped()
    {
        Interlocked.Increment(ref _pathGenSkipped);
        Interlocked.Increment(ref _changeSequence);
    }

    public void PathGenSongFailed()
    {
        Interlocked.Increment(ref _pathGenFailed);
        Interlocked.Increment(ref _changeSequence);
    }

    public void EndPathGeneration()
    {
        _pathGenStopwatch.Stop();
        _pathGenRunning = false;
        _pathGenCurrentSong = null;
        // Snapshot the completed run for the completed operations list
        _lastPathGenSnapshot = BuildPathGenerationSnapshot();
    }

    private PathGenerationProgress? _lastPathGenSnapshot;

    // ─── Snapshot for API ───────────────────────────────────

    /// <summary>
    /// Build the full progress response: current operation + completed history.
    /// </summary>
    public ProgressResponse GetProgressResponse()
    {
        var currentSeq = Volatile.Read(ref _changeSequence);
        var cached = _cachedResponse;
        if (cached is not null && cached.Sequence == currentSeq)
            return cached.Response;

        // Build the running operations list (main scrape phase + path gen if active)
        var running = new List<object>();
        var currentOp = BuildCurrentOperationSnapshot();
        if (currentOp is not null)
            running.Add(currentOp);

        var pathGenSnapshot = BuildPathGenerationSnapshot();
        if (pathGenSnapshot is { Running: true })
            running.Add(pathGenSnapshot);

        // Build the completed operations list (scrape phases + last path gen if finished)
        var completed = new List<object>(_completedOperations);
        if (_lastPathGenSnapshot is not null)
            completed.Add(_lastPathGenSnapshot);

        var response = new ProgressResponse
        {
            Current = currentOp,
            Running = running,
            CompletedOperations = _completedOperations.ToList(),
            Completed = completed,
            PassElapsedSeconds = Math.Round(_passStopwatch.Elapsed.TotalSeconds, 1),
            PathGeneration = pathGenSnapshot,
        };

        _cachedResponse = new CachedProgressResponse(response, currentSeq);
        return response;
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
            ScrapePhase.Initializing => new OperationSnapshot
            {
                Operation = "Initializing",
                SubOperation = _subOperation,
                StartedAtUtc = _phaseStartedAtUtc,
                ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
            },
            ScrapePhase.RefreshingRegisteredUsers or
            ScrapePhase.BackfillingScores or
            ScrapePhase.ReconstructingHistory or
            ScrapePhase.CalculatingFirstSeen or
            ScrapePhase.ComputingRankings or
            ScrapePhase.ComputingRivals or
            ScrapePhase.Precomputing or
            ScrapePhase.Finalizing or
            ScrapePhase.SongMachine => BuildGenericPhaseSnapshot(phase.ToString(), elapsed),
            ScrapePhase.PostScrapeEnrichment => BuildPostScrapeEnrichmentSnapshot(elapsed),
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

        double progressPercent = totalLb > 0
            ? Math.Min(100.0, (double)completedLb / totalLb * 100.0)
            : 0;

        TimeSpan? estimatedRemaining = null;
        if (completedLb > 0 && progressPercent is > 0 and < 100)
        {
            var totalEstimatedTime = elapsed / (progressPercent / 100.0);
            estimatedRemaining = totalEstimatedTime - elapsed;
            if (estimatedRemaining < TimeSpan.Zero)
                estimatedRemaining = TimeSpan.Zero;
        }

        return new OperationSnapshot
        {
            Operation = "Scraping",
            SubOperation = _subOperation,
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
            InFlight = _adaptiveLimiter?.InFlight,
            MaxRequestsPerSecond = _adaptiveLimiter?.MaxRequestsPerSecond is > 0 ? _adaptiveLimiter.MaxRequestsPerSecond : null,
            RequestsPerSecond = elapsed.TotalSeconds > 0
                ? Math.Round(_requestsMade / elapsed.TotalSeconds, 1) : null,
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
            SubOperation = _subOperation,
            Batches = new ProgressCounter { Completed = completed, Total = total },
            AccountsResolved = _nameResResolved,
            FailedBatches = _nameResFailed,
        };
    }

    private OperationSnapshot BuildGenericPhaseSnapshot(string operation, TimeSpan elapsed)
    {
        var total = _phaseTotal;
        var completed = _phaseCompleted;
        var accountsTotal = _phaseAccountsTotal;
        var accountsCompleted = _phaseAccountsCompleted;
        var requests = _phaseRequests;
        var retries = _phaseRetries;
        var updated = _phaseUpdated;

        double? progressPercent = null;
        TimeSpan? estimatedRemaining = null;

        if (total > 0)
        {
            progressPercent = Math.Min(100.0, (double)completed / total * 100.0);
        }
        else if (accountsTotal > 0)
        {
            progressPercent = Math.Min(100.0, (double)accountsCompleted / accountsTotal * 100.0);
        }

        if (progressPercent is > 0 and < 100)
        {
            var totalEstimatedTime = elapsed / (progressPercent.Value / 100.0);
            estimatedRemaining = totalEstimatedTime - elapsed;
            if (estimatedRemaining < TimeSpan.Zero)
                estimatedRemaining = TimeSpan.Zero;
        }

        return new OperationSnapshot
        {
            Operation = operation,
            SubOperation = _subOperation,
            StartedAtUtc = _phaseStartedAtUtc,
            ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
            EstimatedRemainingSeconds = estimatedRemaining.HasValue
                ? Math.Round(estimatedRemaining.Value.TotalSeconds, 0) : null,
            ProgressPercent = progressPercent.HasValue ? Math.Round(progressPercent.Value, 1) : null,
            Accounts = accountsTotal > 0 ? new ProgressCounter { Completed = accountsCompleted, Total = accountsTotal } : null,
            WorkItems = total > 0 ? new ProgressCounter { Completed = completed, Total = total } : null,
            Requests = requests > 0 ? requests : null,
            Retries = retries > 0 ? retries : null,
            EntriesUpdated = updated > 0 ? updated : null,
            CurrentDop = _adaptiveLimiter?.CurrentDop,
            InFlight = _adaptiveLimiter?.InFlight,
            MaxRequestsPerSecond = _adaptiveLimiter?.MaxRequestsPerSecond is > 0 ? _adaptiveLimiter.MaxRequestsPerSecond : null,
            RequestsPerSecond = requests > 0 && elapsed.TotalSeconds > 0
                ? Math.Round(requests / elapsed.TotalSeconds, 1) : null,
        };
    }

    private OperationSnapshot BuildPostScrapeEnrichmentSnapshot(TimeSpan elapsed)
    {
        // Include name resolution sub-progress if available
        var total = _nameResTotal;
        var completed = _nameResCompleted;

        return new OperationSnapshot
        {
            Operation = "PostScrapeEnrichment",
            SubOperation = _subOperation,
            StartedAtUtc = _phaseStartedAtUtc,
            ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
            Batches = total > 0 ? new ProgressCounter { Completed = completed, Total = total } : null,
            AccountsResolved = _nameResResolved > 0 ? _nameResResolved : null,
            FailedBatches = _nameResFailed > 0 ? _nameResFailed : null,
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

    private PathGenerationProgress? BuildPathGenerationSnapshot()
    {
        if (!_pathGenRunning && _pathGenStartedAtUtc is null)
            return null; // never started

        var elapsed = _pathGenStopwatch.Elapsed;
        var total = _pathGenTotal;
        var completed = _pathGenCompleted;
        var skipped = _pathGenSkipped;
        var processed = completed + skipped + _pathGenFailed;

        return new PathGenerationProgress
        {
            Running = _pathGenRunning,
            StartedAtUtc = _pathGenStartedAtUtc,
            ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
            TotalSongs = total,
            Completed = completed,
            Skipped = skipped,
            Failed = _pathGenFailed,
            ProgressPercent = total > 0 ? Math.Round((double)processed / total * 100.0, 1) : 0,
            CurrentSong = _pathGenCurrentSong,
        };
    }
}

// ─── Snapshot DTOs ──────────────────────────────────────────

/// <summary>Top-level response from /api/progress.</summary>
public sealed class ProgressResponse
{
    /// <summary>All currently running operations (main scrape phase + parallel path generation).</summary>
    public List<object> Running { get; init; } = new();
    /// <summary>All completed operations this pass, including last path generation run.</summary>
    public List<object> Completed { get; init; } = new();
    /// <summary>Total wall-clock seconds since the pass started.</summary>
    public double PassElapsedSeconds { get; init; }

    // ── Legacy fields (kept for backward compatibility) ──

    /// <summary>Currently active scrape operation, or null if idle.</summary>
    public OperationSnapshot? Current { get; init; }
    /// <summary>Previously completed scrape operations in this pass (oldest first).</summary>
    public List<OperationSnapshot> CompletedOperations { get; init; } = new();
    /// <summary>Path generation progress (runs in parallel with scraping). Null if never started.</summary>
    public PathGenerationProgress? PathGeneration { get; init; }
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

    /// <summary>Stable snake_case ID for the current sub-step within the operation (e.g. "fetching_leaderboards"). Null when not applicable.</summary>
    public string? SubOperation { get; init; }

    // ── Scraping-specific ──
    public ProgressCounter? Songs { get; init; }
    public ProgressCounter? Leaderboards { get; init; }
    public Dictionary<string, ProgressCounter>? LeaderboardsByInstrument { get; init; }
    public PageProgress? Pages { get; init; }
    public int? Requests { get; init; }
    public int? Retries { get; init; }
    public long? BytesReceived { get; init; }
    public int? CurrentDop { get; init; }
    public int? InFlight { get; init; }
    public int? MaxRequestsPerSecond { get; init; }
    public double? RequestsPerSecond { get; init; }

    // ── Name resolution-specific ──
    public ProgressCounter? Batches { get; init; }
    public int? AccountsResolved { get; init; }
    public int? FailedBatches { get; init; }

    // ── Generic phase progress ──
    public ProgressCounter? Accounts { get; init; }
    public ProgressCounter? WorkItems { get; init; }
    public int? EntriesUpdated { get; init; }
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

/// <summary>
/// Progress of the parallel path generation task.
/// </summary>
public sealed class PathGenerationProgress
{
    /// <summary>True if path generation is currently running.</summary>
    public bool Running { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public double ElapsedSeconds { get; init; }
    public int TotalSongs { get; init; }
    /// <summary>Songs that were downloaded, decrypted, and had CHOpt run.</summary>
    public int Completed { get; init; }
    /// <summary>Songs skipped because lastModified or .dat hash was unchanged.</summary>
    public int Skipped { get; init; }
    /// <summary>Songs that failed (download error, decrypt error, CHOpt error).</summary>
    public int Failed { get; init; }
    public double ProgressPercent { get; init; }
    /// <summary>Title of the song currently being processed, or null.</summary>
    public string? CurrentSong { get; init; }
}
