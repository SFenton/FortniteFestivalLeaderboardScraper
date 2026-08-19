---
status: roadmap
owner: data
last_verified: 2026-08-19
last_verified_commit: a682a16c
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
| Bound physical snapshot generations | Pro bass and pro guitar are generation-partitioned. Scrape 1304 wrote exact dedicated children (`1,395,539` and `3,674,245` rows), left both defaults empty, published all `6,336` solo sources, completed notifications/drain, and exited the run-once worker normally. Seven regular instrument partitions and recurring child retention remain. | Migrate exactly one remaining instrument between complete run-once scrape windows, beginning with solo guitar. Then implement and accept recurring archive-before-child-drop retention/default-child auditing before unattended scheduling |
| Evaluate bounded artifact analytics | DuckDB/Parquet is routed as an artifact-only option, not a production source of truth | Bounded export/replay benchmark that preserves PostgreSQL publication correctness and stays on the FST drive |
| Verify freeze-safe publication cache at a natural publication switch | Service-only promotion is complete. Scrape 1304 supplied the natural switch: publication `92` advanced and unfroze, songs/rankings routes returned HTTP `200`, and the monitor recorded zero service-info failures. The scrape evidence did not attribute first-hit L1/L2 recovery or prove every invalidation-card observation. | At the next publication window or bounded service test, capture pre-publication leakage checks, atomic current/previous cache binding, L1 reset, first-hit L2 attribution, exact route parity, and public health without coupling that evidence to another data candidate. |

Detailed post-scrape phase, progress, replay, deployment, A/B, and optimization
work is owned by the
[post-scrape processing roadmap](post-scrape-processing.md).

Completed physical cleanup, compaction, retirement, and rejected stored-rank
rollout documents were removed from the current tree and must not be
reintroduced as pending work.
