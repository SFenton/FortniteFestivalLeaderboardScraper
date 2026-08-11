---
status: canonical
owner: operations
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - docker-compose.yml
  - deploy/docker-compose.yml
  - deploy/fst-compose.sh
  - FSTService/Dockerfile
  - FortniteFestivalWeb/Dockerfile
  - FortniteFestivalWeb/nginx.conf
  - /home/sfenton/Docker/FestivalServiceTracker/docker-compose.yml
update_triggers:
  - Compose services, images, roles, volumes, ports, networks, health checks, or production ownership change.
---

# Deployment topology

## Ownership

Repository Compose files are templates. The live project is owned from:

```text
/home/sfenton/Docker/FestivalServiceTracker
```

Do not run a repository template as a competing project with the same container
names. `deploy/fst-compose.sh` can route to the live directory through
`FST_DEPLOY_COMPOSE_DIR`.

## Core services

| Service | Role | Key boundary |
|---|---|---|
| `postgres` | PostgreSQL 17 source of truth | Persistent data volume on the FST drive |
| `fstservice` | API/frontend role | No Docker socket; scheduled scraper disabled |
| `fstworker` | Full mutation worker | Unconditional worker-only Docker socket mount; self-heal controls whether the recycler acts |
| `festivalweb` | Nginx static SPA and reverse proxy | Can render maintenance UI independently of API readiness |

`fstservice` and `fstworker` use the same .NET image with different command and
role configuration. `festivalweb` is a separate multi-stage image. FSTService
also supports an embedded SPA fallback for single-container deployments.

## Repository templates

- Root `docker-compose.yml` builds the four core services for local/template
  use. Copy `.env.example` to an ignored `.env`; proxy arrays are documented
  but inactive.
- `deploy/docker-compose.yml` uses published images, the production-like role
  split, an external backend network, and four optional AirVPN Gluetun services
  under the `vpn` profile.

These templates demonstrate shape and defaults. They do not encode the full
live provider inventory.

## Production-owned overlays

Sanitized configuration inspection on 2026-08-11 found:

- a base project with the four core services and 28 numbered Gluetun services;
- `docker-compose.pia-30.yml` with 30 canonical PIA services and 25 effective
  aligned proxy/control/provider/container mappings;
- optional run-once, recovery, preferred-hostname, and 80-endpoint expansion
  overlays.

This describes configured files, not a claim about currently running
containers. Never copy resolved credentials, endpoints, account metadata, or
provider keys into the repository.

The standard worker guard accepts the canonical PIA overlay by exact filename,
requires all 30 canonical service definitions, permits an effective count up to
30, and validates the aligned arrays and worker dependencies.

## Networks and ports

Templates bind API and web ports to localhost. Nginx communicates with
`fstservice` over the Compose network and re-resolves its container DNS name.
The production-like deploy template also joins `festivalweb` to the external
backend network.

Gluetun services expose HTTP proxy/control ports only inside the Compose
network. The worker talks to those service names; the browser and public API do
not.

## Deployment safety

Before a broad deploy or maintenance action, follow
[`live-safety.md`](live-safety.md). Preserve role-specific feature flags,
publication state, PostgreSQL identity/volumes, and the production overlay
order.
