using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService;

/// <summary>
/// Loads runtime state after the pre-pool schema/read-only selection.
/// Implements <see cref="IHealthCheck"/> for read-serving readiness.
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
    private readonly StartupPublicationReadOnlyState _publicationReadOnlyState;
    private readonly TaskCompletionSource _readySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _initializationCancellation;
    private Task? _initializationTask;

    /// <summary>True once databases and song catalog are fully initialized.</summary>
    public bool IsReady => _readySignal.Task.IsCompletedSuccessfully;
    public bool PostgresDefaultTransactionReadOnly { get; private set; }
    public bool ReadOnlyServing => _publicationReadOnlyState.IsLatched;
    public bool MutationReady => IsReady && _publicationReadOnlyState.MutationsReady;
    public string? MutationDisabledReason => _publicationReadOnlyState.Reason;
    public IReadOnlyList<PublicationPathArtifactInitializationFailure> StartupDiagnostics => _publicationReadOnlyState.Failures;
    public IReadOnlyList<PublicationPathArtifactInitializationFailure> StartupWarnings => _publicationReadOnlyState.Warnings;
    public StartupPublicationReadOnlyStatus PublicationStartupStatus => new(
        ReadOnlyServing ? "degraded_read_only" : IsReady ? "ready" : "initializing",
        IsReady, MutationReady, MutationDisabledReason, StartupDiagnostics, StartupWarnings);

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
        StartupPublicationReadOnlyState startupPublicationReadOnlyState,
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
        _publicationReadOnlyState = startupPublicationReadOnlyState;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _initializationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.ApplicationStopping);
        _initializationTask = InitializeInBackgroundAsync(_initializationCancellation.Token);
        return Task.CompletedTask;
    }

    private async Task InitializeInBackgroundAsync(CancellationToken ct)
    {
        try
        {
            _log.LogInformation("Initializing databases and song catalog...");
            if (_publicationReadOnlyState.IsLatched)
            {
                await InitializeReadOnlyUntilAvailableAsync(ct);
                return;
            }

            await VerifyPostgresTransactionModeAsync(ct);
            _log.LogInformation("Startup schema/admission selection completed before runtime pool construction.");

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

            EnsurePublishedScopeSourceReadiness();
            _log.LogInformation(
                "Initialization complete. {SongCount} songs loaded, {DbCount} instrument DBs ready.",
                _festivalService.Songs.Count, 6);
            _publicationReadOnlyState.MarkReady();
            _readySignal.TrySetResult();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _readySignal.TrySetCanceled(ct);
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

    private async Task InitializeReadOnlyUntilAvailableAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await VerifyPostgresTransactionModeAsync(ct);
                _persistence.InitializeReadOnly();
                await _festivalService.InitializePersistedStateOnlyAsync();
                await _shopService.InitializePersistedStateOnlyAsync(ct);
                var sourceReadiness = _persistence.PublishedScopeSourceReadiness.EvaluateCurrent(forceRefresh: true);
                if (!sourceReadiness.Ready)
                    _log.LogWarning("Read-only startup retains route-level publication refusal ({Reason}).",
                        sourceReadiness.Reason);
                _publicationReadOnlyState.MarkReady();
                _readySignal.TrySetResult();
                _log.LogWarning(
                    "Serving persisted public state in degraded read-only mode ({Reason}); all mutations require a guarded restart.",
                    _publicationReadOnlyState.Reason);
                return;
            }
            catch (NpgsqlException exception)
            {
                _log.LogWarning(exception, "Persisted read-only startup data unavailable; retrying without mutation.");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
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
        if (isReadOnly != _publicationReadOnlyState.IsLatched)
        {
            throw new InvalidOperationException(
                _publicationReadOnlyState.IsLatched
                    ? "Read-only startup requires default_transaction_read_only=on."
                    : "Normal startup requires default_transaction_read_only=off.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_initializationCancellation is null)
            return;
        await _initializationCancellation.CancelAsync();
        try
        {
            if (_initializationTask is not null)
                await _initializationTask.WaitAsync(cancellationToken);
        }
        finally
        {
            _initializationCancellation.Dispose();
        }
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object> { ["startup"] = PublicationStartupStatus };
        if (_readOnlyViolations?.HasViolation == true)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "A PostgreSQL read-only violation was detected.",
                _readOnlyViolations.LastViolation, data));
        }
        if (!IsReady)
        {
            return Task.FromResult(
                HealthCheckResult.Unhealthy(
                    "Databases still initializing.", data: data));
        }
        if (ReadOnlyServing)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "degraded_read_only: serving persisted public reads; mutations disabled until guarded restart: " + MutationDisabledReason,
                data));
        }

        var publicationReadiness =
            _persistence.PublishedScopeSourceReadiness
                .EvaluateCurrent();
        return Task.FromResult(
            publicationReadiness.Ready
                ? HealthCheckResult.Healthy(
                    "Databases initialized and published scope-source binding verified.", data)
                : HealthCheckResult.Unhealthy(
                    $"Published scope-source readiness failed: {publicationReadiness.Reason}.", data: data));
    }

    private void EnsurePublishedScopeSourceReadiness()
    {
        var readiness =
            _persistence.PublishedScopeSourceReadiness
                .EvaluateCurrent(forceRefresh: true);
        if (!readiness.Ready)
        {
            throw new InvalidOperationException(
                $"Published scope-source readiness failed during startup: {readiness.Reason}.");
        }
    }
}
