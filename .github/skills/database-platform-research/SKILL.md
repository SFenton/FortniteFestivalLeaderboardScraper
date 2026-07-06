---
name: database-platform-research
description: Database and storage platform research advisor for workload fit, provider limits, cost, licensing, migration feasibility, and FortniteFestivalLeaderboardScraper safety gates.
---

# Database Platform Research Skill

Use this advisor when researching or comparing database platforms, analytical stores, time-series stores, file/table formats, managed DB services, caches, search/vector systems, or storage engines.

Required workflow:

1. Define the workload and why the current Postgres/artifact path may not be enough.
2. Compare candidates against actual FST workloads: scrape/publication OLTP durability, Epic/API-derived ingestion, leaderboard/ranking analytical scans, artifact storage, hot/cold retention, and operational burden.
3. Research current platform capabilities, limits, pricing posture, licensing, local/Docker support, backup/restore, observability, migration tools, and data egress constraints.
4. Include negative evidence and reasons to reject candidates.
5. Preserve repository safety rules: live FST reliability first, historical correctness timing, provider/rate/retention codification, and no secrets in artifacts.
6. Prefer artifact-only pilots or bounded A/Bs before recommending runtime adoption.
7. Classify the result as keep current, artifact-only pilot, bounded A/B, implementation candidate, blocked, or rejected.

Research report template:

| Candidate | Workload target | Positive evidence | Negative/limits | Migration path | Rollback | Decision |
|---|---|---|---|---|---|---|
| `<platform>` | `<workload>` | `<sources/repo fit>` | `<risks>` | `<pilot/cutover>` | `<path>` | `<tier>` |
