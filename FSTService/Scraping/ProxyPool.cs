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

internal static class ProxyRequestState
{
    public static readonly HttpRequestOptionsKey<int> EndpointIndex = new("FSTService.ProxyEndpointIndex");
    public static readonly HttpRequestOptionsKey<string> EndpointName = new("FSTService.ProxyEndpointName");
}

internal sealed class ProxyPool : IProxyHealthReporter, IDisposable
{
    private readonly List<ProxyEndpoint> _endpoints;
    private readonly ILogger<ProxyPool> _log;
    private readonly bool _activeStandby;
    private readonly TimeSpan _activeRotationInterval;
    private readonly TimeSpan _baseCooldown;
    private readonly int _timeoutFailureThreshold;
    private readonly int _httpFailureThreshold;
    private readonly object _lock = new();
    private int _activeIndex;
    private int _nextRoundRobinIndex;
    private DateTimeOffset _activeSince = DateTimeOffset.UtcNow;
    private bool _disposed;

    public ProxyPool(IOptions<ScraperOptions> options, ILogger<ProxyPool> log)
        : this(options.Value, log)
    {
    }

    internal ProxyPool(ScraperOptions options, ILogger<ProxyPool> log)
    {
        _log = log;
        _activeStandby = options.ProxyActiveStandby;
        _activeRotationInterval = options.ProxyActiveRotationSeconds > 0
            ? TimeSpan.FromSeconds(options.ProxyActiveRotationSeconds)
            : TimeSpan.Zero;
        _baseCooldown = TimeSpan.FromSeconds(Math.Max(1, options.ProxyCooldownSeconds));
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
            endpoint.Successes++;
        }
    }

    public void ReportFailure(HttpRequestMessage request, ProxyFailureKind kind)
    {
        if (!TryGetEndpointIndex(request, out var index))
            return;

        ReportFailure(index, kind);
    }

    internal void ReportFailure(int index, ProxyFailureKind kind)
    {
        lock (_lock)
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

            if (_activeStandby && endpoint.Index == _activeIndex)
                RotateActive(DateTimeOffset.UtcNow, $"proxy {endpoint.Name} reported {kind}");
        }
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
                controlUrl: controlUrl);
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
        public ProxyEndpoint(int index, Uri proxyUri, string name, string provider, string controlUrl)
        {
            Index = index;
            ProxyUri = proxyUri;
            Name = name;
            Provider = provider;
            ControlUrl = controlUrl;
            Invoker = new HttpMessageInvoker(CreateHandler(proxyUri), disposeHandler: true);
        }

        public int Index { get; }
        public Uri ProxyUri { get; }
        public string Name { get; }
        public string Provider { get; }
        public string ControlUrl { get; }
        public HttpMessageInvoker Invoker { get; }
        public int InFlight { get; set; }
        public long TotalSelected { get; set; }
        public long Successes { get; set; }
        public long Failures { get; set; }
        public int Cooldowns { get; set; }
        public int ConsecutiveCdnBlocks { get; set; }
        public int ConsecutiveHttpFailures { get; set; }
        public int ConsecutiveTransportFailures { get; set; }
        public DateTimeOffset CooldownUntil { get; set; }
        public DateTimeOffset LastSelectedAt { get; set; }

        public void Dispose() => Invoker.Dispose();

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
