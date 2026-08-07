# Published Physical Snapshot Reuse Runbook

## Current decision

**Tier:** code/readiness accepted; capacity-ready retry rejected and reverted;
the production flag remains off.

Scrape `1278` subsequently published successfully with snapshot reuse still
off, but its monolithic final publication transaction held the global
exclusive advisory lock for about three minutes and repeatedly timed out
publication-bound REST reads. Controlled split-publication scrape `1279`
published successfully as publication `25`: preparation stayed outside the
exclusive lock and final exclusive hold was `2.886s`. The live probe exposed
missing production `public-route:` aliases, so the assumed cached hit returned
the same bounded `503 Retry-After: 1` as the forced miss. Canonical ranking
aliases and the unchanged-catalog lock repair are deployed on
`fstservice:postb-4cab6c08`; one more controlled publication must prove cached
`200` continuity before snapshot reuse runs. Snapshot reuse remains off. This
availability gate is independent of snapshot-reuse data correctness and
capacity:

- no normal full scrape or snapshot-reuse retry is authorized on the old
  publication path;
- the next permitted full scrape is the repaired cached-hit B run using the
  split prepare/commit path with snapshot reuse and unrelated maintenance
  disabled;
- candidate preparation must keep the old complete publication readable;
- exact generation-cache hits must remain HTTP `200` through commit intent;
  a forced miss may return only bounded `503 Retry-After: 1`, never a
  30-second timeout;
- pinning-disabled and pinning-enabled exact cache hits must both remain
  generation-exact; an empty generation may not inherit a nonempty legacy
  compatibility cache;
- every commit intent must stamp a fresh dedicated started-at, heartbeat, and
  owner token rather than inherit scrape-freeze age; the owner heartbeats
  across try-lock retries and cannot be replaced while fresh;
- every noncommitted path, including a commit-time same-scrape
  `AlreadyPublished` race, must restore its pre-intent freeze state, and
  restart/read-gate reconciliation may clear an abandoned intent only after
  its dedicated heartbeat is stale and no active exclusive commit exists;
- failed-candidate status must become durable before advisory drain; a hung
  shared reader may force bounded shared-lock recovery, but must never leave a
  failed working pointer that wedges the next scrape;
- if durable failure recording itself fails, the worker must retain a
  fail-closed `publication-isolation-pending` freeze and frozen process caches;
  no generic failure-finally path may unfreeze before read-gate/startup
  reconciliation establishes the failed-candidate marker;
- startup, stale recovery, failed cleanup, post-commit cleanup, and the next
  preparation must run the exact-name band artifact sweeper, preserving only
  current/previous/active-working publication tables and retrying safely after
  lock timeout;
- post-commit cleanup must acquire the exact current publication cache-build
  advisory key before deleting or truncating staging; an active rebuild causes
  cleanup deferral, never an empty live-generation swap;
- startup must fail and clean a prepared working generation abandoned before
  commit intent unless a live scrape/publication heartbeat or explicit
  deferred marker proves ownership;
- pending isolation must target the frozen scrape ID exactly, confirm its
  durable failed state before unfreezing, and never fail a newer mismatched
  working generation;
- advisory-busy outcomes may retry the same preparation before cutover starts.
  A final-cutover deadline must immediately preserve a deferred ready
  generation rather than reset another exclusive budget or classify
  contention as data failure;
- ordinary publication read leases must expire server-side within 30 seconds,
  with an explicit 180-second export allowance; no shared lease is unbounded;
- HTTP read-gate checks may trigger only TTL-limited single-flight background
  recovery. Reconciliation, DDL, and sweeping must remain off the request
  critical section;
- preparation must enter its shared-lock phase with finite server lock,
  statement, and transaction timeouts;
- deferred/pending/commit recovery freeze reasons must reject generic
  `ScrapeStarting` and unfreeze overwrites. Every worker pass must load and
  retry a persisted deferred preparation before authentication, catalog work,
  or new scrape allocation; no replacement scrape may orphan that ready
  generation;
- deferred publication recovery must run before improvement-notification
  recovery, Epic authentication, API-only waiting, and every scrape-loop
  allocation. Continued contention must back off and retry without escaping
  the worker or stopping the host;
- only proven deferred-preparation metadata corruption may fail the candidate;
  transient database/pool/schema lookup errors must preserve the ready
  generation and deferred freeze for retry;
- shutdown cancellation during either normal or deferred contention retry must
  preserve the ready generation and classify the outcome as deferral, never
  failed-candidate isolation;
- failure to persist `publication-commit-deferred` must remain nonfatal and
  install an in-process fail-closed gate until durable recovery succeeds;
- one owner/heartbeat commit-intent lease must span all contention attempts;
  retry gaps may never restore permissive `publish` reads;
- if owner-aware deferred transition fails, retain and heartbeat the durable
  commit-pending latch for separate API processes while background recovery
  retries; worker-local fail-closed state is additive only;
- notification-gate DB probes and reconciliation must remain inside bounded
  retry/backoff handling, with only host cancellation allowed to propagate;
- isolation pending on a scrape that is already current/published, or safely
  retained after a later publication, must clear only the stale latch and
  never mark published history failed;
- once atomic commit succeeds, cleanup, status, and WebSocket/broadcast
  failures must be treated as post-commit operational degradation, never
  failed-candidate isolation;
- nonterminal commit failures must retain their owner lease through durable
  isolation. Simultaneous failure-record and pending-transition failures may
  add worker-local protection, but must never release the DB-visible
  commit-pending latch seen by separate API processes;
- deferred lookup handlers must not catch commit execution failures. The
  owned lease must be handled at the commit boundary and transferred into
  confirmed isolation or preserved pending-isolation recovery;
- the cumulative final exclusive cutover must be `<=5s`, enforced by
  PostgreSQL transaction timeout across all statements/attempts, with no ungranted
  publication waiter older than one second and exact pointer/cache/source/
  band/notification/WebSocket parity;
- current plus previous cache, catalog, and band rollback objects remain
  retained until the post-publication parity window passes.

Scrape `1267` subsequently published successfully with snapshot reuse still
off. It completed `8,232/8,232` manifests and all publication-critical phases,
then atomically published/unfroze `1267` with exact public and logical-shadow
parity. This clears the separate logical-shadow prerequisite but does not
promote or re-enable snapshot reuse. The flag remains rejected/default-off.

The publishing run used the accepted proxy throughput settings only for its
single candidate window. Network plus writer drain was `5:02:22.661`, `8.79%`
faster than scrape `1265` and `2.51%` slower than scrape `1266`. Minimum free
space was `18,203,201,536`; final measured free space was `41,145,516,032`,
so another full scrape is blocked and the worker remains held.

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/scrape-1267-guarded-publication-20260727T201218Z`.

`Features:SkipUnchangedPhysicalLeaderboardSnapshots` remains default-off. The
refreshed Epic credential, authenticated `25/25` direct/PIA canary, canonical
PIA guard, current-source image, public health, and both start guards passed.
Scrape `1265` completed `8,232/8,232` manifests and all writers, then passed
the post-writer guard at `48,613,908,480` free bytes. It completed four
publication-critical phases, including the full band projection refresh, but
the 60-second safety monitor stopped ranking snapshots when free space reached
`13,144,125,440`, below the declared `14,571,150,203`-byte floor.

Scrape `1265` was reconciled failed at
`capacity_during_ranking_snapshots`. It owns zero published-source rows,
published `1236` remains authoritative and unfrozen, all 13 rollback
route/export/history/ranking fingerprints are exact, and production service,
worker, flag, and compose configuration were restored. After transient ranking
files and three post-run autovacuums completed, free space stabilized at about
`48.78 GB`; the nominal guards pass with only `3.63/4.38 GB` of
baseline/candidate margin. Scheduling remains held because no successful
post-publish guard/parity window exists and this run breached its declared
capacity floor despite passing the nominal preflight model.

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-reuse-live-ab-20260726T110731Z`.

## 2026-07-27 post-1265 low-scratch recovery

No additional scrape ran and snapshot reuse was not re-enabled. The recovery
dropped only four dormant non-constraint secondary index trees from the
retired logical shadow while preserving every logical row and primary-key
constraint.

| Gate | Result |
|---|---|
| Database reclaim | `18,289,049,600` bytes |
| Immediate filesystem free | `67,148,181,504` bytes |
| Corrected start requirement | `60,392,999,803` bytes |
| Corrected margin | about `6.75 GB` |
| Public/logical parity | `13/13` public fingerprints exact; bounded current/version fingerprints exact |
| Logical data | `39,820,273` current rows and `194,171,215` version rows retained; 20 primary-key constraints retained |
| Worker/candidate | Worker held; snapshot reuse remains rejected, reverted, and default-off |

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/post-scrape-1265-capacity-recovery-20260727T0011Z`.

## 2026-07-26 same-drive capacity recovery

The separate BAND-SONG-PROJECTION phase retired only stale optional derived
data. It truncated four `band_song_team_rankings*` tables without `CASCADE`
after successful-scrape writer-disable evidence, live published-read parity,
two rolled-back production proofs, a deterministic rebuild proof, and an exact
same-drive archive.

| Gate | Result |
|---|---|
| Database reclaim | `28,315,533,312` bytes |
| Exact archive retained | `2,184,507,134` bytes, PostgreSQL custom/zstd, full read validation passed |
| Final free space | about `58.97 GB` |
| Baseline scrape margin | `13,822,787,584` bytes |
| SNAPSHOT-REUSE estimated margin | `14,576,143,227` bytes |
| Public/service health | Postgres, service, web, `/readyz`, shell, and `/api/service-info` healthy; `24/24` band route fingerprints exact |
| Worker | Held; not started |

Capacity now permits another guarded candidate start, but this does not change
the prior rejection, enable the default-off flag, or prove publication parity.
Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/band-song-projection-retirement-20260726T103231Z`.

## 2026-07-26 capacity-ready retry

| Gate | Result |
|---|---|
| Runtime / image | `gpt-5.6-sol`, max, long context; `fstservice:snapshot-reuse-efdd70b8` built from `FSTService/Dockerfile` |
| Auth / proxy | Refresh persisted mode `0600`; `25/25` direct and `25/25` PIA Epic responses exact; 25 healthy unique PIA exits at 400 aggregate RPS |
| Validation | `231/231` candidate-focused tests, Release build, source-image build, and exact three-setting production config diff passed |
| Start / post-writer capacity | `58,966,065,152` / `48,613,908,480` free bytes; both baseline and candidate guards passed |
| Manifests / writers | `8,232/8,232` complete; `59,081,828` entries; `592,506` pages; zero parse, retry-exhausted, writer, or critical failures |
| Best-effort phases | Registered-user refresh, registered-player band discovery, and registered-band targeted processing timed out at their declared five-minute bounds |
| Reuse | `273` scopes / `218,892` rows; zero reused scope had scrape-`1265` physical rows |
| Estimated benefit | `112,343,764` physical bytes and about `160,525,751` WAL bytes avoided; snapshot relations still grew `19,439,173,632` bytes |
| Network / resources | About `19,890 s`, `-14.4%` versus `1264`; 694 one-minute samples, zero public-health failures; peak worker/Postgres RSS about `7.87/13.66 GiB` |
| Post-process | Band maintenance completed `29,145/29,145` selected scopes and `32,651,304` rows with zero failures in `5:16:46.669` |
| Capacity stop | Ranking snapshots reached `13,144,125,440` free bytes, below the `14,571,150,203` safety floor; worker stopped before global publication |
| Publication / rollback | No candidate mappings; published `1236` unfrozen; `13/13` rollback fingerprints exact; baseline images/config restored |
| Stabilized capacity | `48,776,298,496` free; nominal baseline/candidate margins about `3.63/4.38 GB`; worker held because the run proved that model insufficient through publication |

The retry proves unchanged-scope physical skipping again, but it does not clear
publication/source-map/API parity and cannot be promoted. The run also showed
material ranking-phase variance while the filesystem was at 100% usage; this
is additional rejection evidence, not a reason to weaken the capacity floor.
Observed peak consumption was `45,821,849,600` bytes, which exceeded the
candidate estimate by `1,427,020,667` bytes (`3.21%`). Preserving the same
safety floor requires at least `60,392,999,803` free bytes at start. Current
post-recovery free space passes that requirement with about `6.75 GB` of
margin. This clears the corrected capacity shortfall only; it does not promote
the rejected candidate or authorize an automatic retry.

## Candidate contract

The flag becomes effective only when all existing correctness controls are
also enabled:

- `Features:WritePublishedScopeSources=true`;
- `Features:EnforceScopeCompletenessManifests=true`;
- `Features:UseLeaderboardScopeFingerprints=true`.

For each non-empty solo scope, the writer:

1. receives the completed manifest before the bounded online write is queued;
2. computes the current deduplicated physical content fingerprint;
3. requires a complete current manifest and exact current/published content
   and row-count parity;
4. requires exact coverage-fingerprint parity, except for the one-way upgrade
   from the legacy 32-character coverage fingerprint on published `1236` to a
   complete 64-character manifest fingerprint;
5. verifies the selected published physical source still exists with the exact
   mapped row count;
6. skips current-scrape physical rows only after all checks pass;
7. pins `leaderboard_snapshot_state` to the validated published source, never
   to a newer failed or merely active source.

Changed, new, incomplete, coverage-changed, missing-source, or ambiguous scopes
write a new physical snapshot. Empty scopes retain explicit-empty mapping
semantics. Publication still validates every expected mapping and promotes the
scope map, fingerprints, band/cache state, and global pointer atomically.

## 2026-07-26 preflight

| Gate | Result |
|---|---|
| Runtime | `gpt-5.6-sol`, reasoning `max`, context `long_context` |
| Production/public health | Postgres, service, web, `/readyz`, shell, service-info, and mapped leaderboard healthy |
| Publication | `1236`, unfrozen; latest `1263` remains failed and isolated |
| DB activity | No active scrape, ungranted lock, long query, vacuum, index build, or rewrite |
| Same-drive capacity | `48,960,053,248` free bytes |
| Measured baseline requirement | `45,148,225,536`; margin `3,811,827,712` |
| Candidate estimate | `44,394,828,933`; margin about `4.565 GB` |
| Estimated reuse | `1,203` scopes / `3,371,702` rows / `753,396,603` physical bytes |
| Published physical sources | `6,096/6,096` exact counts; `39,588,650` rows; `42` explicit empty scopes |
| Proxy guard | `25/25` healthy unique PIA exits, 30 canonical services, 400 aggregate RPS, 2 RPS and one in-flight per effective exit |
| Worker auth | **Blocked:** Epic returned `invalid_refresh_token`; interactive device login is required |
| Low-rate provider probe | Client-token control reached all 26 direct/PIA paths, but all returned JSON auth/entitlement responses; this is not a valid worker-user canary |

The estimate uses exact published-`1236` versus complete-`1263`
content/row parity and measured `1236 -> 1262` per-instrument snapshot relation
growth. It is a capacity estimate, not promotion evidence.

## 2026-07-26 live A/B

| Gate | Result |
|---|---|
| Runtime | `gpt-5.6-sol`, reasoning `max`, context `long_context` |
| Auth persistence | Passed twice on `/mnt/docker-storage`; rotated credential remained mode `0600` |
| Authenticated provider canary | `25/25` direct and `25/25` PIA responses were valid Epic JSON; all 25 entry arrays and structures matched exactly |
| Proxy/compose guard | 30 canonical services, 25 healthy unique PIA exits, 400 aggregate RPS, 2 RPS and one in-flight per effective exit; no AirVPN/direct fallback |
| Image ancestry | Initial production-wrapper build was rejected before scrape allocation when it inherited stale `ghcr.io` code and attempted retired `ix_rh_latest`; the exact backend was cancelled, free space and DB size recovered, and the image was rebuilt with `FSTService/Dockerfile` from `919daa32` |
| Corrected candidate | `fstservice:snapshot-reuse-919daa32`, image `sha256:470d5a5d2bf7...`, binary contained snapshot-reuse/current ownership markers and no `ix_rh_latest` string |
| Current validation | `300/300` targeted snapshot/published-source/export/config tests passed; Release and corrected source-image builds passed |
| Start capacity | `52,199,215,104` free bytes; both initial scrape guards passed with severe alert |
| Scrape | `1264`; `8,232/8,232` complete manifests, `59,077,331` reported entries, `592,460` pages, zero writer failures |
| Scope behavior | `5,815` changed, `36` new, `281` unchanged reusable, and `42` explicit-empty solo scopes |
| Physical skip proof | `219,427` exact rows avoided; zero unchanged scope had a scrape-`1264` physical row |
| Storage benefit | `15,552,274,432` snapshot-relation bytes still grew; actual-run calibration estimates only `78,765,704` physical bytes avoided |
| WAL/temp | `97,876,358,577` WAL bytes and zero temp-byte growth through the stop; approximate avoided-WAL attribution is `166,448,926` bytes |
| Network/writer performance | About `23,247 s`, versus `22,326.583 s` for candidate `1262` (`+4.1%`); 30.7 RPS, 205 retries, 90.4 GB received |
| Resources/public health | 379 one-minute samples, zero health failures/locks/long queries; peak worker/Postgres RSS about `2.76/8.63 GiB`; minimum free space `23,176,040,448` during band flush |
| Post-writer capacity | **Blocked:** `32,390,148,096` free, below both measured requirements |
| Publication | None; `1264` owns zero published-source rows, `1236` remains unfrozen |
| Rollback parity | All 13 route/export/history/ranking fingerprints matched baseline exactly; final stable leaderboard p95 changed `6.482 -> 7.031 ms` (`+8.46%`) |
| Final capacity | `32,725,393,408` free; both baseline and candidate scrape guards block; worker remains held |

The default-off code remains a valid correctness candidate, but this observation
did not produce enough unchanged data to make the storage benefit meaningful
against current full-scrape growth. Do not promote or rerun it until the
post-writer capacity gate can pass on the FST drive.

## Validation completed

- `186/186` focused writer/orchestrator tests passed, including bounded-online
  reuse and legacy-coverage upgrade.
- `317/317` PostgreSQL/API/projection/export tests passed.
- The full service run passed `2,068/2,072`; all four failures are documented
  pre-existing baseline fixtures outside SNAPSHOT-REUSE.
- Release build passed with zero errors.
- The evidence collector now retains expected fail-closed HTTP `503` bodies
  instead of aborting on them.

## Resume procedure

1. Keep the worker held. The corrected capacity guard now passes, but neither
   that recovery nor repository completion of `PUB-COMMIT-SPLIT` authorizes a
   normal candidate. Require a new complete preflight and the controlled
   bounded-publication B plan before any retry.
2. Preserve all DB/storage work on `/mnt/docker-storage`; do not use alternate
   drive scratch or delete data to force a retry.
3. Build current source with `docker build -f FSTService/Dockerfile ...`.
   `/home/sfenton/Docker/FestivalServiceTracker/Dockerfile.fstservice` is a
   registry-wrapper Dockerfile and must not be labeled as a current-source
   candidate.
4. Before any future retry, revalidate auth persistence, authenticated
   direct/PIA JSON parity, the `25/25` compose guard, public health, locks, and
   both start and post-writer capacity gates.
5. Deploy only the snapshot-reuse flag while preserving all other writer
   behavior. Pair it with exactly one qualified network candidate, add an
   explicit snapshot-reuse data profile to the dual-lane wrapper/guard, and do
   not start until that named profile validates the exact merged run-once
   config.
6. Retain the existing 60-second resource monitor, and add 1 Hz representative
   cached plus forced-miss route probes from publication preparation through
   unfreeze. Capture prepare/drain/exclusive durations, lock rejections,
   relation retries, advisory waiters, pool pressure, pointer transitions,
   WebSocket rotation, and exact rollback objects. Hold the worker before
   another scrape.
7. Accept only with complete manifests, zero writer/critical failures, exact
   source/count/content/coverage/public API/workbook parity, meaningful
   physical/WAL growth reduction, and no sustained regression above 10%.

## Rollback

- Set `Features__SkipUnchangedPhysicalLeaderboardSnapshots=false`.
- Restore/recreate the prior accepted worker image/config.
- Retain the additive source map and manifests for diagnosis.
- A failed candidate must own zero published-source rows and leave published
  `1236` or its later accepted successor authoritative.

## Logical-shadow prerequisite

Scrapes `1264` and `1265` did not clear the prerequisite, but scrape `1267`
did. Its disabled-logical-writer publication completed and passed exact
public/logical parity. No truncate ran in SCRAPE-1267; the separate logical
retirement runbook now owns the parity-authorized maintenance action. The
clearance SHA-256 is
`95c55fb66bb33f07eccbfe01b45957ab6ad96439c2a96f41a16dd8a0519e2ae7`.
