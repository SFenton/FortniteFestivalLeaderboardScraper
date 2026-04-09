using System.Diagnostics;

namespace V1ScrapeHarness;

/// <summary>
/// Periodically samples DOP pool state and thread pool metrics.
/// Extends the SongMachineHarness DopSampler with thread pool instrumentation.
/// </summary>
public sealed class DopSampler : IDisposable
{
    private readonly AdaptiveConcurrencyLimiter _limiter;
    private readonly int _intervalMs;
    private readonly List<DopSample> _samples = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _sw;
    private Task? _samplingTask;

    public DopSampler(AdaptiveConcurrencyLimiter limiter, Stopwatch sw, int intervalMs = 25)
    {
        _limiter = limiter;
        _sw = sw;
        _intervalMs = intervalMs;
    }

    public IReadOnlyList<DopSample> Samples => _samples;

    public void Start()
    {
        _samplingTask = Task.Run(SampleLoopAsync);
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_samplingTask is not null)
        {
            try { await _samplingTask; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task SampleLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            _samples.Add(new DopSample(
                TimestampMs: _sw.ElapsedMilliseconds,
                InFlight: _limiter.InFlight,
                CurrentDop: _limiter.CurrentDop,
                TotalRequests: _limiter.TotalRequests,
                IdleSlots: Math.Max(0, _limiter.CurrentDop - _limiter.InFlight),
                RateTokensAvailable: _limiter.RateTokensAvailable,
                ThreadPoolPending: ThreadPool.PendingWorkItemCount,
                ThreadPoolCount: ThreadPool.ThreadCount));

            try { await Task.Delay(_intervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
    }
}

public sealed record DopSample(
    long TimestampMs,
    int InFlight,
    int CurrentDop,
    long TotalRequests,
    int IdleSlots,
    int RateTokensAvailable,
    long ThreadPoolPending,
    int ThreadPoolCount);
