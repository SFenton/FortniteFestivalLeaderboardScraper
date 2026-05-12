using FortniteFestival.Core;
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
    private readonly PathGenerator _pathGenerator;
    private readonly IPathDataStore _pathDataStore;
    private readonly SongsCacheService _songsCache;
    private readonly ScrapeTimePrecomputer _precomputer;
    private readonly ScrapeProgressTracker _progress;
    private readonly NotificationService _notifications;
    private readonly IOptions<ScraperOptions> _options;
    private readonly System.Text.Json.JsonSerializerOptions _jsonOpts;
    private readonly ILogger<SongCatalogRefreshWorker> _log;

    public SongCatalogRefreshWorker(
        FestivalService festivalService,
        StartupInitializer startup,
        GlobalLeaderboardPersistence persistence,
        PathGenerator pathGenerator,
        IPathDataStore pathDataStore,
        SongsCacheService songsCache,
        ScrapeTimePrecomputer precomputer,
        ScrapeProgressTracker progress,
        NotificationService notifications,
        IOptions<ScraperOptions> options,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
        ILogger<SongCatalogRefreshWorker> log)
    {
        _festivalService = festivalService;
        _startup = startup;
        _persistence = persistence;
        _pathGenerator = pathGenerator;
        _pathDataStore = pathDataStore;
        _songsCache = songsCache;
        _precomputer = precomputer;
        _progress = progress;
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
                "SongCatalogRefreshWorker starting. Interval={Interval}, PathGeneration={PathGenerationEnabled}",
                _options.Value.SongSyncInterval,
                _options.Value.EnablePathGeneration);

            PrimeSongsCache();
            _ = Task.Run(() => TryGeneratePathsAsync(force: false, stoppingToken), CancellationToken.None);

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

    private async Task RefreshCatalogAsync(CancellationToken ct)
    {
        try
        {
            var before = _festivalService.Songs.Count;
            await _festivalService.SyncSongsAsync();
            var after = _festivalService.Songs.Count;
            var added = Math.Max(0, after - before);

            if (added > 0)
            {
                _log.LogInformation(
                    "Song catalog refresh: {NewCount} new song(s) discovered ({Total} total).",
                    added,
                    after);
                _persistence.InvalidateTotalSongCount();
                PrimeSongsCache();
                await _notifications.NotifySongsChangedAsync(after, added);
            }
            else
            {
                _log.LogDebug("Song catalog refresh: {Total} songs in catalog (no changes).", after);
            }

            if (await TryGeneratePathsAsync(force: false, ct))
            {
                PrimeSongsCache();
                await _notifications.NotifySongsChangedAsync(_festivalService.Songs.Count, 0);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Song catalog refresh failed. Will retry at next interval.");
        }
    }

    private async Task<bool> TryGeneratePathsAsync(bool force, CancellationToken ct)
    {
        var opts = _options.Value;
        if (!opts.EnablePathGeneration)
            return false;

        var ownsProgress = false;
        try
        {
            var songs = _festivalService.Songs
                .Where(s => s.track?.su is not null && !string.IsNullOrEmpty(s.track.mu))
                .ToList();
            if (songs.Count == 0)
                return false;

            var existingState = _pathDataStore.GetPathGenerationState();
            var requests = songs.Select(s =>
            {
                existingState.TryGetValue(s.track.su, out var state);
                return new PathGenerator.SongPathRequest(
                    s.track.su,
                    s.track.tt ?? s.track.su,
                    s.track.an ?? "Unknown",
                    s.track.mu,
                    s.lastModified == DateTime.MinValue ? null : s.lastModified,
                    state.Hash,
                    state.LastModified);
            }).ToList();

            ownsProgress = _progress.BeginPathGeneration(requests.Count);
            var results = await _pathGenerator.GeneratePathsAsync(requests, force, ct);
            if (results.Count == 0)
                return false;

            foreach (var result in results)
            {
                var scores = new SongMaxScores
                {
                    GeneratedAt = DateTime.UtcNow.ToString("o"),
                    CHOptVersion = "1.10.3",
                };

                foreach (var pathResult in result.Results.Where(r => r.Difficulty == "expert"))
                    scores.SetByInstrument(pathResult.Instrument, pathResult.MaxScore);

                var song = songs.FirstOrDefault(s => s.track.su == result.SongId);
                var songLastModified = song?.lastModified is { } lastModified && lastModified != DateTime.MinValue
                    ? lastModified.ToString("o")
                    : null;
                _pathDataStore.UpdateMaxScores(result.SongId, scores, result.DatFileHash, songLastModified);
            }

            _log.LogInformation("Path generation updated {Count} song(s).", results.Count);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Path generation failed. Song catalog refresh continues unaffected.");
            return false;
        }
        finally
        {
            if (ownsProgress)
                _progress.EndPathGeneration();
        }
    }

    private void PrimeSongsCache()
    {
        try
        {
            _songsCache.Prime(_festivalService, _pathDataStore, _persistence.Meta, _persistence, _precomputer, _jsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to prime songs cache; will rebuild on next request.");
            _songsCache.Invalidate();
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
