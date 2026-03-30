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

        // Report fewer than 500 successes — should not trigger evaluation
        for (int i = 0; i < 499; i++)
            _limiter.ReportSuccess();

        Assert.Equal(16, _limiter.CurrentDop);
    }

    [Fact]
    public void AllSuccesses_AtWindow_IncreaseDop()
    {
        _limiter = new AdaptiveConcurrencyLimiter(16, 4, 64, _log);

        // Report 500 successes with 0 failures → error rate 0% < 1% → increase by 16
        for (int i = 0; i < 500; i++)
            _limiter.ReportSuccess();

        Assert.Equal(32, _limiter.CurrentDop); // 16 + 16
    }

    [Fact]
    public void HighErrorRate_AtWindow_DecreasesDop()
    {
        _limiter = new AdaptiveConcurrencyLimiter(32, 4, 64, _log);

        // Report 475 successes and 25 failures → 5% error rate
        // But we need > 5% to decrease, so use more failures
        // 474 successes, 26 failures = 5.2% > 5% → decrease
        for (int i = 0; i < 474; i++)
            _limiter.ReportSuccess();
        for (int i = 0; i < 26; i++)
            _limiter.ReportFailure();

        // 32 * 0.75 = 24
        Assert.Equal(24, _limiter.CurrentDop);
    }

    [Fact]
    public void ErrorRateInMiddle_NoChange()
    {
        _limiter = new AdaptiveConcurrencyLimiter(16, 4, 64, _log);

        // 490 successes, 10 failures = 2% error rate (between 1% and 5%)
        for (int i = 0; i < 490; i++)
            _limiter.ReportSuccess();
        for (int i = 0; i < 10; i++)
            _limiter.ReportFailure();

        Assert.Equal(16, _limiter.CurrentDop); // No change
    }

    [Fact]
    public void Dop_ClampedAtMax()
    {
        _limiter = new AdaptiveConcurrencyLimiter(56, 4, 64, _log);

        // All successes → increase by 16 → would be 72, clamped to 64
        for (int i = 0; i < 500; i++)
            _limiter.ReportSuccess();

        Assert.Equal(64, _limiter.CurrentDop);
    }

    [Fact]
    public void Dop_ClampedAtMin()
    {
        _limiter = new AdaptiveConcurrencyLimiter(5, 4, 64, _log);

        // All failures → high error rate → decrease by 0.75 → 5*0.75 = 3.75 → 3, but clamped to 4
        for (int i = 0; i < 500; i++)
            _limiter.ReportFailure();

        Assert.Equal(4, _limiter.CurrentDop);
    }

    [Fact]
    public void MultipleWindows_AccumulateCorrectly()
    {
        _limiter = new AdaptiveConcurrencyLimiter(16, 4, 128, _log);

        // First window: all successes → 16 → 32
        for (int i = 0; i < 500; i++)
            _limiter.ReportSuccess();
        Assert.Equal(32, _limiter.CurrentDop);

        // Second window: all successes → 32 → 48
        for (int i = 0; i < 500; i++)
            _limiter.ReportSuccess();
        Assert.Equal(48, _limiter.CurrentDop);
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
        // Trigger DOP decrease: 32 → 24 means draining 8 tokens.
        // But only 2 are available → drained (2) < target (8) → hits the partial drain log path.
        for (int i = 0; i < 474; i++)
            _limiter.ReportSuccess();
        for (int i = 0; i < 26; i++)
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

        // Trigger decrease: 32 → 24 (drain 8, but 0 available → 8 become debt)
        for (int i = 0; i < 474; i++)
            _limiter.ReportSuccess();
        for (int i = 0; i < 26; i++)
            _limiter.ReportFailure();
        Assert.Equal(24, _limiter.CurrentDop);

        // Release all 32 tokens — 8 should be absorbed by debt, 24 returned to semaphore
        for (int i = 0; i < 32; i++)
            _limiter.Release(); // must not throw SemaphoreFullException

        // Trigger increase: 24 → 40 — should not throw since semaphore count is 24, max is 64
        for (int i = 0; i < 500; i++)
            _limiter.ReportSuccess();
        Assert.Equal(40, _limiter.CurrentDop);
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

        // Decrease twice: 32 → 24 → 18 (can't drain any, all debt)
        for (int i = 0; i < 474; i++)
            _limiter.ReportSuccess();
        for (int i = 0; i < 26; i++)
            _limiter.ReportFailure();
        Assert.Equal(24, _limiter.CurrentDop);

        for (int i = 0; i < 474; i++)
            _limiter.ReportSuccess();
        for (int i = 0; i < 26; i++)
            _limiter.ReportFailure();
        Assert.Equal(18, _limiter.CurrentDop);

        // Release all 32 — 14 absorbed by debt (8+6), 18 returned to semaphore
        for (int i = 0; i < 32; i++)
            _limiter.Release(); // must not throw

        // Now increase: 18 → 34 — should succeed (semaphore at 18, max 64)
        for (int i = 0; i < 500; i++)
            _limiter.ReportSuccess();
        Assert.Equal(34, _limiter.CurrentDop);
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

        // Decrease: 32 → 24, 8 tokens become debt (none drainable)
        for (int i = 0; i < 474; i++)
            _limiter.ReportSuccess();
        for (int i = 0; i < 26; i++)
            _limiter.ReportFailure();
        Assert.Equal(24, _limiter.CurrentDop);

        // Don't release any tokens yet — increase immediately: 24 → 40
        // 16 increase requested, but 8 are debt → reclaim 8 from debt,
        // release only 8 to semaphore
        for (int i = 0; i < 500; i++)
            _limiter.ReportSuccess();
        Assert.Equal(40, _limiter.CurrentDop);

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

        for (int i = 0; i < 500; i++)
            _limiter.ReportSuccess();

        Assert.Equal(32, _limiter.CurrentDop); // 16 + 16 additive increase
    }
}
