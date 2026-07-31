namespace FSTService.Scraping;

/// <summary>
/// Coordinates best-effort background maintenance with the scrape lifecycle.
/// Background jobs are allowed to pause/cancel when a new scrape starts so they
/// never create an unbounded backlog that blocks current data freshness.
/// </summary>
public sealed class BackgroundWorkCoordinator
{
    private readonly object _gate = new();
    private CancellationTokenSource _backgroundCts = new();
    private TaskCompletionSource _quiesced = CompletedQuiescence();
    private int _activeOperations;
    private volatile bool _scrapeRunning;

    public bool ScrapeRunning => _scrapeRunning;

    public CancellationToken BackgroundToken
    {
        get
        {
            lock (_gate)
                return _backgroundCts.Token;
        }
    }

    public void RequestPauseForScrape()
    {
        lock (_gate)
        {
            _scrapeRunning = true;
            if (!_backgroundCts.IsCancellationRequested)
                _backgroundCts.Cancel();
        }
    }

    public bool TryBeginBackgroundOperation(out IDisposable? lease)
    {
        lock (_gate)
        {
            if (_scrapeRunning)
            {
                lease = null;
                return false;
            }

            if (_activeOperations++ == 0)
            {
                _quiesced = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            lease = new BackgroundOperationLease(this);
            return true;
        }
    }

    public Task WaitForBackgroundQuiescenceAsync(CancellationToken ct = default)
    {
        Task quiescence;
        lock (_gate)
            quiescence = _quiesced.Task;
        return quiescence.WaitAsync(ct);
    }

    public void ResumeAfterScrape()
    {
        lock (_gate)
        {
            _scrapeRunning = false;
            _backgroundCts.Dispose();
            _backgroundCts = new CancellationTokenSource();
        }
    }

    private void EndBackgroundOperation()
    {
        lock (_gate)
        {
            if (_activeOperations <= 0)
                return;

            _activeOperations--;
            if (_activeOperations == 0)
                _quiesced.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedQuiescence()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }

    private sealed class BackgroundOperationLease(
        BackgroundWorkCoordinator owner) : IDisposable
    {
        private BackgroundWorkCoordinator? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.EndBackgroundOperation();
    }
}
