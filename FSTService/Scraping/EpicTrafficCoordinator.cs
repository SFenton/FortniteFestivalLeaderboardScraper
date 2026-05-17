namespace FSTService.Scraping;

public enum EpicTrafficKind
{
    Background,
    ForegroundRegistration,
}

/// <summary>
/// Coordinates foreground registration sync with background Epic traffic.
/// Foreground registration leases make new background Epic requests wait while
/// already in-flight requests are allowed to finish through their normal path.
/// </summary>
public sealed class EpicTrafficCoordinator
{
    private readonly object _lock = new();
    private readonly AsyncLocal<EpicTrafficKind> _currentKind = new();
    private TaskCompletionSource<bool>? _backgroundResume;
    private int _foregroundRegistrationLeases;

    public bool ForegroundRegistrationActive
    {
        get
        {
            lock (_lock)
                return _foregroundRegistrationLeases > 0;
        }
    }

    public EpicTrafficKind CurrentKind => _currentKind.Value;

    public IDisposable BeginForegroundRegistration()
    {
        lock (_lock)
        {
            if (_foregroundRegistrationLeases++ == 0)
            {
                _backgroundResume = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return new ForegroundRegistrationLease(this);
    }

    public IDisposable BeginRequest(EpicTrafficKind kind)
    {
        var previousKind = _currentKind.Value;
        _currentKind.Value = kind;
        return new RequestScope(this, previousKind);
    }

    public Task WaitForTurnAsync(CancellationToken ct)
        => _currentKind.Value == EpicTrafficKind.ForegroundRegistration
            ? Task.CompletedTask
            : WaitForBackgroundEpicAsync(ct);

    public Task WaitForBackgroundEpicAsync(CancellationToken ct)
    {
        Task? waitTask;
        lock (_lock)
        {
            waitTask = _foregroundRegistrationLeases > 0
                ? _backgroundResume?.Task ?? Task.CompletedTask
                : null;
        }

        return waitTask is null ? Task.CompletedTask : waitTask.WaitAsync(ct);
    }

    private void EndForegroundRegistration()
    {
        TaskCompletionSource<bool>? resume = null;
        lock (_lock)
        {
            if (_foregroundRegistrationLeases <= 0)
                return;

            if (--_foregroundRegistrationLeases == 0)
            {
                resume = _backgroundResume;
                _backgroundResume = null;
            }
        }

        resume?.TrySetResult(true);
    }

    private sealed class ForegroundRegistrationLease : IDisposable
    {
        private EpicTrafficCoordinator? _owner;

        public ForegroundRegistrationLease(EpicTrafficCoordinator owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.EndForegroundRegistration();
        }
    }

    private sealed class RequestScope : IDisposable
    {
        private readonly EpicTrafficCoordinator _owner;
        private readonly EpicTrafficKind _previousKind;
        private bool _disposed;

        public RequestScope(EpicTrafficCoordinator owner, EpicTrafficKind previousKind)
        {
            _owner = owner;
            _previousKind = previousKind;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _owner._currentKind.Value = _previousKind;
            _disposed = true;
        }
    }
}