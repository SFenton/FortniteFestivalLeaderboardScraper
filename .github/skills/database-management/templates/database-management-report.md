# Database Management Report

**Mode:** `<probe/monitoring/research/evaluation/implementation/improvement/retention/best-practices/promotion>`

**Advisor consultation:** `<database-platform-research/database-evaluation/database-implementation/database-probing-monitoring/database-improvement/reliability-if-needed>`

| Surface | Classification | Live-safety risk | Data/timing risk | Evidence | Change or proof plan | Rollback | Decision |
|---|---|---|---|---|---|---|---|
| `<db/tooling>` | `<platform/schema/query/index/retention/monitoring>` | `<none/monitor/defer>` | `<none/needs gate/blocker>` | `<probes/benchmarks/parity>` | `<plan>` | `<path>` | `<tier>` |

## Required notes

- **Current-state evidence:** `<health/locks/size/freshness/query plans>`
- **Data-timing path:** `<Epic/API timestamps, scrape IDs, computed_at, publication state, freeze/unfreeze state>`
- **Correctness gate:** `<row counts, ranges, checksums, query parity, scrape/replay parity>`
- **Performance gate:** `<matched before/after, cache state, resource caps, concurrency>`
- **Maintenance safety:** `<locks, timeouts, downtime, approval, backups/restores>`
- **Documentation:** `<README/database/design/runbook docs updated or not needed>`
