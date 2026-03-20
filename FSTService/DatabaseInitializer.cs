using FortniteFestival.Core.Services;
using FSTService.Persistence;

namespace FSTService;

/// <summary>
/// Initializes all SQLite database schemas and eagerly loads the song catalog
/// as a background hosted service, allowing Kestrel to start accepting connections
/// immediately. The /readyz endpoint gates on <see cref="IsReady"/> to signal
/// when databases and song data are fully loaded.
/// </summary>
public sealed class DatabaseInitializer : IHostedService
{
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly FestivalService _festivalService;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DatabaseInitializer> _log;
    private volatile bool _ready;

    public bool IsReady => _ready;

    public DatabaseInitializer(
        GlobalLeaderboardPersistence persistence,
        FestivalService festivalService,
        IHostApplicationLifetime lifetime,
        ILogger<DatabaseInitializer> log)
    {
        _persistence = persistence;
        _festivalService = festivalService;
        _lifetime = lifetime;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget: run initialization in background so Kestrel can
        // bind the port immediately. /readyz gates on IsReady for traffic.
        _ = InitializeInBackgroundAsync(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task InitializeInBackgroundAsync(CancellationToken ct)
    {
        try
        {
            _log.LogInformation("Initializing databases and song catalog...");

            // Run DB schema init and song catalog load in parallel
            var dbTask = Task.Run(() => _persistence.Initialize(), ct);
            var songTask = _festivalService.InitializeAsync();

            await Task.WhenAll(dbTask, songTask);

            _log.LogInformation(
                "Initialization complete. {SongCount} songs loaded, {DbCount} instrument DBs ready.",
                _festivalService.Songs.Count, 6);
            _ready = true;
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Database initialization failed. Application cannot serve data. Shutting down.");
            _lifetime.StopApplication();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
