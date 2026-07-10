# PostgreSQL Improvement Roadmap

**Audit date:** 2026-07-10  
**Container:** `fst-postgres`  
**Mode:** Current-system probe plus best-practices/performance/capacity roadmap  
**Implementation status:** All production probes were read-only. No schema,
data, index, configuration, vacuum, retention, or container changes were made.

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
