---
name: postgres-database-expert
description: PostgreSQL DBA/schema/query/index/operations advisor seeded by OSS PostgreSQL skills, adapted for FortniteFestivalLeaderboardScraper live-safety, storage, and historical correctness rules.
license: Repository-local synthesis; see database-management/references/attribution.md
---

# PostgreSQL Database Expert Skill

Use this advisor for Postgres-specific schema, queries, indexes, locks, vacuum/analyze, JSONB, time-series/event tables, migrations, backup/restore, and operational health in FortniteFestivalLeaderboardScraper.

This skill is fed by OSS PostgreSQL table-design, PostgreSQL operations, SQL optimizer, database admin, and database optimizer skills. Repository rules override generic Postgres advice.

## Required workflow

1. Protect live FST service/API freshness first. Check Docker health, Postgres readiness, locks/long queries, CPU, memory, disk, and scrape/publication timing before heavy probes or DDL.
2. Probe before changing. Use `pg_stat_activity`, `pg_locks`, table/index size, freshness, row counts, and bounded `EXPLAIN` before recommending changes.
3. Use `EXPLAIN (ANALYZE, BUFFERS)` only when the query is bounded and safe for current load.
4. Design indexes from observed access paths. Prefer composite, partial, covering, BRIN, GIN, or GiST only when the query shape needs them; include write/maintenance cost.
5. Keep migrations idempotent and lock-aware. Use short lock/statement timeouts and concurrent index creation for large live tables when appropriate.
6. Avoid broad table rewrites, `VACUUM FULL`, column drops, non-concurrent large index builds, and destructive pruning outside explicit maintenance approval.
7. Preserve timestamp and publication semantics: Epic/API timestamps, scrape IDs, derived `computed_at`, publication state, and modeled latency must remain auditable.
8. Follow existing schema conventions unless there is a clear reason to change; do not blindly import outside defaults such as UUIDs or identity columns into established tables.

## Postgres review checklist

| Area | What to inspect |
|---|---|
| Query path | predicates, joins, order, limits, row estimates, buffers, temp files |
| Indexes | left-prefix fit, FK/join columns, partial predicates, covering columns, bloat/write cost |
| Time-series/event data | timestamp filters, natural clustering, BRIN suitability, retention/archive needs |
| JSONB | whether typed columns are better, GIN/path indexes, payload size and TOAST pressure |
| Maintenance | dead tuples, vacuum/analyze freshness, autovacuum pressure, table rewrites |
| Safety | lock duration, statement timeout, rollback, backup/restore, live FST impact |

## Report template

| Surface | Current evidence | Postgres issue | Candidate fix | Lock/load risk | Validation | Rollback | Decision |
|---|---|---|---|---|---|---|---|
| `<table/query>` | `<plan/stats>` | `<cause>` | `<fix>` | `<risk>` | `<parity/bench>` | `<path>` | `<tier>` |
