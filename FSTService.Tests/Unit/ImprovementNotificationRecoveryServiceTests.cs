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
    public async Task RunPublishedScrapeAsync_WithoutRefreshPreservesPersistedProjectionPlan()
    {
        var scrapeId = _fixture.Db.StartScrapeRun();
        _fixture.Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        _fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes:
            [
                new SoloCurrentProjectionScopeKey("song-1", "Solo_Guitar"),
            ]);
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

        await recovery.RunPublishedScrapeAsync(
            expectedPublishedScrapeId: scrapeId,
            execute: true,
            baselineOnly: false,
            refreshSoloProjection: false,
            projectionScopes: null,
            force: false,
            source: "test-persisted-plan",
            CancellationToken.None);

        var plan = notificationService.GetProjectionPlan(scrapeId);
        Assert.True(plan.IsReady);
        var scope = Assert.Single(plan.Scopes);
        Assert.Equal("song-1", scope.SongId);
        Assert.Equal("Solo_Guitar", scope.Instrument);
    }

    [Fact]
    public async Task RunPublishedScrapeAsync_ShutdownDefersAndNextAttemptCompletes()
    {
        var scrapeId = _fixture.Db.StartScrapeRun();
        _fixture.Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        _fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes: []);

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
        Assert.False(deferred.IsCompleteForPublishedScrape(
            includePlayers: true,
            includeBands: false,
            includeSongEvents: false,
            includeRankings: false));

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
        Assert.True(completed.IsCompleteForPublishedScrape(
            includePlayers: true,
            includeBands: false,
            includeSongEvents: false,
            includeRankings: false));

        var skipped = await recovery.RunPublishedScrapeAsync(
            expectedPublishedScrapeId: scrapeId,
            execute: true,
            baselineOnly: false,
            refreshSoloProjection: false,
            projectionScopes: [],
            force: false,
            source: "test-already-complete",
            CancellationToken.None);

        Assert.True(skipped.Skipped);
        Assert.Contains("already completed", skipped.SkipReason);
    }

    [Fact]
    public async Task RunPublishedScrapeAsync_BaselineOnlyDoesNotSatisfyDetectionCompletion()
    {
        var scrapeId = _fixture.Db.StartScrapeRun();
        _fixture.Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        _fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes: []);

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

        await recovery.RunPublishedScrapeAsync(
            expectedPublishedScrapeId: scrapeId,
            execute: true,
            baselineOnly: true,
            refreshSoloProjection: false,
            projectionScopes: [],
            force: false,
            source: "test-baseline",
            CancellationToken.None);

        var baselineStatus = notificationService.GetPublicationStatus();
        Assert.Equal("pending", baselineStatus.MarkerStatus);
        Assert.False(baselineStatus.IsCompleteForPublishedScrape(
            includePlayers: true,
            includeBands: false,
            includeSongEvents: false,
            includeRankings: false));

        await recovery.RunPublishedScrapeAsync(
            expectedPublishedScrapeId: scrapeId,
            execute: true,
            baselineOnly: false,
            refreshSoloProjection: false,
            projectionScopes: [],
            force: false,
            source: "test-detection",
            CancellationToken.None);

        var completedStatus = notificationService.GetPublicationStatus();
        Assert.Equal("completed", completedStatus.MarkerStatus);
        Assert.True(completedStatus.IsCompleteForPublishedScrape(
            includePlayers: true,
            includeBands: false,
            includeSongEvents: false,
            includeRankings: false));
    }

    [Fact]
    public async Task RunPublishedScrapeAsync_RejectsMismatchedMarkerWithoutRewritingIt()
    {
        var publishedScrapeId = _fixture.Db.StartScrapeRun();
        _fixture.Db.CompleteScrapeRun(publishedScrapeId, 1, 10, 1, 100);
        _fixture.Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes: []);
        var otherScrapeId = _fixture.Db.StartScrapeRun();
        _fixture.Db.CompleteScrapeRun(otherScrapeId, 1, 10, 1, 100);

        using (var conn = _fixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                ALTER TABLE scrape_publication_state
                DROP CONSTRAINT ck_scrape_publication_notification_plan;

                UPDATE scrape_publication_state
                SET improvement_notifications_scrape_id = @otherScrapeId
                WHERE id = TRUE;
                """;
            cmd.Parameters.AddWithValue("otherScrapeId", (int)otherScrapeId);
            cmd.ExecuteNonQuery();
        }

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
                IncludePlayers = false,
                IncludeBands = false,
            }),
            NullLogger<ImprovementNotificationRecoveryService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recovery.RunPublishedScrapeAsync(
                expectedPublishedScrapeId: publishedScrapeId,
                execute: true,
                baselineOnly: false,
                refreshSoloProjection: false,
                projectionScopes: [],
                force: false,
                source: "test-mismatch",
                CancellationToken.None));

        Assert.Contains("does not match published scrape", exception.Message);
        Assert.Equal(otherScrapeId, notificationService.GetPublicationStatus().MarkerScrapeId);
    }

    [Fact]
    public async Task RunPublishedScrapeAsync_DoesNotReopenDisabledMarker()
    {
        var scrapeId = _fixture.Db.StartScrapeRun();
        _fixture.Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        _fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes: []);

        var notificationService = new ImprovementNotificationService(
            _fixture.DataSource,
            NullLogger<ImprovementNotificationService>.Instance);
        notificationService.MarkPublicationDisabled(scrapeId, "Disabled for test.");
        var recovery = new ImprovementNotificationRecoveryService(
            notificationService,
            new SoloCurrentProjectionBuilder(
                _fixture.DataSource,
                NullLogger<SoloCurrentProjectionBuilder>.Instance),
            Options.Create(new ImprovementNotificationOptions
            {
                Enabled = true,
                IncludePlayers = false,
                IncludeBands = false,
            }),
            NullLogger<ImprovementNotificationRecoveryService>.Instance);

        var report = await recovery.RunPublishedScrapeAsync(
            expectedPublishedScrapeId: scrapeId,
            execute: true,
            baselineOnly: false,
            refreshSoloProjection: false,
            projectionScopes: [],
            force: false,
            source: "test-disabled",
            CancellationToken.None);

        Assert.True(report.Skipped);
        Assert.Equal("disabled", notificationService.GetPublicationStatus().MarkerStatus);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recovery.RunPublishedScrapeAsync(
                expectedPublishedScrapeId: scrapeId,
                execute: true,
                baselineOnly: false,
                refreshSoloProjection: false,
                projectionScopes: [],
                force: true,
                source: "test-disabled-force",
                CancellationToken.None));
        Assert.Contains("terminal", exception.Message);
        Assert.Equal("disabled", notificationService.GetPublicationStatus().MarkerStatus);
    }

    [Fact]
    public async Task RunPublishedScrapeAsync_RejectsConcurrentRecoveryOwner()
    {
        var scrapeId = _fixture.Db.StartScrapeRun();
        _fixture.Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        _fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes: []);

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
                IncludePlayers = false,
                IncludeBands = false,
            }),
            NullLogger<ImprovementNotificationRecoveryService>.Instance);

        using var owner = notificationService.AcquireRecoveryLock(scrapeId);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recovery.RunPublishedScrapeAsync(
                expectedPublishedScrapeId: scrapeId,
                execute: true,
                baselineOnly: false,
                refreshSoloProjection: false,
                projectionScopes: [],
                force: false,
                source: "test-concurrent",
                CancellationToken.None));

        Assert.Contains("already running", exception.Message);
        Assert.Equal("pending", notificationService.GetPublicationStatus().MarkerStatus);
    }

    [Fact]
    public async Task RunPublishedScrapeAsync_CompletedMarkerMustCoverNewlyRequiredLanes()
    {
        var scrapeId = _fixture.Db.StartScrapeRun();
        _fixture.Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        _fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes: []);

        var notificationService = new ImprovementNotificationService(
            _fixture.DataSource,
            NullLogger<ImprovementNotificationService>.Instance);
        var baselineOptions = Options.Create(new ImprovementNotificationOptions
        {
            Enabled = true,
            IncludePlayers = true,
            IncludeBands = false,
            IncludeSongEvents = false,
            IncludeRankings = false,
            RefreshSoloProjection = false,
        });
        var baselineRecovery = new ImprovementNotificationRecoveryService(
            notificationService,
            new SoloCurrentProjectionBuilder(
                _fixture.DataSource,
                NullLogger<SoloCurrentProjectionBuilder>.Instance),
            baselineOptions,
            NullLogger<ImprovementNotificationRecoveryService>.Instance);

        await baselineRecovery.RunPublishedScrapeAsync(
            expectedPublishedScrapeId: scrapeId,
            execute: true,
            baselineOnly: false,
            refreshSoloProjection: false,
            projectionScopes: [],
            force: false,
            source: "test-partial-lanes",
            CancellationToken.None);
        Assert.Equal("completed", notificationService.GetPublicationStatus().MarkerStatus);

        var expandedRecovery = new ImprovementNotificationRecoveryService(
            notificationService,
            new SoloCurrentProjectionBuilder(
                _fixture.DataSource,
                NullLogger<SoloCurrentProjectionBuilder>.Instance),
            Options.Create(new ImprovementNotificationOptions
            {
                Enabled = true,
                IncludePlayers = true,
                IncludeBands = false,
                IncludeSongEvents = false,
                IncludeRankings = true,
                RefreshSoloProjection = false,
            }),
            NullLogger<ImprovementNotificationRecoveryService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            expandedRecovery.RunPublishedScrapeAsync(
                expectedPublishedScrapeId: scrapeId,
                execute: true,
                baselineOnly: false,
                refreshSoloProjection: false,
                projectionScopes: [],
                force: false,
                source: "test-expanded-lanes",
                CancellationToken.None));

        Assert.Contains("does not satisfy the currently required lanes", exception.Message);
        Assert.Equal("completed", notificationService.GetPublicationStatus().MarkerStatus);
    }

    [Fact]
    public async Task RunPublishedScrapeAsync_RejectsMissingOrUnexpectedPublication()
    {
        var notificationService = new ImprovementNotificationService(
            _fixture.DataSource,
            NullLogger<ImprovementNotificationService>.Instance);
        var recovery = CreateRecovery(
            notificationService,
            new ImprovementNotificationOptions
            {
                Enabled = true,
                IncludePlayers = false,
                IncludeBands = false,
            });

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recovery.RunPublishedScrapeAsync(
                expectedPublishedScrapeId: null,
                execute: false,
                baselineOnly: false,
                refreshSoloProjection: false,
                projectionScopes: null,
                force: false,
                source: "test-missing",
                CancellationToken.None));
        Assert.Contains("No published scrape", missing.Message);

        var scrapeId = PublishPendingScrape();
        var changed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recovery.RunPublishedScrapeAsync(
                expectedPublishedScrapeId: scrapeId + 1,
                execute: false,
                baselineOnly: false,
                refreshSoloProjection: false,
                projectionScopes: null,
                force: false,
                source: "test-unexpected",
                CancellationToken.None));
        Assert.Contains("changed from expected", changed.Message);
    }

    [Fact]
    public async Task RunPublishedScrapeAsync_DefersWhilePublicReadsAreFrozen()
    {
        var scrapeId = PublishPendingScrape();
        _fixture.Db.SetPublicReadFreeze(true, scrapeId, "test-maintenance");
        var notificationService = new ImprovementNotificationService(
            _fixture.DataSource,
            NullLogger<ImprovementNotificationService>.Instance);
        var recovery = CreateRecovery(
            notificationService,
            new ImprovementNotificationOptions
            {
                Enabled = true,
                IncludePlayers = false,
                IncludeBands = false,
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recovery.RunPublishedScrapeAsync(
                expectedPublishedScrapeId: scrapeId,
                execute: false,
                baselineOnly: false,
                refreshSoloProjection: false,
                projectionScopes: null,
                force: false,
                source: "test-frozen",
                CancellationToken.None));

        Assert.Contains("public reads are frozen", exception.Message);
    }

    [Fact]
    public async Task RunPublishedScrapeAsync_DisabledOptionClosesPendingMarker()
    {
        var scrapeId = PublishPendingScrape();
        var notificationService = new ImprovementNotificationService(
            _fixture.DataSource,
            NullLogger<ImprovementNotificationService>.Instance);
        var recovery = CreateRecovery(
            notificationService,
            new ImprovementNotificationOptions
            {
                Enabled = false,
                IncludePlayers = false,
                IncludeBands = false,
            });

        var report = await recovery.RunPublishedScrapeAsync(
            expectedPublishedScrapeId: scrapeId,
            execute: true,
            baselineOnly: false,
            refreshSoloProjection: false,
            projectionScopes: [],
            force: false,
            source: "test-disabled-option",
            CancellationToken.None);

        Assert.True(report.Skipped);
        Assert.Contains("disabled", report.SkipReason);
        Assert.Equal(
            "disabled",
            notificationService.GetPublicationStatus().MarkerStatus);
    }

    [Fact]
    public async Task RunPublishedScrapeAsync_DryRunCoversPlayerAndBandLanes()
    {
        var scrapeId = PublishPendingScrape();
        var notificationService = new ImprovementNotificationService(
            _fixture.DataSource,
            NullLogger<ImprovementNotificationService>.Instance);
        var recovery = CreateRecovery(
            notificationService,
            new ImprovementNotificationOptions
            {
                Enabled = true,
                Scope = "all",
                IncludePlayers = true,
                IncludeBands = true,
                IncludeSongEvents = false,
                IncludeRankings = false,
                RefreshSoloProjection = false,
                PruneExpired = false,
            });

        var report = await recovery.RunPublishedScrapeAsync(
            expectedPublishedScrapeId: scrapeId,
            execute: false,
            baselineOnly: false,
            refreshSoloProjection: false,
            projectionScopes: null,
            force: false,
            source: "test-dry-run",
            CancellationToken.None);

        Assert.False(report.Skipped);
        Assert.NotNull(report.Player);
        Assert.NotNull(report.Band);
        Assert.Equal("pending", notificationService.GetPublicationStatus().MarkerStatus);
    }

    [Fact]
    public async Task RunPublishedScrapeAsync_RefreshesPersistedProjectionScopes()
    {
        var scope = new SoloCurrentProjectionScopeKey(
            "song-projection",
            "Solo_Guitar");
        var scrapeId = _fixture.Db.StartScrapeRun();
        _fixture.Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        _fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes: [scope]);
        var notificationService = new ImprovementNotificationService(
            _fixture.DataSource,
            NullLogger<ImprovementNotificationService>.Instance);
        var recovery = CreateRecovery(
            notificationService,
            new ImprovementNotificationOptions
            {
                Enabled = true,
                IncludePlayers = true,
                IncludeBands = false,
                IncludeSongEvents = true,
                IncludeRankings = false,
                RefreshSoloProjection = true,
                PruneExpired = false,
            });

        var report = await recovery.RunPublishedScrapeAsync(
            expectedPublishedScrapeId: scrapeId,
            execute: true,
            baselineOnly: false,
            refreshSoloProjection: true,
            projectionScopes: null,
            force: false,
            source: "test-projection-refresh",
            CancellationToken.None);

        Assert.False(report.Skipped);
        Assert.NotNull(report.Projection);
        Assert.Equal(1, report.Projection!.ScopeCount);
        Assert.Equal(0, report.Projection.FailedScopeCount);
    }

    [Fact]
    public async Task RunPublishedScrapeAsync_AdoptsEmptyPlanWhenRefreshIsSkipped()
    {
        var scrapeId = _fixture.Db.StartScrapeRun();
        _fixture.Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        _fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes: []);
        using (var conn = _fixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                ALTER TABLE scrape_publication_state
                DROP CONSTRAINT ck_scrape_publication_notification_plan;

                UPDATE scrape_publication_state
                SET improvement_notifications_projection_ready = FALSE,
                    improvement_notifications_projection_scrape_id = @scrapeId
                WHERE id = TRUE;
                """;
            cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
            cmd.ExecuteNonQuery();
        }
        var notificationService = new ImprovementNotificationService(
            _fixture.DataSource,
            NullLogger<ImprovementNotificationService>.Instance);
        var recovery = CreateRecovery(
            notificationService,
            new ImprovementNotificationOptions
            {
                Enabled = true,
                IncludePlayers = false,
                IncludeBands = false,
                RefreshSoloProjection = false,
            });

        var report = await recovery.RunPublishedScrapeAsync(
            expectedPublishedScrapeId: scrapeId,
            execute: true,
            baselineOnly: false,
            refreshSoloProjection: false,
            projectionScopes: null,
            force: false,
            source: "test-adopt-empty",
            CancellationToken.None);

        Assert.True(report.Skipped);
        Assert.True(notificationService.GetProjectionPlan(scrapeId).IsReady);
    }

    private long PublishPendingScrape()
    {
        var scrapeId = _fixture.Db.StartScrapeRun();
        _fixture.Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        _fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes: []);
        return scrapeId;
    }

    private ImprovementNotificationRecoveryService CreateRecovery(
        ImprovementNotificationService notificationService,
        ImprovementNotificationOptions options) => new(
            notificationService,
            new SoloCurrentProjectionBuilder(
                _fixture.DataSource,
                NullLogger<SoloCurrentProjectionBuilder>.Instance),
            Options.Create(options),
            NullLogger<ImprovementNotificationRecoveryService>.Instance);

}
