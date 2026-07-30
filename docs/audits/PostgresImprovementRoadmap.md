# PostgreSQL Improvement Roadmap

**Audit date:** 2026-07-10  
**Container:** `fst-postgres`  
**Mode:** Current-system probe plus best-practices/performance/capacity roadmap  
**Implementation status:** The original audit probes were read-only. PG-0 and
PG-1 execution updates below now record accepted tooling, additive schema,
role-specific configuration, container deployment, and live-scrape evidence.

## Autonomous execution update — 2026-07-10

PG-0 is implemented and accepted without production data/schema mutation:

| Task | Decision | Evidence |
|---|---|---|
| PG-0.1 capacity guardrails | Accepted | `tools/postgres-capacity-guard.sh`; commit `8fb707e6`; live 10.47-day projected headroom and verified optional/rewrite refusal paths |
| PG-0.2 authoritative design | Accepted | `docs/database/FSTServiceDatabaseDesign.md`; commit `71c95396`; 269 tables/partitions, 735 indexes, and 273 constraints inventoried |
| PG-0.3 scrape evidence pack | Accepted | `tools/postgres-scrape-evidence.sh`; commit `774d58f5`; checksummed same-drive capture/comparison with route, fingerprint, WAL/temp/checkpoint, phase, and growth evidence |
| PG-0.4 bounded restore drill | Accepted | 32 exact dataset restores plus solo/band API fixture parity; 21,620,913-byte backup; 65,812,147-byte target DB; 9.970-second restore |

The accepted restore artifact is:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/roadmap-20260710T2105Z/bounded-restore-1228/`

Two repair iterations were retained as rejected tooling attempts. The first
used incorrect singleton projection-state order keys. The second completed all
data/API parity but failed final PGDATA-size reporting because of shell
positional-parameter expansion. The third run passed end to end.

Full duplicate restore remains blocked by measured same-drive capacity:
3,934,382,812,204 additional bytes are required for the streaming target/WAL/
safety model versus 314,856,988,672 free at the drill start.

## BAND-HISTORY-COMPACT execution update — 2026-07-28

- Exact frozen v2 history is now `917,793,219` rows /
  `848,759,203,840` bytes: Duets `154,235,944,960`, Trios
  `305,843,961,856`, and Quad `388,775,297,024`.
- The public history/export owner was reconfirmed. The production API read
  statement recorded 474 calls, 7.398 ms mean, and no temp I/O; all nine
  representative overall/combo 30-day/full-range HTTP cases returned 200.
- The accepted v3 schema normalizes team/combo text, uses typed scope IDs and
  `BYTEA(16)` fingerprints, subpartitions by month, and uses one compact
  unique index family for both identity and API date reads.
- A same-drive `4,651,508`-row Duets pilot passed zero bidirectional row
  differences. Compact heap + primary key used `251.98` bytes/row versus
  `716.93` current; matched warm p50/p95 did not regress.
- Retention deletion and Parquet-as-live-source were rejected because all
  history remains served and no runtime rehydration tier exists.
- Lower-scratch calibration proved the `902,775,955,523` figure was dominated
  by the generic seven-day reserve. Chunked copy, explicit checkpoints, and
  deferred one-index-per-leaf construction required `138,328,191,167` bytes
  and passed with `26,497,974,081` bytes of margin.
- Duets v3 was promoted at `215,134,574` rows /
  `52,134,436,864` bytes. The retired `154,235,944,960`-byte v2 leaf was
  detached after exact parity and an 11.827 ms reattach rehearsal, then
  dropped without `CASCADE`.
- Net database reduction is `102,101,475,328` bytes. Repeated `9/9` API
  payload captures remained byte-identical. At this checkpoint Trios and Quad
  remained v2; Trios was promoted in the later clean-rebuild phase below.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/band-history-compact-20260728T100500Z`.
- Execution evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/band-history-compact-lowscratch-20260728T113000Z`.
- Runbook: `docs/database/BandHistoryCompactionRunbook.md`.

## INCOMPLETE-TRIOS-V3-RECLAIM execution update — 2026-07-28

- The manually built compact Trios candidate contained `335,757,940` rows
  across 49 dates from 2026-04-26 through 2026-06-30. Authoritative v2 contains
  `343,275,419` rows across 51 dates through 2026-07-05; the candidate omitted
  July 1 and July 5, totaling `7,517,479` rows.
- Its point partitions had no indexes. The readiness row remained
  `building`, with `row_count=0` and no validation or promotion timestamp.
- Repository, deployed-binary, runtime-config, catalog-dependency, and
  `pg_stat_statements` audits found no production reader or writer. Production
  supports compact reads only for Duets; Trios remained
  `V2NarrowOnly` while band-history writes were disabled.
- Duets v3 remained `ready` and its direct plan used the compact local indexes.
  The live Trios plan used the v2 team/date index. Three sampled Trios payloads
  were byte-identical to the prior v2 baseline, and two public suites were
  `13/13` exact.
- One full v2/v3 checksum attempt was rejected at the bounded 256 MB temp
  limit. It left no query, lock, or health regression. Exact candidate
  manifests, the existing exact v2 date manifest, query-plan ownership, and
  live payload parity replaced that unsafe shape.
- The rollback-only drop rehearsal completed in 0.15 seconds. The committed
  0.70-second transaction removed only the compact Trios parent/four leaves,
  both dictionaries/owned sequences, and the fail-closed `building` state row,
  without `CASCADE`.
- Database size fell from `3,659,422,447,283` to `3,585,943,918,259` bytes,
  an exact `73,478,529,024`-byte reclaim. Stable filesystem free space reached
  about `285.49 GB`.
- Immediate and 60-second public captures were `13/13` exact; all three Trios
  payloads were exact. Published `1267` remained unfrozen, notifications
  remained complete, Duets v3 and Trios v2 remained authoritative, and no
  query, lock, vacuum, or index build remained.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/trios-incomplete-candidate-reclaim-20260728T1914Z`.

## CLEAN-TRIOS-V3 readiness update — 2026-07-29

- After scrape `1268` published/unfroze, the worker remained held and a fresh
  calibrated rewrite guard passed.
- The clean candidate contains all `343,275,419` Trios rows and all 51 dates.
  April, May, June, and July each passed five independent full-row multiset
  hashes against v2.
- All four local unique indexes and the attached parent index are valid and
  ready. Direct overall/combo plans use the compact indexes.
- A separately reversible, default-off `CompactV3TriosReadEnabled` path passed
  focused tests and was deployed. Matched live payloads were exact; every v3
  p95 remained below 5 ms.
- Detach rollback completed in 5.132 ms and metadata-only reattach in 3.306 ms.
  The `305,843,961,856`-byte source was dropped without `CASCADE`.
- Final compact size is `83,664,461,824` bytes, producing
  `222,179,500,032` net database reduction. Stable free space is about
  `483.72 GB`; immediate and 60-second public captures remained `13/13` exact.

## OBSERVATION-RETIRE execution update — 2026-07-28

- Published scrape `1267` proved both `player_score_observations` writers were
  disabled. The table's newest `observed_at` remained
  `2026-07-26T10:17:21.087379Z`, with zero rows in the scrape-to-publication
  window and zero later touches.
- Fresh `pg_stat_statements` classification used only statements beginning
  with `WITH` or `SELECT`; the two observed reads were ownership probes. The
  repository, deployed binary, exports, replay/backfill paths, tools, views,
  routines, triggers, policies, and publications contained no production
  reader. The retained union view is the only database dependency.
- The exact manifest covered `10,167,937` rows /
  `12,682,354,688` bytes, including deterministic fingerprints, samples,
  schema DDL, dependency DDL, and current-baseline-only rehydration limits.
- Two pre-action public suites were `13/13` exact and created no observation
  read/write delta. A rollback-only rehearsal restored all rows, then the
  committed 1.23-second transaction truncated only
  `public.player_score_observations` without `CASCADE`.
- The retained empty relation is `24,576` bytes. Database size fell by
  `12,682,330,112` bytes and stable filesystem free space gained
  `12,680,921,088` bytes to about `212.04 GB`. Schema signatures, both indexes,
  the primary key, union view, and sequence value stayed exact.
- Immediate and 60-second captures were HTTP `200` and `13/13` byte-exact.
  Published `1267` remained unfrozen, notifications remained completed, the
  worker stayed held, and no query, lock, vacuum, or index build remained.
- The preceding drop from about `276.25 GB` to `199.36 GB` free was not
  observation growth: `73,478,529,024` bytes (about `95.56%`) belonged to the
  incomplete v3 Trios candidate, with about `3.31 GB` of WAL growth.
- Legacy `leaderboard_entries_*` remains retained at `36,769,051` rows /
  `40,825,225,216` bytes. Supplemental writes added `970` backfill rows, the
  publication-critical band extractor still reads it, and all 27 bounded
  published-`1267` comparisons differ in count and checksum.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/observation-retirement-20260728T184629Z`.

## SCRAPE-1268 dual-lane qualification update — 2026-07-29

- Scrape `1268` completed and atomically published `6,174` complete solo
  source mappings / `39,944,787` rows. All `8,232` solo+band manifests,
  physical-source checks, writer gates, and 10 publication-critical phases
  passed. Two settled public captures were HTTP `200` and exact `13/13`.
- The notification DB-only surface worked as designed: publication persisted
  a ready zero-scope workset owned by `1268`; no `6,174`-scope fallback
  occurred; one advisory recovery owner ran; player run `166` and band run
  `167` completed every required song/ranking lane; and the marker completed
  `101.76 s` after publication.
- The notification window owned zero Epic sends. It added `266,652,828` WAL
  bytes, zero temp bytes, zero checkpoints, `1,223` inserted rows, `274,338`
  updated rows, and `327` deleted rows. The prior standalone recovery window
  generated about `52.51 GB` WAL; the bounded normal path reduced that by
  about `99.5%`.
- The full scrape added `25,597,894,656` database bytes and consumed
  `29,414,273,024` filesystem free bytes. Final database size is
  `3,611,541,812,915` bytes; final free space is `256,077,381,632` bytes.
  The scrape guard still passes with about `195.68 GB` one-run margin and a
  seven-day alert.
- Full-run WAL was `512,098,894,951` bytes and temp growth was
  `209,783,261,198` bytes, below scrape `1267`'s recorded
  `550,846,974,842` / `349,841,108,977` bytes. Peak worker/Postgres RSS was
  about `7.62 / 8.75 GiB`; no legacy shell/service-info monitor tick,
  deadlock, terminal lock, or maintenance guard failed.
- Promotion is still **iterate**, not accepted, because the shared public
  gate found `13` HTTP `504` plus `20` client-cancelled `499` responses during
  publication. `PublishScrapeRun` took an `ACCESS EXCLUSIVE` cache lock before
  copying and indexing three band ranking snapshots, retaining that lock for
  minutes.
- The prepared next data/query candidate preserves atomic publication but
  performs all long band snapshot copy/index work and fingerprint validation
  before truncating/promoting `api_response_cache`. A concurrency regression
  test holds a band ranking source lock and proves the old public cache remains
  readable while publication waits. Commit `44a1fe9a` is built as
  `fstservice:publication-lock-44a1fe9a` and selected in compose without
  recreating the exited run-once worker. The next live window must pair this
  independently reversible query-order candidate with bounded-only
  `candidate-800-32-6` after c5 safely missed its bounded throughput target at
  `39.314` pages/s. The network wrapper/guard are intentionally unchanged and
  the worker remains held.
- Freshness correction: storage work may hold the worker only through its
  currently active bounded chunk. At the next clean checkpoint it must yield
  to a continuity scrape. If c6 cannot complete one qualified bounded attempt
  promptly, use accepted `candidate-800-32-4`, classify the network lane as an
  accepted-baseline measurement, publish/unfreeze and complete notifications,
  then release the storage owner to resume.
- Other measured regressions remain separate next-candidate evidence:
  `BandMaintenance` was `4:04:02.864` versus `3:12:28.804` on `1267`,
  `ComputeRankings` was `2:49:50.942` versus `1:18:21.810`, and solo
  projection cleanup was `25:15.672` versus `19:57.754`.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/scrape-1268-dual-lane-20260728T184812Z`.

## SCRAPE-1269 freshness recovery update — 2026-07-30

- Storage yielded after its active bounded Quad work reached committed
  checkpoints; the scrape started with no storage query, waiting lock, or
  active index build. At release, the Quad 202604/202605/202606 local unique
  indexes were valid and 202607 plus parent attach/analyze remained resumable.
- Scrape `1269` completed and published `6,174` complete mappings /
  `39,951,796` rows. All `8,232` solo+band manifests, physical-source checks,
  writer gates, and publication-critical phases passed. Two settled public
  suites were HTTP `200` and exact `13/13`.
- The publication cache lock-order repair from `44a1fe9a`, carried by
  `fstservice:band-history-trios-ad015ca7`, is **accepted**. Across `692`
  full-window monitor ticks and nine publication-window ticks there were zero
  public-route failures; festivalweb recorded zero `499` and zero `5xx`.
  Notifications completed `78.59 s` after publication with a persisted
  zero-scope workset.
- The accepted c4 network baseline took `5:01:08.141` through writer drain at
  `32.819` useful pages/s. It emitted `640,250` wire sends, `18,918` blocks
  (`2.955%`), zero `429`/`503`, and `1.0797` amplification. This is baseline
  continuity evidence, not a network promotion; c6 was skipped because it
  could not run promptly after the storage boundary.
- Database size grew by `21,570,781,184` bytes and free space fell by
  `31,926,575,104` bytes from the final preflight snapshot. The terminal parity
  capture had `367,602,806,784` bytes free; the minimum observed during the
  run was `347,991,855,104` bytes. The capacity alert remains active.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/scrape-continuity-recovery-20260729T161535Z`.

### Post-1269 bounded-canary isolation incident

- Two autonomous owners consumed the same boundary from stale local state.
  The continuity owner completed an isolated c6 at `05:59:52 UTC`, rejected it
  on 24/25 invalid payload controls, then began its documented accepted-c4
  fallback at `06:02:07 UTC`. A delayed clearance caused a second owner to
  begin duplicate c6 at `06:02:25 UTC`; the fallback worker became active 12
  seconds later and allocated `1270`. The first c6 is the authoritative
  correctness rejection; the duplicate result is invalid.
- `fstworker` was stopped at `06:04:07 UTC`. Guarded reconciliation required
  published scrape `1269`, running candidate `1270`, zero candidate mappings,
  zero worker queries, zero ungranted/advisory locks, and zero maintenance
  progress before marking `1270` failed with
  `network_canary_concurrent_scrape_abandoned`.
- Published `1269` remained authoritative and unfrozen, notifications remained
  completed, the worker ledger is offline, and public routes stayed HTTP
  `200`. No database candidate or Quad object was mutated by the recovery.
- Future worker-start/cadence owners must fail closed on
  `/home/sfenton/Docker/FestivalServiceTracker/.fst-bounded-network-canary-active.json`;
  freshness does not override an active bounded-canary isolation boundary.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/network-candidate-800-32-6-20260730T060051Z`.

## Executive decision

PostgreSQL remains the correct durable source of truth. The urgent issue is
capacity and write amplification, not platform replacement.

The database is `3,611,541,812,915` bytes on the 3.6 TB FST filesystem, which
is 94% used with about 256.08 GB free. Physical solo snapshots consume about
1.665 TB and add an estimated
14 GB per full scrape. Band v2 history points consume about 799 GB. Together
they account for about 74% of the database. The current scrape also generated
15.39M logical version opens even though 61.03% of observed rows were unchanged.

The accepted order is:

1. Protect publication correctness, capacity, backup, and restore first.
2. Run matched A/B query work on the measured hottest paths.
3. Recover low-scratch disk from proven unused secondary indexes and unowned
   tables.
4. Make physical and logical writes proportional to real semantic changes.
5. Compact/archive history only after manifests, rehydration, and live-scrape
   parity.

## Audit report delivery

This roadmap is accompanied by:

`FST Autonomous Agent: Recap - PostgreSQL Deep Audit · Needs Attention`

Delivery requires rendered HTML/text plus SMTP acceptance, or a recorded SMTP
blocker and exact outbox artifact paths.

## Cross-container publication rollout

Publication correctness is promoted in this exact order:

1. PG-1 adds backward-compatible per-scope published-source schema.
2. Worker dual-writes, validates, and atomically promotes the mapping.
3. Service endpoints and exports consume it behind a rollback flag.
4. Forced frozen cold misses, exports, and a full live shadow scrape prove
   parity.
5. Only then may physical snapshot skipping or old-resolver removal proceed.

## Autonomous execution windows

| Phase/task family | Execution class | Decision window |
|---|---|---|
| PG-0 read-only evidence, manifests, bounded restore | `continuous-safe` | Continue while scrapes run when probes are bounded and live preflight is healthy |
| PG-1 published-source schema/population/read cutover | `full-scrape-ab` | Wait for current publish/unfreeze, hold worker, deploy one coordinated candidate, run one complete scrape, hold and compare parity |
| PG-2 query A/Bs | `continuous-safe` for bounded EXPLAIN/fixture work; `scrape-boundary-deploy` or `full-scrape-ab` for runtime query changes | Use a full scrape when ranking/post-process/WAL/temp/publication can change |
| PG-3 owner cards | `continuous-safe`; proven index drop is `parity-gated-maintenance` | Drop one index only at a clean boundary after parity/recreate proof, then run the owning route/job/scrape validation |
| PG-4 write amplification | `full-scrape-ab` | Exactly one write-path candidate per complete scrape and publication window |
| PG-5 artifact pilots | `continuous-safe` when bounded/read-only; archive/prune is `parity-gated-maintenance` | No source deletion until live parity and rehydration pass |
| PG-6 migration/vacuum/WAL/runtime settings | `full-scrape-ab` for production changes | Worker held for migration/config deploy; compare a complete scrape and recovery window |
| PG-7 archive/destructive reclaim | `parity-gated-maintenance` | Execute only after the accepted full-scrape A/B, then validate and restore normal scraping |

The autonomous skill provides the shared wait-stop-deploy-run-stop-decision
loop. This roadmap supplies the candidate-specific correctness, performance,
headroom, rollback, and maintenance gates.

### Mandatory dual-lane scrape windows - effective 2026-07-28

Every full scrape must pair one independently reversible network candidate
with one independently reversible PostgreSQL/storage/query candidate. This is
an explicit exception to the former one-change-per-scrape rule; it is limited
to one candidate per lane, with separate flags/config, metrics, rollback, and
accept/reject decisions.

The data lane must capture relation growth/reclaim, rows read/written/deleted,
WAL/temp/checkpoint deltas, locks, CPU/memory/IO, query and phase latency, and
published API parity. The shared scrape gate still requires complete scope
manifests, historical correctness, successful ranking/post-process,
publication/unfreeze, notification completion, and public-route health.

Scrape `1268` functionally qualified the notification contract: completion was
`101.76 s` after publication, the persisted workset was empty and bounded,
one recovery owner ran, and the window owned zero Epic sends. The shared
public-route gate failed because the publication transaction locked
`api_response_cache` before long band ranking snapshot copies/index builds.

The next data/query partner therefore retains the complete notification
contract but reorders publication so band snapshot work and fingerprint
validation precede the final cache truncate/insert. Its gate requires the old
public cache to remain readable while band publication waits, zero
representative-route failures, and unchanged notification
workset/owner/completion semantics. The network side is currently unarmed:
`1600/64/8` failed its bounded correctness gate and `2880/128/16` may not be
run by skipping that step.

Registered-user/discovery/targeted processing shares the proxy pool and cannot
be attributed to the DB-only lane while the network profile changes. The
network lane therefore owns the accepted bounded settings: `00:10:00` solo
refresh, `00:05:00` discovery/targeted timeouts, and `80` lookups per
discovery/targeted pass. The paired network lane selects the highest
sequentially qualified named guard profile from `800/32/4`, `1600/64/8`, and
`2880/128/16`.

Bounded canaries, read-only query A/Bs, and unrelated-table compaction/reclaim
may proceed concurrently when live preflight, disk headroom, locks, and shared
IO remain safe. A production scrape still requires the normal capacity guard
and coordinated hold/start boundary.

## Database management classification

| Surface | Mode | Live-safety risk | Data/timing risk | Evidence | Change/proof plan | Rollback | Decision |
|---|---|---|---|---|---|---|---|
| Publication resolver | Correctness/improvement | High | Active versus published scrape | Static trace plus live 1227/1228 state | Bounded fixture and live shadow A/B | Feature flag, prior published mapping | Accepted blocker |
| Query paths | Evaluation | Medium | Result order/rank parity | `pg_stat_statements` since July 7 | Matched bounded plans and response fingerprints | Old query retained | Accepted A/B |
| Snapshot storage | Capacity/retention | High | Historical reconstruction | 1.665 TB, 14 GB/scrape | Fingerprint/coverage/live-scrape parity | Full physical path flag | Experimental until parity |
| Band v2 history | Retention | High | History API correctness | 799 GB, zero-scan large indexes in stats window | Owner cards, manifests, route replay | Recreate indexes/rehydrate archive | Accepted investigation |
| DuckDB/Parquet | Artifact research | Low if bounded | Archive completeness | No current implementation | Same-drive bounded artifact pilot | Delete artifact | Experimental |

## Live baseline

### Capacity and storage

| Metric | Evidence |
|---|---|
| Database size | 3,324 GB |
| FST filesystem | 3.6 TB total, 3.3 TB used, 275 GB free, 93% |
| Solo physical snapshots | 1,665 GB actual |
| Band v2 history points | 365 GB quad + 288 GB trios + 146 GB duets = 799 GB |
| Solo/composite rank history | About 249 GB estimated family total |
| Band current projection | About 129 GB |
| Band identity/member facts | About 114 GB |
| Solo logical versions | About 59 GB estimated |
| Player score observations | 11 GB |
| Large orphan build/staging tables | None found |

### Largest actual relations

| Relation | Total | Heap | Indexes |
|---|---:|---:|---:|
| `leaderboard_entries_snapshot_pro_guitar` | 422 GB | 161 GB | 261 GB |
| `band_team_rank_history_points_v2_quad` | 365 GB | 139 GB | 226 GB |
| `band_team_rank_history_points_v2_trios` | 288 GB | 118 GB | 170 GB |
| `leaderboard_entries_snapshot_solo_guitar` | 255 GB | 114 GB | 141 GB |
| `leaderboard_entries_snapshot_solo_vocals` | 253 GB | 114 GB | 139 GB |
| `leaderboard_entries_snapshot_solo_bass` | 243 GB | 112 GB | 131 GB |
| `leaderboard_entries_snapshot_solo_drums` | 243 GB | 113 GB | 130 GB |
| `leaderboard_entries_snapshot_pro_vocals` | 187 GB | 76 GB | 111 GB |
| `band_team_rank_history_points_v2_duets` | 146 GB | 67 GB | 78 GB |
| `composite_rank_history` | 85 GB | 24 GB | 60 GB |

### Write amplification and maintenance

| Metric | Evidence |
|---|---|
| Scrape 1228 observations | 39,480,046 |
| Unchanged observations | 24,094,693 (61.03%) |
| Changed rows | 15,325,132 |
| New rows | 60,221 |
| Logical current upserts | 15,385,353 |
| Versions closed/opened | 15,325,132 / 15,385,353 |
| Physical snapshot estimate | 14 GB per full scrape |
| WAL since 2026-07-07 13:44 UTC | 3.336 TB |
| WAL buffers full | 83,993,877 |
| Timed checkpoints | 876; about one every five minutes |
| Checkpoint write time | 168,486,769 ms cumulative |
| Database temp bytes | 1.533 TB in the observed database-statistics window |

Physical snapshots alone represent about 19 full-scrape equivalents of current
free space. At roughly two scrapes per day, that is under ten days of headroom
before logical/history growth and transient build space are considered.

### Correctness and duplicate-data evidence

| Finding | Evidence |
|---|---|
| Frozen source mismatch | Published scrape 1227; every finalized active scope pointed to 1228 while reads were frozen |
| Frozen scrape ID | Null |
| Scope fingerprint publication | All 6,087 fingerprint rows had null `published_scrape_id` |
| Coverage fields | All 6,087 rows had null reported entries/pages |
| Missing fingerprints | 42 Pro Vocals scopes had active snapshots but no fingerprint |
| Score history duplicates | 324 nullable-time groups; 1,074 excess rows; max 38 copies |
| Exact duplicate indexes | None found in valid/ready live index definitions |
| Multiple open versions | Zero violations in a 1,000-key live sample |
| Observation store ownership | 11 GB; captured `pg_stat_statements` showed inserts but no production read query |

### Hottest measured query families

The `pg_stat_statements` window began 2026-07-07 13:44 UTC.

| Query family | Calls | Mean | Total | Reads/temp | Decision |
|---|---:|---:|---:|---|---|
| Per-scope active-snapshot resolver | 67,419 | 829.77 ms | 15.54 h | 79.1M shared reads | P0/P1 A/B |
| All-songs active-snapshot/current resolver | 710 | 31.32 s | 6.18 h | 137.6M shared reads; 43.5M temp writes | Highest query A/B |
| Band normalized/current query | 23,085 | 674.93 ms | 4.32 h | 59.2M shared reads | A/B |
| Current rows query | 64,992 | 236.16 ms | 4.26 h | 66.97M shared reads | A/B |
| Band membership union | 694 | 5.27 s | 1.02 h | 791.4M shared reads | Highest read-amplification A/B |
| Latest ranks temp build | 63 | 76.61 s | 1.34 h | 27.5M temp writes | Rank-history redesign |

## Great / good / okay / poor / bad

| Rating | Areas |
|---|---|
| Great | Raw Npgsql; binary COPY; set-based merge; parameterization; partition pruning; real PostgreSQL tests |
| Good | `pg_stat_statements`; `auto_explain`; advisory locks; bounded cleanup; build/swap patterns |
| Okay | Current Postgres platform; logical-version experiment; current retention framework |
| Poor | Migration governance; query fan-out; history/index ownership; backup/restore; vacuum/storage headroom |
| Bad | Frozen active-source fallback; physical snapshot multiplication; 799 GB inactive history posture; 93% disk use |

## Phase PG-0: Immediate capacity and correctness baseline

**Decision:** Accepted, non-destructive first  
**Dependencies:** None  
**Maintenance window:** No

### PG-0.1 - Add hard capacity guardrails

Record before every broad scrape/post-process/maintenance action:

- filesystem free bytes and percentage;
- database and WAL directory size;
- transient build-space estimate;
- active vacuum/build/repack;
- current scrape/publication state.

Define thresholds that:

1. defer optional shadow/history builds;
2. reject rewrites without required scratch;
3. alert before physical snapshot headroom reaches seven days.

### PG-0.2 - Restore an authoritative PostgreSQL design document

`docs/database/FSTServiceDatabaseDesign.md` is absent. Recreate it from the live
schema and code with:

- table owner;
- source-of-truth versus projection/cache/artifact;
- read/write callers;
- scrape/publication semantics;
- retention;
- indexes;
- restore path.

### PG-0.3 - Build a persistent evidence pack

Per scrape, persist:

- counts/ranges/fingerprints;
- logical write metrics;
- table/index growth;
- WAL/temp/checkpoint deltas;
- phase timings;
- route fingerprints;
- free-space projection.

### PG-0.4 - Test backup and restore on isolated storage

No destructive work is promotion-ready without:

- backup method;
- restore command;
- recovery timing;
- count/range/fingerprint parity;
- representative API checks.

All FST backup, artifact, and scratch work remains on the FST drive.

With only 275 GB free, a full 3.324 TB duplicate restore is currently blocked.
The interim accepted drill is:

1. restore schema plus a bounded representative scrape/history slice;
2. verify counts, ranges, fingerprints, constraints, and API fixtures;
3. measure bytes and time;
4. calculate and record the exact same-drive headroom needed for a full restore;
5. run the full restore only after reclaim creates that headroom.

## Phase PG-1: Repair publication source semantics

**Decision:** Accepted correctness blocker  
**Dependencies:** PG-0  
**Maintenance window:** Normal deploy; no destructive rewrite

### PG-1.1 - Add per-scope published physical source

The global published scrape ID cannot describe unchanged scopes pinned to older
physical snapshots. Store and atomically promote the selected published source
per `(song_id, instrument, scope_kind)`.

**Acceptance**

- Every published scope resolves to one validated physical/logical source.
- A migration/backfill fixture contains no missing or ambiguous mapping.

**Rollback/blocked condition**

- Add the schema backward-compatibly and leave readers on the old resolver.
  Service cutover is blocked until worker dual-write/backfill is complete.

### PG-1.2 - Make frozen reads and exports publication-aware

Fix:

- current-state fallback SQL;
- player export source;
- projection readiness;
- overlays/empty scopes;
- cached-only fallback.

**Acceptance**

- Forced cold misses and exports during freeze match the prior published route
  fingerprints exactly.

**Rollback**

- Service resolver flag returns to the prior path while the new mapping remains
  available for diagnosis.

### PG-1.3 - Complete fingerprint coverage semantics

1. Populate `published_scrape_id`.
2. Populate reported total entries/pages.
3. Explain and eliminate the 42 missing Pro Vocals fingerprints.
4. Include page/deep-scrape completeness.

**Acceptance**

- No active expected scope lacks a fingerprint.
- Published ID and reported entry/page fields are non-null for complete scopes.
- Partial/unknown coverage cannot be promoted.

**Rollback/blocked condition**

- Dual-write new coverage fields first. Physical write skipping remains blocked
  until a full live shadow scrape passes.

### PG-1.4 - Add correctness fixtures

Cover:

- changed scope;
- unchanged scope using an older physical snapshot;
- empty leaderboard;
- overlay;
- failed in-progress scrape;
- cold cache during freeze;
- mixed scope source IDs.

**Acceptance**

- Fixture and integration suites prove row/order/rank/export parity and failure
  retention before any resolver cutover.

### PG-1 execution evidence - accepted 2026-07-11

**Decision:** Accepted and promoted behind role-specific rollback flags.
**Candidate image:** `sha256:8ca8001d420f6886a759d0c2bd674335fa3921fd2cb3addf9bbeaa224f08a8ac`
**Baseline / candidate:** published scrapes `1229` / `1230`

- Added the backward-compatible `leaderboard_published_scope_source` table and
  fingerprint completeness fields. The table uses only its publication-first
  primary key; no startup secondary-index build or table rewrite was added.
- The worker records expected-scope coverage, deduplicates API rows with the
  same highest-score-per-account rule as physical snapshots, validates exact
  physical counts, builds the mapping all-or-nothing, and promotes mapping,
  fingerprint publication IDs, band tables, cache rows, and the global scrape
  pointer in one transaction.
- Initial backfill was rejected at `1,811/6,129` scopes because old
  fingerprints counted duplicate API account rows. The repaired physical
  fingerprint backfill completed in `00:30:36.664`: `6,129` mapped scopes,
  `6,087` snapshot scopes, `42` explicit empty scopes, `39,505,439` physical
  rows, zero count/metadata mismatches, and zero incomplete rows.
- Service reads use matching current projections and fall back only mismatched
  scopes to the mapped snapshot plus overlay. A rejected all-scope fallback
  took `14.47s` warm; the accepted partial fallback returned the matched export
  in `0.470s` versus `0.698s` on the old resolver with byte-normalized workbook
  parity.
- During scrape `1230`, all `6,129` active snapshot scopes advanced while the
  public pointer remained on mapped scrape `1229`. After a service restart, a
  forced cold leaderboard query returned `23/23` account, score, and rank rows
  exactly equal to direct mapped-source SQL in `0.176s`.
- Candidate publication produced `6,129/6,129` validated mappings in
  `00:03:27.318`. The final map contains `6,087` snapshot sources on `1230`,
  `42` empty sources, `39,525,359` physical rows, and zero fingerprint,
  coverage, source-count, or publication-ID mismatches.
- The pre-PG-1 baseline held schema DDL locks through its `501.6s` publication
  transaction, timing out `/readyz` and `/api/service-info`. PG-1 now uses a
  read-only schema probe and a separate five-second-lock-timeout repair
  transaction. Candidate publication took `548.0s`; all nine 60-second publish
  monitor ticks kept `/readyz`, the festivalweb shell, and
  `/api/service-info` healthy. Across the complete candidate window, all `525`
  monitor ticks were healthy.
- Matched recorded phase time was `17,363,306ms -> 18,987,286ms` (`+9.35%`);
  rank snapshots were `+3.74%`, composite snapshot `+6.11%`, and publication
  `+9.24%`. Candidate resource evidence recorded `494.9GB` WAL, `172.5GB`
  temp, peak worker `8.38GiB/12GiB`, peak Postgres `15.80GiB/16GiB`, and
  minimum free space `220.3GB`. The map itself is `4.63MB`.
- End-to-end wall time was `6:17:51 -> 7:58:31`; the network slice was not a
  matched performance baseline because scrape `1229` ran in the stale
  long-lived 30-node proxy container while the current production compose
  recreated `1230` with four proxy endpoints. Requests and bytes remained
  comparable (`399,152/57.75GB -> 398,260/57.58GB`), and PG-1 database/public
  phases stayed within the 10% gate.
- Validation: `418` targeted PostgreSQL/Testcontainers tests passed; the
  CI-equivalent FSTService line rate is `94.22%`. The full diagnostic had
  `1,939/1,941` passing in the coverage run; the two failures are stale,
  pre-existing default/removed-route fixtures outside PG-1.

**Production flags**

- `fstworker`: `Features__WritePublishedScopeSources=true`,
  `Features__UsePublishedScopeSources=false`.
- `fstservice`: `Features__WritePublishedScopeSources=false`,
  `Features__UsePublishedScopeSources=true`.
- Rollback sets both flags false and restores the prior service/worker images;
  the additive table and fingerprint fields may remain.

**Artifacts:** `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/pg1-published-source-20260711T0103Z`

**Next integrated task:** SERVICE-0.2 / WORKER-0 durable status and failure
propagation, followed by PG-2 published-resolver query A/Bs. PG-4 physical
snapshot write skipping was not enabled or combined with this phase.

### PG-1 service-contract follow-through - accepted 2026-07-13

- SERVICE-0.1 now consumes the accepted mapping through a shared SQL selector;
  enabled reader SQL has no active-snapshot branch, and projection rows require
  mapped source plus projection-generation parity.
- Per-route totals and published solo exports delegate to that resolver.
  Clean-boundary population floors are snapshotted into the mapping; this
  repaired a diagnosed canary mismatch of `10,042` mapped versus `374,853`
  published-route entries without consulting active scrape state. The
  role-specific rollback flag still restores the prior active resolver.
- SERVICE-0.2 reads scrape/publication/freeze/worker state in one statement and
  reports durable network, post-process, publication, failure, freeze, and
  schedule semantics.
- Failed coverage windows `1232` (`16` incomplete scopes) and `1234` (`3`)
  published nothing and retained `1231`. The accepted complete window `1235`
  atomically promoted `6,138` mappings (`6,096` snapshot, `42` empty),
  `39,578,699` physical rows, and zero incomplete/missing metadata.
- Matched rollback/candidate reads on published `1235` had exact route,
  published-solo-export, and full-export fingerprints. Rankings phase time was
  `+5.25%`; all `604` one-minute accepted-window health ticks passed.
- Final free space was `114,964,156,416` bytes (`3.82` projected days);
  capacity/reclaim work is the immediate dependency before optional PG-2
  build/query experiments.
- Focused PostgreSQL/unit/API validation passed `356/356`; CI-equivalent line
  coverage is `94.24%`. Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/service-0.1-0.2-published-status-20260711T143501Z`.
- This follow-through does not enable PG-2 query optimization or PG-4 snapshot
  write skipping.

### PG-1 / WORKER-0A correctness-ledger follow-through - code accepted, promotion hard-blocked

- Additive metadata only: `leaderboard_scope_manifests`,
  `scrape_writer_failures`, `scrape_phase_outcomes`, and durable failure/warning
  columns on `scrape_log`. No leaderboard/history table rewrite, alternate
  drive, or optional index build is part of this phase.
- Published-source candidate construction requires a matching complete solo
  manifest when `Features:EnforceScopeCompletenessManifests=true`; existing
  physical row-count and content-fingerprint validation remains unchanged.
- Solo and band manifest rows retain the candidate scrape ID even when
  publication is rejected. Binary/JSON writer replay artifacts remain on the
  mounted FST data root and are referenced by exact failed scope/page/row rows.
- `scrape_log.status='failed'` is excluded from completed/publication fallback
  resolution. The global pointer and per-scope map remain the only published
  source contract.
- Promotion requires one full candidate scrape with zero incomplete expected
  manifests, zero writer failures, zero publication-critical phase failures,
  exact physical/mapping validation, acceptable WAL/temp/CPU/memory/disk cost,
  and a verified rollback to the three disabled enforcement flags.
- This follow-through is a correctness dependency for later PG-2/PG-4 work; it
  does not enable query optimization, physical write skipping, retention, or
  broad maintenance.
- Additive schema initialization completed without a leaderboard/history
  rewrite. A candidate startup exposed a pre-existing unbounded logical-shadow
  rollback scan after failed scrape `1237`; that run was stopped before network
  scraping. Subsequent live attempts disabled the experimental logical shadow
  writer only in candidate config, leaving physical snapshots authoritative.
- Capacity guard remained scrape-allowed but below the seven-day target:
  candidate-start free space was `95,961,432,064` bytes (`3.19` projected
  days). No optional build, rewrite, repack, alternate-drive work, or
  destructive cleanup was attempted.
- Live promotion could not clear the data-parity gate because all configured
  PIA exits were provider-blocked even after a complete 30-container reset.
  Final production state is the accepted `824415e9` service/worker image,
  worker held, published `1236`, public reads unfrozen, no ungranted locks, and
  the additive tables retained for the next provider-valid window.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/worker0a-correctness-20260713T1552Z`.

## Phase PG-2: Run matched query A/Bs

**Decision:** Accepted evaluation program  
**Dependencies:** PG-0; publication-dependent queries also require PG-1

### PG-2.AB1 - Published per-scope resolver

| Field | Plan |
|---|---|
| Baseline | Active-snapshot fallback query, 67,419 calls, 829.77 ms mean |
| Candidate | Direct published-source mapping plus indexed snapshot/projection read |
| Dataset | 20 changed/unchanged/empty/overlay scopes; top 10/100 and account lookup |
| Correctness | Exact rows/order/ranks/totals and published scrape/source IDs |
| Performance | Warm/cold p50/p95/p99, buffers, rows scanned, temp, locks |
| Target | Zero correctness differences and >=50% lower p95/buffers |
| Rollback | Resolver feature flag |

### PG-2.AB2 - Remove broad all-songs current-state reconstruction

| Field | Plan |
|---|---|
| Baseline | 710 calls, 31.32 s mean, 43.5M temp blocks written |
| Candidate | Fresh published projection or bounded changed-scope/materialized input |
| Dataset | One instrument, all 681 songs, identical published scrape |
| Correctness | Account/song row fingerprint and ranking output |
| Target | >=70% temp reduction and >=50% p95 reduction |

### PG-2.AB3 - Consolidate band membership lookup

| Field | Plan |
|---|---|
| Baseline | Union of membership, members, and search projection; 5.27 s mean and 791.4M shared reads |
| Candidate | One canonical membership table/index plus explicit fallback during migration |
| Dataset | 1, 10, 100, and 1,000 account IDs |
| Correctness | Exact band/team set |
| Target | >=90% shared-read reduction and <500 ms p95 for one account |

### PG-2.AB4 - Replace full latest-rank history scans

| Field | Plan |
|---|---|
| Baseline | 76.61 s mean temp build over full history |
| Candidate | Incrementally maintained latest table or date-bounded partition-aware query |
| Correctness | Exact latest rank/rating per account |
| Target | <10 s initial, then <2 s for maintained latest state |

### PG-2.AB5 - Batch player fallback and member-score reads

Use one set query for bounded values/accounts/instruments. Require constant
query count, exact result parity, and meaningful p95/buffer reduction.

### PG-2.AB6 - Collapse band projection member aggregation

Replace seven correlated `band_member_stats` aggregates with one grouped or
lateral aggregate. Test 1K-20K teams and preserve member order exactly.

## Phase PG-3: Recover low-scratch space from proven redundancy

**Decision:** Accepted investigation; object removal remains evidence-gated  
**Dependencies:** PG-0 owner cards and route/job replay  
**Maintenance window:** One object at a time

### PG-3.1 - Create index owner cards

For every large zero-scan index record:

- creating migration;
- constraint ownership;
- caller/query;
- stats reset age;
- build/swap age;
- size;
- recreate command;
- write cost.

Highest-value non-constraint candidates include:

- about 89 GB trios v2 history secondary index;
- about 41 GB duets v2 history secondary index;
- snapshot-ID v2 indexes;
- about 39 GB of composite rank-history secondary indexes.

Do not drop primary/unique indexes solely because `idx_scan=0`.

**Projected reclaim:** 100-175 GB if owner proof confirms the large secondary
index family is unused.

An index drop is allowed only after:

1. live-scrape and representative route/job parity;
2. tested exact recreate SQL;
3. lock/load preflight;
4. one-object-at-a-time validation.

This phase does not delete table rows.

### PG-3.2 - Prove ownership of `player_score_observations`

The table is 11 GB and current query statistics showed inserts but no reader.
Confirm exports/external tools and produce an owner decision:

- propose removal of dual writes plus a PG-7 archive/drop plan; or
- propose making it the canonical observation owner plus a PG-7 migration plan
  for the duplicate history path.

PG-3 performs no table-row deletion or table drop.

**2026-07-28 decision:** ownership, live parity, and destructive maintenance
accepted and executed. The pre-action table was `12,682,354,688` bytes and contained
`10,167,937` rows: `9,938,912` `band-member` and `229,025` `solo-history`.
Repository, database-dependency, export, and production-tool audits found no
production reader; the only view is test-owned
`player_score_observation_union`. All solo observations have a semantic
`score_history` match. Historical band observations are not fully
reconstructable from mutable current band facts.

`WriteSoloScoreObservations` and `WriteBandMemberScoreObservations` now provide
independent default-off rollback switches in deployed code/config; `28/28`
targeted flag/writer tests pass. Scrape `1267` supplied the complete writer-off
publication, and the independent retirement phase passed exact public parity,
rollback rehearsal, and post-action validation. The table is now empty at
`24,576` bytes with its schema, union view, indexes, primary key, and sequence
retained. Drop remains a separate future code/schema-removal decision.

### PG-3.3 - Resolve overlapping band member facts

`band_member_stats` and `band_members` together use about 97 GB, before
membership/configuration tables. Define canonical identity, per-score stats,
and search projection ownership.

### PG-3.4 - Repair nullable score-history uniqueness

Manifest the 1,074 known excess rows and design `NULLS NOT DISTINCT`, a partial
unique index, or a generated key. Actual row cleanup and constraint promotion
move to PG-7 after backup/restore and live-scrape parity.

### PG-3 urgent low-scratch reclaim - accepted 2026-07-13

- Scrape `1236` completed, published, and unfroze cleanly with `6,138`
  published scopes (`6,096` snapshot, `42` empty), `39,588,650` published
  rows, zero incomplete scopes, and zero failed phase-timing rows.
- Refreshed owner cards rejected the `44-122 GB` band v2 team/date child
  indexes because bounded public history plans used them in `0.216-0.304 ms`.
  The `23,266,508,800`-byte `ix_crh_retention_cutoff_account` index was also
  retained because the bounded retention plan uses it for both cutoff and
  newer-row probes. Zero scans before the 365-day cutoff was not treated as
  proof of redundancy.
- `ix_crh_latest` was accepted as the one-object reclaim candidate. It was a
  non-constraint `20,890,148,864`-byte index with only 11 small scans in the
  statistics window. The production composite snapshot job selected a
  parallel sequential scan/sort plan instead; forcing the index cost 16.75x
  more, and a transactional drop/rollback produced the identical chosen plan.
- After the worker was held and post-publish autovacuum cleared,
  `DROP INDEX CONCURRENTLY public.ix_crh_latest` completed in `0.18 s`.
  Database size fell by exactly `20,890,148,864` bytes and filesystem free
  space rose from `78,549,483,520` to `99,439,702,016` bytes. The capacity
  guard horizon improved from `2.61` to `3.31` days; the recent four-window
  pressure model improved from `1.49` to `1.89` days.
- Pre/post scrape totals, stable route bodies, leaderboard, normalized solo
  export, all three sampled band-history responses, composite ranking page and
  account responses, and all representative plans were exact matches. No
  invalid indexes, ungranted locks, temp files, or public-path regressions
  remained.
- Startup schema no longer recreates the retired index. Targeted tests execute
  the exact concurrent recreate and drop SQL and pass `6/6`. Rollback remains
  `CREATE INDEX CONCURRENTLY ix_crh_latest ON
  public.composite_rank_history USING btree (account_id, snapshot_date DESC)`.
- `player_score_observations` remains intact. It is about `11.69 GB` and
  approximately `96.89%` band-member observations; current evidence showed
  1,971 insert statements and no production reader. PG-3 classifies it as a
  duplicate audit/observation candidate for a future dual-write A/B and PG-7
  manifest/archive decision, not data deletion in this phase.
- Normal scraping was restored as scrape `1237`, with public reads frozen
  safely on published `1236`. Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/postgres-capacity-reclaim-20260713T072827Z`.

### PG-3 disk-pressure incident reclaim - accepted 2026-07-15

- Scrape `1261` reached post-process with all `8,208` captured manifests
  complete, but the FST filesystem had only `26,207,338,496` bytes free. A
  matched scrape-`1236` monitor showed that the equivalent pre-rank boundary
  through publication had required `45,148,225,536` bytes, so the worker was
  stopped before rankings could exhaust the filesystem.
- The rollback transaction marked `1261` failed at
  `capacity_before_rankings_publish`, cleared its stale operation and public
  freeze, retained published scrape `1236`, and proved that `1261` owned zero
  published-source rows. `fstservice`, `festivalweb`, and Postgres remained
  healthy with no ungranted locks.
- Reproducible non-database cleanup removed `652,046,336` bytes of unused
  Playwright browser cache and `99,295,232` bytes of old test/build scratch.
  Decision evidence, scrape manifests, rollback SQL, and path data were
  retained.
- The next one-family owner decision retired partitioned
  `public.ix_btrhlv2_snapshot` and its Duets/Trios/Quad child indexes. The
  family was non-constraint, occupied `3,277,135,872` live index bytes, had no
  production statement/view/function or repository read owner by
  `snapshot_id`, and was absent from latest-state delta/write plans, which
  continued to use the primary keys. The one observed Duets scan was this
  incident's explicit diagnostic probe.
- A transactional drop/rollback completed in `3.910 ms`; the committed drop
  completed in `68.378 ms`. Database size fell by `3,277,996,032` bytes and
  filesystem free space rose by `3,278,016,512` bytes, from
  `26,942,255,104` to `30,220,271,616`.
- A separate second owner decision retired partitioned
  `public.ix_btrhpv2_snapshot` and its three points-v2 child indexes. Public
  history and parity reads continued to use the retained team/date family;
  only an explicitly unowned `snapshot_id` diagnostic used the retired
  family. Its transactional drop/rollback took `1.422 ms`, and the committed
  drop took `129.457 ms`.
- The points-family drop reduced database size by another `8,864,440,320`
  bytes and increased filesystem free space by `8,864,481,280` bytes, from
  `30,962,761,728` to `39,827,243,008`. It changed no history rows or
  constraints.
- Published scrape totals and stable route fingerprints matched. Twelve
  leaderboard/ranking/history/export responses were byte-exact before,
  after, and on repeat; all sampled Duets/Trios/Quad history and composite
  routes matched; owner/history plans retained the same primary/team-date
  indexes with no temp spill. No invalid indexes or ungranted locks remained.
- Startup schema no longer recreates either retired snapshot lookup family.
  Four targeted tests prove non-recreation and execute each exact
  child-concurrent-build, parent-attach, validate, and family-drop rollback
  sequence.
- The capacity guard improved from `0.87` to `1.32` projected days, but the
  measured safe completion boundary is still short by about `5.32 GB`, before
  adding rollback margin.
  `fstworker` therefore remains held; this incident does not accept
  WORKER-0A or authorize autonomous live scrapes. Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/fst-disk-pressure-20260715T1408Z`.
- The residual owner sweep rejected `ix_cble_trios_team_scope_generation`
  despite its prior zero-scan count: the exact public selected-team lookup
  plan uses that index. `ix_crh_retention_cutoff_account` was retained at that
  incident boundary because the existing retention query used it. The four
  smaller zero-owner secondary indexes total only `4,025,819,136` bytes; even
  dropping all four separately would leave a `1,302,163,456`-byte shortfall
  before any rollback margin.

### PG-3 residual rollback-margin recovery - accepted 2026-07-15

- A fresh owner and plan review isolated
  `public.ix_crh_retention_cutoff_account` as the only single non-constraint
  object large enough to restore the measured scrape-completion margin without
  touching table data. It occupied `23,526,973,440` bytes. Composite ranking
  routes read `composite_rankings`; the latest-history snapshot plan continued
  to choose the same parallel sequential scan/sort without this index.
- The 26.98 GB heap is strongly ordered by `snapshot_date`
  (`pg_stats.correlation=0.96636045`). A concurrent 688,128-byte
  `ix_crh_retention_cutoff_brin` replacement built in `67.53 s` with no
  transient scratch requirement. The retained primary key
  `(account_id, snapshot_date)` covers the correlated newer-row probe.
- Composite retention batches are now deliberately unordered. That lets the
  BRIN reject empty cutoff ranges and lets `LIMIT 5000` stop after one bounded
  batch instead of sorting every eligible row. With the btree removed
  transactionally, the current 365-day no-row probe completed in `1.50 ms`;
  a representative eligible cutoff completed in `0.69-0.72 s`, with no temp
  spill or WAL, versus `0.43 s` through the retired btree.
- Exact concurrent recreate SQL was persisted before mutation.
  `DROP INDEX CONCURRENTLY public.ix_crh_retention_cutoff_account` completed
  in `0.16 s`, reduced database size by exactly `23,526,973,440` bytes, and
  increased filesystem free space by `23,527,038,976` bytes.
- Final measured free space was `63,339,065,344` bytes. The exact
  `45,148,225,536`-byte pre-rank-through-publication guard now passes with
  `18,190,839,808` bytes of margin; the default guard horizon improved from
  `1.32` to `2.11` days. Seven-day optional-build/rewrite headroom is still
  unavailable.
- All `12/12` public route/history/ranking/export fingerprints matched
  baseline, post-drop, and repeat. Duets/Trios/Quad history, solo history,
  composite ranking, and latest-history plans retained their owners; `106`
  targeted ranking/maintenance tests passed. No active query, ungranted lock,
  invalid index, vacuum, or index build remained.
- `fstworker` remains intentionally held. Published scrape `1236` remains
  authoritative and unfrozen; failed scrape `1261` exposed no published
  candidate rows. Capacity is now ready for the next single WORKER-0A
  full-scrape A/B, but this phase did not start it. Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/fst-residual-capacity-20260715T144916Z`.

### PG-3 final WORKER-0A capacity A/B - rejected 2026-07-16

- The prestart measured guard passed at `63,365,509,120` free bytes, and the
  exact post-writer guard passed at `54,284,406,784` bytes after all
  `8,208/8,208` manifests and band writer drain completed.
- Candidate `1262` then grew the database by `26,778,927,104` bytes and reduced
  filesystem free space by `32,087,322,624` bytes before rankings or global
  publication. The stop occurred during band current-projection generation
  `95` publication with `12,972` ready scopes / `21,967,889` rows and
  `30,992,838,656` bytes free.
- Rollback marked the scrape failed, retained published scrape `1236`, proved
  zero candidate published-source rows, and left no active query or ungranted
  lock. The complete manifest/writer/phase ledgers and partial generation
  `94`/`95` evidence remain intact; no destructive cleanup was performed.
- Final measured free space is `31,264,702,464` bytes. The
  `45,148,225,536`-byte guard now blocks another scrape with an exact
  `13,883,523,072`-byte shortfall. Default seven-day optional-build/rewrite
  headroom remains unavailable.
- `12/13` normalized public route/export/history/ranking fingerprints matched
  rollback baseline. The sole mismatch was the live-fallback band best/worst
  songs route; published mappings stayed exactly `6,138` scopes /
  `39,588,650` rows. WORKER-0A is rejected, not promoted, and the worker
  remains held.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/worker0a-final-live-ab-20260715T151317Z`.

### PG-3 post-scrape-1262 residual capacity recovery - accepted 2026-07-16

- The terminal preflight found `31,261,032,448` bytes free, no active scrape,
  vacuum, index build, rewrite, long query, or ungranted lock, and an exact
  `13,887,193,088`-byte measured scrape shortfall. Published scrape `1236`
  remained authoritative and unfrozen while the worker stayed held.
- Docker images and `56.22 GB` of reclaimable build cache were rejected as a
  capacity action because Docker root is `/mnt/storage/docker-data`, not the
  required `/mnt/docker-storage` FST filesystem. Same-drive path data and
  accepted evidence were preserved; the remaining reproducible same-drive
  artifacts were far too small to clear the gate.
- Current plans retained the `261,790,859,264`-byte band-history team/date
  family: Duets/Trios/Quad history reads used it in `0.160-0.213 ms`. The
  `18,807,029,760`-byte band current-projection generation/rank family was
  rejected because removing it transactionally raised warm top-page SQL from
  `0.037` to `2.948 ms` and deep-page SQL from `3.801` to `4.565 ms`.
- The accepted object was partitioned non-constraint family
  `public.ix_rh_latest`: one zero-byte parent plus nine physical child indexes,
  exactly `45,547,339,776` bytes, with no primary, unique, or foreign-key
  ownership. The retained `rank_history` primary-key family covers account
  history and latest-row lookups.
- `SnapshotRankHistory` now resolves each account's latest date with a
  primary-key-backed `MAX(snapshot_date)` group and join. Without
  `ix_rh_latest`, the full latest-row plan cost fell from `8,314,062.24` for
  the old distinct/sort path to `1,212,279.07`; a matched bounded median
  improved from `23.343` to `18.312 ms`. Warm public rank-history SQL improved
  from `0.312` to `0.141 ms` in the transactional proof.
- Exact rollback was persisted and tested before mutation: build all nine
  child indexes concurrently, create the empty partitioned parent, then attach
  each child. PostgreSQL cannot drop a partitioned index concurrently, so the
  guarded parent-family drop used a five-second lock timeout and completed in
  `0.30 s`.
- Database size fell from `3,842,540,050,099` to `3,796,992,710,323` bytes.
  Filesystem free space rose from `31,261,003,776` to `76,808,527,872` bytes,
  a `45,547,524,096`-byte gain. The final measured scrape guard passed at
  `76,804,927,488` bytes free with `31,656,701,952` bytes of margin.
- All `12/12` route/export/history/ranking fingerprints matched baseline,
  post-drop, and repeat. Rank-history route, latest-row, current and eligible
  retention fingerprints were exact; `68/68` targeted schema/ranking tests
  passed. Final route/latest-row medians were `0.291/18.285 ms`, all retained
  band-history and composite-retention plans kept their owners, and no invalid
  index, active query, ungranted lock, vacuum, or index build remained.
- Pushed commit `7050ee93` removes startup recreation and contains the
  primary-key latest-row query. The worker was recreated but not started on
  `fstservice:post1262-capacity-7050ee93`, with `RunOnce=true`, restart policy
  `no`, and state `created`.
- Capacity is ready for another full scrape plus rollback margin. Overall
  WORKER-0A live A/B remains held because the parent-owned band best/worst
  songs published-read parity gap is still unresolved; this capacity phase did
  not run a scrape. Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/post1262-capacity-recovery-20260716T021005Z`.

### PG-3 scrape-1263 stale recovery and renewed capacity hold - mixed 2026-07-25

- Scrape `1263` completed all `8,208` expected manifests and entered
  rankings. Its recovery worker was stopped by the disk watchdog at
  `14,871,388,160` free bytes during rank-history snapshots. The container
  exited `137` after its stop grace period, with `OOMKilled=false`, leaving
  `scrape_log`, the public freeze, and worker operation state stale.
- Read-only preflight found no worker-owned query, long transaction, ungranted
  lock, advisory lock, vacuum, index build, rewrite, or published-source row
  for `1263`. A five-second-lock-timeout transaction marked it failed at
  `capacity_watchdog_abandoned`, retained published `1236`, unfroze the
  publication ledger, and marked the worker offline. Exact rollback SQL was
  dry-run, restored byte-identical pre-state rows, and was then validated by a
  rollback/reapply transaction.
- The unfrozen parity probe proved mapped solo leaderboard, composite, and
  published band ranking/history samples still matched the pre-candidate
  baseline, but player ranking, rank history, and exports contained
  unversioned `1263` derived writes. Service commits `03edc85b` and
  `633e7583` now make those cache misses/exports fail closed while preserving
  exact mapped solo leaderboard reads. Band-song routes remain stable `503`
  because global band generation `96` was never published; no candidate
  `band_entries` fallback occurs.
- Final filesystem free space is `31,385,374,720` bytes against the measured
  `45,148,225,536`-byte full-scrape boundary, a
  `13,762,850,816`-byte shortfall before rollback margin. The scrape and
  reclaim guards both block. Same-drive spool/curl scratch is empty; retained
  path/evidence data cannot close the gap; there are no invalid or large
  abandoned build relations.
- Current owner cards retain every large candidate:
  `ix_btrhpv2_team_date` owns public band history,
  `ix_les_snapshot_song_score` owns published leaderboard reads,
  `ix_cble_scope_generation_rank` has `1,250,396` scans and a previously
  measured hot-page regression without it, and
  `ix_bms_type_team_combo_account_instrument` has `5,502,193` scans.
  No safe single non-constraint index can close the gate.
- The worker compose guard passes after `pia-gluetun-8` recreation with
  `25/25` healthy unique exits. Capacity remains the exact hard gate and the
  worker stays held.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/stale-scrape-1263-recovery-20260725T153938Z`.

### PG-3 post-scrape-1263 residual capacity recovery - accepted 2026-07-25

- The measured preflight started at `31,373,258,752` filesystem bytes free,
  with no active scrape, query, transaction, ungranted lock, vacuum, index
  build, rewrite, or invalid index. Published scrape `1236` remained
  authoritative and unfrozen while `fstworker` stayed exited with restart
  policy `no`.
- Docker had `79.97 GB` of reclaimable build cache, but Docker root is
  `/mnt/storage/docker-data`, not the FST filesystem. Same-drive non-Postgres
  artifacts were not material after excluding active path data, manifests,
  evidence, rollback SQL, and source artifacts, so no non-database deletion
  was accepted.
- Six owner-card decisions dropped `33` non-constraint indexes, one logical
  family at a time: exact duplicate/non-owning band ranking indexes
  (`8,271,380,480` bytes), unowned dirty-work secondary indexes
  (`2,949,160,960`), unused band appearance-sort indexes
  (`1,959,124,992`), orphan latest-snapshot indexes (`1,159,544,832`),
  observation-table read indexes with no production read statement
  (`2,314,706,944`), and duplicate/deprecated composite ranking indexes
  (`520,282,112`).
- Exact recreate SQL was checksummed before mutation. Transactional proofs
  preserved every sampled fingerprint. Published band ranking pages moved to
  byte-equivalent build indexes, current team lookups retained their
  build-owned team indexes, published team lookups retained their primary
  keys, observation lookups retained `ux_pso_source`, latest-state reads
  retained primary keys, and composite adjusted-rank reads retained the
  `composite_rank` unique constraint index.
- The `8,775,794,688`-byte current band-song `*_ix_team` family was rejected:
  representative Duets/Trios/Quad reads remained correct but regressed from
  `0.122/0.076/0.069 ms` to `0.330/0.235/0.257 ms`. The indexes were restored
  by transaction rollback and never dropped from production.
- Database size fell from `3,846,380,738,227` to `3,829,206,537,907` bytes,
  an exact `17,174,200,320`-byte reclaim. Measured filesystem free space rose
  to `48,546,029,568` bytes. The `45,148,225,536`-byte scrape guard now passes
  with `3,397,804,032` bytes of margin; optional builds and rewrites remain
  blocked below seven-day headroom.
- Mapped solo leaderboard output remained byte-exact HTTP `200`; ranking,
  history, export, band ranking, and band-song routes remained the same stable
  failed-candidate HTTP `503`. `120/120` relevant tests passed, the Release
  build succeeded, the proxy guard passed `25/25`, and no active query,
  ungranted lock, invalid index, vacuum, or index build remained.
- Pushed commit `8db72081` prevents startup or future ranking publication from
  recreating the retired duplicate/read-only indexes. The current held worker
  image predates that commit, so no scrape was started; a later resume must
  deploy `8db72081` or newer and rerun the capacity, proxy, and full-public-path
  preflight first.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/fst-residual-capacity-20260725T161042Z`.

### PG-3 logical leaderboard shadow retirement - readiness accepted, truncate blocked 2026-07-25

- Production keeps `Features__WriteLogicalLeaderboardVersions=false`. The
  repository default is now false, and startup options validation rejects an
  attempted true value until a future versioned migration, rebuild/restore
  validation, and live-scrape promotion explicitly restore an owner.
- Exact live targets are the two partitioned parents
  `leaderboard_current_entries` and `leaderboard_entry_versions`, with nine
  leaf partitions each. They occupy `33,480,859,648` and
  `107,982,077,952` bytes respectively, or `141,462,937,600` bytes total.
  `leaderboard_logical_write_metrics` is retained at `108` rows /
  `106,496` bytes.
- Exact counts are `39,820,273` current rows and `194,171,215` version rows
  over scrape IDs `1223`-`1237`. Every current row has exactly one matching
  open version: zero duplicate open keys, missing rows, fingerprint
  mismatches, invalid intervals, or timestamp-close inconsistencies.
- The shadow is demonstrably non-authoritative. Failed scrape `1237` remains
  embedded in `4,531,665` current rows; a sampled public Solo Bass slice
  matched published physical snapshot `1236` with zero diff lines and differed
  from the stale logical current rows by `46` diff lines.
- The pre-probe statistics snapshot showed no table/index access after
  2026-07-13 19:36 UTC. Repository, production config, cron, process,
  `pg_depend`, FK, view, materialized-view, routine, rule, trigger, and policy
  searches found no service/API/external runtime reader. The only non-writer
  source reference is the operator-invoked bounded restore drill.
- Disabled-write windows `1261`, `1262`, and `1263` each completed all
  `8,208/8,208` manifests with zero writer or publication-critical failures,
  but all failed before global publication on capacity. No scrape has both
  completed publication and live route/export/ranking/history parity with the
  logical writer disabled.
- The destructive live-scrape A/B gate is therefore not satisfied. No
  `TRUNCATE`, table/index drop, row deletion, worker start, or schema drop was
  executed. The exact future command remains a two-parent, no-`CASCADE`
  transaction after that missing publication gate passes.
- Schema DDL, exact object/index/TOAST manifests, counts/ranges/fingerprints,
  deterministic samples, and fail-closed rebuild SQL are preserved. A
  rollback-only temporary proof rebuilt `139,264` rows across `27` published
  scopes and all nine instruments with zero scope-count mismatches.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/logical-retire-20260725T2306Z`.

### PG-3 published solo compact read model - research accepted, migration blocked 2026-07-25

- The current `current_leaderboard_entries*` projection is
  `46,633,459,712` bytes for `39,601,283` rows. Its primary-key, account,
  rank, and score index families are `9.19/8.91/6.35/4.35 GB`.
- The complete owner matrix proves exports and unfiltered totals already use
  mapped physical/source metadata, while deep pages, accounts, score bands,
  rivals, rankings/precompute, and notifications still require a complete
  current row set and account/rank/score access.
- Wholesale dynamic reads remain rejected. Improved frozen-overlay SQL was
  exact but regressed warm top p95 by `312.6%` at c1 and `84.7%` at c8;
  deep cold p95 regressed `72.5%`. Live overlay also differed from the frozen
  projection in a sampled top-100 payload.
- The accepted design is a keyless compact btree projection plus a bounded
  generation-hot tier. A 501,284-row sample projects the full compact table to
  `18,536,114,242` bytes. Exact top-100/leeway coverage, registered accounts,
  and frozen overlay keys conservatively raise this to no more than
  `20,215,010,912` bytes, reclaiming at least `26,418,448,800` bytes
  (`56.65%`).
- Exact randomized SQL A/Bs passed top/deep, registered player, selected-row,
  score/population, ranking scan, and filtered-rank parity. The default-off
  stored-rank offset flag reduced filtered-player p95 from `94.678` to
  `17.858 ms` at c1 and `190.519` to `59.291 ms` at c8.
- No production schema/index/read cutover ran. Practical logged migration
  headroom is `41.6-60.2 GB` while rollback is retained. LOGICAL-RETIRE later
  completed the disabled-writer publication and shadow-retirement
  prerequisite, but the optional-build/rewrite guard remains below the
  seven-day threshold; a dedicated dual-build capacity model is still
  required.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/solo-dynamic-ab-20260725T2346Z`.

## Phase PG-4: Make scrape writes proportional to semantic changes

**Decision:** Experimental until live-scrape parity  
**Dependencies:** PG-1 and PG-2

### PG-4.1 - A/B physical snapshot write skipping

**Baseline**

- 1.665 TB retained snapshots.
- About 14 GB per full scrape.
- 61.03% unchanged rows in scrape 1228.

**Candidate**

1. Use complete content and coverage fingerprints.
2. Reuse the last published physical source for unchanged scopes.
3. Write new physical rows only for changed scopes.

**Correctness**

- counts, ranges, row fingerprints, ranks, overlays, failed-scrape rollback,
  freeze/cold-cache behavior, projection output, and exports.

**Promotion target**

- Eliminate physical writes for unchanged scopes.
- Preserve exact API and replay parity.

**SNAPSHOT-REUSE readiness and live execution - code accepted, live A/B capacity-blocked
2026-07-26**

- The original default-off implementation could not produce a useful live
  result: manifest coverage upgrades fingerprints from version 1 to version 2,
  so the old comparison classified nearly every complete scope as changed.
  It also pinned to mutable active state, which could select a failed
  candidate's physical rows.
- The repaired path requires published-source writes, strict manifests, and
  scope fingerprints. Bounded-online results register their complete manifest
  before enqueue. Reuse requires exact current/published content and row-count
  parity, compatible complete coverage, and an exact physical-source count.
  Finalization pins to the current published source, never a failed active
  source.
- Published `1236` uses legacy 32-character coverage fingerprints while strict
  candidates use 64-character manifest fingerprints. A one-way compatibility
  rule permits this first upgrade only when the current manifest is complete;
  once a strict scrape publishes, later reuse requires exact coverage parity.
- All `6,096` published physical sources were present with exact
  `39,588,650` rows; `42` explicit-empty mappings were complete. Exact
  published-`1236` versus complete-`1263` content/row parity identified
  `1,203` reusable scopes / `3,371,702` rows. Calibrated against measured
  `1236 -> 1262` relation growth, the candidate estimate is
  `753,396,603` bytes avoided.
- The measured scrape requirement therefore estimates
  `45,148,225,536 -> 44,394,828,933` bytes. At preflight,
  `48,960,053,248` bytes were free, so both guards passed with a severe
  capacity alert.
- Public health, publication, locks, WAL/temp posture, and the canonical
  `25/25` PIA guard passed. The auth-only canary failed with Epic
  `invalid_refresh_token`; a client-credential network probe reached all
  direct/PIA paths but correctly lacked user entitlement. Interactive device
  login is the exact hard gate, so no candidate deploy, worker start, scrape,
  or production flag change occurred.
- Validation passed `186/186` focused and `317/317` broader
  PostgreSQL/API/projection/export tests plus the Release build. The full suite
  passed `2,068/2,072`; the four failures are existing unrelated baseline
  fixtures.
- Decision: code/readiness accepted, live promotion blocked. Logical tables
  remain untouched and the disabled-writer publication prerequisite is not
  cleared. Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-reuse-20260726T010701Z`.
- After operator device authentication, the auth-only credential rotation and
  `25/25` paired authenticated direct/PIA JSON canary passed. The corrected
  current-source image from `919daa32` ran scrape `1264`; an earlier
  registry-wrapper image was rejected before scrape allocation after it
  attempted retired `ix_rh_latest`, and its exact backend rollback restored
  DB size, free space, and public parity.
- Scrape `1264` completed all `8,232` manifests and `59,077,331` reported
  entries with zero incomplete scopes, parse/retry failures, or writer
  failures. Exact content comparison found only `281` reusable scopes /
  `219,427` rows, not the preflight estimate. Zero reusable scope contained a
  physical scrape-`1264` row.
- Snapshot relations grew `15,552,274,432` bytes. Actual-run calibration
  estimates `78,765,704` physical bytes and about `166,448,926` WAL bytes
  avoided. Total WAL delta was `97,876,358,577` bytes and temp-byte delta was
  zero. Network/writer duration was about `23,247 s`, `+4.1%` versus `1262`.
- The strict post-writer guard blocked at `32,390,148,096` free bytes, below
  both `45,148,225,536` baseline and `44,394,828,933` candidate requirements.
  The worker stopped before rankings/publication; `1264` was reconciled failed,
  owned zero published-source rows, and left `1236` authoritative/unfrozen.
- Production worker/config was reverted, all 13 public/export/history/ranking
  fingerprints matched exactly, and final free space is `32,725,393,408`.
  Both scrape guards now block, so the worker remains held. Decision: retain
  the candidate default-off; no promotion, maintenance, deletion, or logical
  truncate. Live evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-reuse-live-ab-20260726T032124Z`.

**BAND-SONG-PROJECTION stale optional projection retirement - accepted
2026-07-26**

- Exact ownership covered the empty legacy table plus Duets, Trios, and Quad
  current tables: `36,747,099` rows and `28,315,639,808` bytes before
  retirement. The tables are standalone derived projections with no FK, view,
  routine, trigger, policy, scheduler, or external-tool owner. Production
  rebuilds remain disabled.
- The rows were stale: current-table `computed_at` values were July 6 while
  current band rankings were July 13. Successful scrape `1236` recorded
  optional rebuilds skipped and published without refreshing them. The
  retained three-row state ledger dates to June 15 and has no current runtime
  caller.
- Live evidence did not rely on scan count. Commit `7558387f` made `/songs`
  exactly match published `/song-rows`; the same overall and combo parity held
  while scrape `1263` changed candidate band rows. Commits `21bd5f56` and
  `633e7583` made unpublished/missing projections fail closed. Commit
  `9dd93570` closed the remaining post-truncate song-row fallback, and
  `3ac2a7c9` avoids expensive freshness scans when the retired scope is empty.
- A schema dump, deterministic published-current rebuild SQL, and an exact
  `2,184,507,134`-byte PostgreSQL custom/zstd data archive were checksummed on
  the FST drive. Full archive read validation passed. Two production
  `TRUNCATE` proofs rolled back exactly; the deployed proof kept all `24/24`
  overall/combo/list/team/songs/song-row fingerprints exact with no blocked
  lock.
- The accepted transaction truncated only
  `band_song_team_rankings` and the three
  `band_song_team_rankings_current_band_*` tables without `CASCADE` in
  `3.974 ms`. Schema, TOAST, nine valid indexes, ownership, and
  `band_song_team_ranking_state` were retained. Database size fell by exactly
  `28,315,533,312` bytes.
- Final measured free space is about `58.97 GB`. The baseline scrape guard
  passes with `13,822,787,584` bytes of margin and the SNAPSHOT-REUSE estimate
  with `14,576,143,227` bytes. Service/web/Postgres stayed healthy, warm
  fail-closed band-route p95 was `1.133 ms`, and `17/17` targeted tests plus
  the Release build passed.
- The worker remains held and was not started. Capacity is sufficient for the
  next parent-owned SNAPSHOT-REUSE A/B, but the candidate remains default-off
  and unpromoted.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/band-song-projection-retirement-20260726T103231Z`.

**SNAPSHOT-REUSE capacity-ready retry - rejected and reverted 2026-07-26**

- Current-source image `fstservice:snapshot-reuse-efdd70b8`, auth persistence,
  authenticated `25/25` direct/PIA parity, the canonical proxy guard, targeted
  tests, and both start guards passed.
- Scrape `1265` completed `8,232/8,232` manifests, zero writer failures, and
  four publication-critical phases. It reused `273` scopes / `218,892` rows;
  zero unchanged scope had candidate physical rows. Estimated savings were
  `112,343,764` physical bytes and `160,525,751` WAL bytes.
- Network/writer time was about `19,890 s`, `-14.4%` from `1264`. Band
  maintenance completed `29,145/29,145` selected scopes with zero failures,
  but took `5:16:46.669`. Ranking phases showed material variance while the
  filesystem remained at 100% usage.
- The 60-second monitor stopped ranking snapshots at `13,144,125,440` free
  bytes, below the declared `14,571,150,203` floor. The run accumulated
  `456,457,274,086` WAL bytes and `52,117,414,796` temp bytes before the stop.
- Scrape `1265` was reconciled failed, owns zero published mappings, and did
  not clear candidate-source API or logical-retirement publication parity.
  Production images/config and the default-off flag were restored; all `13/13`
  public fingerprints matched and published `1236` remains unfrozen.
- After ranking temp cleanup and three post-run autovacuums, free space
  stabilized at about `48.78 GB`; nominal guards pass with only
  `3.63/4.38 GB` of baseline/candidate margin. The worker remains held because
  the live run breached its safety floor despite those guards and never
  produced post-publish capacity/parity evidence. The corrected observed
  start requirement is `60,392,999,803` bytes, leaving an
  `11,616,701,307`-byte current shortfall. Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-reuse-live-ab-20260726T110731Z`.

### PG-3 post-scrape-1265 low-scratch capacity recovery - accepted 2026-07-27

- The read-only inventory found no active scrape/query/transaction, ungranted
  lock, vacuum, index build, rewrite, replication slot, standby, archive
  backlog, temp file, PostgreSQL log, core dump, or same-drive spool/curl
  scratch. Docker data/build cache is on `/mnt/storage`, not the FST drive.
- `pg_wal` contained `11,089,739,776` bytes in 661 PostgreSQL-managed future
  recycled segments. One standard checkpoint and a safe reload test reclaimed
  zero bytes. WAL was not deleted manually and is not counted as durable
  full-scrape capacity because the next scrape would reuse/reallocate it.
- The only accepted object family was the retired logical shadow's four
  non-constraint secondary trees: `ix_lce_scope_rank`,
  `ix_lce_last_changed`, `ix_lev_open_versions`, and
  `ix_lev_from_scrape`. They owned 36 child indexes and zero constraints.
  Production has no logical reader, and its only writer is default-off and
  startup-rejected.
- A transactional drop/rollback completed in `6.121 ms`. Bounded current and
  version fingerprints were exact; primary-key fallback plans remained
  bounded. Exact concurrent child rebuild, parent creation, and attach SQL was
  checksummed before production mutation.
- The production transaction dropped the one owner-card family. Database size
  changed from `3,829,657,859,763` to `3,811,368,810,163` bytes, reclaiming
  `18,289,049,600` bytes. Immediate free space changed from
  `48,858,976,256` to `67,148,181,504` bytes.
- `13/13` public route/export/history/ranking fingerprints matched. All
  `39,820,273` current rows, `194,171,215` version rows, 20 primary-key
  constraints, and logical sample fingerprints remained exact. `119/119`
  targeted tests and the Release build passed.
- The terminal corrected `60,392,999,803`-byte start guard passed with
  `6,754,436,229` bytes of margin. Optional builds/rewrites and the
  logical-table truncate remain separately blocked; the worker remains held
  and snapshot reuse remains default-off.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/post-scrape-1265-capacity-recovery-20260727T0011Z`.

### PG-3 proxy-retuned disabled-writer baseline - rejected before publication 2026-07-27

- Scrape `1266` completed `8,232/8,232` manifests, `592,631` pages, and
  `59,095,126` reported entries with zero writer, parse, retry-exhaustion, or
  publication-critical phase failures. Network plus writer drain improved
  `11.02%` to `4:54:57.902`.
- A Band Duets schema-ensure deadlock was repaired in the same frozen window.
  Commit `6651ebd9` serializes future ensures and retries one PostgreSQL
  deadlock. Commit `4121e7e5` adds real concurrent rebuild coverage and makes
  exhausted per-type ranking failures reject the publication-critical phase.
- The old worker then entered an unbounded deferred-registration/rivals phase
  and exited before publication. Guarded recovery was dry-run/rollback proven,
  then marked `1266` failed at
  `post_process_no_progress_abandoned`, removed the freeze, retained published
  `1236`, marked the worker offline, and verified zero candidate mappings,
  active worker queries, ungranted/advisory locks, or maintenance work.
- Worker liveness no longer updates the operation progress timestamp.
  Post-process phases and deferred-registration items now advance durable
  progress explicitly; deferred sync has a 30-minute best-effort timeout. The
  autonomous watchdog defaults to 45 minutes without progress and defers while
  a worker-owned PostgreSQL query is active.
- Full logical fingerprints remained exact for `39,820,273` current and
  `194,171,215` version rows with zero scrape-`1266` logical touches. The
  destructive truncate gate remains `NOT_CLEARED_NO_PUBLICATION`.
- Final measured free space is `32,507,674,624`; another corrected full scrape
  is blocked by `27,885,325,179` bytes. Production proxy settings reverted to
  `400 / 2 / 1`; service and held worker image
  `fstservice:scrape1266-recovery-4121e7e5` are deployed, and worker restart is
  `no`.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/proxy-retune-disabled-writer-baseline-20260727T004228Z`
  and
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/stale-scrape-1266-recovery-20260727T184133Z`.

### PG-3 ORPHAN-RECLAIM - accepted 2026-07-27

- The emergency reclaim guard initially rejected the phase at
  `32,675,258,368` free bytes because the guard required the
  `60,392,999,803`-byte scrape window even for a zero-scratch action. The
  accepted repair adds an explicit conservative `--expected-reclaim-bytes`;
  default behavior remains blocked, and the exception passes only when the
  projected result restores the emergency window with no maintenance conflict.
- Exact current/deployed source, `DatabaseInitializer`, dynamic-name, tooling,
  git-history, database dependency, statement, and binary searches proved ten
  Tier 1 objects had no runtime owner. Dirty/shadow content ended at scrape
  `1146`; 27 later scrapes completed through published `1236`.
- Tier 1 truncated nine retained schemas and dropped only
  `notification_cleanup_audit_20260509`, a dated one-off audit table. No
  statement used `CASCADE`. Database size fell by `10,027,671,552` bytes and
  filesystem free space rose by `10,027,778,048` bytes.
- `band_team_rank_history_latest_v2` was proven to be writer/change-detection
  state rather than a public read source. Production
  `BandRankHistory__Mode=Disabled`; `3,000/3,000` bounded edge samples matched
  retained v2 history points. `rank_history_latest` had no current exact owner
  and was stale: `4,386/4,500` bounded rows differed from newer retained
  `rank_history`.
- A separate Tier 2 transaction truncated those two latest-state surfaces,
  reclaiming `18,553,454,592` database bytes and `18,553,565,184` filesystem
  bytes while retaining all schemas, partitions, and primary keys.
- Combined database reclaim was `28,581,126,144` bytes. Final free space is
  `64,001,667,072`, so the corrected full-run guard passes with
  `3,608,667,269` bytes of margin. Optional builds and rewrites remain below
  the seven-day threshold.
- Pre-action, post-Tier-1, and final captures retained exact `13/13`
  route/export/history/ranking fingerprints. Postgres, service, web,
  publication `1236`, worker-offline state, locks, and constraints remained
  healthy through immediate and 60-second monitoring.
- `player_score_observations` remains intact at `10,167,937` rows and
  `12,682,354,688` bytes. Its two writers are default-off in the deployed
  image, but truncate remains blocked on one complete writer-off publication
  and exact public parity.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/orphan-reclaim-20260727T193224Z`.

### PG-3 scrape 1267 disabled-writer publication - accepted 2026-07-28

- Scrape `1267` completed `8,232/8,232` manifests, `592,731` pages,
  `59,105,529` reported entries, zero writer failures, and all 10
  publication-critical phases on the accepted `800 / 32 / 4` proxy candidate.
- Network plus writer drain was `5:02:22.661`, `8.79%` faster than scrape
  `1265` and `2.51%` slower than scrape `1266`. The full publication wall
  clock was `11:57:09.997`.
- Publication advanced atomically to `1267`, public reads unfroze, and
  `6,174` complete source mappings own `39,937,029` physical rows. Two
  post-publish suites were HTTP `200` and `13/13` fingerprint-exact.
- Full logical hashes remained exact for `39,820,273` current rows and
  `194,171,215` version rows. Scrape `1267` touched zero logical rows, wrote
  zero logical metrics, and produced no positive logical read counter delta.
  The logical-shadow destructive parity gate is **CLEARED**; no truncate ran.
- Minimum free space was `18,203,201,536`, `3,632,051,333` above the floor.
  Final measured free space was `41,145,516,032`, so the next full scrape,
  optional builds, and rewrites remain blocked. The worker remains held.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/scrape-1267-guarded-publication-20260727T201218Z`.

### PG-3 LOGICAL-RETIRE destructive reclaim - accepted 2026-07-28

- Independent live recapture reconfirmed published scrape `1267` unfrozen,
  `8,232/8,232` complete manifests, zero writer failures, all 10
  publication-critical phases complete, `6,174` complete published mappings,
  and zero logical rows or metrics touched by scrape `1267`.
- Fresh full-table manifests exactly matched readiness:
  `39,820,273` current rows /
  `054b9bbeb52d6670b4adee9fc7afcc101977132a20cecaf14fcc30690a69f3f2`
  and `194,171,215` version rows /
  `c9ab56adc1a983c62be0e3cc5dbe480ef6b6993a41de601638197cb394424313`.
  A controlled 13-fingerprint public window produced no logical table or
  statement-counter delta.
- Dependency recapture found zero inbound/outbound foreign keys, non-internal
  triggers, views, materialized views, routines, rules, publication tables,
  or prepared statements. The exact live manifest retained 20 relations,
  20 primary-key indexes, 20 constraints, schema DDL, deterministic samples,
  and the fail-closed current rebuild/version-baseline package.
- A rollback-only rehearsal and the production action each completed in about
  130 seconds. The committed short-timeout transaction truncated only
  `leaderboard_current_entries` and `leaderboard_entry_versions`, implicitly
  including their 18 leaves, without `CASCADE`. It retained schemas, all
  primary keys, and `leaderboard_logical_write_metrics`.
- The target family fell from `123,173,888,000` to `294,912` bytes. Database
  size fell from `3,823,878,641,331` to `3,700,705,048,243` bytes, an exact
  `123,173,593,088`-byte database reclaim. Stable filesystem free space rose
  from `41,158,270,976` to about `164,328,067,072` bytes.
- Pre-commit, immediate post-commit, and 60-second post-commit public captures
  were HTTP `200` and `13/13` byte-exact. Published `1267` remained unfrozen;
  Postgres, service, and web stayed healthy; the worker remained offline; and
  no query, ungranted lock, vacuum, or index build remained.
- Final reclaim and scrape guards pass. One corrected full scrape has more
  than `103.9 GB` of margin, while optional builds/rewrites remain below the
  seven-day threshold. The next lowest-risk storage phase is an independent
  scrape-`1267` writer-off gate evaluation for `player_score_observations`;
  do not combine it with this completed reclaim.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/logical-retire-executed-20260728T092804Z`.

### PG-4.2 - Separate semantic score changes from derived rank changes

The current logical version volume is 15.39M rows for one scrape. Evaluate
whether row fingerprints/version history should include derived rank fields
when separate rank-history tables already exist.

Run three models:

1. current full-row version;
2. score/performance semantic version plus separate rank history;
3. scope fingerprint plus event-only row history.

### PG-4.3 - Diff projections and rankings

Use staged insert/update/delete for low-change scopes and retain full replace or
build/swap above a measured change-ratio threshold.

### PG-4.4 - Remove duplicate early band publication

Avoid writing current to published twice per scrape.

## Phase PG-5: Compact and govern history

**Decision:** Accepted research/evaluation  
**Dependencies:** PG-0 manifests and PG-1 correctness

### PG-5.1 - Band v2 history owner and retention

Current v2 points use about 799 GB and major indexes recorded zero scans since
July 7. History snapshot mode was disabled in the audited scrape.

Work:

1. Identify every route/export/replay owner.
2. Record date/scrape coverage and fingerprints.
3. Establish retention and latest-state requirements.
4. Evaluate UUID/integer team dictionary and binary fingerprints.
5. Partition future points by date/range if retention is date-based.

Execution decision:

- owner/read/coverage proof complete;
- compact v3 design, bounded pilot, and Duets production promotion accepted;
- keep all dates and PostgreSQL public ownership;
- Duets v2 retired for `102,101,475,328` net database bytes; Trios and Quad
  remain v2 and must execute in that order under fresh guards.

### PG-5.2 - Rank-history latest-state redesign

Current rank-history snapshots consume most ranking time and hundreds of GB.
Maintain latest state incrementally and keep append-only changes only when rank
or rating actually changes.

### PG-5.3 - Bounded Parquet/DuckDB artifact pilot

Good candidates:

- old physical snapshot slices;
- old band v2 point ranges;
- scrape phase timings/logical metrics;
- ranking evaluation slices.

Rules:

- same FST drive;
- bounded source range;
- schema/count/range/fingerprint/checksum manifest;
- rehydration drill;
- PostgreSQL remains the live source of truth.

## Phase PG-6: Migration, vacuum, WAL, and memory governance

**Decision:** Accepted  
**Dependencies:** PG-0

### PG-6.1 - Introduce versioned migrations

Move monolithic startup and runtime DDL into:

- version ledger;
- advisory lock;
- short lock timeout;
- explicit statement timeout;
- idempotent migration;
- rollback note;
- schema manifest test.

### PG-6.2 - Tune autovacuum per hot table

Observed dead-row pressure includes:

- about 50.5M dead rows in `composite_rank_history`;
- 12-15% dead rows in several logical current/version partitions;
- about 1.9M dead rows in current band duets.

Use table-specific scale factors/thresholds and monitor vacuum duration, WAL,
index cleanup, and API impact.

### PG-6.3 - A/B work memory and temp behavior

Production `work_mem=256MB` is per sort/hash operation and can multiply across
parallel workers/connections. Test lower job-local values and targeted
`SET LOCAL` overrides on the 43.5M-temp-block query family.

### PG-6.4 - A/B WAL/checkpoint configuration

Do not blindly increase buffers. Measure:

- WAL bytes and buffer-full events;
- checkpoint write/sync time;
- backend WAL waits;
- scrape/post-process latency;
- recovery implications.

## Phase PG-7: Archive and destructive reclaim

**Decision:** Maintenance-window-required after parity  
**Dependencies:** PG-1 through PG-6, backup/restore, manifests, rehydration

1. Drop only proven unused secondary indexes, one at a time.
2. Execute the approved `player_score_observations` ownership migration,
   including archive/drop only after its manifest and parity gate.
   The 2026-07-26 package prefers truncate before drop, preserves exact schema
   DDL, rebuilds solo rows from `score_history`, and can rebuild only a current
   band baseline rather than discarded historical band observations.
3. Archive/prune exact snapshot/history ranges.
4. Prefer partition-level or streaming low-scratch operations.
5. Use `pg_repack` only when the FST drive has enough scratch for the exact
   object and indexes.
6. Clean the manifested nullable score-history duplicates and promote the
   uniqueness constraint.
7. Validate disk, WAL, locks, counts, ranges, fingerprints, routes, and restore
   after every object.

The largest snapshot/history tables cannot currently be safely repacked with
about 285.49 GB free after incomplete Trios candidate reclaim. The candidate
is no longer retained; optional rewrites remain blocked below the seven-day
headroom threshold.

The same 2026-07-26 owner phase added checksum-guarded packages for
`8,706,752,512` bytes of stale `scrape_dirty_*` work state and
the legacy `leaderboard_entries_*` surface, now `40,825,225,216` bytes.
ORPHAN-RECLAIM
executed the dirty cleanup and retained all four empty schemas/primary keys.
Legacy cleanup still requires migration of publication-critical
`PostScrapeBandExtractor` and all direct legacy helpers before supplemental
dual writes can be disabled.

## Projected outcomes

| Outcome | Projection/target |
|---|---|
| Immediate capacity | Observation retirement reclaimed `12,682,330,112` bytes; legacy `leaderboard_entries_*` remains blocked at `40,825,225,216` bytes |
| Physical growth | Up to about 14 GB/full scrape avoided when all scopes are reused; proportional savings for partially changed scrapes |
| Logical growth | Substantial reduction if derived rank-only churn is separated from score-state versions |
| Query latency | >=50% p95 reduction on published/current resolver A/Bs; >=90% read reduction on band membership |
| Temp I/O | >=70% reduction on the broad current-state query |
| Ranking | Rank-history snapshot phase initially <45 minutes |
| Long-term reclaim | Hundreds of GB to more than 1 TB only after archive/rehydration/live parity |
| Correctness | No active-unpublished row exposure; complete fingerprints and published source mapping |

## Explicitly rejected shortcuts

- Do not replace PostgreSQL as the live source of truth.
- Do not run `VACUUM FULL`, large rewrites, or broad repack with 275 GB free.
- Do not use another drive for FST scratch/archive/repack.
- Do not drop zero-scan primary/unique indexes without ownership proof.
- Do not prune history before manifest, restore, rehydration, and live-scrape
  parity.
- Do not increase `work_mem`, WAL, or parallelism from intuition alone.
