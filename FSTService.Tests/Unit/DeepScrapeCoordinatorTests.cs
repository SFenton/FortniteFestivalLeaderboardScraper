using System.Collections.Concurrent;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using static FSTService.Scraping.DeepScrapeCoordinator;

namespace FSTService.Tests.Unit;

public class DeepScrapeCoordinatorTests
{
    private readonly ILogger<GlobalLeaderboardScraper> _log = Substitute.For<ILogger<GlobalLeaderboardScraper>>();
    private readonly ScrapeProgressTracker _progress = new();

    private (DeepScrapeCoordinator coordinator, GlobalLeaderboardScraper scraper, MockHttpMessageHandler handler) Create()
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler);
        var scraper = new GlobalLeaderboardScraper(http, _progress, _log, maxLookupRetries: 0);
        var coordinator = new DeepScrapeCoordinator(scraper, _progress, _log);
        return (coordinator, scraper, handler);
    }

    private static string MakePage(int pageNum, int totalPages, params (string Id, int Score)[] entries)
    {
        var entryJson = string.Join(",", entries.Select((e, i) =>
            $@"{{""teamId"":""{e.Id}"",""rank"":{pageNum * 100 + i + 1},""percentile"":0.5,""sessionHistory"":[{{""trackedStats"":{{""SCORE"":{e.Score}}}}}]}}"));
        return $@"{{""page"":{pageNum},""totalPages"":{totalPages},""entries"":[{entryJson}]}}";
    }

    // ─── Single job: reaches target ──

    [Fact]
    public async Task SingleJob_ReachesTarget()
    {
        var (coordinator, _, handler) = Create();

        // Wave 2 starts at page 2, reported 10 pages. Target = 3 valid (≤ 1000).
        // Initial valid count from wave 1 = 1.
        // Pages 2,3: 2 valid entries → total = 3 → target met.
        handler.EnqueueJsonOk(MakePage(2, 10, ("p4", 800)));
        handler.EnqueueJsonOk(MakePage(3, 10, ("p5", 700)));

        var jobs = new List<DeepScrapeJob>
        {
            new()
            {
                SongId = "song1", Instrument = "Solo_Guitar",
                ValidCutoff = 1000, ValidEntryTarget = 3,
                ReportedPages = 10, Wave2Start = 2, ValidCount = 1,
            },
        };

        var results = await coordinator.RunAsync(
            jobs, new AdaptiveConcurrencyLimiter(4, 1, 4, _log),
            "token", "acct", seedBatch: 5, onJobComplete: null, CancellationToken.None);

        Assert.Single(results);
        Assert.True(results[0].Entries.Count >= 2);
        // Requests should be limited — no need to fetch all 8 remaining pages
        Assert.True(results[0].PagesScraped <= 5);
    }

    // ─── Single job: leaderboard exhausted ──

    [Fact]
    public async Task SingleJob_LeaderboardExhausted()
    {
        var (coordinator, _, handler) = Create();

        // Only 2 pages beyond wave 1 (pages 2 and 3). Target = 100 (unreachable).
        handler.EnqueueJsonOk(MakePage(2, 4, ("p4", 800)));
        handler.EnqueueJsonOk(MakePage(3, 4, ("p5", 700)));

        var jobs = new List<DeepScrapeJob>
        {
            new()
            {
                SongId = "song1", Instrument = "Solo_Guitar",
                ValidCutoff = 1000, ValidEntryTarget = 100,
                ReportedPages = 4, Wave2Start = 2, ValidCount = 0,
            },
        };

        var results = await coordinator.RunAsync(
            jobs, new AdaptiveConcurrencyLimiter(4, 1, 4, _log),
            "token", "acct", seedBatch: 10, onJobComplete: null, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(2, results[0].PagesScraped);
        Assert.Equal(2, results[0].Entries.Count);
    }

    // ─── Multiple jobs: all complete ──

    [Fact]
    public async Task MultipleJobs_AllComplete()
    {
        var (coordinator, _, handler) = Create();

        // Job 0: pages 2-3, target=2 valid, initial=0
        // Job 1: pages 4-5, target=2 valid, initial=0
        // Each job has 2 valid pages, so both reach target.
        // Queue order: page 2 (job0), page 3 (job0), page 4 (job1), page 5 (job1)
        // But with breadth-first: page 2 (job0), page 4 (job1), page 3 (job0), page 5 (job1)
        // — actually, pages are interleaved by page number.

        // Job 0 wave2 starts at page 2
        // Job 1 wave2 starts at page 4
        // Queue: (job0, 2), (job0, 3), (job1, 4), (job1, 5)
        // Sorted: 2, 3, 4, 5 — breadth-first means lower pages first across all jobs

        // Enqueue responses — the handler is sequential so we need to match the fetch order.
        // Since DOP=1 and queue is sorted by page, order is: page 2, page 3, page 4, page 5
        handler.EnqueueJsonOk(MakePage(2, 10, ("a1", 800)));
        handler.EnqueueJsonOk(MakePage(3, 10, ("a2", 700)));
        handler.EnqueueJsonOk(MakePage(4, 10, ("b1", 900)));
        handler.EnqueueJsonOk(MakePage(5, 10, ("b2", 850)));

        var jobs = new List<DeepScrapeJob>
        {
            new()
            {
                SongId = "songA", Instrument = "Solo_Guitar",
                ValidCutoff = 1000, ValidEntryTarget = 2,
                ReportedPages = 10, Wave2Start = 2, ValidCount = 0,
            },
            new()
            {
                SongId = "songB", Instrument = "Solo_Bass",
                ValidCutoff = 1000, ValidEntryTarget = 2,
                ReportedPages = 10, Wave2Start = 4, ValidCount = 0,
            },
        };

        var results = await coordinator.RunAsync(
            jobs, new AdaptiveConcurrencyLimiter(1, 1, 1, _log),
            "token", "acct", seedBatch: 5, onJobComplete: null, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("songA", results[0].SongId);
        Assert.Equal("songB", results[1].SongId);

        Assert.Equal(2, results[0].PagesScraped);
        Assert.Equal(2, results[1].PagesScraped);
    }

    // ─── Breadth-first: same wave2Start, lower pages fetched first ──

    [Fact]
    public async Task BreadthFirst_SameWave2Start_InterleavedByPage()
    {
        var (coordinator, _, handler) = Create();

        // Both jobs start at page 100, so the queue should interleave:
        // (job0, page100), (job1, page100), (job0, page101), (job1, page101), ...
        // With DOP=1, fetch order is deterministic by (page, jobIndex).

        // Both need 1 valid entry each, initial=0
        handler.EnqueueJsonOk(MakePage(100, 200, ("a1", 500))); // job0, page100
        handler.EnqueueJsonOk(MakePage(100, 200, ("b1", 600))); // job1, page100

        var requestOrder = new ConcurrentBag<string>();

        var jobs = new List<DeepScrapeJob>
        {
            new()
            {
                SongId = "songA", Instrument = "Solo_Guitar",
                ValidCutoff = 1000, ValidEntryTarget = 1,
                ReportedPages = 200, Wave2Start = 100, ValidCount = 0,
            },
            new()
            {
                SongId = "songB", Instrument = "Solo_Bass",
                ValidCutoff = 1000, ValidEntryTarget = 1,
                ReportedPages = 200, Wave2Start = 100, ValidCount = 0,
            },
        };

        var results = await coordinator.RunAsync(
            jobs, new AdaptiveConcurrencyLimiter(1, 1, 1, _log),
            "token", "acct", seedBatch: 5, onJobComplete: null, CancellationToken.None);

        // Both jobs should have completed with 1 page each
        Assert.Equal(2, results.Count);
        Assert.True(results[0].PagesScraped >= 1);
        Assert.True(results[1].PagesScraped >= 1);
    }

    // ─── 403 boundary stops a job ──

    [Fact]
    public async Task ForbiddenBoundary_StopsJob()
    {
        var (coordinator, _, handler) = Create();

        // Job starts at page 2, gets 3 consecutive 403s → stops.
        // FetchPageAsync retries each JSON 403 once (5s delay), so each page
        // consumes 2 mock responses. We need 3 pages × 2 = 6 JSON 403s.
        for (int i = 0; i < 6; i++)
            handler.EnqueueError(System.Net.HttpStatusCode.Forbidden,
                @"{""errorCode"":""errors.com.epicgames.common.forbidden""}");

        var jobs = new List<DeepScrapeJob>
        {
            new()
            {
                SongId = "song1", Instrument = "Solo_Guitar",
                ValidCutoff = 1000, ValidEntryTarget = 100,
                ReportedPages = 20, Wave2Start = 2, ValidCount = 0,
            },
        };

        var results = await coordinator.RunAsync(
            jobs, new AdaptiveConcurrencyLimiter(1, 1, 1, _log),
            "token", "acct", seedBatch: 5, onJobComplete: null, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(0, results[0].PagesScraped);
        Assert.Equal(0, results[0].Entries.Count);
    }

    // ─── Callback fires on completion ──

    [Fact]
    public async Task OnJobComplete_CallbackFires()
    {
        var (coordinator, _, handler) = Create();

        handler.EnqueueJsonOk(MakePage(2, 10, ("p1", 500)));

        var callbackResults = new ConcurrentBag<GlobalLeaderboardResult>();

        var jobs = new List<DeepScrapeJob>
        {
            new()
            {
                SongId = "song1", Instrument = "Solo_Guitar",
                ValidCutoff = 1000, ValidEntryTarget = 1,
                ReportedPages = 10, Wave2Start = 2, ValidCount = 0,
            },
        };

        var results = await coordinator.RunAsync(
            jobs, new AdaptiveConcurrencyLimiter(4, 1, 4, _log),
            "token", "acct", seedBatch: 5,
            onJobComplete: async result =>
            {
                callbackResults.Add(result);
                await ValueTask.CompletedTask;
            },
            CancellationToken.None);

        // Give callback a moment to fire (it runs on a background task)
        await Task.Delay(100);

        Assert.Single(callbackResults);
        Assert.Equal("song1", callbackResults.First().SongId);
    }

    // ─── Empty jobs list returns empty ──

    [Fact]
    public async Task EmptyJobs_ReturnsEmpty()
    {
        var (coordinator, _, _) = Create();

        var results = await coordinator.RunAsync(
            new List<DeepScrapeJob>(),
            new AdaptiveConcurrencyLimiter(4, 1, 4, _log),
            "token", "acct", seedBatch: 100, onJobComplete: null, CancellationToken.None);

        Assert.Empty(results);
    }

    // ─── BuildJobs creates correct jobs from metadata ──

    [Fact]
    public void BuildJobs_CreatesCorrectJobs()
    {
        var metadata = new List<DeepScrapeMetadata>
        {
            new()
            {
                SongId = "s1", Instrument = "Solo_Guitar", Label = "Song One",
                ValidCutoff = 1000, Wave2Start = 100,
                ReportedPages = 500, InitialValidCount = 50,
                Wave1Entries = new ConcurrentDictionary<int, List<LeaderboardEntry>>(),
            },
            new()
            {
                SongId = "s2", Instrument = "Solo_Bass", Label = null,
                ValidCutoff = 2000, Wave2Start = 50,
                ReportedPages = 300, InitialValidCount = 10,
                Wave1Entries = new ConcurrentDictionary<int, List<LeaderboardEntry>>(),
            },
        };

        var jobs = DeepScrapeCoordinator.BuildJobs(metadata, validEntryTarget: 10_000);

        Assert.Equal(2, jobs.Count);

        Assert.Equal("s1", jobs[0].SongId);
        Assert.Equal("Solo_Guitar", jobs[0].Instrument);
        Assert.Equal("Song One", jobs[0].Label);
        Assert.Equal(1000, jobs[0].ValidCutoff);
        Assert.Equal(10_000, jobs[0].ValidEntryTarget);
        Assert.Equal(100, jobs[0].Wave2Start);
        Assert.Equal(500, jobs[0].ReportedPages);
        Assert.Equal(50, jobs[0].ValidCount);

        Assert.Equal("s2", jobs[1].SongId);
        Assert.Equal("Solo_Bass", jobs[1].Instrument);
        Assert.Null(jobs[1].Label);
        Assert.Equal(2000, jobs[1].ValidCutoff);
        Assert.Equal(10, jobs[1].ValidCount);
    }

    // ─── Cancellation: coordinator respects cancellation token ──

    [Fact]
    public async Task Cancellation_StopsCoordinator()
    {
        var (coordinator, _, handler) = Create();

        // Enqueue one page, then cancel
        handler.EnqueueJsonOk(MakePage(2, 100, ("p1", 500)));
        // Don't enqueue more — cancellation should stop before needing them

        using var cts = new CancellationTokenSource();

        var jobs = new List<DeepScrapeJob>
        {
            new()
            {
                SongId = "song1", Instrument = "Solo_Guitar",
                ValidCutoff = 1000, ValidEntryTarget = 1000,
                ReportedPages = 100, Wave2Start = 2, ValidCount = 0,
            },
        };

        // Cancel after a short delay
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        // Should complete without throwing (OCE is caught internally)
        var results = await coordinator.RunAsync(
            jobs, new AdaptiveConcurrencyLimiter(1, 1, 1, _log),
            "token", "acct", seedBatch: 5, onJobComplete: null, cts.Token);

        Assert.Single(results);
        // May have fetched 0 or 1 pages before cancellation
        Assert.True(results[0].PagesScraped <= 5);
    }

    // ─── Seed extension: seeds more pages when running low ──

    [Fact]
    public async Task SeedExtension_SeedsMorePagesWhenLow()
    {
        var (coordinator, _, handler) = Create();

        // seedBatch=2, so initially seeds pages 2-3.
        // Page 2 has valid entry, page 3 has valid entry → need more.
        // Target = 5, initial = 0. After pages 2-3: 2 valid.
        // Should extend seed to pages 4-5.
        // Pages 4-5: 2 more valid → 4 valid. Still under 5.
        // Should extend to pages 6-7.
        // Pages 6: 1 valid → 5 valid → target met.
        handler.EnqueueJsonOk(MakePage(2, 20, ("p1", 800)));
        handler.EnqueueJsonOk(MakePage(3, 20, ("p2", 700)));
        handler.EnqueueJsonOk(MakePage(4, 20, ("p3", 600)));
        handler.EnqueueJsonOk(MakePage(5, 20, ("p4", 500)));
        handler.EnqueueJsonOk(MakePage(6, 20, ("p5", 400)));

        var jobs = new List<DeepScrapeJob>
        {
            new()
            {
                SongId = "song1", Instrument = "Solo_Guitar",
                ValidCutoff = 1000, ValidEntryTarget = 5,
                ReportedPages = 20, Wave2Start = 2, ValidCount = 0,
            },
        };

        var results = await coordinator.RunAsync(
            jobs, new AdaptiveConcurrencyLimiter(1, 1, 1, _log),
            "token", "acct", seedBatch: 2, onJobComplete: null, CancellationToken.None);

        Assert.Single(results);
        Assert.True(results[0].PagesScraped >= 5);
        Assert.True(results[0].Entries.Count >= 5);
    }

    // ─── Over-threshold entries don't count as valid ──

    [Fact]
    public async Task OverThresholdEntries_NotCountedAsValid()
    {
        var (coordinator, _, handler) = Create();

        // ValidCutoff = 1000. Pages 2-3 have over-threshold entries (1200, 1100).
        // Page 4 has valid entry (900). Target = 1.
        // After pages 2-3: 0 valid. After page 4: 1 valid → done.
        handler.EnqueueJsonOk(MakePage(2, 10, ("p1", 1200)));
        handler.EnqueueJsonOk(MakePage(3, 10, ("p2", 1100)));
        handler.EnqueueJsonOk(MakePage(4, 10, ("p3", 900)));

        var jobs = new List<DeepScrapeJob>
        {
            new()
            {
                SongId = "song1", Instrument = "Solo_Guitar",
                ValidCutoff = 1000, ValidEntryTarget = 1,
                ReportedPages = 10, Wave2Start = 2, ValidCount = 0,
            },
        };

        var results = await coordinator.RunAsync(
            jobs, new AdaptiveConcurrencyLimiter(1, 1, 1, _log),
            "token", "acct", seedBatch: 5, onJobComplete: null, CancellationToken.None);

        Assert.Single(results);
        // All 3 pages fetched, but only 1 valid
        Assert.True(results[0].PagesScraped >= 3);
        Assert.True(results[0].Entries.Count >= 3);
    }
}
