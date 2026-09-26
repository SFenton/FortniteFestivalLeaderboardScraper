using System.Diagnostics;
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
    /// <summary>A replacement tunnel with a verified, distinct, unburned egress is active.</summary>
    Rotated,
    /// <summary>The tunnel was changed, then returned to a verified working state.</summary>
    Restored,
    /// <summary>Nothing was changed.</summary>
    Deferred,
    /// <summary>No safe egress could be verified; the exit must be quarantined.</summary>
    Unsafe,
}

internal readonly record struct ProxyRegionRotationResult(
    ProxyRegionRotationOutcome Outcome,
    IPAddress? Egress = null,
    string? Region = null,
    int Attempts = 0);

/// <summary>
/// Pool-owned egress registry. <see cref="TryClaim"/> atomically rejects an
/// address already used by another exit (or recently rate-limited) and
/// otherwise records it as this exit's pending egress.
/// </summary>
internal interface IProxyEgressClaims
{
    /// <returns><c>null</c> when accepted; otherwise a short rejection reason.</returns>
    string? TryClaim(IPAddress address, bool allowRateLimited);
}

internal sealed record ProxyRegionRotationRequest(
    ProxyRegionTunnel Tunnel,
    IPAddress? PreviousEgress,
    IProxyEgressClaims Claims,
    IReadOnlyList<string> Regions,
    int CandidateOffset,
    bool ReconnectInPlace);

internal interface IProxyRegionRotator
{
    Task<ProxyRegionRotationResult> RotateAsync(
        ProxyRegionRotationRequest request,
        CancellationToken ct);

    Task<IPAddress?> GetEgressAsync(Uri proxyUri, CancellationToken ct);
}

internal interface IProxyEgressProbe
{
    Task<IPAddress?> GetAddressAsync(Uri proxyUri, CancellationToken ct);
}

internal sealed class CurlProxyEgressProbe : IProxyEgressProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
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
            ProbeTimeout,
            _log,
            ct,
            tempDirectory: _options.ProxyCurlTempDirectory,
            primaryTransport: true,
            maximumResponseBytes: 64);
}

/// <summary>
/// Refreshes one PIA Gluetun exit's tunnel through the internal control API:
/// an in-place reconnect (Gluetun selects another random server in the same
/// region) or a region-selector change. Every candidate is accepted only after
/// its real proxy egress differs from the previous egress and from every other
/// exit's known egress, is outside the rate-limited window, and the control
/// API and Docker health agree. Failed candidates fall back to the next one;
/// exhausted candidates restore the original region or the static Compose
/// region, and an unverifiable exit is reported unsafe.
/// </summary>
internal sealed class PiaRegionRotator : IProxyRegionRotator
{
    private readonly HttpClient _control;
    private readonly IProxyContainerRecycler _recycler;
    private readonly IProxyEgressProbe _egress;
    private readonly ILogger<PiaRegionRotator> _log;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _attemptTimeout;
    private readonly int _maxAttempts;
    private readonly TimeSpan _rollbackProbeTimeout;
    private readonly TimeSpan _recoveryTimeout;
    private readonly TimeSpan _restartTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _rejectSettle;

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
        TimeSpan? pollInterval = null,
        TimeSpan? attemptTimeout = null)
    {
        _control = control;
        _recycler = recycler;
        _egress = egress;
        _log = log;
        _probeTimeout = TimeSpan.FromSeconds(Math.Max(1, options.ProxyRegionRotationProbeTimeoutSeconds));
        _attemptTimeout = attemptTimeout
            ?? TimeSpan.FromSeconds(Math.Max(1, options.ProxyRegionRotationAttemptTimeoutSeconds));
        _maxAttempts = Math.Max(1, options.ProxyRegionRotationMaxAttempts);
        _rollbackProbeTimeout = rollbackProbeTimeout ?? TimeSpan.FromMinutes(2);
        _recoveryTimeout = recoveryTimeout ?? TimeSpan.FromMinutes(5);
        _restartTimeout = restartTimeout ?? TimeSpan.FromMinutes(3);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _rejectSettle = _pollInterval * 2;
    }

    public async Task<IPAddress?> GetEgressAsync(Uri proxyUri, CancellationToken ct)
    {
        try
        {
            return await _egress.GetAddressAsync(proxyUri, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(
                "PIA proxy egress probe failed for {Proxy}: {Reason}",
                proxyUri.Host, ex.GetType().Name);
            return null;
        }
    }

    public async Task<ProxyRegionRotationResult> RotateAsync(
        ProxyRegionRotationRequest request,
        CancellationToken ct)
    {
        var tunnel = request.Tunnel;
        string? originalRegion;
        try
        {
            originalRegion = await GetRegionAsync(tunnel.ControlUri, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                "PIA region rotation for {Container} deferred before changing its tunnel: {Reason}",
                tunnel.ContainerName, ex.GetType().Name);
            return new(ProxyRegionRotationOutcome.Deferred);
        }

        if (originalRegion is null)
        {
            _log.LogWarning(
                "PIA region rotation rejected for {Container}: provider, VPN type, or selector is not the qualified single-region PIA contract.",
                tunnel.ContainerName);
            return new(ProxyRegionRotationOutcome.Deferred);
        }

        var candidates = BuildCandidates(originalRegion, request);
        if (candidates.Count == 0)
            return new(ProxyRegionRotationOutcome.Deferred);

        var started = Stopwatch.GetTimestamp();
        var runtimeRegion = originalRegion;
        var attempts = 0;
        var mutated = false;
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(_probeTimeout);
        try
        {
            foreach (var candidate in candidates)
            {
                attempts++;
                var reconnect = candidate.Equals(runtimeRegion, StringComparison.OrdinalIgnoreCase);
                mutated = true;
                var outcome = reconnect
                    ? await ReconnectAsync(tunnel.ControlUri, overall.Token)
                    : await PutRegionAsync(tunnel.ControlUri, candidate, overall.Token);
                if (!outcome.Equals("running", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogWarning(
                        "PIA {Action} for {Container} to {Region} reported {Outcome}; trying the next candidate.",
                        reconnect ? "reconnect" : "region update",
                        tunnel.ContainerName, candidate, outcome);
                    continue;
                }

                runtimeRegion = candidate;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                attempt.CancelAfter(_attemptTimeout);
                IPAddress? verified;
                try
                {
                    verified = await WaitForAcceptableEgressAsync(
                        tunnel, candidate, request.PreviousEgress, request.Claims,
                        allowPreviousAndRateLimited: false, attempt.Token);
                }
                catch (OperationCanceledException) when (!overall.IsCancellationRequested)
                {
                    verified = null;
                }

                if (verified is not null)
                {
                    _log.LogInformation(
                        "PIA proxy {Container} {Action} {OldRegion} -> {NewRegion} in {ElapsedMs}ms (attempt {Attempt}); Docker health and distinct real proxy egress verified.",
                        tunnel.ContainerName,
                        reconnect ? "reconnected" : "changed tunnel region",
                        originalRegion, candidate,
                        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        attempts);
                    return new(ProxyRegionRotationOutcome.Rotated, verified, candidate, attempts);
                }

                _log.LogDebug(
                    "PIA proxy {Container} candidate {Region} produced no acceptable egress within {TimeoutSeconds}s.",
                    tunnel.ContainerName, candidate, _attemptTimeout.TotalSeconds);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogWarning(
                "PIA region rotation for {Container} was canceled after {Attempts} attempt(s); restoring before release.",
                tunnel.ContainerName, attempts);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "PIA region rotation for {Container} exceeded its {TimeoutSeconds}s egress deadline after {Attempts} attempt(s); restoring.",
                tunnel.ContainerName, _probeTimeout.TotalSeconds, attempts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                "PIA region rotation for {Container} failed: {Reason}; restoring the previous tunnel.",
                tunnel.ContainerName, ex.GetType().Name);
        }

        if (!mutated)
            return new(ProxyRegionRotationOutcome.Deferred, Attempts: attempts);

        _log.LogWarning(
            "PIA proxy {Container} found no acceptable replacement egress after {Attempts} attempt(s); restoring {Region}.",
            tunnel.ContainerName, attempts, originalRegion);
        using var recoveryDeadline = new CancellationTokenSource(_recoveryTimeout);
        try
        {
            var restored = await RestoreAsync(
                tunnel, originalRegion, runtimeRegion, request, recoveryDeadline.Token);
            if (restored is { } result)
                return result with { Attempts = attempts };
        }
        catch (OperationCanceledException)
        {
            _log.LogError(
                "PIA proxy {Container} restoration exceeded its bounded deadline.",
                tunnel.ContainerName);
        }

        _log.LogError(
            "PIA proxy {Container} could not be restored after {Attempts} attempt(s); quarantine this exit until operator recovery.",
            tunnel.ContainerName, attempts);
        return new(ProxyRegionRotationOutcome.Unsafe, Attempts: attempts);
    }

    internal IReadOnlyList<string> BuildCandidates(
        string originalRegion, ProxyRegionRotationRequest request)
    {
        var cycle = new List<string>();
        if (request.ReconnectInPlace)
            cycle.Add(originalRegion);

        var regions = request.Regions;
        for (var offset = 0; offset < regions.Count; offset++)
        {
            var region = regions[(request.CandidateOffset + offset) % regions.Count];
            if (!region.Equals(originalRegion, StringComparison.OrdinalIgnoreCase)
                && !cycle.Contains(region, StringComparer.OrdinalIgnoreCase))
                cycle.Add(region);
        }

        if (cycle.Count == 0)
            return [];

        var candidates = new List<string>(_maxAttempts);
        for (var i = 0; i < _maxAttempts; i++)
            candidates.Add(cycle[i % cycle.Count]);
        return candidates;
    }

    private async Task<ProxyRegionRotationResult?> RestoreAsync(
        ProxyRegionTunnel tunnel,
        string originalRegion,
        string runtimeRegion,
        ProxyRegionRotationRequest request,
        CancellationToken ct)
    {
        try
        {
            var reconnect = runtimeRegion.Equals(originalRegion, StringComparison.OrdinalIgnoreCase);
            var outcome = reconnect
                ? await ReconnectAsync(tunnel.ControlUri, ct)
                : await PutRegionAsync(tunnel.ControlUri, originalRegion, ct);
            if (outcome.Equals("running", StringComparison.OrdinalIgnoreCase))
            {
                using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeDeadline.CancelAfter(_rollbackProbeTimeout);
                try
                {
                    var address = await WaitForAcceptableEgressAsync(
                        tunnel, originalRegion, request.PreviousEgress, request.Claims,
                        allowPreviousAndRateLimited: true, probeDeadline.Token);
                    if (address is not null)
                        return new(ProxyRegionRotationOutcome.Restored, address, originalRegion);
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
            return null;
        }

        if (!await _recycler.RestartAsync(tunnel.ContainerName, ct))
            return null;

        using var restartDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        restartDeadline.CancelAfter(_restartTimeout);
        try
        {
            var address = await WaitForAcceptableEgressAsync(
                tunnel, staticRegion, request.PreviousEgress, request.Claims,
                allowPreviousAndRateLimited: true, restartDeadline.Token);
            return address is null
                ? null
                : new ProxyRegionRotationResult(
                    ProxyRegionRotationOutcome.Restored, address, staticRegion);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Polls until the tunnel reports <paramref name="region"/>, Docker reports
    /// healthy, and the real proxy egress is acceptable. A reachable but
    /// unacceptable egress (unchanged, duplicate, or still rate-limited) that
    /// persists past a short settle window returns <c>null</c> so the caller can
    /// move to another candidate instead of waiting for a timeout.
    /// </summary>
    private async Task<IPAddress?> WaitForAcceptableEgressAsync(
        ProxyRegionTunnel tunnel,
        string region,
        IPAddress? previousAddress,
        IProxyEgressClaims claims,
        bool allowPreviousAndRateLimited,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var address = await GetEgressAsync(tunnel.ProxyUri, ct);
                if (address is not null)
                {
                    var rejection = !allowPreviousAndRateLimited
                        && previousAddress is not null
                        && address.Equals(previousAddress)
                            ? "unchanged"
                            : claims.TryClaim(address, allowPreviousAndRateLimited);
                    if (rejection is null)
                    {
                        var selectedRegion = await GetRegionAsync(tunnel.ControlUri, ct);
                        if (selectedRegion is not null
                            && selectedRegion.Equals(region, StringComparison.OrdinalIgnoreCase)
                            && await _recycler.IsHealthyAsync(tunnel.ContainerName, ct))
                            return address;
                    }
                    else if (Stopwatch.GetElapsedTime(started) >= _rejectSettle)
                    {
                        _log.LogDebug(
                            "PIA proxy {Container} candidate {Region} egress rejected: {Reason}.",
                            tunnel.ContainerName, region, rejection);
                        return null;
                    }
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
        return ParseOutcome(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Stops and restarts the VPN loop without changing settings. Gluetun's
    /// random server selection then connects to another server in the region.
    /// </summary>
    private async Task<string> ReconnectAsync(Uri controlUri, CancellationToken ct)
    {
        await PutStatusAsync(controlUri, "stopped", ct);
        return await PutStatusAsync(controlUri, "running", ct);
    }

    private async Task<string> PutStatusAsync(Uri controlUri, string status, CancellationToken ct)
    {
        using var payload = JsonContent.Create(new { status });
        using var response = await _control.PutAsync(
            new Uri(controlUri, "/v1/vpn/status"), payload, ct);
        response.EnsureSuccessStatusCode();
        return ParseOutcome(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Extracts only Gluetun's short outcome scalar. Control responses can
    /// contain VPN credentials, so any other body is reduced to a category and
    /// never returned or logged verbatim.
    /// </summary>
    internal static string ParseOutcome(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("outcome", out var outcome)
                    && outcome.ValueKind == JsonValueKind.String)
                    return SafeScalar(outcome.GetString()!);
            }
            catch (JsonException)
            {
            }

            return "unrecognized-json";
        }

        return SafeScalar(trimmed);
    }

    private static string SafeScalar(string value)
    {
        value = value.Trim();
        return value.Length is > 0 and <= 40
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')
                ? value
                : "unrecognized-outcome";
    }
}
