---
status: canonical
owner: operations
last_verified: 2026-08-27
last_verified_commit: c35b7f47
sources:
  - docker-compose.yml
  - deploy/docker-compose.yml
  - deploy/config/fstservice-role.env
  - deploy/config/fstworker-role.env
  - FSTService/StartupInitializer.cs
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/PublicationGeneration.cs
  - FSTService/Persistence/PublicationPathArtifactReleaseGate.cs
  - deploy/fst-compose.sh
  - FSTService/Dockerfile
  - FortniteFestivalWeb/Dockerfile
  - FortniteFestivalWeb/nginx.conf
  - tools/fst-worker-compose-guard.sh
  - /home/sfenton/Docker/FestivalServiceTracker/docker-compose.yml
update_triggers:
  - Compose services, images, roles, volumes, ports, networks, health checks, or production ownership change.
  - Role startup ordering or startup readiness gates change.
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

## Role startup ordering

Only the schema-initializing role applies database releases. Any role that sets
`Scraper__ApiOnly=true`, `Scraper__SkipStartupSchemaInitialization=true`, or
`Scraper__RolloutReadOnlyStartup=true` never runs DDL, so a release that
changes publication-bound surfaces must be applied before those roles start:

1. Stop or hold the old worker before applying a path-manifest release so an
   older binary cannot prepare or commit a candidate after the schema cut.
2. Start the API/schema-initializing role (`fstservice`, which keeps
   `Scraper__SkipStartupSchemaInitialization=false`). Its startup applies the
   schema plan, including the bounded
   `publication-generation-retirement-columns`,
   `publication-generation-foreign-keys` migration, the
   concurrent `publication-generation-retirement-index` migration, the
   `publication-path-artifacts` migration, and the rebinding of retained active
   pointer snapshots to the current path manifest version. Retirement columns
   use a short transaction; the exact partial index uses bounded
   `CREATE INDEX CONCURRENTLY` under a migration advisory lock and repairs an
   invalid interrupted artifact on retry. The foreign-key step additively
   installs a separately named restrictive FK plus a `BEFORE DELETE` guard.
   An old `c35b7f47` service may restore the legacy named FK to CASCADE without
   removing either new invariant. All steps use bounded lock/statement/command
   timeouts and fail startup for retry rather than continuing partially.
3. Confirm that role is healthy on `/readyz`.
4. Start `fstworker`, any API-only role, and any rollout read-only role. With
   `Scraper__UsePublicationPathArtifacts=true` each verifies the current
   publication's path artifact release before signalling ready, including
   before the rollout read-only early return, and fails fast with an explicit
   remediation message if the schema-initializing step has not been applied.

The service role currently enables `UsePublishedScopeSources`. It therefore
does not signal startup readiness until the current publication owns an exact
authoritative source binding, and `/readyz` continues to revalidate that
binding through a one-second keyed cache. Apply the schema and allow a
publication produced by the binding-hash-aware worker before starting that
role. A legacy or partial current mapping is intentionally unhealthy rather
than silently served; a role that deliberately retains the old read path may
keep `UsePublishedScopeSources=false` during a coordinated rolling transition.

The worker deployment must also provide `MIDI_ENCRYPTION_KEY` as a valid 32-
or 64-character hexadecimal AES key when scrape-pass staging is enabled.
Startup option validation fails before readiness when this prerequisite is
missing or malformed; the API-only service role does not need the worker
secret.

Every publication commit also revalidates the candidate path manifest version,
row count, canonical hash, and cache ownership. A stale deferred candidate or a
candidate with staged paths but an inherited songs cache fails before pointer
movement.

The stored-rank `compose.true.yml` and `compose.false.yml` read-only overlays
are post-schema canaries, not schema bootstrap configurations. After any image
or publication-manifest release, apply their sibling `compose.recovery.yml`
first and wait for `fstservice` readiness; only then apply the read-only true or
false overlay. If the release gate rejects a canary, return to the recovery
overlay rather than weakening the gate.

See
[Publication path artifact snapshots](../database/PublicationPathArtifactSnapshots.md)
for the exact readiness conditions and the path-generation role flags.

## Deployment safety

Before a broad deploy or maintenance action, follow
[`live-safety.md`](live-safety.md). Preserve role-specific feature flags,
publication state, PostgreSQL identity/volumes, and the production overlay
order. Candidate throughput profiles remain run-once-only; continuous startup
uses the approved baseline profile and does not authorize candidate
`1600/64/8`.
