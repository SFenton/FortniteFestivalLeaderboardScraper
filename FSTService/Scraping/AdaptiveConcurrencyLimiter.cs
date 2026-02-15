namespace FSTService.Scraping;

/// <summary>
/// AIMD (Additive Increase, Multiplicative Decrease) concurrency limiter.
/// Dynamically adjusts the degree of parallelism based on observed error rates,
/// similar to TCP congestion control.
///
/// <list type="bullet">
///   <item>Error rate &lt; 1% over the evaluation window → increase DOP by +<see cref="AdditiveIncrease"/></item>
///   <item>Error rate &gt; 5% over the evaluation window → decrease DOP by ×<see cref="MultiplicativeDecrease"/></item>
///   <item>DOP is clamped between <see cref="_minDop"/> and <see cref="_maxDop"/></item>
/// </list>
///
/// The limiter wraps a <see cref="SemaphoreSlim"/> and adjusts its available tokens:
/// increase adds tokens via <c>Release()</c>; decrease drains tokens via non-blocking <c>Wait(0)</c>.
/// </summary>
public sealed class AdaptiveConcurrencyLimiter : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private readonly ILogger _log;
    private readonly int _minDop;
    private readonly int _maxDop;
    private int _currentDop;

    // ── Sliding window counters ──
    private int _windowSuccesses;
    private int _windowFailures;
    private readonly object _evaluationLock = new();

    // ── AIMD parameters ──
    private const int EvaluationWindow = 500;
    private const int AdditiveIncrease = 16;
    private const double MultiplicativeDecrease = 0.75;
    private const double ErrorThresholdHigh = 0.05; // 5% → decrease
    private const double ErrorThresholdLow = 0.01;  // 1% → increase

    /// <summary>Current effective DOP.</summary>
    public int CurrentDop => Volatile.Read(ref _currentDop);

    public AdaptiveConcurrencyLimiter(int initialDop, int minDop, int maxDop, ILogger log)
    {
        _currentDop = initialDop;
        _minDop = Math.Max(1, minDop);
        _maxDop = maxDop;
        _log = log;

        // Semaphore max is set to maxDop so we have headroom to Release() into.
        _semaphore = new SemaphoreSlim(initialDop, maxDop);
    }

    /// <summary>Wait for a concurrency slot (same as SemaphoreSlim.WaitAsync).</summary>
    public Task WaitAsync(CancellationToken ct) => _semaphore.WaitAsync(ct);

    /// <summary>Release a concurrency slot (same as SemaphoreSlim.Release).</summary>
    public void Release() => _semaphore.Release();

    /// <summary>Report a successful request. May trigger an evaluation.</summary>
    public void ReportSuccess()
    {
        Interlocked.Increment(ref _windowSuccesses);
        MaybeEvaluate();
    }

    /// <summary>Report a failed/retried request. May trigger an evaluation.</summary>
    public void ReportFailure()
    {
        Interlocked.Increment(ref _windowFailures);
        MaybeEvaluate();
    }

    private void MaybeEvaluate()
    {
        // Quick check without lock
        int total = Volatile.Read(ref _windowSuccesses) + Volatile.Read(ref _windowFailures);
        if (total < EvaluationWindow) return;

        lock (_evaluationLock)
        {
            // Double-check under lock
            total = _windowSuccesses + _windowFailures;
            if (total < EvaluationWindow) return;

            int failures = _windowFailures;
            _windowSuccesses = 0;
            _windowFailures = 0;

            double errorRate = (double)failures / total;

            if (errorRate > ErrorThresholdHigh)
            {
                int newDop = Math.Max(_minDop, (int)(_currentDop * MultiplicativeDecrease));
                AdjustDop(newDop, errorRate);
            }
            else if (errorRate < ErrorThresholdLow)
            {
                int newDop = Math.Min(_maxDop, _currentDop + AdditiveIncrease);
                AdjustDop(newDop, errorRate);
            }
            else
            {
                _log.LogDebug("Adaptive DOP: holding at {Dop} (error rate {ErrorRate:P1})",
                    _currentDop, errorRate);
            }
        }
    }

    private void AdjustDop(int newDop, double errorRate)
    {
        int delta = newDop - _currentDop;
        if (delta == 0) return;

        if (delta > 0)
        {
            // Increase: release extra tokens into the semaphore
            _semaphore.Release(delta);
        }
        else
        {
            // Decrease: drain tokens from the semaphore (non-blocking)
            int drained = 0;
            for (int i = 0; i < -delta; i++)
            {
                if (!_semaphore.Wait(0)) break;
                drained++;
            }
            // If we couldn't drain all tokens immediately (they're in-flight),
            // the effective DOP will converge as tasks complete and don't get
            // their tokens returned.
            if (drained < -delta)
            {
                _log.LogDebug("Adaptive DOP: drained {Drained}/{Target} tokens (rest in-flight)",
                    drained, -delta);
            }
        }

        _log.LogInformation("Adaptive DOP: {OldDop} → {NewDop} (error rate {ErrorRate:P1})",
            _currentDop, newDop, errorRate);
        _currentDop = newDop;
    }

    public void Dispose() => _semaphore.Dispose();
}
