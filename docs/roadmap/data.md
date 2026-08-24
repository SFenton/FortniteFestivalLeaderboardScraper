---
status: roadmap
owner: data
last_verified: 2026-08-23
last_verified_commit: 4c36926a
sources:
  - FSTService/FeatureOptions.cs
  - FSTService/appsettings.json
  - FSTService/Api/SongEndpoints.cs
  - FSTService/Scraping/ScrapeTimePrecomputer.cs
  - deploy/config/fstservice-role.env
  - deploy/config/fstworker-role.env
  - tools/postgres-snapshot-generation-retention-drill.py
  - docs/database/SnapshotReuseRunbook.md
  - docs/database/StaleSoloRankIndexRetirementRunbook.md
  - docs/database/ProBassSnapshotRewritePilot.md
  - docs/database/SnapshotGenerationPartitionMigration.md
  - docs/database/SnapshotGenerationRetentionSafety.md
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
| Bound physical snapshot generations | All nine instrument roots are generation-partitioned. Scrape `1310` completed `8,484/8,484` manifests and `605,239/605,239` page statuses, matched all nine physical children to publication `103` source sums, left every default empty, completed notifications/drain, and exited `0`. Six failed-scrape `1308` children totaling `12,908,355,584` bytes are wholly unreferenced; the `1310` leaves added `15,870,648,320` bytes; older successful generations remain sparsely pinned. Accepted isolated run `snapshot-generation-retention-phase1-final-20260824T004250Z` proves single-leaf archive/restore, no-socket mailbox/prover recovery, both guarded drop mechanics, exact initial/final local daemon/socket/image identity, endpoint-pinned Engine operations, ID-only `--pull=never` creation, repeated aggregate cleanup, zero Docker-volume delta, and fail-closed terminal sealing. Four older sealed runs are forensic/rejected: two leaked eight anonymous volumes total, and two zero-volume repairs retained Docker/image TOCTOU, premature success publication, and first-error cleanup semantics. The eight volumes were removed and remain absent. | Implement durable whole-child intent/executor/prover ownership and default auditing, then pass disabled/report-only parity, archive-only and manual canaries, API/lock/resource/recovery gates, and explicit promotion; separately gate sparse-child compaction before claiming bounded storage |
| Evaluate bounded artifact analytics | DuckDB/Parquet is routed as an artifact-only option, not a production source of truth | Bounded export/replay benchmark that preserves PostgreSQL publication correctness and stays on the FST drive |
| Verify freeze-safe publication cache at a natural publication switch | Service-only promotion is complete. Scrape `1310` advanced publication `103`, preserved persisted reads through one deferred retry, and recorded zero HTTP failures across 309 monitor samples. The scrape evidence still did not attribute first-hit L1/L2 recovery or prove every invalidation-card observation. | At the next bounded cache-specific test, capture pre-publication leakage checks, atomic current/previous cache binding, L1 reset, first-hit L2 attribution, exact route parity, and public health without coupling that evidence to another data candidate |

Detailed post-scrape phase, progress, replay, deployment, A/B, and optimization
work is owned by the
[post-scrape processing roadmap](post-scrape-processing.md).

Completed physical cleanup, compaction, retirement, and rejected stored-rank
rollout documents were removed from the current tree and must not be
reintroduced as pending work.
