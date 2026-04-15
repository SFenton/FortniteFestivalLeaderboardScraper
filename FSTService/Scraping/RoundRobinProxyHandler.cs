using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace FSTService.Scraping;

/// <summary>
/// A <see cref="DelegatingHandler"/> that round-robins HTTP requests across
/// multiple upstream proxy-backed handlers, with per-proxy CDN cooldown
/// and optional per-slot bearer token assignment.
/// <para>
/// When <see cref="ProxySlot.BearerToken"/> is set, the handler overrides
/// the request's <c>Authorization</c> header before sending — enabling
/// (proxy, token) pairing so the CDN sees each token from a consistent
/// subset of source IPs.
/// </para>
/// Thread-safe via <see cref="Interlocked"/> and lock-free cooldown checks.
/// </summary>
public sealed partial class RoundRobinProxyHandler : DelegatingHandler
{
    private readonly ProxySlot[] _slots;
    private int _counter;
    private int _activeSlotIndex; // used in active/standby mode
    private readonly bool _activeStandby;
    private readonly ILogger? _log;
    private readonly GluetunContainerRecycler? _recycler;

    /// <summary>Matches a 32-char hex account ID at the end of a URL path segment.</summary>
    [GeneratedRegex(@"(?<=/)[0-9a-f]{32}$", RegexOptions.Compiled)]
    private static partial Regex AccountIdRegex();

    /// <summary>Base cooldown duration when a proxy gets CDN-blocked.</summary>
    private static readonly TimeSpan BaseCooldown = TimeSpan.FromSeconds(30);

    /// <summary>Maximum cooldown duration after repeated blocks.</summary>
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Server tiers for VPN rotation, ordered by priority (closest to Epic CDN first).
    /// Each entry is (City, ServerName). Partitioned across slots so no server appears in
    /// more than one slot's list. 255 servers across 40 cities in 5 latency tiers.
    /// </summary>
    private static readonly (string City, string Server)[][] ServerTiers =
    [
        // Tier 0: US (lowest latency to Epic CDN) — 48 servers
        [
            ("Los Angeles", "Maia"), ("Los Angeles", "Revati"), ("Los Angeles", "Sarin"), ("Los Angeles", "Xamidimura"),
            ("Chicago Illinois", "Fang"), ("Chicago Illinois", "Kruger"), ("Chicago Illinois", "Meridiana"),
            ("Chicago Illinois", "Praecipua"), ("Chicago Illinois", "Sadalsuud"), ("Chicago Illinois", "Sneden"), ("Chicago Illinois", "Superba"),
            ("New York City", "Muliphein"), ("New York City", "Paikauhale"), ("New York City", "Sadalmelik"),
            ("New York City", "Terebellum"), ("New York City", "Unukalhai"), ("New York City", "Unurgunite"),
            ("Dallas Texas", "Chamaeleon"), ("Dallas Texas", "Equuleus"), ("Dallas Texas", "Helvetios"),
            ("Dallas Texas", "Leo"), ("Dallas Texas", "Mensa"), ("Dallas Texas", "Pegasus"),
            ("Dallas Texas", "Ran"), ("Dallas Texas", "Scutum"), ("Dallas Texas", "Volans"), ("Dallas Texas", "Vulpecula"),
            ("Miami", "Aladfar"), ("Miami", "Ascella"), ("Miami", "Chertan"),
            ("Miami", "Dziban"), ("Miami", "Elkurud"), ("Miami", "Giausar"), ("Miami", "Meleph"),
            ("Atlanta Georgia", "Hercules"), ("Atlanta Georgia", "Libra"), ("Atlanta Georgia", "Musca"),
            ("Atlanta Georgia", "Sculptor"), ("Atlanta Georgia", "Ursa"),
            ("Denver Colorado", "Sadachbia"), ("Denver Colorado", "Torcular"),
            ("Phoenix Arizona", "Guniibuu"), ("Phoenix Arizona", "Khambalia"), ("Phoenix Arizona", "Sheratan"),
            ("San Jose California", "Bunda"), ("San Jose California", "Imai"),
            ("Fremont California", "Aquila"),
            ("Raleigh North Carolina", "Polis"),
        ],
        // Tier 1: Canada — 38 servers
        [
            ("Montreal", "Lacerta"), ("Montreal", "Ross"),
            ("Toronto Ontario", "Agena"), ("Toronto Ontario", "Alhena"), ("Toronto Ontario", "Alkurhah"),
            ("Toronto Ontario", "Aludra"), ("Toronto Ontario", "Alwaid"), ("Toronto Ontario", "Alya"),
            ("Toronto Ontario", "Angetenar"), ("Toronto Ontario", "Arkab"), ("Toronto Ontario", "Avior"),
            ("Toronto Ontario", "Castula"), ("Toronto Ontario", "Cephei"), ("Toronto Ontario", "Chamukuy"),
            ("Toronto Ontario", "Chort"), ("Toronto Ontario", "Elgafar"), ("Toronto Ontario", "Enif"),
            ("Toronto Ontario", "Gorgonea"), ("Toronto Ontario", "Kornephoros"), ("Toronto Ontario", "Lesath"),
            ("Toronto Ontario", "Mintaka"), ("Toronto Ontario", "Regulus"), ("Toronto Ontario", "Rotanev"),
            ("Toronto Ontario", "Sadalbari"), ("Toronto Ontario", "Saiph"), ("Toronto Ontario", "Sargas"),
            ("Toronto Ontario", "Sharatan"), ("Toronto Ontario", "Sualocin"), ("Toronto Ontario", "Tegmen"),
            ("Toronto Ontario", "Tejat"), ("Toronto Ontario", "Tyl"), ("Toronto Ontario", "Ukdah"),
            ("Vancouver", "Ginan"), ("Vancouver", "Nahn"), ("Vancouver", "Pisces"),
            ("Vancouver", "Sham"), ("Vancouver", "Telescopium"), ("Vancouver", "Titawin"),
        ],
        // Tier 2: Western Europe (moderate latency) — 102 servers
        [
            ("London", "Amansinaya"), ("London", "Arber"), ("London", "Baiduri"),
            ("Amsterdam", "Taiyangshou"), ("Amsterdam", "Vindemiatrix"),
            ("Dublin", "Minchir"),
            ("Frankfurt", "Adhara"), ("Frankfurt", "Adhil"), ("Frankfurt", "Alsephina"),
            ("Frankfurt", "Ashlesha"), ("Frankfurt", "Cervantes"), ("Frankfurt", "Dubhe"),
            ("Frankfurt", "Errai"), ("Frankfurt", "Fuyue"), ("Frankfurt", "Menkalinan"),
            ("Frankfurt", "Mirfak"), ("Frankfurt", "Mirzam"), ("Frankfurt", "Ogma"),
            ("Brussels", "Capricornus"), ("Brussels", "Castor"), ("Brussels", "Columba"),
            ("Brussels", "Diadema"), ("Brussels", "Mebsuta"),
            ("Madrid", "Jishui"), ("Madrid", "Mekbuda"), ("Madrid", "Taurus"),
            ("Barcelona", "Eridanus"),
            ("Manchester", "Bubup"), ("Manchester", "Ceibo"), ("Manchester", "Chaophraya"),
            ("Alblasserdam", "Alchiba"), ("Alblasserdam", "Alcyone"), ("Alblasserdam", "Aljanah"),
            ("Alblasserdam", "Alphard"), ("Alblasserdam", "Alphecca"), ("Alblasserdam", "Alpheratz"),
            ("Alblasserdam", "Alphirk"), ("Alblasserdam", "Alrai"), ("Alblasserdam", "Alshat"),
            ("Alblasserdam", "Alterf"), ("Alblasserdam", "Alzirr"), ("Alblasserdam", "Ancha"),
            ("Alblasserdam", "Andromeda"), ("Alblasserdam", "Anser"), ("Alblasserdam", "Asellus"),
            ("Alblasserdam", "Aspidiske"), ("Alblasserdam", "Atik"), ("Alblasserdam", "Canis"),
            ("Alblasserdam", "Capella"), ("Alblasserdam", "Caph"), ("Alblasserdam", "Celaeno"),
            ("Alblasserdam", "Chara"), ("Alblasserdam", "Comae"), ("Alblasserdam", "Crater"),
            ("Alblasserdam", "Cygnus"), ("Alblasserdam", "Dalim"), ("Alblasserdam", "Diphda"),
            ("Alblasserdam", "Edasich"), ("Alblasserdam", "Elnath"), ("Alblasserdam", "Eltanin"),
            ("Alblasserdam", "Garnet"), ("Alblasserdam", "Gianfar"), ("Alblasserdam", "Gienah"),
            ("Alblasserdam", "Hassaleh"), ("Alblasserdam", "Horologium"), ("Alblasserdam", "Hyadum"),
            ("Alblasserdam", "Hydrus"), ("Alblasserdam", "Jabbah"), ("Alblasserdam", "Kajam"),
            ("Alblasserdam", "Kocab"), ("Alblasserdam", "Larawag"), ("Alblasserdam", "Luhman"),
            ("Alblasserdam", "Maasym"), ("Alblasserdam", "Matar"), ("Alblasserdam", "Melnick"),
            ("Alblasserdam", "Menkent"), ("Alblasserdam", "Merga"), ("Alblasserdam", "Mirach"),
            ("Alblasserdam", "Miram"), ("Alblasserdam", "Muhlifain"), ("Alblasserdam", "Muscida"),
            ("Alblasserdam", "Musica"), ("Alblasserdam", "Nash"), ("Alblasserdam", "Orion"),
            ("Alblasserdam", "Phaet"), ("Alblasserdam", "Piautos"), ("Alblasserdam", "Piscium"),
            ("Alblasserdam", "Pleione"), ("Alblasserdam", "Pyxis"), ("Alblasserdam", "Rukbat"),
            ("Alblasserdam", "Sadr"), ("Alblasserdam", "Salm"), ("Alblasserdam", "Scuti"),
            ("Alblasserdam", "Sheliak"), ("Alblasserdam", "Situla"), ("Alblasserdam", "Subra"),
            ("Alblasserdam", "Suhail"), ("Alblasserdam", "Talitha"), ("Alblasserdam", "Tarazed"),
            ("Alblasserdam", "Tiaki"), ("Alblasserdam", "Tianyi"), ("Alblasserdam", "Zibal"),
        ],
        // Tier 3: Northern/Eastern Europe — 46 servers
        [
            ("Stockholm", "Copernicus"), ("Stockholm", "Lupus"), ("Stockholm", "Norma"), ("Stockholm", "Segin"),
            ("Oslo", "Camelopardalis"), ("Oslo", "Cepheus"), ("Oslo", "Fomalhaut"), ("Oslo", "Gemini"), ("Oslo", "Ophiuchus"),
            ("Uppsala", "Albali"), ("Uppsala", "Algorab"), ("Uppsala", "Alrami"), ("Uppsala", "Alula"),
            ("Uppsala", "Atria"), ("Uppsala", "Azmidiske"), ("Uppsala", "Benetnasch"), ("Uppsala", "Menkab"), ("Uppsala", "Muphrid"),
            ("Prague", "Centaurus"), ("Prague", "Markab"), ("Prague", "Turais"),
            ("Vienna", "Alderamin"), ("Vienna", "Beemim"), ("Vienna", "Caelum"),
            ("Berlin", "Cujam"), ("Berlin", "Taiyi"),
            ("Zurich", "Achernar"), ("Zurich", "Achird"), ("Zurich", "Athebyne"), ("Zurich", "Baiten"),
            ("Zurich", "Dorado"), ("Zurich", "Hamal"), ("Zurich", "Sirrah"), ("Zurich", "Toliman"),
            ("Riga", "Felis"), ("Riga", "Meissa"), ("Riga", "Phact"), ("Riga", "Schedir"),
            ("Tallinn", "Alruba"),
            ("Sofia", "Apus"), ("Sofia", "Grus"),
            ("Belgrade", "Alnitak"), ("Belgrade", "Marsic"),
            ("Bucharest", "Alamak"), ("Bucharest", "Canes"), ("Bucharest", "Nembus"),
        ],
        // Tier 4: Rest of world (highest latency, last resort) — 21 servers
        [
            ("Sao Paulo", "Fulu"),
            ("Tokyo", "Ainalrami"), ("Tokyo", "Albaldah"), ("Tokyo", "Bharani"), ("Tokyo", "Biham"),
            ("Tokyo", "Fleed"), ("Tokyo", "Iskandar"), ("Tokyo", "Okab"), ("Tokyo", "Taphao"),
            ("Singapore", "Auriga"), ("Singapore", "Azelfafage"), ("Singapore", "Circinus"),
            ("Singapore", "Delphinus"), ("Singapore", "Hydra"), ("Singapore", "Luyten"), ("Singapore", "Triangulum"),
            ("Taipei", "Sulafat"),
            ("Auckland", "Fawaris"), ("Auckland", "Mothallah"), ("Auckland", "Theemin"), ("Auckland", "Tianguan"),
        ],
    ];

    /// <summary>
    /// Builds a prioritized server list for a proxy slot. Servers are <b>partitioned</b>
    /// across slots so no server appears in more than one slot's list. Within each tier,
    /// slot <paramref name="slotIndex"/> gets every <paramref name="slotCount"/>-th server
    /// starting at offset <paramref name="slotIndex"/>. This guarantees zero overlap —
    /// when multiple proxies get CDN-blocked simultaneously they never compete for the
    /// same server or IP.
    /// </summary>
    private static (string City, string Server)[] BuildPrioritizedServerList(int slotIndex, int slotCount)
    {
        var servers = new List<(string City, string Server)>();
        foreach (var tier in ServerTiers)
        {
            for (int i = slotIndex; i < tier.Length; i += slotCount)
                servers.Add(tier[i]);
        }
        return servers.ToArray();
    }

    /// <summary>Shared HttpClient for Gluetun control API calls. Per-call timeouts via CancellationTokenSource.</summary>
    private static readonly HttpClient ControlClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Creates a proxy rotator without token pinning (all requests use whatever
    /// Authorization header the caller set).
    /// </summary>
    public RoundRobinProxyHandler(
        IReadOnlyList<string> proxyUrls,
        Func<SocketsHttpHandler> handlerFactory,
        ILogger? logger = null,
        bool activeStandby = false)
        : this(proxyUrls, accounts: null, handlerFactory, logger,
            controlUrls: null, containerNames: null, recycler: null, activeStandby: activeStandby)
    {
    }

    /// <summary>
    /// Creates a proxy rotator with optional per-slot bearer token + account ID assignment.
    /// Accounts are assigned round-robin across slots: slot 0 → account 0,
    /// slot 1 → account 1, slot 2 → account 0, slot 3 → account 1, etc.
    /// When <paramref name="accounts"/> is null or empty, no token/URL override occurs.
    /// </summary>
    public RoundRobinProxyHandler(
        IReadOnlyList<string> proxyUrls,
        IReadOnlyList<(string Token, string AccountId)>? accounts,
        Func<SocketsHttpHandler> handlerFactory,
        ILogger? logger = null,
        IReadOnlyList<string>? controlUrls = null,
        IReadOnlyList<string>? containerNames = null,
        GluetunContainerRecycler? recycler = null,
        bool activeStandby = false)
    {
        ArgumentNullException.ThrowIfNull(proxyUrls);
        if (proxyUrls.Count == 0)
            throw new ArgumentException("At least one proxy URL is required.", nameof(proxyUrls));

        _log = logger;
        _activeStandby = activeStandby;
        _recycler = recycler;
        _slots = new ProxySlot[proxyUrls.Count];
        for (int i = 0; i < proxyUrls.Count; i++)
        {
            var handler = handlerFactory();
            handler.Proxy = new WebProxy(proxyUrls[i]);
            handler.UseProxy = true;
            string? token = accounts is { Count: > 0 }
                ? accounts[i % accounts.Count].Token
                : null;
            string? accountId = accounts is { Count: > 0 }
                ? accounts[i % accounts.Count].AccountId
                : null;
            string? controlUrl = controlUrls is { Count: > 0 }
                ? controlUrls[i % controlUrls.Count]
                : null;
            string? containerName = containerNames is { Count: > 0 }
                ? containerNames[i % containerNames.Count]
                : null;
            _slots[i] = new ProxySlot(proxyUrls[i], new HttpMessageInvoker(handler, disposeHandler: true),
                handlerFactory, BuildPrioritizedServerList(i, proxyUrls.Count), token, accountId, controlUrl, containerName);
        }

        if (accounts is { Count: > 0 })
            logger?.LogInformation("Token pinning: {AccountCount} accounts across {SlotCount} proxy slots",
                accounts.Count, proxyUrls.Count);
        if (controlUrls is { Count: > 0 })
        {
            int totalServers = ServerTiers.Sum(t => t.Length);
            logger?.LogInformation("VPN server cycling enabled: {ControlCount} control URLs, {ServerCount} servers across {TierCount} tiers (partitioned, no overlap)",
                controlUrls.Count, totalServers, ServerTiers.Length);
            if (recycler is not null && containerNames is { Count: > 0 })
                logger?.LogInformation("Container recycling enabled: {Names}",
                    string.Join(", ", containerNames));
            for (int i = 0; i < _slots.Length; i++)
            {
                var slotServers = BuildPrioritizedServerList(i, proxyUrls.Count);
                logger?.LogInformation("  Slot {Slot} servers ({Count}): {Servers}",
                    i, slotServers.Length, string.Join(", ", slotServers.Select(s => $"{s.Server}@{s.City}")));
            }
        }
        logger?.LogInformation("Proxy mode: {Mode}", activeStandby ? "active/standby (failover)" : "round-robin (load-balanced)");
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var slot = PickAvailableSlot();

        // If this slot is reconnecting its VPN, wait for it to become ready
        // rather than sending a request into a half-connected proxy.
        var reconnect = slot.ReconnectTask;
        if (reconnect is not null)
        {
            _log?.LogDebug("Waiting for proxy {Proxy} VPN reconnect...", slot.Url);
            await reconnect.WaitAsync(cancellationToken);
        }

        // Override Authorization header if this slot has a pinned token
        if (slot.BearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", slot.BearerToken);

            if (slot.AccountId is not null && request.RequestUri is not null)
            {
                var uri = request.RequestUri;
                var path = uri.AbsolutePath;
                var replaced = AccountIdRegex().Replace(path, slot.AccountId);
                if (replaced != path)
                {
                    request.RequestUri = new Uri($"{uri.Scheme}://{uri.Authority}{replaced}{uri.Query}");
                }
            }
        }

        // Link the caller's cancellation with the per-proxy in-flight CTS.
        // When a CDN block triggers VPN cycling, CancelInflight() fires and
        // all in-flight requests on this proxy fail fast instead of waiting 30s.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, slot.InflightToken);

        HttpResponseMessage response;
        try
        {
            response = await slot.Invoker.SendAsync(request, linked.Token);
        }
        catch (HttpRequestException ex) when (
            ex.Message.Contains("proxy tunnel", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("status code '503'", StringComparison.OrdinalIgnoreCase))
        {
            // Proxy returned 503 — the HTTP proxy is up but the VPN tunnel isn't
            // routing traffic (e.g. gluetun is crash-looping on a dead WireGuard server).
            // Treat like a CDN block: enter cooldown, cancel in-flight, recycle.
            _log?.LogWarning("Proxy {Proxy} tunnel failure (VPN not routing): {Error}", slot.Url, ex.Message);
            if (slot.ReconnectTask is null)
            {
                slot.EnterCooldown();
                slot.CancelInflight();
                CycleVpnServer(slot);
            }
            int available = _slots.Count(s => !s.IsInCooldown && s.ReconnectTask is null);
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            {
                ReasonPhrase = available > 0 ? "Proxy tunnel failure, cycling" : "All proxies reconnecting",
            };
        }

        // Detect proxy-up-but-VPN-down: empty text/html response means the Gluetun
        // HTTP proxy accepted the connection but the VPN tunnel isn't established yet.
        // This can happen if VPN drops unexpectedly outside of a cycle. Treat like CDN block.
        if (response.Content.Headers.ContentLength == 0 &&
            response.Content.Headers.ContentType?.MediaType == "text/html")
        {
            _log?.LogWarning("Proxy {Proxy} returned empty text/html (VPN tunnel not ready)", slot.Url);
            // Only trigger a VPN cycle if one isn't already in progress
            if (slot.ReconnectTask is null)
            {
                slot.EnterCooldown();
                slot.CancelInflight();
                CycleVpnServer(slot);
            }
            response.Dispose();
            int available = _slots.Count(s => !s.IsInCooldown && s.ReconnectTask is null);
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            {
                ReasonPhrase = available > 0 ? "Proxy VPN tunnel not ready, cycling" : "All proxies reconnecting",
            };
        }

        // Detect CDN block: 403 with non-JSON body
        if ((int)response.StatusCode == 403)
        {
            // Peek at content — need to buffer it so the caller can still read it
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            bool isCdnBlock = !body.TrimStart().StartsWith('{');

            if (isCdnBlock)
            {
                slot.EnterCooldown();
                // Cancel all in-flight requests on this proxy immediately —
                // they'll fail fast instead of waiting 30s for the dead tunnel.
                slot.CancelInflight();
                int available = _slots.Count(s => !s.IsInCooldown);
                _log?.LogWarning(
                    "Proxy {Proxy} CDN-blocked, cooldown {Cooldown:F0}s, cancelled in-flight ({Available}/{Total} proxies available)",
                    slot.Url, slot.CurrentCooldown.TotalSeconds, available, _slots.Length);

                // Cycle the VPN to a new server for a fresh egress IP
                CycleVpnServer(slot);

                // If other proxies are still available, convert the CDN 403 to a
                // retryable 503 so the executor retries on a different proxy instead
                // of triggering a full DOP slash.
                if (available > 0)
                {
                    response.Dispose();
                    return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                    {
                        ReasonPhrase = "Proxy CDN-blocked, retrying on alternate proxy",
                    };
                }
            }

            // ALL proxies blocked or JSON 403 — return the real response
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "text/html";
            response.Content.Dispose();
            response.Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType);
        }
        else if (response.IsSuccessStatusCode)
        {
            slot.ReportSuccess();
        }

        return response;
    }

    /// <summary>
    /// Pick the next proxy that isn't in cooldown. If ALL proxies are in cooldown,
    /// use the one whose cooldown expires soonest (don't deadlock).
    /// </summary>
    private ProxySlot PickAvailableSlot()
    {
        if (_activeStandby)
            return PickActiveStandbySlot();
        return PickRoundRobinSlot();
    }

    /// <summary>
    /// Active/standby: all requests go to the active proxy until it's blocked,
    /// then failover to the next healthy one. Keeps other proxies idle/fresh.
    /// </summary>
    private ProxySlot PickActiveStandbySlot()
    {
        // Try current active slot first
        var active = _slots[Volatile.Read(ref _activeSlotIndex) % _slots.Length];
        if (!active.IsInCooldown && active.ReconnectTask is null)
            return active;

        // Active is down — find the next healthy slot and promote it
        for (int i = 1; i < _slots.Length; i++)
        {
            int idx = (Volatile.Read(ref _activeSlotIndex) + i) % _slots.Length;
            var slot = _slots[idx];
            if (!slot.IsInCooldown && slot.ReconnectTask is null)
            {
                Volatile.Write(ref _activeSlotIndex, idx);
                _log?.LogInformation("Active proxy failover: {Old} → {New}",
                    active.Url, slot.Url);
                return slot;
            }
        }

        // All down — allow reconnecting slots (SendAsync will await the gate)
        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[(Volatile.Read(ref _activeSlotIndex) + i) % _slots.Length];
            if (!slot.IsInCooldown)
                return slot;
        }

        // All in cooldown — pick soonest expiry
        ProxySlot best = _slots[0];
        for (int i = 1; i < _slots.Length; i++)
        {
            if (_slots[i].CooldownUntil < best.CooldownUntil)
                best = _slots[i];
        }
        return best;
    }

    /// <summary>
    /// Round-robin: distribute requests across all proxies evenly.
    /// </summary>
    private ProxySlot PickRoundRobinSlot()
    {
        int start = (int)((uint)Interlocked.Increment(ref _counter) % (uint)_slots.Length);

        // First pass: find a slot that isn't in cooldown AND isn't reconnecting
        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[(start + i) % _slots.Length];
            if (!slot.IsInCooldown && slot.ReconnectTask is null)
                return slot;
        }

        // Second pass: allow cooldown-expired slots even if reconnecting
        // (SendAsync will await the reconnect gate)
        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[(start + i) % _slots.Length];
            if (!slot.IsInCooldown)
                return slot;
        }

        // All proxies in cooldown — pick the one expiring soonest
        ProxySlot best = _slots[0];
        for (int i = 1; i < _slots.Length; i++)
        {
            if (_slots[i].CooldownUntil < best.CooldownUntil)
                best = _slots[i];
        }
        return best;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var slot in _slots)
                slot.Invoker.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Tracks per-proxy state: cooldown, reconnect gate, in-flight CTS, and connection pool.</summary>
    private sealed class ProxySlot
    {
        public readonly string Url;
        public readonly string? BearerToken;
        public readonly string? AccountId;
        public readonly string? ControlUrl;
        public readonly string? ContainerName;
        private readonly Func<SocketsHttpHandler> _handlerFactory;
        private readonly (string City, string Server)[] _serverList;
        private HttpMessageInvoker _invoker;
        private CancellationTokenSource _inflightCts = new();
        private long _cooldownUntilTicks;
        private int _consecutiveBlocks;
        private int _serverIndex;
        private volatile SemaphoreSlim? _reconnectGate;

        public ProxySlot(string url, HttpMessageInvoker invoker, Func<SocketsHttpHandler> handlerFactory,
            (string City, string Server)[] serverList, string? bearerToken = null, string? accountId = null,
            string? controlUrl = null, string? containerName = null)
        {
            Url = url;
            _invoker = invoker;
            _handlerFactory = handlerFactory;
            _serverList = serverList;
            BearerToken = bearerToken;
            AccountId = accountId;
            ControlUrl = controlUrl;
            ContainerName = containerName;
        }

        public HttpMessageInvoker Invoker => _invoker;

        /// <summary>Token that in-flight requests should observe. Cancelled on CDN block to fast-fail.</summary>
        public CancellationToken InflightToken => _inflightCts.Token;

        /// <summary>Cancel all in-flight requests on this proxy (fast-fail instead of 30s timeout).</summary>
        public void CancelInflight()
        {
            var old = _inflightCts;
            _inflightCts = new CancellationTokenSource();
            old.Cancel();
            old.Dispose();
        }

        /// <summary>Reset connection pool by replacing the SocketsHttpHandler + Invoker.</summary>
        public void ResetConnectionPool()
        {
            var handler = _handlerFactory();
            handler.Proxy = new WebProxy(Url);
            handler.UseProxy = true;
            var old = _invoker;
            _invoker = new HttpMessageInvoker(handler, disposeHandler: true);
            old.Dispose();
        }

        /// <summary>
        /// A semaphore that callers await when this proxy is reconnecting its VPN.
        /// Null when the proxy is healthy. Set to a locked semaphore when cycling starts,
        /// released when the VPN is verified live.
        /// </summary>
        public SemaphoreSlim? ReconnectTask => _reconnectGate;

        /// <summary>
        /// Atomically try to begin a reconnect cycle.
        /// Returns the gate if this caller won the race, null if another caller already started cycling.
        /// </summary>
        public SemaphoreSlim? TryBeginReconnect()
        {
            var gate = new SemaphoreSlim(0, 1);
            var existing = Interlocked.CompareExchange(ref _reconnectGate, gate, null);
            if (existing is not null)
            {
                gate.Dispose();
                return null; // another caller already cycling
            }
            return gate;
        }

        /// <summary>Signal that the reconnect is complete — releases all waiters.</summary>
        public void EndReconnect()
        {
            var gate = _reconnectGate;
            _reconnectGate = null;
            gate?.Release();
        }

        public bool IsInCooldown =>
            Volatile.Read(ref _cooldownUntilTicks) > DateTimeOffset.UtcNow.Ticks;

        public DateTimeOffset CooldownUntil =>
            new(Volatile.Read(ref _cooldownUntilTicks), TimeSpan.Zero);

        public TimeSpan CurrentCooldown
        {
            get
            {
                int blocks = Volatile.Read(ref _consecutiveBlocks);
                // Exponential backoff: 30s, 60s, 120s (capped)
                var seconds = BaseCooldown.TotalSeconds * Math.Pow(2, Math.Min(blocks - 1, 2));
                return TimeSpan.FromSeconds(Math.Min(seconds, MaxCooldown.TotalSeconds));
            }
        }

        public void EnterCooldown()
        {
            int blocks = Interlocked.Increment(ref _consecutiveBlocks);
            var seconds = BaseCooldown.TotalSeconds * Math.Pow(2, Math.Min(blocks - 1, 2));
            var cooldown = TimeSpan.FromSeconds(Math.Min(seconds, MaxCooldown.TotalSeconds));
            Interlocked.Exchange(ref _cooldownUntilTicks,
                (DateTimeOffset.UtcNow + cooldown).Ticks);
        }

        public void ReportSuccess()
        {
            Volatile.Write(ref _consecutiveBlocks, 0);
            Volatile.Write(ref _cooldownUntilTicks, 0L);
        }

        /// <summary>Returns the next server from this slot's prioritized list (round-robin).</summary>
        public (string City, string Server) NextServer()
        {
            int idx = Interlocked.Increment(ref _serverIndex);
            return _serverList[(uint)idx % (uint)_serverList.Length];
        }

        /// <summary>Extend cooldown to a specific time (used when VPN is reconnecting).</summary>
        public void ExtendCooldownUntil(DateTimeOffset until)
        {
            long current = Volatile.Read(ref _cooldownUntilTicks);
            if (until.Ticks > current)
                Interlocked.Exchange(ref _cooldownUntilTicks, until.Ticks);
        }
    }

    /// <summary>Maximum number of server attempts per cycle before giving up.</summary>
    private const int MaxServerAttempts = 8;

    /// <summary>Interval between VPN health polls.</summary>
    private static readonly TimeSpan VpnPollInterval = TimeSpan.FromSeconds(3);

    /// <summary>Timeout for a single control API command (just sending, not waiting for VPN).</summary>
    private static readonly TimeSpan ControlCommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Timeout for individual health check poll requests.</summary>
    private static readonly TimeSpan HealthPollTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Maximum total wall time for the entire VPN cycle operation.</summary>
    private static readonly TimeSpan MaxCycleDuration = TimeSpan.FromMinutes(3);

    /// <summary>Settle delay between failed server attempts to let Gluetun finish teardown.</summary>
    private static readonly TimeSpan ServerSettleDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Checks whether the Gluetun control API is reachable.
    /// Returns true if the API responds, false otherwise.
    /// </summary>
    private static async Task<bool> IsControlApiReachableAsync(string controlUrl)
    {
        try
        {
            using var cts = new CancellationTokenSource(ControlCommandTimeout);
            var res = await ControlClient.GetStringAsync($"{controlUrl}/v1/vpn/status", cts.Token);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Checks whether the VPN is connected and actually routing traffic.
    /// Verifies both that gluetun reports "running" and that a public IP is available
    /// (proves DNS resolution and tunnel connectivity work, not just WireGuard handshake).
    /// </summary>
    private static async Task<bool> CheckVpnLiveAsync(string controlUrl)
    {
        try
        {
            using var cts = new CancellationTokenSource(HealthPollTimeout);

            // Check 1: status must be "running"
            var statusRes = await ControlClient.GetStringAsync($"{controlUrl}/v1/vpn/status", cts.Token);
            if (!statusRes.Contains("\"running\""))
                return false;

            // Check 2: public IP must be resolvable (proves the tunnel is actually routing)
            var ipRes = await ControlClient.GetStringAsync($"{controlUrl}/v1/publicip/ip", cts.Token);
            return ipRes.Contains("\"public_ip\"") && !ipRes.Contains("\"public_ip\":\"\"");
        }
        catch { return false; }
    }

    /// <summary>
    /// Sends a server change command to the Gluetun control API.
    /// Sets both the city and server name for precise server selection.
    /// Returns true if the command was accepted, false if the API is unreachable.
    /// </summary>
    private async Task<bool> SendServerChangeAsync(ProxySlot slot, string city, string serverName)
    {
        try
        {
            using var cts = new CancellationTokenSource(ControlCommandTimeout);
            var json = $"{{\"provider\":{{\"server_selection\":{{\"cities\":[\"{city}\"],\"names\":[\"{serverName}\"]}}}}}}";
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var res = await ControlClient.PutAsync($"{slot.ControlUrl}/v1/vpn/settings", content, cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log?.LogWarning("Proxy {Proxy} server command to {Server}@{City} unreachable: {Error}",
                slot.Url, serverName, city, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Polls until the VPN is live (running status), or the deadline expires.
    /// </summary>
    private async Task<bool> WaitForVpnLiveAsync(ProxySlot slot, DateTimeOffset deadline)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(VpnPollInterval);
            if (await CheckVpnLiveAsync(slot.ControlUrl!))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Cycle the Gluetun VPN to a new server, wait for verified connectivity,
    /// then release the reconnect gate. Tries up to <see cref="MaxServerAttempts"/> servers.
    /// <para>
    /// When a <see cref="GluetunContainerRecycler"/> and container name are available,
    /// the container is fully recreated (stop/rm/run) via the Docker Engine API.
    /// This is more reliable than the gluetun control API when WireGuard is wedged
    /// or gluetun is crash-looping. Falls back to the control API if container
    /// recycling is not configured.
    /// </para>
    /// </summary>
    private void CycleVpnServer(ProxySlot slot)
    {
        if (slot.ControlUrl is null) return;

        // Atomically claim the cycling right — only one task per proxy
        var gate = slot.TryBeginReconnect();
        if (gate is null) return; // another caller already cycling

        bool useRecycler = _recycler is not null && slot.ContainerName is not null;

        _ = Task.Run(async () =>
        {
            var operationDeadline = DateTimeOffset.UtcNow + MaxCycleDuration;
            try
            {
                // When not using container recycling, check if the control API is reachable first
                if (!useRecycler && !await IsControlApiReachableAsync(slot.ControlUrl))
                {
                    _log?.LogError("Proxy {Proxy} control API unreachable at {Url}, cannot cycle",
                        slot.Url, slot.ControlUrl);
                    return;
                }

                for (int attempt = 0; attempt < MaxServerAttempts && DateTimeOffset.UtcNow < operationDeadline; attempt++)
                {
                    // Settle delay between attempts — let Gluetun finish prior teardown
                    if (attempt > 0)
                        await Task.Delay(ServerSettleDelay);

                    var (city, server) = slot.NextServer();
                    _log?.LogInformation("Cycling proxy {Proxy} VPN to {Server}@{City} via {Method} (attempt {Attempt}/{Max})",
                        slot.Url, server, city, useRecycler ? "container recycle" : "control API",
                        attempt + 1, MaxServerAttempts);

                    // Phase 1: Change the server — either recreate the container or use the control API
                    bool commandOk;
                    if (useRecycler)
                    {
                        commandOk = await _recycler!.RecycleAsync(slot.ContainerName!, city, server);
                    }
                    else
                    {
                        commandOk = await SendServerChangeAsync(slot, city, server);
                    }

                    if (!commandOk)
                    {
                        _log?.LogWarning("Proxy {Proxy} server command rejected for {Server}@{City}", slot.Url, server, city);
                        continue;
                    }

                    // Phase 2: Keep cooldown extended while VPN reconnects.
                    // Container recreation takes longer than control API — allow 60s.
                    int perAttemptSeconds = useRecycler ? 60 : 45;
                    var perServerDeadline = DateTimeOffset.UtcNow.AddSeconds(perAttemptSeconds);
                    if (perServerDeadline > operationDeadline)
                        perServerDeadline = operationDeadline;
                    slot.ExtendCooldownUntil(perServerDeadline);

                    // Phase 3: Poll until VPN is verified live (status=running)
                    if (await WaitForVpnLiveAsync(slot, perServerDeadline))
                    {
                        _log?.LogInformation("Proxy {Proxy} VPN live on {Server}@{City}", slot.Url, server, city);
                        // Reset the connection pool so stale connections to the old
                        // tunnel are dropped and fresh connections use the new one.
                        slot.ResetConnectionPool();
                        slot.ReportSuccess();
                        return; // finally releases gate
                    }

                    _log?.LogWarning("Proxy {Proxy} VPN did not come up on {Server}@{City}, trying next",
                        slot.Url, server, city);
                }

                _log?.LogError("Proxy {Proxy} exhausted {Max} server attempts, leaving in cooldown",
                    slot.Url, MaxServerAttempts);
            }
            finally
            {
                slot.EndReconnect();
            }
        });
    }
}
