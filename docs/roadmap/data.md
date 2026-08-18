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
| Finish snapshot/current-state ownership migration | Snapshot-overlay worker reads and unchanged-snapshot skipping remain disabled | Complete scrape A/B parity, replay evidence, rollback, and storage/resource comparison |
| Revalidate snapshot reuse | The living runbook contains evaluation history but no blanket promotion | Fresh matched baseline and candidate on current schema/code |
| Evaluate bounded artifact analytics | DuckDB/Parquet is routed as an artifact-only option, not a production source of truth | Bounded export/replay benchmark that preserves PostgreSQL publication correctness and stays on the FST drive |
| Verify freeze-safe publication cache at a natural publication switch | Service-only promotion is complete. PR #55 merged as `2bc7e9f9`; official service/held-worker image digest `4fad543b...976564` is deployed, service healthy, worker Created/offline, web unchanged. Exact Unicode/HTML/control/emoji bytes and ETags pass; classification, selected-context isolation, freeze simulation, atomic staging, restart L2, and public health pass. Across 120 interleaved samples per route, warm p95 is `1.90-3.47 ms` and all 11 routes improve `55.76-82.97%` with no sustained regression. Live staging measured 15,576 current rows, 13.48 MB physical DB growth, 296.66 MB peak free-space excursion, 469.40 MB WAL, zero temp bytes, and 210.03 s core precompute. | Keep worker offline until normal capacity policy permits a natural scrape. Then execute the recorded invalidation card: prove no pre-publication leakage, atomic new-current/retained-previous cache promotion, L1 reset, first-hit L2 recovery, route parity, and public health. |
| Execute the exact pro-bass rewrite when all gates pass | PR #57 protects current/previous/working physical source IDs, excludes stale unnamed maps, and hardcodes only `leaderboard_entries_snapshot_pro_bass`. The live archive/restore proves 308,536,699 rows, exact counts/content hashes for 125 IDs, and the full canonical catalog, then cleans 130,771,858,177 bytes of restore PGDATA. Production planning now uses loose-index IDs, metadata ownership, protected-only fingerprints and the verified archive input. Relocating 17 unreferenced evidence directories to temporary 8 TB staging raised free space to 68,545,114,112 bytes: scratch-build margin is 4,655,423,492 and rollback-safe pre-drop repatriation margin is 1,970,080,474. The final drill also proves atomic evidence, torn-evidence recovery, repeated emergency cancellation/escalation, pre-drop `pg_default` repatriation, and final scratch cleanup. No live replacement/rewrite/swap/drop ran. | Add and validate the exact production-owned scratch bind mount before `check`; run fresh idle/unfrozen preflight and live parity; require source/API/reference/catalog parity while original and scratch rollback relations remain; complete `repatriate`; only then run final drop to remove both rollback relations, normalize names, and remove the tablespace. Retain the verified archive and temporary evidence staging until separate retention decisions. |

Detailed post-scrape phase, progress, replay, deployment, A/B, and optimization
work is owned by the
[post-scrape processing roadmap](post-scrape-processing.md).

Completed physical cleanup, compaction, retirement, and rejected stored-rank
rollout documents were removed from the current tree and must not be
reintroduced as pending work.
