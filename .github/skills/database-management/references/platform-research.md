# Platform Research Reference

Use this reference when researching database platforms, analytical stores, table formats, caches, search/vector systems, managed services, or storage engines.

## Research criteria

Compare candidates against the actual workload instead of generic benchmarks:

- OLTP durability: scrape state, leaderboard snapshots, rankings, publication state, API response caches, and audit trails.
- Ingestion: Epic/API-derived leaderboard pages, song metadata, player registrations, watermarks, provider provenance, and freshness.
- Analytical scans: scrape/replay windows, ranking rebuilds, band-history projections, leaderboard deltas, and report aggregation.
- Artifact storage: hot NVME spools, cold HDD archives, manifests, checksums, and rehydration paths.
- Concurrency: live FST API reads, scraper workers, scrape evaluations, replays, backfills, and post-process jobs.
- Query model: SQL compatibility, JSON/typed columns, indexes, window/lateral queries, time filters, and schema evolution.
- Operations: local Docker support, backup/restore, observability, upgrades, resource caps, maintenance windows, and failure modes.
- Governance: licensing, managed-service terms, data egress, provider retention limits, secrets handling, and cost posture.

## Candidate classification

Use these tiers:

| Tier | Meaning |
|---|---|
| Keep current | Existing Postgres/file artifact path is sufficient; no change now |
| Artifact-only pilot | Candidate can be tested with exported artifacts without touching runtime data paths |
| Bounded A/B | Candidate deserves a matched benchmark against a known workload slice |
| Implementation candidate | Evidence supports scoped implementation after rollback/docs plan |
| Blocked | Missing entitlement, migration path, safety gate, or operator decision |
| Rejected | Candidate does not fit workload or adds more risk than benefit |

## Required evidence

- Current source links for vendor/provider capabilities, limits, pricing posture, license, local/Docker support, and operational constraints.
- Repo evidence for the workload that motivates the research.
- Negative or contradictory evidence, especially around migration complexity, write-path durability, and operational burden.
- Proof path: artifact pilot, export/import parity, benchmark, or phased cutover.
- Rollback path if any runtime adoption is proposed.

## Common fit patterns

These are starting hypotheses, not conclusions:

- **Postgres** remains the durable OLTP source of truth for scrape state, leaderboard/ranking data, publication state, auditability, and live FST behavior.
- **DuckDB/Parquet** can be a strong artifact/scrape/replay companion for read-heavy analytical slices when count/range/fingerprint parity is proven.
- **Columnar analytical stores** can help broad scans, but they add ingestion, consistency, and operations costs that need matched evidence.
- **Time-series extensions or stores** can help retention and time-window queries, but they must not break existing timestamp/historical correctness gates.
- **Caches** can reduce repeated reads, but they must be keyed by all behavior-relevant inputs and validated against exact-output parity.

## OSS-fed candidate set

Use the OSS feeder stack as a starting point, then verify with current vendor docs and repo evidence:

| Candidate | Feeder source | FST starting posture |
|---|---|---|
| PostgreSQL | wshobson PostgreSQL/database-admin/database-optimizer; TerminalSkills PostgreSQL/SQL optimizer | Current source of truth; improve in place first |
| DuckDB + Parquet | duckdb/duckdb-skills; TerminalSkills DuckDB | Artifact-only analytical/scrape/replay pilot |
| ClickHouse | TerminalSkills ClickHouse; database-architect platform sources | Research-only OLAP candidate until DuckDB/artifact paths are insufficient |
| TimescaleDB | PostgreSQL/table-design feeder references plus current Timescale docs | Research-only extension candidate for time-series retention/query pressure |
| Managed warehouses/lakes | database-architect/admin feeder concepts plus vendor docs | Blocked unless data egress, provider terms, secrets, cost, and live-safety posture are documented |

## Platform research report

| Candidate | Workload target | Evidence | Negative evidence | Migration path | Rollback | Decision |
|---|---|---|---|---|---|---|
| `<platform>` | `<workload>` | `<sources/repo data>` | `<limits>` | `<pilot/cutover>` | `<path>` | `<tier>` |
