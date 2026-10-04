---
status: canonical
owner: operations
last_verified: 2026-09-29
last_verified_commit: f65a3a4a
sources:
  - FSTService/Persistence/MetaDatabase.FrozenAcquisitionAbandonment.cs
  - docker-compose.yml
  - deploy/docker-compose.yml
  - deploy/config/fstservice-role.env
  - deploy/config/fstworker-role.env
  - FSTService/StartupInitializer.cs
  - FSTService/Scraping/ItemShopService.cs
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/PublicationGeneration.cs
  - FSTService/Persistence/PublicationPathArtifactReleaseGate.cs
  - deploy/fst-compose.sh
  - FSTService/Dockerfile
  - FSTService/Scraping/PiaRegionRotator.cs
  - FSTService/ScraperWorker.cs
  - FSTService/Scraping/PostScrapeOrchestrator.cs
  - FortniteFestivalWeb/Dockerfile
  - FortniteFestivalWeb/nginx.conf
  - tools/fst-worker-compose-guard.sh
  - /home/sfenton/Docker/FestivalServiceTracker/docker-compose.yml
  - /home/sfenton/Docker/FestivalServiceTracker/docker-compose.pia-30.yml
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
PostgreSQL and `fstservice` to be healthy/ready, validates the continuous
worker image/config binding, and then branches under the same shared
worker-mutation lock. Idle and unfrozen state keeps the existing bounded
effective-proxy recovery, runtime qualification, and continuous worker
recreate path. A stopped/absent worker plus exact active-candidate state
(`currentUpdate.status` `updating` or `stalled`, frozen reads with
`freezeReason=post-process`, a different published scrape, and a stale/offline
prior worker heartbeat) instead loads the durable PostgreSQL resume state,
rejects legacy/null/incomplete checkpoints, starts only the existing
`scrape-resume` run-once worker, waits for publication and unfreeze
convergence, and only then recreates the continuous worker. Failure leaves the
public service, web, and database containers under their normal ownership. A
failed start stops the worker only while operational state remains idle and
unfrozen; if work or a freeze has begun, the worker remains running for the
guarded no-progress recovery procedure.

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
| `fstservice` | API/frontend role | No Docker socket; scheduled scraper disabled; owns Item Shop provider reconciliation |
| `fstworker` | Full mutation worker | `worker` profile, bounded process-crash restart, worker-only Docker socket, guarded host startup; loads persisted Item Shop state only |
| `festivalweb` | Nginx static SPA and reverse proxy | Can render maintenance UI independently of API readiness |

`fstservice` and `fstworker` use the same .NET image with different command and
role configuration. `festivalweb` is a separate multi-stage image. FSTService
also supports an embedded SPA fallback for single-container deployments.
The service role sets `Scraper__EnableItemShopRefresh=true`; the worker role
sets it to `false`, preventing two processes from replacing the same
`item_shop_tracks` projection.

When rolling out this ownership split while a scrape is active, recreate
`fstservice` first and leave the running worker untouched. The old worker
binary can still own its legacy midnight shop timer, so recreate it with the
new image and role file at the next verified idle and unfrozen worker boundary,
before the next UTC midnight. Confirm afterward that service logs show Item
Shop scheduling and worker logs show persisted-state-only initialization.

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

Sanitized configuration inspection on 2026-09-26 found:

- a base project with the four core services and 28 numbered Gluetun services;
- `docker-compose.pia-30.yml` with 30 canonical PIA services and 30 effective
  aligned proxy/control/provider/container mappings;
- optional run-once, recovery, preferred-hostname, and 80-endpoint expansion
  overlays.

On 2026-10-04 the production overlay defined 60 canonical PIA services
(`.env` canonical count 60) with 50 effective aligned endpoints; see
[VPN and proxy pool](vpn-proxy-pool.md) for the exit-scaling and refresh
measurements.

This describes configured files, not a claim about currently running
containers. Never copy resolved credentials, endpoints, account metadata, or
provider keys into the repository.
Service/worker releases use one immutable image built from merged `master`,
with the GHCR digest, local image ID and exact OCI source revision retained
through deployment and guarded worker startup. The master release retains
the shutdown, egress retry and projection changes previously qualified in the
local integration bundle. A service/worker-only rollout preserves the
separately pinned web image and container until an explicit web deployment.
The production-owned worker env enables the egress
refresh plus `Scraper__BandCurrentProjectionMaxParallelScopes=6`,
`BandTeamRankings__OverlapRankHistorySnapshotsWithBandRankings=true`, and
`Scraper__PrepareSoloCurrentProjectionBeforeRivals=true`; the production
`.env` enables `BAND_CURRENT_PROJECTION_USE_BATCHED_MEMBER_STATS_AGGREGATION`.
These remain production canaries, not accepted defaults.

Between master releases, the improvement loop deploys local bundle images at
idle scrape boundaries through the guarded worker deploy: one immutable image
for both API and worker, labelled with the exact bundle revision, built from a
`deploy/loop-bundle*` branch that merges reviewed PRs onto `master`. Bundle
content merges to `master` only after the next scrape on it passes leaderboard
and band fetch. Since the scrape `1460` boundary (2026-10-03) production runs
`deploy/loop-bundle4-20261002` (`1dabb29c`: everything merged through #154
plus #155). The worker env additionally sets
`Scraper__UseValidatedSoloProjectionForLegacyDerivedReaders=true`,
`Scraper__UseValidatedSoloProjectionForLegacyPrecompute=true`,
`Scraper__BandCurrentProjectionPublishParallelism=6`,
`Scraper__BandSearchProjectionParallelBandTypes=true`,
`Scraper__BandSpoolFlushMaxParallelBandTypes=3`, and
`Scraper__RankHistorySnapshotMaxDegreeOfParallelism=2`, and adds US East as an
eighth egress-refresh region (see [VPN/proxy pool](vpn-proxy-pool.md)). These
remain production canaries, not accepted defaults. Check the running image's
`org.opencontainers.image.revision` label before deploying a master image:
a newer master build can lack bundle changes that are not merged yet.

The operator-authorized frozen acquisition recovery on 2026-09-29 failed
scrape `1448` and candidate publication `379` through the qualified native
command, preserving publication `377` / published scrape `1447` and retained
publication `375`. It released the acquisition freeze and working pointer;
there is no remaining recovery or publication obligation for that failed
candidate. Published/retained data and worker fingerprints and the public
songs response matched before/after recovery. All candidate staging and
artifacts were retained, so this fact grants no cleanup or deletion authority.

The standard worker guard accepts the canonical PIA overlay by exact filename,
requires every canonical service definition (`pia-gluetun-1` through the
canonical count, which may be 30 to 60), permits an effective count up to the
canonical count, validates aligned arrays and worker dependencies, rejects static effective
PIA endpoint-IP pins, and provides the bounded production startup handoff.
Canonical effective-service membership and static-pin rejection intentionally
apply to every guard action, including checks and existing recreate flows. The
guard also requires the `worker` profile, `on-failure:5` for continuous merges,
and `restart: no` for run-once merges.

Optional PIA region rotation is a **worker-only** behavior in the shared
service image. A new source build does not activate it: configure an explicit
qualified candidate list and the enabled flag in the production-owned worker
overlay only after the immutable image and full effective-proxy guard pass at
an approved safe scrape/publication boundary. Do not recreate the API, web,
database, or entire Compose project for this worker-only rollout. A running
scrape is not a safe boundary merely because its progress is slow: preserve
the shared worker lock, native interrupted-acquisition isolation/normalization
gates, published scrape, read freeze, health, and rollback before any stop.

Many effective exits have static regions whose OpenVPN servers frequently
fail TLS (for example US Las Vegas, US Salt Lake City, SE Stockholm, Denmark,
CA Toronto, US Michigan, and US Ohio in 2026-09-26 trials). After a container
restart such an exit can stay `starting`, which fails the pre-stop 24/24
guard. Moving only that exit's runtime selector to a qualified region through
its Gluetun control API (credential-free region payload, no Compose change)
restored real egress within seconds during the 2026-09-26 cutover; a later
container restart returns the static region. Replacing those static regions
in the production overlay is a separate operator change.

On 2026-09-27 an idle-boundary attempt changed eight effective exits' static
regions to qualified ones and recreated all eight at once; some did not become
healthy within ten minutes (one logged a transient PIA `AUTH_FAILED`), so the
deploy rolled back every file and service. Recreating them with their old
static regions left six stuck in TLS failures, which blocked the rollback
guard until each was moved at runtime. A settings PUT that answers
`already crashed` changes the selector but leaves the VPN loop down; a
`/v1/vpn/status` `stopped` then `running` cycle reconnects it. Change static
regions one exit at a time with a health check between them, or add already
healthy qualified spares to the worker arrays without recreating effective
exits.

A pre-stop guard check during a live scrape can fail repeatedly on transient
duplicate egress or a probe failure while the running worker refreshes
exits; the mid-acquisition cutover retries until one check passes. After the
worker stops, a stale exit left mid-refresh (for example `unhealthy` after
control-loop timeouts) blocks the recreate guard until it is reconnected.

A worker stopped for a mid-acquisition cutover must exit gracefully so it can
record its phase attempt as `interrupted`; native interrupted-acquisition
normalization rejects a candidate whose attempt is still `running` or whose
worker still owns a database transaction. At the higher throughput reached with
egress refresh, 30- and 150-second stop grace periods both ended in SIGKILL
during 2026-09-26 cutovers (the host's 30-second shutdown window is followed by
synchronous service disposal that can wait on in-flight writes). Give the
worker stop a long grace period (600 seconds was used afterwards). Since
`Scraper:WorkerShutdownTimeoutSeconds` (default 120, clamped to 30–600) the
full-worker host waits long enough for a cancelled pass to finish its bounded
30-second cleanup and record the interrupted attempt; the repository template
sets `stop_grace_period: 180s` on `fstworker` so every Compose stop or
force-recreate outlasts that budget, and the production overlay needs the same
value. A guarded rollback to the previous image can mark an older candidate
`abandoned_staging_cleanup` at the next scrape boundary, preserving published
data, only when startup gates admit a new scrape.

A clean container exit alone does not prove durable interruption. A stopped
worker can leave its acquisition attempt `running` and public reads frozen
with reason `scrape`. Neither interrupted-acquisition normalization nor
ordinary failure isolation accepts that state. Restarting the previous image
does not guarantee cleanup: startup notification recovery rejects frozen
reads before a new scrape can reach the abandonment boundary. A replacement
worker also makes the old running attempt foreign to the current worker
identity. If this occurs, retain the stopped worker, published pointer,
working candidate and freeze evidence. The explicit
[frozen acquisition abandonment command](../reference/cli.md#frozen-acquisition-abandonment)
can recover the qualified stopped/uncheckpointed state under the host worker
lock and exclusive publication fence, retaining all candidate artifacts.
Hold deployment if its exact-state check fails. Do not manually clear the freeze, change the attempt or
worker identity, or repeatedly recreate workers to force startup cleanup.

The in-process control update changes a PIA container's **runtime** region,
not its production Compose environment. On container restart the static
`SERVER_REGIONS` baseline returns; the host boot guard must still verify
healthy, distinct effective egresses and a valid worker image before
starting a worker. A failed dynamic update that cannot recover is
quarantined, not masked by the Docker `healthy` flag or a control response.
See [VPN proxy pool](vpn-proxy-pool.md) for live egress and cooldown gates.

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
