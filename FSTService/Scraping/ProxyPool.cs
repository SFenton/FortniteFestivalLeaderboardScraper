using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

public enum ProxyFailureKind
{
    Transport,
    Timeout,
    CdnBlock,
    RateLimited,
    ServerError,
}

public interface IProxyHealthReporter
{
    void ReportSuccess(HttpRequestMessage request);
    void ReportFailure(HttpRequestMessage request, ProxyFailureKind kind);
}

internal interface IProxyRateLimitReporter
{
    void ReportRateLimited(
        HttpRequestMessage request,
        TimeSpan? retryAfter,
        string? mediaType = null);
}

public interface IProxyCdnBlockHandler
{
    /// <summary>
    /// Reports a CDN block for the proxy used by <paramref name="request"/>.
    /// Returns whether the caller should retry through the proxy pool or pause
    /// globally because the request was not routed through a known proxy.
    /// </summary>
    ProxyCdnBlockDecision ReportCdnBlock(HttpRequestMessage request);
}

public enum ProxyCdnBlockDecision
{
    RetryOnAlternateProxy,
    WaitForProxyCooldown,
    PauseGlobally,
}

internal static class ProxyRequestState
{
    public static readonly HttpRequestOptionsKey<int> EndpointIndex = new("FSTService.ProxyEndpointIndex");
    public static readonly HttpRequestOptionsKey<string> EndpointName = new("FSTService.ProxyEndpointName");
    public static readonly HttpRequestOptionsKey<Uri> EndpointProxyUri = new("FSTService.ProxyEndpointProxyUri");
    /// <summary>
    /// Tunnel generation of the lease. Reports from an older generation (a
    /// request sent before a reconnect or restart) are ignored so they cannot
    /// cool, burn, or restart the replacement tunnel.
    /// </summary>
    public static readonly HttpRequestOptionsKey<long> EndpointGeneration = new("FSTService.ProxyEndpointGeneration");
    public static readonly HttpRequestOptionsKey<Action<bool>> WireSendRecorder =
        new("FSTService.ProxyWireSendRecorder");
}

internal sealed class ProxyPool :
    IProxyHealthReporter,
    IProxyRateLimitReporter,
    IProxyCdnBlockHandler,
    IDisposable
{
    private readonly List<ProxyEndpoint> _endpoints;
    private readonly ILogger<ProxyPool> _log;
    private readonly bool _activeStandby;
    private readonly TimeSpan _activeRotationInterval;
    private readonly TimeSpan _baseCooldown;
    private readonly IProxyContainerRecycler? _containerRecycler;
    private readonly bool _containerSelfHealEnabled;
    private readonly int _containerRestartFailureThreshold;
    private readonly TimeSpan _containerRestartMinInterval;
    private readonly TimeSpan _containerRestartCooldown;
    private readonly int _timeoutFailureThreshold;
    private readonly int _httpFailureThreshold;
    private readonly int _perEndpointMaxRequestsPerSecond;
    private readonly int _perEndpointMaxConcurrentRequests;
    private readonly bool _disableConnectionReuse;
    private readonly bool _useCurlTransport;
    private readonly string _curlTempDirectory;
    private readonly IProxyRegionRotator? _regionRotator;
    private readonly bool _regionRotationEnabled;
    private readonly IReadOnlyList<string> _regionRotationRegions;
    private readonly int _regionRotationThreshold;
    private readonly TimeSpan _regionRotationMinInterval;
    private readonly TimeSpan _regionRotationGlobalInterval;
    private readonly int _regionRotationMaxConcurrent;
    private readonly bool _regionRotationReconnectInPlace;
    private readonly TimeSpan _burnedEgressTtl;
    private readonly int _regionRotationRequestBudget;
    private readonly TimeSpan _regionRotationDrain;
    private readonly SemaphoreSlim _regionRotationGate;
    private readonly CancellationTokenSource _regionRotationCancellation = new();
    private readonly Dictionary<IPAddress, DateTimeOffset> _rateLimitedEgress = new();
    private readonly Timer? _summaryTimer;
    private readonly object _lock = new();
    private int _activeIndex;
    private int _nextRoundRobinIndex;
    private DateTimeOffset _activeSince = DateTimeOffset.UtcNow;
    private DateTimeOffset _nextRegionRotationStart;
    private PoolWindowStats _stats;
    private DateTimeOffset _statsSince = DateTimeOffset.UtcNow;
    private bool _disposed;

    internal static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EgressCensusMaxAge = TimeSpan.FromMinutes(5);

    public ProxyPool(
        IOptions<ScraperOptions> options,
        ILogger<ProxyPool> log,
        IProxyContainerRecycler containerRecycler,
        IProxyRegionRotator? regionRotator = null)
        : this(options.Value, log, containerRecycler, regionRotator)
    {
    }

    internal ProxyPool(
        ScraperOptions options,
        ILogger<ProxyPool> log,
        IProxyContainerRecycler? containerRecycler = null,
        IProxyRegionRotator? regionRotator = null)
    {
        ValidateExpectedConfiguration(options);
        if (options.ProxyRegionRotationEnabled && regionRotator is null)
        {
            throw new InvalidOperationException(
                "PIA region rotation requires a worker-owned region rotator.");
        }

        _log = log;
        _activeStandby = options.ProxyActiveStandby;
        _activeRotationInterval = options.ProxyActiveRotationSeconds > 0
            ? TimeSpan.FromSeconds(options.ProxyActiveRotationSeconds)
            : TimeSpan.Zero;
        _baseCooldown = TimeSpan.FromSeconds(Math.Max(1, options.ProxyCooldownSeconds));
        _containerRecycler = containerRecycler;
        _containerSelfHealEnabled = options.ProxyContainerSelfHealEnabled;
        _containerRestartFailureThreshold = Math.Max(1, options.ProxyContainerRestartFailureThreshold);
        _containerRestartMinInterval = TimeSpan.FromSeconds(Math.Max(1, options.ProxyContainerRestartMinIntervalSeconds));
        _containerRestartCooldown = TimeSpan.FromSeconds(Math.Max(1, options.ProxyContainerRestartCooldownSeconds));
        _timeoutFailureThreshold = Math.Max(1, options.ProxyTimeoutFailureThreshold);
        _httpFailureThreshold = Math.Max(1, options.ProxyHttpFailureThreshold);
        _perEndpointMaxRequestsPerSecond = options.ProxyMaxRequestsPerSecondPerEndpoint;
        _perEndpointMaxConcurrentRequests = options.ProxyMaxConcurrentRequestsPerEndpoint;
        _disableConnectionReuse = options.ProxyDisableConnectionReuse;
        _useCurlTransport = options.ProxyUseCurlTransport;
        _curlTempDirectory = options.ProxyCurlTempDirectory;
        _regionRotator = regionRotator;
        _regionRotationEnabled = options.ProxyRegionRotationEnabled;
        _regionRotationRegions = options.ProxyRegionRotationRegions;
        _regionRotationThreshold = options.ProxyRegionRotationRateLimitThreshold;
        _regionRotationMinInterval =
            TimeSpan.FromSeconds(options.ProxyRegionRotationMinIntervalSeconds);
        _regionRotationGlobalInterval =
            TimeSpan.FromSeconds(options.ProxyRegionRotationGlobalIntervalSeconds);
        _regionRotationMaxConcurrent = Math.Max(1, options.ProxyRegionRotationMaxConcurrent);
        _regionRotationReconnectInPlace = options.ProxyRegionRotationReconnectInPlace;
        _burnedEgressTtl = TimeSpan.FromSeconds(Math.Max(0, options.ProxyRegionRotationBurnedEgressTtlSeconds));
        _regionRotationRequestBudget = Math.Max(0, options.ProxyRegionRotationRequestBudget);
        _regionRotationDrain = TimeSpan.FromSeconds(Math.Max(0, options.ProxyRegionRotationDrainSeconds));
        _regionRotationGate = new SemaphoreSlim(_regionRotationMaxConcurrent, _regionRotationMaxConcurrent);

        _endpoints = BuildEndpoints(options).ToList();
        if (_endpoints.Count > 0)
        {
            _log.LogInformation(
                "Epic proxy pool enabled with {Count} endpoint(s); mode={Mode}, cooldown={CooldownSeconds}s, rotation={RotationSeconds}s, perEndpointRps={PerEndpointRps}, perEndpointConcurrency={PerEndpointConcurrency}, connectionReuse={ConnectionReuse}.",
                _endpoints.Count,
                _activeStandby ? "active-standby" : "least-in-flight",
                _baseCooldown.TotalSeconds,
                _activeRotationInterval.TotalSeconds,
                _perEndpointMaxRequestsPerSecond,
                _perEndpointMaxConcurrentRequests,
                _disableConnectionReuse ? "disabled" : "enabled");

            if (_containerSelfHealEnabled)
            {
                int restartable = _endpoints.Count(e => !string.IsNullOrWhiteSpace(e.ContainerName));
                _log.LogInformation(
                    "Proxy container self-heal enabled for {Restartable}/{Count} endpoint(s); threshold={Threshold}, minInterval={MinIntervalSeconds}s, restartCooldown={CooldownSeconds}s.",
                    restartable,
                    _endpoints.Count,
                    _containerRestartFailureThreshold,
                    _containerRestartMinInterval.TotalSeconds,
                    _containerRestartCooldown.TotalSeconds);

                if (_containerRecycler is null)
                {
                    _log.LogWarning(
                        "Proxy container self-heal is enabled, but no container recycler is registered; proxy containers will not be restarted.");
                }
                else if (restartable < _endpoints.Count)
                {
                    _log.LogWarning(
                        "Proxy container self-heal requires Scraper:ContainerNames aligned with Scraper:ProxyUrls; {Missing} endpoint(s) cannot be restarted.",
                        _endpoints.Count - restartable);
                }
            }

            if (_regionRotationEnabled)
            {
                _log.LogInformation(
                    "Worker PIA region rotation enabled for {Count} exits with {Regions} qualified candidate(s), reconnectInPlace={ReconnectInPlace}, threshold={Threshold} HTTP 429s, requestBudget={Budget}, maxConcurrent={MaxConcurrent}, perExitMinInterval={PerExitSeconds}s, globalInterval={GlobalSeconds}s, rateLimitedEgressTtl={TtlSeconds}s.",
                    _endpoints.Count,
                    _regionRotationRegions.Count,
                    _regionRotationReconnectInPlace,
                    _regionRotationThreshold,
                    _regionRotationRequestBudget,
                    _regionRotationMaxConcurrent,
                    _regionRotationMinInterval.TotalSeconds,
                    _regionRotationGlobalInterval.TotalSeconds,
                    _burnedEgressTtl.TotalSeconds);
                _ = Task.Run(() => EgressCensusLoopAsync(_regionRotationCancellation.Token));
            }

            _summaryTimer = new Timer(
                static state => ((ProxyPool)state!).LogSummary(),
                this, SummaryInterval, SummaryInterval);
        }
    }

    public bool IsEnabled => _endpoints.Count > 0;

    internal int EndpointCount => _endpoints.Count;

    internal IReadOnlyList<string> EndpointNames => _endpoints.Select(e => e.Name).ToList();

    internal bool IsRegionRotationActive(int index)
    {
        lock (_lock)
        {
            var endpoint = _endpoints[index];
            return endpoint.RotationPending || endpoint.RegionRotationTask is not null;
        }
    }

    internal bool UseCurlTransport => _useCurlTransport;

    /// <summary>True when per-exit 429s are answered by refreshing that exit's egress.</summary>
    internal bool RefreshesRateLimitedExits => _regionRotationEnabled && _endpoints.Count > 0;

    internal string CurlTempDirectory => _curlTempDirectory;

    internal void PrepareRequest(HttpRequestMessage request)
    {
        if (_disableConnectionReuse)
            request.Headers.ConnectionClose = true;
    }

    internal async ValueTask<ProxyLease?> AcquireAsync(CancellationToken ct)
    {
        if (_endpoints.Count == 0)
            return null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan delay = TimeSpan.Zero;
            ProxyEndpoint? selected;
            lock (_lock)
            {
                ThrowIfDisposed();

                var now = DateTimeOffset.UtcNow;
                selected = _activeStandby
                    ? SelectActiveStandby(now)
                    : SelectLeastLoaded(now);

                if (selected is not null)
                {
                    selected.InFlight++;
                }
                else
                {
                    delay = GetDelayUntilNextEndpoint(now);
                }
            }

            if (selected is not null)
            {
                try
                {
                    using var pacingLease = selected.RateLimiter is null
                        ? null
                        : await selected.RateLimiter.AcquireAsync(1, ct);
                    if (pacingLease is { IsAcquired: false })
                    {
                        Release(selected.Index);
                        await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
                        continue;
                    }

                    lock (_lock)
                    {
                        ThrowIfDisposed();
                        if (selected.RotationPending
                            || selected.CooldownUntil > DateTimeOffset.UtcNow)
                        {
                            if (selected.InFlight > 0)
                                selected.InFlight--;
                            continue;
                        }

                        selected.TotalSelected++;
                        return new ProxyLease(
                            this,
                            selected.Index,
                            selected.Name,
                            selected.ProxyUri,
                            selected.Invoker,
                            selected.Generation);
                    }
                }
                catch
                {
                    Release(selected.Index);
                    throw;
                }
            }

            await Task.Delay(delay, ct);
        }
    }

    public void ReportSuccess(HttpRequestMessage request)
    {
        lock (_lock)
        {
            if (!TryGetCurrentEndpoint(request, out var endpoint))
                return;

            endpoint.ConsecutiveCdnBlocks = 0;
            endpoint.ConsecutiveHttpFailures = 0;
            endpoint.ConsecutiveTransportFailures = 0;
            endpoint.RestartableCooldownFailures = 0;
            endpoint.ConsecutiveRateLimits = 0;
            endpoint.Successes++;
            endpoint.CountGenerationSuccess();
            _stats.Successes++;
            if (_regionRotationRequestBudget > 0
                && endpoint.GenerationSuccesses >= _regionRotationRequestBudget)
            {
                TryScheduleRegionRotation(endpoint, RegionRotationTrigger.RequestBudget);
            }
        }
    }

    public void ReportFailure(HttpRequestMessage request, ProxyFailureKind kind)
    {
        lock (_lock)
        {
            if (!TryGetCurrentEndpoint(request, out var endpoint))
                return;

            ReportFailureCore(endpoint.Index, kind);
        }
    }

    public void ReportRateLimited(
        HttpRequestMessage request,
        TimeSpan? retryAfter,
        string? mediaType = null)
    {
        lock (_lock)
        {
            if (!TryGetCurrentEndpoint(request, out var endpoint))
                return;

            var now = DateTimeOffset.UtcNow;
            endpoint.Failures++;
            _stats.RateLimited++;
            if (mediaType is not null
                && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                _stats.RateLimitedHtml++;
            if (retryAfter is { } positive && positive > TimeSpan.Zero)
                endpoint.RetryAfterUntil = Max(endpoint.RetryAfterUntil, AddCooldownSafely(now, positive));
            if (endpoint.KnownEgress is { } egress && _burnedEgressTtl > TimeSpan.Zero)
                _rateLimitedEgress[egress] = now;
            CoolDown(
                endpoint,
                ProxyFailureKind.RateLimited,
                retryAfter is { } delay && delay > TimeSpan.Zero
                    ? delay
                    : null);

            endpoint.ConsecutiveCdnBlocks = 0;
            endpoint.ConsecutiveHttpFailures = 0;
            endpoint.ConsecutiveTransportFailures = 0;
            endpoint.ConsecutiveRateLimits++;
            if (endpoint.ConsecutiveRateLimits >= _regionRotationThreshold)
                TryScheduleRegionRotation(endpoint, RegionRotationTrigger.RateLimited);

            if (_activeStandby && endpoint.Index == _activeIndex)
                RotateActive(now, $"proxy {endpoint.Name} reported {ProxyFailureKind.RateLimited}");
        }
    }

    public ProxyCdnBlockDecision ReportCdnBlock(HttpRequestMessage request)
    {
        if (!TryGetEndpointIndex(request, out _))
            return ProxyCdnBlockDecision.PauseGlobally;

        lock (_lock)
        {
            if (!TryGetEndpointIndex(request, out var index) || !IsValidIndex(index))
                return ProxyCdnBlockDecision.PauseGlobally;

            if (TryGetCurrentEndpoint(request, out _))
                ReportFailureCore(index, ProxyFailureKind.CdnBlock);
            return HasAvailableEndpoint(DateTimeOffset.UtcNow)
                ? ProxyCdnBlockDecision.RetryOnAlternateProxy
                : ProxyCdnBlockDecision.WaitForProxyCooldown;
        }
    }

    internal void ReportFailure(int index, ProxyFailureKind kind)
    {
        lock (_lock)
        {
            ReportFailureCore(index, kind);
        }
    }

    private void ReportFailureCore(int index, ProxyFailureKind kind)
    {
        if (!IsValidIndex(index))
            return;

        var endpoint = _endpoints[index];
        endpoint.Failures++;

        bool shouldCooldown = kind switch
        {
            ProxyFailureKind.CdnBlock => ++endpoint.ConsecutiveCdnBlocks >= 1,
            ProxyFailureKind.Timeout => ++endpoint.ConsecutiveTransportFailures >= _timeoutFailureThreshold,
            ProxyFailureKind.Transport => ++endpoint.ConsecutiveTransportFailures >= _timeoutFailureThreshold,
            ProxyFailureKind.RateLimited => ++endpoint.ConsecutiveHttpFailures >= _httpFailureThreshold,
            ProxyFailureKind.ServerError => ++endpoint.ConsecutiveHttpFailures >= _httpFailureThreshold,
            _ => false,
        };

        if (!shouldCooldown)
            return;

        CoolDown(endpoint, kind);
        endpoint.ConsecutiveCdnBlocks = 0;
        endpoint.ConsecutiveHttpFailures = 0;
        endpoint.ConsecutiveTransportFailures = 0;

        TryScheduleContainerRestart(endpoint, kind);

        if (_activeStandby && endpoint.Index == _activeIndex)
            RotateActive(DateTimeOffset.UtcNow, $"proxy {endpoint.Name} reported {kind}");
    }

    internal void Release(int index)
    {
        lock (_lock)
        {
            if (!IsValidIndex(index))
                return;

            var endpoint = _endpoints[index];
            if (endpoint.InFlight > 0)
                endpoint.InFlight--;
        }
    }

    private ProxyEndpoint? SelectActiveStandby(DateTimeOffset now)
    {
        if (_activeIndex < 0 || _activeIndex >= _endpoints.Count)
        {
            _activeIndex = 0;
            _activeSince = now;
        }

        if (_activeRotationInterval > TimeSpan.Zero &&
            now - _activeSince >= _activeRotationInterval)
        {
            RotateActive(now, "proactive rotation interval elapsed");
        }

        var active = _endpoints[_activeIndex];
        if (IsSelectable(active, now))
            return active;

        RotateActive(now, $"active proxy {active.Name} is cooling down");
        active = _endpoints[_activeIndex];
        return IsSelectable(active, now) ? active : null;
    }

    private ProxyEndpoint? SelectLeastLoaded(DateTimeOffset now)
    {
        ProxyEndpoint? selected = null;
        int start = _nextRoundRobinIndex;

        for (int offset = 0; offset < _endpoints.Count; offset++)
        {
            int index = (start + offset) % _endpoints.Count;
            var endpoint = _endpoints[index];
            if (!IsSelectable(endpoint, now))
                continue;

            if (selected is null ||
                endpoint.InFlight < selected.InFlight ||
                (endpoint.InFlight == selected.InFlight && endpoint.LastSelectedAt < selected.LastSelectedAt))
            {
                selected = endpoint;
            }
        }

        if (selected is not null)
        {
            selected.LastSelectedAt = now;
            _nextRoundRobinIndex = (selected.Index + 1) % _endpoints.Count;
        }

        return selected;
    }

    private bool HasAvailableEndpoint(DateTimeOffset now)
        => _endpoints.Any(e => !e.RotationPending && e.CooldownUntil <= now);

    private bool IsSelectable(ProxyEndpoint endpoint, DateTimeOffset now) =>
        !endpoint.RotationPending && endpoint.CooldownUntil <= now &&
        (_perEndpointMaxConcurrentRequests <= 0 ||
         endpoint.InFlight < _perEndpointMaxConcurrentRequests);

    private void RotateActive(DateTimeOffset now, string reason)
    {
        int start = _activeIndex;
        for (int offset = 1; offset <= _endpoints.Count; offset++)
        {
            int candidate = (start + offset) % _endpoints.Count;
            if (IsSelectable(_endpoints[candidate], now))
            {
                if (candidate != _activeIndex)
                {
                    var oldName = IsValidIndex(_activeIndex) ? _endpoints[_activeIndex].Name : "<none>";
                    _activeIndex = candidate;
                    _activeSince = now;
                    _log.LogWarning(
                        "VPN live proxy rotated {OldProxy} -> {NewProxy}: {Reason}",
                        oldName, _endpoints[candidate].Name, reason);
                }
                return;
            }
        }
    }

    private void CoolDown(
        ProxyEndpoint endpoint,
        ProxyFailureKind kind,
        TimeSpan? minimumCooldown = null)
    {
        var now = DateTimeOffset.UtcNow;
        var requestedCooldown = minimumCooldown ?? TimeSpan.Zero;
        var cooldown = _baseCooldown >= requestedCooldown
            ? _baseCooldown
            : requestedCooldown;
        endpoint.CooldownUntil = Max(
            endpoint.CooldownUntil,
            AddCooldownSafely(now, cooldown));
        endpoint.Cooldowns++;

        _log.LogWarning(
            "Proxy {ProxyName} ({Provider}) appears tarpitted after {FailureKind}; cooling down for {CooldownSeconds:F0}s (in-flight={InFlight}, failures={Failures}).",
            endpoint.Name,
            endpoint.Provider,
            kind,
            cooldown.TotalSeconds,
            endpoint.InFlight,
            endpoint.Failures);
    }

    private void TryScheduleContainerRestart(ProxyEndpoint endpoint, ProxyFailureKind kind)
    {
        if (!_containerSelfHealEnabled || _containerRecycler is null || !IsRestartableFailure(kind))
            return;

        if (endpoint.RotationPending)
            return;

        if (string.IsNullOrWhiteSpace(endpoint.ContainerName))
            return;

        endpoint.RestartableCooldownFailures++;
        if (endpoint.RestartableCooldownFailures < _containerRestartFailureThreshold)
            return;

        // With egress refresh enabled, a broken tunnel is first reconnected (or
        // moved to a qualified region) through the control API: a container
        // restart returns to the static region, which may itself be dead. A
        // refresh that cannot reach the control API falls back to a restart.
        if (_regionRotationEnabled && !endpoint.PreferContainerRestart)
        {
            var before = endpoint.RegionRotationTask;
            TryScheduleRegionRotation(endpoint, RegionRotationTrigger.TransportFailure);
            if (!ReferenceEquals(before, endpoint.RegionRotationTask))
                endpoint.RestartableCooldownFailures = 0;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (endpoint.ContainerRestartTask is { IsCompleted: false })
            return;

        if (endpoint.LastContainerRestartAttempt is { } lastAttempt &&
            now - lastAttempt < _containerRestartMinInterval)
        {
            _log.LogDebug(
                "Proxy {ProxyName} container restart suppressed after {FailureKind}; last restart attempt was {ElapsedSeconds:F0}s ago.",
                endpoint.Name,
                kind,
                (now - lastAttempt).TotalSeconds);
            return;
        }

        endpoint.RestartableCooldownFailures = 0;
        endpoint.LastContainerRestartAttempt = now;
        endpoint.CooldownUntil = Max(endpoint.CooldownUntil, now + _containerRestartCooldown);

        var endpointIndex = endpoint.Index;
        var endpointName = endpoint.Name;
        var containerName = endpoint.ContainerName;
        endpoint.ContainerRestartTask = Task.Run(() =>
            RestartProxyContainerAsync(endpointIndex, endpointName, containerName, kind));

        _log.LogWarning(
            "Proxy {ProxyName} ({Provider}) scheduled Docker restart for container {ContainerName} after {FailureKind}; held out of rotation for {CooldownSeconds:F0}s.",
            endpoint.Name,
            endpoint.Provider,
            containerName,
            kind,
            _containerRestartCooldown.TotalSeconds);
    }

    private async Task RestartProxyContainerAsync(
        int endpointIndex,
        string endpointName,
        string containerName,
        ProxyFailureKind kind)
    {
        bool restarted = await _containerRecycler!.RestartAsync(containerName);
        HttpMessageInvoker? oldInvoker = null;

        lock (_lock)
        {
            if (!_disposed && IsValidIndex(endpointIndex))
            {
                var endpoint = _endpoints[endpointIndex];
                endpoint.ContainerRestartTask = null;

                if (restarted)
                {
                    endpoint.ConsecutiveCdnBlocks = 0;
                    endpoint.ConsecutiveHttpFailures = 0;
                    endpoint.ConsecutiveTransportFailures = 0;
                    endpoint.RestartableCooldownFailures = 0;
                    endpoint.CooldownUntil = Max(endpoint.CooldownUntil, DateTimeOffset.UtcNow + _baseCooldown);
                    endpoint.PreferContainerRestart = false;
                    endpoint.AdvanceGeneration(egress: null);
                    oldInvoker = endpoint.ResetInvoker();
                }
            }
        }

        oldInvoker?.Dispose();

        if (restarted)
        {
            _log.LogInformation(
                "Proxy {ProxyName} container {ContainerName} restarted after {FailureKind}; connection pool reset.",
                endpointName,
                containerName,
                kind);
        }
        else
        {
            _log.LogWarning(
                "Proxy {ProxyName} container {ContainerName} restart failed after {FailureKind}; proxy remains cooled down.",
                endpointName,
                containerName,
                kind);
        }
    }

    private static bool IsRestartableFailure(ProxyFailureKind kind)
        => kind is ProxyFailureKind.Transport or ProxyFailureKind.Timeout;

    private enum RegionRotationTrigger
    {
        RateLimited,
        RequestBudget,
        DuplicateEgress,
        TransportFailure,
    }

    private void TryScheduleRegionRotation(
        ProxyEndpoint endpoint, RegionRotationTrigger trigger)
    {
        if (!_regionRotationEnabled
            || _disposed
            || endpoint.RegionRotationTask is { IsCompleted: false }
            || endpoint.RotationPending
            || endpoint.CooldownUntil == DateTimeOffset.MaxValue)
            return;

        var now = DateTimeOffset.UtcNow;
        if (endpoint.LastRegionRotationAttempt is { } lastAttempt
            && now - lastAttempt < _regionRotationMinInterval)
            return;

        // Hold the exit out immediately: after a rate limit its egress is
        // spent, and every further request would only extend the block.
        endpoint.RotationPending = true;
        endpoint.ConsecutiveRateLimits = 0;
        _stats.RotationsScheduled++;
        var index = endpoint.Index;
        endpoint.RegionRotationTask = Task.Run(() =>
            RotateRegionAsync(index, trigger, _regionRotationCancellation.Token));
        if (trigger == RegionRotationTrigger.RateLimited && _regionRotationThreshold > 1)
        {
            _log.LogWarning(
                "PIA proxy {ProxyName} scheduled a bounded region change after {Threshold} HTTP 429s; existing Retry-After cooldown remains in force.",
                endpoint.Name, _regionRotationThreshold);
        }
        else
        {
            _log.LogDebug(
                "PIA proxy {ProxyName} scheduled an egress refresh ({Trigger}) after {Successes} successful request(s) on its current egress.",
                endpoint.Name, trigger, endpoint.GenerationSuccesses);
        }
    }

    private async Task RotateRegionAsync(
        int endpointIndex, RegionRotationTrigger trigger, CancellationToken ct)
    {
        bool acquired = false;
        HttpMessageInvoker? oldInvoker = null;
        long startedAt = 0;
        try
        {
            await _regionRotationGate.WaitAsync(ct);
            acquired = true;

            TimeSpan globalDelay;
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                var startAt = Max(now, _nextRegionRotationStart);
                _nextRegionRotationStart = startAt + _regionRotationGlobalInterval;
                globalDelay = startAt - now;
            }
            if (globalDelay > TimeSpan.Zero)
                await Task.Delay(globalDelay, ct);

            Task? restartTask;
            lock (_lock)
            {
                restartTask = _endpoints[endpointIndex].ContainerRestartTask;
            }
            if (restartTask is not null)
                await restartTask.WaitAsync(ct);

            Uri baselineProxy;
            lock (_lock)
            {
                if (_disposed)
                    return;
                baselineProxy = _endpoints[endpointIndex].ProxyUri;
            }

            // Establish the real current egress immediately before changing
            // the tunnel: the census value may be missing or stale.
            var baseline = await _regionRotator!.GetEgressAsync(baselineProxy, ct);

            ProxyRegionRotationRequest request;
            lock (_lock)
            {
                if (_disposed)
                    return;

                var endpoint = _endpoints[endpointIndex];
                if (baseline is not null)
                {
                    endpoint.KnownEgress = baseline;
                    endpoint.EgressObservedAt = DateTimeOffset.UtcNow;
                    if (trigger == RegionRotationTrigger.RateLimited && _burnedEgressTtl > TimeSpan.Zero)
                        _rateLimitedEgress[baseline] = DateTimeOffset.UtcNow;
                }
                endpoint.LastRegionRotationAttempt = DateTimeOffset.UtcNow;
                var candidateOffset = _regionRotationRegions.Count == 0
                    ? 0
                    : (endpoint.Index + endpoint.RegionRotationAttempts++)
                        % _regionRotationRegions.Count;
                endpoint.PendingEgress = null;
                request = new ProxyRegionRotationRequest(
                    new ProxyRegionTunnel(
                        endpoint.ContainerName, endpoint.ProxyUri,
                        new Uri(endpoint.ControlUrl)),
                    endpoint.KnownEgress,
                    new EndpointEgressClaims(this, endpointIndex),
                    _regionRotationRegions,
                    candidateOffset,
                    _regionRotationReconnectInPlace);
                _stats.RotationsStarted++;
            }

            var drainDeadline = DateTimeOffset.UtcNow + _regionRotationDrain;
            while (DateTimeOffset.UtcNow < drainDeadline)
            {
                ct.ThrowIfCancellationRequested();
                int inFlight;
                lock (_lock)
                {
                    inFlight = _endpoints[endpointIndex].InFlight;
                }
                if (inFlight == 0)
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }

            startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = await _regionRotator!.RotateAsync(request, ct);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
            lock (_lock)
            {
                if (_disposed)
                    return;

                var endpoint = _endpoints[endpointIndex];
                endpoint.PendingEgress = null;
                var now = DateTimeOffset.UtcNow;
                _stats.RotationMilliseconds += (long)elapsed.TotalMilliseconds;
                switch (result.Outcome)
                {
                    case ProxyRegionRotationOutcome.Rotated:
                        _stats.Rotated++;
                        _stats.RetiredEgress++;
                        _stats.RetiredEgressSuccesses += endpoint.GenerationSuccesses;
                        _log.LogDebug(
                            "PIA proxy {Container} retired an egress after {Successes} successful request(s) ({Trigger}).",
                            endpoint.Name, endpoint.GenerationSuccesses, trigger);
                        endpoint.AdvanceGeneration(result.Egress);
                        endpoint.PreferContainerRestart = false;
                        endpoint.ConsecutiveCdnBlocks = 0;
                        endpoint.ConsecutiveHttpFailures = 0;
                        endpoint.ConsecutiveTransportFailures = 0;
                        endpoint.ConsecutiveRateLimits = 0;
                        endpoint.RestartableCooldownFailures = 0;
                        // A fresh egress is not subject to the previous exit's
                        // cooldown; an explicit Retry-After is still honored.
                        endpoint.CooldownUntil = endpoint.RetryAfterUntil > now
                            ? endpoint.RetryAfterUntil
                            : now;
                        oldInvoker = endpoint.ResetInvoker();
                        break;
                    case ProxyRegionRotationOutcome.Restored:
                        _stats.Restored++;
                        // The tunnel was changed (possibly the container
                        // restarted) even when the egress is the same.
                        endpoint.AdvanceGeneration(result.Egress);
                        endpoint.PreferContainerRestart = false;
                        oldInvoker = endpoint.ResetInvoker();
                        endpoint.CooldownUntil = Max(endpoint.CooldownUntil, now + _baseCooldown);
                        break;
                    case ProxyRegionRotationOutcome.Deferred:
                        _stats.Deferred++;
                        if (trigger == RegionRotationTrigger.TransportFailure)
                            endpoint.PreferContainerRestart = true;
                        endpoint.CooldownUntil = Max(endpoint.CooldownUntil, now + _baseCooldown);
                        break;
                    default:
                        _stats.Unsafe++;
                        endpoint.AdvanceGeneration(egress: null);
                        endpoint.CooldownUntil = DateTimeOffset.MaxValue;
                        _log.LogError(
                            "PIA proxy {Container} is quarantined: region change and automatic restoration did not verify a healthy distinct egress.",
                            endpoint.Name);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogInformation(
                "PIA proxy region change canceled for endpoint {EndpointIndex}.", endpointIndex);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (!_disposed && startedAt != 0)
                {
                    _stats.Unsafe++;
                    _endpoints[endpointIndex].AdvanceGeneration(egress: null);
                    _endpoints[endpointIndex].CooldownUntil = DateTimeOffset.MaxValue;
                }
            }
            _log.LogError(
                ex, "PIA proxy region change failed unexpectedly for endpoint {EndpointIndex}; the exit was quarantined.",
                endpointIndex);
        }
        finally
        {
            oldInvoker?.Dispose();
            lock (_lock)
            {
                if (!_disposed)
                {
                    _endpoints[endpointIndex].RotationPending = false;
                    _endpoints[endpointIndex].PendingEgress = null;
                    _endpoints[endpointIndex].RegionRotationTask = null;
                }
            }
            if (acquired)
                _regionRotationGate.Release();
        }
    }

    /// <summary>
    /// Atomically rejects an egress that another exit already uses (or is
    /// about to use), or that returned HTTP 429 within the configured window,
    /// and otherwise records it as the rotating exit's pending egress.
    /// </summary>
    internal string? TryClaimEgress(int endpointIndex, IPAddress address, bool allowRateLimited)
    {
        lock (_lock)
        {
            if (_disposed || !IsValidIndex(endpointIndex))
                return "disposed";

            foreach (var other in _endpoints)
            {
                if (other.Index != endpointIndex
                    && (address.Equals(other.KnownEgress) || address.Equals(other.PendingEgress)))
                    return "peer-duplicate";
            }

            if (!allowRateLimited
                && _rateLimitedEgress.TryGetValue(address, out var limitedAt)
                && DateTimeOffset.UtcNow - limitedAt < _burnedEgressTtl)
                return "rate-limited";

            _endpoints[endpointIndex].PendingEgress = address;
            return null;
        }
    }

    private sealed class EndpointEgressClaims : IProxyEgressClaims
    {
        private readonly ProxyPool _pool;
        private readonly int _index;

        public EndpointEgressClaims(ProxyPool pool, int index)
        {
            _pool = pool;
            _index = index;
        }

        public string? TryClaim(IPAddress address, bool allowRateLimited)
            => _pool.TryClaimEgress(_index, address, allowRateLimited);
    }

    /// <summary>
    /// Keeps each exit's real egress known (so rotations can enforce distinct
    /// egress without probing every peer) and detects out-of-band changes or
    /// duplicates, e.g. after Gluetun's own health restart. One benign IP-echo
    /// probe per exit at most every five minutes; never Epic traffic.
    /// </summary>
    private async Task EgressCensusLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            using var gate = new SemaphoreSlim(2, 2);
            while (!ct.IsCancellationRequested)
            {
                List<(int Index, long Generation, Uri ProxyUri)> due;
                lock (_lock)
                {
                    if (_disposed)
                        return;
                    var now = DateTimeOffset.UtcNow;
                    due = _endpoints
                        .Where(e => !e.RotationPending
                            && e.CooldownUntil != DateTimeOffset.MaxValue
                            && e.ContainerRestartTask is null
                            && (e.KnownEgress is null || now - e.EgressObservedAt >= EgressCensusMaxAge))
                        .Select(e => (e.Index, e.Generation, e.ProxyUri))
                        .ToList();
                }

                await Task.WhenAll(due.Select(async item =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var address = await _regionRotator!.GetEgressAsync(item.ProxyUri, ct);
                        if (address is not null)
                            RecordCensusEgress(item.Index, item.Generation, address);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));

                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Proxy egress census stopped unexpectedly.");
        }
    }

    private void RecordCensusEgress(int index, long generation, IPAddress address)
    {
        HttpMessageInvoker? oldInvoker = null;
        try
        {
            RecordCensusEgressCore(index, generation, address, ref oldInvoker);
        }
        finally
        {
            oldInvoker?.Dispose();
        }
    }

    private void RecordCensusEgressCore(
        int index, long generation, IPAddress address, ref HttpMessageInvoker? oldInvoker)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            var endpoint = _endpoints[index];
            if (endpoint.Generation != generation || endpoint.RotationPending)
                return;

            if (endpoint.KnownEgress is { } known && !known.Equals(address))
            {
                _log.LogInformation(
                    "Proxy {ProxyName} egress changed outside a worker rotation; tracking the new egress.",
                    endpoint.Name);
                endpoint.AdvanceGeneration(address);
                oldInvoker = endpoint.ResetInvoker();
            }
            else
            {
                endpoint.KnownEgress = address;
                endpoint.EgressObservedAt = DateTimeOffset.UtcNow;
            }

            var duplicate = _endpoints.FirstOrDefault(e =>
                e.Index != index && address.Equals(e.KnownEgress));
            if (duplicate is not null)
            {
                _log.LogWarning(
                    "Proxy {ProxyName} shares its egress with {PeerName}; scheduling an egress refresh.",
                    endpoint.Name, duplicate.Name);
                TryScheduleRegionRotation(endpoint, RegionRotationTrigger.DuplicateEgress);
            }
        }
    }

    private void LogSummary()
    {
        PoolWindowStats stats;
        TimeSpan window;
        int selectable = 0, cooling = 0, rotating = 0, quarantined = 0, known = 0, burned;
        lock (_lock)
        {
            if (_disposed)
                return;
            var now = DateTimeOffset.UtcNow;
            stats = _stats;
            _stats = default;
            window = now - _statsSince;
            _statsSince = now;
            foreach (var endpoint in _endpoints)
            {
                if (endpoint.KnownEgress is not null)
                    known++;
                if (endpoint.CooldownUntil == DateTimeOffset.MaxValue)
                    quarantined++;
                else if (endpoint.RotationPending)
                    rotating++;
                else if (endpoint.CooldownUntil > now)
                    cooling++;
                else
                    selectable++;
            }

            if (_burnedEgressTtl > TimeSpan.Zero)
            {
                foreach (var stale in _rateLimitedEgress
                    .Where(pair => now - pair.Value >= _burnedEgressTtl)
                    .Select(pair => pair.Key)
                    .ToList())
                    _rateLimitedEgress.Remove(stale);
            }
            burned = _rateLimitedEgress.Count;
        }

        if (stats.Successes == 0 && stats.RateLimited == 0 && stats.RotationsStarted == 0)
            return;

        var finished = stats.Rotated + stats.Restored + stats.Deferred + stats.Unsafe;
        _log.LogInformation(
            "Proxy pool summary ({WindowSeconds:F0}s): ok={Successes} rateLimited={RateLimited} (html={RateLimitedHtml}) staleReports={Stale}; rotations scheduled={Scheduled} started={Started} rotated={Rotated} restored={Restored} deferred={Deferred} unsafe={Unsafe} avgMs={AvgMs} okPerRetiredEgress={OkPerEgress}; exits selectable={Selectable} cooling={Cooling} rotating={Rotating} quarantined={Quarantined} knownEgress={Known}/{Total} rateLimitedEgress={Burned}.",
            window.TotalSeconds,
            stats.Successes,
            stats.RateLimited,
            stats.RateLimitedHtml,
            stats.StaleReports,
            stats.RotationsScheduled,
            stats.RotationsStarted,
            stats.Rotated,
            stats.Restored,
            stats.Deferred,
            stats.Unsafe,
            finished == 0 ? 0 : stats.RotationMilliseconds / finished,
            stats.RetiredEgress == 0 ? 0 : stats.RetiredEgressSuccesses / stats.RetiredEgress,
            selectable,
            cooling,
            rotating,
            quarantined,
            known,
            _endpoints.Count,
            burned);
    }

    private struct PoolWindowStats
    {
        public long Successes;
        public long RateLimited;
        public long RateLimitedHtml;
        public long StaleReports;
        public long RotationsScheduled;
        public long RotationsStarted;
        public long Rotated;
        public long Restored;
        public long Deferred;
        public long Unsafe;
        public long RotationMilliseconds;
        public long RetiredEgress;
        public long RetiredEgressSuccesses;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right)
        => left >= right ? left : right;

    private static DateTimeOffset AddCooldownSafely(DateTimeOffset start, TimeSpan cooldown)
    {
        var remaining = DateTimeOffset.MaxValue - start;
        return cooldown >= remaining
            ? DateTimeOffset.MaxValue
            : start + cooldown;
    }

    private TimeSpan GetDelayUntilNextEndpoint(DateTimeOffset now)
    {
        var earliest = _endpoints
            .Where(e => !e.RotationPending)
            .Select(e => e.CooldownUntil)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();
        var delay = earliest - now;
        if (delay <= TimeSpan.Zero)
            return TimeSpan.FromMilliseconds(25);
        return delay < TimeSpan.FromSeconds(1) ? delay : TimeSpan.FromSeconds(1);
    }

    private static bool TryGetEndpointIndex(HttpRequestMessage request, out int index)
        => request.Options.TryGetValue(ProxyRequestState.EndpointIndex, out index);

    private bool TryGetCurrentEndpoint(
        HttpRequestMessage request,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ProxyEndpoint? endpoint)
    {
        endpoint = null;
        if (!TryGetEndpointIndex(request, out var index) || !IsValidIndex(index))
            return false;

        var candidate = _endpoints[index];
        if (request.Options.TryGetValue(ProxyRequestState.EndpointGeneration, out var generation)
            && generation != candidate.Generation)
        {
            _stats.StaleReports++;
            return false;
        }

        endpoint = candidate;
        return true;
    }

    private bool IsValidIndex(int index) => index >= 0 && index < _endpoints.Count;

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ProxyPool));
    }

    private static IEnumerable<ProxyEndpoint> BuildEndpoints(ScraperOptions options)
    {
        for (int i = 0; i < options.ProxyUrls.Count; i++)
        {
            var proxyUrl = options.ProxyUrls[i];
            if (string.IsNullOrWhiteSpace(proxyUrl))
                continue;

            if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var proxyUri))
                throw new InvalidOperationException($"Invalid Scraper:ProxyUrls:{i} value: '{proxyUrl}'.");

            string containerName = GetOptional(options.ContainerNames, i);
            string provider = GetOptional(options.VpnProviders, i);
            string controlUrl = GetOptional(options.ControlUrls, i);
            string name = !string.IsNullOrWhiteSpace(containerName) ? containerName : proxyUri.Authority;

            yield return new ProxyEndpoint(
                index: i,
                proxyUri: proxyUri,
                name: name,
                provider: string.IsNullOrWhiteSpace(provider) ? "unknown" : provider,
                controlUrl: controlUrl,
                containerName: containerName,
                maxRequestsPerSecond: options.ProxyMaxRequestsPerSecondPerEndpoint,
                disableConnectionReuse: options.ProxyDisableConnectionReuse);
        }
    }

    private static string GetOptional(IReadOnlyList<string> values, int index)
        => index >= 0 && index < values.Count ? values[index] : "";

    internal static void ValidateExpectedConfiguration(ScraperOptions options)
    {
        int expected = options.ExpectedProxyEndpointCount;
        if (expected < 0)
        {
            throw new InvalidOperationException(
                "Scraper ExpectedProxyEndpointCount cannot be negative.");
        }
        if (options.ProxyRegionRotationEnabled)
        {
            var regionCount = options.ProxyRegionRotationRegions.Count;
            if (expected == 0
                || !options.ProxyUseCurlTransport
                || regionCount > 64
                || (regionCount == 0 && !options.ProxyRegionRotationReconnectInPlace)
                || options.ProxyRegionRotationRegions.Any(
                    region => string.IsNullOrWhiteSpace(region)
                        || region.Length > 80
                        || region.Contains(',')
                        || region.Any(char.IsControl))
                || options.ProxyRegionRotationRegions.Distinct(
                    StringComparer.OrdinalIgnoreCase).Count()
                    != regionCount
                || options.ProxyRegionRotationRateLimitThreshold is < 1 or > 100
                || options.ProxyRegionRotationMinIntervalSeconds is < 5 or > 86_400
                || options.ProxyRegionRotationGlobalIntervalSeconds is < 0 or > 3_600
                || options.ProxyRegionRotationProbeTimeoutSeconds is < 10 or > 360
                || options.ProxyRegionRotationAttemptTimeoutSeconds is < 5 or > 360
                || options.ProxyRegionRotationMaxAttempts is < 1 or > 16
                || options.ProxyRegionRotationMaxConcurrent < 1
                || options.ProxyRegionRotationMaxConcurrent > Math.Max(1, expected)
                || options.ProxyRegionRotationBurnedEgressTtlSeconds is < 0 or > 86_400
                || options.ProxyRegionRotationRequestBudget is < 0 or > 1_000_000
                || (options.ProxyRegionRotationRequestBudget is > 0 and < 10)
                || options.ProxyRegionRotationDrainSeconds is < 0 or > 300
                || !Path.IsPathFullyQualified(options.ProxyCurlTempDirectory)
                || !Path.GetFullPath(options.ProxyCurlTempDirectory).StartsWith(
                    Path.GetFullPath(options.DataDirectory)
                        .TrimEnd(Path.DirectorySeparatorChar)
                        + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Worker PIA region rotation requires an aligned proxy pool, curl transport and same-data-directory curl scratch, 0-64 distinct regions (0 only with reconnect-in-place), threshold 1-100, per-exit interval 5-86400s, global interval 0-3600s, probe timeout 10-360s, attempt timeout 5-360s, 1-16 attempts, 1..exit-count concurrent rotations, rate-limited egress TTL 0-86400s, request budget 0 or 10-1000000, and drain 0-300s.");
            }
        }
        if (expected == 0)
        {
            if (options.ProxyMaxRequestsPerSecondPerEndpoint < 0 ||
                options.ProxyMaxConcurrentRequestsPerEndpoint < 0)
            {
                throw new InvalidOperationException(
                    "Scraper per-endpoint proxy rate and concurrency limits cannot be negative.");
            }
            return;
        }

        if (options.ProxyMaxRequestsPerSecondPerEndpoint < 0 ||
            options.ProxyMaxConcurrentRequestsPerEndpoint < 0)
        {
            throw new InvalidOperationException(
                "Scraper per-endpoint proxy rate and concurrency limits cannot be negative.");
        }

        ValidateAlignedList(nameof(options.ProxyUrls), options.ProxyUrls, expected);
        ValidateAlignedList(nameof(options.ControlUrls), options.ControlUrls, expected);
        ValidateAlignedList(nameof(options.VpnProviders), options.VpnProviders, expected);
        ValidateAlignedList(nameof(options.ContainerNames), options.ContainerNames, expected);
        if (options.ProxyRegionRotationEnabled
            && options.VpnProviders.Any(provider =>
                !provider.Equals("PIA", StringComparison.OrdinalIgnoreCase)
                && !provider.Equals("private internet access", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Worker PIA region rotation requires every aligned VPN provider to be PIA.");
        }

        if (options.ContainerNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != expected)
        {
            throw new InvalidOperationException(
                "Scraper proxy container names must be unique when ExpectedProxyEndpointCount is enabled.");
        }

        for (int index = 0; index < expected; index++)
        {
            string containerName = options.ContainerNames[index];
            ValidateAlignedUri(options.ProxyUrls[index], containerName, 8888, "proxy", index);
            ValidateAlignedUri(options.ControlUrls[index], containerName, 8000, "control", index);
        }
    }

    private static void ValidateAlignedList(
        string name,
        IReadOnlyList<string> values,
        int expected)
    {
        if (values.Count != expected || values.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                $"Scraper {name} must contain exactly {expected} non-empty entries " +
                "when ExpectedProxyEndpointCount is enabled.");
        }
    }

    private static void ValidateAlignedUri(
        string value,
        string containerName,
        int expectedPort,
        string kind,
        int index)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals(containerName, StringComparison.OrdinalIgnoreCase)
            || uri.Port != expectedPort)
        {
            throw new InvalidOperationException(
                $"Scraper {kind} endpoint {index} must target aligned container " +
                $"{containerName} on port {expectedPort}.");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _summaryTimer?.Dispose();
            _regionRotationCancellation.Cancel();
            foreach (var endpoint in _endpoints)
                endpoint.Dispose();
        }
    }

    internal sealed class ProxyLease : IDisposable
    {
        private readonly ProxyPool _pool;
        private int _disposed;

        internal ProxyLease(
            ProxyPool pool,
            int index,
            string name,
            Uri proxyUri,
            HttpMessageInvoker invoker,
            long generation = 0)
        {
            _pool = pool;
            Index = index;
            Name = name;
            ProxyUri = proxyUri;
            Invoker = invoker;
            Generation = generation;
        }

        public int Index { get; }
        public string Name { get; }
        public Uri ProxyUri { get; }
        public HttpMessageInvoker Invoker { get; }
        public long Generation { get; }

        /// <summary>Stamps endpoint identity and tunnel generation onto a request.</summary>
        public void Apply(HttpRequestMessage request)
        {
            request.Options.Set(ProxyRequestState.EndpointIndex, Index);
            request.Options.Set(ProxyRequestState.EndpointName, Name);
            request.Options.Set(ProxyRequestState.EndpointProxyUri, ProxyUri);
            request.Options.Set(ProxyRequestState.EndpointGeneration, Generation);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _pool.Release(Index);
        }
    }

    private sealed class ProxyEndpoint : IDisposable
    {
        public ProxyEndpoint(
            int index,
            Uri proxyUri,
            string name,
            string provider,
            string controlUrl,
            string containerName,
            int maxRequestsPerSecond,
            bool disableConnectionReuse)
        {
            Index = index;
            ProxyUri = proxyUri;
            Name = name;
            Provider = provider;
            ControlUrl = controlUrl;
            ContainerName = containerName;
            DisableConnectionReuse = disableConnectionReuse;
            Invoker = new HttpMessageInvoker(
                CreateHandler(proxyUri, disableConnectionReuse),
                disposeHandler: true);
            if (maxRequestsPerSecond > 0)
            {
                RateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 1,
                    TokensPerPeriod = 1,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1d / maxRequestsPerSecond),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 512,
                    AutoReplenishment = true,
                });
            }
        }

        public int Index { get; }
        public Uri ProxyUri { get; }
        public string Name { get; }
        public string Provider { get; }
        public string ControlUrl { get; }
        public string ContainerName { get; }
        public bool DisableConnectionReuse { get; }
        public HttpMessageInvoker Invoker { get; private set; }
        public TokenBucketRateLimiter? RateLimiter { get; }
        public int InFlight { get; set; }
        public long TotalSelected { get; set; }
        public long Successes { get; set; }
        public long Failures { get; set; }
        public int Cooldowns { get; set; }
        public int RestartableCooldownFailures { get; set; }
        public int ConsecutiveCdnBlocks { get; set; }
        public int ConsecutiveHttpFailures { get; set; }
        public int ConsecutiveTransportFailures { get; set; }
        public int ConsecutiveRateLimits { get; set; }
        public DateTimeOffset CooldownUntil { get; set; }
        public DateTimeOffset LastSelectedAt { get; set; }
        public DateTimeOffset? LastContainerRestartAttempt { get; set; }
        public Task? ContainerRestartTask { get; set; }
        public DateTimeOffset? LastRegionRotationAttempt { get; set; }
        public int RegionRotationAttempts { get; set; }
        public Task? RegionRotationTask { get; set; }
        public bool RotationPending { get; set; }
        public DateTimeOffset RetryAfterUntil { get; set; }
        public bool PreferContainerRestart { get; set; }
        public long Generation { get; private set; }
        public long GenerationSuccesses { get; private set; }
        public IPAddress? KnownEgress { get; set; }
        public IPAddress? PendingEgress { get; set; }
        public DateTimeOffset EgressObservedAt { get; set; }

        public void CountGenerationSuccess() => GenerationSuccesses++;

        /// <summary>
        /// Starts a new tunnel generation after a reconnect, region change, or
        /// container restart. Reports stamped with an older generation are
        /// ignored from now on.
        /// </summary>
        public void AdvanceGeneration(IPAddress? egress)
        {
            Generation++;
            GenerationSuccesses = 0;
            KnownEgress = egress;
            EgressObservedAt = DateTimeOffset.UtcNow;
        }

        public void Dispose()
        {
            RateLimiter?.Dispose();
            Invoker.Dispose();
        }

        public HttpMessageInvoker ResetInvoker()
        {
            var old = Invoker;
            Invoker = new HttpMessageInvoker(
                CreateHandler(ProxyUri, DisableConnectionReuse),
                disposeHandler: true);
            return old;
        }

        private static SocketsHttpHandler CreateHandler(Uri proxyUri, bool disableConnectionReuse)
            => new()
            {
                Proxy = new WebProxy(proxyUri),
                UseProxy = true,
                MaxConnectionsPerServer = disableConnectionReuse ? 1 : 2048,
                PooledConnectionIdleTimeout = disableConnectionReuse
                    ? TimeSpan.Zero
                    : TimeSpan.FromMinutes(2),
                PooledConnectionLifetime = disableConnectionReuse
                    ? TimeSpan.Zero
                    : TimeSpan.FromMinutes(2),
                EnableMultipleHttp2Connections = true,
                AutomaticDecompression = DecompressionMethods.All,
            };
    }
}
