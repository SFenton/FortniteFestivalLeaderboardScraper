---
status: roadmap
owner: data
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - FSTService/FeatureOptions.cs
  - FSTService/appsettings.json
  - deploy/config/fstservice-role.env
  - deploy/config/fstworker-role.env
  - docs/database/SnapshotReuseRunbook.md
  - docs/database/StorageOwnershipReadinessRunbook.md
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
| Revalidate storage ownership/readiness | The readiness runbook mixes completed evidence with remaining gates | Each proposed ownership change receives an explicit accepted/rejected decision |
| Evaluate bounded artifact analytics | DuckDB/Parquet is routed as an artifact-only option, not a production source of truth | Bounded export/replay benchmark that preserves PostgreSQL publication correctness and stays on the FST drive |

Completed physical cleanup, compaction, retirement, and rejected stored-rank
rollouts are archive evidence and must not be reintroduced as pending work.
