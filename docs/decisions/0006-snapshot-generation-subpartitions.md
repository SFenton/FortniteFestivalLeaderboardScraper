---
status: decision
owner: data
last_verified: 2026-08-19
last_verified_commit: 4c36926a
sources:
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionSchema.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.cs
  - FSTService/Scraping/LeaderboardSpoolWriterFactory.cs
  - docs/architecture/data-storage.md
  - docs/database/ProBassSnapshotRewritePilot.md
  - docs/database/SnapshotGenerationPartitionMigration.md
  - docs/database/SnapshotGenerationRetentionSafety.md
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
authorize recurring child deletion. The implemented default-off durable
planner derives exact per-instrument protection from
current/previous/working publication sources plus active/projection state,
audits exact partition keys, catalog/default/index shape, publication bindings,
catalog-derived source key sets, current fingerprints, and authoritative-empty
counterparts, and persists typed report-only `observed` jobs plus append-only
hash-chained evidence. Lifecycle generation IDs do not imply a missing
physical leaf for all-unchanged generations. The planner cannot archive,
restore-prove, detach, or drop. A separately gated executor/prover must perform
those actions before any automatic retirement is accepted.

The isolated PostgreSQL 17 retention drill now verifies that one selected leaf
can be custom-archived with its parent/root/default shape, restored with exact
catalog and content parity, handed to a no-socket filesystem prover, and
removed through either measured attached-drop or ordinary
detach/check/reattach/detached-drop mechanics. The accepted proof requires the
exact initial/final local Docker context, socket, daemon, and image identities;
endpoint-pinned Engine commands; ID-only `--pull=never` creation; run-owned
PGDATA binds for every transient container; repeated cleanup to an empty owned
inventory; an unchanged Docker volume inventory; and an integrity-valid
terminal seal in a nonwritable, symlink-free tree. Four superseded sealed
packages remain forensic/rejected: two leaked eight anonymous volumes total,
and two zero-volume repairs retained Docker/image TOCTOU, premature success
publication, and first-error cleanup semantics. This resolves implementation
facts only. It does not select the production drop strategy or authorize a
recurring owner.

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
10. Record default-off durable whole-child plans after publication,
    notifications, and registration drain; retry current publications across
    restart/incomplete-drain boundaries, and validate exact per-instrument
    fences, source authority, empty defaults, and non-executable `observed`
    report-only state.
11. Use the accepted isolated safety package to implement and validate the
    separate archive-before-child-drop executor and network-none prover.
12. Restore unattended normal scraping only after all nine instruments are
    migrated and the recurring execution owner is ready.

## Consequences

- The worker performs one idempotent generation-ensure call before snapshot
  inserts.
- Direct diagnostic/test inserts still route to the default child.
- A failed ensure cannot silently create an arbitrary table or instrument.
- Retention becomes child-drop maintenance rather than table rewrite.
- Direct attached drop and ordinary detach both take
  `AccessExclusiveLock` on the instrument root in the bounded synthetic
  fixture; production selection remains canary- and parity-gated.
- `DETACH PARTITION ... CONCURRENTLY` is not an option because the exact
  reference fence must share the DDL transaction and every instrument root has
  a default child.
- Layout migration does not make child drop automatic; recurring archive/drop
  ownership remains an explicit promotion gate.
- Report-only eligible jobs are typed `observed`, cannot enter executor
  states, and do not block scrape allocation. Only future non-report-only
  `leased`/`executing` destructive state or a hard safety failure may do so.
- Disabling the planner stops new intent while preserving immutable evidence.
- Each migrated instrument receives one complete run-once scrape validation
  before another instrument is migrated.
- Archives remain required before destructive migration or removal.
- Existing SQL readers continue to query `leaderboard_entries_snapshot`
  unchanged.
