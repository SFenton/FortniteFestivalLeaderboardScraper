---
status: decision
owner: data
last_verified: 2026-10-11
last_verified_commit: 46ab42a3
sources:
  - docs/database/RankHistoryRetentionRewriteRunbook.md
  - tools/postgres-rank-history-retention-rewrite.py
  - FSTService/Persistence/RankHistoryRetention.cs
  - FSTService/Persistence/InstrumentDatabase.cs
  - FSTService/Persistence/MetaDatabase.cs
update_triggers:
  - The rank-history retention window, cutoff semantics, state table, or rewrite procedure changes.
---

# ADR 0013: Keep 30 days of rank history and reach it by copy-swap

## Context

`rank_history` (nine instrument partitions) and `composite_rank_history` give
every ranked account one row per UTC day. On 2026-10-11 they held about 1.49
billion rows and 390 GB, growing about 3 GB a day. Retention was 365 days, but
history only began on 2026-04-24, so nothing had ever been deleted, and the
cleanup phase was pressure-gated and skipped every scrape anyway. The web app
charts 30 days, the API defaults to 30, and composite history has no public
reader. The operator asked for a 30-day window.

At 30 days about 79% of the rows go. Row-by-row deletion would scan and
WAL-log most of each table, leave the space in place until a rewrite, and run
inside post-processing. `VACUUM FULL` and `pg_repack` rewrite the whole table
under heavier locks or with triggers on hot writers.

## Decision

- The worker keeps every row dated after the cutoff (UTC today minus
  `DatabaseMaintenance:RankHistoryRetentionDays`, default 30) plus each
  account's newest row at or before it, so the latest-row invariant and the
  31-day chart window hold. It deletes once per cutoff advance per scope,
  recorded in `rank_history_retention_state`, and the cleanup is no longer
  pressure-gated. A large scope without a state row is refused rather than
  deleted row by row.
- The one-time reduction is a host tool: build a replacement per table from
  immutable rows, swap it in with one lock-bounded transaction during a fetch
  window, archive the old table with `pg_dump`, and drop it after the archive
  verifies.

## Consequences

- About 300 GB is reclaimed and daily growth stays near one day's rows.
- Rank history older than 30 days is gone from the database. The archives on
  the FST drive are the only copy until the operator removes them. The player
  data export now carries about 30 days of rank history.
- Daily retention deletes about one day of rows per scope after each UTC
  midnight, once.
- Band rank history keeps its own `BandRankHistory:RetentionDays`.
