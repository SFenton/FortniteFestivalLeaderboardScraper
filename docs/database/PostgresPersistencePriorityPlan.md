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

### Phase 6: logical leaderboard version persistence

Phase 6 added shadow logical persistence while keeping physical snapshot tables authoritative for reads:

- `leaderboard_current_entries`
- `leaderboard_entry_versions`
- `WriteLogicalLeaderboardVersions` feature flag
- dual-write from `_le_staging`
- rollback for incomplete/orphaned logical artifacts
- fast truncate rollback for all-invalid artifacts
- OOM-safer curl fallback logging

Production eval scrape `1214` completed and was published after manual recovery. Commit: `02460b13 Add logical leaderboard version persistence`.

### Phase 7: logical write metrics

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

## Priority order

### Priority 1: live-safe storage inventory and headroom plan

Goal: identify enough reclaimable or movable space to safely run the next evaluation without risking Postgres availability.

Allowed by default:

- Read-only size inventory by relation, tablespace/path, indexes, dead tuples, and autovacuum state.
- Docker/container health, Postgres locks, long queries, disk, CPU, and memory probes.
- Documentation of candidate actions and exact risk/benefit.

Requires explicit approval before action:

- Data deletion/pruning.
- `VACUUM FULL`, table rewrites, `CLUSTER`, non-concurrent large index builds, or `pg_repack`.
- Moving active Postgres data.
- Restarting production services or `fstworker`.

Proof gate:

- Report current free space, largest relations, reclaim candidates, lock/rewrite risk, expected reclaimed bytes, rollback/restore path, and maintenance window needs.

### Priority 2: non-destructive capacity relief

Goal: create temporary working headroom without changing FST's long-term data ownership.

Candidate actions to evaluate:

- Remove or rotate non-Postgres artifacts only if they are confirmed outside the active Postgres data path and safe.
- Use an approved alternate drive only as scratch for exports, repack workspace, or temporary staging.
- Prefer copy/manifest/checksum workflows over in-place destructive changes.
- Preserve published scrape `1214` and public-read safety during any operation.

Proof gate:

- Count/range/checksum or manifest parity for any moved/exported data.
- Public-read health before, during, and after.
- Confirm final long-term data remains on the FST drive.

### Priority 3: approved retention/archive decision

Goal: reduce durable storage growth only after the user approves exact retention semantics.

Candidate decisions:

- Define which physical snapshots are source-of-truth versus reconstructable artifacts.
- Decide whether old physical snapshot partitions/tables can be archived, compacted, or pruned after logical/current history is validated.
- Decide retention for band read projections and rank-history points.
- Document rehydration/regeneration paths before deleting anything.

Proof gate:

- Complete manifest coverage.
- Restore/rehydration path.
- Representative API/ranking parity.
- Explicit destructive-action approval.

### Priority 4: Phase 8 physical write skipping

Goal: reduce wall clock, WAL, CPU, and I/O by avoiding physical writes for unchanged leaderboard rows/scopes.

Starting hypothesis from Phase 7:

- About 69% of observed rows were unchanged at logical row level in scrape `1218`.
- Physical write skipping should therefore target both row-level and scope-level unchanged cases.
- Worst unchanged rate observed was `Solo_PeripheralVocals` at about 28%; several instruments were above 80% unchanged.

Implementation candidates:

- Skip physical snapshot writes for row fingerprints that match existing current logical state.
- Add scope-level fingerprints to avoid full physical refreshes for scopes that are unchanged.
- Keep physical snapshots authoritative until parity and rollback are proven.
- Keep the logical path feature-flagged and rollback-aware.

Proof gate:

- Targeted unit tests for new/changed/unchanged classification and replay.
- Local or bounded A/B benchmark using identical fixture data, resource caps, and query shapes.
- Full eval only after storage headroom exists.
- Matched metrics: wall clock, rows written, WAL bytes, disk read/write, CPU, memory, locks, publication correctness, and API parity.

### Priority 5: compact source-of-truth design

Goal: decide whether the logical current/version model can become authoritative and reduce dependence on massive physical snapshots.

Required evidence:

- Logical current/version tables reproduce leaderboard API reads exactly for representative songs/instruments/accounts.
- Ranking, improvement notifications, rivals/opps, band history, and public caches are parity-safe.
- Rollback can return reads to physical snapshots.
- Storage model shows durable reduction after migration and cleanup.

Promotion requires a separate Plan->Confirm->Act cycle and production eval.

### Priority 6: operationalize database management

Goal: make database operations repeatable and safe.

Required work:

- Keep `.github/skills/database-management` and focused database advisor skills up to date.
- Keep `.github/instructions/fst-postgres.instructions.md` aligned with actual docs and compose ownership.
- Add/update runbooks for storage inventory, scrape eval monitoring, rollback, publish recovery, and worker stop rules.
- Keep database docs explicit about Epic/API constraints, publication safety, and retention.

## Evaluation cadence for future phases

For approved eval phases:

1. Confirm `fstservice` and `fst-postgres` health, public-read freeze state, published scrape, disk headroom, and absence of dangerous long queries.
2. Start or deploy only the approved candidate.
3. Monitor every 60 seconds with visible status: scrape ID, phase/status, elapsed wall clock, DB locks/long queries, disk free, CPU, memory, and relevant write metrics.
4. When scrape/post-process/publish gates complete, stop `fstworker` before the next automatic scrape starts.
5. Wait for post-publish autovacuum or known cleanup to clear when relevant.
6. Evaluate against the approved wall-clock, I/O, CPU, memory, correctness, and publication gates.
7. Commit and push passing phases; reject or revert failed phases; then propose the next Plan->Confirm->Act step.

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
