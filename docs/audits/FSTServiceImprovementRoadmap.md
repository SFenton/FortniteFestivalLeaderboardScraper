# FSTService Improvement Roadmap

**Audit date:** 2026-07-10  
**Container:** `fstservice`  
**Mode:** Read-only correctness, API, persistence, security, and reliability audit  
**Implementation status:** No service, database, configuration, or deployment
changes were made during this audit.

## Autonomous execution update — 2026-07-10

### SERVICE-0.3 token-backed diagnostics

**Decision:** Accepted and deployed.

- Removed `/api/diag/events` and `/api/diag/leaderboard`; neither anonymous nor
  authenticated callers can trigger an Epic request through the service token.
- Protected `/api/diag/inflight` with API-key authorization and the protected
  rate policy.
- Added an explicit `/api/{**path}` 404 fallback so retired/misspelled API
  routes cannot be masked by the embedded SPA shell with HTTP 200.
- Targeted endpoint-metadata coverage passes, and the production service/web
  paths return 404 for both removed routes and 401 for anonymous inflight
  diagnostics.
- `fstservice`, `festivalweb`, `fstworker`, and `fst-postgres` remained healthy
  through the service-only deployment while scrape 1229 continued.
- A paced 100-request `/api/service-info` sample completed entirely with HTTP
  200 at 1.596 ms p50, 1.861 ms p95, and 1.942 ms p99. An intentionally rejected
  unpaced 200-request probe hit the existing 100-request/second policy; the
  policy was not widened.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/roadmap-20260710T2105Z/service-0.3/`

### SERVICE-0.4 Docker host control

**Decision:** Accepted and deployed.

- Production and repository compose definitions now mount
  `/var/run/docker.sock` and add the Docker group only on `fstworker`.
- API/frontend-only roles resolve a rejecting `DisabledProxyContainerRecycler`;
  worker roles retain `GluetunContainerRecycler`.
- Rendered production compose has only the FST data bind on `fstservice`, with
  no supplemental group. `docker inspect` and an in-container filesystem check
  confirm the socket is absent.
- `fstworker` retained the socket and group 984. A read-only Docker Engine
  `/_ping` from inside the worker returned `OK`, proving the narrow control path
  remained usable without restarting a proxy during active scrape 1229.
- The service-only deployment preserved healthy service, web, worker, and
  PostgreSQL containers plus `/readyz`, the web shell, and `/api/service-info`.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/roadmap-20260710T2105Z/service-0.4/`

### SERVICE-0.5 tracked credential defaults

**Decision:** Mixed — code/config cleanup accepted and deployed; Epic client
credential rotation is hard-blocked by operator/provider access.

- Removed the tracked API-key default and PostgreSQL password default.
- Removed the tracked Epic client ID/secret fallback. `EpicAuthService` now
  requires `EPIC_CLIENT_ID` and `EPIC_CLIENT_SECRET`; API options validate a
  non-empty `Api:ApiKey` at startup.
- Compose/example files now use empty placeholders or fail-closed required
  interpolation, including `PG_PASSWORD` and `WEBAPP_PASSWORD`.
- Added a value-redacting repository scanner, focused tests, and a dedicated
  pull-request/push workflow. The scanner passes on tracked and candidate files.
- Production already supplies every required value. The service-only
  deployment started successfully and preserved all public/container health.
- The production API key does not match the removed tracked default. The
  production Epic client ID and secret do match the removed defaults, so their
  provider-side rotation cannot be completed without operator-owned Epic
  credential access. Values were not logged, copied, or written to artifacts.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/roadmap-20260710T2105Z/service-0.5/`

#### Epic client credential rotation runbook

The remaining provider action is prepared but must be executed by an operator
with Epic client-management access:

1. Create replacement client credentials with the same required grants and
   entitlement. Do not paste values into chat, shell arguments, reports, or
   tracked files.
2. Wait for the active scrape, post-process, publication, and unfreeze boundary;
   hold `fstworker` before the next scrape.
3. Update only `EPIC_CLIENT_ID` and `EPIC_CLIENT_SECRET` in the ignored
   production `.env`. Keep a non-logged rollback copy in operator-controlled
   secret storage.
4. Recreate `fstservice` first. Verify all expected containers, `/readyz`, the
   festivalweb shell, `/api/service-info`, catalog freshness, and zero auth
   failures.
5. Recreate/start `fstworker`; immediately roll back if API/web health or Epic
   authentication regresses.
6. Run one complete scrape/publication window at the unchanged global request
   budget and verify normal scope/count/fingerprint/publication evidence.
7. Revoke the old Epic client credentials only after the candidate scrape and
   rollback proof pass.

The repository and production environment are ready for this sequence. The
provider credential creation/revocation itself remains the exact hard gate.

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

**Execution evidence - accepted 2026-07-11**

- `Features__UsePublishedScopeSources=true` is enabled only on `fstservice`;
  worker post-process reads remain on active candidate state.
- Current leaderboard rows, player profiles, population totals, member filters,
  projection readiness, overlays/empty scopes, and solo exports now resolve
  from the current per-scope mapping. Matching projections stay on the fast
  path; only stale/failed scopes fall back to mapped physical rows.
- With all `6,129` active scopes on scrape `1230`, publication frozen, and the
  mapped pointer still on `1229`, a service restart plus unique cold miss
  returned `23/23` rows exactly equal to direct mapped-source SQL.
- The mapped export returned byte-normalized workbook parity and improved the
  matched warm sample from `0.698s` to `0.470s`.
- All `525` one-minute candidate monitor ticks kept service readiness,
  festivalweb shell, and `/api/service-info` healthy. This includes all nine
  ticks between scrape completion and atomic publication.
- Rollback is `Features__UsePublishedScopeSources=false`; schema and mappings
  remain diagnostic-only.

**SERVICE follow-through execution evidence - accepted 2026-07-13**

- Enabled readers now build from one shared current-publication source CTE;
  their enabled SQL contains no active-snapshot branch.
- Projection fast paths require source and projection-generation parity in the
  same query. Stale/mixed projections fall back to the mapped physical source
  plus overlay instead of returning candidate rows.
- The per-route reported total and `PlayerDataExportService` use the same
  published resolver. A canary exposed a capped raw mapping total
  (`10,042` versus the published route floor `374,853`); the repaired candidate
  snapshots/repairs the population floor only at a clean publication boundary.
  The prior active resolver remains available only when
  `Features__UsePublishedScopeSources=false`.
- A forced service restart while scrape `1232` was frozen on published `1231`
  returned the exact baseline leaderboard fingerprint. Published solo workbook
  sections also matched byte-normalized baseline content exactly.
- The first frozen full export exposed a pre-existing unbounded published-band
  scan. An identity prefilter was rejected because it omitted known teams; the
  accepted indexed `band_members` prefilter returned HTTP `200` in `1.163s`
  while preserving the published solo workbook fingerprint.
- On the same published scrape `1235`, rollback image
  `fstservice:pg1-0f3a37f2` and the candidate returned exact route, solo-export,
  and full-export fingerprints. Matched latency was `14.1ms -> 17.4ms` for the
  sample route and `0.765s -> 0.827s` for the export.
- Focused validation passed `356/356` PostgreSQL/unit/API tests, including
  older physical sources, mixed source IDs, explicit empty scopes, overlays,
  generation mismatch, failed in-progress state, forced frozen cold misses,
  export contents, and status transitions.
- The complete accepted scrape `1235` published `6,138/6,138` mappings:
  `6,096` snapshots, `42` explicit empty scopes, `39,578,699` physical rows,
  and zero incomplete/missing publication metadata.
- Production evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/service-0.1-0.2-published-status-20260711T143501Z`.

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

**Execution evidence - accepted 2026-07-13**

- `/api/service-info` reads latest scrape, published scrape, freeze state, and
  durable worker activity from one PostgreSQL statement.
- The response now exposes active/published IDs, published/frozen timestamps,
  frozen scrape/reason, network/post-process/publication phases, and
  `failed`/`stalled` states without reporting idle during active work.
- Worker heartbeats refresh long-operation timestamps; failed passes retain a
  failed `scrape.pass` operation and release public reads back to the prior
  published generation.
- Next schedule derives from publication or failed-pass completion and is
  suppressed while active, frozen, stalled, offline, stale, or already overdue.
- Live transitions reported `Scraping`/`scrape`, `PostScrapeEnrichment`/
  `post-process`, `Publishing`/`publish`, and post-publication `Finalizing`
  without an idle or stale-schedule contradiction. The frozen timestamp stayed
  fixed and `frozenScrapeId` remained `1231` until atomic promotion to `1235`.
- Scrape `1232` failed closed on `16` incomplete scopes and scrape `1234` on
  `3`; both retained published `1231`, unfroze safely, and reported `failed`.
  Scrape `1233` isolated a worker legacy-path performance regression; service
  safety checks were scoped back to published mode before the accepted retry.
- All `604` one-minute monitor ticks for accepted scrape `1235` kept
  `/readyz`, festivalweb shell, and `/api/service-info` healthy. Rankings phase
  time was `17,032,363ms -> 17,926,147ms` (`+5.25%`); peak Postgres/worker
  memory was `15.20/8.41GiB`.
- Repeated failed provider-coverage windows and their rollback evidence reduced
  free space to `114,964,156,416` bytes (`3.82` projected days). Scraping
  remains capacity-guard allowed, but storage/reclaim work is now the immediate
  operational dependency.
- CI-equivalent line coverage is `94.24%`. The full run had `1,950/1,953`
  passing: two known pre-existing fixtures and one load-sensitive timeout that
  passed immediately in isolation. Settings status tests passed `56/56`.

**WORKER-0A status follow-through - code accepted, live promotion hard-blocked**

- Candidate scrape state is now durable in `scrape_log`, including failure
  time, phase, and message. `/api/service-info` can therefore report a failed
  candidate even after the worker's last-operation slot advances or the worker
  restarts.
- Published scrapes expose best-effort phase warning count/names. Those
  warnings remain distinct from candidate failure: public data is current and
  published, while retryable cleanup/notification/enrichment failures stay
  visible.
- Publication-critical phase, writer, or scope-manifest failure leaves
  `publishedScrapeId` and all mapped reads on the prior generation. A completed
  but not yet published row can be marked failed, and a failed row cannot later
  be completed or published accidentally.
- Candidate service restarts kept `/readyz`, the festivalweb shell, and
  `/api/service-info` healthy. Failed candidate IDs `1237`-`1242` remained
  visibly failed while `publishedScrapeId=1236`; public reads were unfrozen
  after each stopped/rejected attempt.
- The final production rollback restored `fstservice:service02-824415e9`.
  Representative solo and band leaderboard bodies exactly matched the
  pre-candidate baseline; the song route changed only with the normal item-shop
  refresh. Published-source mapping remained `6,138` scopes /
  `39,588,650` rows.
- Live promotion is hard-blocked because all refreshed PIA exits returned CDN
  blocks/timeouts and no complete candidate scrape could begin. The status/API
  code remains committed behind worker enforcement flags for the next valid
  provider window.
- The final guarded retry `1262` later proved service availability throughout
  a complete network and writer window: all `604` captured one-minute checks
  kept `/readyz` and festivalweb HTTP `200`, while `8,208/8,208` manifests,
  zero writer failures, and zero critical-phase failures were recorded.
- Exact rollback parity passed for `12/13` normalized public surfaces. The
  remaining `/api/rankings/bands/{bandType}/{teamKey}/songs` response changed
  only rank, population, and percentile values because
  `GetBandSongPerformanceExtremes` falls back to live `band_entries` when the
  optional band-song ranking projection is stale. Other band routes already
  retained published-generation parity. This endpoint-specific published-read
  gap is now a hard correctness gate; no broad data rewrite or semantic
  weakening was used to mask it.
- Candidate `1262` was rejected before rankings/publication on capacity and
  finalized failed. At that decision, production remained on
  `fstservice:service02-824415e9`/`festivalweb:service02-824415e9`, published
  `1236` was unfrozen, and the worker was held on its validated rollback image.
  Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/worker0a-final-live-ab-20260715T151317Z`.
- The post-`1262` capacity phase reclaimed `45,547,339,776` database bytes by
  retiring non-constraint `ix_rh_latest` after moving its ranking latest-row
  owner to the primary key. Final free space is `76,804,927,488`, and the
  measured scrape gate passes with `31,656,701,952` bytes of margin.
- Service/web/Postgres remained healthy through all `90` reclaim monitor
  samples, and `12/12` public fingerprints matched before, after, and on
  repeat. The worker remains held on
  `fstservice:post1262-capacity-7050ee93`; only the parent-owned band
  best/worst songs published-read parity gap still blocks the next live A/B.
  Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/post1262-capacity-recovery-20260716T021005Z`.
- Scrape `1263` later reached rankings after a complete strict network/writer
  pass. The capacity watchdog stopped its recovery worker at
  `14,871,388,160` free bytes; Docker exit `137` was not an OOM, and the
  forced stop left the scrape/freeze/worker ledgers stale until the
  2026-07-25 incident transaction marked it failed and restored published
  `1236`.
- Unfreezing exposed a second correctness boundary: current player ranking,
  rank-history, and export fingerprints contained failed-candidate derived
  writes even though mapped solo leaderboard and published band ranking
  fingerprints remained exact. Commits `03edc85b` and `633e7583` therefore
  add a durable failed-candidate isolation marker. With the database ledger
  unfrozen, mapped solo leaderboards remain available; unversioned derived
  cache misses and exports fail closed until a later successful publication
  advances beyond the abandoned candidate.
- Production now runs
  `fstservice:failed-candidate-isolation-633e7583`. `/readyz`, festivalweb,
  PostgreSQL, `/api/service-info`, and the mapped published leaderboard are
  healthy. `/api/service-info` reports `1263` failed, worker offline, and
  `publicReadsFrozen=false`; isolated routes carry published-read headers or
  return stable `503`. Focused publication/status/band tests pass `68/68`;
  the full service run passed `2,058/2,063`, with four deterministic unrelated
  baseline failures and one load-sensitive failure that passed alone.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/stale-scrape-1263-recovery-20260725T153938Z`.
- The subsequent residual capacity phase changed no public read contract.
  Mapped leaderboard output stayed byte-exact HTTP `200`; player ranking,
  history, export, composite/band ranking, and band-song routes stayed on the
  same stable failed-candidate HTTP `503`. The service image remained
  `fstservice:failed-candidate-isolation-633e7583`; commit `8db72081` only
  prevents future schema/ranking rebuilds from recreating retired secondary
  indexes. Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/fst-residual-capacity-20260725T161042Z`.
- The LOGICAL-RETIRE readiness phase proved that public service reads have no
  logical-shadow owner. A published Solo Bass slice matched mapped physical
  snapshot `1236` exactly while stale logical current rows containing failed
  scrape `1237` differed. Repository configuration now defaults the writer
  off and startup validation rejects accidental enablement. The target tables
  remain intact because no disabled-writer scrape has completed global
  publication; the destructive A/B gate is still open. Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/logical-retire-20260725T2306Z`.
- SOLO-DYNAMIC-AB classified every published solo read. The accepted
  service-side candidate keeps a compact complete projection for deep/account
  reads, adds bounded generation-hot coverage for top/leeway/registered rows,
  and leaves exports/totals on their mapped physical/source-metadata paths.
  A default-off stored-rank offset flag preserves exact filtered ranks while
  removing full window re-sorts. In 240-pair A/Bs it reduced filtered-player
  p95 from `94.678` to `17.858 ms` at c1 and `190.519` to `59.291 ms` at c8;
  filtered-top p95 also improved at both concurrencies. No production cutover
  occurred because failed-candidate isolation prevents a complete player/export
  API A/B and current DB margin cannot host the compact shadow. Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/solo-dynamic-ab-20260725T2346Z`.
- SNAPSHOT-REUSE changed no public service read contract. The candidate now
  reuses only the validated published physical source; changed, incomplete,
  coverage-changed, missing-source, and failed-active cases write new rows.
  Existing mixed-source, explicit-empty, overlay, frozen cold-miss,
  projection-generation, and workbook tests remain exact.
- Baseline public capture kept the mapped leaderboard HTTP `200` and the
  failed-candidate-isolated ranking/history/export/band-song surfaces on their
  existing stable HTTP `503`. The scrape evidence tool now hashes expected
  non-2xx fail-closed bodies instead of aborting on them.
- No service or worker image was deployed because the Epic refresh canary
  failed with `invalid_refresh_token`. Service, web, Postgres, `/readyz`,
  shell, and `/api/service-info` remained healthy; published `1236` remained
  unfrozen. Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-reuse-20260726T010701Z`.
- The resumed authenticated live attempt ran worker candidate `1264` while the
  service stayed on `fstservice:failed-candidate-isolation-633e7583`.
  Every one-minute public-health sample passed through `8,232/8,232`
  manifests and writer drain. The post-writer capacity guard stopped the
  worker before rankings/publication, so the service read contract did not
  advance.
- Rollback kept published `1236` unfrozen and candidate `1264` owns zero
  published-source rows. All 13 leaderboard/export/player/rank/history/band
  fingerprints matched baseline exactly. Final idle p95 changed
  `2.012 -> 2.046 ms` for service-info, `6.482 -> 7.031 ms` for the mapped
  leaderboard, and `1.421 -> 1.323 ms` for the stable fail-closed rankings
  response. Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-reuse-live-ab-20260726T032124Z`.

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
