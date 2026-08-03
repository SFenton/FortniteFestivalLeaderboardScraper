using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Api;

public sealed class PublicationChangeMonitorService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private readonly StartupInitializer _startup;
    private readonly IMetaDatabase _metaDb;
    private readonly NotificationService _notifications;
    private readonly ScrapeLifecycleNotifier _scrapeLifecycle;
    private readonly SongsCacheService _songsCache;
    private readonly ILogger<PublicationChangeMonitorService> _log;

    public PublicationChangeMonitorService(
        StartupInitializer startup,
        IMetaDatabase metaDb,
        NotificationService notifications,
        ScrapeLifecycleNotifier scrapeLifecycle,
        SongsCacheService songsCache,
        ILogger<PublicationChangeMonitorService> log)
    {
        _startup = startup;
        _metaDb = metaDb;
        _notifications = notifications;
        _scrapeLifecycle = scrapeLifecycle;
        _songsCache = songsCache;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _startup.WaitForReadyAsync(stoppingToken);
        long? previousPublicationId = null;
        PublicReadFreezeState? previousFreeze = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var currentPublicationId =
                    _metaDb.GetPublicationPointerState().CurrentPublicationId;
                var currentFreeze = _metaDb.GetPublicReadFreezeState();
                if (previousFreeze is null)
                {
                    previousFreeze = currentFreeze;
                }
                else if (previousFreeze.IsFrozen &&
                         previousFreeze
                             .RequiresSamePublicationRefreshOnRelease &&
                         !currentFreeze.IsFrozen &&
                         currentPublicationId.HasValue)
                {
                    _log.LogInformation(
                        "Same-publication maintenance freeze completed for publication {PublicationId}; invalidating API caches and refreshing connected clients.",
                        currentPublicationId);
                    _scrapeLifecycle.InvalidateInProcessCaches();
                    _songsCache.Invalidate();
                    await _notifications.NotifyPublicationChangedAsync(
                        currentPublicationId.Value);
                }
                previousFreeze = currentFreeze;

                if (!previousPublicationId.HasValue)
                {
                    if (currentPublicationId.HasValue)
                    {
                        _scrapeLifecycle.InvalidateInProcessCaches();
                        _songsCache.Invalidate();
                    }
                    previousPublicationId = currentPublicationId;
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }

                if (!currentPublicationId.HasValue
                    || currentPublicationId == previousPublicationId)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }

                _log.LogInformation(
                    "Publication changed from {PreviousPublicationId} to {CurrentPublicationId}; rotating API WebSockets.",
                    previousPublicationId,
                    currentPublicationId);
                _scrapeLifecycle.InvalidateInProcessCaches();
                _songsCache.Invalidate();
                await _notifications.NotifyPublicationChangedAsync(
                    currentPublicationId.Value);
                previousPublicationId = currentPublicationId;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Publication change monitor probe failed; retrying.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}
