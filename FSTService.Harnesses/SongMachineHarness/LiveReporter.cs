using System.Diagnostics;
using FSTService.Scraping;

namespace SongMachineHarness;

/// <summary>
/// Prints real-time DOP/RPS utilization stats to the console every N seconds.
/// Reads from the DopSampler and InstrumentedQuerier while the machine is running.
/// </summary>
public sealed class LiveReporter : IDisposable
{
    private readonly AdaptiveConcurrencyLimiter _limiter;
    private readonly InstrumentedQuerier _querier;
    private readonly ResilientHttpExecutor? _executor;
    private readonly Stopwatch _sw;
    private readonly int _configuredDop;
    private readonly int _configuredRps;
    private readonly int _intervalMs;
    private readonly int _totalSongs;
    private readonly int _totalAccounts;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;

    // Rolling window for RPS calculation
    private long _lastTotalRequests;
    private long _lastTimestampMs;
    private long _lastWireCount;

    // Cumulative tracking
    private int _lastEventCount;
    private long _peakInFlight;

    // Song progress tracking
    private readonly HashSet<string> _completedSongs = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastProgressLine;

    public LiveReporter(
        AdaptiveConcurrencyLimiter limiter,
        InstrumentedQuerier querier,
        Stopwatch sw,
        int configuredDop,
        int configuredRps,
        int intervalMs = 3000,
        ResilientHttpExecutor? executor = null,
        int totalSongs = 0,
        int totalAccounts = 0)
    {
        _limiter = limiter;
        _querier = querier;
        _sw = sw;
        _configuredDop = configuredDop;
        _configuredRps = configuredRps;
        _intervalMs = intervalMs;
        _executor = executor;
        _totalSongs = totalSongs;
        _totalAccounts = totalAccounts;
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
            $"{"Time",7} │ {"InFlight",8} {"DOP",5} {"Util%",6} │ " +
            $"{"RPS",6} {"RPSCfg",6} │ {"Calls",7} {"OK",6} {"Fail",5} {"Pages",5} │ " +
            $"{"P50ms",6} {"P95ms",6} │ {"Peak",5} │ {"Wire",6} {"WrRPS",6} {"CDN",4} │ {"ssth",5} {"RtTkn",5}");
        Console.WriteLine(new string('─', 127));

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

        // Call stats from events since last report
        var events = _querier.Events;
        var allEvents = events.ToArray(); // snapshot
        var newEvents = allEvents.Skip(_lastEventCount).ToArray();
        _lastEventCount = allEvents.Length;

        var okCount = newEvents.Count(e => e.Success);
        var failCount = newEvents.Count(e => !e.Success);
        var pagesCount = newEvents.Sum(e => e.PaginationPages);

        // Duration percentiles from new events
        var durations = newEvents.Where(e => e.Success && e.DurationMs > 0)
            .Select(e => e.DurationMs).OrderBy(d => d).ToArray();

        long p50 = 0, p95 = 0;
        if (durations.Length > 0)
        {
            p50 = Percentile(durations, 0.50);
            p95 = Percentile(durations, 0.95);
        }

        // Time string
        var timeStr = elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:D2}s"
            : $"{elapsed.TotalSeconds:F0}s";

        // CDN wire stats
        long wireSends = _executor?.TotalHttpSends ?? 0;
        long cdnBlocks = _executor?.CdnBlocksDetected ?? 0;

        // Wire RPS (actual HTTP sends per second — what the CDN sees)
        var dWire = wireSends - _lastWireCount;
        var wireRps = dtMs > 0 ? dWire * 1000.0 / dtMs : 0;
        _lastWireCount = wireSends;

        // TCP slow-start threshold and rate token headroom
        var ssthresh = _limiter.SlowStartThreshold;
        var rateTkn = _limiter.RateTokensAvailable;

        Console.WriteLine(
            $"{timeStr,7} │ {inFlight,8} {currentDop,5} {util,5:F1}% │ " +
            $"{rps,6:F0} {_configuredRps,6} │ {allEvents.Length,7} {okCount,6} {failCount,5} {pagesCount,5} │ " +
            $"{p50,6} {p95,6} │ {_peakInFlight,5} │ {wireSends,6} {wireRps,6:F0} {cdnBlocks,4} │ {ssthresh,5} {rateTkn,5}");

        // ── Song progress line ──
        if (_totalSongs > 0)
        {
            // Compute song-level progress from all events
            var songInstrumentPairs = new HashSet<(string Song, string Inst)>();
            var alltimeResultsByUser = 0;
            int minSeason = int.MaxValue, maxSeason = int.MinValue;
            int seasonalCalls = 0;

            foreach (var evt in allEvents)
            {
                songInstrumentPairs.Add((evt.SongId, evt.Instrument));

                if (evt.Type == "alltime" && evt.Success)
                    alltimeResultsByUser += evt.ResultCount;
                if (evt.Type == "seasonal" && evt.Success)
                {
                    seasonalCalls++;
                    // Parse season number from prefix like "season013" or "evergreen"
                    if (evt.Season is not null && evt.Season.StartsWith("season") &&
                        int.TryParse(evt.Season.AsSpan(6), out var sn))
                    {
                        if (sn < minSeason) minSeason = sn;
                        if (sn > maxSeason) maxSeason = sn;
                    }
                    else if (evt.Season == "evergreen")
                    {
                        if (1 < minSeason) minSeason = 1;
                        if (1 > maxSeason) maxSeason = 1;
                    }
                }
            }

            // A song is "complete" when it has successful events across all 6 instruments
            // and no recent activity (events stopped arriving).
            // During CDN blocks, failed events don't count — the song will be retried.
            var songGroups = allEvents.GroupBy(e => e.SongId);
            int songsComplete = 0;
            foreach (var grp in songGroups)
            {
                var successfulInstruments = grp
                    .Where(e => e.Success)
                    .Select(e => e.Instrument)
                    .Distinct()
                    .Count();

                if (successfulInstruments >= 4) // 4 pad instruments minimum (Pro may 404)
                {
                    var lastEventMs = grp.Max(e => e.EndMs);
                    if (lastEventMs > 0 && nowMs - lastEventMs > _intervalMs)
                        _completedSongs.Add(grp.Key);
                }
            }
            songsComplete = _completedSongs.Count;

            var seasonRange = minSeason <= maxSeason
                ? $"S{minSeason}-S{maxSeason}"
                : "—";

            // ETA based on songs completed
            var elapsedSec = elapsed.TotalSeconds;
            string eta = "—";
            if (songsComplete > 0 && songsComplete < _totalSongs)
            {
                var secPerSong = elapsedSec / songsComplete;
                var remainingSec = secPerSong * (_totalSongs - songsComplete);
                var etaSpan = TimeSpan.FromSeconds(remainingSec);
                eta = etaSpan.TotalHours >= 1
                    ? $"{(int)etaSpan.TotalHours}h{etaSpan.Minutes:D2}m"
                    : $"{(int)etaSpan.TotalMinutes}m{etaSpan.Seconds:D2}s";
            }
            else if (songsComplete >= _totalSongs)
            {
                eta = "done";
            }

            var progressLine = $"        Songs: {songsComplete}/{_totalSongs} | {seasonRange} | " +
                               $"Alltime hits: {alltimeResultsByUser} | Seasonal: {seasonalCalls} | ETA: {eta}";

            if (progressLine != _lastProgressLine)
            {
                Console.WriteLine(progressLine);
                _lastProgressLine = progressLine;
            }
        }
    }

    private static long Percentile(long[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }

    public void Dispose() => _cts.Dispose();
}
