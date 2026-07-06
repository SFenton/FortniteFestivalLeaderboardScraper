# Postgres Persistence Priority Plan

This plan records the approved direction for improving FST Postgres persistence without starting additional database cleanup, migrations, or scrape evaluations automatically.

## Current production state

- Production compose ownership: `/home/sfenton/Docker/FestivalServiceTracker`.
- Active API service: `fstservice` is healthy.
- Worker: `fstworker` is intentionally stopped until storage headroom and the next evaluation plan are approved.
- Current published scrape: `1214`.
- Public reads: unfrozen.
- Experimental logical shadow tables from Phase 6/7 were truncated after approval to reclaim space.
- The failed/incomplete eval scrape `1218` was removed from `scrape_log` after approval.
- Long-term FST data must remain on the FST drive. Temporary alternate-drive use is allowed only as approved scratch/migration/repack workspace.

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

## Prioritization principles

1. Reclaim space first where the surface is likely derived and correctness risk is low.
2. Reduce write amplification before running another full scrape eval.
3. Do not trade permanent storage correctness for temporary free space.
4. Prefer read-only proof, manifests, parity checks, and reversible config/index changes before destructive work.
5. Separate "immediate free space" work from "future scrape cost" work; both matter, but the disk blocker must be cleared first.
6. Long-term FST data must remain on the FST drive. Alternate-drive use is scratch-only when approved.

## Risk-adjusted priority order

### Priority 0: freeze the current safe operating posture

Goal: keep production stable while reclaim work is planned.

Status and rules:

- `fstservice` and `fst-postgres` remain healthy.
- Published scrape remains `1214`.
- Public reads remain unfrozen.
- `fstworker` remains stopped until explicitly approved.
- No full scrape, destructive cleanup, `VACUUM FULL`, `pg_repack`, table rewrite, data move, or index drop happens from this plan alone.

Validation:

- Confirm service health, publication state, public-read freeze state, disk free, and absence of dangerous locks before any approved work.

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

- Archive old snapshots to scratch/export, verify manifest, then prune only after restore proof.
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
- Repack/vacuum only after sufficient scratch space exists.

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

- `VACUUM FULL`, repack, or table rewrites can need locks and scratch space.
- The system currently has too little headroom for risky rewrite work.
- Some surfaces may be better solved by dropping obsolete derived tables or indexes first.

Allowed candidate actions after approval:

- Plain vacuum/analyze where safe.
- `pg_repack` only with scratch-space and maintenance-window approval.
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
| Maintenance risk | Expected locks, disk scratch need, WAL/temp impact, service health risk, and worker state. |
| Approval statement | Exact object/action approved by the operator. |

## Do-not-do list until explicitly approved

- Do not restart `fstworker`.
- Do not run another full scrape/eval.
- Do not delete/prune historical data.
- Do not drop indexes or tables.
- Do not run `VACUUM FULL`, `CLUSTER`, `pg_repack`, or broad rewrites.
- Do not move active Postgres data off the FST drive.
- Do not use alternate-drive space except as approved temporary scratch/migration/repack workspace.

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
