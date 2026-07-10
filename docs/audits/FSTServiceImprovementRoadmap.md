# FSTService Improvement Roadmap

**Audit date:** 2026-07-10  
**Container:** `fstservice`  
**Mode:** Read-only correctness, API, persistence, security, and reliability audit  
**Implementation status:** No service, database, configuration, or deployment
changes were made during this audit.

## Executive decision

The service has good low-level PostgreSQL primitives and excellent cached local
response speed. Its highest risks are not ORM overhead or basic HTTP serving.
They are publication correctness, process-bound cache/notification behavior,
security boundaries, synchronous/chatty database access, unbounded memory
caches, and unclear API-versus-worker ownership.

The accepted direction is to make `fstservice` the sole public/API owner,
schema-migration owner, catalog/shop/image owner, public-cache owner, and
WebSocket delivery owner. It must consume durable worker events instead of
assuming process-local notifications.

## Audit report delivery

This roadmap and the worker roadmap are accompanied by:

`FST Autonomous Agent: Recap - Service and Worker Deep Audit · Needs Attention`

Delivery requires rendered HTML/text plus SMTP acceptance, or a recorded SMTP
blocker and exact outbox artifact paths.

## Cross-container publication rollout

The publication contract is one coordinated change set:

1. **PostgreSQL:** add backward-compatible per-scope published-source schema.
2. **Worker:** populate/dual-write the source mapping and atomically promote it.
3. **Service:** read the mapping for endpoints and exports while retaining the
   old resolver as rollback.
4. **Parity:** force frozen cold misses and compare route/export fingerprints.
5. **Cutover:** remove the old resolver only after live-scrape parity.

No component may independently cut over before the preceding contract is
deployed and populated.

## Autonomous execution windows

| Phase/task family | Execution class | Decision window |
|---|---|---|
| SERVICE-0.1/0.2 published-source and status semantics | `full-scrape-ab` | Wait for current publish/unfreeze, stop worker, deploy PG/worker/service contract, run one complete scrape, stop, compare cold-miss/export/status parity |
| SERVICE-0.3 through 0.5 security boundaries | `continuous-safe` by default | Implement/test immediately; recreate only `fstservice` and verify the full public path without waiting for worker unless shared proxy/control behavior creates an explicit dependency |
| SERVICE-1 role isolation/events/catalog ownership | `full-scrape-ab` | One complete two-process scrape/event/cache/publication window before accept/reject |
| SERVICE-2 query batching/async and SERVICE-3 caches | `continuous-safe` by default | Deploy/recreate only `fstservice` and run API A/B immediately; require a scrape boundary only when publication, shared DB load, or worker/cache behavior can change |
| SERVICE-4 migrations/startup | `full-scrape-ab` | Worker held for migration/deploy, then one complete scrape and restart/recovery proof |
| SERVICE-5 observability/rate reporting and code-only SERVICE-6 cleanup | `continuous-safe` unless runtime behavior changes | No worker hold for additive metrics/docs/tests; runtime removal follows the stricter owning task |

Every boundary/full-scrape task follows the autonomous skill's
wait-stop-deploy-run-stop-iterate/accept/reject loop.
Independent service tasks continue while scrape evidence accrues. A service
container is stopped only for the shortest practical deploy/recreate window,
not for the full implementation period.

## Current evidence

| Surface | Live/static evidence | Assessment |
|---|---|---|
| Container | Healthy, about 130 MiB RSS under a 2 GiB cap | Good current footprint |
| Cached endpoints | Shell 0.3-0.6 ms; features 0.5-0.6 ms; Songs 0.8-3.6 ms; Shop 0.4-1.1 ms locally | Great cached path |
| Cached leaderboard | Top-10 all-instrument response 4-25 ms | Good |
| Cold leaderboard | Top-11 cache miss 759 ms; another changed-scope miss 1,225 ms; repeat 5 ms | Poor fallback path, strong cache dependency |
| Public state | PostgreSQL was frozen on published scrape 1227 while active snapshot state pointed all 6,129 scopes at scrape 1228 | Correctness-sensitive |
| Service status | `/api/service-info` reported `currentUpdate.status=idle` while the worker and database were still in the scrape/post-process/publish cycle | Bad status contract |
| Public freeze | `public_reads_frozen=true`, reason `publish`, but frozen scrape ID was null | Bad resolver input |
| Static DB use | About 289 synchronous opens and 577 synchronous command executions versus 42/124 async equivalents | Poor scalability |

## Great / good / okay / poor / bad

| Rating | Areas |
|---|---|
| Great | Cached local response latency; parameterized Npgsql; binary COPY/set-based persistence foundations |
| Good | Health endpoints; response-cache staging; publication ledger concept; test breadth |
| Okay | API/worker shared binary as a transitional deployment mechanism |
| Poor | Sync DB access; N+1 query paths; unbounded caches; DTO duplication; startup/schema ownership; role separation |
| Bad | Frozen cold-miss source selection; process-local notification/invalidation; public token-backed diagnostics; Docker socket on public API; misleading status |

## Phase SERVICE-0: Correct public-read and security boundaries

**Decision:** Accepted correctness/security blocker  
**Dependencies:** The schema part of PostgreSQL phase PG-1 may be developed in
the same coordinated release, but service cutover follows worker population.  
**Rollback:** Feature-flag resolver changes; retain the last published scrape
and old cache artifacts.

### SERVICE-0.1 - Resolve every public read from an explicit published source

**Evidence**

- `PublicApiResponseCacheMiddleware.cs:23-38` allows frozen cache misses to
  continue to endpoints.
- `PublicReadGateService.cs:20-27` disables cached-only enforcement.
- `InstrumentDatabase.cs:198-329,2634-2811` falls back through
  `leaderboard_snapshot_state.active_snapshot_id`.
- Active state advanced to 1228 while publication remained 1227.

**Work**

1. Add per-scope published physical-source mapping.
2. Resolve public current state from published mapping, never active mapping.
3. Use `public_reads_frozen_scrape_id` or remove it in favor of an explicit
   published source contract.
4. Make a frozen cache miss return a controlled unavailable response until the
   safe resolver is available.
5. Apply the same source resolver to exports.

**Acceptance**

- A forced cold miss during an in-progress scrape cannot return an unpublished
  account, rank, score, or total.
- Published and frozen API fingerprints remain stable until one atomic promote.

### SERVICE-0.2 - Correct service status semantics

**Evidence**

- `currentUpdate.status=idle` while scrape 1228 was incomplete and public reads
  were frozen.
- `nextScheduledUpdateAt` was already hours in the past.

**Work**

1. Derive public status from the durable scrape/publication/worker operation
   ledger.
2. Represent network, post-process, publication, freeze, and failure states.
3. Update operation timestamps during long phases.

**Acceptance**

- Service status and PostgreSQL state never disagree on active/frozen/published
  scrape IDs.

### SERVICE-0.3 - Protect or remove token-backed diagnostics

**Evidence**

- `Api/DiagEndpoints.cs:78-165` exposes caller-controlled Epic requests through
  the service token and bypasses normal pacing/proxy logic.

**Work**

1. Remove diagnostics from public routing or require administrative auth.
2. Route any retained Epic diagnostics through the same request budget and
   auditing as worker traffic.

**Acceptance**

- Anonymous requests receive 404/401/403 and cannot trigger an Epic request.
- Authenticated diagnostics consume the normal request budget and produce an
  audit event.

**Rollback**

- Disable the route entirely; do not restore anonymous access.

### SERVICE-0.4 - Remove host-control capability from the public API

**Evidence**

- The public API container mounts `/var/run/docker.sock`.

**Work**

1. Move proxy recycle operations to `fstworker` or a narrow sidecar.
2. Render production compose and prove `fstservice` has no Docker socket.

Production ownership is
`/home/sfenton/Docker/FestivalServiceTracker`, not the repository compose
template.

**Acceptance**

- Rendered production compose and `docker inspect fstservice` show no Docker
  socket mount or Docker-control group.
- Worker/sidecar proxy recycle still passes a bounded live health test.

**Rollback**

- Roll back to the narrow worker/sidecar control path, never to Docker socket
  access in the public API.

### SERVICE-0.5 - Remove credential-like tracked defaults

Do not print or copy the values. Replace tracked credential-like defaults with
empty placeholders, rotate affected secrets, and add secret-scanning coverage.

**Acceptance**

- Repository secret scan is clean.
- Production readiness succeeds only with environment/secret-store values.
- Rotation is recorded without putting values in logs or docs.

**Blocked condition**

- Rotation may pause only for operator-owned credential access; code cleanup
  and secret scanning can proceed independently.

## Phase SERVICE-1: Make process boundaries real

**Decision:** Accepted  
**Dependencies:** SERVICE-0

### SERVICE-1.1 - Build role-specific hosts

`fstservice` should own:

- public HTTP/API;
- WebSockets;
- public caches;
- schema migrations;
- song/item-shop catalog refresh;
- image ownership;
- status aggregation.

It should not host scrape, rank, band-history, registration-backfill, proxy
recycle, or worker-only loops.

**Acceptance**

- The API host starts without worker registrations.
- The worker host starts without public endpoints, shop timers, or image work.

### SERVICE-1.2 - Add durable cross-process events

**Evidence**

- `Api/NotificationService.cs:14-18` stores WebSocket clients in process memory.
- Worker-local calls emit notifications and invalidations that API clients
  cannot receive.

**Work**

1. Start with PostgreSQL `LISTEN/NOTIFY` plus a durable outbox or an outbox
   poller for replay.
2. Publish scrape lifecycle, cache generation, score-improvement, backfill, and
   history events.
3. Make service consumers idempotent and persist deduplication identity.

**Acceptance**

- A two-process integration test connects to `fstservice`, publishes from
  `fstworker`, proves at-least-once durable replay after retry/restart, and
  proves idempotent side effects/state convergence.

### SERVICE-1.3 - Give catalog/shop/images one owner

**Evidence**

- Both roles initialize `FestivalService` and `ItemShopService`.
- Process-local image paths can be written to shared PostgreSQL.

**Work**

1. Make service the sole owner.
2. Store provider URLs or shared-volume-relative paths.
3. Batch song persistence.

## Phase SERVICE-2: Remove hot-path query fan-out

**Decision:** Accepted A/B program  
**Dependencies:** SERVICE-0

### SERVICE-2.1 - Convert request repositories to async and propagate aborts

**Work**

1. Prioritize top endpoints by `pg_stat_statements` total time and call count.
2. Convert connection open, command execution, and readers to async.
3. Pass `HttpContext.RequestAborted`.
4. Remove the thread-pool compensation after sync-over-async is gone.

### SERVICE-2.2 - Batch selected-member rankings

**Evidence**

- `/api/rankings/selected-members` can execute up to 81 ranking/count queries.

**Candidate**

- One set query for account arrays and all requested instruments, returning
  ranking and population data together.

**Proof**

- Same accounts, instruments, published scrape, warm/cold state, and response
  fingerprint.
- Measure query count, p50/p95/p99, buffers, connection waits, CPU, and payload.

**Promotion target**

- Constant query count and at least 50% lower p95 without payload differences.

### SERVICE-2.3 - Batch player fallback ranks and member scores

**Evidence**

- `PlayerEndpoints.cs:154-184` performs one rank query per invalid score.
- `LeaderboardEndpoints.cs:195-250` fans out per account and instrument.

**Candidate**

- A bounded `VALUES`/temporary-input set joined once to the published current
  projection.

### SERVICE-2.4 - Replace duplicated profile/member payload logic

**Evidence**

- Player and member endpoints duplicate threshold, ranking, fallback, and
  population calculations and disagree on field shape/accuracy units.

**Work**

1. Build one typed projection service.
2. Version the API response where semantics change.
3. Add golden JSON schema and unit tests.

## Phase SERVICE-3: Bound cache and write-on-read behavior

**Decision:** Accepted  
**Dependencies:** SERVICE-0 and SERVICE-2

### SERVICE-3.1 - Replace unbounded response dictionaries

**Evidence**

- `Api/ResponseCacheService.cs:10-69` uses an unbounded dictionary of byte
  arrays.
- Raw account/instrument/leeway values create high-cardinality keys.

**Work**

1. Canonicalize account IDs, instruments, ordering, and leeway precision.
2. Add byte and entry budgets, TTL, and metrics.
3. Keep published generation in the key.

**Acceptance**

- Cache RSS and entry count remain bounded under high-cardinality load.

### SERVICE-3.2 - Debounce profile activity writes

**Evidence**

- Successful selected-profile responses can trigger synchronous activity or
  registration writes.

**Work**

1. Record activity at most once per profile/time window.
2. Queue non-critical writes outside the response path.
3. Preserve registration correctness with idempotent claims.

### SERVICE-3.3 - Collapse overlapping payload caches

Choose one ownership chain among endpoint construction, persisted precompute,
middleware cache, and memory cache. Keep layered caches only when each layer has
a measured purpose and generation semantics.

## Phase SERVICE-4: Govern schema startup and recovery

**Decision:** Accepted  
**Dependencies:** PostgreSQL roadmap phases PG-0 and PG-1

### SERVICE-4.1 - Make service the migration owner

1. Add a schema version ledger.
2. Use advisory migration lock, lock timeout, and statement timeout.
3. Move runtime `Ensure*Schema` DDL into versioned migrations.
4. Keep worker startup schema initialization disabled.

### SERVICE-4.2 - Replace misleading checkpoint behavior

**Evidence**

- `InstrumentDatabase.Checkpoint` and `MetaDatabase.Checkpoint` are no-ops but
  callers log successful PostgreSQL checkpoints.

Either implement a measured PostgreSQL checkpoint policy or remove the API and
success log. Do not issue manual checkpoints by default under the current WAL
load.

### SERVICE-4.3 - Add backup/restore readiness to service operations

Service deployment is not promotion-ready until the PostgreSQL roadmap has a
tested backup, restore, and route-fingerprint drill.

## Phase SERVICE-5: Health, observability, and rate policy

**Decision:** Accepted

### SERVICE-5.1 - Add endpoint query budgets

For each public endpoint record:

- query count;
- connection wait;
- DB execution time;
- cache status;
- response bytes;
- published generation;
- cancellation.

### SERVICE-5.2 - Separate rate policies

The current policies are effectively identical. Define explicit budgets for
cheap metadata, cached leaderboards, cold leaderboards, search, exports,
diagnostics, and writes.

### SERVICE-5.3 - Expose required-loop and event-consumer health

Service readiness should include migration completion, event-consumer
freshness, publication-state readability, and public-cache generation.

## Phase SERVICE-6: Remove stale paths and configuration

**Decision:** Accepted after reachability proof

Candidates:

- Legacy `RoundRobinProxyHandler` diagnostics/accessor surface.
- Dead service-side scraper helpers.
- Unused feature/config keys.
- `PublicReadGateMiddleware.RequiresPublishedData`.
- Unused auth service methods.
- Legacy writer entry points used only by tests.

**Proof**

1. Production call graph.
2. Two-process integration suite.
3. Route contract snapshot.
4. Remove one chain per commit.

## Projected outcomes

| Outcome | Promotion target |
|---|---|
| Historical correctness | Zero unpublished rows on forced frozen cache misses |
| Status correctness | API, worker, scrape log, freeze, and publication IDs agree |
| Security | No public Epic-token proxy and no Docker socket in `fstservice` |
| Query efficiency | Constant-query selected-member/profile paths; >=50% p95 reduction where fan-out is removed |
| Cold leaderboard | Initial target p95 <250 ms for bounded top-N published reads |
| Cache memory | Explicit byte/entry cap with no unbounded growth |
| Process behavior | Worker events reach API WebSocket/cache consumers across restarts |
| Startup | One migration owner with bounded locks/timeouts |

## Explicitly rejected shortcuts

- Do not serve active snapshot state while public reads are frozen.
- Do not rely on cache warmth as the correctness boundary.
- Do not increase thread-pool minimums instead of removing synchronous DB work.
- Do not keep Docker host control in the public API for convenience.
- Do not introduce a second ad hoc message bus when PostgreSQL outbox/notify can
  prove the process boundary first.
