using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FSTService.Tests.Unit;

public sealed class ImprovementNotificationStalenessMonitorTests
{
    [Fact]
    public async Task Monitor_LogsStalePublishedScrape()
    {
        using var fixture = new InMemoryMetaDatabase();
        var scrapeId = fixture.Db.StartScrapeRun();
        fixture.Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes: []);
        var logger =
            new TestLogger<ImprovementNotificationStalenessMonitor>();
        var monitor = new ImprovementNotificationStalenessMonitor(
            new ImprovementNotificationService(
                fixture.DataSource,
                NullLogger<ImprovementNotificationService>.Instance),
            Options.Create(new ImprovementNotificationOptions
            {
                Enabled = true,
                IncludePlayers = true,
                IncludeBands = false,
                IncludeSongEvents = false,
                IncludeRankings = false,
                StaleAfterPublishedScrapes = 1,
                StaleAfterHours = 0,
                StalenessCheckInterval = TimeSpan.Zero,
            }),
            logger);

        await monitor.StartAsync(CancellationToken.None);
        await monitor.StopAsync(CancellationToken.None);

        var entry = Assert.Single(
            logger.Entries,
            static item => item.Level == LogLevel.Error);
        Assert.Contains(
            $"publishedScrape={scrapeId}",
            entry.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "incompletePublishedScrape=True",
            entry.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Monitor_DisabledOptionSkipsDatabaseProbe()
    {
        using var fixture = new InMemoryMetaDatabase();
        var logger =
            new TestLogger<ImprovementNotificationStalenessMonitor>();
        var monitor = new ImprovementNotificationStalenessMonitor(
            new ImprovementNotificationService(
                fixture.DataSource,
                NullLogger<ImprovementNotificationService>.Instance),
            Options.Create(new ImprovementNotificationOptions
            {
                Enabled = false,
                StalenessCheckInterval = TimeSpan.Zero,
            }),
            logger);

        await monitor.StartAsync(CancellationToken.None);
        await monitor.StopAsync(CancellationToken.None);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Monitor_LogsProbeFailureAndKeepsRunning()
    {
        var fixture = new InMemoryMetaDatabase();
        var notificationService = new ImprovementNotificationService(
            fixture.DataSource,
            NullLogger<ImprovementNotificationService>.Instance);
        fixture.Dispose();
        var logger =
            new TestLogger<ImprovementNotificationStalenessMonitor>();
        var monitor = new ImprovementNotificationStalenessMonitor(
            notificationService,
            Options.Create(new ImprovementNotificationOptions
            {
                Enabled = true,
                StalenessCheckInterval = TimeSpan.Zero,
            }),
            logger);

        await monitor.StartAsync(CancellationToken.None);
        await monitor.StopAsync(CancellationToken.None);

        var entry = Assert.Single(
            logger.Entries,
            static item => item.Level == LogLevel.Warning);
        Assert.Contains(
            "Failed to evaluate improvement notification staleness",
            entry.Message,
            StringComparison.Ordinal);
        Assert.NotNull(entry.Exception);
    }
}
