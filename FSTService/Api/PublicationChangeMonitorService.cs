using FSTService.Persistence;

namespace FSTService.Api;

public sealed class PublicationChangeMonitorService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private readonly StartupInitializer _startup;
    private readonly IMetaDatabase _metaDb;
    private readonly NotificationService _notifications;
    private readonly ILogger<PublicationChangeMonitorService> _log;

    public PublicationChangeMonitorService(
        StartupInitializer startup,
        IMetaDatabase metaDb,
        NotificationService notifications,
        ILogger<PublicationChangeMonitorService> log)
    {
        _startup = startup;
        _metaDb = metaDb;
        _notifications = notifications;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _startup.WaitForReadyAsync(stoppingToken);
        long? previousPublicationId = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var currentPublicationId =
                    _metaDb.GetPublicationPointerState().CurrentPublicationId;
                if (!previousPublicationId.HasValue)
                {
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
