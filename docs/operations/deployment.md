---
status: canonical
owner: operations
last_verified: 2026-08-11
last_verified_commit: 2bdf7287
sources:
  - docker-compose.yml
  - deploy/docker-compose.yml
  - deploy/fst-compose.sh
  - FSTService/Dockerfile
  - FortniteFestivalWeb/Dockerfile
  - FortniteFestivalWeb/nginx.conf
  - tools/fst-worker-compose-guard.sh
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

### Production startup ownership

The production startup contract assigns the first idempotent
`docker compose up -d --no-deps` to the production-owned boot orchestrator, not
repository Compose. Its boot set is PostgreSQL, `fstservice`, `festivalweb`, and
the exact effective proxy services; `fstworker` remains stopped or Created.
The orchestrator then hands off to the production-synchronized copy of the
repository guard's `--recover-start` action.

Both repository templates place `fstworker` behind the `worker` Compose
profile. A bare `docker compose up -d` therefore excludes it, while the guard's
Compose config resolutions and targeted `up ... fstworker` commands explicitly
pass `--profile worker`. Proxy-only recreates remain targeted solely at the
validated effective proxy names and do not activate the worker profile.

The repository provides the guard source. Copying it into the live project and
wiring the boot unit remain production operations; repository templates do not
install or mutate that live wiring.

`--recover-start` does not recreate or restart core services. It requires
PostgreSQL and `fstservice` to be healthy/ready, checks that worker/publication
state is safe, converges only the effective proxy set, runs the full proxy
qualification probes, and finally recreates only `fstworker` with `--no-deps`.
Failure leaves the public service, web, and database containers under their
normal ownership. A failed start stops the worker only while operational state
remains idle and unfrozen; if work or a freeze has begun, the worker remains
running for the guarded no-progress recovery procedure.

This two-step boundary is necessary because a Docker restart policy does not
start a dependent that never passed `service_healthy`. Do not replace it with a
sidecar, relax dependencies to `service_started`, or make the guard a broad
stack reconciler.

The continuous worker uses `restart: on-failure:5`. That policy provides a
bounded response to a nonzero in-process exit while Docker remains up, but it
does not start the worker after a Docker daemon or host restart. Guarded host
startup owns that transition. Run-once overlays continue to resolve to
`restart: no`.

The recovery action has a 1,800-second default total deadline spanning core
readiness, both proxy windows, runtime qualification, and worker readiness.
Size the production unit's outer startup timeout above that deadline plus
signal-cleanup margin. A 300-second timeout is invalid for this contract.

All worker-start/recreate actions share one host lock. Its default is
`.fst-worker-compose-guard.lock` inside the resolved Compose directory.
Production units and manual invocations must resolve the same Compose directory
and run as the same Unix owner, or set one explicit shared absolute lock path.

## Core services

| Service | Role | Key boundary |
|---|---|---|
| `postgres` | PostgreSQL 17 source of truth | Persistent data volume on the FST drive |
| `fstservice` | API/frontend role | No Docker socket; scheduled scraper disabled |
| `fstworker` | Full mutation worker | `worker` profile, bounded process-crash restart, worker-only Docker socket, guarded host startup |
| `festivalweb` | Nginx static SPA and reverse proxy | Can render maintenance UI independently of API readiness |

`fstservice` and `fstworker` use the same .NET image with different command and
role configuration. `festivalweb` is a separate multi-stage image. FSTService
also supports an embedded SPA fallback for single-container deployments.

## Repository templates

- Root `docker-compose.yml` builds the four core services for local/template
  use. Copy `.env.example` to an ignored `.env`; proxy arrays are documented
  but inactive. Bare `up` omits the profiled worker.
- `deploy/docker-compose.yml` uses published images, the production-like role
  split, an external backend network, and four optional AirVPN Gluetun services
  under the `vpn` profile. Its worker is independently gated by the `worker`
  profile.

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
30, validates aligned arrays and worker dependencies, rejects static effective
PIA endpoint-IP pins, and provides the bounded production startup handoff.
Canonical effective-service membership and static-pin rejection intentionally
apply to every guard action, including checks and existing recreate flows. The
guard also requires the `worker` profile, `on-failure:5` for continuous merges,
and `restart: no` for run-once merges.

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
order. Candidate throughput profiles remain run-once-only; continuous startup
uses the approved baseline profile and does not authorize candidate
`1600/64/8`.
