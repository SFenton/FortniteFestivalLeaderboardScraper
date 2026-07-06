# Postgres Persistence Priority Plan

This plan records the approved direction for improving FST Postgres persistence without starting additional database cleanup, migrations, or scrape evaluations automatically.

## Current production state

- Production compose ownership: `/home/sfenton/Docker/FestivalServiceTracker`.
- Active API service: `fstservice` is healthy.
- `fstservice` and `festivalweb` must remain live and usable during backend database work unless an exact restart/redeploy is explicitly approved.
- Worker: `fstworker` is intentionally stopped until storage headroom and the next evaluation plan are approved.
- Current published scrape: `1214`.
- Public reads: unfrozen.
- Experimental logical shadow tables from Phase 6/7 were truncated after approval to reclaim space.
- The failed/incomplete eval scrape `1218` was removed from `scrape_log` after approval.
- All FST database/storage/reclaim work must remain on the 4 TB FST drive. Do not use alternate drives for data, scratch, migration, export, or repack workspace unless SFenton explicitly overrides this rule later.

## Completed persistence phases

### [x] Phase 6: logical leaderboard version persistence

Phase 6 added shadow logical persistence while keeping physical snapshot tables authoritative for reads:

- `leaderboard_current_entries`
- `leaderboard_entry_versions`
- `WriteLogicalLeaderboardVersions` feature flag
- dual-write from `_le_staging`
- rollback for incomplete/orphaned logical artifacts
- fast truncate rollback for all-invalid artifacts
- OOM-safer curl fallback logging

Production eval scrape `1214` completed and was published after manual recovery. Commit: `02460b13 Add logical leaderboard version persistence`.

### [x] Phase 7: logical write metrics

Phase 7 added logical write classification metrics before attempting physical write skipping:

- `leaderboard_logical_write_metrics`
- metrics upsert from staging
- metrics rollback cleanup
- unit coverage for changed/new/unchanged classification and rollback cleanup

Commit: `2ac02445 Record logical leaderboard write metrics`.

Full-scale scrape `1218` produced useful metrics but failed before publish because Postgres ran out of space during ranking work:

| Metric | Rows | Share |
|---|---:|---:|
| Observed | 39,385,606 | 100.00% |
| Unchanged | 27,178,074 | 69.01% |
| Changed | 12,175,970 | 30.91% |
| New | 31,562 | 0.08% |
| Current upserts / versions opened | 12,207,532 | 31.00% |

Important finding: the logical model indicates most observed rows are unchanged, so physical write skipping is likely valuable. Phase 7 itself is blocked/rejected as a deployable eval outcome until storage headroom is solved.

## Immediate blocker

`/mnt/docker-storage` hosts the active Postgres bind mount and remains too full for another full scrape/post-process/publish cycle. The biggest consumers are physical leaderboard snapshots, band rank-history point tables, band read projections, rank/composite history tables, and their indexes.

No destructive retention prune is approved. Do not restart `fstworker`, run another full scrape, or start cleanup/migration work until the next plan is explicitly approved.

## Task status

| Task | Status | Notes |
|---|---|---|
| Phase 6 logical current/version dual-write | Complete | Implemented, deployed, evaluated on scrape `1214`, committed as `02460b13`. |
| Phase 7 logical write metrics | Complete | Implemented, deployed, committed as `2ac02445`; production metrics captured from failed scrape `1218`. |
| Experimental logical shadow cleanup | Complete | Approved cleanup truncated experimental logical shadow tables and removed incomplete scrape `1218`. |
| Database architecture evaluation | Complete | Read-only code review and production probes completed on 2026-07-06. |
| Autonomous scrape rollout | Blocked | `fstworker` remains stopped. Do not continue until storage/reclaim plan is approved. |
| Destructive retention/reclaim | Not approved | No deletes, drops, rewrites, repacks, or moves are approved by this document alone. |
| Next implementation phase | Pending approval | Must have explicit operator approval for any production mutation, destructive maintenance, worker restart, or full scrape/eval. |

## Architecture evaluation evidence (2026-07-06)

The current storage blocker is not a single table; it is the combined effect of physical snapshots, band history, band read projections, rank history, and wide indexes.

| Evidence | Current value | Interpretation |
|---|---:|---|
| `/mnt/docker-storage` free space | about 77 GB free, 98% used | Unsafe for another full scrape/post-process/publish eval. |
| Solo physical snapshots | 1,579 GB total; 681 GB heap; 898 GB index/toast | Largest storage target, but highest correctness risk. |
| Band rank-history points v2 | 799 GB total; 324 GB heap; 475 GB index/toast | Major storage and write-maintenance surface. |
| Band read projections | 398 GB total; 254 GB heap; 144 GB index/toast | Large derived surface; repo code did not reference several `band_read_*` names during review. |
| Solo/composite rank history | 230 GB total; 95 GB heap; 136 GB index/toast | User-visible history, with notable dead tuples and low-scan indexes. |
| Current band leaderboard entries | 129 GB total | Hot derived current-state surface; update/index cost matters. |
| Band identity/member facts | 108 GB total | High dead tuple ratios suggest cleanup/repack candidates after proof. |
| `pg_stat_database.temp_bytes` | 3,354 GB | Ranking/rebuild queries spill heavily; not a disk-free fix, but major I/O/CPU work. |
| `api_response_cache` | 6,597 rows, 106 MB, about 54 KB average JSON | Not a storage blocker; still avoid live request-time cache churn. |

Large low-scan indexes observed during the read-only probe:

| Surface | Example observed indexes | Size / scans | Priority meaning |
|---|---|---:|---|
| Band history points | `band_team_rank_history_points_*_ranking_scope_combo*` | 114 GB / 1 scan, 89 GB / 4 scans, 41 GB / 15 scans | Prove access paths, then replace/drop if unused. |
| Band read projections | `band_read_subject_row_pkey`, `ix_brsr_generation_subject_scope`, `band_read_hot_window_pkey` | 45 GB / 0, 34 GB / 0, 31 GB / 0 | Verify ownership and reader usage first; likely high-value reclaim. |
| Composite history | `ix_crh_retention_cutoff_account`, `ix_crh_latest` | 19 GB / 2, 19 GB / 8 | Validate retention/latest query paths before changing. |
| Rank history | instrument-account-date indexes | 3.5-10 GB each, fewer than 100 scans | Review whether primary key and query shape already cover actual reads. |
| Build/current band ranking indexes | build-table-named indexes on current/published tables | multiple 1-3 GB indexes with 0 scans | Verify whether stale build-name indexes are expected after table swaps. |

High dead tuple candidates observed:

| Surface | Dead tuple signal | Decision |
|---|---:|---|
| `band_members`, `band_member_stats`, `band_search_*_projection`, `band_entries_duets` | about 99% dead tuple ratio in stats | Reclaim only after proof and approved maintenance; may need vacuum/repack strategy. |
| `band_team_rank_history_points_v2_trios` | about 44.5% dead tuples | High-value maintenance candidate after history retention/index plan. |
| `band_team_rank_history_points_v2_duets` | about 24.9% dead tuples | High-value maintenance candidate after history retention/index plan. |
| Solo/composite rank history partitions | about 14-15% dead tuples on several large partitions | Consider after retention/index review. |

## Autonomous roadmap execution log

### [x] Phase A: P0-P2 read-only proof package (2026-07-06T22:36:16Z)

Mode: Current-system probe / retention feasibility. No schema, data, runtime config, service, index, table, worker, or scrape mutations were performed.

| Priority | Decision | Evidence | Remaining gate |
|---|---|---|---|
| P0 live safety | Accepted | `fstservice` `/readyz` returned `Healthy`; `festivalweb` was healthy and serving the app shell; Postgres was healthy; public reads were unfrozen; published scrape was `1214`; `fstworker` remained intentionally stopped/stale; no ungranted locks. | Continue checking before every approved phase. |
| P1 `band_read_*` ownership proof | Accepted for proof; blocked for reclaim | Source search found no active repo references outside this plan; `pg_views` found no view references; `pg_stat_statements` references were only diagnostic queries; representative `/api/songs` and `/api/leaderboard/{songId}/bands/Band_Duets?top=5` returned 200 without `band_read_*` usage. | Any quarantine/drop/repack/truncate requires explicit approval. |
| P2 low-scan index proof | Accepted for proof; blocked for changes | Read-only index inventory found large low-scan surfaces, including band rank-history points v2, rank/composite history, current/published band ranking projections, band search, and scrape-dirty indexes. Several primary keys have zero scans but are structural constraints and are not safe drop candidates without design review. | Any index drop/replacement requires exact object approval and rollback DDL. |

P1 `band_read_*` inventory:

| Table | Total size | Heap | Index/toast | Estimated rows | Stats scans | Interpretation |
|---|---:|---:|---:|---:|---:|---|
| `band_read_hot_window` | 191 GB | 160 GB | 31 GB | 174,369,920 | 0 seq / 0 idx | Derived read projection with no observed usage. |
| `band_read_subject_row` | 190 GB | 88 GB | 102 GB | 60,946,732 | 0 seq / 0 idx | Derived read projection with no observed usage. |
| `band_read_rank_anchor` | 12 GB | 4,974 MB | 7,713 MB | 12,570,308 | 0 seq / 0 idx | Derived read projection with no observed usage. |
| `band_read_scope_state` | 5,459 MB | 1,901 MB | 3,559 MB | 7,615,178 | 0 seq / 0 idx | Derived read projection metadata with no observed usage. |
| `band_read_generation` | 96 KB | 16 KB | 80 KB | 65 exact rows | 0 seq / 0 idx | Small metadata table. |
| `band_read_publication_state` | 24 KB | 8 KB | 16 KB | 1 exact row | 0 seq / 0 idx | Small metadata table. |

P1 highest-value `band_read_*` index candidates for approval-gated quarantine/drop:

| Index | Size | Scans | Notes |
|---|---:|---:|---|
| `band_read_subject_row_pkey` | 45 GB | 0 | Structural primary key; table-level quarantine is safer than isolated PK drop. |
| `ix_brsr_generation_subject_scope` | 34 GB | 0 | Non-primary covering index; potential drop candidate if table remains unused. |
| `band_read_hot_window_pkey` | 31 GB | 0 | Structural primary key; table-level quarantine is safer than isolated PK drop. |
| `ix_brsr_song_scope_team` | 21 GB | 0 | Non-primary read-path index; potential drop candidate if table remains unused. |
| `ix_brra_scope_sort` | 5,325 MB | 0 | Non-primary read-path index; potential drop candidate if table remains unused. |

Recommended P1 approval package:

1. Approve a non-destructive quarantine plan for `band_read_*` objects, not immediate deletion.
2. Rename tables/indexes or otherwise hide the surface in a reversible maintenance window while keeping `fstservice` and `festivalweb` live.
3. Monitor API routes and logs for failed references.
4. Drop only after observation, rollback proof, and explicit approval.

P2 low-scan giant index evidence:

| Group | Low-scan index count | Total size | Risk classification |
|---|---:|---:|---|
| Other indexes, including snapshot primary keys/current-state primary keys | 58 | 827 GB | Not a blanket drop pool; many are structural constraints despite low scans. |
| Band rank-history points v2 indexes | 9 | 474 GB | High-value design review target; history API parity required. |
| Rank/composite history indexes | 17 | 134 GB | Retention/latest-query ownership required before changes. |
| Band ranking projection indexes | 10 | 22 GB | Check generated build-table naming and current/published query plans. |
| Band search projection indexes | 4 | 19 GB | Validate search/profile routes before changes. |
| Band entries indexes | 5 | 6,304 MB | Must preserve scrape/write conflict checks and member lookups. |
| `scrape_dirty_band_team` indexes | 2 | 6,107 MB | Candidate for table-state proof; do not drop without dirty-workflow validation. |

P2 decision:

- Do not drop all low-scan indexes. Low `idx_scan` is useful evidence, but primary keys and unique constraints may be required for correctness, upserts, table swaps, or future writes.
- The highest safe next proof is to build per-index owner cards for non-primary, non-unique indexes first: query owner, source reference, endpoint/job dependency, replacement index if any, rollback DDL, and estimated reclaim.
- Primary-key/unique indexes should be handled through table/source-of-truth decisions, not isolated index drops.

### [x] Phase B: P3 snapshot publication correctness check (2026-07-06T22:41:14Z)

Mode: Current-system probe / live-safe non-destructive correction. No services were restarted, `fstworker` was not started, and no data was deleted.

Finding:

- `scrape_publication_state.published_scrape_id` was `1214`.
- `scrape_publication_state.public_reads_frozen` was `false`.
- `leaderboard_snapshot_state` had all 6,102 scopes finalized and active on snapshot `1218`.
- `scrape_log` contained `1214` but did not contain `1218`.
- Current-state read SQL uses `leaderboard_snapshot_state.active_snapshot_id` when public reads are unfrozen, and uses `scrape_publication_state.published_scrape_id` when public reads are frozen.

Live-safe correction applied:

```sql
UPDATE scrape_publication_state
SET public_reads_frozen = TRUE,
    public_reads_frozen_at = now(),
    public_reads_frozen_scrape_id = published_scrape_id,
    public_reads_frozen_reason = 'freeze-to-published-1214-after-1218-storage-failure',
    updated_at = now()
WHERE id = TRUE
  AND published_scrape_id = 1214;
```

Validation:

| Check | Result |
|---|---|
| `scrape_publication_state` | `published_scrape_id=1214`, `public_reads_frozen=true`, `public_reads_frozen_scrape_id=1214` |
| API health | `/readyz` returned `Healthy` after the correction |
| Web health | `festivalweb` remained healthy and served the app shell |
| Public solo route | `/api/leaderboard/{songId}/Solo_Guitar?top=3` returned 200 |
| Public band route | `/api/leaderboard/{songId}/bands/Band_Duets?top=3` returned 200 |
| Locks | No ungranted locks |

Decision:

- Accepted. Public/current-state reads are pinned back to the published scrape while storage/reclaim work continues.
- Keep `public_reads_frozen=true` until a later published scrape is safely promoted or a dedicated publication-state repair phase explicitly changes it.
- Treat any future `leaderboard_snapshot_state.active_snapshot_id` that points at a scrape missing from `scrape_log` as a correctness incident, not just a storage artifact.

### [x] Phase C: P4 band rank-history v2 proof (2026-07-06T22:43:00Z)

Mode: Current-system probe / history retention feasibility. No history rows, indexes, tables, workers, services, or configuration were changed.

Evidence:

| Surface | Total size | Heap | Index/toast | Estimated rows | Interpretation |
|---|---:|---:|---:|---:|---|
| `band_team_rank_history_points_v2_quad` | 365 GB | 139 GB | 226 GB | 329,537,184 | Largest band-history points partition. |
| `band_team_rank_history_points_v2_trios` | 288 GB | 118 GB | 170 GB | 323,710,688 | Large history surface with prior dead-tuple pressure. |
| `band_team_rank_history_points_v2_duets` | 146 GB | 67 GB | 78 GB | 197,216,976 | Smaller but still high-value surface. |
| `band_team_rank_history_latest_v2_*` | about 17.9 GB combined | about 8.2 GB | about 9.7 GB | about 21.5M combined | Latest-state delta detector; structural, not first reclaim target. |
| `band_team_rank_history_snapshot_v2` | 14 MB | 5.3 MB | 9.1 MB | 26,239 | Small metadata/freshness table. |

Metadata coverage:

| Band type | Completed snapshots | Date range | Source rows | Changed rows |
|---|---:|---|---:|---:|
| `Band_Duets` | 2,419 | 2026-04-26 to 2026-07-05 | 148,713,761 | 139,082,211 |
| `Band_Trios` | 7,713 | 2026-04-26 to 2026-07-06 | 227,017,957 | 203,548,764 |
| `Band_Quad` | 17,651 | 2026-04-26 to 2026-07-05 | 240,373,768 | 186,065,121 |

Representative public history route:

- `/api/rankings/bands/Band_Duets/{teamKey}/history` returned 200 with history data.
- `pg_stat_statements` then showed a `band_team_rank_history_points_v2` read returning 17 rows in about 9.35 ms with 28 shared blocks read.
- No ungranted locks were observed after the probe.

P4 decision:

- `band_team_rank_history_points_v2` is active user-facing history data, not an obsolete projection.
- Do not table-quarantine or broadly drop history points or latest-state indexes.
- Retention/index work must be history-semantics-first: define retention policy, prove endpoint parity, then target only redundant/non-public indexes or old date slices.
- Primary keys are structural. Low `idx_scan` on primary keys is not enough to drop them.
- The most promising next safe work is per-index owner cards for non-primary history indexes and a retention-policy design for old history slices.

## Prioritization principles

1. Reclaim space first where the surface is likely derived and correctness risk is low.
2. Reduce write amplification before running another full scrape eval.
3. Do not trade permanent storage correctness for temporary free space.
4. Prefer read-only proof, manifests, parity checks, and reversible config/index changes before destructive work.
5. Separate "immediate free space" work from "future scrape cost" work; both matter, but the disk blocker must be cleared first.
6. All work must remain on the 4 TB FST drive. Alternate-drive data, scratch, migration, export, or repack workspace is prohibited unless SFenton explicitly overrides this rule later.

## Risk-adjusted priority order

### Priority 0: freeze the current safe operating posture

Goal: keep production stable while reclaim work is planned.

Status and rules:

- `fstservice` and `fst-postgres` remain healthy.
- `festivalweb` remains healthy and users can use the app against the last published scrape.
- Published scrape remains `1214`.
- Public reads remain unfrozen.
- `fstworker` remains stopped until explicitly approved.
- No full scrape, destructive cleanup, `VACUUM FULL`, `pg_repack`, table rewrite, data move, or index drop happens from this plan alone.

Validation:

- Confirm service health, publication state, public-read freeze state, disk free, and absence of dangerous locks before any approved work.
- Confirm `fstservice` `/readyz`, `festivalweb` health, and at least one browser-visible app route after any explicitly approved service/web redeploy.

### Priority 1: prove and reclaim stale/derived band read projections

Goal: reclaim the best risk-adjusted space first.

Target surfaces:

- `band_read_hot_window`
- `band_read_subject_row`
- `band_read_rank_anchor`
- `band_read_scope_state`
- related `band_read_*` indexes

Why first:

- Observed group size is about 398 GB.
- Multiple large indexes showed zero scans.
- Repository search did not find active code references for several `band_read_*` table names.
- If these are obsolete derived projections, reclaim could be large with lower correctness risk than physical snapshot deletion.

Required proof before action:

- Confirm tables are not referenced by current deployed code, stored procedures, views, scheduled jobs, or API endpoints.
- Check `pg_depend`, view definitions, prepared jobs, and `pg_stat_statements` for table references.
- Capture row counts, size by table/index, min/max generation/scrape/date fields, and a manifest.
- Confirm published API responses do not depend on these tables.

Allowed candidate actions after approval:

- Rename quarantine first, then observe API/service behavior.
- Drop only after a rollback/restore path and observation window.
- Prefer dropping unused indexes before dropping tables if table ownership is uncertain.

Success metrics:

- Reclaimed bytes.
- No API/public-read regression.
- No failed queries referencing quarantined objects.

Decision tier: highest priority, proof-first.

### Priority 2: prove, replace, or drop low-scan giant indexes

Goal: reclaim index storage and reduce write/index-maintenance overhead without changing row ownership.

Target surfaces:

- Band rank-history points v2 low-scan indexes.
- Band read projection indexes.
- Composite/rank-history low-scan indexes.
- Current/published band ranking indexes with build-table-derived names and zero scans.
- `scrape_dirty_band_team` indexes if the table is empty/obsolete after current phase.

Why second:

- Potential reclaim is likely hundreds of GB.
- Dropping unused indexes can reduce future writes, WAL, vacuum, and checkpoints.
- Index drops are usually easier to roll back than data deletion, but rebuilds can be expensive and need disk.

Required proof before action:

- For each index: table, size, `idx_scan`, query texts, API endpoint owner, and whether the primary key or another index covers the path.
- Run plain `EXPLAIN` for representative reads; use `EXPLAIN ANALYZE` only in a safe bounded window.
- Confirm no maintenance or retention job needs the index.

Allowed candidate actions after approval:

- Drop clearly unused noncritical indexes one at a time.
- Replace broad btree indexes with narrower, partial, or differently ordered indexes only after matched query-plan proof.
- Use `CREATE INDEX CONCURRENTLY` for replacements on large live tables when required.

Success metrics:

- Reclaimed bytes.
- Lower WAL/index writes in the next scrape.
- No p95/p99 read regression for affected endpoints/jobs.

Decision tier: high priority, maintenance-window likely for some replacements.

### Priority 3: define source-of-truth and retention for physical leaderboard snapshots

Goal: unlock the largest storage win without breaking historical correctness.

Target surfaces:

- `leaderboard_entries_snapshot_*` partitions.
- Snapshot indexes, especially primary keys and score indexes.
- `leaderboard_snapshot_state`.
- Logical current/version tables and scope fingerprints used for parity proof.

Why third:

- Physical snapshots are the largest observed storage group at about 1.58 TB.
- They are wide full-row copies and duplicate much of `leaderboard_entries`, `leaderboard_current_entries`, and `leaderboard_entry_versions`.
- They are high correctness risk until logical/current reads and restore semantics are proven.

Required proof before action:

- Decide whether each snapshot generation is canonical, reconstructable, or disposable after publication.
- Prove logical current/version tables can reproduce representative leaderboard reads.
- Prove ranking, rivals/opps, player stats, improvement notifications, band history dependencies, API response cache, and exports remain correct.
- Build manifest coverage: scrape IDs, row counts, song/instrument counts, checksums/fingerprints, byte sizes, and restore path.

Allowed candidate actions after approval:

- Archive old snapshots only to approved locations on the 4 TB FST drive, verify manifest, then prune only after restore proof.
- Keep latest published and recent safety window on FST drive.
- Consider time/scrape-range partitioning for future snapshot retention if physical snapshots remain.

Success metrics:

- Reclaimed bytes, likely the largest possible win.
- Exact API parity for representative and sampled full-scope reads.
- Restore/rehydration time documented and tested.

Decision tier: high-impact but blocked until parity and approval.

### Priority 4: band rank-history points retention and index redesign

Goal: reduce the 799 GB band history surface while preserving user-visible history.

Target surfaces:

- `band_team_rank_history_points_v2_*`
- `band_team_rank_history_latest_v2_*`
- `band_team_rank_history_snapshot_v2`
- associated snapshot/team/date indexes

Why fourth:

- The surface is very large and has high index/toast overhead.
- Some large indexes have very low scan counts.
- Trios and Duets points show high dead tuple ratios.
- History is user-visible, so retention decisions are semantic, not purely technical.

Required proof before action:

- Define retention policy: all history forever, daily coalesced history, season-scoped history, or cold archive.
- Prove API history endpoints and status/freshness reads with representative teams/scopes.
- Verify whether low-scan indexes are unused or only used by rare admin/repair paths.
- Confirm whether row fingerprints and latest state can avoid same-day duplicate history writes.

Allowed candidate actions after approval:

- Drop/replace unused history indexes.
- Partition by time only for future layout or after a controlled migration.
- Archive old points by date/scope only with manifest and restore proof.
- Repack/vacuum only after sufficient scratch space exists on the 4 TB FST drive.

Success metrics:

- Reclaimed bytes and lower index write cost.
- Band history API parity.
- Reduced history snapshot wall clock, WAL, and temp reads.

Decision tier: high-impact, semantics-gated.

### Priority 5: Phase 8 unchanged row/scope physical write skipping

Goal: reduce future storage growth, WAL, CPU, and I/O for every scrape.

Starting evidence:

- Phase 7 observed 39,385,606 rows.
- 27,178,074 rows were unchanged, or 69.01%.
- Current upserts / versions opened were 12,207,532.
- Several instruments had more than 80% unchanged rows.

Target write paths:

- Solo snapshot insert from `_le_staging`.
- Legacy live `leaderboard_entries` merge when enabled.
- Logical current/version write path.
- Band writes and member stats when scope fingerprints prove unchanged.
- Projection refreshes that currently delete/reinsert unchanged scopes.

Candidate design:

- Add scope-level fingerprints before expensive staging/merge where possible.
- Skip full physical snapshot writes for unchanged scope generations.
- Skip row writes when row fingerprint matches logical current state.
- Keep physical snapshots authoritative until parity is proven.
- Preserve rollback by feature flag and by retaining old read paths.

Validation:

- Unit tests for new/changed/unchanged classification.
- A/B fixture benchmark with matched data and resource caps.
- Production eval only after disk headroom.
- Measure wall clock, WAL bytes, rows inserted/updated/deleted, temp bytes, CPU, memory, locks, and API parity.

Decision tier: essential future-cost reduction after reclaim headroom.

### Priority 6: rank/temp spill and ranking rebuild reduction

Goal: reduce database work even when storage is not being reclaimed.

Starting evidence:

- `pg_stat_database.temp_bytes` showed about 3,354 GB.
- Top temp writers include `_valid_entries`, `_latest_ranks`, `_band_rank_results`, `_band_song_rank_results`, and index builds on temp/build tables.

Target surfaces:

- Solo rank recomputation.
- Composite rank history.
- Band aggregate ranking rebuilds.
- Band song/team ranking build tables.
- Current/published ranking table index creation.

Candidate design:

- Reduce repeated temp-table materialization.
- Precompute or persist narrow current-state inputs.
- Avoid rebuilding rank outputs when source scope fingerprints are unchanged.
- Cap concurrent DB-heavy phases end-to-end, not just ranking internals.
- Evaluate whether sort keys/indexes can be narrower or partition-local.

Validation:

- Temp bytes per phase.
- Ranking wall clock by phase/instrument/band type.
- CPU, memory, disk I/O, and lock waits.
- Rank parity and API parity.

Decision tier: high database-work priority; not first for free-space blocker.

### Priority 7: dead tuple/bloat maintenance after reclaim headroom

Goal: reclaim table bloat only after enough free space exists and object ownership is proven.

Target candidates:

- `band_members`
- `band_member_stats`
- `band_search_member_projection`
- `band_search_team_projection`
- `band_entries_duets`
- `band_team_rank_history_points_v2_trios`
- `band_team_rank_history_points_v2_duets`
- selected rank-history partitions

Why later:

- `VACUUM FULL`, repack, or table rewrites can need locks and scratch space; this scratch space must be on the 4 TB FST drive unless SFenton explicitly overrides this rule later.
- The system currently has too little headroom for risky rewrite work.
- Some surfaces may be better solved by dropping obsolete derived tables or indexes first.

Allowed candidate actions after approval:

- Plain vacuum/analyze where safe.
- `pg_repack` only with 4 TB FST-drive scratch-space and maintenance-window approval.
- Rebuild derived projections from source if cheaper than repacking.

Validation:

- Before/after relation size.
- Dead tuple ratio.
- Query/runtime parity.
- Lock duration and API health.

Decision tier: important but after safer reclaim.

### Priority 8: hot read-path and cache pressure reduction

Goal: reduce steady-state DB CPU/I/O and cache churn.

Target surfaces:

- `/api/status` instrument counts.
- `/api/songs` JSON composition.
- `/api/leaderboard/{songId}/members/scores` fan-out.
- Public API response cache writes.
- Player profile fallback reads.

Candidate design:

- Replace `COUNT(*)` status paths with maintained per-instrument scrape counters.
- Split `/api/songs` base catalog from stats overlays or precompute publication-keyed payloads.
- Batch current-state fallback/profile reads.
- Make public cache publication-keyed and write primarily during publish/precompute, not on every live cacheable GET.
- Keep selected-player/band overlays outside public static cache unless explicitly keyed.

Validation:

- Query count per request.
- p50/p95/p99 latency.
- Response byte size and serialization time.
- `api_response_cache` write rate and bytes.
- Exact response parity for representative requests.

Decision tier: lower immediate storage reclaim, good operational efficiency.

## Architecture evaluation backlog

| Track | Target | Baseline evidence | Candidate change | Success metric | Safety gate |
|---|---|---|---|---|---|
| Space reclaim | `band_read_*` projections | 398 GB group; many 0-scan indexes | Prove ownership; quarantine/drop stale derived surfaces | Reclaimed bytes | API/query reference proof and rollback |
| Space + write cost | Low-scan giant indexes | 100 GB+ indexes with near-zero scans | Drop/replace after plan proof | Reclaimed bytes and lower WAL/index writes | Representative `EXPLAIN` and endpoint parity |
| Largest storage | Physical snapshots | 1.58 TB group | Archive/compact/prune after logical parity | Reclaimed bytes | Manifest, restore, API/ranking parity |
| History storage | Band rank-history v2 | 799 GB group | Retention/index redesign | Storage and history job time down | History API parity |
| Future scrape cost | Unchanged row/scope writes | 69.01% unchanged in P7 | Scope/row write skipping | WAL/rows written down | Full scrape parity |
| Temp/CPU/I/O | Ranking temp tables | 3,354 GB temp bytes | Reduce temp materialization/rebuilds | Temp bytes and wall clock down | Rank/API parity |
| Bloat | High-dead derived/history tables | 25-99% dead tuple ratios on candidates | Vacuum/repack/rebuild after headroom | Relation size down | Maintenance approval |
| Hot reads | Status/songs/member-score/cache paths | Count scans, fan-out, live cache writes | Maintained counters/projections, batched reads | p95/query count down | Response parity |

## Required proof package for every reclaim action

Before any approved reclaim action, produce a short proof package:

| Required item | Purpose |
|---|---|
| Object inventory | Tables/indexes, sizes, row counts, dead tuples, dependencies, and owners. |
| Access evidence | `pg_stat_user_indexes`, `pg_stat_statements`, source references, endpoint/job ownership, and representative query plans. |
| Correctness gate | API parity, row count/range/checksum/fingerprint parity, or manifest coverage depending on object type. |
| Rollback path | Rename-back, recreate index DDL, restore archive, regenerate projection, or read-source flag. |
| Maintenance risk | Expected locks, 4 TB FST-drive scratch need, WAL/temp impact, service health risk, and worker state. |
| Approval statement | Exact object/action approved by the operator. |

## Do-not-do list until explicitly approved

- Do not restart `fstworker`.
- Do not stop or break `fstservice` or `festivalweb` for backend/database work unless the exact restart/redeploy is explicitly approved.
- Do not run another full scrape/eval.
- Do not delete/prune historical data.
- Do not drop indexes or tables.
- Do not run `VACUUM FULL`, `CLUSTER`, `pg_repack`, or broad rewrites.
- Do not move active Postgres data off the 4 TB FST drive.
- Do not use alternate-drive space for data, scratch, migration, export, or repack workspace unless SFenton explicitly overrides this rule later.

## Evaluation cadence for future phases

For approved eval phases:

1. Confirm `fstservice` and `fst-postgres` health, public-read freeze state, published scrape, disk headroom, and absence of dangerous long queries.
2. Start or deploy only the approved candidate.
3. Monitor every 60 seconds with visible status: scrape ID, phase/status, elapsed wall clock, DB locks/long queries, disk free, CPU, memory, and relevant write metrics.
4. When scrape/post-process/publish gates complete, stop `fstworker` before the next automatic scrape starts.
5. Wait for post-publish autovacuum or known cleanup to clear when relevant.
6. Evaluate against the approved wall-clock, I/O, CPU, memory, correctness, and publication gates.
7. Commit and push passing phases; reject or revert failed phases; then continue to the next approved autonomous task or stop at the exact hard gate.

## Success criteria

Near-term success:

- Storage headroom is safe enough for a full eval without emergency cleanup.
- P7 metrics are preserved in docs and used to design P8.
- No public-read regression and no accidental worker scrape.

Medium-term success:

- Physical write volume falls materially for unchanged rows/scopes.
- Wall clock is maintained or improved.
- WAL, disk I/O, CPU, and memory pressure are significantly lower.
- Publication correctness and API parity remain proven.

Long-term success:

- FST stores leaderboard history in a compact, auditable source-of-truth model.
- Massive physical snapshots become either unnecessary, bounded, or safely retained as derived/rebuildable artifacts.
- Database operations have repeatable probes, manifests, rollback paths, and documented approval gates.
