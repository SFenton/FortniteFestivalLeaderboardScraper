# Proxy Rotation on CDN Block — Design Document

**Date:** April 8, 2026
**Status:** Proposed

## Problem Statement

CDN blocks from Epic's API are 403 responses with HTML bodies (not JSON). The current `ResilientHttpExecutor` detects these and runs a probe loop on the **same IP** with escalating backoff (500ms → 1s → 2s → 5s → 10s → 15s → 30s → 45s → 60s, up to 30 retries ≈ 7 minutes). If all retries are exhausted, a `CdnBlockedException` escapes to the scrape pass level, the pass is aborted, and the next attempt is 4 hours later.

**Key insight:** CDN blocks are IP-based. If we can change our exit IP, we can escape the block immediately.

---

## Current CDN Block Handling

### Detection

In `ResilientHttpExecutor.SendAsync()`:

```csharp
if (statusCode == 403)
{
    var body = await res.Content.ReadAsStringAsync(ct);
    bool isCdnBlock = !body.TrimStart().StartsWith('{');  // non-JSON = CDN
    if (isCdnBlock) {
        // Increment metrics & launch probe
        throw new CdnBlockedException(...);
    }
    // Otherwise: JSON 403 → return to caller (API error like no_score_found)
}
```

### Probe Mechanism

- `LaunchCdnProbe()` starts a background task protected by `_cdnGate` semaphore (only one probe at a time)
- Probe walks a fixed backoff schedule: 500ms, 1s, 2s, 5s, 10s, 15s, 30s, 45s, 60s (then 60s indefinitely)
- All other requests on the same executor wait on `_cdnResolved` (a `TaskCompletionSource`)
- On success: signals `_cdnResolved`, clears CDN state, all waiting requests resume
- On exhaustion: signals failure, `CdnBlockedException` propagates up

### Concurrency Impact

- `AdaptiveConcurrencyLimiter.SlashDop()` is called on CDN block — cuts DOP to `minDop`, sets `ssthresh = oldDop / 2`
- Recovery uses TCP slow-start: multiplicative ×1.333 below ssthresh, additive +16 above
- DOP slot is released during probe wait, reacquired for probe sends

### Metrics

- `CdnBlocksDetected` — number of 403 non-JSON responses
- `CdnProbeAttempts` — probe HTTP sends during retry
- `CdnProbeSuccesses` — successful probes (CDN cleared)
- `TotalHttpSends` — all HTTP sends including probes/retries

---

## Proposed Solution: Proxy Rotation via Gluetun + AirVPN

### Architecture

Add configurable HTTP proxy rotation to the scraper. When a CDN 403 block is detected, the system rotates to a different exit IP via proxy instead of waiting on the same blocked IP.

**Proxy infrastructure:** [Gluetun](https://github.com/qdm12/gluetun) Docker sidecar containers, each tunneling via WireGuard to a different AirVPN server and exposing a built-in HTTP proxy on port 8888.

### Why Gluetun + AirVPN (Not Commercial SOCKS5)

| Factor | Gluetun + AirVPN | Commercial SOCKS5 |
|---|---|---|
| **Monthly cost** | $0 (already subscribed) | $2–50/mo |
| **Bandwidth** | Unlimited | May throttle at ~60 GB/day |
| **Setup** | ~30 min one-time | 5 min |
| **Containers** | 3–4 extra (~43 MB each) | None |
| **Control** | Full (regions, health, stealth) | Provider-dependent |

The scraper pulls ~10 GB per scrape cycle × 6 cycles/day = ~60 GB/day. Per-GB proxy pricing (Bright Data at $8–15/GB, Oxylabs, Smartproxy) is prohibitively expensive. Unlimited-bandwidth providers like PIA standalone SOCKS5 ($2/mo) or NordVPN ($4/mo) are viable but may throttle at this volume. AirVPN via gluetun costs nothing additional and handles the bandwidth.

### Why HTTP Proxy (Not `network_mode`)

Gluetun supports two connection modes:

**❌ `network_mode: "service:gluetun"`** — ALL of FSTService's traffic routes through the VPN. Breaks PostgreSQL queries, API responses, health checks. Only one gluetun instance usable at a time.

**✅ HTTP proxy via `HTTPPROXY=on`** — Only specific HTTP requests (scraper calls to Epic's API) are routed through gluetun's proxy. PostgreSQL, API serving, health checks, and all other containers are completely unaffected. Multiple gluetun instances can be used simultaneously or rotated between.

### Traffic Isolation

| Traffic | Path |
|---|---|
| FSTService → PostgreSQL | Direct Docker network (unchanged) |
| FSTService → clients (API responses) | Direct port 8080 (unchanged) |
| FSTService → Epic API (normal) | Direct internet (no proxy) |
| FSTService → Epic API (CDN blocked) | Through `gluetun-{region}:8888` → VPN → internet |
| PostgreSQL, festivalweb, etc. | Completely unaware of gluetun |

---

## Proxy Pool Design

### Pool Composition

AirVPN allows 5 simultaneous connections per standard plan. The pool:

| Slot | Connection | Exit IP |
|---|---|---|
| 0 | Direct (no proxy) | Server's real IP |
| 1 | gluetun-us | AirVPN US server |
| 2 | gluetun-eu | AirVPN Netherlands |
| 3 | gluetun-asia | AirVPN Singapore/Japan |
| 4 | gluetun-us2 | AirVPN different US server |

**Recommendation: 3–4 gluetun instances** (different regions) + direct = 4–5 exit IPs. Geographic diversity matters more than count — same-region AirVPN servers may share exit IPs or subnets.

### Rotation Strategy

**Reactive only** — rotate on CDN block, not preemptively.

```
Scraping on Direct (#0) → CDN 403
  → rotate to gluetun-us (#1), quick probe
  → success → continue scraping on gluetun-us
  ...
  CDN 403 on gluetun-us (#1)
  → rotate to gluetun-eu (#2), quick probe
  → success → continue on gluetun-eu
  ...
  CDN 403 on gluetun-eu (#2)
  → rotate to gluetun-asia (#3), quick probe
  ...
  CDN 403 on ALL proxies → ALL EXHAUSTED
  → fall back to timed backoff on oldest-blocked proxy (#0)
```

**Two phases:**

1. **Fast rotation phase:** Try each proxy with a quick probe (~2–5 seconds per rotation). Cycle through all N proxies.
2. **Timed backoff phase:** If all N are exhausted, fall back to the existing backoff schedule (500ms → 60s) on the **oldest-blocked proxy** (most recovery time elapsed).

### Per-Proxy Cooldown Tracking

`ProxyRotator` tracks `lastBlockedAt` per proxy (`DateTimeOffset[]`):

- `RotateNext()` skips proxies blocked within the cooldown window (e.g., < 5 minutes ago)
- Only probes proxies that have had time to recover
- Avoids wasting time probing proxies blocked 30 seconds ago
- On the next CDN block (minutes later), the system already knows which proxies are likely still blocked

### DOP Reset on Rotation

When `AdaptiveConcurrencyLimiter.SlashDop()` fires on CDN block, DOP drops to `minDop`. After rotating to a fresh (unblocked) proxy, the DOP would be stuck at a low value — defeating the purpose of fast recovery.

**Solution:** Add `ResetForProxyRotation(int targetDop)` to `AdaptiveConcurrencyLimiter`:
- Restores DOP to `targetDop` (adds semaphore tokens, clears release debt)
- Clears `ssthresh` (no slow-start — fresh proxy shouldn't be penalized)
- Resets evaluation window (clean slate for AIMD)

**Shared limiter (not per-proxy):** A single `AdaptiveConcurrencyLimiter` across all proxies, with DOP reset on successful rotation. Simpler than per-proxy limiters, and the key benefit (escaping blocks by switching IP) is achieved either way. Can upgrade to per-proxy limiters later if needed.

---

## Rate Limiting Analysis

### Shared vs. Per-Proxy Limiter

**Option 1: Shared limiter with DOP reset** (recommended)
- Single DOP budget across all proxies
- `ResetForProxyRotation()` restores DOP after switching to unblocked proxy
- Global req/s cap stays enforced — won't hammer Epic harder with more IPs
- Simpler to implement; can upgrade later

**Option 2: Per-proxy limiter** (more robust, more complex)
- Each proxy gets its own `AdaptiveConcurrencyLimiter`
- CDN `SlashDop()` on proxy A doesn't affect proxy B's DOP
- Requires separating the rate limiter from `AdaptiveConcurrencyLimiter` (or wrapper)
- Total concurrent requests = sum of all proxies' DOPs

**Decision:** Option 1 — shared limiter with DOP reset.

---

## Implementation Plan

### Phase 1: Configuration

Add to `ScraperOptions`:

```csharp
/// <summary>
/// List of proxy URIs for CDN block rotation. The system starts with direct
/// (no proxy) and rotates through these on CDN block detection.
/// Format: http://host:port or socks5://user:pass@host:port
/// Set via Scraper__ProxyUrls__0, Scraper__ProxyUrls__1, etc.
/// </summary>
public List<string> ProxyUrls { get; set; } = [];

/// <summary>
/// When true, rotate to the next proxy on CDN block instead of probing
/// the same IP. Requires ProxyUrls to be configured. Default: true.
/// </summary>
public bool RotateOnCdnBlock { get; set; } = true;
```

### Phase 2: ProxyRotator Service

New file `FSTService/Scraping/ProxyRotator.cs`:

- Circular pool of `HttpClient` instances (one per proxy URI + one for direct/null)
- `CurrentClient` — active HttpClient
- `CurrentLabel` — human-readable label for logging ("direct", "gluetun-us", etc.)
- `RotateNext()` — advance to next proxy, skip recently-blocked proxies (cooldown tracking)
- `MarkBlocked(int index)` — record block timestamp for a proxy
- `GetOldestBlocked()` — return the proxy with the most recovery time
- `Count` — total available proxies (including direct)
- Thread-safe via `Interlocked`; `IDisposable` for cleanup
- Each `HttpClient` created with `SocketsHttpHandler { Proxy = new WebProxy(uri) }` copying existing handler config (timeouts, decompression, connection limits)

Register as singleton in DI.

### Phase 3: AdaptiveConcurrencyLimiter DOP Reset

Add to `AdaptiveConcurrencyLimiter`:

```csharp
/// <summary>
/// Reset DOP after rotating to a fresh proxy. Restores concurrency to the
/// target level, clears slow-start threshold, and resets the evaluation window.
/// Called after a successful CDN probe on a new proxy.
/// </summary>
public void ResetForProxyRotation(int targetDop)
```

### Phase 4: ResilientHttpExecutor Integration

- Add optional `ProxyRotator?` to constructor
- Modify `SendAsync()`: use `_rotator?.CurrentClient ?? _http` for sends
- Modify `LaunchCdnProbe()`:
  1. If `ProxyRotator` available + `RotateOnCdnBlock`:
     - Call `_rotator.MarkBlocked(currentIndex)`
     - Call `_rotator.RotateNext()` — skips recently-blocked proxies
     - Quick-probe using `_rotator.CurrentClient`
     - If success: swap `_http`, call `limiter.ResetForProxyRotation(initialDop)`, signal `_cdnResolved`
     - If still blocked: rotate to next, repeat
     - If all proxies exhausted: fall back to timed backoff on `_rotator.GetOldestBlocked()`
  2. Log: `"CDN block: rotating to {Label} ({Index}/{Count})"`

### Phase 5: Wire into Scraper Classes

Pass `ProxyRotator` through constructors → into `ResilientHttpExecutor`:
- `GlobalLeaderboardScraper`
- `AccountNameResolver`
- `HistoryReconstructor`

Update `Program.cs` DI to resolve and inject `ProxyRotator`.

### Phase 6: Docker / AirVPN Configuration

Add gluetun sidecar service definitions to `deploy/docker-compose.yml`:

```yaml
gluetun-us:
  image: qmcgaw/gluetun
  container_name: gluetun-us
  restart: unless-stopped
  cap_add:
    - NET_ADMIN
  devices:
    - /dev/net/tun:/dev/net/tun
  environment:
    - VPN_SERVICE_PROVIDER=airvpn
    - VPN_TYPE=wireguard
    - SERVER_COUNTRIES=United States
    - WIREGUARD_PRIVATE_KEY=${AIRVPN_WG_PRIVATE_KEY}
    - WIREGUARD_PRESHARED_KEY=${AIRVPN_WG_PRESHARED_KEY}
    - WIREGUARD_ADDRESSES=${AIRVPN_WG_ADDRESSES}
    - HTTPPROXY=on
    - HTTPPROXY_LISTENING_ADDRESS=:8888
    - HTTPPROXY_STEALTH=on

gluetun-eu:
  image: qmcgaw/gluetun
  container_name: gluetun-eu
  # ... same structure, SERVER_COUNTRIES=Netherlands

gluetun-asia:
  image: qmcgaw/gluetun
  container_name: gluetun-asia
  # ... same structure, SERVER_COUNTRIES=Japan
```

FSTService env vars:
```yaml
- Scraper__ProxyUrls__0=http://gluetun-us:8888
- Scraper__ProxyUrls__1=http://gluetun-eu:8888
- Scraper__ProxyUrls__2=http://gluetun-asia:8888
```

### AirVPN Setup (One-Time)

1. Log in to AirVPN → Config Generator → select WireGuard
2. Generate configs for 3–4 different server regions
3. Extract from each config: `WIREGUARD_PRIVATE_KEY`, `WIREGUARD_PRESHARED_KEY`, `WIREGUARD_ADDRESSES`
4. Same keys can work for all gluetun instances — gluetun picks the server per `SERVER_COUNTRIES`
5. Add keys to `.env` on the Docker host (not committed to repo)

---

## Files Changed

| File | Change |
|---|---|
| `FSTService/ScraperOptions.cs` | Add `ProxyUrls`, `RotateOnCdnBlock` |
| `FSTService/Scraping/ProxyRotator.cs` | **New file** |
| `FortniteFestival.Core/Scraping/AdaptiveConcurrencyLimiter.cs` | Add `ResetForProxyRotation()` |
| `FSTService/Scraping/ResilientHttpExecutor.cs` | Accept `ProxyRotator`, modify probe + send |
| `FSTService/Scraping/GlobalLeaderboardScraper.cs` | Pass `ProxyRotator` to executor |
| `FSTService/Scraping/AccountNameResolver.cs` | Pass `ProxyRotator` to executor |
| `FSTService/Scraping/HistoryReconstructor.cs` | Pass `ProxyRotator` to executor |
| `FSTService/Program.cs` | Register `ProxyRotator`, inject into scrapers |
| `deploy/docker-compose.yml` | Gluetun sidecar definitions + proxy env vars |
| `docker-compose.yml` | Proxy env vars for local dev |

---

## Verification

1. **Unit test `ProxyRotator`** — rotation wrapping, client creation, cooldown tracking, label generation, disposal
2. **Unit test `ResetForProxyRotation()`** — DOP restored, ssthresh cleared, eval window reset
3. **Unit test `ResilientHttpExecutor`** — rotation on CDN block, client swap on probe success, fallback to timed backoff after exhausting pool, per-proxy cooldown respected
4. **Manual test** — configure gluetun sidecar with AirVPN, trigger CDN block, observe rotation + recovery

---

## Impact Summary

**Today:** CDN block → wait up to 7 min on same IP → might exhaust retries → scrape pass fails, retry in 4 hours.

**With proxy rotation:** CDN block → try 3–4 other IPs in ~10–20 seconds → likely one works → continue scraping immediately. If all blocked, fall back to timed backoff on oldest-blocked IP (which has had the most recovery time).

---

## Design Decisions

| Decision | Rationale |
|---|---|
| Gluetun HTTP proxy, not `network_mode` | Only scraper traffic routes through VPN; all other containers unaffected |
| AirVPN via gluetun sidecars, not commercial SOCKS5 | Already subscribed, unlimited bandwidth, $0/mo ongoing |
| Shared limiter with DOP reset, not per-proxy | Simpler, key benefit achieved either way, upgradeable |
| Reactive rotation only, not preemptive | User preference; avoids unnecessary proxy usage |
| Direct connection stays in pool | Can rotate back when block expires |
| One shared `ProxyRotator` across all scrapers | CDN blocks are IP-based; rotation benefits all |
| Per-proxy cooldown tracking | Avoids re-probing recently blocked proxies |
| 3–4 gluetun instances (diverse regions) | Enough rotation depth without exhausting AirVPN connection limit (5) |
| `HTTPPROXY_STEALTH=on` | Don't add X-Forwarded-For headers that reveal proxy usage |
