using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService;

/// <summary>
/// Initializes database schemas and eagerly loads the song catalog
/// as a background hosted service, allowing Kestrel to start accepting connections
/// immediately. Implements <see cref="IHealthCheck"/> for the /readyz endpoint.
/// </summary>
public sealed class StartupInitializer : IHostedService, IHealthCheck
{
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly NpgsqlDataSource _dataSource;
    private readonly FestivalService _festivalService;
    private readonly ItemShopService _shopService;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ScraperOptions _scraperOptions;
    private readonly RolloutReadOnlyViolationMonitor? _readOnlyViolations;
    private readonly ILogger<StartupInitializer> _log;
    private readonly TaskCompletionSource _readySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True once databases and song catalog are fully initialized.</summary>
    public bool IsReady => _readySignal.Task.IsCompletedSuccessfully;
    public bool PostgresDefaultTransactionReadOnly { get; private set; }

    /// <summary>Awaitable task that completes when initialization finishes.</summary>
    public Task WaitForReadyAsync(CancellationToken ct = default)
        => _readySignal.Task.WaitAsync(ct);

    public StartupInitializer(
        GlobalLeaderboardPersistence persistence,
        NpgsqlDataSource dataSource,
        FestivalService festivalService,
        ItemShopService shopService,
        IHostApplicationLifetime lifetime,
        IOptions<ScraperOptions> scraperOptions,
        ILogger<StartupInitializer> log,
        RolloutReadOnlyViolationMonitor? readOnlyViolations = null)
    {
        _persistence = persistence;
        _dataSource = dataSource;
        _festivalService = festivalService;
        _shopService = shopService;
        _lifetime = lifetime;
        _scraperOptions = scraperOptions.Value;
        _log = log;
        _readOnlyViolations = readOnlyViolations;
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
            await VerifyPostgresTransactionModeAsync(ct);

            if (_scraperOptions.RolloutReadOnlyStartup)
            {
                _log.LogWarning(
                    "Rollout read-only startup enabled. Loading existing published state without schema, cleanup, provider sync, item-shop refresh, timers, or persistence writes.");
                _persistence.InitializeReadOnly();
                await _festivalService.InitializePersistedStateOnlyAsync();
                await _shopService.InitializePersistedStateOnlyAsync(ct);
                _readySignal.TrySetResult();
                _log.LogInformation(
                    "Rollout read-only initialization complete. {SongCount} persisted songs loaded.",
                    _festivalService.Songs.Count);
                return;
            }

            if (_scraperOptions.ApiOnly || _scraperOptions.SkipStartupSchemaInitialization)
            {
                _log.LogInformation(
                    "Skipping startup schema initialization; ApiOnly={ApiOnly}, SkipStartupSchemaInitialization={SkipStartupSchemaInitialization}. Relying on existing database schema.",
                    _scraperOptions.ApiOnly,
                    _scraperOptions.SkipStartupSchemaInitialization);
            }
            else
            {
                await EnsureSchemaWithRetryAsync(ct);
            }

            // Clean up any leftover spool files from previous runs
            SpoolWriter<LeaderboardEntry>.CleanupStaleFiles(_log);
            ScraperDataCleanup.DeleteLegacyDatFiles(_scraperOptions.DataDirectory, _log);
            ScraperDataCleanup.CleanupStaleDataSpools(
                _scraperOptions.DataDirectory,
                TimeSpan.FromHours(Math.Max(1, _scraperOptions.StaleSpoolCleanupMinAgeHours)),
                _log);

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

    private async Task EnsureSchemaWithRetryAsync(CancellationToken ct)
    {
        const int maxRetries = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DatabaseInitializer.EnsureSchemaAsync(_dataSource, ct);
                return;
            }
            catch (Exception ex) when (attempt < maxRetries &&
                (ex is NpgsqlException || ex is System.Net.Sockets.SocketException ||
                 ex.InnerException is System.Net.Sockets.SocketException))
            {
                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt - 1), 30));
                _log.LogWarning(ex,
                    "Schema init attempt {Attempt}/{MaxRetries} failed. Retrying in {Delay}s...",
                    attempt, maxRetries, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    private async Task VerifyPostgresTransactionModeAsync(CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SHOW default_transaction_read_only";
        var value = Convert.ToString(
            await command.ExecuteScalarAsync(ct),
            System.Globalization.CultureInfo.InvariantCulture);
        var isReadOnly = string.Equals(
            value,
            "on",
            StringComparison.OrdinalIgnoreCase)
            ? true
            : string.Equals(
                value,
                "off",
                StringComparison.OrdinalIgnoreCase)
                ? false
                : throw new InvalidOperationException(
                    $"Unexpected default_transaction_read_only value: {value ?? "<null>"}.");
        PostgresDefaultTransactionReadOnly = isReadOnly;
        if (isReadOnly != _scraperOptions.RolloutReadOnlyStartup)
        {
            throw new InvalidOperationException(
                _scraperOptions.RolloutReadOnlyStartup
                    ? "Rollout read-only startup requires default_transaction_read_only=on."
                    : "Normal startup requires default_transaction_read_only=off.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_readOnlyViolations?.HasViolation == true)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "A PostgreSQL read-only violation was detected.",
                _readOnlyViolations.LastViolation));
        }
        return Task.FromResult(IsReady
            ? HealthCheckResult.Healthy("Databases initialized.")
            : HealthCheckResult.Unhealthy("Databases still initializing."));
    }
}
