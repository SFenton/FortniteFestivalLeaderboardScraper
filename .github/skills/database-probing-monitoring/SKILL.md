---
name: database-probing-monitoring
description: Read-only database probing, monitoring, health-check, freshness, lock, long-query, and incident-triage advisor.
---

# Database Probing and Monitoring Skill

Use this advisor when inspecting existing database systems, monitoring Postgres health, checking locks/long queries, assessing table/index sizes, auditing freshness/coverage, or triaging database incidents.

Required workflow:

1. Scope the probe to a database, service, table, query, timestamp range, or incident symptom.
2. Protect live FST service/API freshness: check scrape/publication timing, Docker health, Postgres readiness, CPU, memory, disk, and active scrape/replay/backfill load before broad probes.
3. Use read-only commands first. Do not mutate schema/data/config, restart services, terminate backends, or add indexes from a monitoring task unless explicitly approved.
4. Inspect `pg_stat_activity`, `pg_locks`, table/index sizes, freshness watermarks, row counts, and bounded query plans.
5. Recheck transient locks or long queries before escalating.
6. Report findings visibly with current risk, least-disruptive next action, and whether a maintenance window is required.
7. Add reliability review when DB health could affect API freshness, FST scrape publication, rankings, notifications, or scrape/publication operations.

Monitoring report template:

| Time | Surface | Health | Freshness/coverage | Locks/long queries | Resources | Action | Decision |
|---|---|---|---|---|---|---|---|
| `<time>` | `<db/table/service>` | `<ok/degraded>` | `<timestamps/counts>` | `<none/issues>` | `<cpu/mem/disk>` | `<probe/recheck/fix>` | `<healthy/monitoring/blocked>` |
