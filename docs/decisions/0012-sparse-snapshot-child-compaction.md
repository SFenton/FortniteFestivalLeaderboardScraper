---
status: decision
owner: data
last_verified: 2026-10-02
last_verified_commit: b7f39f16
sources:
  - docs/database/SnapshotGenerationArchiveRetirementRunbook.md
  - tools/postgres-snapshot-sparse-compact.py
  - tools/postgres-snapshot-sparse-compact-drill.py
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionOracle.cs
update_triggers:
  - Snapshot liveness roots, compaction eligibility, swap, archive, or rollback semantics change.
---

# ADR 0012: Compact sparse snapshot children to their live scopes

## Context

Whole-child retirement ([ADR 0010](0010-snapshot-archive-retirement.md),
[ADR 0011](0011-automatic-snapshot-archive-retirement.md)) reclaims children
that no root references. With skip-unchanged physical snapshots, each child
stays referenced by the scopes that have not changed since that scrape. On
2026-10-02, 217 attached children held 231 GiB, but 133 of them were at most
50% live by estimated rows and about 186.5 GiB was unreachable. Children decay
into this state continuously, so retirement alone cannot bound storage.
Repacking the churn-heavy band tables was rejected because their roughly 50%
bloat regrows within one or two scrapes; sparse snapshot rows are immutable and
never return.

## Decision

An operator tool compacts one child at a time. Liveness uses the retention
oracle's roots at song granularity (active snapshot state, solo projection
source, and published scope sources of the current, previous, working,
building, or current publications); any whole-child root skips the child. The
tool archives and verifies the whole child like retirement, builds a
replacement holding only the live songs' rows with the parent's index shapes
and a validated bound `CHECK`, requires an exact live-subset fingerprint,
re-proves liveness, and swaps it in with one lock-bounded `DETACH`/`ATTACH`
transaction that re-checks liveness under the parent lock. The old child is
dropped without `CASCADE` only after attachment, index adoption, and the live
fingerprint verify.

## Consequences

- Live readers see identical rows: compaction changes only rows that no
  publication, active state, or projection can reach.
- A compacted child gets a new physical identity. Report cycles recorded after
  the swap classify it normally; it may be compacted again as it decays and is
  retired once no scope references it.
- Rollback is online: the archived non-live rows are reinserted into the
  attached child and the original whole-child fingerprint is required.
- The swap holds the instrument parent's exclusive lock only for catalog work
  (milliseconds) because the indexes are prebuilt and the `CHECK` skips the
  attach scan; contention defers instead of queueing.
- Compaction is manual until a production canary and a following clean report
  cycle are accepted; it shares retirement's lock and live-safety windows.
