---
status: canonical
owner: operations
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - docs/database/
  - docs/archive/legacy/database/
update_triggers:
  - A runbook is created, completed, rejected, rehearsed, or archived.
---

# Runbooks

## Living procedures

| Procedure | Path | Safety note |
|---|---|---|
| Improvement notification recovery | [`ImprovementNotificationRecoveryRunbook.md`](../../database/ImprovementNotificationRecoveryRunbook.md) | Operates on an already-published scrape; preserve its projection plan |
| Score-history deduplication | [`ScoreHistoryDedupMaintenanceRunbook.md`](../../database/ScoreHistoryDedupMaintenanceRunbook.md) | Dry run first; execute is digest-bound and maintenance-gated |
| Snapshot reuse evaluation | [`SnapshotReuseRunbook.md`](../../database/SnapshotReuseRunbook.md) | Treat status sections as evidence to revalidate, not automatic approval |
| Solo-family ranking backfill | [`SoloFamilyRankingBackfillRunbook.md`](../../database/SoloFamilyRankingBackfillRunbook.md) | Requires quiescence and publication-lock safety |
| Storage ownership readiness | [`StorageOwnershipReadinessRunbook.md`](../../database/StorageOwnershipReadinessRunbook.md) | Readiness/evaluation document; do not infer promotion |

## Terminal evidence

Completed and rejected one-shot procedures are under
[`docs/archive/legacy/database/`](../../archive/legacy/database/). Compatibility
stubs at the old paths explicitly prevent repeat execution.

All database/storage maintenance remains subject to
[`live-safety.md`](../live-safety.md).
