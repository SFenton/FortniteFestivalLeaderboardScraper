---
name: database-management
description: Mode-driven database management orchestrator for platform research, evaluation, implementation, probing, monitoring, performance, reliability, retention, and improvement work in FortniteFestivalLeaderboardScraper.
license: Repository-local synthesis; see references/attribution.md
---

# Database Management Skill

Use this skill when researching database platforms, evaluating storage/query alternatives, designing or changing schema, implementing migrations or persistence, probing and monitoring existing database systems, improving current database performance/reliability, planning retention/archive work, or responding to data-integrity incidents.

This is an umbrella skill. Load only the relevant advisor skill(s) and reference file(s) for the task instead of putting every database checklist into context.

Like `ml-implementation-review`, this skill is seeded by permissively licensed OSS skills/agents and then adapted to this repository. Read `references/oss-feeders.md` and `references/attribution.md` when adding, changing, or justifying feeder-derived database guidance.

## Repository rules override general database guidance

1. Protect live FST service/API freshness before heavy database work. Check Docker service health, Postgres long queries/locks, CPU, memory, disk, and scrape/publication timing before broad probes, backfills, exports, index builds, VACUUM rewrites, or scrape/replay scans.
2. Use read-only probes first. Do not mutate schema, data, runtime config, indexes, retention state, or Docker services until the current state and risk are understood.
3. Treat destructive or locking work as parity-gated maintenance. Data deletion, table rewrites, `VACUUM FULL`, non-concurrent large index builds, retention pruning, export/import cutovers, and platform migrations are auto-approved after live-scrape A/B testing proves the new path has the same data as the old path, with rollback/restore, resource risk, live-safety window, and post-action validation recorded.
4. Preserve historical leaderboard correctness semantics. Scrape, publication, ranking, and notification behavior must only use data valid for the published or in-progress scrape being evaluated.
5. Keep all FST database/storage/reclaim work on the 4 TB FST drive. Do not use alternate drives for data, scratch, migration, export, or repack workspace unless SFenton explicitly overrides this rule later.
6. Codify Epic/API feeds, entitlements, rates, quotas, request pacing, storage, retention, and cleanup before adding or widening data-source automation or platform-managed ingestion.
7. Prefer the smallest reproducible benchmark or probe that answers the question. Compare against matched baselines with identical data ranges, query shapes, concurrency, cache state, resource caps, and correctness checks.
8. Keep secrets out of logs, docs, artifacts, shell history, and committed files. Redact connection strings and provider credentials.
9. Update README and database/design/runbook docs whenever schema, persistence behavior, storage layout, retention, DB commands, platform posture, or operational safety changes.

## Mode selection

Infer the mode from the user's prompt. If the user names a mode, use it directly. If multiple modes apply, run the earliest safety gate first: Current-System Probe -> Research -> Evaluation -> Implementation/Improvement -> Promotion Readiness.

| Mode | Trigger phrases | Primary outcome |
|---|---|---|
| Current-System Probe | "inspect DB", "what is running", "status", "locks", "size", "coverage", "probe" | Read-only inventory, health, data-shape, and risk report |
| Monitoring / Incident | "monitor", "stale", "slow", "blocked", "failed", "incident", "outage" | Live-safe diagnosis, least-disruptive remediation path, and visible status report |
| Platform Research | "research databases", "should we use", "Postgres vs", "DuckDB", "ClickHouse", "Timescale", "alternative" | Evidence-backed platform fit/reject recommendation |
| Evaluation / Benchmark | "evaluate", "benchmark", "compare", "A/B", "prove faster", "prove cheaper" | Matched correctness/performance/cost benchmark with decision tier |
| Implementation / Migration | "implement", "add table", "schema", "migration", "repository", "index", "import/export" | Tested, documented, rollback-aware database change |
| Performance / Capacity Improvement | "make faster", "reduce DB load", "optimize", "bloat", "I/O", "memory", "disk" | Measured bottleneck, safe improvement, and matched before/after evidence |
| Data Integrity / Retention | "checksum", "manifest", "archive", "prune", "restore", "coverage", "data quality" | Integrity gate, retention decision, and restore/rehydration path |
| Best Practices / Roadmap | "audit", "best practices", "roadmap", "what should we improve" | Prioritized DB reliability/performance/data-quality plan |
| Promotion Readiness | "turn on", "make default", "cut over", "production", "deploy" | Promote/reject/experimental decision with rollback and docs checklist |

Read `references/orchestrator-modes.md` for the full workflow and report shape for each mode.

## Advisor skill routing

Consult the focused database advisor skills before recommendations or implementation. If the runtime supports skill invocation, invoke them; otherwise read the corresponding `.github/skills/<name>/SKILL.md` files and summarize their relevant advice.

| Advisor | Use for |
|---|---|
| `database-architecture-expert` | Data-layer architecture, schema design, platform fit, normalization/denormalization, and migration strategy |
| `postgres-database-expert` | Postgres-specific schema, queries, indexes, locks, vacuum/analyze, backup/restore, and operations |
| `duckdb-analytics-expert` | DuckDB/Parquet direct-file analytics, scrape/replay artifact pilots, and local OLAP benchmarking |
| `database-platform-research` | External DB/storage platform research, licensing, cost, managed-service posture, migration feasibility |
| `database-evaluation` | Correctness/performance/cost benchmark design and matched baseline interpretation |
| `database-implementation` | Schema, migration, repository, data import/export, index, and rollback-safe implementation |
| `database-probing-monitoring` | Read-only DB health probes, lock/long-query inspection, freshness, incidents, and operator monitoring |
| `database-improvement` | Query/index/storage/retention/concurrency tuning and current-system improvement loops |

Add reliability review whenever database work could affect the live FST API, Docker runtime, scraper, publication gate, rankings, notifications, or Home Assistant-facing status. Add evaluation review when database work changes scrape/replay evidence, derived artifacts, or promotion evidence.

## Reference routing

| Task shape | Read next |
|---|---|
| Mode selection, advisor consultation, mode-specific outputs | `references/orchestrator-modes.md` |
| Safe health probes, read-only inventory, locks, freshness, sizes, and monitoring | `references/operational-probing.md` |
| OSS database skill/agent feeders, licenses, and local adaptation boundaries | `references/oss-feeders.md` |
| Researching Postgres alternatives, analytical stores, time-series stores, managed platforms, or file/table formats | `references/platform-research.md` |
| Schema, migration, repository, import/export, index, and rollback implementation | `references/implementation-migrations.md` |
| Query tuning, index design, storage pressure, vacuum/analyze, resource caps, and capacity planning | `references/performance-reliability.md` |
| Backups, restores, retention, secrets, destructive operations, and data-safety gates | `references/security-data-safety.md` |
| Source/license questions or updating this skill | `references/attribution.md` |

## Required workflow

1. **Classify the database surface.** State whether the task touches platform choice, schema, persistence code, query/index design, retention/archive, ingestion, scrape/replay data, operational monitoring, or incident response.
2. **Run the live-safety gate.** Decide whether live FST service/API freshness, scraper progress, publication, rankings, notifications, or ingestion could be affected. Pause or defer heavy work when live safety is at risk.
3. **Probe before changing.** Gather current health, table/index size, query, lock, coverage, and freshness evidence with read-only commands first.
4. **Protect timing semantics.** Identify Epic/API timestamps, row timestamps, scrape IDs, `computed_at`, publication state, and any latency assumptions before changing scrape/replay data paths.
5. **Choose the proof ladder.** Use smoke probes for plumbing, matched benchmarks for performance claims, count/range/checksum parity for data movement, and scrape/replay parity when scrape/publication behavior could change.
6. **Make the smallest safe change.** Prefer existing schema/init patterns, repository helpers, lock/statement timeouts, concurrent index paths, and artifact-only pilots before runtime defaults.
7. **Plan rollback and cleanup.** Record how to revert schema/config, drop experimental indexes, restore archived data, rehydrate cold payloads, and validate after rollback.
8. **Persist evidence.** Save commands, date ranges, row counts, checksums, query plans, runtime metrics, resource caps, and artifact paths when they support a decision.
9. **Report the decision tier.** Use promoted, accepted, experimental, research win, rejected, blocked, or maintenance-window-required.

## Output

For any substantive database task, include this table or the fuller `templates/database-management-report.md` version:

| Surface | Mode | Live-safety risk | Data/timing risk | Evidence | Change plan | Rollback | Decision |
|---|---|---|---|---|---|---|---|
| `<db/tooling>` | `<mode>` | `<none/monitor/defer>` | `<none/needs gate/blocker>` | `<probes/benchmarks/parity>` | `<read-only/implementation/maintenance>` | `<path>` | `<tier>` |
