---
status: canonical
owner: repository
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - README.md
  - AGENTS.md
  - .github/instructions/documentation.instructions.md
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
| Work on the React application | [Web app](components/web-app.md) |
| Work on HTTP serving and API behavior | [Service and API](components/service-api.md) |
| Work on scheduled scraping and derived data | [Worker](components/worker.md) |
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

## Living runbooks

These procedures remain at their established paths because they may be used
again. Verify their preconditions and current code before execution.

- [Improvement notification recovery](database/ImprovementNotificationRecoveryRunbook.md)
- [Score-history deduplication maintenance](database/ScoreHistoryDedupMaintenanceRunbook.md)
- [Snapshot reuse evaluation](database/SnapshotReuseRunbook.md)
- [Solo-family ranking backfill](database/SoloFamilyRankingBackfillRunbook.md)
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
