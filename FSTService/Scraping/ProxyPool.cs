using System.Net;
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
}

internal sealed class ProxyPool : IProxyHealthReporter, IProxyCdnBlockHandler, IDisposable
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
    private readonly object _lock = new();
    private int _activeIndex;
    private int _nextRoundRobinIndex;
    private DateTimeOffset _activeSince = DateTimeOffset.UtcNow;
    private bool _disposed;

    public ProxyPool(
        IOptions<ScraperOptions> options,
        ILogger<ProxyPool> log,
        IProxyContainerRecycler containerRecycler)
        : this(options.Value, log, containerRecycler)
    {
    }

    internal ProxyPool(ScraperOptions options, ILogger<ProxyPool> log, IProxyContainerRecycler? containerRecycler = null)
    {
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

        _endpoints = BuildEndpoints(options).ToList();
        if (_endpoints.Count > 0)
        {
            _log.LogInformation(
                "Epic proxy pool enabled with {Count} endpoint(s); mode={Mode}, cooldown={CooldownSeconds}s, rotation={RotationSeconds}s.",
                _endpoints.Count,
                _activeStandby ? "active-standby" : "least-in-flight",
                _baseCooldown.TotalSeconds,
                _activeRotationInterval.TotalSeconds);

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
        }
    }

    public bool IsEnabled => _endpoints.Count > 0;

    internal int EndpointCount => _endpoints.Count;

    internal IReadOnlyList<string> EndpointNames => _endpoints.Select(e => e.Name).ToList();

    internal async ValueTask<ProxyLease?> AcquireAsync(CancellationToken ct)
    {
        if (_endpoints.Count == 0)
            return null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan delay;
            lock (_lock)
            {
                ThrowIfDisposed();

                var now = DateTimeOffset.UtcNow;
                var selected = _activeStandby
                    ? SelectActiveStandby(now)
                    : SelectLeastLoaded(now);

                if (selected is not null)
                {
                    selected.InFlight++;
                    selected.TotalSelected++;
                    return new ProxyLease(this, selected.Index, selected.Name, selected.ProxyUri, selected.Invoker);
                }

                delay = GetDelayUntilNextEndpoint(now);
            }

            await Task.Delay(delay, ct);
        }
    }

    public void ReportSuccess(HttpRequestMessage request)
    {
        if (!TryGetEndpointIndex(request, out var index))
            return;

        lock (_lock)
        {
            if (!IsValidIndex(index))
                return;

            var endpoint = _endpoints[index];
            endpoint.ConsecutiveCdnBlocks = 0;
            endpoint.ConsecutiveHttpFailures = 0;
            endpoint.ConsecutiveTransportFailures = 0;
            endpoint.RestartableCooldownFailures = 0;
            endpoint.Successes++;
        }
    }

    public void ReportFailure(HttpRequestMessage request, ProxyFailureKind kind)
    {
        if (!TryGetEndpointIndex(request, out var index))
            return;

        ReportFailure(index, kind);
    }

    public ProxyCdnBlockDecision ReportCdnBlock(HttpRequestMessage request)
    {
        if (!TryGetEndpointIndex(request, out var index))
            return ProxyCdnBlockDecision.PauseGlobally;

        lock (_lock)
        {
            if (!IsValidIndex(index))
                return ProxyCdnBlockDecision.PauseGlobally;

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
        if (active.CooldownUntil <= now)
            return active;

        RotateActive(now, $"active proxy {active.Name} is cooling down");
        active = _endpoints[_activeIndex];
        return active.CooldownUntil <= now ? active : null;
    }

    private ProxyEndpoint? SelectLeastLoaded(DateTimeOffset now)
    {
        ProxyEndpoint? selected = null;
        int start = _nextRoundRobinIndex;

        for (int offset = 0; offset < _endpoints.Count; offset++)
        {
            int index = (start + offset) % _endpoints.Count;
            var endpoint = _endpoints[index];
            if (endpoint.CooldownUntil > now)
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
        => _endpoints.Any(e => e.CooldownUntil <= now);

    private void RotateActive(DateTimeOffset now, string reason)
    {
        int start = _activeIndex;
        for (int offset = 1; offset <= _endpoints.Count; offset++)
        {
            int candidate = (start + offset) % _endpoints.Count;
            if (_endpoints[candidate].CooldownUntil <= now)
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

    private void CoolDown(ProxyEndpoint endpoint, ProxyFailureKind kind)
    {
        var now = DateTimeOffset.UtcNow;
        var cooldown = _baseCooldown;
        endpoint.CooldownUntil = now + cooldown;
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

        if (string.IsNullOrWhiteSpace(endpoint.ContainerName))
            return;

        endpoint.RestartableCooldownFailures++;
        if (endpoint.RestartableCooldownFailures < _containerRestartFailureThreshold)
            return;

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

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right)
        => left >= right ? left : right;

    private TimeSpan GetDelayUntilNextEndpoint(DateTimeOffset now)
    {
        var earliest = _endpoints.Min(e => e.CooldownUntil);
        var delay = earliest - now;
        if (delay <= TimeSpan.Zero)
            return TimeSpan.FromMilliseconds(25);
        return delay < TimeSpan.FromSeconds(1) ? delay : TimeSpan.FromSeconds(1);
    }

    private static bool TryGetEndpointIndex(HttpRequestMessage request, out int index)
        => request.Options.TryGetValue(ProxyRequestState.EndpointIndex, out index);

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
                containerName: containerName);
        }
    }

    private static string GetOptional(IReadOnlyList<string> values, int index)
        => index >= 0 && index < values.Count ? values[index] : "";

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var endpoint in _endpoints)
                endpoint.Dispose();
        }
    }

    internal sealed class ProxyLease : IDisposable
    {
        private readonly ProxyPool _pool;
        private int _disposed;

        internal ProxyLease(ProxyPool pool, int index, string name, Uri proxyUri, HttpMessageInvoker invoker)
        {
            _pool = pool;
            Index = index;
            Name = name;
            ProxyUri = proxyUri;
            Invoker = invoker;
        }

        public int Index { get; }
        public string Name { get; }
        public Uri ProxyUri { get; }
        public HttpMessageInvoker Invoker { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _pool.Release(Index);
        }
    }

    private sealed class ProxyEndpoint : IDisposable
    {
        public ProxyEndpoint(int index, Uri proxyUri, string name, string provider, string controlUrl, string containerName)
        {
            Index = index;
            ProxyUri = proxyUri;
            Name = name;
            Provider = provider;
            ControlUrl = controlUrl;
            ContainerName = containerName;
            Invoker = new HttpMessageInvoker(CreateHandler(proxyUri), disposeHandler: true);
        }

        public int Index { get; }
        public Uri ProxyUri { get; }
        public string Name { get; }
        public string Provider { get; }
        public string ControlUrl { get; }
        public string ContainerName { get; }
        public HttpMessageInvoker Invoker { get; private set; }
        public int InFlight { get; set; }
        public long TotalSelected { get; set; }
        public long Successes { get; set; }
        public long Failures { get; set; }
        public int Cooldowns { get; set; }
        public int RestartableCooldownFailures { get; set; }
        public int ConsecutiveCdnBlocks { get; set; }
        public int ConsecutiveHttpFailures { get; set; }
        public int ConsecutiveTransportFailures { get; set; }
        public DateTimeOffset CooldownUntil { get; set; }
        public DateTimeOffset LastSelectedAt { get; set; }
        public DateTimeOffset? LastContainerRestartAttempt { get; set; }
        public Task? ContainerRestartTask { get; set; }

        public void Dispose() => Invoker.Dispose();

        public HttpMessageInvoker ResetInvoker()
        {
            var old = Invoker;
            Invoker = new HttpMessageInvoker(CreateHandler(ProxyUri), disposeHandler: true);
            return old;
        }

        private static SocketsHttpHandler CreateHandler(Uri proxyUri)
            => new()
            {
                Proxy = new WebProxy(proxyUri),
                UseProxy = true,
                MaxConnectionsPerServer = 2048,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                EnableMultipleHttp2Connections = true,
                AutomaticDecompression = DecompressionMethods.All,
            };
    }
}
