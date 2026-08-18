using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Publishes background-worker liveness and activity to PostgreSQL so the API-only
/// process can report the real scraper process state.
/// </summary>
public sealed class WorkerStatusPublisher
{
    public const string ScraperWorkerKey = "scraper";

    private readonly IMetaDatabase _metaDb;
    private readonly ILogger<WorkerStatusPublisher> _log;
    private readonly DurablePhaseProgressSink? _phaseProgress;
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkerOperationInfo> _activeOperations = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _instanceId;
    private readonly DateTime _startedAtUtc;
    private WorkerOperationInfo? _currentOperation;
    private WorkerOperationInfo? _lastOperation;

    public WorkerStatusPublisher(
        IMetaDatabase metaDb,
        ILogger<WorkerStatusPublisher> log,
        DurablePhaseProgressSink? phaseProgress = null)
    {
        _metaDb = metaDb;
        _log = log;
        _phaseProgress = phaseProgress;
        _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        _startedAtUtc = DateTime.UtcNow;
    }

    public string InstanceId => _instanceId;

    public void AttachScrape(long scrapeId)
    {
        _phaseProgress?.AttachScrape(scrapeId, _instanceId);
        WorkerOperationInfo? current;
        lock (_gate)
            current = _currentOperation;
        if (current is null)
            return;

        var descriptor = PhaseProgressCatalog.FindByOperationKey(current.OperationKey);
        var view = descriptor is null
            ? null
            : _phaseProgress?.StartPhase(descriptor, current.SubOperation);
        if (view is not null)
            ApplyDurableProgress(view);
    }

    public void PublishHeartbeat(string status = "running", string? message = null)
    {
        var now = DateTime.UtcNow;
        WorkerOperationInfo? currentOperation;
        lock (_gate)
        {
            currentOperation = _currentOperation is null
                ? null
                : CopyOperation(_currentOperation, heartbeatAtUtc: now);
            if (currentOperation is not null)
            {
                _currentOperation = currentOperation;
                _activeOperations[currentOperation.OperationKey] = currentOperation;
            }
        }

        _phaseProgress?.Heartbeat();
        TryPublish(() => _metaDb.UpsertWorkerHeartbeat(
            ScraperWorkerKey,
            status,
            mode: "scraper",
            instanceId: _instanceId,
            startedAtUtc: _startedAtUtc,
            heartbeatAtUtc: now,
            message,
            currentOperation));
    }

    public void MarkOffline(string? message = null)
        => PublishHeartbeat("offline", message ?? "Worker stopped");

    public void BeginOperation(string operationKey, string operationLabel,
        string? phase = null, string? subOperation = null, string? detail = null,
        double? progressPercent = null)
    {
        var now = DateTime.UtcNow;
        var descriptor = PhaseProgressCatalog.FindByOperationKey(operationKey);
        var durableView = descriptor is null
            ? null
            : _phaseProgress?.StartPhase(descriptor, subOperation);
        var operation = new WorkerOperationInfo
        {
            ContractVersion = 2,
            OperationKey = operationKey,
            OperationLabel = operationLabel,
            Status = "running",
            Phase = phase,
            SubOperation = subOperation,
            Detail = detail,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            ProgressPercent = progressPercent,
            OperationId = durableView?.OperationId,
            PhaseId = durableView?.PhaseId,
            PhaseStatus = durableView?.PhaseStatus,
            SubphaseId = durableView?.SubphaseId,
            PhasePlanVersion = durableView?.PlanVersion,
            PhaseOrdinal = durableView?.PhaseOrdinal,
            PhaseAttempt = durableView?.Attempt,
            UnitsKind = durableView?.UnitsKind,
            UnitsCompleted = durableView?.UnitsCompleted,
            UnitsTotal = durableView?.UnitsTotal,
            UnitsTotalFinal = durableView?.UnitsTotalFinal,
            PhasePercent = durableView?.PhasePercent,
            OverallPercentKind = durableView?.OverallPercentKind
                ?? "indeterminate",
            OverallPercent = durableView?.OverallPercent,
            OverallModelVersion = durableView?.OverallModelVersion,
            EtaLowerSeconds = durableView?.EtaLowerSeconds,
            EtaUpperSeconds = durableView?.EtaUpperSeconds,
            EtaConfidence = durableView?.EtaConfidence,
            EtaSampleCount = durableView?.EtaSampleCount,
            LastProgressAtUtc = durableView?.LastProgressAtUtc,
            HeartbeatAtUtc = now,
            SubphaseProgress = durableView?.SubphaseProgress,
        };

        lock (_gate)
        {
            _activeOperations[operationKey] = operation;
            _currentOperation = operation;
        }

        TryPublish(() => _metaDb.UpdateWorkerActivity(
            ScraperWorkerKey,
            operation,
            status: "running",
            updatedAtUtc: now,
            instanceId: _instanceId));
    }

    public void UpdateOperation(string operationKey, string? operationLabel = null,
        string? phase = null, string? subOperation = null, string? detail = null,
        double? progressPercent = null, double? estimatedRemainingSeconds = null)
    {
        WorkerOperationInfo? operation;
        var now = DateTime.UtcNow;

        lock (_gate)
        {
            if (!_activeOperations.TryGetValue(operationKey, out var existing))
                return;

            var descriptor = PhaseProgressCatalog.FindByOperationKey(operationKey);
            var durableView = descriptor is null
                ? null
                : _phaseProgress?.TransitionSubphase(
                    descriptor.Id,
                    subOperation ?? existing.SubOperation);

            operation = CopyOperation(existing,
                operationLabel: operationLabel ?? existing.OperationLabel,
                phase: phase ?? existing.Phase,
                subOperation: subOperation ?? existing.SubOperation,
                detail: detail ?? existing.Detail,
                progressPercent: progressPercent ?? existing.ProgressPercent,
                estimatedRemainingSeconds: estimatedRemainingSeconds ?? existing.EstimatedRemainingSeconds,
                updatedAtUtc: now,
                elapsedSeconds: (now - existing.StartedAtUtc).TotalSeconds,
                durableProgress: durableView);

            _activeOperations[operationKey] = operation;
            _currentOperation = operation;
        }

        TryPublish(() => _metaDb.UpdateWorkerActivity(
            ScraperWorkerKey,
            operation,
            updatedAtUtc: now,
            instanceId: _instanceId));
    }

    public void CompleteOperation(string operationKey, string status = "completed", string? detail = null)
    {
        WorkerOperationInfo? current;
        WorkerOperationInfo? completed;
        var now = DateTime.UtcNow;

        lock (_gate)
        {
            if (!_activeOperations.Remove(operationKey, out var existing))
                existing = _currentOperation is { OperationKey: var currentKey } &&
                    string.Equals(currentKey, operationKey, StringComparison.OrdinalIgnoreCase)
                        ? _currentOperation
                        : null;

            if (existing is null)
                return;

            var descriptor = PhaseProgressCatalog.FindByOperationKey(operationKey);
            var durableView = descriptor is null
                ? null
                : _phaseProgress?.CompletePhase(
                    descriptor.Id,
                    status,
                    warningMessage: status is "skipped" or "deferred" ? detail : null,
                    errorMessage: status is "failed" or "cancelled" ? detail : null);
            completed = CopyOperation(existing,
                status: status,
                detail: detail ?? existing.Detail,
                updatedAtUtc: now,
                endedAtUtc: now,
                elapsedSeconds: (now - existing.StartedAtUtc).TotalSeconds,
                durableProgress: durableView);

            _lastOperation = completed;

            if (_currentOperation is not null &&
                string.Equals(_currentOperation.OperationKey, operationKey, StringComparison.OrdinalIgnoreCase))
            {
                _currentOperation = _activeOperations.Values.OrderByDescending(o => o.UpdatedAtUtc).FirstOrDefault();
            }

            current = _currentOperation;
        }

        TryPublish(() => _metaDb.UpdateWorkerActivity(
            ScraperWorkerKey,
            current,
            completed,
            status: "running",
            updatedAtUtc: now,
            instanceId: _instanceId));

        if (string.Equals(operationKey, "scrape.pass", StringComparison.OrdinalIgnoreCase))
            _phaseProgress?.EndScrape(detail);
    }

    public void FailOperation(string operationKey, Exception? ex = null, string? detail = null)
        => CompleteOperation(operationKey, "failed", detail ?? ex?.Message);

    public void ApplyDurableProgress(DurablePhaseProgressView view)
    {
        WorkerOperationInfo? operation;
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_currentOperation is null)
                return;
            operation = CopyOperation(
                _currentOperation,
                updatedAtUtc: view.LastProgressAtUtc,
                elapsedSeconds: (now - _currentOperation.StartedAtUtc).TotalSeconds,
                durableProgress: view);
            _currentOperation = operation;
            _activeOperations[operation.OperationKey] = operation;
        }

        TryPublish(() => _metaDb.UpdateWorkerActivity(
            ScraperWorkerKey,
            operation,
            updatedAtUtc: now,
            instanceId: _instanceId));
    }

    private static WorkerOperationInfo CopyOperation(WorkerOperationInfo source,
        string? operationLabel = null,
        string? status = null,
        string? phase = null,
        string? subOperation = null,
        string? detail = null,
        DateTime? updatedAtUtc = null,
        DateTime? endedAtUtc = null,
        double? progressPercent = null,
        double? elapsedSeconds = null,
        double? estimatedRemainingSeconds = null,
        DurablePhaseProgressView? durableProgress = null,
        DateTime? heartbeatAtUtc = null)
        => new()
        {
            ContractVersion = 2,
            OperationKey = source.OperationKey,
            OperationLabel = operationLabel ?? source.OperationLabel,
            Status = status ?? source.Status,
            Phase = phase ?? source.Phase,
            SubOperation = subOperation ?? source.SubOperation,
            Detail = detail ?? source.Detail,
            StartedAtUtc = source.StartedAtUtc,
            UpdatedAtUtc = updatedAtUtc ?? source.UpdatedAtUtc,
            EndedAtUtc = endedAtUtc ?? source.EndedAtUtc,
            ProgressPercent = progressPercent ?? source.ProgressPercent,
            ElapsedSeconds = elapsedSeconds ?? source.ElapsedSeconds,
            EstimatedRemainingSeconds = estimatedRemainingSeconds ?? source.EstimatedRemainingSeconds,
            OperationId = durableProgress is null ? source.OperationId : durableProgress.OperationId,
            PhaseId = durableProgress is null ? source.PhaseId : durableProgress.PhaseId,
            PhaseStatus = durableProgress is null ? source.PhaseStatus : durableProgress.PhaseStatus,
            SubphaseId = durableProgress is null ? source.SubphaseId : durableProgress.SubphaseId,
            PhasePlanVersion = durableProgress is null ? source.PhasePlanVersion : durableProgress.PlanVersion,
            PhaseOrdinal = durableProgress is null ? source.PhaseOrdinal : durableProgress.PhaseOrdinal,
            PhaseAttempt = durableProgress is null ? source.PhaseAttempt : durableProgress.Attempt,
            UnitsKind = durableProgress is null ? source.UnitsKind : durableProgress.UnitsKind,
            UnitsCompleted = durableProgress is null ? source.UnitsCompleted : durableProgress.UnitsCompleted,
            UnitsTotal = durableProgress is null ? source.UnitsTotal : durableProgress.UnitsTotal,
            UnitsTotalFinal = durableProgress is null ? source.UnitsTotalFinal : durableProgress.UnitsTotalFinal,
            PhasePercent = durableProgress is null ? source.PhasePercent : durableProgress.PhasePercent,
            OverallPercentKind = durableProgress is null ? source.OverallPercentKind : durableProgress.OverallPercentKind,
            OverallPercent = durableProgress is null ? source.OverallPercent : durableProgress.OverallPercent,
            OverallModelVersion = durableProgress is null ? source.OverallModelVersion : durableProgress.OverallModelVersion,
            EtaLowerSeconds = durableProgress is null ? source.EtaLowerSeconds : durableProgress.EtaLowerSeconds,
            EtaUpperSeconds = durableProgress is null ? source.EtaUpperSeconds : durableProgress.EtaUpperSeconds,
            EtaConfidence = durableProgress is null ? source.EtaConfidence : durableProgress.EtaConfidence,
            EtaSampleCount = durableProgress is null ? source.EtaSampleCount : durableProgress.EtaSampleCount,
            LastProgressAtUtc = durableProgress is null ? source.LastProgressAtUtc : durableProgress.LastProgressAtUtc,
            HeartbeatAtUtc = heartbeatAtUtc ?? source.HeartbeatAtUtc,
            SubphaseProgress = durableProgress is null
                ? source.SubphaseProgress
                : durableProgress.SubphaseProgress,
        };

    private void TryPublish(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Failed to publish worker status update. Continuing without blocking scraper work.");
        }
    }
}

public sealed class WorkerStatusHeartbeatService : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private readonly WorkerStatusPublisher _publisher;

    public WorkerStatusHeartbeatService(WorkerStatusPublisher publisher)
    {
        _publisher = publisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _publisher.PublishHeartbeat("starting", "Worker service starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            _publisher.PublishHeartbeat("running");

            try
            {
                await Task.Delay(HeartbeatInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _publisher.PublishHeartbeat("stopping", "Worker service stopping");
        await base.StopAsync(cancellationToken);
        _publisher.MarkOffline("Worker service stopped");
    }
}