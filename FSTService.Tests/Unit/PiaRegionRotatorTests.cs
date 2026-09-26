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
    private static readonly IPAddress Original = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress Peer = IPAddress.Parse("192.0.2.2");

    [Fact]
    public async Task RotateAsync_ChangesOnlyPiaRegion_AndVerifiesNewDistinctProxyEgress()
    {
        var server = new RecordingControlServer();
        var recycler = new RecordingHealthRecycler(server);
        var claims = new RecordingClaims();
        using var client = new HttpClient(server);
        var rotator = CreateRotator(client, recycler, new RecordingEgressProbe(server));

        var result = await rotator.RotateAsync(
            Request(claims, ["US Seattle", "DE Frankfurt"]), CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Rotated, result.Outcome);
        Assert.Equal("US Seattle", result.Region);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), result.Egress);
        Assert.Equal("US Seattle", server.CurrentRegion);
        Assert.Equal(["US Seattle"], server.PutRegions);
        Assert.Equal(0, server.Reconnects);
        Assert.Equal(0, recycler.RestartCount);
        Assert.Equal(result.Egress, claims.LastClaimed);
    }

    [Fact]
    public async Task RotateAsync_ReconnectInPlace_KeepsRegionAndAcceptsNewServerEgress()
    {
        var server = new RecordingControlServer();
        using var client = new HttpClient(server);
        var rotator = CreateRotator(client, new RecordingHealthRecycler(server),
            new RecordingEgressProbe(server));

        var result = await rotator.RotateAsync(
            Request(new RecordingClaims(), [], reconnectInPlace: true),
            CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Rotated, result.Outcome);
        Assert.Equal("US Las Vegas", result.Region);
        Assert.Equal(1, server.Reconnects);
        Assert.Empty(server.PutRegions);
        Assert.NotEqual(Original, result.Egress);
    }

    [Fact]
    public async Task RotateAsync_ReconnectToRateLimitedEgress_TriesAnotherServer()
    {
        var server = new RecordingControlServer();
        var claims = new RecordingClaims();
        claims.RateLimited.Add(IPAddress.Parse("192.0.2.21"));
        using var client = new HttpClient(server);
        var rotator = CreateRotator(client, new RecordingHealthRecycler(server),
            new RecordingEgressProbe(server), maxAttempts: 3);

        var result = await rotator.RotateAsync(
            Request(claims, [], reconnectInPlace: true), CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Rotated, result.Outcome);
        Assert.Equal(IPAddress.Parse("192.0.2.22"), result.Egress);
        Assert.Equal(2, server.Reconnects);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task RotateAsync_DeadCandidate_FallsBackToNextRegionWithoutWaitingForOverallDeadline()
    {
        var server = new RecordingControlServer();
        using var client = new HttpClient(server);
        var probe = new RecordingEgressProbe(server) { DeadRegions = { "US Seattle" } };
        var rotator = CreateRotator(client, new RecordingHealthRecycler(server), probe,
            maxAttempts: 2, attemptTimeout: TimeSpan.FromMilliseconds(250));

        var result = await rotator.RotateAsync(
            Request(new RecordingClaims(), ["US Seattle", "DE Frankfurt"]),
            CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Rotated, result.Outcome);
        Assert.Equal("DE Frankfurt", result.Region);
        Assert.Equal(["US Seattle", "DE Frankfurt"], server.PutRegions);
    }

    [Fact]
    public async Task RotateAsync_OnlyRateLimitedCandidates_KeepsWorkingTunnelWithoutRollback()
    {
        var server = new RecordingControlServer();
        var recycler = new RecordingHealthRecycler(server);
        var claims = new RecordingClaims();
        claims.RateLimited.Add(IPAddress.Parse("192.0.2.10"));
        using var client = new HttpClient(server);
        var rotator = CreateRotator(client, recycler, new RecordingEgressProbe(server), maxAttempts: 1);

        var result = await rotator.RotateAsync(
            Request(claims, ["US Seattle"]), CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Restored, result.Outcome);
        Assert.Equal("US Seattle", result.Region);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), result.Egress);
        Assert.Equal(["US Seattle"], server.PutRegions);
        Assert.Equal(0, recycler.RestartCount);
    }

    [Fact]
    public async Task RotateAsync_RejectsUnqualifiedProviderBeforeAnyMutation()
    {
        var server = new RecordingControlServer { Provider = "airvpn" };
        using var client = new HttpClient(server);
        var rotator = CreateRotator(client, new RecordingHealthRecycler(server),
            new RecordingEgressProbe(server));

        var result = await rotator.RotateAsync(
            Request(new RecordingClaims(), ["US Seattle"]), CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Deferred, result.Outcome);
        Assert.Empty(server.PutRegions);
        Assert.Equal(0, server.Reconnects);
    }

    [Fact]
    public async Task RotateAsync_DuplicateNewEgress_RestoresOriginalRegion()
    {
        var server = new RecordingControlServer();
        using var client = new HttpClient(server);
        var rotator = CreateRotator(client, new RecordingHealthRecycler(server),
            new RecordingEgressProbe(server) { DuplicateNewEgress = true }, maxAttempts: 1);

        var result = await rotator.RotateAsync(
            Request(new RecordingClaims { Peers = { Peer } }, ["US Seattle"]),
            CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Restored, result.Outcome);
        Assert.Equal(Original, result.Egress);
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
        var rotator = CreateRotator(client, new RecordingHealthRecycler(server), probe);
        using var cancellation = new CancellationTokenSource();

        var rotation = rotator.RotateAsync(
            Request(new RecordingClaims(), ["US Seattle"]), cancellation.Token);
        await probe.CandidateProbeStarted.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await rotation.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ProxyRegionRotationOutcome.Restored, result.Outcome);
        Assert.Equal("US Las Vegas", server.CurrentRegion);
        Assert.Equal(["US Seattle", "US Las Vegas"], server.PutRegions);
    }

    [Fact]
    public async Task RotateAsync_ControlReadFailure_NeverChangesTunnel()
    {
        var server = new RecordingControlServer { FailSettingsRead = true };
        using var client = new HttpClient(server);
        var rotator = CreateRotator(client, new RecordingHealthRecycler(server),
            new RecordingEgressProbe(server));

        var result = await rotator.RotateAsync(
            Request(new RecordingClaims(), ["US Seattle"]), CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Deferred, result.Outcome);
        Assert.Empty(server.PutRegions);
    }

    [Fact]
    public async Task RotateAsync_ControlRollbackCrashes_UsesStaticContainerRecovery()
    {
        var server = new RecordingControlServer { CrashOnRollback = true };
        var recycler = new RecordingHealthRecycler(server);
        using var client = new HttpClient(server);
        var rotator = CreateRotator(client, recycler,
            new RecordingEgressProbe(server) { DuplicateNewEgress = true }, maxAttempts: 1);

        var result = await rotator.RotateAsync(
            Request(new RecordingClaims { Peers = { Peer } }, ["US Seattle"]),
            CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Restored, result.Outcome);
        Assert.Equal("US Las Vegas", server.CurrentRegion);
        Assert.Equal(1, recycler.RestartCount);
    }

    [Fact]
    public async Task RotateAsync_SlowControlRollback_WaitsForRealEgressWithoutRestart()
    {
        var server = new RecordingControlServer();
        var recycler = new RecordingHealthRecycler(server);
        using var client = new HttpClient(server);
        var rotator = CreateRotator(client, recycler,
            new RecordingEgressProbe(server)
            {
                DuplicateNewEgress = true,
                RestorationFailuresRemaining = 2,
            },
            maxAttempts: 1,
            rollbackProbeTimeout: TimeSpan.FromMilliseconds(500));

        var result = await rotator.RotateAsync(
            Request(new RecordingClaims { Peers = { Peer } }, ["US Seattle"]),
            CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Restored, result.Outcome);
        Assert.Equal(["US Seattle", "US Las Vegas"], server.PutRegions);
        Assert.Equal(0, recycler.RestartCount);
    }

    [Fact]
    public async Task RotateAsync_UnrecoverableExit_IsReportedUnsafe()
    {
        var server = new RecordingControlServer { CrashOnRollback = true };
        var recycler = new RecordingHealthRecycler(server) { RestartSucceeds = false };
        using var client = new HttpClient(server);
        var rotator = CreateRotator(client, recycler,
            new RecordingEgressProbe(server) { DuplicateNewEgress = true }, maxAttempts: 1);

        var result = await rotator.RotateAsync(
            Request(new RecordingClaims { Peers = { Peer } }, ["US Seattle"]),
            CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Unsafe, result.Outcome);
        Assert.Equal(1, recycler.RestartCount);
    }

    [Fact]
    public async Task RotateAsync_HungDockerRestart_IsBoundedAndUnsafe()
    {
        var server = new RecordingControlServer { CrashOnRollback = true };
        var recycler = new RecordingHealthRecycler(server) { HangOnRestart = true };
        using var client = new HttpClient(server);
        var rotator = CreateRotator(client, recycler,
            new RecordingEgressProbe(server) { DuplicateNewEgress = true }, maxAttempts: 1,
            recoveryTimeout: TimeSpan.FromMilliseconds(200));

        var result = await rotator.RotateAsync(
            Request(new RecordingClaims { Peers = { Peer } }, ["US Seattle"]),
            CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ProxyRegionRotationOutcome.Unsafe, result.Outcome);
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
        var rotator = CreateRotator(client, recycler,
            new RecordingEgressProbe(server) { DuplicateNewEgress = true }, maxAttempts: 1,
            restartTimeout: TimeSpan.FromMilliseconds(200));

        var result = await rotator.RotateAsync(
            Request(new RecordingClaims { Peers = { Peer } }, ["US Seattle"]),
            CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Unsafe, result.Outcome);
        Assert.Equal("US Seattle", server.CurrentRegion);
    }

    [Fact]
    public void BuildCandidates_ReconnectInPlaceThenRotatesListedRegionsFromOffset()
    {
        var server = new RecordingControlServer();
        using var client = new HttpClient(server);
        var rotator = CreateRotator(client, new RecordingHealthRecycler(server),
            new RecordingEgressProbe(server), maxAttempts: 4);

        var candidates = rotator.BuildCandidates(
            "US Las Vegas",
            Request(new RecordingClaims(), ["CA Vancouver", "US Las Vegas", "US Denver"],
                reconnectInPlace: true, offset: 2));

        Assert.Equal(
            ["US Las Vegas", "US Denver", "CA Vancouver", "US Las Vegas"],
            candidates);
    }

    [Theory]
    [InlineData("running", "running")]
    [InlineData("{\"outcome\":\"running\"}", "running")]
    [InlineData(" {\"outcome\":\"stopped\"}\n", "stopped")]
    [InlineData("settings left unchanged", "settings left unchanged")]
    [InlineData("{\"openvpn\":{\"user\":\"secret\",\"password\":\"secret\"}}", "unrecognized-json")]
    [InlineData("user=secret password=secret\n", "unrecognized-outcome")]
    public void ParseOutcome_AcceptsPlainAndJsonGluetunResponses(string body, string expected)
        => Assert.Equal(expected, PiaRegionRotator.ParseOutcome(body));

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

    private static ProxyRegionRotationRequest Request(
        RecordingClaims claims,
        IReadOnlyList<string> regions,
        bool reconnectInPlace = false,
        int offset = 0)
        => new(Target, Original, claims, regions, offset, reconnectInPlace);

    private static PiaRegionRotator CreateRotator(
        HttpClient client,
        IProxyContainerRecycler recycler,
        IProxyEgressProbe probe,
        int maxAttempts = 2,
        TimeSpan? attemptTimeout = null,
        TimeSpan? rollbackProbeTimeout = null,
        TimeSpan? recoveryTimeout = null,
        TimeSpan? restartTimeout = null)
    {
        var options = Options();
        options.ProxyRegionRotationMaxAttempts = maxAttempts;
        return new PiaRegionRotator(client, recycler, probe, options,
            NullLogger<PiaRegionRotator>.Instance,
            recoveryTimeout: recoveryTimeout,
            restartTimeout: restartTimeout,
            rollbackProbeTimeout: rollbackProbeTimeout,
            pollInterval: TimeSpan.FromMilliseconds(20),
            attemptTimeout: attemptTimeout ?? TimeSpan.FromMilliseconds(500));
    }

    private static ScraperOptions Options()
        => new() { ProxyRegionRotationProbeTimeoutSeconds = 2 };

    private sealed class RecordingClaims : IProxyEgressClaims
    {
        public HashSet<IPAddress> Peers { get; } = [];
        public HashSet<IPAddress> RateLimited { get; } = [];
        public IPAddress? LastClaimed { get; private set; }

        public string? TryClaim(IPAddress address, bool allowRateLimited)
        {
            if (Peers.Contains(address))
                return "peer-duplicate";
            if (!allowRateLimited && RateLimited.Contains(address))
                return "rate-limited";
            LastClaimed = address;
            return null;
        }
    }

    private sealed class RecordingControlServer : HttpMessageHandler
    {
        public string CurrentRegion { get; set; } = "US Las Vegas";
        public string Provider { get; set; } = "private internet access";
        public bool CrashOnRollback { get; set; }
        public bool FailSettingsRead { get; set; }
        public List<string> PutRegions { get; } = [];
        public int Reconnects { get; private set; }
        public int CachedPublicIpReads { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/vpn/settings")
            {
                if (request.Method == HttpMethod.Get)
                {
                    if (FailSettingsRead)
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
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

            if (request.RequestUri.AbsolutePath == "/v1/vpn/status"
                && request.Method == HttpMethod.Put)
            {
                using var payload = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                var status = payload.RootElement.GetProperty("status").GetString()!;
                if (status == "running")
                    Reconnects++;
                return Json(new { outcome = status });
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
        public bool BlockAfterCandidateUpdate { get; set; }
        public int RestorationFailuresRemaining { get; set; }
        public HashSet<string> DeadRegions { get; } = [];
        private readonly TaskCompletionSource _candidateProbeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CandidateProbeStarted => _candidateProbeStarted.Task;

        public RecordingEgressProbe(RecordingControlServer server)
            => _server = server;

        public async Task<IPAddress?> GetAddressAsync(Uri proxyUri, CancellationToken ct)
        {
            if (DeadRegions.Contains(_server.CurrentRegion))
                return null;
            if (_server.CurrentRegion == "US Las Vegas"
                && _server.PutRegions.Count > 0
                && RestorationFailuresRemaining > 0)
            {
                RestorationFailuresRemaining--;
                return null;
            }
            if (_server.CurrentRegion != "US Las Vegas"
                && BlockAfterCandidateUpdate)
            {
                _candidateProbeStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            if (_server.CurrentRegion == "US Las Vegas")
            {
                return _server.Reconnects == 0 || _server.PutRegions.Count > 0
                    ? Original
                    : IPAddress.Parse($"192.0.2.{20 + _server.Reconnects}");
            }

            if (DuplicateNewEgress)
                return Peer;
            return IPAddress.Parse(
                $"192.0.2.{10 + _server.PutRegions.Count - 1 + _server.Reconnects}");
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
