---
name: database-architecture-expert
description: Database architecture and schema-design advisor seeded by OSS database-architect/schema-designer skills, adapted for FortniteFestivalLeaderboardScraper storage, timing, and live-safety rules.
license: Repository-local synthesis; see database-management/references/attribution.md
---

# Database Architecture Expert Skill

Use this advisor when designing or reviewing database architecture, schema boundaries, platform choice, normalization/denormalization, indexes, retention layout, hot/cold data ownership, or migration strategy.

This skill is fed by OSS database-architect and schema-designer guidance, but repository rules override general advice.

## Required workflow

1. Start with workload requirements: write path, read path, analytical scans, scrape/replay behavior, data freshness, retention, and consistency needs.
2. Classify the data owner: live Postgres source-of-truth, scrape/replay artifact, cold archive, derived cache, or experimental platform pilot.
3. Preserve historical leaderboard correctness semantics for every scrape/replay table, artifact, projection, and exported slice.
4. Normalize first for durable source-of-truth data; denormalize, materialize, or compact only where measured read/evaluation pressure justifies the added maintenance.
5. Prefer existing schema/repository conventions unless a measured issue requires a new pattern.
6. Design indexes from access paths, not speculation. Include write overhead, maintenance cost, and live FST impact.
7. Plan migrations before implementation: compatibility, lock risk, idempotency, backfill, rollback, validation, and docs.
8. Keep platform changes artifact-only or bounded A/B until correctness, performance, operations, and rollback evidence exists.

## FST defaults

- Postgres remains the durable source of truth for FST scrape state, leaderboard snapshots, rankings, publication state, Epic/API-derived records, and schema-owned audit data.
- DuckDB/Parquet-style paths are candidates for read-heavy artifacts and scrape/replay slices, not live source-of-truth replacement, unless a later promotion explicitly changes that.
- Columnar/time-series platforms such as ClickHouse or TimescaleDB require a workload-specific research and benchmark gate before implementation.
- Long-term active Postgres data belongs on the FST drive. Temporary alternate-drive workspace is allowed only for approved scratch, migration, or repack operations.

## Report template

| Surface | Owner | Access pattern | Schema/layout choice | Index/partition plan | Migration risk | Validation | Decision |
|---|---|---|---|---|---|---|---|
| `<data surface>` | `<postgres/artifact/archive/pilot>` | `<reads/writes/scans>` | `<design>` | `<indexes/layout>` | `<locks/backfill>` | `<parity/bench>` | `<tier>` |
