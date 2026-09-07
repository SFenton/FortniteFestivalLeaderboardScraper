---
status: roadmap
owner: data
last_verified: 2026-09-07
last_verified_commit: 0b07fff0
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
  - docs/database/SnapshotGenerationRetirementControlPlane.md
  - docs/database/SnapshotGenerationOfflineRetentionReport.md
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
| Bound physical snapshot generations | All nine instrument roots are generation-partitioned. Archive-only, quarantine/reattach, exact non-cascading DROP, logical restore, and H6 continuation/finalization are live-accepted. Candidate scrape `1345` and official scrape `1346` closed the DROP tier; cycle `33` then proved a 3.28 GiB Solo Guitar child through exact archive/network-none restore without source mutation. A separate default-off host control plane persists bounded immutable policy and largest-first plan evidence only. The offline reporter's first deployment was rejected because general schema initialization rewrote a current path binding. Its compatible additive retention schema remains deployed; the source-preserving repair, dedicated initializer and sticky read-only startup availability gate require parent review. Solo Bass `1308` remains protected by unreplayed writer-failure evidence. | Review the repair, then use a natural idle stop and dedicated retention-only initialization with non-retention parity before recreating the service and guarded compatible worker. Require non-degraded mutation readiness as well as public HTTP health. Do not roll back the additive schema or claim the prior binding mutation was repaired. Prove the genuine receipt and bounded offline report's current-cycle/oracle agreement, source parity and public health; then collect plan/reconcile evidence. Archive execution remains blocked on exact container binding, full-duration admission and cooperative process/Docker cleanup. Automatic deletion, multi-child execution and sparse compaction remain disabled and separately gated |
| Evaluate bounded artifact analytics | DuckDB/Parquet is routed as an artifact-only option, not a production source of truth | Bounded export/replay benchmark that preserves PostgreSQL publication correctness and stays on the FST drive |
| Measure the offline retention observation budget at production scale | Isolated correctness proves genuine persistence, canonical identity, configuration admission and cleanup. Worker cycles exercised the shared planner at full scale in about 1.4-1.6 seconds; the host path remains unmeasured. Scale adjudication rejects a capacity-risking full duplicate or partial-artifact substitute. | After reviewed deployment and an externally guarded terminal idle stop with an authentic compatible-worker receipt, run one bounded host-only offline-report canary. Capture phase timings, resources, locks, public reads, source/control parity and ownership release under unchanged 15/120/150-second bounds. Reuse an accepted canonical current cycle, or create one if absent; then separately validate plan-only policy and `plan-cycle`. No deployment, live run or budget increase is implied by local commit/build |
| Verify freeze-safe publication cache at a natural publication switch | Service-only promotion is complete. Scrape `1310` advanced publication `103`, preserved persisted reads through one deferred retry, and recorded zero HTTP failures across 309 monitor samples. The scrape evidence still did not attribute first-hit L1/L2 recovery or prove every invalidation-card observation. | At the next bounded cache-specific test, capture pre-publication leakage checks, atomic current/previous cache binding, L1 reset, first-hit L2 attribution, exact route parity, and public health without coupling that evidence to another data candidate |

Detailed post-scrape phase, progress, replay, deployment, A/B, and optimization
work is owned by the
[post-scrape processing roadmap](post-scrape-processing.md).

Completed physical cleanup, compaction, retirement, and rejected stored-rank
rollout documents were removed from the current tree and must not be
reintroduced as pending work.
