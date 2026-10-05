using System.Diagnostics;
using System.Net;
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
        Assert.Equal(["US Seattle", "DE Frankfurt"], rotator.LastRequest!.Regions);
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
    public async Task RegionRotation_SingleRateLimitHoldsExitOutAndRotatesToImmediatelyUsableEgress()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationRateLimitThreshold = 1;
        options.ProxyCooldownSeconds = 30;
        var rotator = new RecordingRegionRotator();
        using var pool = new ProxyPool(options, _log, new RecordingRecycler(), rotator);
        using var request = RequestFor(0, "gluetun-1");

        pool.ReportRateLimited(request, null, "text/html");
        await rotator.Started.WaitAsync(TimeSpan.FromSeconds(2));
        using (var during = await pool.AcquireAsync(CancellationToken.None))
            Assert.Equal(1, during!.Index);

        rotator.Complete(ProxyRegionRotationOutcome.Rotated);
        await rotator.Finished.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => SelectableIndexesAsync(pool, 2), [0, 1]);
    }

    [Fact]
    public async Task RegionRotation_RotatesUpToConfiguredConcurrency()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationRateLimitThreshold = 1;
        options.ProxyRegionRotationMaxConcurrent = 2;
        options.ProxyRegionRotationGlobalIntervalSeconds = 0;
        var rotator = new RecordingRegionRotator();
        using var pool = new ProxyPool(options, _log, new RecordingRecycler(), rotator);
        using var first = RequestFor(0, "gluetun-1");
        using var second = RequestFor(1, "gluetun-2");

        pool.ReportRateLimited(first, null);
        pool.ReportRateLimited(second, null);

        await WaitUntilAsync(() => Task.FromResult(rotator.InvocationCount), 2);
        Assert.Equal(2, rotator.MaxObservedConcurrency);
        rotator.Complete(ProxyRegionRotationOutcome.Rotated);
    }

    [Fact]
    public async Task RegionRotation_ReportsFromPreviousTunnelGenerationAreIgnored()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationRateLimitThreshold = 1;
        options.ProxyTimeoutFailureThreshold = 1;
        options.ProxyCooldownSeconds = 30;
        var rotator = new RecordingRegionRotator();
        using var pool = new ProxyPool(options, _log, new RecordingRecycler(), rotator);

        var lease = await pool.AcquireAsync(CancellationToken.None);
        using var stale = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        lease!.Apply(stale);
        var index = lease.Index;
        lease.Dispose();
        pool.ReportRateLimited(stale, null);
        await rotator.Started.WaitAsync(TimeSpan.FromSeconds(2));
        rotator.Complete(ProxyRegionRotationOutcome.Rotated);
        await rotator.Finished.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => SelectableIndexesAsync(pool, 2), [0, 1]);

        pool.ReportRateLimited(stale, null);
        pool.ReportFailure(stale, ProxyFailureKind.Transport);

        Assert.Equal([0, 1], await SelectableIndexesAsync(pool, 2));
        Assert.Equal(1, rotator.InvocationCount);
        Assert.InRange(index, 0, 1);
    }

    [Fact]
    public async Task RegionRotation_ClaimRejectsPeerEgressAndRecentlyRateLimitedEgress()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationRateLimitThreshold = 1;
        var egress = IPAddress.Parse("198.51.100.20");
        var rotator = new RecordingRegionRotator { RotatedEgress = egress };
        using var pool = new ProxyPool(options, _log, new RecordingRecycler(), rotator);
        using var first = RequestFor(0, "gluetun-1");
        pool.ReportRateLimited(first, null);
        rotator.Complete(ProxyRegionRotationOutcome.Rotated);
        await rotator.Finished.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => SelectableIndexesAsync(pool, 2), [0, 1]);

        Assert.Equal("peer-duplicate", pool.TryClaimEgress(1, egress, allowRateLimited: true));
        var other = IPAddress.Parse("198.51.100.30");
        Assert.Null(pool.TryClaimEgress(1, other, allowRateLimited: false));
        Assert.Equal("peer-duplicate", pool.TryClaimEgress(0, other, allowRateLimited: false));

        pool.ReportRateLimited(first, null);
        Assert.Equal("rate-limited", pool.TryClaimEgress(0, egress, allowRateLimited: false));
        Assert.Null(pool.TryClaimEgress(0, egress, allowRateLimited: true));
        Assert.Equal(1, rotator.InvocationCount);
    }

    [Fact]
    public void TargetEndpoints_ReservesLeastRecentlyUsedQualifiedServerNotHeldByPeers()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationTargetEndpoints = true;
        using var pool = new ProxyPool(options, _log, new RecordingRecycler(), new RecordingRegionRotator());
        var t0 = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        var seattle = IPAddress.Parse("198.51.100.81");
        var frankfurt = IPAddress.Parse("198.51.100.82");
        var unqualified = IPAddress.Parse("198.51.100.83");
        var claimedByPeer = IPAddress.Parse("198.51.100.84");
        pool.RecordKnownServer(seattle, "us seattle", t0);
        pool.RecordKnownServer(frankfurt, "DE Frankfurt", t0 + TimeSpan.FromMinutes(1));
        pool.RecordKnownServer(unqualified, "US Las Vegas", t0 - TimeSpan.FromHours(1));
        pool.RecordKnownServer(claimedByPeer, "US Seattle", t0 - TimeSpan.FromMinutes(10));
        Assert.Null(pool.TryClaimEgress(1, claimedByPeer, allowRateLimited: false));

        Assert.Equal(new ProxyEgressTarget(seattle, "us seattle"), pool.TryReserveTarget(0, []));
        Assert.Equal("peer-duplicate", pool.TryClaimEgress(1, seattle, allowRateLimited: false));
        Assert.Equal(new ProxyEgressTarget(claimedByPeer, "US Seattle"), pool.TryReserveTarget(1, []));
        Assert.Equal(new ProxyEgressTarget(frankfurt, "DE Frankfurt"), pool.TryReserveTarget(0, [seattle]));
        Assert.Null(pool.TryReserveTarget(0, [seattle, frankfurt]));
        Assert.Equal(4, pool.KnownServerCount);
    }

    [Fact]
    public void TargetEndpoints_FailedTargetBacksOffAndRegionCorrectionClearsIt()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationTargetEndpoints = true;
        using var pool = new ProxyPool(options, _log, new RecordingRecycler(), new RecordingRegionRotator());
        var server = IPAddress.Parse("198.51.100.90");
        pool.RecordKnownServer(server, "US Seattle");

        Assert.Equal(server, pool.TryReserveTarget(0, [])?.Address);
        pool.ReportTargetFailed(0, server);
        Assert.Null(pool.TryReserveTarget(0, []));
        Assert.Null(pool.TryReserveTarget(1, []));

        pool.RecordKnownServer(server, "DE Frankfurt");
        Assert.Equal(new ProxyEgressTarget(server, "DE Frankfurt"), pool.TryReserveTarget(1, []));
    }

    [Fact]
    public void TargetEndpoints_DisabledNeverLearnsOrReserves()
    {
        using var pool = new ProxyPool(
            CreatePiaRotationOptions(), _log, new RecordingRecycler(), new RecordingRegionRotator());

        pool.RecordKnownServer(IPAddress.Parse("198.51.100.91"), "US Seattle");

        Assert.Equal(0, pool.KnownServerCount);
        Assert.Null(pool.TryReserveTarget(0, []));
    }

    [Fact]
    public void TargetEndpoints_RequiresRotationWithQualifiedRegions()
    {
        var withoutRotation = CreateOptions(activeStandby: false);
        withoutRotation.ProxyRegionRotationTargetEndpoints = true;
        Assert.Contains("Targeted PIA egress refresh", Assert.Throws<InvalidOperationException>(
            () => new ProxyPool(withoutRotation, _log)).Message);

        var reconnectOnly = CreatePiaRotationOptions();
        reconnectOnly.ProxyRegionRotationRegions = [];
        reconnectOnly.ProxyRegionRotationReconnectInPlace = true;
        reconnectOnly.ProxyRegionRotationTargetEndpoints = true;
        Assert.Contains("Targeted PIA egress refresh", Assert.Throws<InvalidOperationException>(
            () => new ProxyPool(reconnectOnly, _log, new RecordingRecycler(), new RecordingRegionRotator()))
            .Message);
    }

    [Fact]
    public async Task TargetEndpoints_RateLimitedServerRanksByItsLastUseNotAsNeverUsed()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationRateLimitThreshold = 1;
        options.ProxyRegionRotationBurnedEgressTtlSeconds = 1;
        options.ProxyRegionRotationTargetEndpoints = true;
        var burned = IPAddress.Parse("198.51.100.95");
        var rested = IPAddress.Parse("198.51.100.96");
        var rotator = new RecordingRegionRotator
        {
            Egress = uri => uri.Host == "gluetun-1" ? burned : null,
        };
        using var pool = new ProxyPool(options, _log, new RecordingRecycler(), rotator);
        using var request = RequestFor(0, "gluetun-1");
        pool.ReportRateLimited(request, null);
        await rotator.Started.WaitAsync(TimeSpan.FromSeconds(2));
        rotator.Complete(ProxyRegionRotationOutcome.Rotated);
        await rotator.Finished.WaitAsync(TimeSpan.FromSeconds(2));

        pool.RecordKnownServer(burned, "US Seattle");
        pool.RecordKnownServer(rested, "US Seattle", DateTimeOffset.UtcNow - TimeSpan.FromHours(1));
        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        Assert.Equal(rested, pool.TryReserveTarget(1, [])?.Address);
        Assert.Equal(burned, pool.TryReserveTarget(1, [rested])?.Address);
    }

    [Fact]
    public void TargetEndpoints_MinimumRestSkipsRecentlyUsedServers()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationTargetEndpoints = true;
        options.ProxyRegionRotationTargetMinRestSeconds = 600;
        using var pool = new ProxyPool(options, _log, new RecordingRecycler(), new RecordingRegionRotator());
        var now = DateTimeOffset.UtcNow;
        var recent = IPAddress.Parse("198.51.100.97");
        var rested = IPAddress.Parse("198.51.100.98");
        pool.RecordKnownServer(recent, "US Seattle", now - TimeSpan.FromMinutes(5));
        pool.RecordKnownServer(rested, "US Seattle", now - TimeSpan.FromMinutes(15));

        Assert.Equal(rested, pool.TryReserveTarget(0, [])?.Address);
        Assert.Null(pool.TryReserveTarget(1, []));

        options.ProxyRegionRotationTargetMinRestSeconds = 3_601;
        Assert.Contains("minimum rest", Assert.Throws<InvalidOperationException>(
            () => new ProxyPool(options, _log, new RecordingRecycler(), new RecordingRegionRotator()))
            .Message);
    }

    [Fact]
    public async Task TargetEndpoints_SeedAddsOnlyNewQualifiedServersAndPrefersNeverUsed()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationTargetEndpoints = true;
        options.ProxyRegionRotationSeedServerCatalog = true;
        var recycler = new RecordingRecycler();
        using var pool = new ProxyPool(options, _log, recycler, new RecordingRegionRotator());
        var learned = IPAddress.Parse("198.51.100.110");
        var seededSeattle = IPAddress.Parse("198.51.100.111");
        var seededFrankfurt = IPAddress.Parse("198.51.100.112");
        var unqualified = IPAddress.Parse("198.51.100.113");
        pool.RecordKnownServer(learned, "US Seattle", DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
        recycler.ServerLists["gluetun-1"] =
        [
            new(learned, "DE Frankfurt"),
            new(seededSeattle, "US Seattle"),
            new(unqualified, "US Las Vegas"),
        ];
        recycler.ServerLists["gluetun-2"] =
        [
            new(seededSeattle, "US Seattle"),
            new(seededFrankfurt, "de frankfurt"),
        ];

        Assert.Equal(2, await pool.SeedServerCatalogAsync(CancellationToken.None));
        Assert.Equal(3, pool.KnownServerCount);
        var first = pool.TryReserveTarget(0, [])!.Value;
        var second = pool.TryReserveTarget(1, [])!.Value;
        Assert.Equal(
            new HashSet<IPAddress> { seededSeattle, seededFrankfurt },
            new HashSet<IPAddress> { first.Address, second.Address });
        Assert.Equal(new ProxyEgressTarget(learned, "US Seattle"), pool.TryReserveTarget(0, [first.Address]));
        Assert.Equal(0, await pool.SeedServerCatalogAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TargetEndpoints_SeedRecordsEachExitsRegionsForTargets()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationTargetEndpoints = true;
        options.ProxyRegionRotationSeedServerCatalog = true;
        var recycler = new RecordingRecycler();
        using var pool = new ProxyPool(options, _log, recycler, new RecordingRegionRotator());
        var seattle = IPAddress.Parse("198.51.100.120");
        var frankfurt = IPAddress.Parse("198.51.100.121");
        Assert.Equal(["US Seattle", "DE Frankfurt"], pool.RotationRegionsFor(0));
        recycler.ServerLists["gluetun-1"] = [new(seattle, "US Seattle")];
        recycler.ServerLists["gluetun-2"] = [new(seattle, "US Seattle"), new(frankfurt, "de frankfurt")];

        await pool.SeedServerCatalogAsync(CancellationToken.None);

        Assert.Equal(["US Seattle"], pool.RotationRegionsFor(0));
        Assert.Equal(["US Seattle", "DE Frankfurt"], pool.RotationRegionsFor(1));
        Assert.Equal(seattle, pool.TryReserveTarget(0, [])?.Address);
        Assert.Null(pool.TryReserveTarget(0, [seattle]));
        Assert.Equal(frankfurt, pool.TryReserveTarget(1, [seattle])?.Address);
    }

    [Fact]
    public async Task TargetEndpoints_RotationOffersOnlyRegionsInTheExitsList()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationRateLimitThreshold = 1;
        options.ProxyRegionRotationTargetEndpoints = true;
        options.ProxyRegionRotationSeedServerCatalog = true;
        var recycler = new RecordingRecycler();
        var rotator = new RecordingRegionRotator();
        using var pool = new ProxyPool(options, _log, recycler, rotator);
        recycler.ServerLists["gluetun-1"] = [new(IPAddress.Parse("198.51.100.122"), "DE Frankfurt")];
        await pool.SeedServerCatalogAsync(CancellationToken.None);
        using var request = RequestFor(0, "gluetun-1");

        pool.ReportRateLimited(request, null);
        await rotator.Started.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["DE Frankfurt"], rotator.LastRequest!.Regions);
        rotator.Complete(ProxyRegionRotationOutcome.Rotated);
        await rotator.Finished.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void TargetEndpoints_SeedRequiresTargeting()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationSeedServerCatalog = true;
        Assert.Contains("Seeding the PIA server catalog", Assert.Throws<InvalidOperationException>(
            () => new ProxyPool(options, _log, new RecordingRecycler(), new RecordingRegionRotator()))
            .Message);
    }

    [Fact]
    public void ParsePiaServerList_ReadsUdpIpv4AddressesFromGluetunFormats()
    {
        const string wrapped = """
            {"version":1,"timestamp":2,"servers":[
              {"vpn":"openvpn","region":"US East","server_name":"a","tcp":true,"udp":true,"ips":["198.51.100.1","2001:db8::1","bad"]},
              {"vpn":"openvpn","region":"US East","server_name":"b","tcp":true,"udp":false,"ips":["198.51.100.2"]},
              {"vpn":"openvpn","region":"","udp":true,"ips":["198.51.100.3"]},
              {"vpn":"openvpn","region":"Netherlands","udp":true,"ips":["198.51.100.4"]}]}
            """;
        using var wrappedStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(wrapped));
        Assert.Equal(
            [new(IPAddress.Parse("198.51.100.1"), "US East"), new(IPAddress.Parse("198.51.100.4"), "Netherlands")],
            GluetunContainerRecycler.ParsePiaServerList(wrappedStream));

        using var bare = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            """[{"region":"CA Montreal","udp":true,"ips":["198.51.100.5"]}]"""));
        Assert.Equal([new(IPAddress.Parse("198.51.100.5"), "CA Montreal")],
            GluetunContainerRecycler.ParsePiaServerList(bare));

        using var other = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"version\":1}"));
        Assert.Empty(GluetunContainerRecycler.ParsePiaServerList(other));
    }

    [Fact]
    public async Task TargetEndpoints_RotationRequestsTargetingAndLearnsVerifiedServer()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationRateLimitThreshold = 1;
        options.ProxyRegionRotationTargetEndpoints = true;
        var rotator = new RecordingRegionRotator { RotatedRegion = "DE Frankfurt" };
        using var pool = new ProxyPool(options, _log, new RecordingRecycler(), rotator);
        using var request = RequestFor(0, "gluetun-1");

        pool.ReportRateLimited(request, null);
        await rotator.Started.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(rotator.LastRequest!.TargetEndpoints);
        rotator.Complete(ProxyRegionRotationOutcome.Rotated);
        await rotator.Finished.WaitAsync(TimeSpan.FromSeconds(2));

        await WaitUntilAsync(() => Task.FromResult(pool.KnownServerCount), 1);
        // The adopting exit holds it, so it is not a target for anyone.
        Assert.Null(pool.TryReserveTarget(1, []));
    }

    [Fact]
    public async Task RegionRotation_UsesFreshBaselineAndMarksItRateLimited()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationRateLimitThreshold = 1;
        var baseline = IPAddress.Parse("198.51.100.40");
        var rotator = new RecordingRegionRotator
        {
            Egress = uri => uri.Host == "gluetun-1" ? baseline : null,
        };
        using var pool = new ProxyPool(options, _log, new RecordingRecycler(), rotator);
        using var request = RequestFor(0, "gluetun-1");

        pool.ReportRateLimited(request, null);
        await rotator.Started.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(baseline, rotator.LastRequest!.PreviousEgress);
        rotator.Complete(ProxyRegionRotationOutcome.Rotated);
        await rotator.Finished.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => Task.FromResult(
            pool.TryClaimEgress(1, baseline, allowRateLimited: false)), "rate-limited");
        Assert.Null(pool.TryClaimEgress(1, baseline, allowRateLimited: true));
    }

    [Fact]
    public async Task RegionRotation_RestoredResultStartsNewGenerationEvenWithSameEgress()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationRateLimitThreshold = 1;
        options.ProxyCooldownSeconds = 1;
        var rotator = new RecordingRegionRotator();
        using var pool = new ProxyPool(options, _log, new RecordingRecycler(), rotator);
        var lease = await pool.AcquireAsync(CancellationToken.None);
        using var stale = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        lease!.Apply(stale);
        lease.Dispose();

        pool.ReportRateLimited(stale, null);
        await rotator.Started.WaitAsync(TimeSpan.FromSeconds(2));
        rotator.Complete(ProxyRegionRotationOutcome.Restored);
        await rotator.Finished.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => SelectableIndexesAsync(pool, 2), [0, 1]);

        pool.ReportRateLimited(stale, null);
        Assert.Equal([0, 1], await SelectableIndexesAsync(pool, 2));
    }

    [Fact]
    public async Task TransportFailures_WithEgressRefresh_ReconnectFirstAndRestartOnlyAfterDeferredRefresh()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyTimeoutFailureThreshold = 1;
        options.ProxyContainerSelfHealEnabled = true;
        options.ProxyContainerRestartCooldownSeconds = 1;
        options.ProxyContainerRestartMinIntervalSeconds = 1;
        var recycler = new RecordingRecycler();
        var rotator = new RecordingRegionRotator();
        using var pool = new ProxyPool(options, _log, recycler, rotator);

        pool.ReportFailure(0, ProxyFailureKind.Transport);
        await rotator.Started.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(recycler.RestartedContainers);

        rotator.Complete(ProxyRegionRotationOutcome.Deferred);
        await rotator.Finished.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => Task.FromResult(IsRotationIdle(pool, 0)), true);
        pool.ReportFailure(0, ProxyFailureKind.Transport);

        await recycler.WaitForRestartAsync("gluetun-1");
        Assert.Equal(1, rotator.InvocationCount);
    }

    private static bool IsRotationIdle(ProxyPool pool, int index)
        => !pool.IsRegionRotationActive(index);

    [Fact]
    public async Task RegionRotation_QuarantinedExitRetriesVerifiedRefreshAndRejoins()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationRateLimitThreshold = 1;
        options.ProxyRegionRotationQuarantineRetrySeconds = 1;
        options.ProxyRegionRotationMinIntervalSeconds = 5;
        var rotator = new SequencedRegionRotator(
            ProxyRegionRotationOutcome.Unsafe, ProxyRegionRotationOutcome.Rotated);
        using var pool = new ProxyPool(options, _log, new RecordingRecycler(), rotator);
        pool.CensusInitialDelay = TimeSpan.FromMilliseconds(20);
        pool.CensusInterval = TimeSpan.FromMilliseconds(200);
        using var request = RequestFor(0, "gluetun-1");

        pool.ReportRateLimited(request, null);
        await WaitUntilAsync(() => Task.FromResult(rotator.Calls), 1);
        await WaitUntilAsync(() => SelectableIndexesAsync(pool, 2), [1]);

        await WaitUntilAsync(() => Task.FromResult(rotator.Calls), 2, TimeSpan.FromSeconds(12));
        await WaitUntilAsync(() => SelectableIndexesAsync(pool, 2), [0, 1]);
    }

    [Fact]
    public void RegionRotation_QuarantineRetryRangeIsValidated()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationQuarantineRetrySeconds = 3_601;
        Assert.Throws<InvalidOperationException>(
            () => new ProxyPool(options, _log, new RecordingRecycler(), new RecordingRegionRotator()));
    }

    private sealed class SequencedRegionRotator : IProxyRegionRotator
    {
        private readonly Queue<ProxyRegionRotationOutcome> _outcomes;
        private int _calls;

        public SequencedRegionRotator(params ProxyRegionRotationOutcome[] outcomes)
            => _outcomes = new(outcomes);

        public int Calls => Volatile.Read(ref _calls);

        public Task<ProxyRegionRotationResult> RotateAsync(
            ProxyRegionRotationRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            ProxyRegionRotationOutcome outcome;
            lock (_outcomes)
            {
                outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : ProxyRegionRotationOutcome.Deferred;
            }
            return Task.FromResult(new ProxyRegionRotationResult(
                outcome,
                outcome == ProxyRegionRotationOutcome.Rotated ? IPAddress.Parse("198.51.100.77") : null));
        }

        public Task<IPAddress?> GetEgressAsync(Uri proxyUri, CancellationToken ct)
            => Task.FromResult<IPAddress?>(null);
    }

    [Fact]
    public void FormatPercentiles_ReportsNearestRankPercentiles()
    {
        Assert.Equal("n=0", ProxyPool.FormatPercentiles([]));
        var samples = Enumerable.Range(1, 100).Reverse().ToList();
        Assert.Equal("n=100 p50=51 p90=91 p99=100 max=100", ProxyPool.FormatPercentiles(samples));
    }

    [Fact]
    public async Task RegionRotation_RequestBudgetTriggersProactiveRefresh()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationRequestBudget = 10;
        var rotator = new RecordingRegionRotator();
        using var pool = new ProxyPool(options, _log, new RecordingRecycler(), rotator);
        using var request = RequestFor(0, "gluetun-1");

        for (var i = 0; i < 9; i++)
            pool.ReportSuccess(request);
        await Task.Delay(80);
        Assert.False(rotator.Started.IsCompleted);

        pool.ReportSuccess(request);
        var tunnel = await rotator.Started.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("gluetun-1", tunnel.ContainerName);
        rotator.Complete(ProxyRegionRotationOutcome.Rotated);
    }

    [Fact]
    public void RegionRotation_ReconnectInPlaceAllowsEmptyRegionList()
    {
        var options = CreatePiaRotationOptions();
        options.ProxyRegionRotationRegions = [];
        Assert.Throws<InvalidOperationException>(
            () => new ProxyPool(options, _log, new RecordingRecycler(), new RecordingRegionRotator()));

        options.ProxyRegionRotationReconnectInPlace = true;
        using var pool = new ProxyPool(options, _log, new RecordingRecycler(), new RecordingRegionRotator());
        Assert.Equal(2, pool.EndpointCount);

        options.ProxyRegionRotationMaxConcurrent = 3;
        Assert.Throws<InvalidOperationException>(
            () => new ProxyPool(options, _log, new RecordingRecycler(), new RecordingRegionRotator()));
    }

    private static async Task<List<int>> SelectableIndexesAsync(ProxyPool pool, int count)
    {
        var leases = new List<ProxyPool.ProxyLease>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
                try
                {
                    var lease = await pool.AcquireAsync(cancellation.Token);
                    if (lease is not null)
                        leases.Add(lease);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return leases.Select(lease => lease.Index).Distinct().Order().ToList();
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
    }

    private static async Task WaitUntilAsync<T>(Func<Task<T>> probe, T expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        T last = await probe();
        while (!Equals(last, expected) && !(last is System.Collections.IEnumerable a
            && expected is System.Collections.IEnumerable b
            && a.Cast<object>().SequenceEqual(b.Cast<object>())))
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Condition not reached; last value {last}.");
            await Task.Delay(20);
            last = await probe();
        }
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
        options.ProxyRegionRotationMinIntervalSeconds = 5;
        options.ProxyRegionRotationGlobalIntervalSeconds = 0;
        options.ProxyRegionRotationDrainSeconds = 60;
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
        public Dictionary<string, IReadOnlyList<PiaServerAddress>> ServerLists { get; } = new();

        public Task<IReadOnlyList<PiaServerAddress>> ReadPiaServerListAsync(
            string containerName, CancellationToken ct)
            => Task.FromResult(ServerLists.TryGetValue(containerName, out var list)
                ? list
                : (IReadOnlyList<PiaServerAddress>)[]);

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
        private int _concurrent;
        private int _maxConcurrent;
        private int _invocationCount;

        public Task<ProxyRegionTunnel> Started => _started.Task;
        public Task Finished => _finished.Task;
        public ProxyRegionRotationRequest? LastRequest { get; private set; }
        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public int MaxObservedConcurrency => Volatile.Read(ref _maxConcurrent);
        public IPAddress? RotatedEgress { get; set; } = IPAddress.Parse("198.51.100.7");
        public string? RotatedRegion { get; set; }
        public Func<Uri, IPAddress?> Egress { get; set; } = _ => null;

        public async Task<ProxyRegionRotationResult> RotateAsync(
            ProxyRegionRotationRequest request,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _invocationCount);
            var now = Interlocked.Increment(ref _concurrent);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxConcurrent))
                && Interlocked.CompareExchange(ref _maxConcurrent, now, seen) != seen)
            {
            }
            LastRequest = request;
            _started.TrySetResult(request.Tunnel);
            try
            {
                var outcome = await _result.Task.WaitAsync(ct);
                _finished.TrySetResult();
                return new(outcome,
                    outcome == ProxyRegionRotationOutcome.Rotated ? RotatedEgress : null,
                    outcome == ProxyRegionRotationOutcome.Rotated ? RotatedRegion : null);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        public Task<IPAddress?> GetEgressAsync(Uri proxyUri, CancellationToken ct)
            => Task.FromResult(Egress(proxyUri));

        public void Complete(ProxyRegionRotationOutcome outcome)
            => _result.TrySetResult(outcome);
    }
}
