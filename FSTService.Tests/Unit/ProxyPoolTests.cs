using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSTService.Tests.Unit;

public sealed class ProxyPoolTests
{
    private readonly ILogger<ProxyPool> _log = NullLogger<ProxyPool>.Instance;

    [Fact]
    public async Task AcquireAsync_LeastLoadedMode_DistributesAcrossAvailableEndpoints()
    {
        using var pool = new ProxyPool(CreateOptions(activeStandby: false), _log);

        using var first = await pool.AcquireAsync(CancellationToken.None);
        using var second = await pool.AcquireAsync(CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Index, second!.Index);
    }

    [Fact]
    public async Task CdnBlock_CoolsEndpoint_AndRoutesToAnotherProxy()
    {
        using var pool = new ProxyPool(CreateOptions(activeStandby: true), _log);

        using (var first = await pool.AcquireAsync(CancellationToken.None))
        {
            Assert.NotNull(first);
            using var request = RequestFor(first!);
            pool.ReportFailure(request, ProxyFailureKind.CdnBlock);
        }

        using var next = await pool.AcquireAsync(CancellationToken.None);

        Assert.NotNull(next);
        Assert.Equal(1, next!.Index);
    }

    [Fact]
    public async Task ReportCdnBlock_WhenAnotherEndpointAvailable_DoesNotRequireGlobalPause()
    {
        using var pool = new ProxyPool(CreateOptions(activeStandby: false), _log);

        using var first = await pool.AcquireAsync(CancellationToken.None);
        Assert.NotNull(first);
        using var request = RequestFor(first!);

        var decision = pool.ReportCdnBlock(request);

        Assert.Equal(ProxyCdnBlockDecision.RetryOnAlternateProxy, decision);
        using var next = await pool.AcquireAsync(CancellationToken.None);
        Assert.NotNull(next);
        Assert.NotEqual(first.Index, next!.Index);
    }

    [Fact]
    public void ReportCdnBlock_WhenAllEndpointsCoolingDown_WaitsForProxyCooldown()
    {
        using var pool = new ProxyPool(CreateOptions(activeStandby: false), _log);

        using var first = RequestFor(0, "gluetun-1");
        using var second = RequestFor(1, "gluetun-2");

        Assert.Equal(ProxyCdnBlockDecision.RetryOnAlternateProxy, pool.ReportCdnBlock(first));
        Assert.Equal(ProxyCdnBlockDecision.WaitForProxyCooldown, pool.ReportCdnBlock(second));
    }

    [Fact]
    public void ReportCdnBlock_WhenRequestHasNoProxyEndpoint_PausesGlobally()
    {
        using var pool = new ProxyPool(CreateOptions(activeStandby: false), _log);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");

        Assert.Equal(ProxyCdnBlockDecision.PauseGlobally, pool.ReportCdnBlock(request));
    }

    [Fact]
    public async Task TimeoutFailures_CoolEndpointOnlyAfterThreshold()
    {
        var options = CreateOptions(activeStandby: true);
        options.ProxyTimeoutFailureThreshold = 2;
        using var pool = new ProxyPool(options, _log);

        int activeIndex;
        using (var first = await pool.AcquireAsync(CancellationToken.None))
        {
            Assert.NotNull(first);
            activeIndex = first!.Index;
            using var request = RequestFor(first);
            pool.ReportFailure(request, ProxyFailureKind.Timeout);
        }

        using (var stillActive = await pool.AcquireAsync(CancellationToken.None))
        {
            Assert.NotNull(stillActive);
            Assert.Equal(activeIndex, stillActive!.Index);
            using var request = RequestFor(stillActive);
            pool.ReportFailure(request, ProxyFailureKind.Timeout);
        }

        using var failedOver = await pool.AcquireAsync(CancellationToken.None);

        Assert.NotNull(failedOver);
        Assert.NotEqual(activeIndex, failedOver!.Index);
    }

    [Fact]
    public async Task TransportFailures_WhenSelfHealEnabled_RestartConfiguredContainer()
    {
        var options = CreateOptions(activeStandby: false);
        options.ProxyTimeoutFailureThreshold = 1;
        options.ProxyContainerSelfHealEnabled = true;
        options.ProxyContainerRestartCooldownSeconds = 1;
        options.ProxyContainerRestartMinIntervalSeconds = 1;
        var recycler = new RecordingRecycler();
        using var pool = new ProxyPool(options, _log, recycler);

        pool.ReportFailure(0, ProxyFailureKind.Transport);

        await recycler.WaitForRestartAsync("gluetun-1");
    }

    [Fact]
    public async Task CdnBlock_WhenSelfHealEnabled_DoesNotRestartContainer()
    {
        var options = CreateOptions(activeStandby: false);
        options.ProxyContainerSelfHealEnabled = true;
        options.ProxyContainerRestartCooldownSeconds = 1;
        options.ProxyContainerRestartMinIntervalSeconds = 1;
        var recycler = new RecordingRecycler();
        using var pool = new ProxyPool(options, _log, recycler);

        pool.ReportFailure(0, ProxyFailureKind.CdnBlock);

        await Task.Delay(100);
        Assert.Empty(recycler.RestartedContainers);
    }

    private static ScraperOptions CreateOptions(bool activeStandby)
        => new()
        {
            ProxyUrls =
            [
                "http://gluetun-1:8888",
                "http://gluetun-2:8888",
            ],
            ContainerNames =
            [
                "gluetun-1",
                "gluetun-2",
            ],
            VpnProviders =
            [
                "AirVPN",
                "AirVPN",
            ],
            ProxyActiveStandby = activeStandby,
            ProxyCooldownSeconds = 30,
        };

    private static HttpRequestMessage RequestFor(ProxyPool.ProxyLease lease)
        => RequestFor(lease.Index, lease.Name);

    private static HttpRequestMessage RequestFor(int index, string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        request.Options.Set(ProxyRequestState.EndpointIndex, index);
        request.Options.Set(ProxyRequestState.EndpointName, name);
        return request;
    }

    private sealed class RecordingRecycler : IProxyContainerRecycler
    {
        private readonly TaskCompletionSource<string> _restart =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> RestartedContainers { get; } = [];

        public Task<bool> RestartAsync(string containerName)
        {
            lock (RestartedContainers)
            {
                RestartedContainers.Add(containerName);
            }

            _restart.TrySetResult(containerName);
            return Task.FromResult(true);
        }

        public async Task WaitForRestartAsync(string expectedContainer)
        {
            var completed = await Task.WhenAny(_restart.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(_restart.Task, completed);
            Assert.Equal(expectedContainer, await _restart.Task);
        }
    }
}
