---
status: roadmap
owner: data
last_verified: 2026-08-17
last_verified_commit: dffca41c
sources:
  - FSTService/FeatureOptions.cs
  - FSTService/appsettings.json
  - FSTService/Api/SongEndpoints.cs
  - FSTService/Scraping/ScrapeTimePrecomputer.cs
  - deploy/config/fstservice-role.env
  - deploy/config/fstworker-role.env
  - docs/database/SnapshotReuseRunbook.md
  - docs/database/StaleSoloRankIndexRetirementRunbook.md
  - docs/database/ProBassSnapshotRewritePilot.md
  - docs/roadmap/post-scrape-processing.md
update_triggers:
  - Publication, snapshot ownership, retention, or analytics readiness changes.
---

# Data and publication readiness

These are verified gaps, not automatic implementation approvals.

| Item | Current evidence | Acceptance gate |
|---|---|---|
| Complete generation-addressable publication bindings | `EnablePublicationReadContext` remains false for both service and worker roles | Every publication-bound surface reports ready; stale/current generation behavior passes contract tests and live-safe validation |
| Finish snapshot/current-state ownership migration | Snapshot reuse is accepted and enabled for the worker role after scrape 1303; snapshot-overlay readers remain disabled | Complete reader migration with replay/live parity, rollback, and storage/resource comparison |
| Bound physical snapshot generations | Scrape 1303 reused 1,717 scopes / 6,112,541 rows globally, but regular instrument partitions still retain whole historical snapshot IDs. The candidate initializer/write path creates fixed per-`snapshot_id` children and remains compatible before migration. Isolated tests prove exact one-child removal preserves other generations/index routing. | Complete archive/restore drills and live migration, remove obsolete `1301`, migrate every instrument, and implement recurring archive-before-child-drop retention/default-child auditing before another scrape |
| Evaluate bounded artifact analytics | DuckDB/Parquet is routed as an artifact-only option, not a production source of truth | Bounded export/replay benchmark that preserves PostgreSQL publication correctness and stays on the FST drive |
| Verify freeze-safe publication cache at a natural publication switch | Service-only promotion is complete. PR #55 merged as `2bc7e9f9`; official service/held-worker image digest `4fad543b...976564` is deployed, service healthy, worker Created/offline, web unchanged. Exact Unicode/HTML/control/emoji bytes and ETags pass; classification, selected-context isolation, freeze simulation, atomic staging, restart L2, and public health pass. Across 120 interleaved samples per route, warm p95 is `1.90-3.47 ms` and all 11 routes improve `55.76-82.97%` with no sustained regression. Live staging measured 15,576 current rows, 13.48 MB physical DB growth, 296.66 MB peak free-space excursion, 469.40 MB WAL, zero temp bytes, and 210.03 s core precompute. | Keep worker offline until normal capacity policy permits a natural scrape. Then execute the recorded invalidation card: prove no pre-publication leakage, atomic new-current/retained-previous cache promotion, L1 reset, first-hit L2 recovery, route parity, and public health. |
| Validate and extend the accepted pro-bass transition | The live rewrite returned `152,985,165,824` bytes. Validation scrape `1303` published with zero best-effort failures, reused 350/702 pro-bass scopes and 1,436,731 rows, and grew pro bass by `1,000,898,560` bytes. Publication/API parity passed and the worker is held. | Remove obsolete `1301` through generation migration, preserve `1302-1303`, migrate all remaining instruments, and accept recurring whole-generation archive/drop retention before another scrape. |

Detailed post-scrape phase, progress, replay, deployment, A/B, and optimization
work is owned by the
[post-scrape processing roadmap](post-scrape-processing.md).

Completed physical cleanup, compaction, retirement, and rejected stored-rank
rollout documents were removed from the current tree and must not be
reintroduced as pending work.
