using System.Net;
using System.Text;
using System.Text.Json;
using FSTService.Scraping;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FSTService.Tests.Unit;

public sealed class PiaRegionRotatorEdgeCaseTests
{
    private const string Home = "US Las Vegas";
    private static readonly ProxyRegionTunnel Target =
        new("gluetun-7", new Uri("http://gluetun-7:8888"),
            new Uri("http://gluetun-7:8000"));
    private static readonly IPAddress Original = IPAddress.Parse("198.51.100.1");
    private static readonly IPAddress Peer = IPAddress.Parse("198.51.100.2");

    [Fact]
    public async Task GetEgressAsync_ProbeFailure_ReturnsNull()
    {
        var control = new ScriptedControl();
        var probe = new ScriptedProbe(control)
        {
            Next = (_, _, _) => throw new HttpRequestException("proxy refused"),
        };
        using var client = new HttpClient(control);

        Assert.Null(await CreateRotator(client, new ScriptedRecycler(control), probe)
            .GetEgressAsync(Target.ProxyUri, CancellationToken.None));
    }

    [Fact]
    public async Task RotateAsync_NoCandidates_DefersWithoutMutation()
    {
        var control = new ScriptedControl();
        using var client = new HttpClient(control);

        var result = await CreateRotator(client, new ScriptedRecycler(control),
                new ScriptedProbe(control))
            .RotateAsync(Request([]), CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Deferred, result.Outcome);
        Assert.Empty(control.PutRegions);
        Assert.Equal(0, control.StatusPuts);
    }

    [Theory]
    [InlineData("wireguard", false, 1)]
    [InlineData("openvpn", true, 1)]
    [InlineData("openvpn", false, 2)]
    public async Task RotateAsync_UnqualifiedSelector_DefersWithoutMutation(
        string vpnType, bool cityFilter, int regionCount)
    {
        var control = new ScriptedControl
        {
            VpnType = vpnType,
            Cities = cityFilter ? ["Las Vegas"] : null,
            ExtraRegions = regionCount - 1,
        };
        using var client = new HttpClient(control);

        var result = await CreateRotator(client, new ScriptedRecycler(control),
                new ScriptedProbe(control))
            .RotateAsync(Request(["US Seattle"]), CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Deferred, result.Outcome);
        Assert.Empty(control.PutRegions);
    }

    [Fact]
    public async Task RotateAsync_NonRunningOutcome_TriesNextCandidate()
    {
        var control = new ScriptedControl();
        control.PutOutcomes["US Seattle"] = "{\"outcome\":\"stopped\"}";
        using var client = new HttpClient(control);

        var result = await CreateRotator(client, new ScriptedRecycler(control),
                new ScriptedProbe(control))
            .RotateAsync(Request(["US Seattle", "US Denver"]), CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Rotated, result.Outcome);
        Assert.Equal("US Denver", result.Region);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(["US Seattle", "US Denver"], control.PutRegions);
    }

    [Fact]
    public async Task RotateAsync_FailedUpdateWithUnreadableSettings_TriesNextCandidate()
    {
        var control = new ScriptedControl { FailedGetNumbers = { 2 } };
        control.PutStatus["US Seattle"] = HttpStatusCode.InternalServerError;
        using var client = new HttpClient(control);

        var result = await CreateRotator(client, new ScriptedRecycler(control),
                new ScriptedProbe(control))
            .RotateAsync(Request(["US Seattle", "US Denver"]), CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Rotated, result.Outcome);
        Assert.Equal("US Denver", result.Region);
        Assert.Equal(3, control.Gets);
    }

    [Fact]
    public async Task RotateAsync_TransientHealthFailure_KeepsVerifyingCandidate()
    {
        var control = new ScriptedControl();
        var recycler = new ScriptedRecycler(control) { HealthFailuresRemaining = 1 };
        using var client = new HttpClient(control);

        var result = await CreateRotator(client, recycler, new ScriptedProbe(control))
            .RotateAsync(Request(["US Seattle"]), CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Rotated, result.Outcome);
        Assert.Equal(0, recycler.HealthFailuresRemaining);
    }

    [Fact]
    public async Task RotateAsync_OverallDeadline_KeepsWorkingCandidateTunnel()
    {
        var control = new ScriptedControl();
        var hung = 0;
        var probe = new ScriptedProbe(control)
        {
            Next = async (region, _, ct) =>
            {
                if (region == Home)
                    return Original;
                if (Interlocked.Exchange(ref hung, 1) == 0)
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return IPAddress.Parse("198.51.100.50");
            },
        };
        using var client = new HttpClient(control);

        var result = await CreateRotator(client, new ScriptedRecycler(control), probe,
                maxAttempts: 1, attemptTimeout: TimeSpan.FromSeconds(10))
            .RotateAsync(Request(["US Seattle"]), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(ProxyRegionRotationOutcome.Restored, result.Outcome);
        Assert.Equal("US Seattle", result.Region);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task RotateAsync_ControlRollbackWithoutEgress_RestartsStaticRegion()
    {
        var control = new ScriptedControl();
        var recycler = new ScriptedRecycler(control);
        using var client = new HttpClient(control);

        var result = await CreateRotator(client, recycler,
                DuplicateUntilRestart(control, recycler), maxAttempts: 1)
            .RotateAsync(Request(["US Seattle"], Peer), CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Restored, result.Outcome);
        Assert.Equal(Home, result.Region);
        Assert.Equal(Original, result.Egress);
        Assert.Equal(1, recycler.Restarts);
        Assert.Equal(["US Seattle", Home], control.PutRegions);
    }

    [Fact]
    public async Task RotateAsync_MissingStaticRegion_IsUnsafeWithoutRestart()
    {
        var control = new ScriptedControl();
        var recycler = new ScriptedRecycler(control) { StaticRegion = null };
        using var client = new HttpClient(control);

        var result = await CreateRotator(client, recycler,
                DuplicateUntilRestart(control, recycler), maxAttempts: 1)
            .RotateAsync(Request(["US Seattle"], Peer), CancellationToken.None);

        Assert.Equal(ProxyRegionRotationOutcome.Unsafe, result.Outcome);
        Assert.Equal(0, recycler.Restarts);
    }

    [Fact]
    public async Task RotateAsync_RestartWithoutEgress_IsUnsafeAfterRestartDeadline()
    {
        var control = new ScriptedControl();
        var recycler = new ScriptedRecycler(control);
        var probe = new ScriptedProbe(control)
        {
            Next = (region, _, _) => Task.FromResult(region == Home ? null : Peer),
        };
        using var client = new HttpClient(control);

        var result = await CreateRotator(client, recycler, probe, maxAttempts: 1)
            .RotateAsync(Request(["US Seattle"], Peer), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(ProxyRegionRotationOutcome.Unsafe, result.Outcome);
        Assert.Equal(1, recycler.Restarts);
    }

    [Fact]
    public void ParseOutcome_MalformedJson_IsCategorized()
        => Assert.Equal("unrecognized-json", PiaRegionRotator.ParseOutcome("{\"outcome\":"));

    [Fact]
    public async Task CurlEgressProbe_NonOkResponse_ReturnsNull()
    {
        var probe = new CurlProxyEgressProbe(
            new ScraperOptions(), NullLogger<CurlProxyEgressProbe>.Instance,
            (_, _) => Task.FromResult<HttpResponseMessage?>(
                new(HttpStatusCode.ServiceUnavailable)));

        Assert.Null(await probe.GetAddressAsync(Target.ProxyUri, CancellationToken.None));
    }

    [Fact]
    public async Task CurlEgressProbe_UnreachableProxy_NeverReturnsAddress()
    {
        var probe = new CurlProxyEgressProbe(
            Options.Create(new ScraperOptions()),
            NullLogger<CurlProxyEgressProbe>.Instance);

        try
        {
            Assert.Null(await probe.GetAddressAsync(
                new Uri("http://127.0.0.1:9"), CancellationToken.None));
        }
        catch (HttpRequestException)
        {
        }
    }

    private static ScriptedProbe DuplicateUntilRestart(
        ScriptedControl control, ScriptedRecycler recycler)
        => new(control)
        {
            Next = (region, _, _) => Task.FromResult<IPAddress?>(
                region != Home ? Peer : recycler.Restarts > 0 ? Original : null),
        };

    private static ProxyRegionRotationRequest Request(
        IReadOnlyList<string> regions, params IPAddress[] peers)
        => new(Target, Original, new Claims(peers), regions, 0, false);

    private static PiaRegionRotator CreateRotator(
        HttpClient client,
        IProxyContainerRecycler recycler,
        IProxyEgressProbe probe,
        int maxAttempts = 2,
        TimeSpan? attemptTimeout = null)
        => new(client, recycler, probe,
            new ScraperOptions
            {
                ProxyRegionRotationProbeTimeoutSeconds = 1,
                ProxyRegionRotationMaxAttempts = maxAttempts,
            },
            NullLogger<PiaRegionRotator>.Instance,
            recoveryTimeout: TimeSpan.FromSeconds(5),
            restartTimeout: TimeSpan.FromMilliseconds(300),
            rollbackProbeTimeout: TimeSpan.FromMilliseconds(300),
            pollInterval: TimeSpan.FromMilliseconds(20),
            attemptTimeout: attemptTimeout ?? TimeSpan.FromMilliseconds(400));

    private sealed class Claims(IEnumerable<IPAddress> peers) : IProxyEgressClaims
    {
        private readonly HashSet<IPAddress> _peers = [.. peers];

        public string? TryClaim(IPAddress address, bool allowRateLimited)
            => _peers.Contains(address) ? "peer-duplicate" : null;
    }

    private sealed class ScriptedControl : HttpMessageHandler
    {
        public string CurrentRegion { get; set; } = Home;
        public string VpnType { get; init; } = "openvpn";
        public string[]? Cities { get; init; }
        public int ExtraRegions { get; init; }
        public Dictionary<string, string> PutOutcomes { get; } = [];
        public Dictionary<string, HttpStatusCode> PutStatus { get; } = [];
        public HashSet<int> FailedGetNumbers { get; } = [];
        public List<string> PutRegions { get; } = [];
        public int Gets { get; private set; }
        public int StatusPuts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/vpn/settings" && request.Method == HttpMethod.Get)
            {
                Gets++;
                if (FailedGetNumbers.Contains(Gets))
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                return Json(new
                {
                    type = VpnType,
                    provider = new
                    {
                        name = "private internet access",
                        server_selection = new
                        {
                            regions = Enumerable.Repeat(CurrentRegion, 1 + ExtraRegions).ToArray(),
                            cities = Cities,
                            names = (string[]?)null,
                            countries = Array.Empty<string>(),
                            hostnames = (string[]?)null,
                        },
                    },
                });
            }

            if (path == "/v1/vpn/settings")
            {
                using var payload = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                var region = payload.RootElement.GetProperty("provider")
                    .GetProperty("server_selection").GetProperty("regions")[0].GetString()!;
                PutRegions.Add(region);
                if (PutStatus.TryGetValue(region, out var status))
                    return new HttpResponseMessage(status);
                var outcome = PutOutcomes.GetValueOrDefault(region, "running");
                if (outcome == "running")
                    CurrentRegion = region;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(outcome),
                };
            }

            if (path == "/v1/vpn/status")
            {
                StatusPuts++;
                using var payload = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json(new { outcome = payload.RootElement.GetProperty("status").GetString() });
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

    private sealed class ScriptedProbe(ScriptedControl control) : IProxyEgressProbe
    {
        private int _calls;

        public Func<string, int, CancellationToken, Task<IPAddress?>>? Next { get; init; }

        public Task<IPAddress?> GetAddressAsync(Uri proxyUri, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            if (Next is not null)
                return Next(control.CurrentRegion, call, ct);
            return Task.FromResult<IPAddress?>(control.CurrentRegion == Home
                ? Original
                : IPAddress.Parse($"198.51.100.{10 + control.PutRegions.Count}"));
        }
    }

    private sealed class ScriptedRecycler(ScriptedControl control) : IProxyContainerRecycler
    {
        public string? StaticRegion { get; init; } = Home;
        public int HealthFailuresRemaining { get; set; }
        public int Restarts { get; private set; }

        public Task<bool> IsHealthyAsync(string containerName, CancellationToken ct)
        {
            if (HealthFailuresRemaining > 0)
            {
                HealthFailuresRemaining--;
                throw new InvalidOperationException("docker inspect unavailable");
            }

            return Task.FromResult(true);
        }

        public Task<string?> GetConfiguredRegionAsync(string containerName, CancellationToken ct)
            => Task.FromResult(StaticRegion);

        public Task<bool> RestartAsync(string containerName, CancellationToken ct = default)
        {
            Restarts++;
            control.CurrentRegion = StaticRegion ?? Home;
            return Task.FromResult(true);
        }
    }
}
