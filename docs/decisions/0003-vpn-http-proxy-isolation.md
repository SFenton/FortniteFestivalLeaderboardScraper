---
status: decision
owner: operations
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - FSTService/Program.cs
  - FSTService/Scraping/ProxyPool.cs
  - FSTService/Scraping/ResilientHttpExecutor.cs
  - deploy/docker-compose.yml
update_triggers:
  - VPN routing, proxy transport, provider, or network-namespace strategy changes.
---

# ADR 0003: Isolate VPN use to HTTP proxy endpoints

## Decision

Expose each Gluetun tunnel as an HTTP proxy and select an endpoint per Epic
leaderboard/history request. Do not place the entire worker or stack inside one
VPN network namespace.

## Rationale

- Multiple independent exits can be selected, cooled, and recovered.
- CDN blocks are isolated to one endpoint instead of stopping all traffic.
- PostgreSQL, API, health, authentication, and browser traffic keep normal
  routing and predictable latency.
- Worker-only Docker control can repair tunnels without granting the public API
  host control.

## Consequences

- Proxy, control, provider, and container arrays must stay index-aligned.
- Per-endpoint pacing and concurrency are first-class configuration.
- Production provider overlays remain separate from repository templates and
  must not leak credentials into source control.
