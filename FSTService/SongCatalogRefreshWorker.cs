using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService;

/// <summary>
/// API-service-owned song catalog refresher. Keeps /api/songs fresh and
/// broadcasts catalog changes to connected clients.
/// It never generates paths: path generation is owned by the worker
/// publication-safe scrape pass and by explicit admin requests, so a catalog
/// refresh can never promote mutable live song rows out of band.
/// </summary>
public sealed class SongCatalogRefreshWorker : BackgroundService
{
    private readonly FestivalService _festivalService;
    private readonly StartupInitializer _startup;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IPathDataStore _pathDataStore;
    private readonly SongsCacheService _songsCache;
    private readonly ScrapeTimePrecomputer _precomputer;
    private readonly NotificationService _notifications;
    private readonly IOptions<ScraperOptions> _options;
    private readonly System.Text.Json.JsonSerializerOptions _jsonOpts;
    private readonly ILogger<SongCatalogRefreshWorker> _log;

    public SongCatalogRefreshWorker(
        FestivalService festivalService,
        StartupInitializer startup,
        GlobalLeaderboardPersistence persistence,
        IPathDataStore pathDataStore,
        SongsCacheService songsCache,
        ScrapeTimePrecomputer precomputer,
        NotificationService notifications,
        IOptions<ScraperOptions> options,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
        ILogger<SongCatalogRefreshWorker> log)
    {
        _festivalService = festivalService;
        _startup = startup;
        _persistence = persistence;
        _pathDataStore = pathDataStore;
        _songsCache = songsCache;
        _precomputer = precomputer;
        _notifications = notifications;
        _options = options;
        _jsonOpts = jsonOptions.Value.SerializerOptions;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _startup.WaitForReadyAsync(stoppingToken);
            _log.LogInformation(
                "SongCatalogRefreshWorker starting. Interval={Interval}, PathGeneration={PathGenerationEnabled}. Catalog refresh never generates paths.",
                _options.Value.SongSyncInterval,
                _options.Value.EnablePathGeneration);

            PrimeSongsCache();

            while (!stoppingToken.IsCancellationRequested)
            {
                await DelayUntilNextBoundaryAsync(_options.Value.SongSyncInterval, stoppingToken);
                await RefreshCatalogAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SongCatalogRefreshWorker failed unexpectedly.");
        }
    }

    internal async Task RefreshCatalogAsync(CancellationToken ct)
    {
        try
        {
            var beforeSnapshot = SongCatalogSnapshotBuilder
                .Create(_festivalService.Songs);
            var syncResult =
                await _festivalService.SyncSongsWithResultAsync();
            var afterSnapshot = SongCatalogSnapshotBuilder
                .Create(_festivalService.Songs);
            if (syncResult.PersistenceToken is not null)
            {
                SongCatalogSnapshotBuilder.ValidateToken(
                    afterSnapshot,
                    syncResult.PersistenceToken);
            }

            if (HasExactCatalogChanged(
                    beforeSnapshot.ContentHash,
                    syncResult))
            {
                var changeSet = SongCatalogSnapshotBuilder
                    .ComputeChangeSet(
                    beforeSnapshot.CatalogJson,
                    afterSnapshot.CatalogJson);
                _log.LogInformation(
                    "Song catalog refresh changed the exact provider catalog: {AddedCount} added, {ChangedCount} changed, {RemovedCount} removed ({Total} total).",
                    changeSet.Added,
                    changeSet.Changed,
                    changeSet.Removed,
                    afterSnapshot.SongCount);
                if (beforeSnapshot.SongCount
                    != afterSnapshot.SongCount)
                {
                    _persistence.InvalidateTotalSongCount();
                }
                _songsCache.InvalidateForContentChange();
                PrimeSongsCache();
                CatalogPublicationLagState? lag = null;
                try
                {
                    lag = _persistence.Meta
                        .GetCatalogPublicationLagState(
                            commandTimeoutSeconds: 5);
                }
                catch (Exception ex)
                    when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(
                        ex,
                        "Song catalog refresh persisted, but catalog-lag telemetry could not be read before notification.");
                }
                await _notifications.NotifySongsChangedAsync(
                    afterSnapshot.SongCount,
                    changeSet.Added,
                    changeSet.Removed,
                    changeSet.Changed,
                    lag?.PublishedSongCount,
                    lag?.AwaitingPublication);
            }
            else
            {
                _log.LogDebug(
                    "Song catalog refresh: {Total} songs in catalog (no exact changes).",
                    afterSnapshot.SongCount);
            }
        }
        catch (SongCatalogPersistenceBusyException ex)
        {
            _log.LogWarning(
                ex,
                "Song catalog refresh deferred because publication persistence is busy. Will retry at next interval.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Song catalog refresh failed. Will retry at next interval.");
        }
    }

    internal static bool HasExactCatalogChanged(
        string beforeContentHash,
        SongCatalogSyncResult syncResult) =>
        syncResult.IsExact
        && syncResult.PersistenceToken is not null
        && !string.Equals(
            beforeContentHash,
            syncResult.PersistenceToken.ContentHash,
            StringComparison.Ordinal);

    private void PrimeSongsCache()
    {
        try
        {
            _songsCache.Prime(
                _festivalService,
                _pathDataStore,
                _persistence.Meta,
                _persistence,
                _precomputer,
                _jsonOpts,
                persistPublicationCache: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to prime songs cache; will rebuild on next request.");
            _songsCache.InvalidateForContentChange();
        }
    }

    private static Task DelayUntilNextBoundaryAsync(TimeSpan interval, CancellationToken ct)
    {
        if (interval <= TimeSpan.Zero)
            interval = TimeSpan.FromMinutes(5);

        var now = DateTime.UtcNow;
        var nextTick = new DateTime((now.Ticks / interval.Ticks + 1) * interval.Ticks, DateTimeKind.Utc);
        var delay = nextTick - now;
        return Task.Delay(delay, ct);
    }
}
