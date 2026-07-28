using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FSTService.Tests.Unit;

public sealed class ImprovementNotificationRecoveryServiceTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RunPublishedScrapeAsync_ShutdownDefersAndNextAttemptCompletes()
    {
        var scrapeId = _fixture.Db.StartScrapeRun();
        _fixture.Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        _fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true);

        var notificationService = new ImprovementNotificationService(
            _fixture.DataSource,
            NullLogger<ImprovementNotificationService>.Instance);
        var recovery = new ImprovementNotificationRecoveryService(
            notificationService,
            new SoloCurrentProjectionBuilder(
                _fixture.DataSource,
                NullLogger<SoloCurrentProjectionBuilder>.Instance),
            Options.Create(new ImprovementNotificationOptions
            {
                Enabled = true,
                Scope = "registered",
                IncludePlayers = true,
                IncludeBands = false,
                IncludeSongEvents = false,
                IncludeRankings = false,
                RefreshSoloProjection = false,
                PruneExpired = false,
            }),
            NullLogger<ImprovementNotificationRecoveryService>.Instance);

        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            recovery.RunPublishedScrapeAsync(
                expectedPublishedScrapeId: scrapeId,
                execute: true,
                baselineOnly: false,
                refreshSoloProjection: false,
                projectionScopes: [],
                force: false,
                source: "test-shutdown",
                shutdown.Token));

        var deferred = notificationService.GetPublicationStatus();
        Assert.Equal("pending", deferred.MarkerStatus);
        Assert.Equal(1, deferred.AttemptCount);
        Assert.False(deferred.IsCompleteForPublishedScrape(includePlayers: true, includeBands: false));

        var report = await recovery.RunPublishedScrapeAsync(
            expectedPublishedScrapeId: scrapeId,
            execute: true,
            baselineOnly: false,
            refreshSoloProjection: false,
            projectionScopes: [],
            force: false,
            source: "test-resume",
            CancellationToken.None);

        Assert.False(report.Skipped);
        Assert.Equal(scrapeId, report.PublishedScrapeId);
        Assert.NotNull(report.Player?.RunId);

        var completed = notificationService.GetPublicationStatus();
        Assert.Equal("completed", completed.MarkerStatus);
        Assert.Equal(2, completed.AttemptCount);
        Assert.True(completed.IsCompleteForPublishedScrape(includePlayers: true, includeBands: false));
    }
}
