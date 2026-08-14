---
status: canonical
owner: operations
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - docs/database/
update_triggers:
  - A runbook is created, completed, rejected, rehearsed, or removed.
---

# Runbooks

## Living procedures

| Procedure | Path | Safety note |
|---|---|---|
| Improvement notification recovery | [`ImprovementNotificationRecoveryRunbook.md`](../../database/ImprovementNotificationRecoveryRunbook.md) | Operates on an already-published scrape; preserve its projection plan |
| Max-score correction maintenance | [`MaxScoreCorrectionMaintenanceRunbook.md`](../../database/MaxScoreCorrectionMaintenanceRunbook.md) | Stage first; apply is manifest/plan-digest bound and fail-closed |
| Score-history deduplication | [`ScoreHistoryDedupMaintenanceRunbook.md`](../../database/ScoreHistoryDedupMaintenanceRunbook.md) | Dry run first; execute is digest-bound and maintenance-gated |
| Snapshot reuse evaluation | [`SnapshotReuseRunbook.md`](../../database/SnapshotReuseRunbook.md) | Treat status sections as evidence to revalidate, not automatic approval |
| Solo-family ranking backfill | [`SoloFamilyRankingBackfillRunbook.md`](../../database/SoloFamilyRankingBackfillRunbook.md) | Requires quiescence and publication-lock safety |

## Completed and rejected procedures

After their valid current-state conclusions are incorporated into canonical
docs, completed and rejected one-shot procedure documents are removed. Git
history may be consulted for forensic context, but removed commands are not
current procedures or reusable authorization.

All database/storage maintenance remains subject to
[`live-safety.md`](../live-safety.md).
