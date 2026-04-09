using System.Collections.Concurrent;

namespace V1ScrapeHarness;

/// <summary>
/// Collects timing samples from AdaptiveConcurrencyLimiter callbacks
/// and the InstrumentedHttpHandler. Provides live percentile access.
/// </summary>
public sealed class TimingCollector
{
    private readonly ConcurrentBag<long> _slotWaitMs = new();
    private readonly ConcurrentBag<long> _rateTokenWaitMs = new();
    private readonly ConcurrentBag<HttpTimingSample> _httpSamples = new();
    private readonly ConcurrentBag<PersistTimingSample> _persistSamples = new();

    public IReadOnlyCollection<long> SlotWaitMs => _slotWaitMs;
    public IReadOnlyCollection<long> RateTokenWaitMs => _rateTokenWaitMs;
    public IReadOnlyCollection<HttpTimingSample> HttpSamples => _httpSamples;
    public IReadOnlyCollection<PersistTimingSample> PersistSamples => _persistSamples;

    public void RecordSlotWait(long ms) => _slotWaitMs.Add(ms);
    public void RecordRateTokenWait(long ms) => _rateTokenWaitMs.Add(ms);
    public void RecordHttpTiming(HttpTimingSample sample) => _httpSamples.Add(sample);
    public void RecordPersistTiming(PersistTimingSample sample) => _persistSamples.Add(sample);

    /// <summary>Get percentile from a snapshot of collected values.</summary>
    public static long Percentile(long[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        int index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    /// <summary>Get slot wait percentiles from current data.</summary>
    public (long P50, long P95, long P99) GetSlotWaitPercentiles()
    {
        var sorted = _slotWaitMs.OrderBy(x => x).ToArray();
        return (Percentile(sorted, 0.50), Percentile(sorted, 0.95), Percentile(sorted, 0.99));
    }

    /// <summary>Get rate token wait percentiles from current data.</summary>
    public (long P50, long P95, long P99) GetRateTokenWaitPercentiles()
    {
        var sorted = _rateTokenWaitMs.OrderBy(x => x).ToArray();
        return (Percentile(sorted, 0.50), Percentile(sorted, 0.95), Percentile(sorted, 0.99));
    }

    /// <summary>Get HTTP wire time percentiles from current data.</summary>
    public (long P50, long P95, long P99) GetHttpPercentiles()
    {
        var sorted = _httpSamples.Select(s => s.WireMs).OrderBy(x => x).ToArray();
        return (Percentile(sorted, 0.50), Percentile(sorted, 0.95), Percentile(sorted, 0.99));
    }

    /// <summary>Get recent slot wait percentiles (last N samples).</summary>
    public (long P50, long P95) GetRecentSlotWaitPercentiles(int windowSize = 500)
    {
        var sorted = _slotWaitMs.TakeLast(windowSize).OrderBy(x => x).ToArray();
        return (Percentile(sorted, 0.50), Percentile(sorted, 0.95));
    }

    /// <summary>Get recent rate token wait percentiles (last N samples).</summary>
    public (long P50, long P95) GetRecentRateTokenWaitPercentiles(int windowSize = 500)
    {
        var sorted = _rateTokenWaitMs.TakeLast(windowSize).OrderBy(x => x).ToArray();
        return (Percentile(sorted, 0.50), Percentile(sorted, 0.95));
    }

    /// <summary>Get recent HTTP wire time percentiles (last N samples).</summary>
    public (long P50, long P95) GetRecentHttpPercentiles(int windowSize = 500)
    {
        var sorted = _httpSamples.TakeLast(windowSize).Select(s => s.WireMs).OrderBy(x => x).ToArray();
        return (Percentile(sorted, 0.50), Percentile(sorted, 0.95));
    }
}

public readonly record struct HttpTimingSample(
    long TimestampMs,
    long WireMs,
    int StatusCode,
    long ResponseBytes,
    string? Url);

public readonly record struct PersistTimingSample(
    long TimestampMs,
    string SongId,
    int EntryCount,
    long EnqueueMs,
    long TotalMs);
