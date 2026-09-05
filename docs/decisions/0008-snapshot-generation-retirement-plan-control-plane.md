---
status: decision
owner: data
last_verified: 2026-09-04
last_verified_commit: afb562d2
sources:
  - docs/database/SnapshotGenerationRetentionSafety.md
  - docs/database/SnapshotGenerationRetirementControlPlane.md
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetirementSchema.cs
  - tools/FstSnapshotGenerationRetirement/
update_triggers:
  - The automatic-retirement process boundary or first executable stage changes.
---

# ADR 0008: Start automatic retirement with a host-owned plan control plane

## Status

Accepted.

## Context

The report-only planner, archive/proof package, quarantine/reattach tier, and
guarded DROP/logical-restore tier have independent production evidence.
Recurring retention still needs bounded policy, durable target identity,
auditable transitions, deterministic ordering, and crash-safe orchestration.

The first archive-executing control-plane prototype also required exact
container binding, long-lived database admission locks, cooperative subprocess
cleanup, Docker resource ownership, interruption reconciliation, lease
recovery, and cross-role worker/service changes. Independent review found that
shipping all of those concerns together would make the first default-off slice
harder to prove than the behavior it was intended to control.

## Decision

Introduce a separate host-owned .NET executable and additive schema, but limit
the first slice to authorization, status, reconciliation, operator
deactivation, and deterministic largest-first planning.

The service and worker do not invoke the tool and do not acquire new admission
locks. The tool writes only immutable policy/event evidence, the default-off
control singleton, and one plan-only job. It has no archive or destructive
command and no Docker integration.

The schema and tool preserve these invariants:

- report-only planner relations and hashes are unchanged;
- authorization is time-, count-, byte-, reviewer-, code-, and database-bound;
- one globally planned job is selected from the newest accepted cycle;
- Solo Bass snapshot `1308` is excluded;
- no fallback target is selected;
- target identity and physical size are immutable;
- stale, expired, exhausted, or operator-stopped work terminalizes durably;
- plan/reconcile share the publication/planner/partition-DDL lock protocol and
  lock only the exact root/child catalog surface through commit;
- mutable holds, unreplayed writer failures, target scrape state, and active
  worker operation are revalidated and fenced;
- server-side transaction timeouts bound lock ownership if the host stalls;
- control mutation follows control, policy, then job row-lock order.

## Consequences

The project gains a reviewable and deployable control-plane foundation without
changing scrape, publication, API, worker, or archive behavior. It can collect
production planning evidence while normal ingestion continues.

It does not reduce storage and cannot support the previously proposed
three-cycle archive gate. Archive execution remains a later change that must
independently solve exact source-container binding, full-duration admission,
cooperative cancellation, and owned-process/container/volume/scratch cleanup.

Microservice extraction remains unjustified. The fixed-purpose host executable
provides process isolation and replayability without adding an always-on
service, network API, deployment unit, or distributed transaction boundary.

## Rejected alternatives

- **Ship archive execution in the first slice:** rejected because process and
  admission cleanup could not yet be proven fail-closed under cancellation,
  backend loss, and physical-clone scenarios.
- **Run retirement inside the worker or API:** rejected because a maintenance
  failure could share lifecycle and privileges with production ingestion or
  serving.
- **Keep only in-memory plans:** rejected because authorization, target
  identity, supersession, and review evidence must survive process restarts.
- **Start with microservices:** rejected because no independent scaling or
  availability need offsets the operational and consistency cost.
