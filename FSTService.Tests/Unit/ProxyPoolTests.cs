using System.Diagnostics;
using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSTService.Tests.Unit;

public sealed class ProxyPoolTests
{
    private readonly ILogger<ProxyPool> _log = NullLogger<ProxyPool>.Instance;

    [Fact]
    public async Task DisabledRecycler_RejectsContainerRestart()
    {
        var recycler = new DisabledProxyContainerRecycler(
            NullLogger<DisabledProxyContainerRecycler>.Instance);

        var restarted = await recycler.RestartAsync("gluetun-1");

        Assert.False(restarted);
    }

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
    public async Task AcquireAsync_PerEndpointRateCap_PacesRepeatedSelections()
    {
        var options = CreateOptions(activeStandby: false);
        options.ProxyUrls.RemoveAt(1);
        options.ContainerNames.RemoveAt(1);
        options.VpnProviders.RemoveAt(1);
        options.ControlUrls.RemoveAt(1);
        options.ProxyMaxRequestsPerSecondPerEndpoint = 5;
        using var pool = new ProxyPool(options, _log);

        using (var first = await pool.AcquireAsync(CancellationToken.None))
            Assert.NotNull(first);

        var sw = Stopwatch.StartNew();
        using var second = await pool.AcquireAsync(CancellationToken.None);
        sw.Stop();

        Assert.NotNull(second);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(100), $"Elapsed: {sw.Elapsed}");
    }

    [Fact]
    public async Task AcquireAsync_PerEndpointConcurrencyCap_WaitsForLeaseRelease()
    {
        var options = CreateOptions(activeStandby: false);
        options.ProxyUrls.RemoveAt(1);
        options.ContainerNames.RemoveAt(1);
        options.VpnProviders.RemoveAt(1);
        options.ControlUrls.RemoveAt(1);
        options.ProxyMaxConcurrentRequestsPerEndpoint = 1;
        using var pool = new ProxyPool(options, _log);

        var first = await pool.AcquireAsync(CancellationToken.None);
        Assert.NotNull(first);

        var secondTask = pool.AcquireAsync(CancellationToken.None).AsTask();
        await Task.Delay(75);
        Assert.False(secondTask.IsCompleted);

        first!.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.NotNull(second);
    }

    [Fact]
    public void PrepareRequest_WhenConnectionReuseDisabled_AddsConnectionClose()
    {
        var options = CreateOptions(activeStandby: false);
        options.ProxyDisableConnectionReuse = true;
        using var pool = new ProxyPool(options, _log);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");

        pool.PrepareRequest(request);

        Assert.True(request.Headers.ConnectionClose);
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
    public async Task RateLimitedEndpoint_CoolsImmediately_AndSuccessDoesNotReleaseCooldown()
    {
        var options = CreateOptions(activeStandby: false);
        options.ProxyCooldownSeconds = 1;
        options.ProxyHttpFailureThreshold = 5;
        using var pool = new ProxyPool(options, _log);

        using var first = await pool.AcquireAsync(CancellationToken.None);
        Assert.NotNull(first);
        using var rateLimitedRequest = RequestFor(first!);
        pool.ReportRateLimited(rateLimitedRequest, TimeSpan.FromSeconds(2));
        first.Dispose();

        using var second = await pool.AcquireAsync(CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(1, second!.Index);

        using var successRequest = RequestFor(0, "gluetun-1");
        pool.ReportSuccess(successRequest);
        second.Dispose();

        using var stillAlternate = await pool.AcquireAsync(CancellationToken.None);
        Assert.NotNull(stillAlternate);
        Assert.Equal(1, stillAlternate!.Index);
    }

    [Fact]
    public async Task RateLimitedEndpoint_CooldownWaitIsCancellable()
    {
        var options = CreateOptions(activeStandby: false);
        options.ProxyUrls.RemoveAt(1);
        options.ContainerNames.RemoveAt(1);
        options.VpnProviders.RemoveAt(1);
        options.ControlUrls.RemoveAt(1);
        options.ProxyCooldownSeconds = 1;
        using var pool = new ProxyPool(options, _log);

        using var request = RequestFor(0, "gluetun-1");
        pool.ReportRateLimited(request, TimeSpan.FromSeconds(5));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.AcquireAsync(cancellation.Token).AsTask());
    }

    [Fact]
    public async Task RateLimitedEndpoint_LongRetryAfterSurvivesBaseAndShorterReport()
    {
        var options = CreateOptions(activeStandby: false);
        options.ProxyUrls.RemoveAt(1);
        options.ContainerNames.RemoveAt(1);
        options.VpnProviders.RemoveAt(1);
        options.ControlUrls.RemoveAt(1);
        options.ProxyCooldownSeconds = 1;
        using var pool = new ProxyPool(options, _log);

        using var request = RequestFor(0, "gluetun-1");
        pool.ReportRateLimited(request, TimeSpan.FromSeconds(3));
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        pool.ReportRateLimited(request, TimeSpan.FromMilliseconds(100));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.AcquireAsync(cancellation.Token).AsTask());
    }

    [Fact]
    public async Task RateLimitedEndpoint_ExtremeRetryAfter_IsCancellableWithoutOverflow()
    {
        var options = CreateOptions(activeStandby: false);
        options.ProxyUrls.RemoveAt(1);
        options.ContainerNames.RemoveAt(1);
        options.VpnProviders.RemoveAt(1);
        options.ControlUrls.RemoveAt(1);
        using var pool = new ProxyPool(options, _log);

        using var request = RequestFor(0, "gluetun-1");
        pool.ReportRateLimited(request, TimeSpan.MaxValue);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.AcquireAsync(cancellation.Token).AsTask());
    }

    [Fact]
    public async Task RegionRotation_UsesOnlyPiaAfterThresholdAndDrainsInflight()
    {
        var options = CreatePiaRotationOptions();
        var rotator = new RecordingRegionRotator();
        using var pool = new ProxyPool(options, _log, new RecordingRecycler(), rotator);

        var lease = await pool.AcquireAsync(CancellationToken.None);
        Assert.NotNull(lease);
        using var request = RequestFor(lease!);
        pool.ReportRateLimited(request, TimeSpan.FromSeconds(4));
        await Task.Delay(80);
        Assert.False(rotator.Started.IsCompleted);

        pool.ReportRateLimited(request, TimeSpan.FromSeconds(4));
        await Task.Delay(80);
        Assert.False(rotator.Started.IsCompleted);

        using var alternate = await pool.AcquireAsync(CancellationToken.None);
        Assert.NotNull(alternate);
        Assert.NotEqual(lease.Index, alternate!.Index);
        lease.Dispose();

        var tunnel = await rotator.Started.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("gluetun-1", tunnel.ContainerName);
        Assert.Equal("http://gluetun-1:8000/", tunnel.ControlUri.ToString());
        Assert.Equal(1, rotator.PeerCount);
        rotator.Complete(ProxyRegionRotationOutcome.Rotated);

        using var next = await pool.AcquireAsync(CancellationToken.None);
        Assert.Equal(alternate.Index, next!.Index);
    }

    [Fact]
    public async Task RegionRotation_FailedRestorationQuarantinesOnlyAffectedExit()
    {
        var rotator = new RecordingRegionRotator();
        using var pool = new ProxyPool(
            CreatePiaRotationOptions(), _log, new RecordingRecycler(), rotator);
        using var request = RequestFor(0, "gluetun-1");

        pool.ReportRateLimited(request, null);
        pool.ReportRateLimited(request, null);
        await rotator.Started.WaitAsync(TimeSpan.FromSeconds(2));
        rotator.Complete(ProxyRegionRotationOutcome.Unsafe);
        await rotator.Finished.WaitAsync(TimeSpan.FromSeconds(2));

        using var alternate = await pool.AcquireAsync(CancellationToken.None);
        Assert.Equal(1, alternate!.Index);

        using var alternateRequest = RequestFor(1, "gluetun-2");
        pool.ReportRateLimited(alternateRequest, TimeSpan.MaxValue);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.AcquireAsync(cancellation.Token).AsTask());
    }

    [Fact]
    public async Task RegionRotation_NeverChangesTwoContainersAtTheSameTime()
    {
        var rotator = new RecordingRegionRotator();
        using var pool = new ProxyPool(
            CreatePiaRotationOptions(), _log, new RecordingRecycler(), rotator);
        using var first = RequestFor(0, "gluetun-1");
        using var second = RequestFor(1, "gluetun-2");

        pool.ReportRateLimited(first, null);
        pool.ReportRateLimited(first, null);
        pool.ReportRateLimited(second, null);
        pool.ReportRateLimited(second, null);

        await rotator.Started.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.Equal(1, rotator.InvocationCount);
        rotator.Complete(ProxyRegionRotationOutcome.Restored);
    }

    [Fact]
    public void RegionRotation_RequiresQualifiedAlignedPiaWorkerConfiguration()
    {
        var options = CreatePiaRotationOptions();
        Assert.Throws<InvalidOperationException>(() => new ProxyPool(options, _log));

        options.VpnProviders[0] = "AirVPN";
        Assert.Contains("PIA", Assert.Throws<InvalidOperationException>(
            () => new ProxyPool(options, _log, new RecordingRecycler(), new RecordingRegionRotator()))
            .Message);
        options.VpnProviders[0] = "PIA";

        options.ProxyRegionRotationRegions.Add("us seattle");
        Assert.Contains("distinct regions", Assert.Throws<InvalidOperationException>(
            () => new ProxyPool(options, _log, new RecordingRecycler(), new RecordingRegionRotator()))
            .Message);
        options.ProxyRegionRotationRegions.RemoveAt(2);

        options.ProxyCurlTempDirectory = "/tmp/unowned-curl-scratch";
        Assert.Contains("same-data-directory", Assert.Throws<InvalidOperationException>(
            () => new ProxyPool(options, _log, new RecordingRecycler(), new RecordingRegionRotator()))
            .Message);
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

    [Fact]
    public void ExpectedEndpointCount_WhenOverlayIsMissing_Throws()
    {
        var options = new ScraperOptions
        {
            ExpectedProxyEndpointCount = 2,
        };

        var act = () => new ProxyPool(options, _log);

        var error = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("ProxyUrls", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedEndpointCount_WhenNegative_Throws()
    {
        var options = CreateOptions(activeStandby: false);
        options.ExpectedProxyEndpointCount = -1;

        var act = () => new ProxyPool(options, _log);

        var error = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("cannot be negative", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PerEndpointRateCap_WhenNegative_Throws()
    {
        var options = CreateOptions(activeStandby: false);
        options.ProxyMaxRequestsPerSecondPerEndpoint = -1;

        var act = () => new ProxyPool(options, _log);

        var error = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("per-endpoint proxy", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PerEndpointConcurrencyCap_WhenNegative_Throws()
    {
        var options = CreateOptions(activeStandby: false);
        options.ProxyMaxConcurrentRequestsPerEndpoint = -1;

        var act = () => new ProxyPool(options, _log);

        var error = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("per-endpoint proxy", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedEndpointCount_WhenMetadataIsMissing_Throws()
    {
        var options = CreateOptions(activeStandby: false);
        options.ExpectedProxyEndpointCount = 2;
        options.ControlUrls.Clear();

        var act = () => new ProxyPool(options, _log);

        var error = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("ControlUrls", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedEndpointCount_WhenContainerNamesAreDuplicated_Throws()
    {
        var options = CreateOptions(activeStandby: false);
        options.ExpectedProxyEndpointCount = 2;
        options.ContainerNames[1] = options.ContainerNames[0];

        var act = () => new ProxyPool(options, _log);

        var error = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("must be unique", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedEndpointCount_WhenProxyUriIsMisaligned_Throws()
    {
        var options = CreateOptions(activeStandby: false);
        options.ExpectedProxyEndpointCount = 2;
        options.ProxyUrls[1] = "http://gluetun-1:8888";

        var act = () => new ProxyPool(options, _log);

        var error = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("proxy endpoint 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedEndpointCount_WhenControlUriIsMisaligned_Throws()
    {
        var options = CreateOptions(activeStandby: false);
        options.ExpectedProxyEndpointCount = 2;
        options.ControlUrls[1] = "http://gluetun-2:8888";

        var act = () => new ProxyPool(options, _log);

        var error = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("control endpoint 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedEndpointCount_WhenConfigurationIsAligned_AllowsPool()
    {
        var options = CreateOptions(activeStandby: false);
        options.ExpectedProxyEndpointCount = 2;

        using var pool = new ProxyPool(options, _log);

        Assert.Equal(2, pool.EndpointCount);
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
            ControlUrls =
            [
                "http://gluetun-1:8000",
                "http://gluetun-2:8000",
            ],
            ProxyActiveStandby = activeStandby,
            ProxyCooldownSeconds = 30,
        };

    private static ScraperOptions CreatePiaRotationOptions()
    {
        var options = CreateOptions(activeStandby: false);
        options.ExpectedProxyEndpointCount = 2;
        options.VpnProviders = ["PIA", "PIA"];
        options.ProxyRegionRotationEnabled = true;
        options.ProxyRegionRotationRegions = ["US Seattle", "DE Frankfurt"];
        options.ProxyRegionRotationRateLimitThreshold = 2;
        options.ProxyUseCurlTransport = true;
        options.ProxyCurlTempDirectory =
            Path.Combine(Path.GetFullPath(options.DataDirectory), "curl-region-test");
        options.ProxyCooldownSeconds = 1;
        return options;
    }

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

        public Task<bool> RestartAsync(
            string containerName, CancellationToken ct = default)
        {
            lock (RestartedContainers)
            {
                RestartedContainers.Add(containerName);
            }

            _restart.TrySetResult(containerName);
            return Task.FromResult(true);
        }

        public Task<bool> IsHealthyAsync(string containerName, CancellationToken ct)
            => Task.FromResult(true);

        public Task<string?> GetConfiguredRegionAsync(
            string containerName, CancellationToken ct)
            => Task.FromResult<string?>("US Las Vegas");

        public async Task WaitForRestartAsync(string expectedContainer)
        {
            var completed = await Task.WhenAny(_restart.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(_restart.Task, completed);
            Assert.Equal(expectedContainer, await _restart.Task);
        }
    }

    private sealed class RecordingRegionRotator : IProxyRegionRotator
    {
        private readonly TaskCompletionSource<ProxyRegionTunnel> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ProxyRegionRotationOutcome> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProxyRegionTunnel> Started => _started.Task;
        public Task Finished => _finished.Task;
        public int PeerCount { get; private set; }
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public async Task<ProxyRegionRotationOutcome> RotateAsync(
            ProxyRegionTunnel tunnel,
            IReadOnlyList<ProxyRegionTunnel> peers,
            IReadOnlyList<string> regions,
            int candidateOffset,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _invocationCount);
            PeerCount = peers.Count;
            _started.TrySetResult(tunnel);
            var outcome = await _result.Task.WaitAsync(ct);
            _finished.TrySetResult();
            return outcome;
        }

        public void Complete(ProxyRegionRotationOutcome outcome)
            => _result.TrySetResult(outcome);

        private int _invocationCount;
    }
}
