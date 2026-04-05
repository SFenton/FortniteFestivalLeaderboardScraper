using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace FortniteFestival.Core.Scraping
{
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
    /// A circuit breaker pauses all slot acquisitions when the failure rate exceeds
    /// <see cref="CircuitBreakerThreshold"/> within a sliding window, preventing
    /// thundering-herd retry storms from overwhelming downstream services.
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
        private readonly object _evaluationLock = new object();

        // ── Throughput tracking ──
        private readonly Stopwatch _windowStopwatch = Stopwatch.StartNew();
        private readonly Stopwatch _lifetimeStopwatch = Stopwatch.StartNew();
        private long _totalRequests;

        /// <summary>
        /// Tracks tokens that should have been drained during a DOP decrease
        /// but couldn't be because they were held by in-flight tasks.
        /// When a task calls <see cref="Release"/>, it absorbs one debt token
        /// instead of returning it to the semaphore.
        /// </summary>
        private int _releaseDebt;

        // ── AIMD parameters ──
        private const int EvaluationWindow = 500;
        private const int AdditiveIncrease = 16;
        private const double MultiplicativeDecrease = 0.75;
        private const double ErrorThresholdHigh = 0.05; // 5% → decrease
        private const double ErrorThresholdLow = 0.01;  // 1% → increase

        // ── Circuit breaker ──
        private const double CircuitBreakerThreshold = 0.50; // 50% failure rate → trip
        private const int CircuitBreakerWindowMs = 30_000;    // 30 s sliding window
        private const int CircuitBreakerPauseMs = 10_000;     // 10 s pause
        private const int CircuitBreakerMinSamples = 50;      // need this many before tripping
        private volatile bool _circuitOpen;
        private DateTimeOffset _circuitOpensAt;
        private int _cbWindowSuccesses;
        private int _cbWindowFailures;
        private readonly Stopwatch _cbWindowStopwatch = Stopwatch.StartNew();
        private readonly object _cbLock = new object();

        // ── Health logging timer ──
#pragma warning disable CS8632 // nullable annotations (net472 compat)
        private readonly Timer? _healthTimer;
#pragma warning restore CS8632

        // ── Stale DOP detection ──
        private int _consecutiveStaleChecks;
        private long _lastHealthTotalRequests;

        // ── Token-bucket rate limiter ──
        private const int RateBucketIntervalMs = 50;  // refill every 50ms
        private const int RateBucketTicksPerSecond = 1000 / RateBucketIntervalMs; // 20
#pragma warning disable CS8632 // nullable annotations (net472 compat)
        private readonly SemaphoreSlim? _rateBucket;
        private readonly Timer? _rateBucketTimer;
#pragma warning restore CS8632
        private readonly int _tokensPerTick;
        private readonly int _maxRequestsPerSecond;

        /// <summary>Current effective DOP.</summary>
        public int CurrentDop
        {
            get { return Volatile.Read(ref _currentDop); }
        }

        /// <summary>Total requests (successes + failures) reported since construction.</summary>
        public long TotalRequests => Volatile.Read(ref _totalRequests);

        /// <summary>Approximate in-flight requests (acquired but not yet released slots).</summary>
        public int InFlight => Math.Max(0, CurrentDop - _semaphore.CurrentCount);

        /// <summary>Configured max requests per second (0 = unlimited).</summary>
        public int MaxRequestsPerSecond => _maxRequestsPerSecond;

        /// <summary>Whether the circuit breaker is currently open (pausing acquisitions).</summary>
        public bool IsCircuitOpen => _circuitOpen;

        public AdaptiveConcurrencyLimiter(int initialDop, int minDop, int maxDop, ILogger log,
            int maxRequestsPerSecond = 0)
        {
            _minDop = Math.Max(1, minDop);
            _maxDop = Math.Max(_minDop, maxDop);
            _currentDop = Math.Clamp(initialDop, _minDop, _maxDop);
            _log = log;
            _maxRequestsPerSecond = Math.Max(0, maxRequestsPerSecond);

            // Semaphore max is set to maxDop so we have headroom to Release() into.
            _semaphore = new SemaphoreSlim(_currentDop, _maxDop);

            // ── Token-bucket rate limiter ──
            if (_maxRequestsPerSecond > 0)
            {
                _tokensPerTick = Math.Max(1, _maxRequestsPerSecond / RateBucketTicksPerSecond);
                _rateBucket = new SemaphoreSlim(_tokensPerTick, _tokensPerTick);
                _rateBucketTimer = new Timer(RefillRateBucket, null, RateBucketIntervalMs, RateBucketIntervalMs);
                _log.LogInformation(
                    "Rate limiter active: {MaxRps} req/s ({TokensPerTick} tokens every {IntervalMs}ms)",
                    _maxRequestsPerSecond, _tokensPerTick, RateBucketIntervalMs);
            }

            // ── Periodic health logging (every 60 s when active) ──
            _healthTimer = new Timer(LogHealth, null, 60_000, 60_000);
        }

        /// <summary>Wait for a concurrency slot, then a rate-limiter token (if active).
        /// If the circuit breaker is open, blocks until the pause expires.</summary>
        public async Task WaitAsync(CancellationToken ct)
        {
            // ── Circuit breaker: wait for pause to expire if open ──
            if (_circuitOpen)
            {
                var remaining = _circuitOpensAt.AddMilliseconds(CircuitBreakerPauseMs) - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, ct);
                _circuitOpen = false;
            }

            await _semaphore.WaitAsync(ct);

            if (_rateBucket != null)
                await _rateBucket.WaitAsync(ct);
        }

        private void RefillRateBucket(object state)
        {
            if (_rateBucket == null) return;

            // Release up to _tokensPerTick, but don't exceed the semaphore maximum.
            int toRelease = Math.Min(_tokensPerTick, _tokensPerTick - _rateBucket.CurrentCount);
            if (toRelease > 0)
                _rateBucket.Release(toRelease);
        }

        /// <summary>Release a concurrency slot. If there is outstanding release debt
        /// from an incomplete DOP decrease, the token is absorbed instead of
        /// being returned to the semaphore.</summary>
        public void Release()
        {
            // CAS loop: if debt > 0, absorb this token to fulfil the debt
            while (true)
            {
                int debt = Volatile.Read(ref _releaseDebt);
                if (debt <= 0) break;
                if (Interlocked.CompareExchange(ref _releaseDebt, debt - 1, debt) == debt)
                    return; // token absorbed — do NOT release to semaphore
            }
            _semaphore.Release();
        }

        /// <summary>Report a successful request. May trigger an evaluation.</summary>
        public void ReportSuccess()
        {
            Interlocked.Increment(ref _totalRequests);
            Interlocked.Increment(ref _windowSuccesses);
            Interlocked.Increment(ref _cbWindowSuccesses);
            MaybeEvaluate();
        }

        /// <summary>Report a failed/retried request. May trigger an evaluation.</summary>
        public void ReportFailure()
        {
            Interlocked.Increment(ref _totalRequests);
            Interlocked.Increment(ref _windowFailures);
            Interlocked.Increment(ref _cbWindowFailures);
            MaybeEvaluateCircuitBreaker();
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
                double windowSeconds = _windowStopwatch.Elapsed.TotalSeconds;
                double windowRps = windowSeconds > 0 ? total / windowSeconds : 0;
                double overallRps = _lifetimeStopwatch.Elapsed.TotalSeconds > 0
                    ? Volatile.Read(ref _totalRequests) / _lifetimeStopwatch.Elapsed.TotalSeconds : 0;
                int inFlight = Math.Max(0, _currentDop - _semaphore.CurrentCount);

                _windowSuccesses = 0;
                _windowFailures = 0;
                _windowStopwatch.Restart();

                double errorRate = (double)failures / total;

                if (errorRate > ErrorThresholdHigh)
                {
                    int newDop = Math.Max(_minDop, (int)(_currentDop * MultiplicativeDecrease));
                    AdjustDop(newDop, errorRate, windowRps, overallRps, inFlight);
                }
                else if (errorRate < ErrorThresholdLow)
                {
                    int newDop = Math.Min(_maxDop, _currentDop + AdditiveIncrease);
                    AdjustDop(newDop, errorRate, windowRps, overallRps, inFlight);
                }
                else
                {
                    _log.LogInformation(
                        "Adaptive DOP: holding at {Dop} (error rate {ErrorRate:P1}, window RPS {WindowRps:N0}, overall RPS {OverallRps:N0}, in-flight ~{InFlight})",
                        _currentDop, errorRate, windowRps, overallRps, inFlight);
                }
            }
        }

        private void AdjustDop(int newDop, double errorRate, double windowRps = 0, double overallRps = 0, int inFlight = 0)
        {
            int delta = newDop - _currentDop;
            if (delta == 0) return;

            if (delta > 0)
            {
                // Increase: first reclaim tokens from outstanding debt,
                // then release any remainder into the semaphore.
                int toRelease = delta;
                while (toRelease > 0)
                {
                    int debt = Volatile.Read(ref _releaseDebt);
                    if (debt <= 0) break;
                    int reclaim = Math.Min(toRelease, debt);
                    if (Interlocked.CompareExchange(ref _releaseDebt, debt - reclaim, debt) == debt)
                        toRelease -= reclaim;
                }
                if (toRelease > 0)
                    _semaphore.Release(toRelease);
            }
            else
            {
                // Decrease: drain tokens from the semaphore (non-blocking)
                int toDrain = -delta;
                int drained = 0;
                for (int i = 0; i < toDrain; i++)
                {
                    if (!_semaphore.Wait(0)) break;
                    drained++;
                }
                // Tokens we couldn't drain are in-flight — record as debt so
                // they get absorbed when the holding tasks call Release().
                int undrained = toDrain - drained;
                if (undrained > 0)
                {
                    Interlocked.Add(ref _releaseDebt, undrained);
                    _log.LogDebug("Adaptive DOP: drained {Drained}/{Target} tokens ({Undrained} deferred to release debt)",
                        drained, toDrain, undrained);
                }
            }

            _log.LogInformation(
                "Adaptive DOP: {OldDop} → {NewDop} (error rate {ErrorRate:P1}, window RPS {WindowRps:N0}, overall RPS {OverallRps:N0}, in-flight ~{InFlight})",
                _currentDop, newDop, errorRate, windowRps, overallRps, inFlight);
            _currentDop = newDop;
        }

        public void Dispose()
        {
            _healthTimer?.Dispose();
            _rateBucketTimer?.Dispose();
            _rateBucket?.Dispose();
            _semaphore.Dispose();
        }

        // ── Circuit breaker evaluation ──

        private void MaybeEvaluateCircuitBreaker()
        {
            if (_circuitOpen) return;

            lock (_cbLock)
            {
                // Reset window if it's expired
                if (_cbWindowStopwatch.ElapsedMilliseconds >= CircuitBreakerWindowMs)
                {
                    _cbWindowSuccesses = 0;
                    _cbWindowFailures = 0;
                    _cbWindowStopwatch.Restart();
                }

                int total = _cbWindowSuccesses + _cbWindowFailures;
                if (total < CircuitBreakerMinSamples) return;

                double failureRate = (double)_cbWindowFailures / total;
                if (failureRate >= CircuitBreakerThreshold)
                {
                    _circuitOpen = true;
                    _circuitOpensAt = DateTimeOffset.UtcNow;
                    _cbWindowSuccesses = 0;
                    _cbWindowFailures = 0;
                    _cbWindowStopwatch.Restart();

                    _log.LogWarning(
                        "Circuit breaker OPEN: failure rate {FailureRate:P0} ({Failures}/{Total}) in {Window}s window. " +
                        "Pausing new acquisitions for {Pause}s. DOP={Dop}, InFlight={InFlight}.",
                        failureRate, _cbWindowFailures, total,
                        CircuitBreakerWindowMs / 1000, CircuitBreakerPauseMs / 1000,
                        _currentDop, InFlight);
                }
            }
        }

        // ── Periodic health logging ──

        private void LogHealth(object state)
        {
            int inFlight = InFlight;
            if (inFlight <= 0 && Volatile.Read(ref _totalRequests) == 0) return;

            double cbFailureRate;
            lock (_cbLock)
            {
                int cbTotal = _cbWindowSuccesses + _cbWindowFailures;
                cbFailureRate = cbTotal > 0 ? (double)_cbWindowFailures / cbTotal : 0;
            }

            // ── Stale DOP detection ──
            long currentTotal = Volatile.Read(ref _totalRequests);
            if (inFlight >= _currentDop && currentTotal == _lastHealthTotalRequests && _currentDop > 0)
            {
                _consecutiveStaleChecks++;
                if (_consecutiveStaleChecks >= 5)
                {
                    _log.LogCritical(
                        "DOP STALL DETECTED: InFlight={InFlight} == DOP={Dop} with TotalRequests={Total} unchanged " +
                        "for {Minutes} consecutive health checks. All slots may be held by sleeping tasks.",
                        inFlight, _currentDop, currentTotal, _consecutiveStaleChecks);
                }
            }
            else
            {
                _consecutiveStaleChecks = 0;
            }
            _lastHealthTotalRequests = currentTotal;

            _log.LogInformation(
                "Limiter health: DOP={Dop}, InFlight={InFlight}, TotalRequests={Total}, " +
                "CB={CircuitState} (failure rate {CbFailureRate:P0})",
                _currentDop, inFlight, Volatile.Read(ref _totalRequests),
                _circuitOpen ? "OPEN" : "closed", cbFailureRate);
        }
    }
}
