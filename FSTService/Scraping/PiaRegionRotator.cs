using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

internal readonly record struct ProxyRegionTunnel(
    string ContainerName,
    Uri ProxyUri,
    Uri ControlUri);

internal enum ProxyRegionRotationOutcome
{
    Rotated,
    Restored,
    Unsafe,
}

internal interface IProxyRegionRotator
{
    Task<ProxyRegionRotationOutcome> RotateAsync(
        ProxyRegionTunnel tunnel,
        IReadOnlyList<ProxyRegionTunnel> peers,
        IReadOnlyList<string> regions,
        int candidateOffset,
        CancellationToken ct);
}

internal interface IProxyEgressProbe
{
    Task<IPAddress?> GetAddressAsync(Uri proxyUri, CancellationToken ct);
}

internal sealed class CurlProxyEgressProbe : IProxyEgressProbe
{
    private readonly ScraperOptions _options;
    private readonly ILogger<CurlProxyEgressProbe> _log;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>> _send;

    public CurlProxyEgressProbe(
        IOptions<ScraperOptions> options,
        ILogger<CurlProxyEgressProbe> log)
        : this(options.Value, log)
    {
    }

    internal CurlProxyEgressProbe(
        ScraperOptions options,
        ILogger<CurlProxyEgressProbe> log,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? send = null)
    {
        _options = options;
        _log = log;
        _send = send ?? SendWithCurlAsync;
    }

    public async Task<IPAddress?> GetAddressAsync(Uri proxyUri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.ipify.org");
        request.Options.Set(ProxyRequestState.EndpointProxyUri, proxyUri);
        using var response = await _send(request, ct);
        if (response?.StatusCode != HttpStatusCode.OK)
            return null;

        var body = await response.Content.ReadAsStringAsync(ct);
        return IPAddress.TryParse(body.Trim(), out var address) ? address : null;
    }

    private Task<HttpResponseMessage?> SendWithCurlAsync(
        HttpRequestMessage request, CancellationToken ct)
        => ResilientHttpExecutor.CurlHttpFallback.SendAsync(
            request,
            "vpn-region-egress-canary",
            TimeSpan.FromSeconds(8),
            _log,
            ct,
            tempDirectory: _options.ProxyCurlTempDirectory,
            maximumResponseBytes: 64);
}

internal sealed class PiaRegionRotator : IProxyRegionRotator
{
    private readonly HttpClient _control;
    private readonly IProxyContainerRecycler _recycler;
    private readonly IProxyEgressProbe _egress;
    private readonly ILogger<PiaRegionRotator> _log;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _rollbackProbeTimeout;
    private readonly TimeSpan _recoveryTimeout;
    private readonly TimeSpan _restartTimeout;
    private readonly TimeSpan _pollInterval;

    public PiaRegionRotator(
        IHttpClientFactory clientFactory,
        IProxyContainerRecycler recycler,
        IProxyEgressProbe egress,
        IOptions<ScraperOptions> options,
        ILogger<PiaRegionRotator> log)
        : this(clientFactory.CreateClient(nameof(PiaRegionRotator)),
            recycler, egress, options.Value, log)
    {
    }

    internal PiaRegionRotator(
        HttpClient control,
        IProxyContainerRecycler recycler,
        IProxyEgressProbe egress,
        ScraperOptions options,
        ILogger<PiaRegionRotator> log,
        TimeSpan? recoveryTimeout = null,
        TimeSpan? restartTimeout = null,
        TimeSpan? rollbackProbeTimeout = null,
        TimeSpan? pollInterval = null)
    {
        _control = control;
        _recycler = recycler;
        _egress = egress;
        _log = log;
        _probeTimeout = TimeSpan.FromSeconds(options.ProxyRegionRotationProbeTimeoutSeconds);
        _rollbackProbeTimeout = rollbackProbeTimeout ?? TimeSpan.FromMinutes(4);
        _recoveryTimeout = recoveryTimeout ?? TimeSpan.FromMinutes(8);
        _restartTimeout = restartTimeout ?? TimeSpan.FromMinutes(5);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
    }

    public async Task<ProxyRegionRotationOutcome> RotateAsync(
        ProxyRegionTunnel tunnel,
        IReadOnlyList<ProxyRegionTunnel> peers,
        IReadOnlyList<string> regions,
        int candidateOffset,
        CancellationToken ct)
    {
        string? originalRegion;
        HashSet<IPAddress> peerAddresses;
        IPAddress? originalAddress;
        try
        {
            originalRegion = await GetRegionAsync(tunnel.ControlUri, ct);
            if (originalRegion is null)
            {
                _log.LogWarning(
                    "PIA region rotation rejected for {Container}: provider, VPN type, or selector is not the qualified single-region PIA contract.",
                    tunnel.ContainerName);
                return ProxyRegionRotationOutcome.Restored;
            }

            peerAddresses = await GetPeerAddressesAsync(peers, ct)
                ?? throw new InvalidOperationException("A peer egress could not be verified.");
            originalAddress = await GetEgressAsync(tunnel.ProxyUri, ct);
            if (originalAddress is not null && peerAddresses.Contains(originalAddress))
            {
                _log.LogError(
                    "PIA proxy {Container} already duplicates a peer egress; quarantining the exit.",
                    tunnel.ContainerName);
                return ProxyRegionRotationOutcome.Unsafe;
            }
            if (originalAddress is null)
                throw new InvalidOperationException("The current egress could not be verified.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                "PIA region rotation for {Container} deferred before changing its tunnel: {Reason}",
                tunnel.ContainerName, ex.GetType().Name);
            return ProxyRegionRotationOutcome.Restored;
        }

        var candidate = Enumerable.Range(0, regions.Count)
            .Select(offset => regions[(candidateOffset + offset) % regions.Count])
            .FirstOrDefault(region =>
                !region.Equals(originalRegion, StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
            return ProxyRegionRotationOutcome.Restored;

        try
        {
            var outcome = await PutRegionAsync(tunnel.ControlUri, candidate, ct);
            if (!outcome.Equals("running", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning(
                    "PIA region update for {Container} to {Region} reported {Outcome}; verifying rollback.",
                    tunnel.ContainerName, candidate, outcome);
            }
            else
            {
                using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeDeadline.CancelAfter(_probeTimeout);
                var verified = await WaitForVerifiedEgressAsync(
                    tunnel, candidate, originalAddress, peerAddresses,
                    probeDeadline.Token);
                if (verified
                    && await DistinctFromLivePeersAsync(tunnel, peers, probeDeadline.Token))
                {
                    _log.LogInformation(
                        "PIA proxy {Container} changed tunnel region {OldRegion} -> {NewRegion}; Docker health and distinct real proxy egress verified.",
                        tunnel.ContainerName, originalRegion, candidate);
                    return ProxyRegionRotationOutcome.Rotated;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                "PIA region update for {Container} to {Region} failed: {Reason}; restoring the previous tunnel.",
                tunnel.ContainerName, candidate, ex.GetType().Name);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "PIA region update for {Container} to {Region} was canceled or exceeded the egress deadline; restoring before release.",
                tunnel.ContainerName, candidate);
        }

        using var recoveryDeadline = new CancellationTokenSource(_recoveryTimeout);
        bool restored;
        try
        {
            restored = await RestoreAsync(
                tunnel, originalRegion, peers, peerAddresses,
                recoveryDeadline.Token);
        }
        catch (OperationCanceledException)
        {
            _log.LogError(
                "PIA proxy {Container} restoration exceeded its bounded deadline.",
                tunnel.ContainerName);
            restored = false;
        }
        if (!restored)
        {
            _log.LogError(
                "PIA proxy {Container} could not be restored after trying {Region}; quarantine this exit until operator recovery.",
                tunnel.ContainerName, candidate);
            return ProxyRegionRotationOutcome.Unsafe;
        }

        return ProxyRegionRotationOutcome.Restored;
    }

    private async Task<HashSet<IPAddress>?> GetPeerAddressesAsync(
        IReadOnlyList<ProxyRegionTunnel> peers, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(4, 4);
        var probes = peers.Select(async peer =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await GetEgressAsync(peer.ProxyUri, ct);
            }
            finally
            {
                gate.Release();
            }
        });
        var results = await Task.WhenAll(probes);
        if (results.Any(address => address is null))
            return null;

        var addresses = results.Select(address => address!).ToHashSet();
        return addresses.Count == peers.Count ? addresses : null;
    }

    private async Task<bool> DistinctFromLivePeersAsync(
        ProxyRegionTunnel tunnel, IReadOnlyList<ProxyRegionTunnel> peers,
        CancellationToken ct)
    {
        var peersNow = await GetPeerAddressesAsync(peers, ct);
        var addressNow = await GetEgressAsync(tunnel.ProxyUri, ct);
        return peersNow is not null
            && addressNow is not null
            && !peersNow.Contains(addressNow);
    }

    private async Task<IPAddress?> GetEgressAsync(Uri proxyUri, CancellationToken ct)
    {
        try
        {
            return await _egress.GetAddressAsync(proxyUri, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                "PIA proxy egress verification failed for {Proxy}: {Reason}",
                proxyUri.Host, ex.GetType().Name);
            return null;
        }
    }

    private async Task<string?> GetRegionAsync(Uri controlUri, CancellationToken ct)
    {
        using var response = await _control.GetAsync(
            new Uri(controlUri, "/v1/vpn/settings"), ct);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var root = document.RootElement;
        if (!root.GetProperty("type").GetString()!.Equals("openvpn", StringComparison.OrdinalIgnoreCase))
            return null;

        var provider = root.GetProperty("provider");
        if (!provider.GetProperty("name").GetString()!.Equals(
            "private internet access", StringComparison.OrdinalIgnoreCase))
            return null;

        var selection = provider.GetProperty("server_selection");
        foreach (var key in new[] { "cities", "names", "countries", "hostnames" })
        {
            var filter = selection.GetProperty(key);
            if (filter.ValueKind != JsonValueKind.Null
                && (filter.ValueKind != JsonValueKind.Array
                    || filter.GetArrayLength() != 0))
                return null;
        }

        var regions = selection.GetProperty("regions");
        if (regions.ValueKind != JsonValueKind.Array
            || regions.GetArrayLength() != 1)
            return null;

        var region = regions[0].GetString();
        return string.IsNullOrWhiteSpace(region) ? null : region;
    }

    private async Task<string> PutRegionAsync(Uri controlUri, string region, CancellationToken ct)
    {
        using var payload = JsonContent.Create(new
        {
            provider = new
            {
                server_selection = new { regions = new[] { region } },
            },
        });
        using var response = await _control.PutAsync(
            new Uri(controlUri, "/v1/vpn/settings"), payload, ct);
        response.EnsureSuccessStatusCode();
        var outcome = await response.Content.ReadAsStringAsync(ct);
        return outcome.Trim();
    }

    private async Task<bool> WaitForVerifiedEgressAsync(
        ProxyRegionTunnel tunnel,
        string? region,
        IPAddress? previousAddress,
        HashSet<IPAddress> peerAddresses,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var selectedRegion = await GetRegionAsync(tunnel.ControlUri, ct);
                if (selectedRegion is not null
                    && (region is null ||
                        selectedRegion.Equals(region, StringComparison.OrdinalIgnoreCase))
                    && await _recycler.IsHealthyAsync(tunnel.ContainerName, ct))
                {
                    var address = await GetEgressAsync(tunnel.ProxyUri, ct);
                    if (address is not null
                        && !address.Equals(previousAddress)
                        && !peerAddresses.Contains(address))
                        return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(
                    "PIA tunnel verification pending for {Container}: {Reason}",
                    tunnel.ContainerName, ex.GetType().Name);
            }

            await Task.Delay(_pollInterval, ct);
        }

        return false;
    }

    private async Task<bool> RestoreAsync(
        ProxyRegionTunnel tunnel,
        string originalRegion,
        IReadOnlyList<ProxyRegionTunnel> peers,
        HashSet<IPAddress> peerAddresses,
        CancellationToken ct)
    {
        try
        {
            var outcome = await PutRegionAsync(tunnel.ControlUri, originalRegion, ct);
            if (outcome.Equals("running", StringComparison.OrdinalIgnoreCase))
            {
                using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeDeadline.CancelAfter(_rollbackProbeTimeout);
                try
                {
                    if (await WaitForVerifiedEgressAsync(
                        tunnel, originalRegion, null, peerAddresses, probeDeadline.Token)
                        && await DistinctFromLivePeersAsync(
                            tunnel, peers, probeDeadline.Token))
                        return true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _log.LogWarning(
                        "PIA proxy {Container} did not recover through the control API; restarting its container.",
                        tunnel.ContainerName);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                "PIA proxy {Container} control rollback failed: {Reason}; restarting its container.",
                tunnel.ContainerName, ex.GetType().Name);
        }

        var staticRegion = await _recycler.GetConfiguredRegionAsync(
            tunnel.ContainerName, ct);
        if (staticRegion is null)
        {
            _log.LogError(
                "PIA proxy {Container} has no verifiable static Compose region for safe rollback.",
                tunnel.ContainerName);
            return false;
        }

        if (!await _recycler.RestartAsync(tunnel.ContainerName, ct))
            return false;

        using var restartDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        restartDeadline.CancelAfter(_restartTimeout);
        try
        {
            return await WaitForVerifiedEgressAsync(
                tunnel, staticRegion, null, peerAddresses, restartDeadline.Token)
                && await DistinctFromLivePeersAsync(
                    tunnel, peers, restartDeadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
