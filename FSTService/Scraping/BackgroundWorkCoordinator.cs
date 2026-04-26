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

    public void ResumeAfterScrape()
    {
        lock (_gate)
        {
            _scrapeRunning = false;
            _backgroundCts.Dispose();
            _backgroundCts = new CancellationTokenSource();
        }
    }
}
