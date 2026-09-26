using System.Net;
using System.Text;
using System.Text.Json;
using FSTService.Scraping;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSTService.Tests.Unit;

public sealed class PiaRegionRotatorTests
{
    private static readonly ProxyRegionTunnel Target =
        new("gluetun-1", new Uri("http://gluetun-1:8888"),
            new Uri("http://gluetun-1:8000"));
    private static readonly ProxyRegionTunnel Peer =
        new("gluetun-2", new Uri("http://gluetun-2:8888"),
            new Uri("http://gluetun-2:8000"));

    [Fact]
    public async Task RotateAsync_ChangesOnlyPiaRegion_AndVerifiesNewDistinctProxyEgress()
    {
        var server = new RecordingControlServer();
        var recycler = new RecordingHealthRecycler(server);
        using var client = new HttpClient(server);
        var rotator = new PiaRegionRotator(client, recycler,
            new RecordingEgressProbe(server), Options(),
            NullLogger<PiaRegionRotator>.Instance);

        var outcome = await rotator.RotateAsync(
            Target, [Peer], ["US Seattle", "DE Frankfurt"], 0,
            CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Rotated, outcome);
        Assert.Equal("US Seattle", server.CurrentRegion);
        Assert.Equal(["US Seattle"], server.PutRegions);
        Assert.Equal(0, recycler.RestartCount);
    }

    [Fact]
    public async Task RotateAsync_RejectsUnqualifiedProviderBeforeAnyMutation()
    {
        var server = new RecordingControlServer { Provider = "airvpn" };
        using var client = new HttpClient(server);
        var rotator = new PiaRegionRotator(client,
            new RecordingHealthRecycler(server),
            new RecordingEgressProbe(server), Options(),
            NullLogger<PiaRegionRotator>.Instance);

        var outcome = await rotator.RotateAsync(
            Target, [Peer], ["US Seattle"], 0, CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Restored, outcome);
        Assert.Empty(server.PutRegions);
    }

    [Fact]
    public async Task RotateAsync_DuplicateNewEgress_RestoresOriginalRegion()
    {
        var server = new RecordingControlServer();
        using var client = new HttpClient(server);
        var rotator = new PiaRegionRotator(client,
            new RecordingHealthRecycler(server),
            new RecordingEgressProbe(server) { DuplicateNewEgress = true },
            Options(), NullLogger<PiaRegionRotator>.Instance);

        var outcome = await rotator.RotateAsync(
            Target, [Peer], ["US Seattle"], 0, CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Restored, outcome);
        Assert.Equal("US Las Vegas", server.CurrentRegion);
        Assert.Equal(["US Seattle", "US Las Vegas"], server.PutRegions);
        Assert.Equal(0, server.CachedPublicIpReads);
    }

    [Fact]
    public async Task RotateAsync_CancellationAfterRegionUpdate_RestoresBeforeRelease()
    {
        var server = new RecordingControlServer();
        var probe = new RecordingEgressProbe(server)
        {
            BlockAfterCandidateUpdate = true,
        };
        using var client = new HttpClient(server);
        var rotator = new PiaRegionRotator(client,
            new RecordingHealthRecycler(server), probe, Options(),
            NullLogger<PiaRegionRotator>.Instance);
        using var cancellation = new CancellationTokenSource();

        var rotation = rotator.RotateAsync(
            Target, [Peer], ["US Seattle"], 0, cancellation.Token);
        await probe.CandidateProbeStarted.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var outcome = await rotation.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ProxyRegionRotationOutcome.Restored, outcome);
        Assert.Equal("US Las Vegas", server.CurrentRegion);
        Assert.Equal(["US Seattle", "US Las Vegas"], server.PutRegions);
    }

    [Fact]
    public async Task RotateAsync_MissingPeerProxyEgress_NeverChangesTunnel()
    {
        var server = new RecordingControlServer();
        using var client = new HttpClient(server);
        var rotator = new PiaRegionRotator(client,
            new RecordingHealthRecycler(server),
            new RecordingEgressProbe(server) { PeerUnavailable = true },
            Options(), NullLogger<PiaRegionRotator>.Instance);

        var outcome = await rotator.RotateAsync(
            Target, [Peer], ["US Seattle"], 0, CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Restored, outcome);
        Assert.Empty(server.PutRegions);
    }

    [Fact]
    public async Task RotateAsync_ControlRollbackCrashes_UsesStaticContainerRecovery()
    {
        var server = new RecordingControlServer { CrashOnRollback = true };
        var recycler = new RecordingHealthRecycler(server);
        using var client = new HttpClient(server);
        var rotator = new PiaRegionRotator(client, recycler,
            new RecordingEgressProbe(server) { DuplicateNewEgress = true },
            Options(), NullLogger<PiaRegionRotator>.Instance);

        var outcome = await rotator.RotateAsync(
            Target, [Peer], ["US Seattle"], 0, CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Restored, outcome);
        Assert.Equal("US Las Vegas", server.CurrentRegion);
        Assert.Equal(1, recycler.RestartCount);
    }

    [Fact]
    public async Task RotateAsync_UnrecoverableExit_IsReportedUnsafe()
    {
        var server = new RecordingControlServer { CrashOnRollback = true };
        var recycler = new RecordingHealthRecycler(server) { RestartSucceeds = false };
        using var client = new HttpClient(server);
        var rotator = new PiaRegionRotator(client, recycler,
            new RecordingEgressProbe(server) { DuplicateNewEgress = true },
            Options(), NullLogger<PiaRegionRotator>.Instance);

        var outcome = await rotator.RotateAsync(
            Target, [Peer], ["US Seattle"], 0, CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Unsafe, outcome);
        Assert.Equal(1, recycler.RestartCount);
    }

    [Fact]
    public async Task RotateAsync_HungDockerRestart_IsBoundedAndUnsafe()
    {
        var server = new RecordingControlServer { CrashOnRollback = true };
        var recycler = new RecordingHealthRecycler(server) { HangOnRestart = true };
        using var client = new HttpClient(server);
        var rotator = new PiaRegionRotator(client, recycler,
            new RecordingEgressProbe(server) { DuplicateNewEgress = true },
            Options(), NullLogger<PiaRegionRotator>.Instance,
            recoveryTimeout: TimeSpan.FromMilliseconds(200));

        var outcome = await rotator.RotateAsync(
            Target, [Peer], ["US Seattle"], 0, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ProxyRegionRotationOutcome.Unsafe, outcome);
        Assert.Equal(1, recycler.RestartCount);
    }

    [Fact]
    public async Task RotateAsync_RestartOnCandidateInsteadOfStaticRegion_IsUnsafe()
    {
        var server = new RecordingControlServer { CrashOnRollback = true };
        var recycler = new RecordingHealthRecycler(server)
        {
            RestartSelectsWrongRegion = true,
        };
        using var client = new HttpClient(server);
        var rotator = new PiaRegionRotator(client, recycler,
            new RecordingEgressProbe(server) { DuplicateNewEgress = true },
            Options(), NullLogger<PiaRegionRotator>.Instance,
            restartTimeout: TimeSpan.FromMilliseconds(200));

        var outcome = await rotator.RotateAsync(
            Target, [Peer], ["US Seattle"], 0, CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Unsafe, outcome);
        Assert.Equal("US Seattle", server.CurrentRegion);
    }

    [Fact]
    public async Task CurlEgressProbe_UsesSpecifiedProxyAndAcceptsOnlyRealIpResponse()
    {
        var proxyUri = Target.ProxyUri;
        var called = 0;
        var probe = new CurlProxyEgressProbe(
            Options(), NullLogger<CurlProxyEgressProbe>.Instance,
            (request, ct) =>
            {
                called++;
                Assert.Equal("api.ipify.org", request.RequestUri!.Host);
                Assert.True(request.Options.TryGetValue(
                    ProxyRequestState.EndpointProxyUri, out var selected));
                Assert.Equal(proxyUri, selected);
                return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        called == 1 ? "192.0.2.9\n" : "not an ip"),
                });
            });

        Assert.Equal(IPAddress.Parse("192.0.2.9"),
            await probe.GetAddressAsync(proxyUri, CancellationToken.None));
        Assert.Null(await probe.GetAddressAsync(proxyUri, CancellationToken.None));
        Assert.Equal(2, called);
    }

    private static ScraperOptions Options()
        => new() { ProxyRegionRotationProbeTimeoutSeconds = 1 };

    private sealed class RecordingControlServer : HttpMessageHandler
    {
        public string CurrentRegion { get; set; } = "US Las Vegas";
        public string Provider { get; set; } = "private internet access";
        public bool CrashOnRollback { get; set; }
        public List<string> PutRegions { get; } = [];
        public int CachedPublicIpReads { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/vpn/settings")
            {
                if (request.Method == HttpMethod.Get)
                {
                    return Json(new
                    {
                        type = "openvpn",
                        provider = new
                        {
                            name = Provider,
                            server_selection = new
                            {
                                regions = new[] { CurrentRegion },
                                cities = (string[]?)null,
                                names = (string[]?)null,
                                countries = (string[]?)null,
                                hostnames = (string[]?)null,
                            },
                        },
                        openvpn = new { username = "never-log-vpn-settings" },
                    });
                }

                using var payload = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                var region = payload.RootElement.GetProperty("provider")
                    .GetProperty("server_selection").GetProperty("regions")[0]
                    .GetString()!;
                PutRegions.Add(region);
                CurrentRegion = region;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        CrashOnRollback && region == "US Las Vegas"
                            ? "already crashed" : "running"),
                };
            }

            if (request.RequestUri.AbsolutePath == "/v1/publicip/ip")
            {
                CachedPublicIpReads++;
                return Json(new { public_ip = "192.0.2.3" });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
            };
    }

    private sealed class RecordingEgressProbe : IProxyEgressProbe
    {
        private readonly RecordingControlServer _server;
        public bool DuplicateNewEgress { get; set; }
        public bool PeerUnavailable { get; set; }
        public bool BlockAfterCandidateUpdate { get; set; }
        private readonly TaskCompletionSource _candidateProbeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CandidateProbeStarted => _candidateProbeStarted.Task;

        public RecordingEgressProbe(RecordingControlServer server)
            => _server = server;

        public async Task<IPAddress?> GetAddressAsync(Uri proxyUri, CancellationToken ct)
        {
            if (proxyUri.Host == "gluetun-2" && PeerUnavailable)
                return null;
            if (proxyUri.Host == "gluetun-1"
                && _server.CurrentRegion != "US Las Vegas"
                && BlockAfterCandidateUpdate)
            {
                _candidateProbeStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            var address = proxyUri.Host == "gluetun-2"
                ? "192.0.2.2"
                : _server.CurrentRegion == "US Las Vegas"
                    ? "192.0.2.1"
                    : DuplicateNewEgress ? "192.0.2.2" : "192.0.2.3";
            return IPAddress.Parse(address);
        }
    }

    private sealed class RecordingHealthRecycler : IProxyContainerRecycler
    {
        private readonly RecordingControlServer _server;
        public bool RestartSucceeds { get; set; } = true;
        public bool HangOnRestart { get; set; }
        public bool RestartSelectsWrongRegion { get; set; }
        public int RestartCount { get; private set; }

        public RecordingHealthRecycler(RecordingControlServer server)
            => _server = server;

        public Task<bool> IsHealthyAsync(string containerName, CancellationToken ct)
            => Task.FromResult(true);

        public Task<string?> GetConfiguredRegionAsync(
            string containerName, CancellationToken ct)
            => Task.FromResult<string?>("US Las Vegas");

        public async Task<bool> RestartAsync(
            string containerName, CancellationToken ct = default)
        {
            RestartCount++;
            if (HangOnRestart)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            if (RestartSucceeds)
                _server.CurrentRegion = RestartSelectsWrongRegion
                    ? "US Seattle" : "US Las Vegas";
            return RestartSucceeds;
        }
    }
}
