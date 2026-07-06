---
name: database-improvement
description: Current-system database improvement advisor for query tuning, indexes, storage pressure, retention, concurrency, reliability, and capacity.
---

# Database Improvement Skill

Use this advisor when improving current database performance, reliability, storage layout, query/index behavior, retention/archive paths, concurrency, or resource-cap behavior.

Required workflow:

1. Diagnose the bottleneck from measured evidence before proposing changes.
2. Capture before metrics: query plans, row counts, table/index sizes, latency, throughput, CPU, memory, disk I/O, locks, and freshness lag.
3. Prefer the smallest safe fix: query rewrite, existing index reuse, bounded scans, batching, cache key repair, compact projection, archive, or concurrency cap.
4. Keep heavy index builds, table rewrites, pruning, and `VACUUM FULL` for explicit maintenance windows.
5. Prove correctness with counts, ranges, checksums, representative samples, and scrape/replay parity when behavior could change.
6. Compare matched before/after performance under identical data, cache state, resource caps, and concurrency.
7. Document rollback, cleanup, and docs updates before promoting the improvement.
8. Continue improvement loops only while there is a measured bottleneck and a safe next A/B; stop when live-safety gates pause work or no useful improvement remains.

Improvement report template:

| Bottleneck | Evidence | Candidate fix | Correctness gate | Before/after | Risk | Rollback | Decision |
|---|---|---|---|---|---|---|---|
| `<surface>` | `<plan/metrics>` | `<fix>` | `<parity>` | `<delta>` | `<locks/load>` | `<path>` | `<tier>` |
