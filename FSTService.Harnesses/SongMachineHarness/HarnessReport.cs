using System.Text;
using System.Text.Json;
using FSTService.Scraping;
using static FSTService.Scraping.SongProcessingMachine;

namespace SongMachineHarness;

public sealed class HarnessReport
{
    private readonly IReadOnlyList<DopSample> _samples;
    private readonly IReadOnlyCollection<CallEvent> _events;
    private readonly MachineResult _result;
    private readonly TimeSpan _elapsed;
    private readonly int _configuredDop;
    private readonly int _configuredRps;
    private readonly int _songDop;
    private readonly int _batchSize;
    private readonly int _songCount;
    private readonly int _accountCount;
    private readonly int[] _seasons;
    private readonly ResilientHttpExecutor? _executor;

    public HarnessReport(
        IReadOnlyList<DopSample> samples,
        IReadOnlyCollection<CallEvent> events,
        MachineResult result,
        TimeSpan elapsed,
        int configuredDop,
        int configuredRps,
        int songDop,
        int batchSize,
        int songCount,
        int accountCount,
        int[] seasons,
        ResilientHttpExecutor? executor = null)
    {
        _samples = samples;
        _events = events;
        _result = result;
        _elapsed = elapsed;
        _configuredDop = configuredDop;
        _configuredRps = configuredRps;
        _songDop = songDop;
        _batchSize = batchSize;
        _songCount = songCount;
        _accountCount = accountCount;
        _seasons = seasons;
        _executor = executor;
    }

    public void WriteAll(string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        var summary = BuildSummary();
        Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(outputDir, "summary.txt"), summary);

        WriteCallLog(Path.Combine(outputDir, "call-log.jsonl"));
        WriteDopSamples(Path.Combine(outputDir, "dop-samples.csv"));
        WriteSongTimeline(Path.Combine(outputDir, "song-timeline.csv"));
        WriteStressTestMetrics(Path.Combine(outputDir, "stress-metrics.csv"));

        Console.WriteLine();
        Console.WriteLine($"Output written to: {Path.GetFullPath(outputDir)}");
        Console.WriteLine($"  summary.txt       ({new FileInfo(Path.Combine(outputDir, "summary.txt")).Length:N0} bytes)");
        Console.WriteLine($"  call-log.jsonl    ({new FileInfo(Path.Combine(outputDir, "call-log.jsonl")).Length:N0} bytes)");
        Console.WriteLine($"  dop-samples.csv   ({new FileInfo(Path.Combine(outputDir, "dop-samples.csv")).Length:N0} bytes)");
        Console.WriteLine($"  song-timeline.csv ({new FileInfo(Path.Combine(outputDir, "song-timeline.csv")).Length:N0} bytes)");
        Console.WriteLine($"  stress-metrics.csv ({new FileInfo(Path.Combine(outputDir, "stress-metrics.csv")).Length:N0} bytes)");
    }

    private string BuildSummary()
    {
        var sb = new StringBuilder();
        var events = _events.ToList();
        var successfulEvents = events.Where(e => e.Success).ToList();
        var failedEvents = events.Where(e => !e.Success).ToList();

        sb.AppendLine("═══════════════════════════════════════════════════════");
        sb.AppendLine("  SongMachine DOP Utilization Report");
        sb.AppendLine("═══════════════════════════════════════════════════════");
        sb.AppendLine();

        // ── Config ──
        sb.AppendLine("── Configuration ───────────────────────────────────────");
        sb.AppendLine($"  DOP: {_configuredDop}  |  RPS: {_configuredRps}  |  SongDOP: {_songDop}  |  BatchSize: {_batchSize}");
        sb.AppendLine($"  Songs: {_songCount}  |  Accounts: {_accountCount}  |  Seasons: [{string.Join(",", _seasons)}]");
        sb.AppendLine();

        // ── Totals ──
        sb.AppendLine("── Totals ──────────────────────────────────────────────");
        sb.AppendLine($"  API Calls:         {_result.ApiCalls,-10}  Elapsed:          {_elapsed.TotalSeconds:F1}s");
        sb.AppendLine($"  Entries Updated:   {_result.EntriesUpdated,-10}  Sessions:         {_result.SessionsInserted}");
        sb.AppendLine($"  Instrumented Calls:{events.Count,-10}  Failed:           {failedEvents.Count}");
        sb.AppendLine($"  Actual RPS:        {(_elapsed.TotalSeconds > 0 ? events.Count / _elapsed.TotalSeconds : 0):F1}");
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
            long peakTimestamp = _samples.First(s => s.InFlight == peakInFlight).TimestampMs;

            sb.AppendLine("── DOP Utilization ─────────────────────────────────────");
            sb.AppendLine($"  Configured DOP:    {_configuredDop,-10}  Peak InFlight:    {peakInFlight} (at {peakTimestamp}ms)");
            sb.AppendLine($"  Avg InFlight:      {avgInFlight,-10:F1}  DOP Utilization:  {utilization:F1}%");
            sb.AppendLine($"  Time at zero:      {zeroPercent,-10:F1}%  Idle slots (avg): {avgIdle:F1}");
            sb.AppendLine();

            // ── InFlight Distribution ──
            var buckets = new (string Label, int Min, int Max)[]
            {
                ("0",       0,   0),
                ("1-10",    1,   10),
                ("11-25",   11,  25),
                ("26-50",   26,  50),
                ("51-100",  51,  100),
                ("101-200", 101, 200),
                ("201-400", 201, 400),
                ("401-575", 401, 575),
                ("576+",    576, int.MaxValue),
            };

            sb.AppendLine("── InFlight Distribution ───────────────────────────────");
            foreach (var (label, min, max) in buckets)
            {
                int count = _samples.Count(s => s.InFlight >= min && s.InFlight <= max);
                double pct = (double)count / _samples.Count * 100;
                if (count == 0 && min > peakInFlight) continue;  // skip empty buckets above peak
                int barLen = (int)(pct / 100 * 40);
                string bar = new string('█', barLen) + new string('░', 40 - barLen);
                sb.AppendLine($"  {label,-10} {bar}  {pct:F1}%  ({count})");
            }
            sb.AppendLine();
        }

        // ── Call Duration ──
        if (successfulEvents.Count > 0)
        {
            var durations = successfulEvents.Select(e => e.DurationMs).OrderBy(d => d).ToList();

            sb.AppendLine("── Call Duration ───────────────────────────────────────");
            sb.AppendLine($"  Min:  {durations[0],-8}ms  P50:  {Percentile(durations, 50),-8}ms  P90:  {Percentile(durations, 90)}ms");
            sb.AppendLine($"  P95:  {Percentile(durations, 95),-8}ms  P99:  {Percentile(durations, 99),-8}ms  Max:  {durations[^1]}ms");
            sb.AppendLine();
        }

        // ── Per-Instrument Breakdown ──
        if (events.Count > 0)
        {
            var byInstrument = events
                .GroupBy(e => e.Instrument)
                .OrderBy(g => g.Key);

            sb.AppendLine("── Per-Instrument Breakdown ────────────────────────────");
            foreach (var grp in byInstrument)
            {
                int calls = grp.Count();
                double avgMs = grp.Where(e => e.Success).Select(e => e.DurationMs).DefaultIfEmpty(0).Average();
                double avgBatch = grp.Average(e => e.BatchSize);
                int failed = grp.Count(e => !e.Success);
                sb.AppendLine($"  {grp.Key,-22} {calls,5} calls, avg {avgMs,6:F0}ms, batch {avgBatch:F1}, failed {failed}");
            }
            sb.AppendLine();
        }

        // ── Timeline Phases (10s windows) ──
        if (_samples.Count > 0 && events.Count > 0)
        {
            long totalMs = _samples[^1].TimestampMs;
            int windowSizeMs = 10_000;

            sb.AppendLine("── Timeline (10s windows) ──────────────────────────────");
            for (long start = 0; start < totalMs; start += windowSizeMs)
            {
                long end = start + windowSizeMs;
                var windowSamples = _samples.Where(s => s.TimestampMs >= start && s.TimestampMs < end).ToList();
                var windowEvents = events.Where(e => e.StartMs >= start && e.StartMs < end).ToList();

                if (windowSamples.Count == 0) continue;

                double avgIF = windowSamples.Average(s => s.InFlight);
                int peakIF = windowSamples.Max(s => s.InFlight);
                double rps = windowEvents.Count / (windowSizeMs / 1000.0);

                sb.AppendLine($"  {start / 1000,3}-{end / 1000,3}s:  avg IF={avgIF,6:F1}  peak IF={peakIF,4}  calls={windowEvents.Count,5}  rps={rps,6:F1}");
            }
            sb.AppendLine();
        }

        // ── Starvation Analysis ──
        if (successfulEvents.Count > 0 && _elapsed.TotalSeconds > 0)
        {
            double slotSecondsAvailable = _configuredDop * _elapsed.TotalSeconds;
            double slotSecondsUsed = successfulEvents.Sum(e => e.DurationMs) / 1000.0;
            double slotUtilization = slotSecondsAvailable > 0 ? slotSecondsUsed / slotSecondsAvailable * 100 : 0;
            double theoreticalMaxRps = successfulEvents.Count > 0
                ? _configuredDop / (successfulEvents.Average(e => e.DurationMs) / 1000.0)
                : 0;

            sb.AppendLine("── Starvation Analysis ─────────────────────────────────");
            sb.AppendLine($"  Slot-seconds available: {_configuredDop} × {_elapsed.TotalSeconds:F1}s = {slotSecondsAvailable:F0}");
            sb.AppendLine($"  Slot-seconds used:      {slotSecondsUsed:F1}  (sum of all call durations)");
            sb.AppendLine($"  Slot utilization:       {slotUtilization:F1}%");
            sb.AppendLine($"  Theoretical max RPS:    {theoreticalMaxRps:F0}  (DOP / avg call duration)");
            sb.AppendLine();
        }

        // ── Errors ──
        if (failedEvents.Count > 0)
        {
            sb.AppendLine("── Errors ──────────────────────────────────────────────");
            foreach (var grp in failedEvents.GroupBy(e => e.ExceptionType ?? "Unknown"))
            {
                sb.AppendLine($"  {grp.Key}: {grp.Count()} occurrences");
                var first = grp.First();
                sb.AppendLine($"    First: {first.ExceptionMessage}");
            }
            sb.AppendLine();
        }

        // ── CDN Wire Diagnostics ──
        if (_executor is not null)
        {
            sb.AppendLine("── CDN Wire Diagnostics ────────────────────────────────");
            sb.AppendLine($"  Total HTTP sends:  {_executor.TotalHttpSends,-10}  CDN blocks detected: {_executor.CdnBlocksDetected}");
            sb.AppendLine($"  Probe attempts:    {_executor.CdnProbeAttempts,-10}  Probe successes:     {_executor.CdnProbeSuccesses}");
            if (_executor.TotalHttpSends > 0)
            {
                double cdnPct = (double)_executor.CdnBlocksDetected / _executor.TotalHttpSends * 100;
                sb.AppendLine($"  CDN block rate:    {cdnPct:F2}%");
            }
            sb.AppendLine();
        }

        sb.AppendLine("═══════════════════════════════════════════════════════");
        return sb.ToString();
    }

    private void WriteCallLog(string path)
    {
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        foreach (var evt in _events)
        {
            var obj = new Dictionary<string, object?>
            {
                ["id"] = evt.CallId,
                ["type"] = evt.Type,
                ["song"] = evt.SongId,
                ["inst"] = evt.Instrument,
                ["batch"] = evt.BatchSize,
                ["season"] = evt.Season,
                ["startMs"] = evt.StartMs,
                ["endMs"] = evt.EndMs,
                ["durMs"] = evt.DurationMs,
                ["results"] = evt.ResultCount,
                ["ok"] = evt.Success,
                ["pages"] = evt.PaginationPages,
                ["ifStart"] = evt.InFlightAtStart,
                ["ifEnd"] = evt.InFlightAtEnd,
            };

            if (evt.ExceptionType is not null)
            {
                obj["errType"] = evt.ExceptionType;
                obj["errMsg"] = evt.ExceptionMessage;
            }

            writer.WriteLine(JsonSerializer.Serialize(obj, options));
        }
    }

    private void WriteDopSamples(string path)
    {
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        writer.WriteLine("timestamp_ms,in_flight,current_dop,total_requests,idle_slots");
        foreach (var s in _samples)
        {
            writer.WriteLine($"{s.TimestampMs},{s.InFlight},{s.CurrentDop},{s.TotalRequests},{s.IdleSlots}");
        }
    }

    private void WriteSongTimeline(string path)
    {
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        writer.WriteLine("song_id,first_call_ms,last_call_ms,duration_ms,total_calls,alltime_calls,seasonal_calls");

        var bySong = _events
            .GroupBy(e => e.SongId)
            .OrderBy(g => g.Min(e => e.StartMs));

        foreach (var grp in bySong)
        {
            long first = grp.Min(e => e.StartMs);
            long last = grp.Max(e => e.EndMs);
            int total = grp.Count();
            int alltime = grp.Count(e => e.Type == "alltime");
            int seasonal = grp.Count(e => e.Type == "seasonal");
            writer.WriteLine($"{grp.Key},{first},{last},{last - first},{total},{alltime},{seasonal}");
        }
    }

    /// <summary>
    /// Per-song/instrument stress test metrics: alltime calls, users queried,
    /// results found (= users with scores), seasonal calls by season, etc.
    /// </summary>
    private void WriteStressTestMetrics(string path)
    {
        var events = _events.ToList();

        // ── Per song/instrument CSV ──
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        writer.WriteLine("song_id,instrument,alltime_calls,alltime_users_queried,alltime_results_found,alltime_users_filtered,alltime_wire_sends,seasonal_calls,seasonal_results,seasonal_wire_sends,distinct_seasons,history_recon_calls");

        var byCombo = events
            .GroupBy(e => (e.SongId, e.Instrument))
            .OrderBy(g => g.Key.SongId)
            .ThenBy(g => g.Key.Instrument);

        int totalAlltimeCalls = 0, totalAlltimeUsersQueried = 0, totalAlltimeResults = 0;
        int totalSeasonalCalls = 0, totalSeasonalResults = 0, totalHistoryReconCalls = 0;

        foreach (var grp in byCombo)
        {
            var alltimeEvents = grp.Where(e => e.Type == "alltime").ToList();
            var seasonalEvents = grp.Where(e => e.Type == "seasonal").ToList();

            int atCalls = alltimeEvents.Count;
            int atUsersQueried = alltimeEvents.Sum(e => e.BatchSize);
            int atResults = alltimeEvents.Sum(e => e.ResultCount);
            int atFiltered = atUsersQueried - atResults;
            int atWireSends = alltimeEvents.Sum(e => e.PaginationPages);

            int sCalls = seasonalEvents.Count;
            int sResults = seasonalEvents.Sum(e => e.ResultCount);
            int sWireSends = seasonalEvents.Sum(e => e.PaginationPages);
            int distinctSeasons = seasonalEvents.Select(e => e.Season).Distinct().Count();

            // History recon = seasonal calls beyond the current season
            // (current season = highest season number; anything else is backfill)
            int histReconCalls = 0;
            if (seasonalEvents.Count > 0 && _seasons.Length > 0)
            {
                var currentSeasonPrefix = $"season{_seasons.Max():D03}";
                histReconCalls = seasonalEvents.Count(e => e.Season != currentSeasonPrefix && e.Season != "evergreen");
            }

            totalAlltimeCalls += atCalls;
            totalAlltimeUsersQueried += atUsersQueried;
            totalAlltimeResults += atResults;
            totalSeasonalCalls += sCalls;
            totalSeasonalResults += sResults;
            totalHistoryReconCalls += histReconCalls;

            writer.WriteLine($"{grp.Key.SongId},{grp.Key.Instrument},{atCalls},{atUsersQueried},{atResults},{atFiltered},{atWireSends},{sCalls},{sResults},{sWireSends},{distinctSeasons},{histReconCalls}");
        }

        // ── Summary line at the end ──
        writer.WriteLine();
        writer.WriteLine("# SUMMARY");
        writer.WriteLine($"# total_accounts,{_accountCount}");
        writer.WriteLine($"# total_songs,{_songCount}");
        writer.WriteLine($"# total_alltime_calls,{totalAlltimeCalls}");
        writer.WriteLine($"# total_alltime_users_queried,{totalAlltimeUsersQueried}");
        writer.WriteLine($"# total_alltime_results_found,{totalAlltimeResults}");
        writer.WriteLine($"# total_alltime_users_filtered,{totalAlltimeUsersQueried - totalAlltimeResults}");
        writer.WriteLine($"# alltime_hit_rate,{(totalAlltimeUsersQueried > 0 ? (double)totalAlltimeResults / totalAlltimeUsersQueried * 100 : 0):F1}%");
        writer.WriteLine($"# total_seasonal_calls,{totalSeasonalCalls}");
        writer.WriteLine($"# total_seasonal_results,{totalSeasonalResults}");
        writer.WriteLine($"# total_history_recon_calls,{totalHistoryReconCalls}");
        writer.WriteLine($"# total_api_calls,{events.Count}");
        writer.WriteLine($"# total_wire_sends,{_executor?.TotalHttpSends ?? events.Count}");
        writer.WriteLine($"# elapsed_seconds,{_elapsed.TotalSeconds:F1}");
        writer.WriteLine($"# effective_rps,{(_elapsed.TotalSeconds > 0 ? events.Count / _elapsed.TotalSeconds : 0):F1}");

        // ── Also add stress summary to console ──
        Console.WriteLine("── Stress Test Summary ─────────────────────────────────");
        Console.WriteLine($"  Alltime:  {totalAlltimeCalls} calls, {totalAlltimeUsersQueried} users queried, {totalAlltimeResults} found ({(totalAlltimeUsersQueried > 0 ? (double)totalAlltimeResults / totalAlltimeUsersQueried * 100 : 0):F1}% hit)");
        Console.WriteLine($"  Seasonal: {totalSeasonalCalls} calls, {totalSeasonalResults} results");
        Console.WriteLine($"  History:  {totalHistoryReconCalls} recon calls (non-current season)");
        Console.WriteLine($"  Total:    {events.Count} API calls, {_executor?.TotalHttpSends ?? events.Count} wire sends in {_elapsed.TotalSeconds:F1}s");
        Console.WriteLine();
    }

    private static long Percentile(List<long> sorted, int p)
    {
        if (sorted.Count == 0) return 0;
        int index = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
    }
}
