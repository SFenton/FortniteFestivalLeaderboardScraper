using System.Collections.Concurrent;
using System.Diagnostics;

namespace FSTService.Scraping;

/// <summary>
/// Coordinates deep scrape wave 2 across multiple song/instrument combos using
/// breadth-first page ordering. All page 101s across all combos are processed
/// before any page 102, keeping the DOP saturated while ensuring lower pages
/// (with higher-ranked entries) are captured first.
/// </summary>
public sealed class DeepScrapeCoordinator
{
    private const int ForbiddenThreshold = 3;

    private readonly GlobalLeaderboardScraper _scraper;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger _log;

    public DeepScrapeCoordinator(
        GlobalLeaderboardScraper scraper,
        ScrapeProgressTracker progress,
        ILogger log)
    {
        _scraper = scraper;
        _progress = progress;
        _log = log;
    }

    /// <summary>
    /// Per-combo state for a deep scrape job.
    /// </summary>
    internal sealed class DeepScrapeJob : IDisposable
    {
        // ── Identity ──
        public required string SongId { get; init; }
        public required string Instrument { get; init; }
        public string? Label { get; init; }

        // ── Config ──
        public required int ValidCutoff { get; init; }
        public required int ValidEntryTarget { get; init; }
        public required int ReportedPages { get; init; }
        public required int Wave2Start { get; init; }

        // ── Cursor state ──
        /// <summary>Last consecutive completed page number. Starts at Wave2Start - 1.</summary>
        public int CursorPage { get; set; }
        /// <summary>Total valid entries counted (including wave 1 initial count).</summary>
        public int ValidCount { get; set; }

        // ── Storage ──
        /// <summary>All fetched entries keyed by page number.</summary>
        public ConcurrentDictionary<int, List<LeaderboardEntry>> Entries { get; } = new();

        // ── Pending buffer for out-of-order pages ──
        /// <summary>
        /// Pages completed but not yet consecutive with cursor.
        /// Guarded by <see cref="CursorLock"/>.
        /// </summary>
        public SortedDictionary<int, List<LeaderboardEntry>> PendingPages { get; } = new();

        /// <summary>Lock for cursor advancement (PendingPages + CursorPage + ValidCount).</summary>
        public object CursorLock { get; } = new();

        // ── Boundary / completion ──
        public int Consecutive403s;
        public volatile bool Done;
        public CancellationTokenSource? Cts { get; set; }

        // ── Seeding ──
        /// <summary>Highest page number enqueued into the priority queue.</summary>
        public int LastEnqueuedPage { get; set; }

        // ── Stats ──
        public int RequestCount;
        public long BytesReceived;

        public void Dispose() => Cts?.Dispose();
    }

    /// <summary>
    /// Run coordinated deep scrapes for all provided jobs using breadth-first page ordering.
    /// </summary>
    /// <param name="jobs">Deep scrape jobs built from deferred metadata.</param>
    /// <param name="limiter">Shared concurrency limiter (DOP + optional RPS).</param>
    /// <param name="accessToken">Epic access token.</param>
    /// <param name="accountId">Caller's Epic account ID.</param>
    /// <param name="seedBatch">Pages to seed per job (typically OverThresholdExtraPages).</param>
    /// <param name="onJobComplete">Callback when a job reaches its target or stops.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One result per job with deep scrape entries.</returns>
    internal async Task<List<GlobalLeaderboardResult>> RunAsync(
        List<DeepScrapeJob> jobs,
        AdaptiveConcurrencyLimiter limiter,
        string accessToken,
        string accountId,
        int seedBatch,
        Func<GlobalLeaderboardResult, ValueTask>? onJobComplete,
        CancellationToken ct)
    {
        if (jobs.Count == 0)
            return [];

        var sw = Stopwatch.StartNew();
        _log.LogInformation(
            "Deep scrape coordinator starting: {JobCount} jobs, seed batch size {SeedBatch}.",
            jobs.Count, seedBatch);

        // Priority queue: (jobIndex, pageNumber) ordered by pageNumber.
        // When pages are equal, jobIndex breaks ties (arbitrary but deterministic).
        var queue = new PriorityQueue<(int JobIndex, int Page), (int Page, int JobIndex)>();
        var queueLock = new object();

        int activeJobs = jobs.Count;
        int inFlight = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new GlobalLeaderboardResult[jobs.Count];
        var workAvailable = new SemaphoreSlim(0);

        // Initialize per-job CTS and seed pages into queue
        for (int i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            job.CursorPage = job.Wave2Start - 1;
            job.Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            SeedPages(i, seedBatch);
        }

        int initialQueueCount;
        lock (queueLock) { initialQueueCount = queue.Count; }

        _log.LogInformation(
            "Deep scrape coordinator seeded {TotalPages} pages across {JobCount} jobs.",
            initialQueueCount, jobs.Count);

        // ── Local functions ──

        void SeedPages(int jobIndex, int count)
        {
            var job = jobs[jobIndex];
            int seedStart = job.LastEnqueuedPage < job.Wave2Start
                ? job.Wave2Start
                : job.LastEnqueuedPage + 1;
            int seedEnd = Math.Min(seedStart + count, job.ReportedPages);

            if (seedEnd <= seedStart) return;

            lock (queueLock)
            {
                for (int p = seedStart; p < seedEnd; p++)
                    queue.Enqueue((jobIndex, p), (p, jobIndex));
            }

            job.LastEnqueuedPage = seedEnd - 1;

            // Signal work availability for each new page
            for (int n = 0; n < seedEnd - seedStart; n++)
            {
                try { workAvailable.Release(); } catch (SemaphoreFullException) { }
            }
        }

        bool AdvanceCursor(DeepScrapeJob job, int page, List<LeaderboardEntry> entries)
        {
            lock (job.CursorLock)
            {
                // Buffer this page
                job.PendingPages[page] = entries;

                // Advance cursor through consecutive completed pages
                while (job.PendingPages.Count > 0)
                {
                    int nextExpected = job.CursorPage + 1;
                    if (!job.PendingPages.TryGetValue(nextExpected, out var pageEntries))
                        break;

                    job.PendingPages.Remove(nextExpected);
                    job.CursorPage = nextExpected;
                    job.ValidCount += pageEntries.Count(e => e.Score <= job.ValidCutoff);
                }

                return job.ValidCount >= job.ValidEntryTarget;
            }
        }

        void CompleteJob(int jobIndex, string reason)
        {
            var job = jobs[jobIndex];
            if (job.Done) return;
            job.Done = true;

            try { job.Cts?.Cancel(); } catch { }

            var pagesScraped = job.Entries.Count;
            _log.LogInformation(
                "Deep scrape job completed for {Label} ({Song}/{Instrument}): reason={Reason}, " +
                "{ValidCount:N0}/{Target:N0} valid entries, {Pages:N0} pages scraped.",
                job.Label ?? job.SongId, job.SongId, job.Instrument, reason,
                job.ValidCount, job.ValidEntryTarget, pagesScraped);

            // Build result
            var ordered = job.Entries
                .OrderBy(x => x.Key)
                .SelectMany(x => x.Value)
                .ToList();

            results[jobIndex] = new GlobalLeaderboardResult
            {
                SongId = job.SongId,
                Instrument = job.Instrument,
                Entries = ordered,
                TotalPages = job.Wave2Start + pagesScraped,
                ReportedTotalPages = job.ReportedPages,
                PagesScraped = pagesScraped,
                Requests = job.RequestCount,
                BytesReceived = job.BytesReceived,
            };

            if (Interlocked.Decrement(ref activeJobs) == 0)
            {
                // Wake the consumer loop so it can check completion
                try { workAvailable.Release(); } catch (SemaphoreFullException) { }
            }

            // Fire callback asynchronously
            if (onJobComplete is not null)
            {
                _ = Task.Run(async () =>
                {
                    try { await onJobComplete(results[jobIndex]); }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "onJobComplete callback failed for {Song}/{Instrument}.",
                            job.SongId, job.Instrument);
                    }
                });
            }
        }

        void ExtendSeedIfNeeded(int jobIndex)
        {
            var job = jobs[jobIndex];
            if (job.Done || job.LastEnqueuedPage >= job.ReportedPages - 1) return;

            // Count remaining seeded but unfetched pages
            int remaining = job.LastEnqueuedPage - job.CursorPage;
            if (remaining < seedBatch / 4)
            {
                SeedPages(jobIndex, seedBatch);
            }
        }

        // ── Consumer loop: dequeue work items, acquire DOP slot, fire fetch ──
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await workAvailable.WaitAsync(ct);

                    // Check if all jobs are done
                    if (Volatile.Read(ref activeJobs) == 0)
                    {
                        if (Volatile.Read(ref inFlight) == 0)
                            break;
                        continue;
                    }

                    (int JobIndex, int Page) item;
                    bool hasItem;
                    lock (queueLock)
                    {
                        hasItem = queue.TryDequeue(out item, out _);
                    }

                    if (!hasItem)
                        continue;

                    var job = jobs[item.JobIndex];
                    if (job.Done)
                        continue;

                    // Acquire DOP slot
                    try
                    {
                        await limiter.WaitAsync(job.Cts!.Token);
                    }
                    catch (OperationCanceledException) when (job.Done)
                    {
                        continue;
                    }

                    Interlocked.Increment(ref inFlight);

                    var capturedJobIndex = item.JobIndex;
                    var capturedPage = item.Page;
                    _ = ProcessPageAsync(capturedJobIndex, capturedPage);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Normal shutdown
            }
            finally
            {
                if (!completion.Task.IsCompleted)
                    completion.TrySetResult();
            }
        }, ct);

        async Task ProcessPageAsync(int jobIndex, int page)
        {
            var job = jobs[jobIndex];
            try
            {
                if (job.Cts!.IsCancellationRequested) return;

                var (parsed, bodyLen, status) = await _scraper.FetchPageAsync(
                    job.SongId, job.Instrument, page, accessToken, accountId, limiter, job.Cts.Token);
                Interlocked.Increment(ref job.RequestCount);
                Interlocked.Add(ref job.BytesReceived, bodyLen);
                _progress.ReportPageFetched(bodyLen);

                if (parsed is not null)
                {
                    job.Entries[page] = parsed.Entries;
                    Interlocked.Exchange(ref job.Consecutive403s, 0);

                    bool targetMet = AdvanceCursor(job, page, parsed.Entries);
                    if (targetMet)
                        CompleteJob(jobIndex, "target_met");
                }
                else if (status == GlobalLeaderboardScraper.FetchStatus.Forbidden)
                {
                    var count = Interlocked.Increment(ref job.Consecutive403s);
                    if (count >= ForbiddenThreshold)
                    {
                        _log.LogInformation(
                            "Hit access boundary during coordinated deep scrape for {Label} ({Song}/{Instrument}) at page {Page}.",
                            job.Label ?? job.SongId, job.SongId, job.Instrument, page);
                        CompleteJob(jobIndex, "access_boundary");
                    }
                }
            }
            catch (OperationCanceledException) when (job.Cts!.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Job cancelled — not an error
            }
            finally
            {
                limiter.Release();
                var remaining = Interlocked.Decrement(ref inFlight);

                if (!job.Done)
                    ExtendSeedIfNeeded(jobIndex);

                // Check for overall completion
                if (Volatile.Read(ref activeJobs) == 0 && remaining == 0)
                {
                    bool hasQueuedWork;
                    lock (queueLock) { hasQueuedWork = queue.Count > 0; }
                    if (!hasQueuedWork)
                        completion.TrySetResult();
                }
            }
        }

        await completion.Task;

        // Dispose per-job CTS
        foreach (var job in jobs)
            job.Dispose();

        // Fill in results for any jobs that weren't completed normally
        for (int i = 0; i < jobs.Count; i++)
        {
            if (results[i] is null)
            {
                var job = jobs[i];
                results[i] = new GlobalLeaderboardResult
                {
                    SongId = job.SongId,
                    Instrument = job.Instrument,
                    Entries = [],
                    TotalPages = job.Wave2Start,
                    ReportedTotalPages = job.ReportedPages,
                    PagesScraped = 0,
                    Requests = job.RequestCount,
                    BytesReceived = job.BytesReceived,
                };
            }
        }

        sw.Stop();
        var totalPages = jobs.Sum(j => j.Entries.Count);
        var totalValid = jobs.Sum(j => j.ValidCount);
        _log.LogInformation(
            "Deep scrape coordinator completed: {JobCount} jobs, {TotalPages:N0} pages fetched, " +
            "{TotalValid:N0} total valid entries in {Elapsed}.",
            jobs.Count, totalPages, totalValid, sw.Elapsed);

        workAvailable.Dispose();
        return results.ToList();
    }

    /// <summary>
    /// Build <see cref="DeepScrapeJob"/>s from deferred metadata collected after wave 1.
    /// </summary>
    internal static List<DeepScrapeJob> BuildJobs(
        IEnumerable<DeepScrapeMetadata> metadata,
        int validEntryTarget)
    {
        return metadata.Select(m => new DeepScrapeJob
        {
            SongId = m.SongId,
            Instrument = m.Instrument,
            Label = m.Label,
            ValidCutoff = m.ValidCutoff,
            ValidEntryTarget = validEntryTarget,
            ReportedPages = m.ReportedPages,
            Wave2Start = m.Wave2Start,
            ValidCount = m.InitialValidCount,
        }).ToList();
    }
}
