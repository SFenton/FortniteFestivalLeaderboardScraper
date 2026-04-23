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
        BandScraping,
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

    // ─── Pass history (48h rolling window) ──────────────────

    private static readonly TimeSpan PassRetention = TimeSpan.FromHours(48);
    private readonly List<PassRecord> _passHistory = new();
    private int _passIdCounter;
    private PassRecord? _currentPass;

    // ─── Attachment tracking ────────────────────────────────

    private readonly ConcurrentDictionary<string, AttachmentProgressEntry> _attachments = new(StringComparer.Ordinal);
    private readonly List<AttachmentSummary> _completedAttachments = new();

    /// <summary>Register an attachment so the progress API can report it.</summary>
    public void RegisterAttachment(string callerId, SongMachineSource source, IReadOnlyList<UserWorkItem> users, int songCount)
    {
        var entry = new AttachmentProgressEntry
        {
            CallerId = callerId,
            Source = source,
            SongCount = songCount,
            StartedAtUtc = DateTime.UtcNow,
            Users = users.Select(u => new UserProgressSummary
            {
                AccountId = u.AccountId,
                Phase = u.Purposes.HasFlag(WorkPurpose.HistoryRecon) ? "HistoryRecon"
                      : u.Purposes.HasFlag(WorkPurpose.Backfill) ? "Backfill"
                      : "PostScrape",
            }).ToList(),
        };
        _attachments[callerId] = entry;
        Interlocked.Increment(ref _changeSequence);
    }

    /// <summary>Remove an attachment when it completes or is cancelled.</summary>
    public void UnregisterAttachment(string callerId)
    {
        _attachments.TryRemove(callerId, out _);
        Interlocked.Increment(ref _changeSequence);
    }

    /// <summary>Snapshot a completed attachment so it survives into the phase's completed snapshot, then remove it from active tracking.</summary>
    public void CompleteAttachment(string callerId)
    {
        if (_attachments.TryRemove(callerId, out var entry))
        {
            lock (_completedAttachments)
            {
                _completedAttachments.Add(new AttachmentSummary
                {
                    CallerId = entry.CallerId,
                    Source = entry.Source.ToString(),
                    SongCount = entry.SongCount,
                    StartedAtUtc = entry.StartedAtUtc,
                    Users = entry.Users.Select(u => new UserProgressSummary
                    {
                        AccountId = u.AccountId,
                        Phase = u.Phase,
                        ItemsCompleted = u.ItemsCompleted,
                        TotalItems = u.TotalItems,
                        EntriesFound = u.EntriesFound,
                    }).ToList(),
                });
            }
        }
        Interlocked.Increment(ref _changeSequence);
    }

    /// <summary>Update per-user progress for an attachment from the sync tracker.</summary>
    public void UpdateAttachmentUserProgress(string callerId, UserSyncProgressTracker syncTracker)
    {
        if (!_attachments.TryGetValue(callerId, out var entry)) return;
        foreach (var user in entry.Users)
        {
            var p = syncTracker.GetProgress(user.AccountId);
            if (p is null) continue;
            user.ItemsCompleted = Volatile.Read(ref p.ItemsCompleted);
            user.TotalItems = p.TotalItems;
            user.EntriesFound = Volatile.Read(ref p.EntriesFound);
        }
    }

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

    // ─── Branch tracking (parallel branches inside a phase) ──
    // Each branch is a named, independently-tracked unit of work that runs
    // alongside other branches in the same phase (e.g. rank_recompute,
    // first_seen, name_resolution, pruning during PostScrapeEnrichment).
    // Branches are reset on every SetPhase call. Their snapshot is included
    // in the phase OperationSnapshot and contributes to ProgressPercent.

    private sealed class BranchEntry
    {
        public string Id { get; }
        public string Status; // "pending" | "running" | "complete" | "skipped" | "failed"
        public DateTime? StartedAtUtc;
        public DateTime? CompletedAtUtc;
        public int Completed;
        public int Total;
        public bool HasCounters;
        public string? Message;

        public BranchEntry(string id) { Id = id; Status = "pending"; }
    }

    private readonly List<BranchEntry> _branches = new();

    /// <summary>
    /// Declare the set of branches for the current phase. Clears any prior branches.
    /// Order is preserved in the snapshot. Each branch starts in "pending" status.
    /// </summary>
    public void RegisterBranches(IEnumerable<string> branchIds)
    {
        lock (_branches)
        {
            _branches.Clear();
            foreach (var id in branchIds)
                _branches.Add(new BranchEntry(id));
        }
        Interlocked.Increment(ref _changeSequence);
    }

    /// <summary>Mark a branch as running and record its start time. No-op if branch not registered.</summary>
    public void StartBranch(string branchId)
    {
        lock (_branches)
        {
            var b = _branches.FirstOrDefault(x => x.Id == branchId);
            if (b is null) return;
            b.Status = "running";
            b.StartedAtUtc ??= DateTime.UtcNow;
        }
        Interlocked.Increment(ref _changeSequence);
    }

    /// <summary>Set the total work-item count for a branch, enabling per-branch progress fraction.</summary>
    public void SetBranchTotal(string branchId, int total)
    {
        lock (_branches)
        {
            var b = _branches.FirstOrDefault(x => x.Id == branchId);
            if (b is null) return;
            b.Total = total;
            b.HasCounters = true;
        }
        Interlocked.Increment(ref _changeSequence);
    }

    /// <summary>Set the absolute completed count for a branch (replaces, does not add).</summary>
    public void ReportBranchProgress(string branchId, int completed, int? total = null)
    {
        lock (_branches)
        {
            var b = _branches.FirstOrDefault(x => x.Id == branchId);
            if (b is null) return;
            b.Completed = completed;
            if (total.HasValue) { b.Total = total.Value; }
            b.HasCounters = true;
        }
        Interlocked.Increment(ref _changeSequence);
    }

    /// <summary>Increment the completed count for a branch by the given amount.</summary>
    public void IncrementBranchProgress(string branchId, int by = 1)
    {
        lock (_branches)
        {
            var b = _branches.FirstOrDefault(x => x.Id == branchId);
            if (b is null) return;
            b.Completed += by;
            b.HasCounters = true;
        }
        Interlocked.Increment(ref _changeSequence);
    }

    /// <summary>Mark a branch as terminal. Status should be "complete", "skipped", or "failed".</summary>
    public void CompleteBranch(string branchId, string status = "complete", string? message = null)
    {
        lock (_branches)
        {
            var b = _branches.FirstOrDefault(x => x.Id == branchId);
            if (b is null) return;
            b.Status = status;
            b.CompletedAtUtc = DateTime.UtcNow;
            if (message is not null) b.Message = message;
        }
        Interlocked.Increment(ref _changeSequence);
    }

    private List<BranchProgress>? BuildBranches()
    {
        lock (_branches)
        {
            if (_branches.Count == 0) return null;
            var list = new List<BranchProgress>(_branches.Count);
            foreach (var b in _branches)
            {
                list.Add(new BranchProgress
                {
                    Id = b.Id,
                    Status = b.Status,
                    StartedAtUtc = b.StartedAtUtc,
                    CompletedAtUtc = b.CompletedAtUtc,
                    Completed = b.HasCounters ? b.Completed : null,
                    Total = b.HasCounters ? b.Total : null,
                    Message = b.Message,
                });
            }
            return list;
        }
    }

    /// <summary>
    /// Compute an aggregate progress percent from registered branches.
    /// Terminal branches (complete/skipped/failed) contribute 1.0; running
    /// branches with counters contribute completed/total; running branches
    /// without counters contribute 0.0; pending contributes 0.0.
    /// Returns null when no branches are registered.
    /// </summary>
    private double? ComputeBranchPercent()
    {
        lock (_branches)
        {
            if (_branches.Count == 0) return null;
            double sum = 0;
            foreach (var b in _branches)
            {
                if (b.Status is "complete" or "skipped" or "failed")
                    sum += 1.0;
                else if (b.Status == "running" && b.HasCounters && b.Total > 0)
                    sum += Math.Min(1.0, (double)b.Completed / b.Total);
            }
            return Math.Round(Math.Min(100.0, sum / _branches.Count * 100.0), 1);
        }
    }

    private void ResetBranches()
    {
        lock (_branches) { _branches.Clear(); }
    }

    // ─── Sub-operation detail tracking ──────────────────────

    // Spool flush progress
    private volatile string? _flushingInstrument;
    private int _instrumentsFlushCompleted;
    private int _instrumentsFlushTotal;

    // Index management progress
    private volatile string? _indexOperation;   // "dropping" | "creating" | null
    private volatile string? _currentIndex;
    private int _indexesCompleted;
    private int _indexesTotal;

    // Band fetch progress
    private volatile string? _bandPhase;        // "page0_discovery" | "fetching_pages" | "complete" | null
    private long _bandPagesCompleted;
    private long _bandPagesTotal;
    private int _bandSongsDiscovered;
    private long _bandRetries;

    // Solo vs band completion
    private volatile bool _soloFetchComplete;
    private volatile bool _bandFetchComplete;

    /// <summary>Report spool flush progress for one instrument.</summary>
    public void ReportFlushProgress(string instrument, int completed, int total)
    {
        _flushingInstrument = instrument;
        _instrumentsFlushCompleted = completed;
        _instrumentsFlushTotal = total;
        Interlocked.Increment(ref _changeSequence);
    }

    /// <summary>Report index drop/create progress.</summary>
    public void ReportIndexProgress(string operation, string indexName, int completed, int total)
    {
        _indexOperation = operation;
        _currentIndex = indexName;
        _indexesCompleted = completed;
        _indexesTotal = total;
        Interlocked.Increment(ref _changeSequence);
    }

    /// <summary>Update band fetch progress counters.</summary>
    public void SetBandFetchProgress(string bandPhase, long pagesCompleted, long pagesTotal, int songsDiscovered, long retries)
    {
        _bandPhase = bandPhase;
        Interlocked.Exchange(ref _bandPagesCompleted, pagesCompleted);
        Interlocked.Exchange(ref _bandPagesTotal, pagesTotal);
        _bandSongsDiscovered = songsDiscovered;
        Interlocked.Exchange(ref _bandRetries, retries);
        Interlocked.Increment(ref _changeSequence);
    }

    /// <summary>Mark the solo fetch as complete.</summary>
    public void SetSoloFetchComplete() { _soloFetchComplete = true; Interlocked.Increment(ref _changeSequence); }

    /// <summary>Mark the band fetch as complete.</summary>
    public void SetBandFetchComplete() { _bandFetchComplete = true; Interlocked.Increment(ref _changeSequence); }

    private void ResetSubOperationDetail()
    {
        _flushingInstrument = null;
        _instrumentsFlushCompleted = 0;
        _instrumentsFlushTotal = 0;
        _indexOperation = null;
        _currentIndex = null;
        _indexesCompleted = 0;
        _indexesTotal = 0;
        _bandPhase = null;
        Interlocked.Exchange(ref _bandPagesCompleted, 0);
        Interlocked.Exchange(ref _bandPagesTotal, 0);
        _bandSongsDiscovered = 0;
        Interlocked.Exchange(ref _bandRetries, 0);
        _soloFetchComplete = false;
        _bandFetchComplete = false;
    }

    /// <summary>Build a snapshot of the sub-operation detail, or null if nothing interesting is set.</summary>
    private SubOperationDetail? BuildSubOperationDetail()
    {
        var flushInst = _flushingInstrument;
        var indexOp = _indexOperation;
        var bandPh = _bandPhase;
        var soloComplete = _soloFetchComplete;
        var bandComplete = _bandFetchComplete;

        // Only emit detail when there's something to report
        if (flushInst is null && indexOp is null && bandPh is null && !soloComplete && !bandComplete)
            return null;

        return new SubOperationDetail
        {
            FlushingInstrument = flushInst,
            InstrumentsFlushCompleted = flushInst is not null ? _instrumentsFlushCompleted : null,
            InstrumentsFlushTotal = flushInst is not null ? _instrumentsFlushTotal : null,
            IndexOperation = indexOp,
            IndexesCompleted = indexOp is not null ? _indexesCompleted : null,
            IndexesTotal = indexOp is not null ? _indexesTotal : null,
            CurrentIndex = indexOp is not null ? _currentIndex : null,
            BandPhase = bandPh,
            BandPagesCompleted = bandPh is not null ? Interlocked.Read(ref _bandPagesCompleted) : null,
            BandPagesTotal = bandPh is not null ? Interlocked.Read(ref _bandPagesTotal) : null,
            BandSongsDiscovered = bandPh is not null ? _bandSongsDiscovered : null,
            BandRetries = bandPh is not null ? Interlocked.Read(ref _bandRetries) : null,
            SoloFetchComplete = soloComplete,
            BandFetchComplete = bandComplete,
        };
    }

    // ─── Timing ─────────────────────────────────────────────

    private readonly Stopwatch _phaseStopwatch = new();
    private readonly Stopwatch _passStopwatch = new();
    private DateTime _startedAtUtc;
    private DateTime _phaseStartedAtUtc;

    // ─── Lifecycle ──────────────────────────────────────────

    /// <summary>Begin a new scrape pass. Resets counters for the new pass but preserves pass history.</summary>
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
        ResetSubOperationDetail();
        ResetBranches();
        _startedAtUtc = DateTime.UtcNow;
        _phaseStartedAtUtc = _startedAtUtc;
        _passStopwatch.Restart();
        _phaseStopwatch.Restart();
        _phase = ScrapePhase.Scraping;

        // Create a new pass record (preserving prior passes for 48h window)
        _currentPass = new PassRecord
        {
            PassId = Interlocked.Increment(ref _passIdCounter),
            StartedAtUtc = _startedAtUtc,
            Status = "Running",
        };
        _passHistory.Add(_currentPass);

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
        lock (_completedAttachments) { _completedAttachments.Clear(); }
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
        {
            _completedOperations.Add(currentOp);
            _currentPass?.Operations.Add(currentOp);
        }

        ResetGenericCounters();
        ResetBranches();
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
        {
            _completedOperations.Add(currentOp);
            _currentPass?.Operations.Add(currentOp);
        }

        _passStopwatch.Stop();
        _phaseStopwatch.Stop();
        _subOperation = null;
        _phase = ScrapePhase.Idle;

        // Finalize the current pass record
        if (_currentPass is not null)
        {
            _currentPass.EndedAtUtc = DateTime.UtcNow;
            _currentPass.ElapsedSeconds = Math.Round(_passStopwatch.Elapsed.TotalSeconds, 1);
            _currentPass.Status = "Completed";
            _currentPass = null;
        }

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

        // Evict passes older than 48h
        var cutoff = DateTime.UtcNow - PassRetention;
        _passHistory.RemoveAll(p => p.Status == "Completed" && p.StartedAtUtc < cutoff);

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

        // Build pass history snapshot (update current pass's elapsed time)
        if (_currentPass is not null)
            _currentPass.ElapsedSeconds = Math.Round(_passStopwatch.Elapsed.TotalSeconds, 1);

        var response = new ProgressResponse
        {
            Current = currentOp,
            Running = running,
            CompletedOperations = _completedOperations.ToList(),
            Completed = completed,
            PassElapsedSeconds = Math.Round(_passStopwatch.Elapsed.TotalSeconds, 1),
            PathGeneration = pathGenSnapshot,
            Passes = _passHistory.Select(p => new PassRecord
            {
                PassId = p.PassId,
                StartedAtUtc = p.StartedAtUtc,
                EndedAtUtc = p.EndedAtUtc,
                ElapsedSeconds = p.ElapsedSeconds,
                Status = p.Status,
                Operations = p.Operations.ToList(),
            }).ToList(),
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
            ScrapePhase.SongMachine or
            ScrapePhase.BandScraping => BuildGenericPhaseSnapshot(phase.ToString(), elapsed),
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
            Detail = BuildSubOperationDetail(),
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
        else
        {
            // Fall back to branch-based percent (e.g. Finalizing's checkpoint + cache-warm).
            progressPercent = ComputeBranchPercent();
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
            Detail = BuildSubOperationDetail(),
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
            Attachments = BuildAttachmentsList(),
            Branches = BuildBranches(),
        };
    }

    /// <summary>
    /// Build the merged list of active + completed attachments for the current phase snapshot.
    /// Returns null when there are no attachments at all.
    /// </summary>
    private List<AttachmentSummary>? BuildAttachmentsList()
    {
        var active = _attachments.IsEmpty
            ? []
            : _attachments.Values.Select(a => new AttachmentSummary
            {
                CallerId = a.CallerId,
                Source = a.Source.ToString(),
                SongCount = a.SongCount,
                StartedAtUtc = a.StartedAtUtc,
                Users = a.Users.Select(u => new UserProgressSummary
                {
                    AccountId = u.AccountId,
                    Phase = u.Phase,
                    ItemsCompleted = u.ItemsCompleted,
                    TotalItems = u.TotalItems,
                    EntriesFound = u.EntriesFound,
                }).ToList(),
            }).ToList();

        List<AttachmentSummary> completed;
        lock (_completedAttachments) { completed = _completedAttachments.ToList(); }

        if (active.Count == 0 && completed.Count == 0)
            return null;

        completed.AddRange(active);
        return completed;
    }

    private OperationSnapshot BuildPostScrapeEnrichmentSnapshot(TimeSpan elapsed)
    {
        // Include name resolution sub-progress if available
        var total = _nameResTotal;
        var completed = _nameResCompleted;

        var branchPercent = ComputeBranchPercent();

        TimeSpan? estimatedRemaining = null;
        if (branchPercent is > 0 and < 100)
        {
            var totalEstimatedTime = elapsed / (branchPercent.Value / 100.0);
            estimatedRemaining = totalEstimatedTime - elapsed;
            if (estimatedRemaining < TimeSpan.Zero)
                estimatedRemaining = TimeSpan.Zero;
        }

        return new OperationSnapshot
        {
            Operation = "PostScrapeEnrichment",
            SubOperation = _subOperation,
            StartedAtUtc = _phaseStartedAtUtc,
            ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
            EstimatedRemainingSeconds = estimatedRemaining.HasValue
                ? Math.Round(estimatedRemaining.Value.TotalSeconds, 0) : null,
            ProgressPercent = branchPercent,
            Batches = total > 0 ? new ProgressCounter { Completed = completed, Total = total } : null,
            AccountsResolved = _nameResResolved > 0 ? _nameResResolved : null,
            FailedBatches = _nameResFailed > 0 ? _nameResFailed : null,
            Branches = BuildBranches(),
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

    // ── Pass history (rolling 48h window) ──

    /// <summary>Historical and current pass records. Completed passes older than 48h are evicted.</summary>
    public List<PassRecord> Passes { get; init; } = new();
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

    // ── Sub-operation detail ──
    public SubOperationDetail? Detail { get; init; }

    // ── Attachment progress ──
    public List<AttachmentSummary>? Attachments { get; init; }

    // ── Branch progress (parallel branches inside one phase) ──
    public List<BranchProgress>? Branches { get; init; }
}

/// <summary>
/// Snapshot of a single named branch within a phase. Branches run in parallel
/// (e.g. rank_recompute, first_seen, name_resolution, pruning during
/// PostScrapeEnrichment; final_checkpoint, pre_warming_cache during Finalizing).
/// </summary>
public sealed class BranchProgress
{
    /// <summary>Stable snake_case branch identifier.</summary>
    public string Id { get; init; } = "";
    /// <summary>"pending" | "running" | "complete" | "skipped" | "failed".</summary>
    public string Status { get; init; } = "pending";
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    /// <summary>Items completed so far. Null when the branch does not report counters.</summary>
    public int? Completed { get; init; }
    /// <summary>Total items planned. Null when the branch does not report counters.</summary>
    public int? Total { get; init; }
    /// <summary>Optional human-readable summary (e.g. "12,345 entries updated").</summary>
    public string? Message { get; init; }
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
/// Structured detail about the current sub-operation: flush progress,
/// index management, band fetch status, and solo/band completion.
/// </summary>
public sealed class SubOperationDetail
{
    // Spool flush
    public string? FlushingInstrument { get; init; }
    public int? InstrumentsFlushCompleted { get; init; }
    public int? InstrumentsFlushTotal { get; init; }

    // Index management
    public string? IndexOperation { get; init; }
    public int? IndexesCompleted { get; init; }
    public int? IndexesTotal { get; init; }
    public string? CurrentIndex { get; init; }

    // Band fetch
    public string? BandPhase { get; init; }
    public long? BandPagesCompleted { get; init; }
    public long? BandPagesTotal { get; init; }
    public int? BandSongsDiscovered { get; init; }
    public long? BandRetries { get; init; }

    // Solo vs band completion
    public bool SoloFetchComplete { get; init; }
    public bool BandFetchComplete { get; init; }
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

// ─── Pass & attachment tracking DTOs ────────────────────────

/// <summary>
/// A single scrape pass — may be the current pass or a historical one.
/// Completed passes older than 48h are evicted from the response.
/// </summary>
public sealed class PassRecord
{
    public int PassId { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime? EndedAtUtc { get; set; }
    public double ElapsedSeconds { get; set; }
    public string Status { get; set; } = "Running";
    public List<OperationSnapshot> Operations { get; set; } = new();
}

/// <summary>
/// Internal mutable tracking entry for an active SongMachine attachment.
/// Not serialized directly — projected to <see cref="AttachmentSummary"/> for API output.
/// </summary>
internal sealed class AttachmentProgressEntry
{
    public required string CallerId { get; init; }
    public required SongMachineSource Source { get; init; }
    public required int SongCount { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public required List<UserProgressSummary> Users { get; init; }
}

/// <summary>
/// API-facing snapshot of an active SongMachine attachment.
/// </summary>
public sealed class AttachmentSummary
{
    public string CallerId { get; init; } = "";
    public string Source { get; init; } = "";
    public int SongCount { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public List<UserProgressSummary> Users { get; init; } = new();
}

/// <summary>
/// Per-user progress within an attachment. Mutable internally; projected as init-only for API output.
/// </summary>
public sealed class UserProgressSummary
{
    public string AccountId { get; set; } = "";
    public string Phase { get; set; } = "";
    public int ItemsCompleted { get; set; }
    public int TotalItems { get; set; }
    public int EntriesFound { get; set; }
}
