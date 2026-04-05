using System.Diagnostics;

namespace SongMachineHarness;

public sealed class DopSampler : IDisposable
{
    private readonly AdaptiveConcurrencyLimiter _limiter;
    private readonly int _intervalMs;
    private readonly List<DopSample> _samples = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _sw;
    private Task? _samplingTask;

    public DopSampler(AdaptiveConcurrencyLimiter limiter, int intervalMs = 25)
    {
        _limiter = limiter;
        _intervalMs = intervalMs;
        _sw = new Stopwatch();
    }

    public IReadOnlyList<DopSample> Samples => _samples;

    public void Start()
    {
        _sw.Start();
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
        _sw.Stop();
    }

    private async Task SampleLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            var inFlight = _limiter.InFlight;
            var currentDop = _limiter.CurrentDop;
            var totalRequests = _limiter.TotalRequests;

            _samples.Add(new DopSample(
                TimestampMs: _sw.ElapsedMilliseconds,
                InFlight: inFlight,
                CurrentDop: currentDop,
                TotalRequests: totalRequests,
                IdleSlots: currentDop - inFlight));

            try { await Task.Delay(_intervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
    }
}
