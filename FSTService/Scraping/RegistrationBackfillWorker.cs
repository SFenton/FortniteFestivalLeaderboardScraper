using FortniteFestival.Core.Services;
using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

/// <summary>
/// Worker-owned claimant for API-queued registration backfills. Keeping this in
/// the scraper worker process ensures backfill shares the active song machine,
/// DOP limiter, RPS limiter, and CDN recovery state.
/// </summary>
public sealed class RegistrationBackfillWorker : BackgroundService
{
    private readonly StartupInitializer _startup;
    private readonly FestivalService _festivalService;
    private readonly CyclicalSongMachine _cyclicalMachine;
    private readonly BackfillOrchestrator _backfillOrchestrator;
    private readonly IMetaDatabase _metaDb;
    private readonly BackgroundWorkCoordinator _coordinator;
    private readonly IOptions<ScraperOptions> _options;
    private readonly ILogger<RegistrationBackfillWorker> _log;

    public RegistrationBackfillWorker(
        StartupInitializer startup,
        FestivalService festivalService,
        CyclicalSongMachine cyclicalMachine,
        BackfillOrchestrator backfillOrchestrator,
        IMetaDatabase metaDb,
        BackgroundWorkCoordinator coordinator,
        IOptions<ScraperOptions> options,
        ILogger<RegistrationBackfillWorker> log)
    {
        _startup = startup;
        _festivalService = festivalService;
        _cyclicalMachine = cyclicalMachine;
        _backfillOrchestrator = backfillOrchestrator;
        _metaDb = metaDb;
        _coordinator = coordinator;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _startup.WaitForReadyAsync(stoppingToken);
            _cyclicalMachine.Start(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!_coordinator.TryBeginBackgroundOperation(out var operationLease))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                try
                {
                    using (operationLease)
                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                               stoppingToken,
                               _coordinator.BackgroundToken))
                    {
                        var opts = _options.Value;
                        var claimed = await RunAvailableRegistrationWorkAsync(
                            opts.RegistrationBackfillBatchSize,
                            (batchSize, token) => _backfillOrchestrator.RunQueuedRegistrationBackfillBatchAsync(
                                _festivalService,
                                batchSize,
                                token),
                            token => _backfillOrchestrator.RunHistoryReconAsync(
                                _festivalService,
                                token),
                            () => _metaDb.GetPendingBackfills().Count > 0
                                  || _metaDb.GetDeferredBackfills().Count > 0,
                            claimedInBatch => _log.LogInformation(
                                "Claimed {Count} queued registration backfill account(s).",
                                claimedInBatch),
                            linkedCts.Token);

                        if (claimed > 0)
                            continue;

                        var delay = opts.RegistrationBackfillPollInterval <= TimeSpan.Zero
                            ? TimeSpan.FromSeconds(30)
                            : opts.RegistrationBackfillPollInterval;
                        await Task.Delay(delay, linkedCts.Token);
                    }
                }
                catch (OperationCanceledException) when (
                    !stoppingToken.IsCancellationRequested
                    && _coordinator.ScrapeRunning)
                {
                    _log.LogInformation(
                        "Registration backfill paused at the scrape publication boundary.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "RegistrationBackfillWorker failed unexpectedly.");
        }
    }

    internal static async Task<int> DrainQueuedRegistrationBackfillsAsync(
        int batchSize,
        Func<int, CancellationToken, Task<int>> runBatchAsync,
        Action<int> onBatchClaimed,
        CancellationToken ct)
    {
        var totalClaimed = 0;
        while (!ct.IsCancellationRequested)
        {
            var claimed = await runBatchAsync(batchSize, ct);
            if (claimed <= 0)
                return totalClaimed;

            totalClaimed += claimed;
            onBatchClaimed(claimed);
        }

        ct.ThrowIfCancellationRequested();
        return totalClaimed;
    }

    internal static async Task<int> RunAvailableRegistrationWorkAsync(
        int batchSize,
        Func<int, CancellationToken, Task<int>> runBatchAsync,
        Func<CancellationToken, Task> runHistoryReconAsync,
        Func<bool> hasQueuedBackfills,
        Action<int> onBatchClaimed,
        CancellationToken ct)
    {
        var claimed = await DrainQueuedRegistrationBackfillsAsync(
            batchSize,
            runBatchAsync,
            onBatchClaimed,
            ct);
        if (!hasQueuedBackfills())
            await runHistoryReconAsync(ct);

        return claimed;
    }
}