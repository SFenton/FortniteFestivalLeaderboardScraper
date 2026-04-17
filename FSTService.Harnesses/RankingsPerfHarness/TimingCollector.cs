using System.Collections.Concurrent;

namespace RankingsPerfHarness;

/// <summary>
/// Thread-safe latency sample collector that emits p50 / p95 / p99 / max stats.
/// Mirrors the pattern used by <c>V1ScrapeHarness.TimingCollector</c>.
/// </summary>
public sealed class TimingCollector
{
    private readonly ConcurrentBag<long> _samplesMicros = new();

    public void Record(TimeSpan duration) => _samplesMicros.Add((long)(duration.TotalMilliseconds * 1000));

    public int Count => _samplesMicros.Count;

    public Stats Snapshot()
    {
        var arr = _samplesMicros.ToArray();
        if (arr.Length == 0) return new Stats(0, 0, 0, 0, 0, 0);
        Array.Sort(arr);
        long p50 = arr[(int)(arr.Length * 0.50)];
        long p95 = arr[Math.Min(arr.Length - 1, (int)(arr.Length * 0.95))];
        long p99 = arr[Math.Min(arr.Length - 1, (int)(arr.Length * 0.99))];
        long max = arr[^1];
        double mean = arr.Average();
        return new Stats(arr.Length, p50 / 1000.0, p95 / 1000.0, p99 / 1000.0, max / 1000.0, mean / 1000.0);
    }

    public readonly record struct Stats(int Count, double P50Ms, double P95Ms, double P99Ms, double MaxMs, double MeanMs);
}
