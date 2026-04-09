using System.Text;
using System.Text.Json;

namespace V1ScrapeHarness;

/// <summary>
/// Generates post-run diagnostic reports with multi-layer timing breakdown.
/// </summary>
public sealed class HarnessReport
{
    private readonly IReadOnlyList<DopSample> _samples;
    private readonly TimingCollector _collector;
    private readonly TimeSpan _elapsed;
    private readonly int _configuredDop;
    private readonly int _configuredRps;
    private readonly int _songCount;
    private readonly int _maxPages;
    private readonly bool _sequential;
    private readonly bool _noPersist;
    private readonly long _totalWireSends;
    private readonly long _cdnBlocks;
    private readonly int _songsCompleted;
    private readonly int _leaderboardsCompleted;
    private readonly long _totalBytes;
    private readonly int _totalPages;

    public HarnessReport(
        IReadOnlyList<DopSample> samples,
        TimingCollector collector,
        TimeSpan elapsed,
        int configuredDop,
        int configuredRps,
        int songCount,
        int maxPages,
        bool sequential,
        bool noPersist,
        long totalWireSends,
        long cdnBlocks,
        int songsCompleted,
        int leaderboardsCompleted,
        long totalBytes,
        int totalPages)
    {
        _samples = samples;
        _collector = collector;
        _elapsed = elapsed;
        _configuredDop = configuredDop;
        _configuredRps = configuredRps;
        _songCount = songCount;
        _maxPages = maxPages;
        _sequential = sequential;
        _noPersist = noPersist;
        _totalWireSends = totalWireSends;
        _cdnBlocks = cdnBlocks;
        _songsCompleted = songsCompleted;
        _leaderboardsCompleted = leaderboardsCompleted;
        _totalBytes = totalBytes;
        _totalPages = totalPages;
    }

    public void WriteAll(string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        var summary = BuildSummary();
        Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(outputDir, "summary.txt"), summary);

        WriteDopSamples(Path.Combine(outputDir, "dop-samples.csv"));
        WriteTimingSamples(Path.Combine(outputDir, "timing-samples.csv"));
        WriteBottleneckAnalysis(Path.Combine(outputDir, "bottleneck-analysis.txt"));

        Console.WriteLine();
        Console.WriteLine($"Output written to: {Path.GetFullPath(outputDir)}");
        foreach (var f in new[] { "summary.txt", "dop-samples.csv", "timing-samples.csv", "bottleneck-analysis.txt" })
        {
            var path = Path.Combine(outputDir, f);
            if (File.Exists(path))
                Console.WriteLine($"  {f,-30} ({new FileInfo(path).Length:N0} bytes)");
        }
    }

    private string BuildSummary()
    {
        var sb = new StringBuilder();

        sb.AppendLine("═══════════════════════════════════════════════════════");
        sb.AppendLine("  V1 Scrape Diagnostic Report");
        sb.AppendLine("═══════════════════════════════════════════════════════");
        sb.AppendLine();

        // ── Config ──
        sb.AppendLine("── Configuration ───────────────────────────────────────");
        sb.AppendLine($"  DOP: {_configuredDop}  |  RPS: {(_configuredRps == 0 ? "unlimited" : _configuredRps)}  |  Songs: {_songCount}  |  MaxPages: {(_maxPages == 0 ? "unlimited" : _maxPages)}");
        sb.AppendLine($"  Mode: {(_sequential ? "Sequential" : "Parallel")}  |  Persistence: {(_noPersist ? "OFF" : "ON")}");
        sb.AppendLine();

        // ── Totals ──
        sb.AppendLine("── Totals ──────────────────────────────────────────────");
        sb.AppendLine($"  Elapsed:             {_elapsed.TotalSeconds:F1}s");
        sb.AppendLine($"  Songs completed:     {_songsCompleted}/{_songCount}");
        sb.AppendLine($"  Leaderboards done:   {_leaderboardsCompleted}");
        sb.AppendLine($"  Pages fetched:       {_totalPages:N0}");
        sb.AppendLine($"  Bytes received:      {_totalBytes:N0} ({_totalBytes / 1_048_576.0:F1} MB)");
        sb.AppendLine($"  HTTP wire sends:     {_totalWireSends:N0}");
        sb.AppendLine($"  CDN blocks:          {_cdnBlocks}");
        sb.AppendLine($"  Actual RPS:          {(_elapsed.TotalSeconds > 0 ? _totalPages / _elapsed.TotalSeconds : 0):F1}");
        sb.AppendLine();

        // ── DOP Utilization ──
        if (_samples.Count > 0)
        {
            int peakInFlight = _samples.Max(s => s.InFlight);
            double avgInFlight = _samples.Average(s => s.InFlight);
            double utilization = _configuredDop > 0 ? avgInFlight / _configuredDop * 100 : 0;
            int zeroCount = _samples.Count(s => s.InFlight == 0);
            double zeroPercent = (double)zeroCount / _samples.Count * 100;
            double avgIdle = _samples.Average(s => s.IdleSlots);
            long peakThreadPool = _samples.Max(s => s.ThreadPoolPending);
            double avgThreadPool = _samples.Average(s => s.ThreadPoolPending);

            sb.AppendLine("── DOP Utilization ─────────────────────────────────────");
            sb.AppendLine($"  Configured DOP:    {_configuredDop,-10}  Peak InFlight:    {peakInFlight}");
            sb.AppendLine($"  Avg InFlight:      {avgInFlight,-10:F1}  DOP Utilization:  {utilization:F1}%");
            sb.AppendLine($"  Time at zero:      {zeroPercent,-10:F1}%  Idle slots (avg): {avgIdle:F1}");
            sb.AppendLine();

            sb.AppendLine("── Thread Pool ─────────────────────────────────────────");
            sb.AppendLine($"  Peak pending:      {peakThreadPool,-10}  Avg pending:      {avgThreadPool:F1}");
            sb.AppendLine($"  Peak thread count: {_samples.Max(s => s.ThreadPoolCount),-10}  Avg thread count: {_samples.Average(s => s.ThreadPoolCount):F1}");
            sb.AppendLine();

            // ── InFlight Distribution ──
            var maxBucket = Math.Max(peakInFlight, _configuredDop);
            var buckets = new (string Label, int Min, int Max)[]
            {
                ("0",       0,   0),
                ("1-10",    1,   10),
                ("11-50",   11,  50),
                ("51-100",  51,  100),
                ("101-250", 101, 250),
                ("251-500", 251, 500),
                ("501+",    501, int.MaxValue),
            };

            sb.AppendLine("── InFlight Distribution ───────────────────────────────");
            foreach (var (label, min, max) in buckets)
            {
                int count = _samples.Count(s => s.InFlight >= min && s.InFlight <= max);
                double pct = (double)count / _samples.Count * 100;
                if (count == 0 && min > peakInFlight) continue;
                int barLen = (int)(pct / 100 * 40);
                string bar = new string('█', barLen) + new string('░', 40 - barLen);
                sb.AppendLine($"  {label,-10} {bar}  {pct:F1}%  ({count})");
            }
            sb.AppendLine();
        }

        // ── Slot Wait Timing ──
        var slotWaits = _collector.SlotWaitMs.OrderBy(x => x).ToArray();
        if (slotWaits.Length > 0)
        {
            sb.AppendLine("── Slot Wait (DOP semaphore) ────────────────────────────");
            sb.AppendLine($"  Samples: {slotWaits.Length:N0}  |  Min: {slotWaits[0]}ms  |  Max: {slotWaits[^1]}ms");
            sb.AppendLine($"  P50: {TimingCollector.Percentile(slotWaits, 0.50)}ms  |  P95: {TimingCollector.Percentile(slotWaits, 0.95)}ms  |  P99: {TimingCollector.Percentile(slotWaits, 0.99)}ms");
            sb.AppendLine($"  Mean: {slotWaits.Average():F1}ms");
            sb.AppendLine();
        }

        // ── Rate Token Wait Timing ──
        var rateWaits = _collector.RateTokenWaitMs.OrderBy(x => x).ToArray();
        if (rateWaits.Length > 0)
        {
            sb.AppendLine("── Rate Token Wait (token bucket) ──────────────────────");
            sb.AppendLine($"  Samples: {rateWaits.Length:N0}  |  Min: {rateWaits[0]}ms  |  Max: {rateWaits[^1]}ms");
            sb.AppendLine($"  P50: {TimingCollector.Percentile(rateWaits, 0.50)}ms  |  P95: {TimingCollector.Percentile(rateWaits, 0.95)}ms  |  P99: {TimingCollector.Percentile(rateWaits, 0.99)}ms");
            sb.AppendLine($"  Mean: {rateWaits.Average():F1}ms");
            sb.AppendLine();
        }

        // ── HTTP Wire Timing ──
        var httpSamples = _collector.HttpSamples.ToArray();
        if (httpSamples.Length > 0)
        {
            var wireTimes = httpSamples.Select(s => s.WireMs).OrderBy(x => x).ToArray();
            sb.AppendLine("── HTTP Wire Time ──────────────────────────────────────");
            sb.AppendLine($"  Samples: {wireTimes.Length:N0}  |  Min: {wireTimes[0]}ms  |  Max: {wireTimes[^1]}ms");
            sb.AppendLine($"  P50: {TimingCollector.Percentile(wireTimes, 0.50)}ms  |  P95: {TimingCollector.Percentile(wireTimes, 0.95)}ms  |  P99: {TimingCollector.Percentile(wireTimes, 0.99)}ms");
            sb.AppendLine($"  Mean: {wireTimes.Average():F1}ms");

            // Status code distribution
            var byCodes = httpSamples.GroupBy(s => s.StatusCode).OrderByDescending(g => g.Count());
            sb.AppendLine($"  Status codes:");
            foreach (var grp in byCodes)
                sb.AppendLine($"    {grp.Key}: {grp.Count()} ({(double)grp.Count() / httpSamples.Length * 100:F1}%)");
            sb.AppendLine();
        }

        // ── Persistence Timing ──
        var persistSamples = _collector.PersistSamples.ToArray();
        if (persistSamples.Length > 0)
        {
            var persistTimes = persistSamples.Select(s => s.TotalMs).OrderBy(x => x).ToArray();
            sb.AppendLine("── Persistence Timing ──────────────────────────────────");
            sb.AppendLine($"  Samples: {persistTimes.Length:N0}  |  Min: {persistTimes[0]}ms  |  Max: {persistTimes[^1]}ms");
            sb.AppendLine($"  P50: {TimingCollector.Percentile(persistTimes, 0.50)}ms  |  P95: {TimingCollector.Percentile(persistTimes, 0.95)}ms  |  P99: {TimingCollector.Percentile(persistTimes, 0.99)}ms");
            sb.AppendLine($"  Mean: {persistTimes.Average():F1}ms");
            sb.AppendLine();
        }

        // ── Timeline Phases (10s windows) ──
        if (_samples.Count > 0)
        {
            long totalMs = _samples[^1].TimestampMs;
            int windowSizeMs = 10_000;

            sb.AppendLine("── Timeline (10s windows) ──────────────────────────────");
            sb.AppendLine($"  {"Window",-10} {"AvgIF",6} {"PeakIF",6} {"AvgDOP",6} {"RPS",6} {"SlotP50",8} {"WireP50",8} {"TPPend",6}");

            for (long start = 0; start < totalMs; start += windowSizeMs)
            {
                long end = start + windowSizeMs;
                var windowSamples = _samples.Where(s => s.TimestampMs >= start && s.TimestampMs < end).ToList();
                if (windowSamples.Count == 0) continue;

                double avgIF = windowSamples.Average(s => s.InFlight);
                int peakIF = windowSamples.Max(s => s.InFlight);
                double avgDop = windowSamples.Average(s => s.CurrentDop);
                double windowRps = (windowSamples[^1].TotalRequests - windowSamples[0].TotalRequests) / ((double)windowSizeMs / 1000);
                double avgTP = windowSamples.Average(s => s.ThreadPoolPending);

                // Window slot wait
                var windowSlots = _collector.SlotWaitMs.Skip((int)(start / 100)).Take((int)(windowSizeMs / 100)).OrderBy(x => x).ToArray();
                string slotP50Str = windowSlots.Length > 0 ? $"{TimingCollector.Percentile(windowSlots, 0.50)}ms" : "—";

                // Window wire time
                var windowHttp = _collector.HttpSamples
                    .Where(s => s.TimestampMs >= start && s.TimestampMs < end)
                    .Select(s => s.WireMs).OrderBy(x => x).ToArray();
                string wireP50Str = windowHttp.Length > 0 ? $"{TimingCollector.Percentile(windowHttp, 0.50)}ms" : "—";

                sb.AppendLine($"  {start / 1000,3}-{end / 1000,3}s   {avgIF,6:F1} {peakIF,6} {avgDop,6:F0} {windowRps,6:F1} {slotP50Str,8} {wireP50Str,8} {avgTP,6:F0}");
            }
            sb.AppendLine();
        }

        // ── Starvation Analysis ──
        if (_elapsed.TotalSeconds > 0 && _totalPages > 0)
        {
            var httpSamplesAll = _collector.HttpSamples.ToArray();
            double avgWireMs = httpSamplesAll.Length > 0 ? httpSamplesAll.Average(s => s.WireMs) : 0;
            double theoreticalMaxRps = avgWireMs > 0 ? _configuredDop / (avgWireMs / 1000.0) : 0;
            double actualRps = _totalPages / _elapsed.TotalSeconds;

            sb.AppendLine("── Starvation Analysis ─────────────────────────────────");
            sb.AppendLine($"  Theoretical max RPS:  {theoreticalMaxRps:F0}  (DOP={_configuredDop} / avgWire={avgWireMs:F0}ms)");
            sb.AppendLine($"  Actual RPS:           {actualRps:F1}");
            sb.AppendLine($"  Efficiency:           {(theoreticalMaxRps > 0 ? actualRps / theoreticalMaxRps * 100 : 0):F1}%");
            sb.AppendLine();
        }

        sb.AppendLine("═══════════════════════════════════════════════════════");
        return sb.ToString();
    }

    private void WriteDopSamples(string path)
    {
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        writer.WriteLine("timestamp_ms,in_flight,current_dop,total_requests,idle_slots,rate_tokens,tp_pending,tp_count");
        foreach (var s in _samples)
        {
            writer.WriteLine($"{s.TimestampMs},{s.InFlight},{s.CurrentDop},{s.TotalRequests},{s.IdleSlots},{s.RateTokensAvailable},{s.ThreadPoolPending},{s.ThreadPoolCount}");
        }
    }

    private void WriteTimingSamples(string path)
    {
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        writer.WriteLine("timestamp_ms,type,value_ms,status_code,response_bytes,url");

        foreach (var s in _collector.HttpSamples.OrderBy(s => s.TimestampMs))
        {
            writer.WriteLine($"{s.TimestampMs},http,{s.WireMs},{s.StatusCode},{s.ResponseBytes},{EscapeCsv(s.Url)}");
        }

        foreach (var s in _collector.PersistSamples.OrderBy(s => s.TimestampMs))
        {
            writer.WriteLine($"{s.TimestampMs},persist,{s.TotalMs},0,{s.EntryCount},{EscapeCsv(s.SongId)}");
        }
    }

    private void WriteBottleneckAnalysis(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("═══════════════════════════════════════════════════════");
        sb.AppendLine("  Bottleneck Analysis");
        sb.AppendLine("═══════════════════════════════════════════════════════");
        sb.AppendLine();

        var slotWaits = _collector.SlotWaitMs.ToArray();
        var rateWaits = _collector.RateTokenWaitMs.ToArray();
        var httpSamples = _collector.HttpSamples.ToArray();
        var persistSamples = _collector.PersistSamples.ToArray();

        double totalSlotWaitMs = slotWaits.Sum();
        double totalRateWaitMs = rateWaits.Sum();
        double totalWireMs = httpSamples.Sum(s => s.WireMs);
        double totalPersistMs = persistSamples.Sum(s => s.TotalMs);
        double grandTotal = totalSlotWaitMs + totalRateWaitMs + totalWireMs + totalPersistMs;

        sb.AppendLine("── Time Breakdown (cumulative wall-clock across all tasks) ─");
        sb.AppendLine($"  Slot wait (DOP):    {totalSlotWaitMs / 1000:F1}s  ({(grandTotal > 0 ? totalSlotWaitMs / grandTotal * 100 : 0):F1}%)");
        sb.AppendLine($"  Rate token wait:    {totalRateWaitMs / 1000:F1}s  ({(grandTotal > 0 ? totalRateWaitMs / grandTotal * 100 : 0):F1}%)");
        sb.AppendLine($"  HTTP wire time:     {totalWireMs / 1000:F1}s  ({(grandTotal > 0 ? totalWireMs / grandTotal * 100 : 0):F1}%)");
        sb.AppendLine($"  Persistence:        {totalPersistMs / 1000:F1}s  ({(grandTotal > 0 ? totalPersistMs / grandTotal * 100 : 0):F1}%)");
        sb.AppendLine($"  Grand total:        {grandTotal / 1000:F1}s");
        sb.AppendLine();

        // Identify primary bottleneck
        var layers = new (string Name, double Ms)[]
        {
            ("DOP slot wait", totalSlotWaitMs),
            ("Rate token wait", totalRateWaitMs),
            ("HTTP wire time", totalWireMs),
            ("Persistence", totalPersistMs),
        };
        var dominant = layers.OrderByDescending(l => l.Ms).First();

        sb.AppendLine("── Primary Bottleneck ──────────────────────────────────");
        sb.AppendLine($"  {dominant.Name}: {dominant.Ms / 1000:F1}s ({(grandTotal > 0 ? dominant.Ms / grandTotal * 100 : 0):F1}% of total)");
        sb.AppendLine();

        // Recommendations
        sb.AppendLine("── Recommendations ─────────────────────────────────────");

        if (dominant.Name == "DOP slot wait" && totalSlotWaitMs > totalWireMs)
        {
            sb.AppendLine("  ⚠ DOP slots are the bottleneck.");
            sb.AppendLine("    → Increase --dop (more concurrent requests)");
            sb.AppendLine("    → Check if AIMD is reducing DOP (CDN blocks?)");
        }
        else if (dominant.Name == "Rate token wait")
        {
            sb.AppendLine("  ⚠ Rate limiter is the bottleneck.");
            sb.AppendLine("    → Increase --rps (more tokens per second)");
            sb.AppendLine("    → Or set --rps 0 for unlimited");
        }
        else if (dominant.Name == "HTTP wire time")
        {
            sb.AppendLine("  ✓ HTTP wire time is the dominant cost (expected).");
            sb.AppendLine("    → Epic API latency is the limiting factor");
            sb.AppendLine("    → Increase DOP to parallelize more requests");
        }
        else if (dominant.Name == "Persistence")
        {
            sb.AppendLine("  ⚠ Persistence is the bottleneck.");
            sb.AppendLine("    → Run with --no-persist to verify");
            sb.AppendLine("    → Check DB connection pool and WAL settings");
        }

        // Thread pool analysis
        if (_samples.Count > 0)
        {
            var avgPending = _samples.Average(s => s.ThreadPoolPending);
            var peakPending = _samples.Max(s => s.ThreadPoolPending);
            if (avgPending > 100 || peakPending > 500)
            {
                sb.AppendLine();
                sb.AppendLine($"  ⚠ Thread pool pressure detected: avg pending={avgPending:F0}, peak={peakPending}");
                sb.AppendLine("    → Increase ThreadPool.SetMinThreads()");
                sb.AppendLine("    → Check for sync-over-async (.GetAwaiter().GetResult())");
            }
        }

        sb.AppendLine();
        sb.AppendLine("═══════════════════════════════════════════════════════");

        File.WriteAllText(path, sb.ToString());
    }

    private static string EscapeCsv(string? value)
    {
        if (value is null) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
