---
name: duckdb-analytics-expert
description: DuckDB and Parquet analytics advisor seeded by OSS DuckDB skills, adapted for FortniteFestivalLeaderboardScraper artifact-only scrape/replay/export workflows.
license: Repository-local synthesis; see database-management/references/attribution.md
---

# DuckDB Analytics Expert Skill

Use this advisor when evaluating DuckDB, Parquet, CSV/JSON file querying, local analytical slices, scrape/replay exports, artifact profiling, or DuckDB-backed benchmark pilots.

This skill is fed by OSS DuckDB skills for direct file reads, attached database sessions, Friendly SQL querying, and file profiling. Repository rules override general DuckDB advice.

## Required workflow

1. Treat DuckDB as an artifact/scrape/replay companion by default, not the live FST source of truth.
2. Start from a bounded export or manifest. Do not point broad DuckDB scans at hot Postgres data during live-sensitive windows.
3. Prove correctness before speed: row counts, min/max timestamps, schema, checksums/fingerprints, sample parity, and scrape/replay parity when behavior could change.
4. Keep secrets out of DuckDB state files, query logs, committed artifacts, and repo-local `.duckdb-skills` directories.
5. Prefer read-only direct file queries for exploration. Persist DuckDB databases only when the artifact owner, retention, cleanup, and rebuild path are documented.
6. Use Parquet for reusable analytical slices when type preservation, compression, predicate pushdown, and partition pruning matter.
7. Sandbox ad-hoc file queries with allowed paths where practical, and avoid unbounded result output in chat.
8. Compare against Postgres with matched scrape ranges, songs/instruments/accounts, query shapes, cache state, resource caps, and correctness gates.

## FST fit

| Candidate use | Default decision |
|---|---|
| Read-only profiling of exported CSV/JSON/Parquet artifacts | Good bounded pilot |
| Replay/evaluation slice analytics with count/range/fingerprint parity | Good candidate |
| Runtime FST runtime ledger source of truth | Rejected by default |
| Replacement for Postgres ingestion/Epic/API state | Rejected until explicit platform promotion |
| Local session state in repo | Avoid unless gitignored and secret-free |

## Report template

| Artifact/query | Source range | Correctness gate | DuckDB metric | Postgres/baseline metric | Storage impact | Decision |
|---|---|---|---|---|---|---|
| `<slice>` | `<dates/scope>` | `<parity>` | `<latency/size>` | `<latency/size>` | `<bytes/path>` | `<tier>` |
