---
status: decision
owner: data
last_verified: 2026-08-19
last_verified_commit: 21d7193c
sources:
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Scraping/LeaderboardSpoolWriterFactory.cs
  - docs/architecture/data-storage.md
  - docs/database/ProBassSnapshotRewritePilot.md
  - docs/database/SnapshotGenerationPartitionMigration.md
update_triggers:
  - Physical snapshot partitioning, retention, write routing, archive, migration, or rollback changes.
---

# ADR 0006: Subpartition physical snapshots by generation

## Decision

Keep PostgreSQL as the physical leaderboard snapshot source of truth.

Partition `leaderboard_entries_snapshot` first by instrument and then by
`snapshot_id`. Each instrument partition owns:

- one child for every retained physical snapshot generation;
- an empty default child for compatibility and fail-closed diagnostics;
- a partitioned primary key and score index whose leaf indexes belong to each
  generation.

The worker must ensure the exact generation child exists before inserting
snapshot rows. The helper is fixed to the nine supported instruments, uses an
advisory transaction lock for concurrent batch writers, and is a no-op while a
production instrument is still on the pre-migration regular-table layout.

Retention archives and restore-proves an original instrument before replacing
it. Current, previous, working, active-state, and projection-source physical
IDs remain hot. Obsolete generations are reclaimed by dropping whole child
relations instead of rewriting a cumulative instrument table.

This decision establishes the physical layout and migration path; it does not
by itself authorize or schedule recurring child deletion. The separately
implemented operator-only DROP/restore tier derives one exact target from
immutable retention, archive, and quarantine evidence and remains live-gated.
It is not a worker retention owner. Automatic recurring child deletion still
requires a later independently accepted promotion.

## Context

The former instrument-only layout appended every generation into one regular
partition. Snapshot indexes represented more than half of physical storage,
and reclaiming old IDs required rewriting the complete instrument.

The accepted pro-bass transition reduced a 150,098,894,848-byte relation to
2,811,404,288 bytes. Validation scrape 1303 then reused 350/702 pro-bass
scopes, but the regular partition still grew by 1,000,898,560 bytes and retained
obsolete snapshot 1301. Snapshot reuse reduces new writes; it does not make old
generations independently reclaimable.

Pro bass and pro guitar were then converted to generation children. Validation
scrape 1304 routed `1,395,539` pro-bass rows and `3,674,245` pro-guitar rows
into dedicated `1304` children while both defaults remained empty. Exact
published source counts matched the children, publication advanced and
unfroze, notifications and registration drain completed, and the run-once
worker exited normally. This validates mixed legacy/generation write routing;
it does not implement recurring child retention.

## Rationale

- Existing reads already constrain `snapshot_id` and `instrument`, so the
  second partition key matches the dominant access path.
- The parent primary key includes `snapshot_id`, satisfying PostgreSQL unique
  partition-key requirements.
- New generation children are empty at creation, so index creation is cheap.
- Whole-child drop gives predictable, immediate reclaim with no dead tuples,
  `VACUUM FULL`, or full-instrument rewrite.
- Instrument-first ownership preserves current table naming, query contracts,
  publication semantics, and per-instrument migration/rollback.

## Alternatives

### Keep instrument-only partitions plus periodic rewrites

Rejected. Rewrites repeatedly require duplicate heap/index capacity, long
copy/index work, and complex retained-original rollback.

### Partition only by snapshot ID

Rejected. It removes the existing instrument isolation and makes
instrument-local operations, capacity decisions, and phased migration harder.

### Delete old IDs and vacuum

Rejected. Batched deletes create large WAL/dead-tuple pressure and do not
return filesystem space predictably.

### Store snapshots only in object/columnar artifacts

Rejected for the live source of truth. Archive artifacts remain recovery and
analysis inputs; PostgreSQL publication/source semantics stay authoritative.

## Migration sequence

1. Deploy compatible generation-child creation while existing regular
   instrument partitions remain valid.
2. Hold the worker and capture current protected physical IDs.
3. Archive and restore-prove each original instrument.
4. Build a replacement partitioned by `snapshot_id`, copy protected IDs into
   dedicated children, and create an empty default child.
5. Short-swap under exact catalog, publication, lock, and API guards while
   retaining the original for rollback.
6. Validate fingerprints, references, parent index attachment, and public
   parity.
7. Drop the original only after acceptance, with the restore-proved archive
   copied into an independent read-only recovery package, kernel-leased, and
   checksum/evidence-chain reverified through the destructive commit and
   durable report. Persist its recovery manifest before commit and keep an
   anchored authoritative path so a torn post-commit report can be rebuilt
   without the original archive. Retain that package until the separate
   archive-deletion decision.
8. Run exactly one guarded generation-aware validation scrape through
   publication, notification recovery, post-publication registration drain,
   and normal run-once worker exit; then hold the worker again.
9. Repeat the migration and validation cycle one instrument at a time.
10. Validate the separate exact-child DROP/restore canary, including retained
    hold/DEFAULT fencing and logical restore with a new physical identity.
11. Implement and validate recurring archive-before-child-drop retention,
    including empty/default-child auditing.
12. Restore unattended normal scraping only after all nine instruments are migrated and
    the recurring retention owner is ready.

## Consequences

- The worker performs one idempotent generation-ensure call before snapshot
  inserts.
- Direct diagnostic/test inserts still route to the default child.
- A failed ensure cannot silently create an arbitrary table or instrument.
- Retention becomes child-drop maintenance rather than table rewrite.
- Layout migration does not make child drop automatic; recurring archive/drop
  ownership remains an explicit promotion gate.
- Each migrated instrument receives one complete run-once scrape validation
  before another instrument is migrated.
- Archives remain required before destructive migration or removal.
- Existing SQL readers continue to query `leaderboard_entries_snapshot`
  unchanged.
