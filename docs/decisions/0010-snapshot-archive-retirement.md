---
status: decision
owner: data
last_verified: 2026-09-26
last_verified_commit: 95351cac
sources:
  - docs/database/SnapshotGenerationArchiveRetirementRunbook.md
  - tools/postgres-snapshot-archive-retire.py
  - docs/database/SnapshotGenerationDropRunbook.md
  - docs/database/SnapshotGenerationRetentionSafety.md
update_triggers:
  - Snapshot retirement authorization, archive/verification requirements, or restore semantics change.
---

# ADR 0010: Retire report-only candidates through verified archives

## Context

Report-only retention cycles have agreed (planner and independent oracle) on
roughly 995 unreferenced snapshot-generation children since early September,
about 1.1 TB of a 2.36 TB database on a 3.6 TB drive. The per-child
quarantine, attestation, DROP, and restore-continuation machine
([ADR 0007](0007-snapshot-generation-drop-and-logical-restore.md)) retired and
restored one child after many gated revisions. It could not reclaim this
backlog in practice, and storage pressure kept growing.

## Decision

The operator retires a cycle's candidates with a single tool that binds the
exact cycle hash, re-proves each child's physical identity and liveness
immediately before and after archiving, stores a zstd `pg_dump` archive of
every child on the FST drive with an independent row-count verification and
periodic full restore drills, and only then detaches (plain, lock-bounded) and
drops it without CASCADE. Rollback is one `restore` command that recreates,
re-attaches, and fingerprint-checks the child.

## Consequences

- Space is reclaimed at about 92% of each child's on-disk size, because
  archives omit indexes and compress well.
- Historical snapshot data stays recoverable; the archive, not the live child,
  becomes the retained copy for retired generations.
- Liveness proof relies on the same report-only planner/oracle cycle plus
  execution-time re-proof of publication roots, instead of per-child
  quarantine soak periods.
- A plain detach briefly takes an exclusive lock on one instrument parent;
  bounded lock timeouts keep that impact to seconds, and runs avoid
  post-processing.
- The quarantine/DROP machine remains available but is no longer the path for
  bulk retirement.
