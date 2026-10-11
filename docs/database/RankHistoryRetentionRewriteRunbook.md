---
status: living-runbook
owner: data
last_verified: 2026-10-11
last_verified_commit: 46ab42a3
sources:
  - tools/postgres-rank-history-retention-rewrite.py
  - tools/postgres-rank-history-retention-rewrite.test.py
  - tools/postgres-rank-history-retention-rewrite-drill.py
  - tools/postgres-snapshot-archive-retire.py
  - FSTService/Persistence/RankHistoryRetention.cs
  - FSTService/Persistence/DatabaseInitializer.cs
  - docs/decisions/0013-rank-history-30-day-retention.md
update_triggers:
  - Rank-history table layout, retention semantics, state table, archive format, or rewrite/rollback commands change.
---

# Rank-history retention rewrite

This procedure reduces `rank_history` (nine instrument partitions) and
`composite_rank_history` to the retention window once. After it, the worker's
daily retention keeps them there (see
[data storage: rank-history retention](../architecture/data-storage.md#rank-history-retention)
and [ADR 0013](../decisions/0013-rank-history-30-day-retention.md)). The
operator authorized a 30-day window on 2026-10-11.

## What is kept

The cutoff is the UTC build day minus `--retention-days` (default 30). Each
table keeps every row dated after the cutoff and each account's newest row
dated at or before it. The worker's
`DatabaseMaintenance:RankHistoryRetentionDays` must be the same value.

## How it works

`tools/postgres-rank-history-retention-rewrite.py` works one table at a time,
smallest partition first and composite last:

1. **build** (no locks on writers): in one repeatable-read transaction, create
   `<table>_rtn_new` with `fillfactor = 100`, aggregate per-date counts and
   each account's boundary date in one scan, copy the kept rows dated before
   the build day, add a bound `CHECK` for partitions, then build the primary
   key and a BRIN index on `snapshot_date`. The table is then set to
   `fillfactor = 70`, analyzed, and verified: per-date counts after the cutoff
   must match the source, and there is exactly one boundary row per account.
   Rows dated before the build day are never written again, so the build
   needs no lock.
2. **swap** (fetch window only): one transaction locks the replacement
   exclusively and takes a `SHARE` lock on the table, waiting up to 3 s so the
   server cancels any autovacuum holding them (the replacement is built with
   autovacuum off, re-enabled here). It copies rows dated on or after the
   build day, then exchanges names with a 500 ms lock timeout, because those
   locks block readers. A partition is detached and the replacement
   attached (the bound `CHECK` skips the attach scan), and its BRIN index is
   attached to the parent's `ix_rh_retention_cutoff_brin`. Composite's
   renamed BRIN index becomes `ix_crh_retention_cutoff_brin`. The same
   transaction records the cutoff in `rank_history_retention_state`. Lock
   timeouts and deadlocks retry 12 times, then defer. The bound `CHECK` is
   dropped afterwards.
3. **archive** (each table starts in a fetch window): `pg_dump -Fc -Z zstd:3`
   of each detached `<table>_rtn_old` to the archive root. Its TOC must hold
   the table, data, and primary key, and a stream restore in a disposable
   `postgres:17` container must count the rows the build and swap recorded.
4. **drop**: drops `<table>_rtn_old` only when its archive's SHA-256 matches
   and the table is standalone.

Before the first partition swap the tool creates `ix_rh_retention_cutoff_brin`
`ON ONLY rank_history`; it becomes valid when the last partition's index is
attached. The tool also creates `rank_history_retention_state`. Running the
tool before deploying the 30-day worker means the schema initializer finds
both and never builds a BRIN index on a large table (it builds them only below
20 million estimated rows).

The tool shares the snapshot retirement flock, so it never overlaps
`fst-snapshot-retire-auto`, and it refuses to start a build when the FST drive
is below the retirement safety floor, a container is unhealthy, or more than
20 lock waits are active. The manifest is
`fst-data/archives/rank-history-retention/manifest.json`.

## Procedure

```bash
cd /home/sfenton/FortniteFestivalLeaderboardScraper
T=tools/postgres-rank-history-retention-rewrite.py
PYTHONDONTWRITEBYTECODE=1 python3 $T plan --sample     # read-only
PYTHONDONTWRITEBYTECODE=1 python3 $T run               # builds and swaps, waiting for fetch windows
PYTHONDONTWRITEBYTECODE=1 python3 $T archive           # after a scrape passes on the new tables
PYTHONDONTWRITEBYTECODE=1 python3 $T drop
```

`build` and `swap` can also run separately, and `--tables` limits any command.
Watch the API, `pg_locks`, and the scrape while each swap runs. Deploy the
30-day worker once every table is swapped.

## Rollback

Before `drop`, `rollback <table>` restores the original. It first gives the
detached original a validated bound `CHECK` and a BRIN index, with no effect on
the live table. It then upserts the rows the replacement gained since the
build day, swaps the names back, and deletes the scope's state row. The
replacement is kept as `<table>_rtn_new`.

After `drop`, restore from the archive into a scratch name and copy back the
rows you need:

```bash
docker exec -i fst-postgres pg_restore -U fst -d fstservice --no-owner < <archive>.dump
# creates <table>_rtn_old; INSERT ... SELECT ... ON CONFLICT DO NOTHING into the live table, then drop it
```

Restored old rows make the daily retention delete them again unless
`rank_history_retention_state` is cleared for the scope and the retention
window is raised first.

## Validation

```bash
PYTHONDONTWRITEBYTECODE=1 python3 tools/postgres-rank-history-retention-rewrite.test.py
# disposable postgres:17; build, a write between build and swap, swap, rollback, archive, drop
PYTHONDONTWRITEBYTECODE=1 python3 tools/postgres-rank-history-retention-rewrite-drill.py
```
