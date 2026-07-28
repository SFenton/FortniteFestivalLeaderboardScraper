using FSTService.Persistence;

namespace FSTService.Tests.Unit;

public sealed class ImprovementNotificationStalenessEvaluatorTests
{
    [Fact]
    public void Evaluate_FlagsPublishedScrapeWithoutDetection()
    {
        var now = new DateTime(2026, 7, 28, 15, 0, 0, DateTimeKind.Utc);
        var status = CreateStatus(
            publishedScrapeId: 1267,
            playerScrapeId: 1236,
            bandScrapeId: 1236,
            completedAt: now.AddDays(-15),
            scrapesBehind: 1,
            markerStatus: "pending");

        var result = ImprovementNotificationStalenessEvaluator.Evaluate(
            status,
            EnabledOptions(),
            now);

        Assert.True(result.IsStale);
        Assert.True(result.IncompletePublishedScrape);
        Assert.Equal(1, result.PublishedScrapesBehind);
    }

    [Fact]
    public void Evaluate_IsHealthyWhenCurrentPublishedScrapeCompletedRecently()
    {
        var now = new DateTime(2026, 7, 28, 15, 0, 0, DateTimeKind.Utc);
        var status = CreateStatus(
            publishedScrapeId: 1267,
            playerScrapeId: 1267,
            bandScrapeId: 1267,
            completedAt: now.AddMinutes(-5),
            scrapesBehind: 0,
            markerStatus: "completed");

        var result = ImprovementNotificationStalenessEvaluator.Evaluate(
            status,
            EnabledOptions(),
            now);

        Assert.False(result.IsStale);
        Assert.False(result.IncompletePublishedScrape);
    }

    [Fact]
    public void Evaluate_FlagsAgeEvenWhenNoNewScrapeIsPending()
    {
        var now = new DateTime(2026, 7, 28, 15, 0, 0, DateTimeKind.Utc);
        var status = CreateStatus(
            publishedScrapeId: 1267,
            playerScrapeId: 1267,
            bandScrapeId: 1267,
            completedAt: now.AddHours(-25),
            scrapesBehind: 0,
            markerStatus: "completed");

        var result = ImprovementNotificationStalenessEvaluator.Evaluate(
            status,
            EnabledOptions(),
            now);

        Assert.True(result.IsStale);
        Assert.False(result.IncompletePublishedScrape);
        Assert.True(result.Age >= TimeSpan.FromHours(25));
    }

    private static ImprovementNotificationOptions EnabledOptions() => new()
    {
        Enabled = true,
        IncludePlayers = true,
        IncludeBands = true,
        StaleAfterPublishedScrapes = 1,
        StaleAfterHours = 24,
    };

    private static ImprovementNotificationPublicationStatus CreateStatus(
        long publishedScrapeId,
        long playerScrapeId,
        long bandScrapeId,
        DateTime completedAt,
        int scrapesBehind,
        string markerStatus) => new(
        PublishedScrapeId: publishedScrapeId,
        PublishedAtUtc: completedAt,
        PublicReadsFrozen: false,
        MarkerScrapeId: publishedScrapeId,
        MarkerStatus: markerStatus,
        AttemptCount: 1,
        StartedAtUtc: completedAt,
        CompletedAtUtc: completedAt,
        ErrorMessage: null,
        LatestPlayerScrapeId: playerScrapeId,
        LatestPlayerRunId: 1,
        LatestPlayerCompletedAtUtc: completedAt,
        LatestPlayerIncludesSongEvents: true,
        LatestPlayerIncludesRankings: true,
        LatestBandScrapeId: bandScrapeId,
        LatestBandRunId: 2,
        LatestBandCompletedAtUtc: completedAt,
        LatestBandIncludesSongEvents: true,
        LatestBandIncludesRankings: true,
        PlayerPublishedScrapesBehind: scrapesBehind,
        BandPublishedScrapesBehind: scrapesBehind);
}
