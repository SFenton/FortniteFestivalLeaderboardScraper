using System.Diagnostics;
using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class AdaptiveConcurrencyLimiterTests : IDisposable
{
    private readonly ILogger _log = Substitute.For<ILogger>();
    private AdaptiveConcurrencyLimiter? _limiter;

    public void Dispose()
    {
        _limiter?.Dispose();
    }

    [Fact]
    public void Constructor_SetsInitialDop()
    {
        _limiter = new AdaptiveConcurrencyLimiter(16, 4, 64, _log);
        Assert.Equal(16, _limiter.CurrentDop);
    }

    [Fact]
    public async Task WaitAsync_And_Release_WorkCorrectly()
    {
        _limiter = new AdaptiveConcurrencyLimiter(2, 1, 10, _log);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Should acquire 2 slots without blocking
        await _limiter.WaitAsync(cts.Token);
        await _limiter.WaitAsync(cts.Token);

        // Release both
        _limiter.Release();
        _limiter.Release();

        // Should be able to acquire again
        await _limiter.WaitAsync(cts.Token);
        _limiter.Release();
    }

    [Fact]
    public void ReportSuccess_BelowWindow_DoesNotChangeDop()
    {
        _limiter = new AdaptiveConcurrencyLimiter(16, 4, 64, _log);

        // At DOP=16, effective window = Clamp(16*2, 20, 500) = 32
        // Report fewer than 32 successes — should not trigger evaluation
        for (int i = 0; i < 31; i++)
            _limiter.ReportSuccess();

        Assert.Equal(16, _limiter.CurrentDop);
    }

    [Fact]
    public void AllSuccesses_AtWindow_IncreaseDop()
    {
        _limiter = new AdaptiveConcurrencyLimiter(16, 4, 64, _log);

        // At DOP=16, effective window = Clamp(32, 20, 500) = 32
        // 32 successes → 0% error < 1% → multiplicative increase: ceil(16/0.75) = 22
        for (int i = 0; i < 32; i++)
            _limiter.ReportSuccess();

        Assert.Equal(22, _limiter.CurrentDop); // ceil(16 / 0.75)
    }

    [Fact]
    public void HighErrorRate_AtWindow_DecreasesDop()
    {
        _limiter = new AdaptiveConcurrencyLimiter(32, 4, 64, _log);

        // At DOP=32, effective window = Clamp(64, 20, 500) = 64
        // 60 successes + 4 failures = 6.25% > 5% → decrease
        for (int i = 0; i < 60; i++)
            _limiter.ReportSuccess();
        for (int i = 0; i < 4; i++)
            _limiter.ReportFailure();

        // 32 * 0.75 = 24
        Assert.Equal(24, _limiter.CurrentDop);
    }

    [Fact]
    public void ErrorRateInMiddle_NoChange()
    {
        _limiter = new AdaptiveConcurrencyLimiter(16, 4, 64, _log);

        // At DOP=16, effective window = 32
        // 31 successes, 1 failure = 3.125% (between 1% and 5%) → no change
        for (int i = 0; i < 31; i++)
            _limiter.ReportSuccess();
        for (int i = 0; i < 1; i++)
            _limiter.ReportFailure();

        Assert.Equal(16, _limiter.CurrentDop); // No change
    }

    [Fact]
    public void Dop_ClampedAtMax()
    {
        _limiter = new AdaptiveConcurrencyLimiter(56, 4, 64, _log);

        // At DOP=56, effective window = Clamp(112, 20, 500) = 112
        // All successes → ceil(56/0.75) = 75, clamped to maxDop=64
        for (int i = 0; i < 112; i++)
            _limiter.ReportSuccess();

        Assert.Equal(64, _limiter.CurrentDop);
    }

    [Fact]
    public void Dop_ClampedAtMin()
    {
        _limiter = new AdaptiveConcurrencyLimiter(5, 4, 64, _log);

        // At DOP=5, effective window = Clamp(10, 20, 500) = 20
        // All failures → 100% > 5% → 5*0.75 = 3.75 → 3, clamped to 4
        for (int i = 0; i < 20; i++)
            _limiter.ReportFailure();

        Assert.Equal(4, _limiter.CurrentDop);
    }

    [Fact]
    public void MultipleWindows_AccumulateCorrectly()
    {
        _limiter = new AdaptiveConcurrencyLimiter(16, 4, 128, _log);

        // Window 1: DOP=16, window=32. All successes → ceil(16/0.75)=22
        for (int i = 0; i < 32; i++)
            _limiter.ReportSuccess();
        Assert.Equal(22, _limiter.CurrentDop);

        // Window 2: DOP=22, window=44. All successes → ceil(22/0.75)=30
        for (int i = 0; i < 44; i++)
            _limiter.ReportSuccess();
        Assert.Equal(30, _limiter.CurrentDop);
    }

    [Fact]
    public void MinDop_EnforcedAt1_WhenConfiguredBelow()
    {
        // minDop = 0 should be clamped to 1 internally
        _limiter = new AdaptiveConcurrencyLimiter(2, 0, 64, _log);

        // All failures → decrease → 2*0.75=1, but min should be 1
        for (int i = 0; i < 500; i++)
            _limiter.ReportFailure();

        Assert.True(_limiter.CurrentDop >= 1);
    }

    [Fact]
    public void Constructor_InitialDopExceedsMaxDop_ClampedToMax()
    {
        // Simulates the crash when DegreeOfParallelism (4096) > hardcoded maxDop (2048)
        _limiter = new AdaptiveConcurrencyLimiter(4096, 256, 2048, _log);
        Assert.Equal(2048, _limiter.CurrentDop);
    }

    [Fact]
    public void Constructor_InitialDopBelowMinDop_ClampedToMin()
    {
        _limiter = new AdaptiveConcurrencyLimiter(1, 16, 64, _log);
        Assert.Equal(16, _limiter.CurrentDop);
    }

    [Fact]
    public async Task HighErrorRate_WithInFlightTokens_LogsPartialDrain()
    {
        _limiter = new AdaptiveConcurrencyLimiter(32, 4, 64, _log);

        // Acquire many tokens to keep them "in-flight"
        for (int i = 0; i < 30; i++)
            await _limiter.WaitAsync(CancellationToken.None);

        // Now 30 of 32 tokens are in-flight. Only 2 tokens are in the semaphore.
        // At DOP=32, window=64. Trigger decrease: 32 → 24 means draining 8 tokens.
        // But only 2 are available → drained (2) < target (8) → hits the partial drain log path.
        for (int i = 0; i < 60; i++)
            _limiter.ReportSuccess();
        for (int i = 0; i < 4; i++)
            _limiter.ReportFailure();

        Assert.Equal(24, _limiter.CurrentDop);
    }

    [Fact]
    public async Task DecreaseThenIncrease_WithAllTokensInFlight_DoesNotThrowSemaphoreFullException()
    {
        // Regression: when DOP decreased but tokens couldn't be drained (all in-flight),
        // returning those tokens + a subsequent DOP increase would overflow the semaphore.
        _limiter = new AdaptiveConcurrencyLimiter(32, 4, 64, _log);

        // Acquire all 32 tokens
        for (int i = 0; i < 32; i++)
            await _limiter.WaitAsync(CancellationToken.None);

        // At DOP=32, window=64. Trigger decrease: 32 → 24 (drain 8, but 0 available → 8 become debt)
        for (int i = 0; i < 60; i++)
            _limiter.ReportSuccess();
        for (int i = 0; i < 4; i++)
            _limiter.ReportFailure();
        Assert.Equal(24, _limiter.CurrentDop);

        // Release all 32 tokens — 8 should be absorbed by debt, 24 returned to semaphore
        for (int i = 0; i < 32; i++)
            _limiter.Release(); // must not throw SemaphoreFullException

        // At DOP=24, window=48. All successes → ceil(24/0.75)=32
        for (int i = 0; i < 48; i++)
            _limiter.ReportSuccess();
        Assert.Equal(32, _limiter.CurrentDop);
    }

    [Fact]
    public async Task DecreaseThenIncrease_DebtReclaimedBeforeSemaphoreRelease()
    {
        // Verify that when increasing DOP, outstanding debt is reclaimed first
        // so we don't release more tokens than the semaphore can handle.
        _limiter = new AdaptiveConcurrencyLimiter(32, 4, 64, _log);

        // Acquire all tokens
        for (int i = 0; i < 32; i++)
            await _limiter.WaitAsync(CancellationToken.None);

        // DOP=32, window=64. Decrease: 32 → 24 (can't drain any, all debt)
        for (int i = 0; i < 60; i++)
            _limiter.ReportSuccess();
        for (int i = 0; i < 4; i++)
            _limiter.ReportFailure();
        Assert.Equal(24, _limiter.CurrentDop);

        // DOP=24, window=48. Decrease: 24 → 18 (can't drain any, all debt)
        for (int i = 0; i < 45; i++)
            _limiter.ReportSuccess();
        for (int i = 0; i < 3; i++)
            _limiter.ReportFailure();
        Assert.Equal(18, _limiter.CurrentDop);

        // Release all 32 — 14 absorbed by debt (8+6), 18 returned to semaphore
        for (int i = 0; i < 32; i++)
            _limiter.Release(); // must not throw

        // DOP=18, window=36. All successes → ceil(18/0.75)=24
        for (int i = 0; i < 36; i++)
            _limiter.ReportSuccess();
        Assert.Equal(24, _limiter.CurrentDop);
    }

    [Fact]
    public async Task Increase_WithOutstandingDebt_ReclainsFromDebtFirst()
    {
        // When increasing DOP while debt is still outstanding (tasks haven't
        // returned yet), the increase should reduce debt rather than releasing
        // tokens into the semaphore.
        _limiter = new AdaptiveConcurrencyLimiter(32, 4, 64, _log);

        // Acquire all tokens
        for (int i = 0; i < 32; i++)
            await _limiter.WaitAsync(CancellationToken.None);

        // DOP=32, window=64. Decrease: 32 → 24, 8 tokens become debt (none drainable)
        for (int i = 0; i < 60; i++)
            _limiter.ReportSuccess();
        for (int i = 0; i < 4; i++)
            _limiter.ReportFailure();
        Assert.Equal(24, _limiter.CurrentDop);

        // Don't release any tokens yet — increase immediately:
        // DOP=24, window=48. All successes → ceil(24/0.75)=32 (+8)
        // 8 increase requested, but 8 are debt → reclaim 8 from debt,
        // release 0 to semaphore
        for (int i = 0; i < 48; i++)
            _limiter.ReportSuccess();
        Assert.Equal(32, _limiter.CurrentDop);

        // Now release all 32 in-flight tokens — should not throw
        for (int i = 0; i < 32; i++)
            _limiter.Release();

        // Semaphore should be functional — can acquire and release
        await _limiter.WaitAsync(CancellationToken.None);
        _limiter.Release();
    }

    [Fact]
    public void TotalRequests_IncrementsOnSuccessAndFailure()
    {
        _limiter = new AdaptiveConcurrencyLimiter(16, 4, 64, _log);

        Assert.Equal(0, _limiter.TotalRequests);

        _limiter.ReportSuccess();
        _limiter.ReportSuccess();
        _limiter.ReportFailure();

        Assert.Equal(3, _limiter.TotalRequests);
    }

    [Fact]
    public async Task InFlight_ReflectsAcquiredSlots()
    {
        _limiter = new AdaptiveConcurrencyLimiter(16, 4, 64, _log);

        Assert.Equal(0, _limiter.InFlight);

        await _limiter.WaitAsync(CancellationToken.None);
        await _limiter.WaitAsync(CancellationToken.None);
        Assert.Equal(2, _limiter.InFlight);

        _limiter.Release();
        Assert.Equal(1, _limiter.InFlight);

        _limiter.Release();
        Assert.Equal(0, _limiter.InFlight);
    }

    // ─── Rate limiter tests ────────────────────────────────────

    [Fact]
    public void MaxRequestsPerSecond_Zero_MeansUnlimited()
    {
        _limiter = new AdaptiveConcurrencyLimiter(16, 4, 64, _log, maxRequestsPerSecond: 0);
        Assert.Equal(0, _limiter.MaxRequestsPerSecond);
    }

    [Fact]
    public async Task MaxRequestsPerSecond_StillAllowsConcurrency()
    {
        // High rate limit (won't actually throttle), but verify WaitAsync works
        _limiter = new AdaptiveConcurrencyLimiter(4, 1, 8, _log, maxRequestsPerSecond: 10000);
        Assert.Equal(10000, _limiter.MaxRequestsPerSecond);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _limiter.WaitAsync(cts.Token);
        await _limiter.WaitAsync(cts.Token);
        Assert.Equal(2, _limiter.InFlight);

        _limiter.Release();
        _limiter.Release();
        Assert.Equal(0, _limiter.InFlight);
    }

    [Fact]
    public async Task MaxRequestsPerSecond_ThrottlesThroughput()
    {
        // Set rate to 100 RPS → tokens per tick = max(1, 100/20) = 5, every 50ms.
        // Over 200ms (4 ticks) we expect ~20 tokens.
        // Without rate limiting, 16 DOP with instant work would complete instantly.
        _limiter = new AdaptiveConcurrencyLimiter(16, 4, 64, _log, maxRequestsPerSecond: 100);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sw = Stopwatch.StartNew();
        int completed = 0;

        // Fire 20 requests — with 100 RPS cap, should take ~150-250ms
        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await _limiter.WaitAsync(cts.Token);
                Interlocked.Increment(ref completed);
                _limiter.Release();
            }, cts.Token));
        }

        await Task.WhenAll(tasks);
        sw.Stop();

        Assert.Equal(20, completed);
        // Should take at least 100ms (rate limiting in effect, not instant)
        Assert.True(sw.ElapsedMilliseconds >= 50,
            $"Expected >= 50ms but completed in {sw.ElapsedMilliseconds}ms — rate limiter may not be working");
    }

    [Fact]
    public async Task MaxRequestsPerSecond_CoexistsWithAimd()
    {
        // Rate-limited + AIMD: report successes, DOP should still increase
        _limiter = new AdaptiveConcurrencyLimiter(16, 4, 64, _log, maxRequestsPerSecond: 10000);

        // At DOP=16, window=32. All successes → ceil(16/0.75)=22
        for (int i = 0; i < 32; i++)
            _limiter.ReportSuccess();

        Assert.Equal(22, _limiter.CurrentDop); // ceil(16 / 0.75) multiplicative increase
    }

    [Fact]
    public void ScaledEvalWindow_RecoveryFromMinDop()
    {
        // Verify the full recovery chain from minDop=4 back to maxDop.
        // Each eval window is DOP×2 (min 20), increase is ÷0.75.
        _limiter = new AdaptiveConcurrencyLimiter(4, 4, 575, _log);

        // Track the recovery chain
        var chain = new List<int> { 4 };
        while (_limiter.CurrentDop < 575)
        {
            int window = Math.Clamp(_limiter.CurrentDop * 2, 20, 500);
            for (int i = 0; i < window; i++)
                _limiter.ReportSuccess();
            chain.Add(_limiter.CurrentDop);
        }

        // Verify it reaches max and the chain is monotonically increasing
        Assert.Equal(575, _limiter.CurrentDop);
        for (int i = 1; i < chain.Count; i++)
            Assert.True(chain[i] > chain[i - 1],
                $"DOP should increase: chain[{i - 1}]={chain[i - 1]}, chain[{i}]={chain[i]}");

        // With multiplicative ÷0.75 and scaled windows, recovery should be fast (<25 evals)
        Assert.True(chain.Count <= 25,
            $"Recovery took {chain.Count - 1} evals — expected ≤24. Chain: {string.Join("→", chain)}");
    }

    [Fact]
    public void ScaledEvalWindow_SmallDopUsesMinWindow()
    {
        // At DOP=4, effective window should be min(20), not 4×2=8
        _limiter = new AdaptiveConcurrencyLimiter(4, 4, 575, _log);

        // 19 reports should not trigger eval
        for (int i = 0; i < 19; i++)
            _limiter.ReportSuccess();
        Assert.Equal(4, _limiter.CurrentDop);

        // 20th report triggers eval → increase to ceil(4/0.75) = ceil(5.33) = 6
        _limiter.ReportSuccess();
        Assert.Equal(6, _limiter.CurrentDop);
    }

    [Fact]
    public void ScaledEvalWindow_LargeDopUsesFullWindow()
    {
        // At DOP=300, effective window should be 500 (clamped at max)
        _limiter = new AdaptiveConcurrencyLimiter(300, 4, 575, _log);

        // 499 reports should not trigger eval
        for (int i = 0; i < 499; i++)
            _limiter.ReportSuccess();
        Assert.Equal(300, _limiter.CurrentDop);

        // 500th report triggers eval
        _limiter.ReportSuccess();
        Assert.Equal(400, _limiter.CurrentDop); // ceil(300/0.75) = 400
    }

    // ─── TCP slow start / ssthresh tests ────────────────────────

    [Fact]
    public void SlashDop_SetsSsthresh()
    {
        _limiter = new AdaptiveConcurrencyLimiter(575, 4, 575, _log);
        Assert.Equal(0, _limiter.SlowStartThreshold);

        _limiter.SlashDop();

        Assert.Equal(4, _limiter.CurrentDop);
        Assert.Equal(287, _limiter.SlowStartThreshold); // 575 / 2
    }

    [Fact]
    public void SlashDop_SsthreshClampedToMinDop()
    {
        // If preDop is small enough, ssthresh should be clamped to minDop
        _limiter = new AdaptiveConcurrencyLimiter(6, 4, 575, _log);
        _limiter.SlashDop();

        Assert.Equal(4, _limiter.CurrentDop);
        Assert.Equal(4, _limiter.SlowStartThreshold); // max(4, 6/2=3) = 4
    }

    [Fact]
    public void PostCdn_UsesLargerMinEvalWindow()
    {
        _limiter = new AdaptiveConcurrencyLimiter(100, 4, 575, _log);
        _limiter.SlashDop(); // 100 → 4, ssthresh = 50

        Assert.Equal(4, _limiter.CurrentDop);
        Assert.Equal(50, _limiter.SlowStartThreshold);

        // At DOP=4 with ssthresh>0: minWindow=100 (not 20)
        // 99 reports should not trigger eval
        for (int i = 0; i < 99; i++)
            _limiter.ReportSuccess();
        Assert.Equal(4, _limiter.CurrentDop);

        // 100th report triggers eval → ceil(4/0.75) = 6
        _limiter.ReportSuccess();
        Assert.Equal(6, _limiter.CurrentDop);
    }

    [Fact]
    public void PostCdn_TwoPhaseRecovery()
    {
        _limiter = new AdaptiveConcurrencyLimiter(575, 4, 575, _log);
        _limiter.SlashDop(); // 575 → 4, ssthresh = 287

        Assert.Equal(4, _limiter.CurrentDop);
        Assert.Equal(287, _limiter.SlowStartThreshold);

        var chain = new List<int> { 4 };
        bool sawAdditivePhase = false;

        while (_limiter.CurrentDop < 575)
        {
            int prevDop = _limiter.CurrentDop;
            int minWindow = _limiter.SlowStartThreshold > 0 ? 100 : 20;
            int window = Math.Clamp(_limiter.CurrentDop * 2, minWindow, 500);

            for (int i = 0; i < window; i++)
                _limiter.ReportSuccess();

            int newDop = _limiter.CurrentDop;
            chain.Add(newDop);

            // Detect additive phase: increase of exactly 16
            if (newDop - prevDop == 16)
                sawAdditivePhase = true;
        }

        Assert.Equal(575, _limiter.CurrentDop);
        Assert.Equal(0, _limiter.SlowStartThreshold); // cleared at maxDop
        Assert.True(sawAdditivePhase,
            $"Expected additive +16 phase above ssthresh. Chain: {string.Join("→", chain)}");

        // Two-phase recovery should take more evals than pure multiplicative (≤24)
        Assert.True(chain.Count > 25,
            $"Expected >25 evals with ssthresh, got {chain.Count - 1}. Chain: {string.Join("→", chain)}");
    }

    [Fact]
    public void PostCdn_CongestionAvoidanceIncrementIsAdditive()
    {
        // Start above ssthresh to exercise only the additive phase
        _limiter = new AdaptiveConcurrencyLimiter(300, 4, 575, _log);
        _limiter.SlashDop(); // 300 → 4, ssthresh = 150

        // Pump up to 150 (the ssthresh) using multiplicative slow start
        while (_limiter.CurrentDop < 150)
        {
            int window = Math.Clamp(_limiter.CurrentDop * 2, 100, 500);
            for (int i = 0; i < window; i++)
                _limiter.ReportSuccess();
        }

        // Now at or above ssthresh — next increase should be additive +16
        int dopBefore = _limiter.CurrentDop;
        int evalWindow = Math.Clamp(dopBefore * 2, 100, 500);
        for (int i = 0; i < evalWindow; i++)
            _limiter.ReportSuccess();

        Assert.Equal(dopBefore + 16, _limiter.CurrentDop);
    }

    [Fact]
    public void NoSsthresh_NormalAimd_UsesMultiplicativeIncrease()
    {
        // Without SlashDop, ssthresh=0 → always multiplicative
        _limiter = new AdaptiveConcurrencyLimiter(100, 4, 575, _log);
        Assert.Equal(0, _limiter.SlowStartThreshold);

        // At DOP=100, window=200. All successes → ceil(100/0.75) = 134
        for (int i = 0; i < 200; i++)
            _limiter.ReportSuccess();

        Assert.Equal(134, _limiter.CurrentDop); // multiplicative, not +16
        Assert.Equal(0, _limiter.SlowStartThreshold);
    }
}
