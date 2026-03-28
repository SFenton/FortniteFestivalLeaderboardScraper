using System;
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

        /// <summary>Current effective DOP.</summary>
        public int CurrentDop
        {
            get { return Volatile.Read(ref _currentDop); }
        }

        public AdaptiveConcurrencyLimiter(int initialDop, int minDop, int maxDop, ILogger log)
        {
            _minDop = Math.Max(1, minDop);
            _maxDop = Math.Max(_minDop, maxDop);
            _currentDop = Math.Clamp(initialDop, _minDop, _maxDop);
            _log = log;

            // Semaphore max is set to maxDop so we have headroom to Release() into.
            _semaphore = new SemaphoreSlim(_currentDop, _maxDop);
        }

        /// <summary>Wait for a concurrency slot (same as SemaphoreSlim.WaitAsync).</summary>
        public Task WaitAsync(CancellationToken ct)
        {
            return _semaphore.WaitAsync(ct);
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

            _log.LogInformation("Adaptive DOP: {OldDop} → {NewDop} (error rate {ErrorRate:P1})",
                _currentDop, newDop, errorRate);
            _currentDop = newDop;
        }

        public void Dispose()
        {
            _semaphore.Dispose();
        }
    }
}
