using System.Net;
using FortniteFestival.Core.Scraping;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
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

        var token = await _pool.AcquireLowAsync(CancellationToken.None);
        _pool.ReleaseLow(token);

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

    [Fact]
    public async Task AcquireLowAsync_BackgroundWaitsDuringForegroundRegistration()
    {
        var coordinator = new EpicTrafficCoordinator();
        _pool = new SharedDopPool(1, 1, 1, 100, _log, trafficCoordinator: coordinator);

        using var lease = coordinator.BeginForegroundRegistration();
        var acquireTask = _pool.AcquireLowAsync(CancellationToken.None);

        var completedWhileForeground = await Task.WhenAny(acquireTask, Task.Delay(TimeSpan.FromMilliseconds(50))) == acquireTask;
        Assert.False(completedWhileForeground);

        lease.Dispose();
        var token = await acquireTask.WaitAsync(TimeSpan.FromSeconds(1));
        _pool.ReleaseLow(token);
    }

    [Fact]
    public async Task AcquireHighAsync_ForegroundRegistrationBypassesForegroundGate()
    {
        var coordinator = new EpicTrafficCoordinator();
        _pool = new SharedDopPool(1, 1, 1, 100, _log, trafficCoordinator: coordinator);

        using var lease = coordinator.BeginForegroundRegistration();

        await _pool.AcquireHighAsync(
            CancellationToken.None,
            EpicTrafficKind.ForegroundRegistration).WaitAsync(TimeSpan.FromSeconds(1));
        _pool.ReleaseHigh();
    }

    [Fact]
    public async Task WaitForTurnAsync_AdmittedBackgroundRequest_BypassesLaterForegroundGate()
    {
        var coordinator = new EpicTrafficCoordinator();

        using var admittedRequest = coordinator.BeginAdmittedRequest();
        using var lease = coordinator.BeginForegroundRegistration();

        await coordinator.WaitForTurnAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WithCdnResilience_AdmittedLowPrioritySend_DoesNotDeadlockBehindForegroundLease()
    {
        var coordinator = new EpicTrafficCoordinator();
        _pool = new SharedDopPool(1, 1, 1, 100, _log, trafficCoordinator: coordinator);

        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk("""{"ok":true}""");
        using var http = new HttpClient(handler);
        var executor = new ResilientHttpExecutor(http, _log, coordinator);

        LowPriorityToken lowToken = default;
        using var response = await executor.WithCdnResilienceAsync(
            work: async () =>
            {
                using var lease = coordinator.BeginForegroundRegistration();
                return await executor.SendAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, "https://example.com/api/test"),
                    _pool.Limiter,
                    "admitted-background",
                    maxRetries: 0,
                    CancellationToken.None);
            },
            CancellationToken.None,
            acquireSlot: async () => { lowToken = await _pool.AcquireLowAsync(CancellationToken.None); },
            releaseSlot: () =>
            {
                _pool.ReleaseLow(lowToken);
                lowToken = default;
            }).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(handler.Requests);
    }
}
