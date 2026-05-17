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
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkerOperationInfo> _activeOperations = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _instanceId;
    private readonly DateTime _startedAtUtc;
    private WorkerOperationInfo? _currentOperation;
    private WorkerOperationInfo? _lastOperation;

    public WorkerStatusPublisher(IMetaDatabase metaDb, ILogger<WorkerStatusPublisher> log)
    {
        _metaDb = metaDb;
        _log = log;
        _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        _startedAtUtc = DateTime.UtcNow;
    }

    public void PublishHeartbeat(string status = "running", string? message = null)
    {
        TryPublish(() => _metaDb.UpsertWorkerHeartbeat(
            ScraperWorkerKey,
            status,
            mode: "scraper",
            instanceId: _instanceId,
            startedAtUtc: _startedAtUtc,
            heartbeatAtUtc: DateTime.UtcNow,
            message));
    }

    public void MarkOffline(string? message = null)
        => PublishHeartbeat("offline", message ?? "Worker stopped");

    public void BeginOperation(string operationKey, string operationLabel,
        string? phase = null, string? subOperation = null, string? detail = null,
        double? progressPercent = null)
    {
        var now = DateTime.UtcNow;
        var operation = new WorkerOperationInfo
        {
            OperationKey = operationKey,
            OperationLabel = operationLabel,
            Status = "running",
            Phase = phase,
            SubOperation = subOperation,
            Detail = detail,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            ProgressPercent = progressPercent,
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
            updatedAtUtc: now));
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

            operation = CopyOperation(existing,
                operationLabel: operationLabel ?? existing.OperationLabel,
                phase: phase ?? existing.Phase,
                subOperation: subOperation ?? existing.SubOperation,
                detail: detail ?? existing.Detail,
                progressPercent: progressPercent ?? existing.ProgressPercent,
                estimatedRemainingSeconds: estimatedRemainingSeconds ?? existing.EstimatedRemainingSeconds,
                updatedAtUtc: now,
                elapsedSeconds: (now - existing.StartedAtUtc).TotalSeconds);

            _activeOperations[operationKey] = operation;
            _currentOperation = operation;
        }

        TryPublish(() => _metaDb.UpdateWorkerActivity(
            ScraperWorkerKey,
            operation,
            updatedAtUtc: now));
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

            completed = CopyOperation(existing,
                status: status,
                detail: detail ?? existing.Detail,
                updatedAtUtc: now,
                endedAtUtc: now,
                elapsedSeconds: (now - existing.StartedAtUtc).TotalSeconds);

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
            updatedAtUtc: now));
    }

    public void FailOperation(string operationKey, Exception? ex = null, string? detail = null)
        => CompleteOperation(operationKey, "failed", detail ?? ex?.Message);

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
        double? estimatedRemainingSeconds = null)
        => new()
        {
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