using Microsoft.Extensions.Options;

namespace FSTService.Persistence;

public sealed class ImprovementNotificationRecoveryService
{
    private readonly ImprovementNotificationService _notifications;
    private readonly SoloCurrentProjectionBuilder _soloCurrentProjectionBuilder;
    private readonly IOptions<ImprovementNotificationOptions> _options;
    private readonly ILogger<ImprovementNotificationRecoveryService> _log;

    public ImprovementNotificationRecoveryService(
        ImprovementNotificationService notifications,
        SoloCurrentProjectionBuilder soloCurrentProjectionBuilder,
        IOptions<ImprovementNotificationOptions> options,
        ILogger<ImprovementNotificationRecoveryService> log)
    {
        _notifications = notifications;
        _soloCurrentProjectionBuilder = soloCurrentProjectionBuilder;
        _options = options;
        _log = log;
    }

    public async Task<ImprovementNotificationRecoveryReport> RunPublishedScrapeAsync(
        long? expectedPublishedScrapeId,
        bool execute,
        bool baselineOnly,
        bool refreshSoloProjection,
        IReadOnlyCollection<SoloCurrentProjectionScopeKey>? projectionScopes,
        bool force,
        string source,
        CancellationToken ct)
    {
        var options = _options.Value;
        var status = _notifications.GetPublicationStatus();
        if (!status.PublishedScrapeId.HasValue)
            throw new InvalidOperationException("No published scrape is available for improvement notification recovery.");

        var publishedScrapeId = status.PublishedScrapeId.Value;
        if (expectedPublishedScrapeId.HasValue && expectedPublishedScrapeId.Value != publishedScrapeId)
        {
            throw new InvalidOperationException(
                $"Published scrape changed from expected {expectedPublishedScrapeId.Value} to {publishedScrapeId}.");
        }

        using var recoveryLock = _notifications.AcquireRecoveryLock(publishedScrapeId);
        status = _notifications.GetPublicationStatus();
        if (status.PublishedScrapeId != publishedScrapeId)
        {
            throw new InvalidOperationException(
                $"Published scrape changed from {publishedScrapeId} to " +
                $"{status.PublishedScrapeId?.ToString() ?? "null"} while recovery was acquiring ownership.");
        }

        if (status.PublicReadsFrozen)
            throw new InvalidOperationException("Improvement notification recovery is deferred while public reads are frozen.");

        if (status.MarkerStatus == "disabled")
        {
            if (!force)
            {
                return ImprovementNotificationRecoveryReport.CreateSkipped(
                    publishedScrapeId,
                    "Improvement notification marker is already disabled.");
            }

            throw new InvalidOperationException(
                $"Improvement notification marker for published scrape {publishedScrapeId} " +
                "is terminal (disabled) and cannot be forced back to pending.");
        }
        if (status.MarkerScrapeId != publishedScrapeId)
        {
            throw new InvalidOperationException(
                $"Improvement notification marker {status.MarkerScrapeId?.ToString() ?? "null"} " +
                $"does not match published scrape {publishedScrapeId}.");
        }
        if (status.MarkerStatus == "completed")
        {
            var completedForRequiredLanes = status.IsCompleteForPublishedScrape(
                options.IncludePlayers,
                options.IncludeBands,
                options.IncludeSongEvents,
                options.IncludeRankings);
            if (!force && completedForRequiredLanes)
            {
                return ImprovementNotificationRecoveryReport.CreateSkipped(
                    publishedScrapeId,
                    "Improvement notification marker is already completed for every required lane.");
            }

            throw new InvalidOperationException(
                $"Improvement notification marker for published scrape {publishedScrapeId} " +
                "is terminal (completed) but does not satisfy the currently required lanes; " +
                "an explicit operator transition is required.");
        }

        if (!options.Enabled)
        {
            _notifications.MarkPublicationDisabled(
                publishedScrapeId,
                "Improvement notifications were disabled before pending recovery completed.");
            return ImprovementNotificationRecoveryReport.CreateSkipped(
                publishedScrapeId,
                "Improvement notifications are disabled.");
        }

        if (execute)
        {
            if (projectionScopes is not null)
            {
                _notifications.AdoptProjectionPlanForRecovery(
                    publishedScrapeId,
                    projectionScopes);
            }
            else if (!refreshSoloProjection)
            {
                var persistedPlan = _notifications.GetProjectionPlan(publishedScrapeId);
                if (!persistedPlan.IsReady)
                {
                    _notifications.AdoptProjectionPlanForRecovery(
                        publishedScrapeId,
                        []);
                }
            }

            _notifications.EnsurePublicationPending(publishedScrapeId);
            status = _notifications.GetPublicationStatus();
            if (!force
                && !baselineOnly
                && status.IsCompleteForPublishedScrape(
                    options.IncludePlayers,
                    options.IncludeBands,
                    options.IncludeSongEvents,
                    options.IncludeRankings))
            {
                _notifications.MarkPublicationCompleted(publishedScrapeId);
                return ImprovementNotificationRecoveryReport.CreateSkipped(
                    publishedScrapeId,
                    "The published scrape already has completed player and band detection runs.");
            }

            _notifications.MarkPublicationRunning(publishedScrapeId);
        }

        try
        {
            SoloCurrentProjectionIncrementalRefreshResult? projectionReport = null;
            if (refreshSoloProjection
                && options.RefreshSoloProjection
                && options.IncludePlayers
                && options.IncludeSongEvents)
            {
                var scopes = projectionScopes;
                if (scopes is null)
                {
                    var plan = _notifications.GetProjectionPlan(publishedScrapeId);
                    if (!plan.IsReady)
                    {
                        throw new InvalidOperationException(
                            $"Improvement notification projection plan for published scrape {publishedScrapeId} is not ready.");
                    }

                    scopes = plan.Scopes;
                }

                if (scopes.Count > 0)
                {
                    await _soloCurrentProjectionBuilder.EnsureSchemaAsync(ct);
                    projectionReport = await _soloCurrentProjectionBuilder.RefreshScopesAsync(
                        scopes,
                        new SoloCurrentProjectionRebuildOptions
                        {
                            CommandTimeoutSeconds = options.SoloProjectionCommandTimeoutSeconds,
                        },
                        ct);

                    if (projectionReport.FailedScopeCount > 0)
                    {
                        throw new InvalidOperationException(
                            $"Solo current projection refresh failed for {projectionReport.FailedScopeCount} notification scope(s).");
                    }
                }
            }

            ct.ThrowIfCancellationRequested();
            ImprovementNotificationPrecomputeReport? playerReport = null;
            ImprovementNotificationPrecomputeReport? bandReport = null;

            if (options.IncludePlayers)
            {
                playerReport = await Task.Run(() => _notifications.Precompute(
                    new ImprovementNotificationPrecomputeOptions(
                        Scope: options.Scope,
                        Execute: execute,
                        BaselineOnly: baselineOnly,
                        IncludePlayers: true,
                        IncludeBands: false,
                        IncludeSongEvents: options.IncludeSongEvents,
                        IncludeRankings: options.IncludeRankings,
                        PruneExpired: options.PruneExpired,
                        CommandTimeoutSeconds: options.CommandTimeoutSeconds,
                        Source: $"{source}-player",
                        PublishedScrapeId: publishedScrapeId)), ct);
            }

            ct.ThrowIfCancellationRequested();
            if (options.IncludeBands)
            {
                bandReport = await Task.Run(() => _notifications.Precompute(
                    new ImprovementNotificationPrecomputeOptions(
                        Scope: options.Scope,
                        Execute: execute,
                        BaselineOnly: baselineOnly,
                        IncludePlayers: false,
                        IncludeBands: true,
                        IncludeSongEvents: options.IncludeSongEvents,
                        IncludeRankings: options.IncludeRankings,
                        PruneExpired: options.PruneExpired,
                        CommandTimeoutSeconds: options.CommandTimeoutSeconds,
                        Source: $"{source}-band",
                        PublishedScrapeId: publishedScrapeId)), ct);
            }

            if (execute)
            {
                if (baselineOnly)
                {
                    _notifications.MarkPublicationDeferred(
                        publishedScrapeId,
                        "Baseline-only run completed; event detection remains pending.");
                }
                else
                {
                    _notifications.MarkPublicationCompleted(publishedScrapeId);
                }
            }

            var report = new ImprovementNotificationRecoveryReport(
                PublishedScrapeId: publishedScrapeId,
                Execute: execute,
                BaselineOnly: baselineOnly,
                Skipped: false,
                SkipReason: null,
                Projection: projectionReport,
                Player: playerReport,
                Band: bandReport);

            _log.LogInformation(
                "Improvement notification recovery complete for published scrape {ScrapeId}: player run={PlayerRunId}, band run={BandRunId}, player events={PlayerEvents:N0}, band events={BandEvents:N0}.",
                publishedScrapeId,
                playerReport?.RunId,
                bandReport?.RunId,
                (playerReport?.PlayerSongEventsInserted ?? 0) + (playerReport?.PlayerRankEventsInserted ?? 0),
                (bandReport?.BandSongEventsInserted ?? 0) + (bandReport?.BandRankEventsInserted ?? 0));

            return report;
        }
        catch (OperationCanceledException)
        {
            if (execute)
                _notifications.MarkPublicationDeferred(publishedScrapeId, "Notification recovery was interrupted and will resume.");
            throw;
        }
        catch (Exception ex)
        {
            if (execute)
                _notifications.MarkPublicationFailed(publishedScrapeId, ex.Message);
            throw;
        }
    }
}

public sealed record ImprovementNotificationRecoveryReport(
    long PublishedScrapeId,
    bool Execute,
    bool BaselineOnly,
    bool Skipped,
    string? SkipReason,
    SoloCurrentProjectionIncrementalRefreshResult? Projection,
    ImprovementNotificationPrecomputeReport? Player,
    ImprovementNotificationPrecomputeReport? Band)
{
    public static ImprovementNotificationRecoveryReport CreateSkipped(long scrapeId, string reason) => new(
        scrapeId,
        Execute: false,
        BaselineOnly: false,
        Skipped: true,
        SkipReason: reason,
        Projection: null,
        Player: null,
        Band: null);
}
