using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

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

    private readonly GlobalLeaderboardScraper? _scraper;
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

    private DeepScrapeCoordinator(
        ScrapeProgressTracker progress,
        ILogger log)
    {
        _progress = progress;
        _log = log;
    }

    internal sealed record DeepScrapeFetchedPage(
        GlobalLeaderboardScraper.ParsedPage? Page,
        int BodyLength,
        GlobalLeaderboardScraper.FetchStatus Status);

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
        public ConcurrentDictionary<int, GlobalLeaderboardScraper.FetchStatus> PageStatuses { get; } = new();

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
        public string? CompletionReason { get; set; }
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
    internal Task<List<GlobalLeaderboardResult>> RunAsync(
        List<DeepScrapeJob> jobs,
        AdaptiveConcurrencyLimiter limiter,
        string accessToken,
        string accountId,
        int seedBatch,
        Func<GlobalLeaderboardResult, ValueTask>? onJobComplete,
        CancellationToken ct,
        ScrapeAccessTokenProvider? accessTokenProvider = null) =>
        RunCoreAsync(
            jobs,
            limiter,
            accessToken,
            accountId,
            seedBatch,
            onJobComplete,
            ct,
            accessTokenProvider,
            pageFetcher: null);

    internal static Task<List<GlobalLeaderboardResult>>
        RunCaptureAsync(
        List<DeepScrapeJob> jobs,
        int seedBatch,
        Func<
            DeepScrapeJob,
            int,
            CancellationToken,
            Task<DeepScrapeFetchedPage>> pageFetcher,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pageFetcher);
        var coordinator = new DeepScrapeCoordinator(
            new ScrapeProgressTracker(),
            NullLogger.Instance);
        return coordinator.RunCoreAsync(
            jobs,
            limiter: null,
            accessToken: "",
            accountId: "",
            seedBatch,
            onJobComplete: null,
            ct,
            accessTokenProvider: null,
            pageFetcher);
    }

    private async Task<List<GlobalLeaderboardResult>> RunCoreAsync(
        List<DeepScrapeJob> jobs,
        AdaptiveConcurrencyLimiter? limiter,
        string accessToken,
        string accountId,
        int seedBatch,
        Func<GlobalLeaderboardResult, ValueTask>? onJobComplete,
        CancellationToken ct,
        ScrapeAccessTokenProvider? accessTokenProvider,
        Func<
            DeepScrapeJob,
            int,
            CancellationToken,
            Task<DeepScrapeFetchedPage>>? pageFetcher)
    {
        if (jobs.Count == 0)
            return [];

        var sw = Stopwatch.StartNew();
        _log.LogInformation(
            "Deep scrape coordinator starting: {JobCount} jobs, seed batch size {SeedBatch}.",
            jobs.Count, seedBatch);

        var results = new GlobalLeaderboardResult?[jobs.Count];
        var callbackTasks = new ConcurrentBag<Task>();

        // Initialize per-job state
        for (int i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            job.CursorPage = job.Wave2Start - 1;
            job.Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        // Build the initial sorted work list: all page requests across all jobs,
        // sorted by page number (breadth-first). Within same page, jobIndex breaks ties.
        var workItems = new List<(int JobIndex, int Page)>();
        for (int i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            int seedEnd = Math.Min(job.Wave2Start + seedBatch, job.ReportedPages);
            for (int p = job.Wave2Start; p < seedEnd; p++)
                workItems.Add((i, p));
            job.LastEnqueuedPage = Math.Max(job.Wave2Start - 1, seedEnd - 1);
        }

        workItems.Sort((a, b) =>
        {
            int cmp = a.Page.CompareTo(b.Page);
            return cmp != 0 ? cmp : a.JobIndex.CompareTo(b.JobIndex);
        });

        _log.LogInformation(
            "Deep scrape coordinator seeded {TotalPages} pages across {JobCount} jobs.",
            workItems.Count, jobs.Count);

        // ── Periodic progress logging ──
        int completedJobCount = 0;
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressTask = Task.Run(async () =>
        {
            try
            {
                while (!progressCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(30_000, progressCts.Token);
                    int done = Volatile.Read(ref completedJobCount);
                    int totalPages = jobs.Sum(j => j.Entries.Count);
                    int totalReqs = jobs.Sum(j => Volatile.Read(ref j.RequestCount));
                    _log.LogInformation(
                        "Deep scrape progress: {Done}/{Total} jobs complete, {Pages:N0} pages fetched, " +
                        "{Requests:N0} requests, DOP={Dop}, elapsed {Elapsed}.",
                        done, jobs.Count, totalPages, totalReqs,
                        limiter?.CurrentDop ?? 0, sw.Elapsed);
                }
            }
            catch (OperationCanceledException) { }
        }, progressCts.Token);

        // ── Local functions ──

        bool AdvanceCursor(DeepScrapeJob job, int page, List<LeaderboardEntry> entries)
        {
            lock (job.CursorLock)
            {
                job.PendingPages[page] = entries;

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
            lock (job.CursorLock)
            {
                if (job.Done)
                    return;
                job.Done = true;
                job.CompletionReason = reason;
            }
            Interlocked.Increment(ref completedJobCount);

            try { job.Cts?.Cancel(); } catch { }

            var pagesScraped = job.Entries.Count;
            _log.LogInformation(
                "Deep scrape job completed for {Label} ({Song}/{Instrument}): reason={Reason}, " +
                "{ValidCount:N0}/{Target:N0} valid entries, {Pages:N0} pages scraped.",
                job.Label ?? job.SongId, job.SongId, job.Instrument, reason,
                job.ValidCount, job.ValidEntryTarget, pagesScraped);

            var ordered = job.Entries
                .OrderBy(x => x.Key)
                .SelectMany(x => x.Value)
                .ToList();
            var expectedLastPage = reason == "target_met"
                ? job.CursorPage
                : job.ReportedPages - 1;
            var terminalBoundaryPage = job.PageStatuses
                .Where(static pair => pair.Value == GlobalLeaderboardScraper.FetchStatus.Forbidden)
                .Select(static pair => pair.Key)
                .DefaultIfEmpty(-1)
                .Min();

            results[jobIndex] = new GlobalLeaderboardResult
            {
                SongId = job.SongId,
                Instrument = job.Instrument,
                Entries = ordered,
                EntriesCount = ordered.Count,
                TotalPages = job.Wave2Start + pagesScraped,
                ReportedTotalPages = job.ReportedPages,
                PagesScraped = pagesScraped,
                Requests = job.RequestCount,
                BytesReceived = job.BytesReceived,
                CompletenessManifest = ScopeCompletenessManifest.Create(
                    job.Wave2Start,
                    expectedLastPage,
                    job.PageStatuses,
                    ordered,
                    job.ReportedPages,
                    reason == "access_boundary"
                        ? ScopeTerminalBoundaryKind.EpicForbidden
                        : ScopeTerminalBoundaryKind.None,
                    terminalBoundaryPage >= 0 ? terminalBoundaryPage : null,
                    job.Wave2Start,
                    job.Entries.Keys.DefaultIfEmpty(job.Wave2Start - 1).Max()),
            };

            // Fire callback asynchronously — tracked so we await before returning
            if (onJobComplete is not null)
            {
                callbackTasks.Add(Task.Run(async () =>
                {
                    await onJobComplete(results[jobIndex]!);
                }));
            }
        }

        // ── Launch all seeded pages as tasks (breadth-first via sorted order) ──
        // Tasks queue up on limiter.WaitAsync(), so launch order = processing order.
        // When a job completes, remaining tasks for that job skip immediately.
        // Extension: when a job's seeded pages are done but target not met,
        // launch more tasks for additional pages.

        var pendingTasks = new List<Task>(workItems.Count);
        var extensionLock = new object();

        Task SchedulePage(int jobIndex, int page)
        {
            var start = new TaskCompletionSource();
            var task = RunTrackedAsync();
            lock (extensionLock)
            {
                pendingTasks.Add(task);
            }
            start.SetResult();
            return task;

            async Task RunTrackedAsync()
            {
                await start.Task;
                await ProcessPageAsync(jobIndex, page);
            }
        }

        try
        {
            foreach (var (jobIndex, page) in workItems)
            {
                _ = SchedulePage(jobIndex, page);
            }

            // Wait for initial batch. Extension tasks are added to pendingTasks as needed.
            while (true)
            {
                Task[] snapshot;
                lock (extensionLock)
                {
                    snapshot = pendingTasks.ToArray();
                }
                await Task.WhenAll(snapshot);

                // Check if any extensions were added after the snapshot
                bool hasNew;
                lock (extensionLock)
                {
                    hasNew =
                        pendingTasks.Count >
                        snapshot.Length;
                }
                if (!hasNew)
                    break;
            }
        }
        catch
        {
            foreach (var job in jobs)
            {
                lock (job.CursorLock)
                {
                    job.Done = true;
                }
                try
                {
                    job.Cts?.Cancel();
                }
                catch
                {
                }
            }
            while (true)
            {
                Task[] drain;
                lock (extensionLock)
                {
                    drain = pendingTasks.ToArray();
                }
                try
                {
                    await Task.WhenAll(drain);
                }
                catch
                {
                }
                lock (extensionLock)
                {
                    if (pendingTasks.Count ==
                        drain.Length)
                    {
                        break;
                    }
                }
            }
            progressCts.Cancel();
            try
            {
                await progressTask;
            }
            catch (OperationCanceledException)
            {
            }
            foreach (var job in jobs)
                job.Dispose();
            throw;
        }

        async Task ProcessPageAsync(int jobIndex, int page)
        {
            var job = jobs[jobIndex];
            if (job.Done) return;

            try
            {
                DeepScrapeFetchedPage fetched;
                if (pageFetcher is not null)
                {
                    fetched = await pageFetcher(
                        job,
                        page,
                        job.Cts!.Token);
                }
                else
                {
                    var scraper = _scraper ??
                        throw new InvalidOperationException(
                            "Deep scrape provider is unavailable.");
                    var activeLimiter = limiter ??
                        throw new InvalidOperationException(
                            "Deep scrape limiter is unavailable.");
                    var result =
                        await scraper.Executor
                            .WithCdnResilienceAsync(
                                work: async () =>
                                {
                                    if (job.Done)
                                    {
                                        return (
                                            (GlobalLeaderboardScraper
                                                .ParsedPage?)null,
                                            0,
                                            GlobalLeaderboardScraper
                                                .FetchStatus
                                                .OtherFailure);
                                    }
                                    return await scraper.FetchPageAsync(
                                        job.SongId,
                                        job.Instrument,
                                        page,
                                        accessToken,
                                        accountId,
                                        activeLimiter,
                                        job.Cts!.Token,
                                        accessTokenProvider);
                                },
                                ct,
                                acquireSlot: () =>
                                    scraper.AcquireEpicSlotAsync(
                                        activeLimiter,
                                        job.Cts!.Token),
                                releaseSlot:
                                    activeLimiter.Release);
                    fetched = new DeepScrapeFetchedPage(
                        result.Item1,
                        result.Item2,
                        result.Item3);
                }
                var parsed = fetched.Page;
                var bodyLen = fetched.BodyLength;
                var status = fetched.Status;

                Interlocked.Increment(ref job.RequestCount);
                Interlocked.Add(ref job.BytesReceived, bodyLen);
                _progress.ReportPageFetched(bodyLen);
                job.PageStatuses[page] = parsed is not null
                    ? GlobalLeaderboardScraper.FetchStatus.Success
                    : status;

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
                // Extend seed if this job needs more pages
                var extStart = -1;
                var extEnd = -1;
                lock (job.CursorLock)
                {
                    if (!job.Done &&
                        job.LastEnqueuedPage <
                            job.ReportedPages - 1)
                    {
                        var remaining =
                            job.LastEnqueuedPage -
                            job.CursorPage;
                        if (remaining <
                            Math.Max(
                                1,
                                seedBatch / 4))
                        {
                            extStart =
                                job.LastEnqueuedPage + 1;
                            extEnd = Math.Min(
                                extStart + seedBatch,
                                job.ReportedPages);
                            job.LastEnqueuedPage =
                                extEnd - 1;
                        }
                    }
                }
                if (extEnd > extStart)
                {
                    for (var nextPage = extStart;
                         nextPage < extEnd;
                         nextPage++)
                    {
                        _ = SchedulePage(
                            jobIndex,
                            nextPage);
                    }
                }
            }
        }

        // Wait for all job-completion callbacks to finish before returning,
        // so callers (e.g. ScrapeOrchestrator) can safely close channels.
        try
        {
            if (!callbackTasks.IsEmpty)
                await Task.WhenAll(callbackTasks);
        }
        finally
        {
            progressCts.Cancel();
            try { await progressTask; } catch (OperationCanceledException) { }

            foreach (var job in jobs)
                job.Dispose();
        }

        // Fill in results for any jobs that weren't completed normally
        for (int i = 0; i < jobs.Count; i++)
        {
            if (results[i] is null)
            {
                var job = jobs[i];
                var ordered = job.Entries
                    .OrderBy(x => x.Key)
                    .SelectMany(x => x.Value)
                    .ToList();
                results[i] = new GlobalLeaderboardResult
                {
                    SongId = job.SongId,
                    Instrument = job.Instrument,
                    Entries = ordered,
                    EntriesCount = ordered.Count,
                    TotalPages = job.Wave2Start + job.Entries.Count,
                    ReportedTotalPages = job.ReportedPages,
                    PagesScraped = job.Entries.Count,
                    Requests = job.RequestCount,
                    BytesReceived = job.BytesReceived,
                    CompletenessManifest = ScopeCompletenessManifest.Create(
                        job.Wave2Start,
                        job.ReportedPages - 1,
                        job.PageStatuses,
                        ordered,
                        job.ReportedPages,
                        ScopeTerminalBoundaryKind.None,
                        deepStartPage: job.Wave2Start,
                        deepEndPage: job.Entries.Keys.DefaultIfEmpty(job.Wave2Start - 1).Max()),
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

        return results.ToList()!;
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
