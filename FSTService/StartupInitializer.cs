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
    private readonly PublicationCommitOptions
        _publicationCommitOptions;
    private readonly IPublicationRecoveryCoordinator?
        _publicationRecovery;
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
        RolloutReadOnlyViolationMonitor? readOnlyViolations = null,
        IOptions<PublicationCommitOptions>?
            publicationCommitOptions = null,
        IPublicationRecoveryCoordinator?
            publicationRecovery = null)
    {
        _persistence = persistence;
        _dataSource = dataSource;
        _festivalService = festivalService;
        _shopService = shopService;
        _lifetime = lifetime;
        _scraperOptions = scraperOptions.Value;
        _publicationCommitOptions =
            publicationCommitOptions?.Value
            ?? new PublicationCommitOptions();
        _publicationRecovery = publicationRecovery;
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
                await EnsurePublicationPathArtifactReleaseAsync(ct);
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

            if (_scraperOptions.SkipsStartupSchemaInitialization)
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

            await EnsurePublicationPathArtifactReleaseAsync(ct);

            PurgeSongsRouteCacheRowsIfPublicationBound();

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

            var recovery = _publicationRecovery?.RunOnce();
            var commitIntentReconciliation =
                recovery?.CommitIntent
                ?? _persistence.Meta
                    .ReconcileStalePublicationCommitIntent(
                        TimeSpan.FromSeconds(
                            Math.Max(
                                1,
                                _publicationCommitOptions
                                    .StaleCommitIntentSeconds)));
            if (commitIntentReconciliation.Status is not (
                PublicationCommitIntentReconciliationStatus.NotPresent
                or PublicationCommitIntentReconciliationStatus.Fresh))
            {
                _log.LogWarning(
                    "Startup publication commit-intent reconciliation result: {Status}; scrape={ScrapeId}; age={Age}.",
                    commitIntentReconciliation.Status,
                    commitIntentReconciliation.ScrapeId,
                    commitIntentReconciliation.Age);
            }
            if (recovery is null)
            {
                _ = _persistence.Meta
                    .ReconcileAbandonedWorkingPublication(
                        TimeSpan.FromSeconds(
                            Math.Max(
                                1,
                                _publicationCommitOptions
                                    .AbandonedReadyGraceSeconds)),
                        TimeSpan.FromSeconds(
                            Math.Max(
                                1,
                                _publicationCommitOptions
                                    .WorkerHeartbeatFreshSeconds)));
            }
            var bandOrphanSweep =
                recovery?.BandSweep
                ?? _persistence.Meta
                    .SweepPublicationBandTableOrphans();
            if (!bandOrphanSweep.Completed)
            {
                _log.LogWarning(
                    "Startup publication band orphan sweep deferred. LockAcquired={LockAcquired}, Examined={Examined}.",
                    bandOrphanSweep.LockAcquired,
                    bandOrphanSweep.ExaminedTableCount);
            }

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

    /// <summary>
    /// Retires pre-existing <c>public-route:/api/songs</c> rows so the
    /// canonical <c>public-api:songs:v1</c> row wins immediately after a
    /// publication-bound rollout. Best effort: runtime plan filtering already
    /// prevents route-key rows from being read or written in this mode.
    /// </summary>
    private void PurgeSongsRouteCacheRowsIfPublicationBound()
    {
        if (!_scraperOptions.UsePublicationPathArtifacts
            || PostgresDefaultTransactionReadOnly)
        {
            return;
        }

        try
        {
            var purged = _persistence.Meta
                .PurgeApiResponseCacheKeysWithPrefix(
                    PublicApiResponseCachePolicy
                        .SongsRouteCacheKeyPrefix);
            if (purged > 0)
            {
                _log.LogInformation(
                    "Purged {Count} legacy /api/songs route-key response cache row(s); the canonical publication songs key now owns the surface.",
                    purged);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Could not purge legacy /api/songs route-key response cache rows. Publication-bound reads still ignore them.");
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

    private async Task EnsurePublicationPathArtifactReleaseAsync(
        CancellationToken ct)
    {
        // A startup mode that never runs DDL must not read publication-bound
        // path artifacts before the schema-initializing role applies the
        // current manifest release.
        if (!_scraperOptions.RequiresPublicationPathArtifactReleaseGate)
            return;

        await PublicationPathArtifactReleaseGate
            .EnsureReleasedAsync(_dataSource, ct);
        _log.LogInformation(
            "Publication path artifact release verified: contractVersion={ContractVersion}, manifestVersion={ManifestVersion}.",
            PublicationPathArtifactSchema.ContractVersion,
            PublicationPathArtifactSchema.ManifestVersion);
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
