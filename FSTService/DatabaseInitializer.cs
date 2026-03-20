using FSTService.Persistence;

namespace FSTService;

/// <summary>
/// Initializes all SQLite database schemas (meta + 6 instrument DBs) as a
/// background hosted service, allowing Kestrel to start accepting connections
/// immediately. The /readyz endpoint gates on <see cref="IsReady"/> to signal
/// when databases are fully initialized.
/// </summary>
public sealed class DatabaseInitializer : IHostedService
{
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly ILogger<DatabaseInitializer> _log;
    private volatile bool _ready;

    public bool IsReady => _ready;

    public DatabaseInitializer(
        GlobalLeaderboardPersistence persistence,
        ILogger<DatabaseInitializer> log)
    {
        _persistence = persistence;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("Initializing SQLite databases...");
        _persistence.Initialize();
        _ready = true;
        _log.LogInformation("Database initialization complete.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
