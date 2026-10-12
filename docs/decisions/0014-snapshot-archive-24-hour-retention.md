---
status: decision
owner: data
last_verified: 2026-10-12
last_verified_commit: 33ae0555
sources:
  - docs/database/SnapshotGenerationArchiveRetirementRunbook.md
  - tools/postgres-snapshot-archive-retire.py
  - tools/postgres-snapshot-sparse-compact.py
  - deploy/systemd/fst-snapshot-retire-auto.service
update_triggers:
  - The archive retention period, prunable manifest states, or the timer's prune step change.
---

# ADR 0014: Delete snapshot archives 24 hours after retirement or compaction

## Context

Retirement ([ADR 0010](0010-snapshot-archive-retirement.md),
[ADR 0011](0011-automatic-snapshot-archive-retirement.md)) and compaction
([ADR 0012](0012-sparse-snapshot-child-compaction.md)) archive every child with
`pg_dump` before mutating it, and kept every archive. By 2026-10-12 the
archives held 196 GB on the FST drive and grew about 9 GB a day, more than the
database itself once rank history kept 30 days. They hold only rows that no
publication, active state, or projection references. The operator decided they
should be deleted automatically.

## Decision

Each tick of the retirement timer runs `prune-archives` after retirement and
compaction. It deletes an archive once its child has been dropped, compacted,
or restored, or was archived without being changed, for 24 hours. It records
an `archive_deleted` manifest entry and keeps the manifests. Archives of
unverified compactions (`swapped`) are never deleted, and nothing is deleted
while either `AUTO_DISABLED` file exists.

The archive step itself stays: the dump verifies the row count and periodic
restore drills before any drop, and gives a one-day rollback window.

## Consequences

- Archives stay at about one day's worth (about 10 GB) instead of growing.
- A retired or compacted child can be restored only within 24 hours. After
  that, superseded snapshot rows are gone.
- An integrity failure that trips `AUTO_DISABLED` pauses pruning, so the
  evidence and rollback sources for that run are kept until an operator clears
  it.
