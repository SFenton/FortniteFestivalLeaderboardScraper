using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FSTService;

/// <summary>
/// Initializes database schemas and eagerly loads the song catalog
/// as a background hosted service, allowing Kestrel to start accepting connections
/// immediately. Implements <see cref="IHealthCheck"/> for the /readyz endpoint.
/// </summary>
public sealed class StartupInitializer : IHostedService, IHealthCheck
{
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly FestivalService _festivalService;
    private readonly ItemShopService _shopService;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<StartupInitializer> _log;
    private readonly TaskCompletionSource _readySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True once databases and song catalog are fully initialized.</summary>
    public bool IsReady => _readySignal.Task.IsCompletedSuccessfully;

    /// <summary>Awaitable task that completes when initialization finishes.</summary>
    public Task WaitForReadyAsync(CancellationToken ct = default)
        => _readySignal.Task.WaitAsync(ct);

    public StartupInitializer(
        GlobalLeaderboardPersistence persistence,
        FestivalService festivalService,
        ItemShopService shopService,
        IHostApplicationLifetime lifetime,
        ILogger<StartupInitializer> log)
    {
        _persistence = persistence;
        _festivalService = festivalService;
        _shopService = shopService;
        _lifetime = lifetime;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = InitializeInBackgroundAsync(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task InitializeInBackgroundAsync(CancellationToken ct)
    {
        try
        {
            _log.LogInformation("Initializing databases and song catalog...");

            var dbTask = Task.Run(() => _persistence.Initialize(), ct);
            var songTask = _festivalService.InitializeAsync();

            await Task.WhenAll(dbTask, songTask);

            // Initialize Item Shop service (loads from DB + first scrape)
            await _shopService.InitializeAsync(ct);

            _log.LogInformation(
                "Initialization complete. {SongCount} songs loaded, {DbCount} instrument DBs ready.",
                _festivalService.Songs.Count, 6);
            _readySignal.TrySetResult();
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Database initialization failed. Shutting down.");
            _readySignal.TrySetException(ex);
            _lifetime.StopApplication();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(IsReady
            ? HealthCheckResult.Healthy("Databases initialized.")
            : HealthCheckResult.Unhealthy("Databases still initializing."));
    }
}
