using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class DeepScrapeTests
{
    private readonly ILogger<GlobalLeaderboardScraper> _log = Substitute.For<ILogger<GlobalLeaderboardScraper>>();
    private readonly ScrapeProgressTracker _progress = new();

    private (GlobalLeaderboardScraper scraper, MockHttpMessageHandler handler) CreateScraper()
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler);
        var scraper = new GlobalLeaderboardScraper(http, _progress, _log, maxLookupRetries: 0);
        return (scraper, handler);
    }

    /// <summary>
    /// Helper: build a V1 leaderboard page JSON string with the given entries.
    /// Each entry is (teamId, score). Page metadata is inserted automatically.
    /// </summary>
    private static string MakePage(int pageNum, int totalPages, params (string Id, int Score)[] entries)
    {
        var entryJson = string.Join(",", entries.Select((e, i) =>
            $@"{{""teamId"":""{e.Id}"",""rank"":{pageNum * 100 + i + 1},""percentile"":0.5,""sessionHistory"":[{{""trackedStats"":{{""SCORE"":{e.Score}}}}}]}}"));
        return $@"{{""page"":{pageNum},""totalPages"":{totalPages},""entries"":[{entryJson}]}}";
    }

    // ─── No CHOpt max → normal behavior ──────────────────

    [Fact]
    public async Task NoChoptMax_NormalPages_NoWave2()
    {
        var (scraper, handler) = CreateScraper();

        handler.EnqueueJsonOk(MakePage(0, 2, ("p1", 5000), ("p2", 4000)));
        handler.EnqueueJsonOk(MakePage(1, 2, ("p3", 3000)));

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct",
            maxPages: 100,
            choptMaxScore: null);

        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(2, result.PagesScraped);
    }

    // ─── CHOpt max present, top score under threshold → normal pages ────

    [Fact]
    public async Task ChoptMax_UnderThreshold_NormalPages()
    {
        var (scraper, handler) = CreateScraper();

        // CHOpt max = 10000, threshold = 10500. Top score is 10400 → under.
        handler.EnqueueJsonOk(MakePage(0, 2, ("p1", 10400), ("p2", 9000)));
        handler.EnqueueJsonOk(MakePage(1, 2, ("p3", 8000)));

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct",
            maxPages: 100,
            choptMaxScore: 10000);

        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(2, result.PagesScraped);
    }

    // ─── CHOpt max present, top score over threshold, over-threshold fits in wave 1 → wave 2 fires ──

    [Fact]
    public async Task ChoptMax_OverThreshold_FitsInWave1_Wave2Fires()
    {
        var (scraper, handler) = CreateScraper();

        // CHOpt max = 1000, threshold at 1.05x = 1050.
        // Page 0 has a score of 1100 (over threshold) → deep scrape triggered.
        // Wave 1: maxPages=2 (pages 0-1). Over-threshold on page 0 only.
        // Reported 5 total pages.
        // Wave 2 should fetch pages 2-4 (page 0 + extraPages=3, capped at reportedPages=5).
        handler.EnqueueJsonOk(MakePage(0, 5, ("p1", 1100), ("p2", 900)));
        handler.EnqueueJsonOk(MakePage(1, 5, ("p3", 800)));
        // Wave 2: pages 2, 3, 4
        handler.EnqueueJsonOk(MakePage(2, 5, ("p4", 700)));
        handler.EnqueueJsonOk(MakePage(3, 5, ("p5", 600)));
        handler.EnqueueJsonOk(MakePage(4, 5, ("p6", 500)));

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct",
            maxPages: 2,
            choptMaxScore: 1000,
            overThresholdMultiplier: 1.05,
            overThresholdExtraPages: 3);

        // Should have all 6 entries from 5 pages
        Assert.Equal(6, result.Entries.Count);
        Assert.Equal(5, result.PagesScraped);
    }

    // ─── Over-threshold reaches edge of wave 1 → wave 2 fetches extra pages ──

    [Fact]
    public async Task ChoptMax_OverThresholdAtWave1Edge_Wave2FetchesExtraPages()
    {
        var (scraper, handler) = CreateScraper();

        // CHOpt max = 1000, threshold = 1050.
        // maxPages = 2 (pages 0-1). Over-threshold on BOTH pages (edge of wave 1).
        // Reported 6 total pages.
        // Wave 2: pages 2 through min(2 + 3, 6) - 1 = pages 2-4.
        handler.EnqueueJsonOk(MakePage(0, 6, ("p1", 1200)));
        handler.EnqueueJsonOk(MakePage(1, 6, ("p2", 1100))); // still over threshold at wave 1 edge
        // Wave 2: pages 2, 3, 4
        handler.EnqueueJsonOk(MakePage(2, 6, ("p3", 900)));
        handler.EnqueueJsonOk(MakePage(3, 6, ("p4", 800)));
        handler.EnqueueJsonOk(MakePage(4, 6, ("p5", 700)));

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct",
            maxPages: 2,
            choptMaxScore: 1000,
            overThresholdMultiplier: 1.05,
            overThresholdExtraPages: 3);

        // 5 entries from 5 pages
        Assert.Equal(5, result.Entries.Count);
        Assert.Equal(5, result.PagesScraped);
    }

    // ─── No wave 2 when wave 1 already covers all reported pages ──

    [Fact]
    public async Task ChoptMax_OverThreshold_AllPagesInWave1_NoWave2()
    {
        var (scraper, handler) = CreateScraper();

        // Only 2 pages reported, maxPages=100 covers all.
        // Over-threshold on page 0, but no extra pages to fetch.
        handler.EnqueueJsonOk(MakePage(0, 2, ("p1", 1100)));
        handler.EnqueueJsonOk(MakePage(1, 2, ("p2", 800)));

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct",
            maxPages: 100,
            choptMaxScore: 1000);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(2, result.PagesScraped);
    }

    // ─── SongMaxScores.GetByInstrument wiring ──

    [Fact]
    public void SongMaxScores_GetByInstrument_ReturnsCorrectValues()
    {
        var ms = new SongMaxScores
        {
            MaxLeadScore = 100,
            MaxBassScore = 200,
            MaxDrumsScore = 300,
            MaxVocalsScore = 400,
            MaxProLeadScore = 500,
            MaxProBassScore = 600,
        };

        Assert.Equal(100, ms.GetByInstrument("Solo_Guitar"));
        Assert.Equal(200, ms.GetByInstrument("Solo_Bass"));
        Assert.Equal(300, ms.GetByInstrument("Solo_Drums"));
        Assert.Equal(400, ms.GetByInstrument("Solo_Vocals"));
        Assert.Equal(500, ms.GetByInstrument("Solo_PeripheralGuitar"));
        Assert.Equal(600, ms.GetByInstrument("Solo_PeripheralBass"));
        Assert.Null(ms.GetByInstrument("Unknown"));
    }

    // ─── Config multiplier is respected ──

    [Fact]
    public async Task ChoptMax_CustomMultiplier_Respected()
    {
        var (scraper, handler) = CreateScraper();

        // CHOpt max = 1000. With multiplier=1.20, threshold = 1200.
        // Top score 1150 → under 1200 → no deep scrape.
        handler.EnqueueJsonOk(MakePage(0, 3, ("p1", 1150)));
        handler.EnqueueJsonOk(MakePage(1, 3, ("p2", 900)));
        handler.EnqueueJsonOk(MakePage(2, 3, ("p3", 800)));

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct",
            maxPages: 100,
            choptMaxScore: 1000,
            overThresholdMultiplier: 1.20);

        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(3, result.PagesScraped);
    }

    [Fact]
    public async Task ChoptMax_CustomMultiplierTriggersDeepScrape()
    {
        var (scraper, handler) = CreateScraper();

        // CHOpt max = 1000. With multiplier=1.10, threshold = 1100.
        // Top score 1150 → over 1100 → deep scrape triggers.
        // maxPages=2, reported=4, extraPages=2.
        handler.EnqueueJsonOk(MakePage(0, 4, ("p1", 1150)));
        handler.EnqueueJsonOk(MakePage(1, 4, ("p2", 900)));
        // Wave 2: pages 2-3
        handler.EnqueueJsonOk(MakePage(2, 4, ("p3", 800)));
        handler.EnqueueJsonOk(MakePage(3, 4, ("p4", 700)));

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct",
            maxPages: 2,
            choptMaxScore: 1000,
            overThresholdMultiplier: 1.10,
            overThresholdExtraPages: 2);

        Assert.Equal(4, result.Entries.Count);
        Assert.Equal(4, result.PagesScraped);
    }

    // ─── Valid entry target: stops when target met ──

    [Fact]
    public async Task ValidTarget_StopsWhenTargetMet()
    {
        var (scraper, handler) = CreateScraper();

        // CHOpt max = 1000, threshold = 1050. Top score 1100 → deep scrape.
        // Wave 1: maxPages=2 (pages 0-1). All entries on page 0 are over threshold.
        // Page 1 has 1 valid entry. Target = 3 valid entries.
        // Reported 10 total pages.
        // Wave 2 batch 1 (pages 2-4): pages 2-4 have valid entries.
        // After batch 1: 1 (wave1) + 3 (batch1) = 4 valid ≥ target(3) → stop.
        handler.EnqueueJsonOk(MakePage(0, 10, ("p1", 1100), ("p2", 1080)));
        handler.EnqueueJsonOk(MakePage(1, 10, ("p3", 900)));
        // Batch 1: pages 2, 3, 4
        handler.EnqueueJsonOk(MakePage(2, 10, ("p4", 800)));
        handler.EnqueueJsonOk(MakePage(3, 10, ("p5", 700)));
        handler.EnqueueJsonOk(MakePage(4, 10, ("p6", 600)));
        // Pages 5-9 should NOT be fetched

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct",
            maxPages: 2,
            choptMaxScore: 1000,
            overThresholdMultiplier: 1.05,
            overThresholdExtraPages: 3,
            validEntryTarget: 3);

        // 6 entries total (2 invalid + 4 valid), from 5 pages
        Assert.Equal(6, result.Entries.Count);
        Assert.Equal(5, result.PagesScraped);
    }

    // ─── Valid entry target: few cheaters → minimal extra pages ──

    [Fact]
    public async Task ValidTarget_FewCheaters_MinimalExtraPages()
    {
        var (scraper, handler) = CreateScraper();

        // CHOpt max = 1000, threshold = 1050. Only 1 cheater on page 0.
        // Wave 1: maxPages=2 (pages 0-1). 1 valid on page 0, 1 valid on page 1 = 2 valid.
        // Target = 4. Batch 1 (pages 2-4): 3 valid entries.
        // After batch 1: 2 + 3 = 5 ≥ 4 → stop after first batch.
        handler.EnqueueJsonOk(MakePage(0, 10, ("p1", 1100), ("p2", 900)));
        handler.EnqueueJsonOk(MakePage(1, 10, ("p3", 800)));
        // Batch 1: pages 2, 3, 4
        handler.EnqueueJsonOk(MakePage(2, 10, ("p4", 700)));
        handler.EnqueueJsonOk(MakePage(3, 10, ("p5", 600)));
        handler.EnqueueJsonOk(MakePage(4, 10, ("p6", 500)));
        // Pages 5-9 should NOT be fetched

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct",
            maxPages: 2,
            choptMaxScore: 1000,
            overThresholdMultiplier: 1.05,
            overThresholdExtraPages: 3,
            validEntryTarget: 4);

        Assert.Equal(6, result.Entries.Count);
        Assert.Equal(5, result.PagesScraped);
    }

    // ─── Valid entry target: many cheaters → multiple batches ──

    [Fact]
    public async Task ValidTarget_ManyCheaters_MultipleBatches()
    {
        var (scraper, handler) = CreateScraper();

        // CHOpt max = 1000, threshold = 1050. All wave 1 entries are cheaters.
        // Target = 2 valid entries. Batch size = 2 pages.
        // Batch 1 (pages 2-3): still cheaters → 0 valid. Batch 2 (pages 4-5): 2 valid → done.
        handler.EnqueueJsonOk(MakePage(0, 8, ("p1", 2000)));
        handler.EnqueueJsonOk(MakePage(1, 8, ("p2", 1800)));
        // Batch 1: pages 2-3 (more cheaters)
        handler.EnqueueJsonOk(MakePage(2, 8, ("p3", 1500)));
        handler.EnqueueJsonOk(MakePage(3, 8, ("p4", 1200)));
        // Batch 2: pages 4-5 (valid scores!)
        handler.EnqueueJsonOk(MakePage(4, 8, ("p5", 900)));
        handler.EnqueueJsonOk(MakePage(5, 8, ("p6", 800)));
        // Pages 6-7 should NOT be fetched

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct",
            maxPages: 2,
            choptMaxScore: 1000,
            overThresholdMultiplier: 1.05,
            overThresholdExtraPages: 2,
            validEntryTarget: 2);

        Assert.Equal(6, result.Entries.Count);
        Assert.Equal(6, result.PagesScraped);
    }

    // ─── Valid entry target: leaderboard exhausted before target met ──

    [Fact]
    public async Task ValidTarget_LeaderboardExhausted()
    {
        var (scraper, handler) = CreateScraper();

        // CHOpt max = 1000, threshold = 1050. All entries are cheaters.
        // Target = 5, but only 4 pages exist total and all are over threshold.
        // Should fetch everything and log a warning, not crash.
        handler.EnqueueJsonOk(MakePage(0, 4, ("p1", 2000)));
        handler.EnqueueJsonOk(MakePage(1, 4, ("p2", 1800)));
        // Batch 1: pages 2-3
        handler.EnqueueJsonOk(MakePage(2, 4, ("p3", 1500)));
        handler.EnqueueJsonOk(MakePage(3, 4, ("p4", 1200)));

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct",
            maxPages: 2,
            choptMaxScore: 1000,
            overThresholdMultiplier: 1.05,
            overThresholdExtraPages: 3,
            validEntryTarget: 5);

        // All 4 entries from 4 pages, 0 valid
        Assert.Equal(4, result.Entries.Count);
        Assert.Equal(4, result.PagesScraped);
    }

    // ─── Valid entry target = 0 → legacy fixed-page behavior ──

    [Fact]
    public async Task ValidTarget_Zero_LegacyBehavior()
    {
        var (scraper, handler) = CreateScraper();

        // Same setup as ChoptMax_OverThreshold_FitsInWave1_Wave2Fires but with explicit validEntryTarget=0.
        // Should use legacy fixed-page behavior, not target-driven.
        handler.EnqueueJsonOk(MakePage(0, 5, ("p1", 1100), ("p2", 900)));
        handler.EnqueueJsonOk(MakePage(1, 5, ("p3", 800)));
        // Legacy wave 2: pages 2, 3, 4
        handler.EnqueueJsonOk(MakePage(2, 5, ("p4", 700)));
        handler.EnqueueJsonOk(MakePage(3, 5, ("p5", 600)));
        handler.EnqueueJsonOk(MakePage(4, 5, ("p6", 500)));

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct",
            maxPages: 2,
            choptMaxScore: 1000,
            overThresholdMultiplier: 1.05,
            overThresholdExtraPages: 3,
            validEntryTarget: 0);

        Assert.Equal(6, result.Entries.Count);
        Assert.Equal(5, result.PagesScraped);
    }

    // ─── Valid entry target: wave 1 already has enough valid entries → no extension ──

    [Fact]
    public async Task ValidTarget_AlreadyMetInWave1_NoExtension()
    {
        var (scraper, handler) = CreateScraper();

        // CHOpt max = 1000, threshold = 1050.
        // Page 0 has 1 cheater + 1 valid. Page 1 has 1 valid. Target = 2.
        // Wave 1 already has 2 valid → no wave 2 batches.
        // But wave 1 doesn't cover all reported pages (reported=5, maxPages=2),
        // so wave 2 check fires — but valid count ≥ target → loop body never executes.
        handler.EnqueueJsonOk(MakePage(0, 5, ("p1", 1100), ("p2", 900)));
        handler.EnqueueJsonOk(MakePage(1, 5, ("p3", 800)));
        // No additional pages should be fetched

        var result = await scraper.ScrapeLeaderboardAsync(
            "song1", "Solo_Guitar", "token", "acct",
            maxPages: 2,
            choptMaxScore: 1000,
            overThresholdMultiplier: 1.05,
            overThresholdExtraPages: 3,
            validEntryTarget: 2);

        // Only wave 1 entries
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(2, result.PagesScraped);
    }
}
