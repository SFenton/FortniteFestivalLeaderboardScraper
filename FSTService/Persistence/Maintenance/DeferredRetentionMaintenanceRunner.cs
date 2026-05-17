using Microsoft.Extensions.Options;

namespace FSTService.Persistence.Maintenance;

public sealed class DeferredRetentionMaintenanceRunner : IDisposable
{
    private readonly IDatabaseRetentionMaintenanceService _maintenanceService;
    private readonly IDatabasePressureMonitor _pressureMonitor;
    private readonly IOptions<DatabaseMaintenanceOptions> _options;
    private readonly ILogger<DeferredRetentionMaintenanceRunner> _log;
    private readonly object _sync = new();
    private CancellationTokenSource? _runCts;
    private Task? _currentRun;
    private bool _disposed;

    public DeferredRetentionMaintenanceRunner(
        IDatabaseRetentionMaintenanceService maintenanceService,
        IDatabasePressureMonitor pressureMonitor,
        IOptions<DatabaseMaintenanceOptions> options,
        ILogger<DeferredRetentionMaintenanceRunner> log)
    {
        _maintenanceService = maintenanceService;
        _pressureMonitor = pressureMonitor;
        _options = options;
        _log = log;
    }

    public bool ScheduleAfterPublication(string reason, CancellationToken stoppingToken = default)
    {
        var options = _options.Value;
        if (!options.ServiceLevelRetentionMaintenanceEnabled || !options.DeferredServiceLevelRetentionEnabled)
            return false;

        lock (_sync)
        {
            if (_disposed)
                return false;

            if (_currentRun is { IsCompleted: false })
            {
                _log.LogInformation("Deferred service-level retention maintenance already scheduled; new request ignored. Reason={Reason}", reason);
                return false;
            }

            _runCts?.Dispose();
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _currentRun = Task.Run(() => RunAsync(reason, _runCts.Token), CancellationToken.None);
            return true;
        }
    }

    internal Task? CurrentRunForTests
    {
        get
        {
            lock (_sync)
                return _currentRun;
        }
    }

    private async Task RunAsync(string reason, CancellationToken ct)
    {
        var options = _options.Value;
        var startedAtUtc = DateTime.UtcNow;
        var deadlineUtc = startedAtUtc + TimeSpan.FromMinutes(Math.Max(1, options.DeferredServiceLevelRetentionMaxRuntimeMinutes));
        var maxAttempts = Math.Max(1, options.DeferredServiceLevelRetentionMaxAttempts);
        var pollDelay = TimeSpan.FromSeconds(Math.Max(1, options.DeferredServiceLevelRetentionPollSeconds));
        var initialDelay = TimeSpan.FromSeconds(Math.Max(0, options.DeferredServiceLevelRetentionInitialDelaySeconds));

        _log.LogInformation(
            "Deferred service-level retention maintenance scheduled after publication. Reason={Reason}; initialDelay={InitialDelay}; maxAttempts={MaxAttempts:N0}; deadline={DeadlineUtc:o}.",
            reason,
            initialDelay,
            maxAttempts,
            deadlineUtc);

        try
        {
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, ct);

            for (var attempt = 1; attempt <= maxAttempts && DateTime.UtcNow < deadlineUtc; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var pressure = await _pressureMonitor.GetPressureSnapshotAsync(options, ct);
                if (pressure.IsUnderPressure)
                {
                    _log.LogInformation(
                        "Deferred service-level retention maintenance waiting for database pressure to clear before attempt {Attempt:N0}/{MaxAttempts:N0}: {Reasons}.",
                        attempt,
                        maxAttempts,
                        string.Join("; ", pressure.Reasons));
                    await DelayBeforeNextAttemptAsync(pollDelay, deadlineUtc, ct);
                    continue;
                }

                var result = await _maintenanceService.RunAsync(ct);
                if (!result.Skipped)
                {
                    _log.LogInformation(
                        "Deferred service-level retention maintenance completed after {Attempt:N0} attempt(s): {Reason}. Metadata rows deleted={MetadataDeleted:N0}, snapshot candidates={SnapshotCandidates:N0}, rewrites={RewriteCount:N0}.",
                        attempt,
                        result.Reason,
                        result.MetadataCleanup.TotalDeletedRows,
                        result.SnapshotRetention.CandidateCount,
                        result.SnapshotRetention.RewriteResults.Count);
                    return;
                }

                if (!IsRetryableSkip(result.Reason))
                {
                    _log.LogInformation(
                        "Deferred service-level retention maintenance stopped after non-retryable skip: {Reason}.",
                        result.Reason);
                    return;
                }

                _log.LogInformation(
                    "Deferred service-level retention maintenance skipped on attempt {Attempt:N0}/{MaxAttempts:N0}: {Reason}.",
                    attempt,
                    maxAttempts,
                    result.Reason);
                await DelayBeforeNextAttemptAsync(pollDelay, deadlineUtc, ct);
            }

            _log.LogWarning(
                "Deferred service-level retention maintenance expired before running cleanly. Reason={Reason}; attempts={MaxAttempts:N0}; runtime={Runtime}.",
                reason,
                maxAttempts,
                DateTime.UtcNow - startedAtUtc);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogInformation("Deferred service-level retention maintenance canceled. Reason={Reason}", reason);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Deferred service-level retention maintenance failed. Reason={Reason}", reason);
        }
    }

    private static bool IsRetryableSkip(string reason) =>
        reason.Contains("pressure", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("advisory lock", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("already holds", StringComparison.OrdinalIgnoreCase);

    private static async Task DelayBeforeNextAttemptAsync(TimeSpan delay, DateTime deadlineUtc, CancellationToken ct)
    {
        var remaining = deadlineUtc - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return;

        await Task.Delay(remaining < delay ? remaining : delay, ct);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = null;
        }
    }
}