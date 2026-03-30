using FortniteFestival.Core.Scraping;
using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class SharedDopPoolTests : IDisposable
{
    private readonly ILogger _log = Substitute.For<ILogger>();
    private SharedDopPool? _pool;

    public void Dispose()
    {
        _pool?.Dispose();
    }

    [Fact]
    public async Task AcquireHighAsync_And_ReleaseHigh_WorkCorrectly()
    {
        _pool = new SharedDopPool(4, 2, 8, 50, _log);

        await _pool.AcquireHighAsync(CancellationToken.None);
        _pool.ReleaseHigh();

        Assert.True(_pool.CurrentDop > 0);
    }

    [Fact]
    public async Task AcquireLowAsync_And_ReleaseLow_WorkCorrectly()
    {
        _pool = new SharedDopPool(4, 2, 8, 50, _log);

        await _pool.AcquireLowAsync(CancellationToken.None);
        _pool.ReleaseLow();

        Assert.True(_pool.CurrentDop > 0);
    }

    [Fact]
    public async Task AcquireLowAsync_CancellationDuringInnerWait_ReleasesGate()
    {
        // Create a pool with 1 slot in the inner limiter
        var inner = new AdaptiveConcurrencyLimiter(1, 1, 1, _log);
        _pool = new SharedDopPool(inner, lowPrioritySlots: 1);

        // Acquire the only slot (high priority)
        await _pool.AcquireHighAsync(CancellationToken.None);

        // Try to acquire low priority — inner wait should block, then cancel
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _pool.AcquireLowAsync(cts.Token));

        // Release the high-priority slot
        _pool.ReleaseHigh();
    }

    [Fact]
    public void ReportSuccess_And_ReportFailure_DoNotThrow()
    {
        _pool = new SharedDopPool(4, 2, 8, 50, _log);
        _pool.ReportSuccess();
        _pool.ReportFailure();
    }

    [Fact]
    public void Limiter_ReturnsInnerLimiter()
    {
        _pool = new SharedDopPool(4, 2, 8, 50, _log);
        Assert.NotNull(_pool.Limiter);
    }

    [Fact]
    public void Dispose_OwnsInner_DisposesInner()
    {
        var pool = new SharedDopPool(4, 2, 8, 50, _log);
        pool.Dispose();
        // Disposing again should not throw
        pool.Dispose();
    }

    [Fact]
    public void Dispose_DoesNotOwnInner_DoesNotDisposeInner()
    {
        var inner = new AdaptiveConcurrencyLimiter(4, 2, 8, _log);
        var pool = new SharedDopPool(inner, lowPrioritySlots: 2);
        pool.Dispose();
        // Inner should still be usable
        Assert.True(inner.CurrentDop > 0);
        inner.Dispose();
    }
}
