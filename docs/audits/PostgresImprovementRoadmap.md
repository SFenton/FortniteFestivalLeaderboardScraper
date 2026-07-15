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

## Executive decision

PostgreSQL remains the correct durable source of truth. The urgent issue is
capacity and write amplification, not platform replacement.

The database is 3.324 TB on a 3.6 TB FST filesystem that is 93% used, leaving
275 GB. Physical solo snapshots consume about 1.665 TB and add an estimated
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
3. Archive/prune exact snapshot/history ranges.
4. Prefer partition-level or streaming low-scratch operations.
5. Use `pg_repack` only when the FST drive has enough scratch for the exact
   object and indexes.
6. Clean the manifested nullable score-history duplicates and promote the
   uniqueness constraint.
7. Validate disk, WAL, locks, counts, ranges, fingerprints, routes, and restore
   after every object.

The largest snapshot/history tables cannot currently be safely repacked with
only 275 GB free. Repack is blocked until low-scratch reclaim creates headroom.

## Projected outcomes

| Outcome | Projection/target |
|---|---|
| Immediate capacity | 100-175 GB possible from proven unused secondary indexes; 11 GB additional observation-table decision |
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
