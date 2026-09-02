---
status: roadmap
owner: data
last_verified: 2026-08-30
last_verified_commit: 21d7193c
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
  - docs/database/SnapshotGenerationPartitionMigration.md
  - docs/database/SnapshotGenerationRetentionSafety.md
  - docs/database/SnapshotGenerationDropRunbook.md
  - FSTService/Persistence/Maintenance/SnapshotGenerationDropSchema.cs
  - docs/roadmap/post-scrape-processing.md
update_triggers:
  - Publication, snapshot ownership, retention, or analytics readiness changes.
---

# Data and publication readiness

These are verified gaps, not automatic implementation approvals.

| Item | Current evidence | Acceptance gate |
|---|---|---|
| Complete generation-addressable publication bindings | `EnablePublicationReadContext` remains false for both service and worker roles | Every publication-bound surface reports ready; stale/current generation behavior passes contract tests and live-safe validation |
| Finish snapshot/current-state ownership migration | Snapshot reuse is accepted and enabled. Scrape 1304 proved mixed legacy/generation writer routing and publication, but snapshot-overlay readers remain disabled. | Complete reader migration with replay/live parity, rollback, and storage/resource comparison |
| Bound physical snapshot generations | All nine instrument roots are generation-partitioned. Archive-only, quarantine/reattach, exact non-cascading DROP, logical restore, and H6 continuation/finalization are live-accepted on Pro Cymbals `1314`. Authorization `0ed3cd7125af6fdf8748915318b0893d` released hold 3 and removed the mutation trigger while preserving restored OID/relfilenode `321906645`, 8,627 rows, and both index chains. Candidate scrape `1337`, publication `171`, notifications, and cycle `17` passed with zero failures and exact planner/oracle agreement; scrapes `1338` through `1341` also completed cleanly. Solo Bass `1308` remains excluded by unreplayed writer-failure evidence. | Promote the accepted branch through PR/CI, deploy official images at a terminal scrape boundary, and run an official-image confirmation scrape. Permanent single-child deletion, automatic retirement, multi-child execution, and sparse compaction remain disabled and separately gated |
| Evaluate bounded artifact analytics | DuckDB/Parquet is routed as an artifact-only option, not a production source of truth | Bounded export/replay benchmark that preserves PostgreSQL publication correctness and stays on the FST drive |
| Verify freeze-safe publication cache at a natural publication switch | Service-only promotion is complete. Scrape `1310` advanced publication `103`, preserved persisted reads through one deferred retry, and recorded zero HTTP failures across 309 monitor samples. The scrape evidence still did not attribute first-hit L1/L2 recovery or prove every invalidation-card observation. | At the next bounded cache-specific test, capture pre-publication leakage checks, atomic current/previous cache binding, L1 reset, first-hit L2 attribution, exact route parity, and public health without coupling that evidence to another data candidate |

Detailed post-scrape phase, progress, replay, deployment, A/B, and optimization
work is owned by the
[post-scrape processing roadmap](post-scrape-processing.md).

Completed physical cleanup, compaction, retirement, and rejected stored-rank
rollout documents were removed from the current tree and must not be
reintroduced as pending work.
