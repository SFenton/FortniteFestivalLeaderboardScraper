---
status: canonical
owner: operations
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - FSTService/Program.cs
  - FSTService/ScraperOptions.cs
  - FSTService/Scraping/ProxyPool.cs
  - FSTService/Scraping/ResilientHttpExecutor.cs
  - FSTService/Scraping/GluetunContainerRecycler.cs
  - deploy/docker-compose.yml
  - tools/fst-worker-compose-guard.sh
  - /home/sfenton/Docker/FestivalServiceTracker/docker-compose.pia-30.yml
update_triggers:
  - Proxy selection, pacing, transport, Gluetun services, provider overlays, self-heal, or guard contracts change.
---

# VPN proxy pool

## Why it exists

High-volume Epic leaderboard/history collection can encounter exit-IP-scoped
CDN blocks, rate limits, and unhealthy tunnels. A single exit can therefore
stall a scrape even when the request and credentials are valid.

FST uses independent Gluetun containers as HTTP proxy endpoints so it can:

- distribute requests across exit IPs;
- cool down only the affected exit after a CDN block;
- retry on another healthy endpoint;
- pace and bound concurrency per endpoint;
- restart a broken tunnel without restarting the worker;
- keep PostgreSQL, API serving, browser traffic, and unrelated HTTP clients off
  the VPN path.

This is traffic isolation and resilience, not anonymity for the whole stack.

## Request path

```mermaid
flowchart LR
    EpicClient[Leaderboard/history HTTP client]
      --> Handler[ProxyRoutingHttpMessageHandler or curl transport]
      --> Pool[ProxyPool lease]
      --> Gluetun[Gluetun HTTP proxy :8888]
      --> Epic[Epic API/CDN]
    Pool --> Control[Gluetun control :8000]
    Worker[Worker-only Docker control] -. tunnel restart .-> Gluetun
```

`Program.cs` installs proxy routing only for proxy-aware Epic clients. Auth,
database, web, health, and general service traffic use normal networking.

## Endpoint contract

The worker receives four index-aligned arrays:

- `Scraper:ProxyUrls`
- `Scraper:ControlUrls`
- `Scraper:VpnProviders`
- `Scraper:ContainerNames`

When `ExpectedProxyEndpointCount` is nonzero, each array must contain exactly
that many non-empty entries. Proxy and control hostnames must match the aligned
container and expected internal ports. Container names must be unique.

No direct endpoint is appended to a configured pool. An empty pool uses the
normal direct HTTP handler.

## Selection and failure handling

The pool supports active/standby rotation or least-in-flight selection.
Per-endpoint request starts, concurrency, connection reuse, cooldown, and
rotation are configurable.

On a CDN block:

1. mark and cool the endpoint immediately;
2. retry through another available proxy;
3. if every proxy is cooling down, wait for pool recovery;
4. pause globally only when the request was not associated with a known proxy.

Transport, timeout, rate-limit, and server failures use separate thresholds.
Curl can be the primary proxied transport so production behavior matches proxy
qualification canaries. Same-drive scratch is required for curl bodies.

## Self-heal boundary

Repeated tunnel-level transport failures can restart the aligned container.
CDN blocks alone cool/fail over; they do not prove a tunnel is broken.

Only `fstworker` receives `/var/run/docker.sock`. API/frontend roles use
`DisabledProxyContainerRecycler`, which rejects restart requests. The recycler
normally restarts a container without rewriting provider selectors; legacy
recreate/city-selection support exists for provider-specific workflows.

## Compose layouts

| Layer | Purpose |
|---|---|
| Root template | Four core services; proxy examples inactive |
| `deploy/` template | Four optional AirVPN Gluetun endpoints |
| Production base | Core services plus a larger provider pool |
| Standard PIA overlay | 30 canonical services, currently 25 effective aligned endpoints |
| Optional expansion overlays | Additional endpoints/recovery variants owned by the production project |

The PIA guard requires the overlay filename `docker-compose.pia-30.yml`,
canonical count 30, effective count no greater than 30, exact service names,
aligned arrays, PIA provider labels, and matching worker dependencies. The
optional 80-endpoint expansion is a separate production-owned topology and is
not the standard guard target.

## Safety and secrecy

- Never commit VPN credentials, provider account data, private endpoints, or
  resolved `.env` values.
- Do not route the entire worker/container through one VPN network namespace;
  the design depends on per-request endpoint selection.
- Do not give the API service Docker control.
- Validate effective Compose configuration and key names without printing
  values.
- Treat configured service counts separately from observed running-container
  state.
