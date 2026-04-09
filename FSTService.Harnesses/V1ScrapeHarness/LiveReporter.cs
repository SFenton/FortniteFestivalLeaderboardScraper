using System.Diagnostics;
using FSTService.Scraping;

namespace V1ScrapeHarness;

/// <summary>
/// Prints real-time multi-layer timing stats to the console every N seconds.
/// Shows DOP utilization, slot wait, rate token wait, HTTP wire time, 
/// persistence time, and thread pool metrics.
/// </summary>
public sealed class LiveReporter : IDisposable
{
    private readonly AdaptiveConcurrencyLimiter _limiter;
    private readonly TimingCollector _collector;
    private readonly ResilientHttpExecutor? _executor;
    private readonly ScrapeProgressTracker _progress;
    private readonly Stopwatch _sw;
    private readonly int _configuredDop;
    private readonly int _configuredRps;
    private readonly int _intervalMs;
    private readonly int _totalSongs;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;

    // Rolling window for RPS calculation
    private long _lastTotalRequests;
    private long _lastTimestampMs;
    private long _peakInFlight;

    public LiveReporter(
        AdaptiveConcurrencyLimiter limiter,
        TimingCollector collector,
        ScrapeProgressTracker progress,
        Stopwatch sw,
        int configuredDop,
        int configuredRps,
        int intervalMs = 3000,
        ResilientHttpExecutor? executor = null,
        int totalSongs = 0)
    {
        _limiter = limiter;
        _collector = collector;
        _progress = progress;
        _sw = sw;
        _configuredDop = configuredDop;
        _configuredRps = configuredRps;
        _intervalMs = intervalMs;
        _executor = executor;
        _totalSongs = totalSongs;
    }

    public void Start()
    {
        _lastTimestampMs = _sw.ElapsedMilliseconds;
        _lastTotalRequests = _limiter.TotalRequests;
        _task = Task.Run(ReportLoopAsync);
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_task is not null)
        {
            try { await _task; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task ReportLoopAsync()
    {
        var ct = _cts.Token;

        // Print header
        Console.WriteLine();
        Console.WriteLine(
            $"{"Time",7} │ {"IF",4} {"DOP",4} {"Ut%",4} │ " +
            $"{"RPS",5} │ {"SlotP50",7} {"SlotP95",7} │ " +
            $"{"RateP50",7} {"RateP95",7} │ " +
            $"{"WireP50",7} {"WireP95",7} │ " +
            $"{"CDN",4} │ {"TPPend",6} {"TPCnt",5} │ " +
            $"{"RtTkn",5} {"ssth",5}");
        Console.WriteLine(new string('─', 126));

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_intervalMs, ct); }
            catch (OperationCanceledException) { break; }

            PrintLine();
        }
    }

    private void PrintLine()
    {
        var nowMs = _sw.ElapsedMilliseconds;
        var elapsed = TimeSpan.FromMilliseconds(nowMs);

        // Pool state
        var inFlight = _limiter.InFlight;
        var currentDop = _limiter.CurrentDop;
        var totalReqs = _limiter.TotalRequests;
        var util = currentDop > 0 ? (double)inFlight / currentDop * 100 : 0;

        if (inFlight > _peakInFlight) _peakInFlight = inFlight;

        // RPS over this interval
        var dtMs = nowMs - _lastTimestampMs;
        var dReqs = totalReqs - _lastTotalRequests;
        var rps = dtMs > 0 ? dReqs * 1000.0 / dtMs : 0;
        _lastTimestampMs = nowMs;
        _lastTotalRequests = totalReqs;

        // Slot wait percentiles (recent window)
        var (slotP50, slotP95) = _collector.GetRecentSlotWaitPercentiles();

        // Rate token wait percentiles (recent window)
        var (rateP50, rateP95) = _collector.GetRecentRateTokenWaitPercentiles();

        // HTTP wire time percentiles (recent window)
        var (wireP50, wireP95) = _collector.GetRecentHttpPercentiles();

        // CDN/wire stats
        long wireSends = _executor?.TotalHttpSends ?? 0;
        long cdnBlocks = _executor?.CdnBlocksDetected ?? 0;

        // Thread pool
        var tpPending = ThreadPool.PendingWorkItemCount;
        var tpCount = ThreadPool.ThreadCount;

        // Rate tokens & slow start
        var rateTkn = _limiter.RateTokensAvailable;
        var ssthresh = _limiter.SlowStartThreshold;

        var timeStr = elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:D2}s"
            : $"{elapsed.TotalSeconds:F0}s";

        Console.WriteLine(
            $"{timeStr,7} │ {inFlight,4} {currentDop,4} {util,3:F0}% │ " +
            $"{rps,5:F0} │ {slotP50,5}ms {slotP95,5}ms │ " +
            $"{rateP50,5}ms {rateP95,5}ms │ " +
            $"{wireP50,5}ms {wireP95,5}ms │ " +
            $"{cdnBlocks,4} │ {tpPending,6} {tpCount,5} │ " +
            $"{rateTkn,5} {ssthresh,5}");

        // Song progress line
        if (_totalSongs > 0)
        {
            var snap = _progress.GetProgressResponse();
            var current = snap.Current;
            if (current is not null)
            {
                var completedSongs = current.Songs?.Completed ?? 0;
                var totalPages = current.Pages?.Fetched ?? 0;
                var totalBytes = current.BytesReceived;

                var elapsedSec = elapsed.TotalSeconds;
                string eta = "—";
                if (completedSongs > 0 && completedSongs < _totalSongs)
                {
                    var secPerSong = elapsedSec / completedSongs;
                    var remainingSec = secPerSong * (_totalSongs - completedSongs);
                    var etaSpan = TimeSpan.FromSeconds(remainingSec);
                    eta = etaSpan.TotalHours >= 1
                        ? $"{(int)etaSpan.TotalHours}h{etaSpan.Minutes:D2}m"
                        : $"{(int)etaSpan.TotalMinutes}m{etaSpan.Seconds:D2}s";
                }

                Console.WriteLine(
                    $"        Songs: {completedSongs}/{_totalSongs} | " +
                    $"Pages: {totalPages:N0} | " +
                    $"Bytes: {totalBytes:N0} | " +
                    $"ETA: {eta}");
            }
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
    }
}
