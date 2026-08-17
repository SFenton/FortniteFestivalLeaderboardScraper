---
status: roadmap
owner: data
last_verified: 2026-08-17
last_verified_commit: 57efc5bd
sources:
  - FSTService/FeatureOptions.cs
  - FSTService/appsettings.json
  - deploy/config/fstservice-role.env
  - deploy/config/fstworker-role.env
  - docs/database/SnapshotReuseRunbook.md
  - docs/database/StaleSoloRankIndexRetirementRunbook.md
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
| Execute exact `ix_le_song_rank` retirement | The guarded package validates one parent plus nine leaves, `5,147,222,016` bytes, zero observed scans with an explicit statistics-reset caveat, no constraint ownership, and exact rollback; live DDL remains unexecuted | Focused PR review, deployment of reviewed tooling, fresh matching check package/digests, quiet lock window, exact post-drop catalog/filesystem proof, and full public-path validation |
| Produce an exact snapshot-capacity/reclaim plan | Report-only retention planning is already enabled, but its exact protected generations, candidate partitions, purge/retained bytes, rewrite workspace, runtime cost, and rollback objects are not persisted in current evidence; rewrite remains off and current free space is below the 500 GiB gate | Run the existing planner read-only after a safe scrape boundary, preserve its exact output, prove planner load is safe, then require current live-scrape parity and rollback before any rewrite/reclaim |

Detailed post-scrape phase, progress, replay, deployment, A/B, and optimization
work is owned by the
[post-scrape processing roadmap](post-scrape-processing.md).

Completed physical cleanup, compaction, retirement, and rejected stored-rank
rollout documents were removed from the current tree and must not be
reintroduced as pending work.
