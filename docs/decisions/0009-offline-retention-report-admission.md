---
status: decision
owner: data
last_verified: 2026-09-06
last_verified_commit: 880802ec
sources:
  - docs/database/SnapshotGenerationOfflineRetentionReport.md
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.Offline.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.Locks.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionRepository.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionSchema.cs
  - tools/FstSnapshotGenerationRetentionReport/
update_triggers:
  - Offline-report provenance, transaction admission, runtime attestation, or production promotion boundaries change.
---

# ADR 0009: Produce genuine retention evidence after an external idle stop

## Status

Implemented, independently reviewed candidate. Scale adjudication permits the
bounded canary described below; reviewed deployment, its fresh operator gate
and actual production acceptance remain outstanding.

## Context

The continuous worker queues a terminal report after publication but drains it
immediately before the next pass. That pass freezes public reads before its
synchronous, cancellation-free allocation lock. Holding the publication key
shared therefore prevents allocation without establishing a safe point to stop
the worker. Increasing polling frequency cannot repair that ordering.

The accepted planner's worker admission also consumes process-local
background-quiescence and scores-changed broadcast facts. An offline host
cannot honestly fabricate those facts or invoke notifications to reconstruct
them.

## Decision

Add a separate pinned host command that observes only the current publication
after an externally established stopped-worker boundary. It creates no service
host and invokes the real planner's shared observation/oracle/classification/
hashing/persistence primitives.

Use the distinct `operator_offline_post_publication` safe-point kind. This is
provenance, not identity: one canonical cycle is keyed by trigger
scrape/publication across both kinds. Cycle and deferral checks are exact,
and upgrade refuses existing duplicates instead of rewriting immutable rows.
This is not a reinterpretation
of `terminal_worker_post_publication` or a new pending-work flag. Existing
worker requests retain every original admission requirement. Existing
persisted rows and their canonical hash encoding remain unchanged.

Require an immutable receipt of the stopped worker's actual deployed
report-only configuration and canonical lookup protocol version, bound to its
instance and service code hash. The CLI cannot self-enable. New offline
reporting is gated until the compatible worker has published that receipt.

Compose bounded transaction-scoped advisory admission in canonical order.
PostgreSQL scopes the schema key by database; its initializer acquisition is
nonblocking. Acquire mutable-state locks
before the offline transaction's first snapshot, and retain them through
normal report persistence and final admission validation. A shared schema
fence prevents supported initialization from crossing attestation. A separate
advisory transaction releases immediately after the real data transaction
commits, before disposal; no publication session lock, unbounded exclusive
waiter, or durable execution lease is added.

An already-current accepted cycle is genuinely reobserved and hash-compared;
it is returned unchanged or refused, never silently replaced. Failed/mismatched
observations retain ordinary report-only evidence semantics.

Explicit binary, source-bundle, wrapper, Git-base, database/role, and schema
assertions identify the actual candidate. Uncommitted reviewed source is
reported as such rather than assigned a fictional commit. The existing
retirement policy's separate clean-tree contract is unchanged.

Budget exhaustion is a measured, explicit rollback outcome; the existing
15/120-second bounds are not raised speculatively. A committed result can
survive cleanup failure only with authoritative exact-cycle and ownership-
absence confirmation. Loader variables cannot redirect child execution or
PATH utilities; a trusted host launcher remains required.

## Scale measurement decision

Use one bounded first live offline-report canary after reviewed deployment and
an externally established idle stop. Do not require a full physical duplicate
that would consume most available ext4 FST-drive capacity including operating
reserve. The old partial restores are not scale evidence. Full-scale worker
observations provide a useful baseline for shared planner work, not proof of
the new host admission path.

Keep all existing identity, receipt, publication, notification, source-parity
and timeout gates. A current accepted cycle is fully revalidated and reused;
only a publication without a canonical cycle may acquire one new offline
cycle. Local commit/build can precede measurement, but no deployment, budget
increase, automatic lifecycle action or archive capability follows from that.

## Rejected shortcuts

- Fabricating a worker broadcast/quiescence request or inserting a cycle
  manually.
- Starting an API/worker host merely to reach the planner.
- Deliberately failing a completed scrape's resume validation.
- Stopping a worker blocked after it has already frozen reads.
- Duplicating planner SQL or weakening its independent liveness oracle.
- Treating this bounded report transaction as archive/execution admission.

## Consequences and rollback

The operator can use the prior idle interval to stop safely, then obtain the
current report without another scrape. No production lifecycle action is
implemented or authorized by the command.

The schema extension must be deployed through normal reviewed initialization;
the host tool refuses a missing or older shape and never repairs it. Rolling
back the host binary does not require deleting reports or narrowing the cycle
constraint. Preserve offline-kind evidence and the additive schema once such
rows exist. Retain a canonical-aware mutation worker after offline evidence
exists; an older kind-scoped worker cannot interpret its uniqueness conflict.
Production canary/scale acceptance and later archive execution remain separate
gates. The report-only scale decision does not waive normal backup/recovery or
destructive parity requirements.
