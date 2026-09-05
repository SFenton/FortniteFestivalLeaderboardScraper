---
status: canonical
owner: repository
last_verified: 2026-08-27
last_verified_commit: 21d7193c
sources:
  - README.md
  - AGENTS.md
  - .github/instructions/documentation.instructions.md
  - docs/architecture/replay-artifacts.md
  - docs/database/StaleSoloRankIndexRetirementRunbook.md
  - docs/database/ProBassSnapshotRewritePilot.md
  - docs/database/SnapshotGenerationPartitionMigration.md
  - docs/database/SnapshotGenerationRetentionSafety.md
  - docs/database/SnapshotGenerationRetirementControlPlane.md
  - docs/database/SnapshotGenerationDropRunbook.md
  - docs/database/PublicationPathArtifactSnapshots.md
update_triggers:
  - Any canonical document is added, moved, or removed.
---

# Documentation

This index contains current guidance, living procedures, forward-looking work,
and architectural decisions. Code, configuration, tests, and observed runtime
evidence remain the behavioral sources of truth.

## Start here

| Need | Canonical document |
|---|---|
| Understand the whole system | [System overview](architecture/system-overview.md) |
| Follow data from Epic to the browser | [Scrape and publication flow](architecture/data-publication-flow.md) |
| Understand PostgreSQL ownership and data shapes | [Data storage](architecture/data-storage.md) |
| Review snapshot-generation retention safety | [Snapshot generation retention safety](database/SnapshotGenerationRetentionSafety.md) |
| Operate the plan-only retirement control plane | [Snapshot generation retirement plan control plane](database/SnapshotGenerationRetirementControlPlane.md) |
| Execute the gated snapshot-generation DROP/restore canary | [Snapshot generation DROP and logical restore](database/SnapshotGenerationDropRunbook.md) |
| Understand immutable replay evidence packages | [Replay evidence artifacts](architecture/replay-artifacts.md) |
| Work on the React application | [Web app](components/web-app.md) |
| Work on HTTP serving and API behavior | [Service and API](components/service-api.md) |
| Work on scheduled scraping and derived data | [Worker](components/worker.md) |
| Understand optimal path generation and regeneration | [Path generation](components/path-generation.md) |
| Work on shared .NET or TypeScript code | [Shared code](components/shared-code.md) |
| Review API synchronization requirements | [API contract](reference/api-contract.md) |
| Configure a role or deployment | [Configuration](reference/configuration.md) |
| Review backend and public feature flags | [Feature flags](reference/feature-flags.md) |
| Run a service mode or one-shot command | [CLI reference](reference/cli.md) |
| Find repository scripts and tools | [Tooling](reference/tooling.md) |
| Deploy the container stack | [Deployment topology](operations/deployment.md) |
| Understand the Gluetun/VPN proxy pool | [VPN proxy pool](operations/vpn-proxy-pool.md) |
| Perform live-sensitive work | [Live safety](operations/live-safety.md) |
| Select validation commands | [Testing](testing/README.md) |
| Find active future work | [Roadmap](roadmap/README.md) |
| Understand why a boundary exists | [Architecture decisions](decisions/README.md) |
| Change documentation safely | [Documentation governance](governance/documentation.md) |

## Database safety

- [Snapshot generation retention safety](database/SnapshotGenerationRetentionSafety.md)
- [Snapshot generation retirement plan control plane](database/SnapshotGenerationRetirementControlPlane.md)

## Living runbooks

These procedures remain at their established paths because they may be used
again. Verify their preconditions and current code before execution.

- [Improvement notification recovery](database/ImprovementNotificationRecoveryRunbook.md)
- [Max-score correction maintenance](database/MaxScoreCorrectionMaintenanceRunbook.md)
- [Pro-bass snapshot archive/rewrite pilot](database/ProBassSnapshotRewritePilot.md)
- [Publication path artifact snapshots](database/PublicationPathArtifactSnapshots.md)
- [Score-history deduplication maintenance](database/ScoreHistoryDedupMaintenanceRunbook.md)
- [Snapshot generation partition migration](database/SnapshotGenerationPartitionMigration.md)
- [Snapshot generation DROP and logical restore](database/SnapshotGenerationDropRunbook.md)
- [Snapshot reuse evaluation](database/SnapshotReuseRunbook.md)
- [Solo-family ranking backfill](database/SoloFamilyRankingBackfillRunbook.md)
- [Stale solo rank index retirement](database/StaleSoloRankIndexRetirementRunbook.md)
- [Runbook index and lifecycle](operations/runbooks/README.md)

## Documentation classes

| Status | Meaning |
|---|---|
| `canonical` | Current explanation of implemented behavior. |
| `living-runbook` | Repeatable operator procedure with current gates and rollback. |
| `roadmap` | Unresolved work only; never evidence that behavior exists. |
| `decision` | Accepted architectural rationale and consequences. |

Obsolete documentation is removed from the current tree after its valid
conclusions are incorporated into canonical docs. Git history remains
available for forensic review, but is not current guidance.

Run `node tools/check-docs.mjs` before completing a documentation-affecting
change.
