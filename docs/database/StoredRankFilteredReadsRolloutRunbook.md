# Stored-rank filtered-read rollout

`Features__UseStoredSoloProjectionRanksForFilteredReads` remains `false` by
default. This package prepares a service-only A/B after cleanup scrape `1278`
finishes, publishes, and leaves public reads unfrozen. It does not authorize a
worker rollout, production default change, database mutation, or compose-file
edit in `/home/sfenton/Docker/FestivalServiceTracker`.

## 2026-08-06 live decision

**Decision:** rejected for promotion; keep the service and worker flags false.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/stored-rank-filtered-reads-20260806T154419Z`

The service-only A/B completed all 110 API workloads and 264 matched benchmark
blocks. Correctness passed: the 99 manifest row-parity cases had zero
differences, API statuses and bodies matched, sample counts passed, and the
worker remained stopped. Automatic rollback and normal-mode recovery passed.

Promotion failed eight workload p95 gates and the cold PostgreSQL read-resource
gate. The largest candidate outlier was a filtered single-member rank query
that read `242,340` blocks versus `10,957` for baseline. Commit `e080e4fb`
replaced its full ordering predicate with the indexed stored-rank comparison
and a bounded live probe then read zero physical blocks, but the endpoint still
took `13.352493s` against the prior matched baseline p95 of `12.9209083s`.
Unchanged endpoint work dominates, so the required warm-core improvement is
not available. A second four-hour matrix was not justified.

The exact 157-row durable-job backlog was restored after the A/B. Production
returned to normal read-write service mode on the candidate image, public
scrape `1278` remained unfrozen, and `fstworker` remained stopped with restart
policy `no`.

## What the package proves

The row harness references `FSTService` and invokes
`InstrumentDatabase.GetCurrentStateLeaderboardWithCount` and
`GetCurrentStatePlayerRankingsFiltered` twice against the same manifest:

- baseline: stored-rank flag `false`;
- candidate: stored-rank flag `true`;
- published-source reads enabled for both;
- manifest/preflight snapshot transactions forced read-only; filtered
  player/member rank, population, valid-score fallback, and last-played reads
  stage threshold dictionaries through typed array `unnest` materialized CTEs,
  with no temporary-table DDL, COPY, or DML;
- the supplied database role required to be `SELECT` plus `TEMP` only, with
  no superuser, durable-table/sequence write, durable-schema create, or
  database create privilege, and membership in `pg_read_all_stats` or
  `pg_monitor` for cross-role activity/lock visibility;
- bounded connection, statement, lock, and idle-transaction timeouts.
- the manifest binds `current_database()`, PostgreSQL
  `system_identifier`, server address/port, and socket directories to the
  sanitized effective `fstservice` host/port/database/username and to the
  production Compose Postgres container ID, image ID/reference, one shared
  network ID, its server addresses, and the service host alias exclusively
  owned by that container.

The evidence connection must reach that exact container and database. A
same-named clone, physical clone with a different address/container, alternate
database, mismatched `POSTGRES_CONTAINER`, service target drift, or network
alias/ID drift is rejected before requests and again at every boundary. The
runner enumerates every active endpoint on the alias network; a stale or clone
container matching through Docker `Aliases`, `DNSNames`, or its normalized
container name fails closed. Raw
connection strings and passwords are never rendered.

It does not copy either ranking query. Manifest SQL reuses
`PublishedSoloScopeSql.CurrentSourcesCte`, and tie sampling reuses
`SoloLeaderboardOrderingSql`. Thresholds use
`LeaderboardRankOffsetCalculator.CalculateThreshold`, which preserves the
exact C# truncation used by filtered API reads rather than PostgreSQL integer
cast rounding. Raw max scores come from the same `PathDataStore` reader used by
the API, not a copied query or a potentially stale `song_stats` value.

The deterministic manifest records its seed and fingerprint and must cover:

- all nine solo instruments;
- every source class present in the sealed publication; structurally absent
  reused or source-mismatch cases are not fabricated as live promotion gates
  and remain covered by PostgreSQL/unit fixtures;
- explicit-empty mappings before projection-readiness classification, because
  an empty scope intentionally has no projection generation;
- a source-matched ready current/reused projection whose candidate path returns
  and compares at least one overlay-derived row; source-mismatch fallback does
  not satisfy this gate;
- at least one exact `score` plus order-time peer, including selected account
  IDs;
- threshold `-1`, exact, and `+1` executions backed by real rows at the exact
  and `+1` scores plus expected total-count and membership transitions;
- both rank/page offsets `99` and `100`, with non-empty executions whose first
  filtered ranks are respectively `100` and `101`;
- single leaderboard, all-instrument list, player, and member API paths.

A missing category is a failed promotion gate, not a reason to weaken the
manifest.

Generation also records a source/projection/overlay guard fingerprint before
sampling and rechecks it afterward. A publication-stable but internally moving
scope is rejected rather than mixed into parity evidence.

## Evidence location

All full-run reports and response bodies must stay under:

```text
/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence
```

The tool rejects any configured evidence root or output outside that tree.
Connection strings are read only from an environment variable and are never
written to reports.

## Exact operator card

Do not run this card until scrape `1278` is `completed`, is the selected
published scrape, has a complete published source map, and public reads are
unfrozen. `fstworker` must already be stopped in Docker state `exited` or
`created`; its durable ledger must be offline/stale with no active operation,
worker connection, lease, or running registration/history/rivals/deep/band
job. The runner never starts, stops, or recreates it.

```bash
cd /home/sfenton/FortniteFestivalLeaderboardScraper

export FST_STORED_RANK_CONNECTION_STRING='<SELECT+TEMP+pg_read_all_stats connection string>'
export FST_STORED_RANK_EVIDENCE_ROOT=/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence
export EVIDENCE_DIR="$FST_STORED_RANK_EVIDENCE_ROOT/stored-rank-filtered-reads-$(date -u +%Y%m%dT%H%M%SZ)"
export EXPECTED_PUBLISHED_SCRAPE_ID=1278
export EXPECTED_FSTSERVICE_IMAGE='ghcr.io/sfenton/fstservice:latest@sha256:<reviewed-64-hex-digest>'
export EXPECTED_FST_EVIDENCE_DEVICE='<reviewed findmnt source for /mnt/docker-storage>'
export EXPECTED_FST_EVIDENCE_FSTYPE='<reviewed filesystem type>'
export COMPOSE_DIR=/home/sfenton/Docker/FestivalServiceTracker
export BASE_COMPOSE_FILE="$COMPOSE_DIR/docker-compose.yml"
# Optional assertion only: the runner derives this from the inspected
# fstservice 8080/tcp loopback binding and rejects a mismatch.
export BASE_URL=http://127.0.0.1:8081
export WEB_BASE_URL=http://127.0.0.1:3001
export POSTGRES_CONTAINER=fst-postgres
export ROLLOUT_SEED=20260804
export WARM_REQUEST_STARTS_PER_SECOND=80

# Local/static validation only.
tools/postgres-stored-rank-rollout.sh validate

# Bounded read-only manifest and exact row parity. Stop if this exits nonzero.
tools/postgres-stored-rank-rollout.sh prepare

# Service-only A/B. The script always restores false on exit.
export ALLOW_SERVICE_RECREATE=YES
tools/postgres-stored-rank-rollout.sh run
```

The `run` action:

1. verifies `/mnt/docker-storage` is the exact reviewed mounted device and
   filesystem before creating the run directory, then acquires the exclusive
   global rollout lock;
2. resolves the operator-reviewed immutable service tag+digest, verifies the
   current service image ID matches it, and records it in `image-pin.json` and
   `manifest.json`;
3. resolves the production Compose service database target and Postgres
   container, requires `POSTGRES_CONTAINER` to resolve to that exact Compose
   container, requires the configured host alias on exactly one shared network
   and exclusively owned by that container, and records the
   container/image/network-ID/address binding;
4. reads the database name, cluster system identifier, server address/port, and
   socket directories through the bounded evidence connection and rejects any
   mismatch with the service/container binding;
5. repeats the cleanup/publication preflight;
   each preflight also rejects active scrapes/worker operations, ungranted
   locks, and queries active longer than five minutes, and records compose
   status, current Docker CPU/memory, and FST-drive headroom;
6. rechecks the selected source/projection/overlay/path-max, database target,
   and mount bindings
   before every
   API or benchmark block;
7. verifies the resolved compose roles and pinned service image;
8. captures API responses in process-cold `A-B-B-A` order;
9. runs the seed-randomized `ABBA`/`BAAB` benchmark schedule;
10. recreates only `fstservice` for each block using the digest override and
   `--pull never`;
11. derives the direct API URL from that exact container's inspected
   `8080/tcp` loopback binding, then verifies its service-info container
   hostname and per-process nonce before separately checking the web proxy;
12. verifies the same container identity, exact variant flags, image, HTTP 200
   `/readyz`/web/service-info health, expected publication, and stopped
   offline/stale worker after every block;
13. writes only `analysis-provisional.json`;
14. captures verified false/read-only rollback evidence;
15. recreates `fstservice` in normal mode with stored-rank false and the same
    pinned image;
16. emits `acceptance.json` only after verified normal-mode recovery. Any
    rollback or recovery
    failure emits a rejection/incident artifact and never final acceptance.

Each health request uses a 2-second connect timeout and 5-second overall
timeout. The complete health wait is capped at 180 seconds. A timeout exits the
active A/B path and triggers the EXIT trap; rollback uses the same bounded
health wait, so an unresponsive candidate cannot block false restoration
indefinitely.

Tracked overrides:

- candidate:
  `deploy/rollout/stored-rank-filtered-reads/compose.true.yml`;
- baseline/rollback:
  `deploy/rollout/stored-rank-filtered-reads/compose.false.yml`.
- final normal-mode recovery:
  `deploy/rollout/stored-rank-filtered-reads/compose.recovery.yml`.

All explicitly set the worker flag to `false`. None changes the production
compose file or tracked defaults.

All overrides require `FST_STORED_RANK_SERVICE_IMAGE` and apply it only to
`fstservice`. The runner derives it from `EXPECTED_FSTSERVICE_IMAGE`. Mutable
tag-only references, a reviewed tag different from production Compose, a
missing local digest, or a running image-ID mismatch reject before mutation.
The manifest fingerprint includes the immutable reference and resolved image
ID; every baseline/candidate recreate and rollback re-verifies both.

The baseline/candidate overrides set
`Scraper__RolloutReadOnlyStartup=true` on `fstservice` and explicitly keep it
`false` on `fstworker`. This rollout-only mode loads
existing PostgreSQL song, leaderboard, and item-shop state but suppresses
schema initialization, startup cleanup, spool/DAT deletion, provider catalog
and image sync/persistence, item-shop HTTP/write/timer work, and all
mutation-capable background hosted services. Normal startup remains unchanged
because the option defaults to `false`. The script verifies the role split
after every measurement recreate and read-only rollback.

The same overrides set `Scraper__RolloutPostgresReadOnly=true`. FSTService
appends `-c default_transaction_read_only=on` to the existing Npgsql connection
string in memory, preserving the Compose-provided secret without rendering or
logging it. `StartupInitializer` queries the actual
`default_transaction_read_only` value before every rollout or normal startup.
Rollout startup requires `on`; normal startup/recovery requires `off`, and
`/api/service-info` reports that observed value rather than inferring it from
configuration. `/api/service-info` also returns only the effective sanitized
PostgreSQL target and whether the read-only connection option is present; the
runner compares those fields to the manifest after every recreate.

During rollout read-only mode, middleware returns HTTP 503 with
`Cache-Control: no-store` for mutation-capable methods and known
mutation-on-GET routes. Selected-profile activity writes are skipped for
HTTP/WebSocket GETs. Any unhandled PostgreSQL read-only violation is latched
into unhealthy readiness, so it cannot be swallowed into successful parity.
Guarded paths are canonicalized by trimming trailing slashes (except `/`) and
matched case-insensitively, so alternate route spellings cannot bypass the
block. PostgreSQL `25006` detection recursively unwraps task/parallel
`AggregateException` and nested provider exceptions, so any missed write is
latched unhealthy and returns explicit no-store HTTP 503 rather than 500.

Filtered player and member-score leeway routes are exercised against an actual
service data source with `default_transaction_read_only=on`, including
projection-based ranks, filtered population, historical valid-score fallback,
and thresholded last-played reads.

After read-only false rollback evidence is captured, the recovery override
recreates `fstservice` again with stored-rank `false` and
`RolloutReadOnlyStartup=false`. Acceptance is finalized only after the pinned
image, normal service mode, worker false state, and HTTP/service-info health
are verified. A failed normal-mode recovery leaves the mutation marker armed,
emits incident evidence, and cannot produce acceptance.

The normal recovery container ID is captured immediately after the recreate
and never rebound. Image, environment flags, direct port binding, hostname,
process nonce, health, and the final identity check all use that one ID.
A concurrent replacement—even same-image with normal-looking health—fails the
pinned check, leaves the marker armed, and re-enters recovery.
The same immutable ID is passed through recovery/final quiescence capture,
role-evidence persistence, repeated health checks, and final acceptance
snapshots. Marker clearing occurs only after the pinned recovery quiescence and
role evidence are complete and reverified; evidence capture never adopts the
current container.

Operational recovery is unconditional after any read-only rollback attempt.
Evidence-mount or role-evidence failures cannot prevent the normal-mode
recreate. Once normal image/env/worker/HTTP state is verified, the mutation
marker is cleared from observed state even if evidence persistence later
fails; the command still returns nonzero and emits rejection/incident evidence.
The EXIT trap probes normal state first and never replays the read-only
override onto an already-recovered service.

Standalone rollback first reads and validates the existing manifest and target
entirely in memory. It then arms the recovery marker and EXIT trap before
opening or writing the evidence lock. ENOSPC, a read-only/unwritable evidence
mount, or later evidence failure therefore triggers normal-mode recovery,
attempts incident/evidence persistence only best-effort afterward, and returns
nonzero even when operational recovery succeeds.

Role evidence is populated from freshly inspected service/worker container IDs,
image identity, environment flags, and exact health results rather than from
expected constants. Immediately before acceptance, the runner rechecks that
those same IDs, image, flags, and health remain unchanged. A same-image
concurrent recreate or environment/health drift rejects, emits incident
evidence, and re-establishes normal mode when needed.
Each record also binds the direct loopback URL, inspected container hostname,
and service-info process nonce. A stale direct responder is rejected even when
`festivalweb` still returns healthy JSON; the proxy response must independently
match the already-attested direct nonce.

Finalization captures and hashes DB quiescence first, then immediately captures
a fresh `final` runtime evidence record for service and worker IDs, images,
flags, Docker state, and HTTP/service-info health. Acceptance embeds both
artifacts and is written by same-filesystem atomic rename. A recreate during
the final quiescence window is therefore detected before acceptance.
The final service container ID must exactly equal the verified normal-recovery
container ID; a same-image replacement is still drift and cannot be accepted.

The pre-mutation manifest also pins `fstworker` container ID, image ID/reference,
Docker status, and its stored-rank/published-source/read-only flags. That worker
pin must remain unchanged and false-role throughout every measurement block,
read-only rollback, normal recovery, and final acceptance. A same-image worker
recreate/start/env drift aborts and incidents while service normal recovery
still completes.

Database quiescence reports are captured and SHA-256 hashed before and after
every API/benchmark block, after read-only rollback, after normal recovery, and
immediately before acceptance. They reject any worker-related PostgreSQL
session/application name, granted advisory mutation lease (including
`fst-path-generation-admission`), active durable worker job, or worker ledger
state other than offline/stale.
Pending/deferred registration, history, rivals, deep-scrape, and queued band
jobs also count as non-quiescent.
Preflight requires effective `USAGE`, not merely `MEMBER`, for
`pg_read_all_stats` or `pg_monitor`. It also opens a controlled session through
the sanitized production service connection and proves that the rollout role
can see that distinct role's `usename`, application name, and query in
`pg_stat_activity`. `NOINHERIT` membership and redacted fields reject.
The quiescence helper captures command status locally and never changes the
caller shell's `errexit` state, preserving EXIT rollback/incident handling and
the original failure status.

Every manifest-bound quiescence report embeds a fresh database identity
attestation. Separate pre-request `database-attestation-*.json` files and their
SHA-256 files prove that the evidence/benchmark connection still reaches the
manifest cluster immediately before each API or benchmark block.

The mutation marker is armed before every service recreate. Automatic rollback
checks the false override recreate, all four service/worker role values, and
bounded public health explicitly. It records rollback-success evidence and
clears the marker only after every check passes; any partial recreate or
rollback-step failure remains nonzero and fail-closed.

The global `flock` remains held from before the first mutation through
measurement, false rollback, and acceptance finalization. External deploys
that ignore the lock are detected by the per-block container-ID recheck and
cause rollback.

### Independent verification

```bash
cd /home/sfenton/Docker/FestivalServiceTracker
export FST_STORED_RANK_SERVICE_IMAGE="$EXPECTED_FSTSERVICE_IMAGE"

docker compose \
  -f docker-compose.yml \
  -f /home/sfenton/FortniteFestivalLeaderboardScraper/deploy/rollout/stored-rank-filtered-reads/compose.true.yml \
  config --format json \
  | python3 -c '
import json, sys
services = json.load(sys.stdin)["services"]
flag = "Features__UseStoredSoloProjectionRanksForFilteredReads"
published = "Features__UsePublishedScopeSources"
print("fstservice.stored=" + str(services["fstservice"]["environment"][flag]).lower())
print("fstworker.stored=" + str(services["fstworker"]["environment"][flag]).lower())
print("fstservice.published=" + str(services["fstservice"]["environment"][published]).lower())
print("fstworker.published=" + str(services["fstworker"]["environment"][published]).lower())
'

service_id="$(docker compose -f docker-compose.yml ps -q fstservice)"
worker_id="$(docker compose -f docker-compose.yml ps -q fstworker)"

docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$service_id" \
  | grep -Fx 'Features__UseStoredSoloProjectionRanksForFilteredReads=true'
docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$worker_id" \
  | grep -Fx 'Features__UseStoredSoloProjectionRanksForFilteredReads=false'
docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$service_id" \
  | grep -Fx 'Features__UsePublishedScopeSources=true'
docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$worker_id" \
  | grep -Fx 'Features__UsePublishedScopeSources=false'
docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$service_id" \
  | grep -Fx 'Scraper__RolloutReadOnlyStartup=true'
docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$worker_id" \
  | grep -Fx 'Scraper__RolloutReadOnlyStartup=false'
docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$service_id" \
  | grep -Fx 'Scraper__RolloutPostgresReadOnly=true'
docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$worker_id" \
  | grep -Fx 'Scraper__RolloutPostgresReadOnly=false'
docker inspect --format '{{.Config.Image}}' "$service_id" \
  | grep -Fx "$EXPECTED_FSTSERVICE_IMAGE"

curl --fail http://127.0.0.1:8081/readyz
curl --fail http://127.0.0.1:3001/
curl --fail http://127.0.0.1:3001/api/service-info

docker compose -f docker-compose.yml ps --all -q postgres
docker inspect --format '{{.Id}}|{{.Image}}|{{.Config.Image}}|{{json .NetworkSettings.Networks}}' \
  "$POSTGRES_CONTAINER"
curl --fail http://127.0.0.1:3001/api/service-info \
  | python3 -c '
import json, sys
target = json.load(sys.stdin)["postgresConnectionTarget"]
print(json.dumps(target, sort_keys=True))
'
```

### Exact rollback

```bash
cd /home/sfenton/FortniteFestivalLeaderboardScraper
# Uses the original manifest image and mount bindings; no new image pin is
# resolved and image-pin.json is not overwritten. The standalone path also
# loads the original published scrape ID, rejects a conflicting supplied ID,
# reloads and reattests the original database/container/network binding in
# memory, then arms recovery before attempting the evidence lock. Evidence
# ENOSPC/unwritable failures cannot prevent normal-mode service recovery.
# Pre-exclusive-alias manifests are rejected; regenerate with schema version 4.
tools/postgres-stored-rank-rollout.sh rollback
```

Equivalent explicit command:

```bash
docker compose \
  --project-directory /home/sfenton/Docker/FestivalServiceTracker \
  -f /home/sfenton/Docker/FestivalServiceTracker/docker-compose.yml \
  -f /home/sfenton/FortniteFestivalLeaderboardScraper/deploy/rollout/stored-rank-filtered-reads/compose.false.yml \
  up -d --no-deps --force-recreate --pull never fstservice

docker compose \
  --project-directory /home/sfenton/Docker/FestivalServiceTracker \
  -f /home/sfenton/Docker/FestivalServiceTracker/docker-compose.yml \
  -f /home/sfenton/FortniteFestivalLeaderboardScraper/deploy/rollout/stored-rank-filtered-reads/compose.recovery.yml \
  up -d --no-deps --force-recreate --pull never fstservice
```

Do not recreate `fstworker` for enablement or rollback.

## Matched benchmark contract

The schedule orientation is seed-randomized but every block remains ABBA or
BAAB. Each measured block gets a fresh `fstservice` process. The core workloads
are:

- filtered top: single leaderboard route;
- filtered player query: a dedicated single-account member route invocation.

The separate multi-account member workload remains API parity/non-core
performance evidence and cannot satisfy the core player gate. The actual
`/api/player` route also remains byte/status parity evidence.

Per core workload and variant, the schedule collects:

| Mode | c1 | c8 |
|---|---:|---:|
| Process-cold | 30 requests | 32 simultaneous first requests |
| Warm | 200 requests | 200 requests |

Other single/list/player/member and fallback workloads collect smaller matched
samples and may not regress by more than 10%.

Warm request starts are capped at 80 per second in c1/c8 waves. This preserves
the requested concurrency while staying below the service's 100-request/second
public and global fixed-window limit; pacing wait is outside measured request
latency and is recorded in every block.

PostgreSQL evidence is separate from HTTP latency:

- CPU: per-block `docker stats` samples;
- reads: block-local `pg_stat_database.blks_read` delta;
- temp: block-local `temp_bytes` and `temp_files` delta;
- memory: block-local current container-memory samples and p95.

The runner never treats lifetime `memory.peak` or another cumulative memory
counter as a candidate measurement. A PostgreSQL statistics reset or regressing
counter during any block rejects the run instead of being clamped to zero.
CPU, current memory, reads, temp bytes, and temp files are computed, reported,
and gated independently for process-cold and warm blocks in deterministic
`cold`, then `warm` order. Each block records request start/end and the actual
Docker observation timestamp; CPU and memory analysis uses only observations
inside that request window. A post-request-only observation rejects even a fast
process-cold c1 block.

The sampler exposes an explicit armed timestamp and the request window cannot
start until the Docker sampler process has been launched. For fast blocks, the
single in-flight Docker observation is allowed to finish under its deadline
instead of being canceled/discarded; the report separately records HTTP
completion and the resource-observation window. A late/unarmed or missing
observation still rejects.

Relative resource changes are nullable. A zero baseline with a positive
candidate records the corresponding `*BaselineZero` flag as `true`, emits a
null change percentage, and rejects automatically. Zero-to-zero remains valid,
so `acceptance.json` always remains strict, finite JSON.

Every post-mutation Docker inspect/stats/compose query is wrapped by a bounded
`timeout`; C# Docker stats also has a per-command deadline and cancellation
kills its in-flight process. A hanging Docker command therefore returns to the
EXIT rollback trap rather than blocking it.

Health accepts exactly HTTP 200; redirects are failures. `/api/service-info`
must be valid JSON for the expected published scrape, unfrozen reads, no active
scrape, idle current update, and an offline/stale scraper worker with no current
operation. Its sanitized PostgreSQL target and connection read-only-option
state must exactly match the manifest and active rollout phase.

## Acceptance and rejection

Promotion requires all of the following in one uncontaminated service-only
window:

- zero row differences, including rank, order, count, score, timestamp,
  `api_rank`, and source;
- zero API body, status, content-type, ETag, or source differences, with every
  capture also matching its explicit successful expected status (HTTP 200 for
  the current manifest);
- unchanged published scrape and unfrozen public reads throughout;
- complete manifest coverage, including exact score/time peers;
- source-matched candidate overlay execution with an overlay-derived row;
- real threshold `-1`/exact/`+1` count and membership transitions;
- successful non-empty offset-99 and offset-100 row executions with first
  ranks `100` and `101`;
- at least 10% candidate p95 improvement for both core filtered workloads at
  c1 and c8 warm load;
- no more than 10% p95 regression for process-cold or non-core workloads;
- no more than 10% regression in PostgreSQL CPU, current memory, reads, or temp
  per request, independently for process-cold and warm blocks;
- one unchanged operator-reviewed digest-pinned service image for baseline,
  candidate, and rollback;
- one unchanged stopped worker container/image/state with stored-rank,
  published-source, and rollout-read-only flags all false;
- rollout read-only startup observed on the service and disabled on the worker,
  with no startup/background database or filesystem mutation;
- effective service PostgreSQL `default_transaction_read_only=on`, request-path
  mutation guard active, and zero latched read-only violations;
- evidence/benchmark database name, cluster system identifier, server
  address/port/socket directories, service target, and production Postgres
  container/image/exclusive-alias network binding unchanged at every
  checkpoint;
- final normal-mode service recovery with stored-rank false, read-only startup
  false, observed `default_transaction_read_only=off`, pinned image unchanged,
  worker false, and healthy service-info;
- unchanged reviewed evidence mount source/target/filesystem and exclusive
  rollout-lock ownership;
- healthy service/web/public path after every recreate;
- worker Docker state remains `exited`/`created`, durable ledger remains
  offline/stale, no worker connections or active durable jobs exist, and all
  worker role flags remain `false`.

Any correctness, publication, role-split, or health difference is an immediate
rejection and false rollback. Performance or resource failure is also a
rejection even when rows match.

## Artifacts

The run directory contains:

- `preflight-*.json`: cleanup/publication guard for every block;
- `manifest-guard-*.json`: selected mapping/projection/overlay/path-max guard
  before every measured block;
- `quiescence-*.json` plus `.sha256`: DB worker-session/lease/job evidence at
  every required boundary;
- `quiescence-manifest.jsonl`: ordered label/file/hash ledger;
- `runtime-preflight/*/`: compose status, current container resources, and
  FST-drive headroom for every block;
- `role-verification/*.json`: safe, secret-free resolved runtime role evidence;
- `database-target.json`: sanitized service target and production Postgres
  container/image/network-ID/address/exclusive-alias pin;
- `database-attestation-*.json` plus `.sha256`: exact-cluster checks immediately
  before request blocks;
- `image-pin.json`: configured tag, immutable reference, resolved image ID, and
  pre-mutation service and worker runtime evidence;
- `manifest.json`: seed, selected mappings/accounts/ties, workloads, coverage,
  pinned service/worker/Postgres runtime and database identity, and fingerprint;
- `row-parity.json`: exact baseline/candidate row and player-rank comparison;
- `api/*/capture.json` and `api/*/bodies/`: process-cold service captures;
- `api-comparison*.json`: candidate and repeated-baseline comparisons;
- `benchmark-schedule.tsv`: deterministic ABBA/BAAB order and sample counts;
- `benchmark-blocks/block-*.json`: latency, status/body fingerprints, and
  block-local PostgreSQL resource evidence plus the exact-cluster database
  attestation enforced by final analysis;
- `analysis-provisional.json`: pre-rollback benchmark decision;
- `rollback-evidence.jsonl`: append-only verified read-only rollback and normal
  recovery events;
- `rollout-incident-*.json`: fail-closed rejection evidence when mutation or
  rollback fails;
- `acceptance.json`: final gate decision, written only after verified rollback.

## Remaining hard gate

The package is readiness work only. The remaining gate is one cleanup-complete,
published, unfrozen scrape `1278` followed by this uncontaminated service-only
A/B. The worker must remain false. Until the manifest proves real exact
score/time peers and every acceptance condition passes, do not enable the flag
in production defaults or worker configuration.
