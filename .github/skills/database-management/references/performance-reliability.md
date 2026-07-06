# Performance and Reliability Reference

Use this reference for query tuning, index design, storage pressure, vacuum/analyze behavior, DB resource caps, concurrency, connection pressure, scrape/replay load, and current-system improvement loops.

## Diagnosis before tuning

Start with measured evidence:

- Slow query text, parameters, row counts, date ranges, and caller.
- `EXPLAIN` plan and, when safe, `EXPLAIN (ANALYZE, BUFFERS)`.
- Table/index sizes, dead tuples, last vacuum/analyze, and bloat symptoms.
- Locks, long queries, wait events, and competing workers/scrape evaluations/backfills.
- CPU, memory, disk I/O, disk headroom, and Docker resource caps.
- Cache behavior and repeated query shape.

Do not add indexes, caches, or config changes until the bottleneck is identified.

## Candidate fixes

| Bottleneck | Prefer first | Escalate only after proof |
|---|---|---|
| Missing selective index | Existing index reuse, query predicate/order fix | New concurrent index or covering index |
| Broad historical scans | Scrape/song/instrument/account bounds, artifact slices, spools | Columnar/export companion path |
| Repeated latest reads | Lateral latest-N, bounded context, short TTL cache | Shared preload/candidate cache |
| JSONB payload pressure | Typed compact projection, cold archive manifest | Prune/rewrite with explicit approval |
| Long locks | Smaller batches, timeout, after-hours window | Maintenance-window DDL |
| Disk pressure | Archive cold payloads, clean artifacts, validate retention | Destructive prune/VACUUM FULL |
| CPU/memory pressure | Query shape, batching, concurrency cap | Resource cap change or platform split |

## Acceptance gates

- Correctness parity: rows, ranges, checksums/fingerprints, samples, and behavior parity where applicable.
- Matched performance: same dataset, query shape, cache state, concurrency, hardware/resource caps, and process version.
- Reliability: no new long locks, deadlocks, unbounded memory, retry storms, stale watermarks, or live FST freshness regressions.
- Maintainability: docs updated, rollback known, and experimental flags clearly labeled.

## Reporting metrics

Use metrics that explain the tradeoff:

- p50/p95/p99 latency and throughput.
- Wall-clock runtime, CPU, RSS/heap, disk read/write, and network where relevant.
- Rows scanned/returned, buffers hit/read/dirtied, temp files, and sort/hash spills.
- Table/index storage growth.
- Lock wait time, query age, and error/retry counts.
- Live API freshness, scrape progress, publication lag, and ingestion lag when runtime is affected.

## Improvement report

| Bottleneck | Evidence | Candidate | Correctness | Before | After | Risk | Decision |
|---|---|---|---|---|---|---|---|
| `<surface>` | `<plan/metric>` | `<fix>` | `<parity>` | `<metric>` | `<metric>` | `<risk>` | `<tier>` |
