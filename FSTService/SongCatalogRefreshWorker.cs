using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService;

/// <summary>
/// API-service-owned song catalog refresher. Keeps /api/songs fresh, broadcasts
/// catalog changes to connected clients, and generates CHOpt/path metadata for
/// newly discovered or changed songs without involving the scrape worker.
/// </summary>
public sealed class SongCatalogRefreshWorker : BackgroundService
{
    private readonly FestivalService _festivalService;
    private readonly StartupInitializer _startup;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly PathGenerationCoordinator _pathGeneration;
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
        PathGenerationCoordinator pathGeneration,
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
        _pathGeneration = pathGeneration;
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
                "SongCatalogRefreshWorker starting. Interval={Interval}, PathGeneration={PathGenerationEnabled}, AutomaticPathGeneration={AutomaticPathGenerationEnabled}",
                _options.Value.SongSyncInterval,
                _options.Value.EnablePathGeneration,
                _options.Value.EnableAutomaticPathGeneration);

            PrimeSongsCache();
            _ = Task.Run(
                () => TryGeneratePathsAsync(stoppingToken),
                CancellationToken.None);

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
            var before = _festivalService.Songs.Count;
            var beforeHash = SongCatalogSnapshotBuilder
                .Create(_festivalService.Songs)
                .ContentHash;
            var syncResult =
                await _festivalService.SyncSongsWithResultAsync();
            var after = _festivalService.Songs.Count;
            var added = Math.Max(0, after - before);
            var removed = Math.Max(0, before - after);

            if (HasExactCatalogChanged(
                    beforeHash,
                    syncResult))
            {
                _log.LogInformation(
                    "Song catalog refresh changed the exact provider catalog: {AddedCount} added, {RemovedCount} removed ({Total} total).",
                    added,
                    removed,
                    after);
                if (before != after)
                    _persistence.InvalidateTotalSongCount();
                _songsCache.InvalidateForContentChange();
                PrimeSongsCache();
                await _notifications.NotifySongsChangedAsync(after, added);
            }
            else
            {
                _log.LogDebug(
                    "Song catalog refresh: {Total} songs in catalog (no exact changes).",
                    after);
            }

            await TryGeneratePathsAsync(ct);
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

    private async Task<bool> TryGeneratePathsAsync(CancellationToken ct)
    {
        var opts = _options.Value;
        if (!opts.EnablePathGeneration ||
            !opts.EnableAutomaticPathGeneration)
            return false;

        try
        {
            var songs = _festivalService.Songs
                .Where(s => s.track?.su is not null && !string.IsNullOrEmpty(s.track.mu))
                .ToList();
            if (songs.Count == 0)
                return false;

            var result = await _pathGeneration.GenerateAutomaticPathsAsync(
                songs,
                ct);
            return result.Changed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Path generation failed. Song catalog refresh continues unaffected.");
            return false;
        }
    }

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
