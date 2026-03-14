using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PercentileService;

/// <summary>
/// BackgroundService that runs the percentile scrape once per day
/// at the configured time of day (default: 3:30 AM PST).
/// </summary>
public sealed class PercentileScrapeWorker : BackgroundService
{
    private readonly EpicTokenManager _tokenManager;
    private readonly LeaderboardQuerier _querier;
    private readonly FstClient _fstClient;
    private readonly PercentileOptions _opts;
    private readonly ILogger<PercentileScrapeWorker> _log;
    private readonly PercentileScrapeProgressTracker _progress;

    public PercentileScrapeWorker(
        EpicTokenManager tokenManager,
        LeaderboardQuerier querier,
        FstClient fstClient,
        IOptions<PercentileOptions> opts,
        ILogger<PercentileScrapeWorker> log,
        PercentileScrapeProgressTracker progress)
    {
        _tokenManager = tokenManager;
        _querier = querier;
        _fstClient = fstClient;
        _opts = opts.Value;
        _log = log;
        _progress = progress;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for TokenRefreshWorker to complete first auth
        await Task.Delay(TimeSpan.FromSeconds(_opts.InitialDelaySeconds), stoppingToken);

        _log.LogInformation("PercentileScrapeWorker started. Scheduled at {Time} {TZ}.",
            _opts.ScrapeTimeOfDay, _opts.ScrapeTimeZone);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = CalculateDelayUntilNextRun();
            _log.LogInformation("Next percentile scrape in {Delay:g} (at {Time:u}).",
                delay, DateTime.UtcNow.Add(delay));

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await RunScrapeAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Percentile scrape failed.");
            }
        }
    }

    internal TimeSpan CalculateDelayUntilNextRun()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(_opts.ScrapeTimeZone);
        var nowUtc = DateTime.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);

        if (!TimeSpan.TryParse(_opts.ScrapeTimeOfDay, out var targetTime))
            targetTime = new TimeSpan(3, 30, 0);

        var targetToday = nowLocal.Date.Add(targetTime);
        if (targetToday <= nowLocal)
            targetToday = targetToday.AddDays(1);

        var targetUtc = TimeZoneInfo.ConvertTimeToUtc(targetToday, tz);
        return targetUtc - nowUtc;
    }

    internal async Task RunScrapeAsync(CancellationToken ct)
    {
        _log.LogInformation("Starting percentile scrape...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. Get player entries from FSTService
        var entries = await _fstClient.GetPlayerEntriesAsync(_opts.AccountId, ct);
        _log.LogInformation("Found {Count} song/instrument entries to query.", entries.Count);

        if (entries.Count == 0)
        {
            _log.LogWarning("No entries found for account {AccountId}. Skipping.", _opts.AccountId);
            return;
        }

        // 2. Set up progress tracking
        _progress.BeginScrape(entries.Count);

        // 3. Set up adaptive concurrency limiter
        using var limiter = new AdaptiveConcurrencyLimiter(
            _opts.StartingDegreeOfParallelism,
            minDop: _opts.MinDegreeOfParallelism,
            maxDop: _opts.MaxDegreeOfParallelism,
            _log);
        _progress.SetAdaptiveLimiter(limiter);

        _log.LogInformation(
            "Adaptive DOP: starting at {StartDop}, range [{MinDop}–{MaxDop}].",
            _opts.StartingDegreeOfParallelism, _opts.MinDegreeOfParallelism, _opts.MaxDegreeOfParallelism);

        // 4. Query V1 API for each entry (with adaptive parallelism)
        var results = new List<LeaderboardPopulationItem>();
        var resultsLock = new object();
        var tasks = new List<Task>();

        foreach (var entry in entries)
        {
            await limiter.WaitAsync(ct);
            tasks.Add(QueryWithAdaptiveLimiter(entry.SongId, entry.Instrument, limiter, results, resultsLock, ct));
        }

        await Task.WhenAll(tasks);

        var progressSnapshot = _progress.GetProgressResponse();
        _progress.EndScrape();

        _log.LogInformation(
            "V1 queries complete: {Succeeded} populated, {Skipped} skipped, {Failed} failed (of {Total}). Final DOP: {Dop}.",
            progressSnapshot.Entries?.Succeeded ?? 0,
            progressSnapshot.Entries?.Skipped ?? 0,
            progressSnapshot.Entries?.Failed ?? 0,
            entries.Count,
            limiter.CurrentDop);

        // 5. POST results to FSTService
        if (results.Count > 0)
        {
            await _fstClient.PostLeaderboardPopulationAsync(results, ct);
        }

        sw.Stop();
        _log.LogInformation("Percentile scrape completed in {Elapsed:g}. Submitted {Count} entries.",
            sw.Elapsed, results.Count);
    }

    internal async Task QueryWithAdaptiveLimiter(
        string songId, string instrument,
        AdaptiveConcurrencyLimiter limiter,
        List<LeaderboardPopulationItem> results,
        object resultsLock,
        CancellationToken ct)
    {
        try
        {
            var token = _tokenManager.AccessToken
                ?? throw new InvalidOperationException("No access token available.");

            var result = await _querier.QueryAsync(songId, instrument, _opts.AccountId, token, ct);

            if (result is null)
            {
                _log.LogDebug("Skipped {SongId}/{Instrument}: no score on leaderboard.", songId, instrument);
                limiter.ReportSuccess(); // Not an API error
                _progress.ReportSkipped();
                return;
            }

            if (result.TotalEntries <= 0)
            {
                _log.LogDebug("Failed {SongId}/{Instrument}: could not derive population (percentile={Percentile}).",
                    songId, instrument, result.Percentile);
                limiter.ReportSuccess(); // Not an API error
                _progress.ReportFailed(songId, instrument, "Could not derive population");
                return;
            }

            _log.LogDebug(
                "OK {SongId}/{Instrument}: rank={Rank}, population={Population}, percentile={Percentile:P4}.",
                songId, instrument, result.Rank, result.TotalEntries, result.Percentile);

            limiter.ReportSuccess();
            _progress.ReportSuccess();

            lock (resultsLock)
            {
                results.Add(new LeaderboardPopulationItem
                {
                    SongId = result.SongId,
                    Instrument = result.Instrument,
                    TotalEntries = result.TotalEntries,
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Error querying {SongId}/{Instrument}.", songId, instrument);
            limiter.ReportFailure();
            _progress.ReportFailed(songId, instrument, ex.Message);
        }
        finally
        {
            limiter.Release();
        }
    }
}

