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
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        request.Options.Set(ProxyRequestState.EndpointIndex, lease.Index);
        request.Options.Set(ProxyRequestState.EndpointName, lease.Name);
        return request;
    }
}
