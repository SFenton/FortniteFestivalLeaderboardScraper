---
name: database-evaluation
description: Database benchmark and evaluation advisor for correctness, matched baselines, performance, resource use, cost, and promotion-tier scrape/publication decisions.
---

# Database Evaluation Skill

Use this advisor when benchmarking a database platform, query, index, cache, export format, retention layout, compact projection, or data-access path.

Required workflow:

1. Define baseline, candidate, dataset, query mix, scrape range, songs/instruments/accounts, resource caps, cache state, concurrency, and success criteria before running.
2. Protect live FST service/API freshness before heavy benchmarks; avoid broad scans during live-sensitive windows.
3. Prove correctness before speed: row counts, min/max timestamps, checksums/fingerprints, representative samples, query parity, and scrape/replay parity when behavior could change.
4. Compare matched before/after results on identical data, hardware/resource caps, cache state, concurrency, and code/config version.
5. Measure latency, throughput, CPU, memory, disk I/O, lock impact, storage growth, and error/retry behavior.
6. Explain tradeoffs and classify as accepted, experimental, research win, rejected, blocked, or maintenance-window-required.
7. Persist benchmark commands, artifacts, query plans, counts, checksums, resource caps, and decision notes.

Benchmark report template:

| Benchmark | Baseline | Candidate | Correctness | Performance | Resource impact | Risk | Decision |
|---|---|---|---|---|---|---|---|
| `<name>` | `<metric>` | `<metric>` | `<parity>` | `<delta>` | `<cpu/mem/disk/locks>` | `<risk>` | `<tier>` |
