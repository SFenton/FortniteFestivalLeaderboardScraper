# Database Management Orchestrator Modes

Use this reference to run `database-management` in the correct mode. Each mode may read additional references from this skill and consult focused database advisor skills.

## Shared mode rules

- State the selected mode at the top of the response.
- If the prompt is ambiguous, infer the safest mode and say why.
- Do not implement, migrate, prune, or change runtime defaults in Research, Probe, Monitoring, or Best Practices mode unless the user explicitly approves that transition.
- Use read-only inspection before mutation.
- Protect live FST service/API freshness, ingestion, and Postgres durability before heavy database work.
- Preserve historical correctness timing for scrape/replay data surfaces.
- Require matched evidence for performance claims and count/range/checksum parity for data movement claims.

## Core advisor set

Research, Best Practices, Evaluation, and Promotion modes should consult these focused advisors:

| Advisor | Use for |
|---|---|
| `database-architecture-expert` | Data-layer architecture, schema design, data ownership, platform fit |
| `postgres-database-expert` | PostgreSQL-specific operations, query/index design, locks, maintenance, safety |
| `duckdb-analytics-expert` | DuckDB/Parquet artifact analytics, file profiling, local OLAP pilots |
| `database-platform-research` | Platform fit, licensing, operations, migration feasibility |
| `database-evaluation` | Benchmark design, correctness gates, before/after interpretation |
| `database-implementation` | Schema/repository/migration design, rollback, tests |
| `database-probing-monitoring` | Current-state probes, health, locks, freshness, live incidents |
| `database-improvement` | Query/index/storage/concurrency/retention tuning |

Add reliability review for live FST runtime impact. Add evaluation review when DB work affects scrape evaluations, derived artifacts, or promotion evidence.

## Mode 1: Current-System Probe

Use when the user asks for a status check, inventory, schema/data shape, lock/long-query inspection, freshness audit, table sizes, index state, or a read-only probe.

Workflow:

1. Identify the database, service, schema, table, query, or data surface being probed.
2. Check whether the probe could add material load during a live-sensitive window.
3. Use read-only commands only: service health, `pg_stat_activity`, locks, table/index sizes, row counts, min/max timestamps, freshness watermarks, and selected query plans.
4. Avoid `EXPLAIN ANALYZE` on broad production queries until a safe window is confirmed.
5. Report risk and the next safe action; do not implement unless asked.

Output:

| Surface | Health | Freshness/coverage | Locks/long queries | Size/capacity | Risk | Next action |
|---|---|---|---|---|---|---|
| `<db/table/query>` | `<ok/degraded>` | `<timestamps/counts>` | `<none/issues>` | `<sizes/headroom>` | `<low/medium/high>` | `<probe/monitor/fix>` |

## Mode 2: Monitoring / Incident

Use when the database is slow, blocked, stale, failing health checks, out of disk, causing timeouts, or threatening live FST reliability.

Workflow:

1. Treat availability and data durability as the job; do not change scrape, ranking, or publication semantics from an incident response task.
2. Capture the exact symptom, affected services, current scrape/publication timing, and whether live FST service/API freshness is active.
3. Check Docker services, Postgres readiness, locks, long queries, disk, CPU, memory, restart counts, scrape progress, and publication freshness.
4. Prefer least-disruptive remediation: wait/recheck transient locks, reduce new load, stop nonessential evaluation/backfill work, then apply targeted DB/service fixes only when necessary.
5. Report visible status with current risk, action taken, and next check cadence.

Output:

| Time | Symptom | Services | DB health | Resource pressure | Action | Decision |
|---|---|---|---|---|---|---|
| `<time>` | `<issue>` | `<affected>` | `<locks/queries/freshness>` | `<cpu/mem/disk>` | `<done/planned>` | `<healthy/monitoring/intervened/blocked>` |

## Mode 3: Platform Research

Use when considering other database platforms, managed services, analytical stores, table formats, caches, search/vector systems, or file-backed query engines.

Workflow:

1. Define the workload and why the current platform may be insufficient.
2. Compare candidates against the actual workload: OLTP, time-series ingestion, analytical scans, scrape/replay artifacts, retention/archive, concurrency, query complexity, durability, and operational burden.
3. Research current provider features, limits, pricing posture, licensing, local/Docker support, backup/restore, observability, and migration tools.
4. Include negative evidence and reasons to reject candidates.
5. Classify the result as keep current, artifact-only pilot, bounded A/B, implementation candidate, blocked, or rejected.

Output:

| Candidate | Workload fit | Ops burden | Migration path | Cost/license | Risks | Decision |
|---|---|---|---|---|---|---|
| `<platform>` | `<fit>` | `<low/med/high>` | `<path>` | `<notes>` | `<limits>` | `<tier>` |

## Mode 4: Evaluation / Benchmark

Use when proving a database alternative, query change, index, export format, cache, retention strategy, or data layout is correct and useful.

Workflow:

1. Define baseline, candidate, dataset, query mix, scrape range, songs/instruments/accounts, resource caps, cache state, and success criteria.
2. Verify correctness before speed: row counts, ranges, checksums/fingerprints, representative samples, and scrape/replay parity where applicable.
3. Measure cold and warm behavior when relevant, plus p50/p95/p99 latency, throughput, CPU, memory, disk I/O, lock impact, and storage growth.
4. Run matched comparisons and explain regressions or tradeoffs.
5. Report whether the candidate is accepted, experimental, rejected, blocked, or needs a maintenance window.

Output:

| Benchmark | Baseline | Candidate | Correctness | Performance | Resource impact | Decision |
|---|---|---|---|---|---|---|
| `<name>` | `<metric>` | `<metric>` | `<parity>` | `<delta>` | `<cpu/mem/disk/locks>` | `<tier>` |

## Mode 5: Implementation / Migration

Use when adding or changing schema, migrations, repository methods, SQL scripts, import/export jobs, indexes, data movement, or runtime database configuration.

Workflow:

1. Read the relevant repo instructions and component docs before editing.
2. Reproduce the problem or write the smallest failing test/invariant when feasible.
3. Design for idempotency, short lock/statement timeouts, backward compatibility, and rollback.
4. Keep destructive changes behind explicit flags or operator approval.
5. Update docs in the same patch, including README and component/runbook docs when behavior changes.
6. Validate with the smallest targeted tests/builds and any required DB smoke.

Output:

| Change | Invariant/test | Migration safety | Runtime impact | Docs | Rollback | Decision |
|---|---|---|---|---|---|---|
| `<change>` | `<test>` | `<locks/timeouts/idempotent>` | `<none/low/high>` | `<updated>` | `<path>` | `<accepted/blocked>` |

## Mode 6: Performance / Capacity Improvement

Use when improving slow queries, index design, table bloat, vacuum/analyze behavior, connection pressure, scrape/replay DB load, hot/cold storage, or resource-cap behavior.

Workflow:

1. Identify the bottleneck from measured evidence, not speculation.
2. Capture before metrics and query plans.
3. Choose the smallest candidate fix: query shape, index, batching, cache, retention, compact table, archive, pool sizing, or resource cap.
4. Prove correctness and matched before/after performance.
5. Keep heavyweight index builds, rewrites, and pruning for maintenance windows.

Output:

| Bottleneck | Evidence | Candidate fix | Correctness gate | Before/after | Risk | Decision |
|---|---|---|---|---|---|---|
| `<surface>` | `<plan/metrics>` | `<fix>` | `<parity>` | `<delta>` | `<locks/load>` | `<tier>` |

## Mode 7: Data Integrity / Retention

Use for archives, restores, manifests, checksums, compact projections, pruning, cold storage, retention policies, and data-quality audits.

Workflow:

1. Identify source tables/files, provider/universe/timeframe/range, and retention objective.
2. Build or verify manifests with counts, ranges, byte sizes, checksums/fingerprints, format, compression, and storage path.
3. Validate read/query parity before any prune or cutover.
4. Require explicit operator approval for destructive pruning or table rewrites.
5. Document restore/rehydration and cleanup.

Output:

| Dataset | Coverage | Manifest/checksum | Parity | Prune/cutover risk | Restore path | Decision |
|---|---|---|---|---|---|---|
| `<table/range>` | `<counts/ranges>` | `<ok/missing>` | `<pass/fail>` | `<none/maintenance>` | `<path>` | `<tier>` |

## Mode 8: Best Practices / Roadmap

Use for DB architecture audits, reliability/performance roadmaps, schema/data-quality reviews, platform strategy, and operational runbook improvements.

Workflow:

1. Inspect current docs, schema, scripts, Docker config, repository helpers, scrape/replay pressures, storage roots, and runbooks.
2. Consult the focused database advisors and summarize consensus.
3. Prioritize issues by live-safety risk, data-integrity risk, performance/cost impact, and implementation complexity.
4. Separate guidance-only items from implementation candidates.
5. Ask for explicit approval before applying changes or running heavy probes.

Output:

| Area | Current state | Gap | Proposed change | Proof plan | Risk | Approval needed |
|---|---|---|---|---|---|---|
| `<db area>` | `<observed>` | `<gap>` | `<change>` | `<probe/test/benchmark>` | `<risk>` | `<yes/no>` |

## Mode 9: Promotion Readiness

Use when making a DB change default, cutting over storage/query paths, adopting a new platform, pruning old data, or deploying DB-affecting runtime behavior.

Workflow:

1. Verify correctness parity, benchmark evidence, live-safety impact, backup/restore readiness, rollback path, and docs.
2. Confirm maintenance-window needs for locks, rewrites, index builds, or service restarts.
3. Confirm downstream scrape/replay behavior remains historical correctness and parity-safe.
4. Reject or keep experimental if correctness, rollback, or live-safety gates are incomplete.

Output:

| Candidate | Correctness evidence | Performance evidence | Live safety | Rollback | Docs | Decision |
|---|---|---|---|---|---|---|
| `<change>` | `<parity>` | `<delta>` | `<safe/blocked>` | `<path>` | `<ready/needed>` | `<promote/reject/experimental>` |
