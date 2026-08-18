---
status: living-runbook
owner: data
last_verified: 2026-08-18
last_verified_commit: 3c467408
sources:
  - tools/postgres-snapshot-generation-migration.py
  - tools/postgres-snapshot-generation-migration.sh
  - tools/postgres-snapshot-generation-migration-drill.py
  - tools/postgres-snapshot-generation-migration.test.py
  - tools/postgres-pro-bass-snapshot-rewrite.py
  - docs/database/ProBassSnapshotRewritePilot.md
  - docs/operations/live-safety.md
update_triggers:
  - Snapshot instrument bounds, protected-source ownership, archive/restore evidence, migration stages, capacity margins, rollback, or retention rules change.
---

# Snapshot generation partition migration

## Status and boundary

The fixed migration package converts one
`leaderboard_entries_snapshot` instrument partition at a time from a regular
table into a `LIST (snapshot_id)` partitioned table. It retains only the exact
physical snapshot IDs still required by active state, the solo current
projection, and the current/previous/working publication source maps.

The package has passed an isolated PostgreSQL 17 drill. It has **not** run
against production. This runbook is not authorization to start a scrape,
unfreeze reads, select alternate scratch storage, delete an archive, or weaken
a failed gate.

This package migrates physical layout only. It does not implement recurring
generation retention. Do not resume normal worker scheduling after the nine
instrument migrations until a separate archive-before-child-drop owner is
implemented, restore-tested, documented, and accepted.

Production Compose ownership remains:

```text
/home/sfenton/Docker/FestivalServiceTracker
```

Repository Compose files are templates. The only authorized temporary scratch
device is `/dev/nvme2n1p2`, mounted at `/`. Accepted PostgreSQL relations and
every retained generation child must finish in `pg_default` on the 4 TB FST
PGDATA filesystem. The tool never creates an 8 TB tablespace.

## Fixed targets

The command accepts only these nine compiled instrument keys. There is no
relation, table, partition-bound, or SQL argument.

| Key | Fixed partition | Fixed bound |
|---|---|---|
| `solo-guitar` | `leaderboard_entries_snapshot_solo_guitar` | `Solo_Guitar` |
| `solo-bass` | `leaderboard_entries_snapshot_solo_bass` | `Solo_Bass` |
| `solo-drums` | `leaderboard_entries_snapshot_solo_drums` | `Solo_Drums` |
| `solo-vocals` | `leaderboard_entries_snapshot_solo_vocals` | `Solo_Vocals` |
| `pro-guitar` | `leaderboard_entries_snapshot_pro_guitar` | `Solo_PeripheralGuitar` |
| `pro-bass` | `leaderboard_entries_snapshot_pro_bass` | `Solo_PeripheralBass` |
| `pro-vocals` | `leaderboard_entries_snapshot_pro_vocals` | `Solo_PeripheralVocals` |
| `pro-cymbals` | `leaderboard_entries_snapshot_pro_cymbals` | `Solo_PeripheralCymbals` |
| `pro-drums` | `leaderboard_entries_snapshot_pro_drums` | `Solo_PeripheralDrums` |

The accepted pro-bass rewrite and its historical custom archive remain
independent evidence. Converting the current pro-bass relation into generation
children still creates and restore-proves a new archive of that exact current
source before dropping it. Existing pro-bass evidence must not be deleted or
treated as scratch capacity.

## Current protected-source expectation

Validation scrape `1303` is current and `1302` is previous. Publication
generations `89` and `80` currently resolve to those scrapes, and observed
source maps reuse physical IDs `1302` and `1303`. That is planning evidence,
not a hard-coded retention list.

For each instrument, `plan` independently derives and groups IDs from:

1. `leaderboard_snapshot_state.active_snapshot_id`;
2. `solo_current_projection_scope.source_snapshot_id`;
3. `leaderboard_published_scope_source.source_snapshot_id` for publication
   generations named by
   `scrape_publication_state.current_publication_id`,
   `previous_publication_id`, and `working_publication_id`.

The stage fails closed if a named publication does not resolve, a named
snapshot source is null/nonpositive, the protected set is empty, or any
protected ID is absent from that instrument. It does not retain an arbitrary
number of recent completed scrapes and does not protect source maps belonging
only to unnamed historical publication generations.

## Required live gates

Every stage rechecks the fixed host and database identity. Do not continue if
any gate fails.

- PostgreSQL is the exact `fst-postgres` container in project
  `festivalservicetracker`, from the production Compose working directory.
- PostgreSQL 17 is healthy and its `data_directory` is inside the single
  read-write `/var/lib/postgresql/data` bind mount beneath
  `/mnt/docker-storage`.
- Container ID/image, PGDATA source/device, database OID, system identifier,
  and top-parent OID still match the `check` report.
- `fstworker` is stopped and durable worker status is offline/stopped/idle.
- No scrape or scrape phase is running.
- Public reads are unfrozen, current and previous publication IDs exist,
  working publication is null, and publication/max-score mutation intents are
  empty.
- There are no waiting locks, worker/scrape backends, competing maintenance
  backends, or locks on the top parent/current target.
- The parent is exactly `LIST (instrument)` in `pg_default`; the selected
  partition is attached with its fixed bound.
- No `sgm_*` artifact from another instrument exists. A rollback candidate
  intentionally blocks starting another instrument until it is reconciled.
- Representative `/api/songs` and `/api/rankings/overview` body/header
  fingerprints remain exact; readiness/service-info routes remain HTTP 200
  with the same content type.

The initial production `check` also requires a clean repository checkout. The
workspace marker binds the commit and SHA-256 of the migration/drill entry
points, so changing code requires a new workspace.

## Archive and storage rules

Before any source drop, the selected original is streamed to a PostgreSQL
custom archive on `/dev/nvme2n1p2`. The archive package contains:

- the exact parent and selected instrument only;
- archive SHA-256 and byte count;
- `pg_restore -l` TOC with the selected table, table data, primary key, and
  score index;
- the source catalog;
- source OID/relfilenode/heap/index/total bytes and insert/update/delete
  counters before and after the stream;
- the protected fingerprint and publication/database identity.

The before/after fence must be unchanged. The archive is then restored into a
deterministic, run-owned, `--network none` PostgreSQL 17 container. The restore
must prove the complete snapshot-ID distribution, whole-archive fingerprint,
protected distribution, source catalog, cleanup of transient PGDATA, container
removal, and continued archive checksum.

The archive survives `drop`. Deletion is a separate retention decision outside
this migration. Never count retained archives as reclaimable bytes.

## Replacement shape

`build` runs only after the archive restore proof and uses `pg_default`
directly. For the derived protected set, it creates:

```text
<instrument partition> PARTITION BY LIST (snapshot_id)
├── <instrument partition>_s<protected ID>
├── ... one child for every and only protected ID
└── <instrument partition>_default   (empty)
```

The root has a partitioned primary key on
`(snapshot_id, song_id, instrument, account_id)` and a partitioned score index
on `(snapshot_id, song_id, instrument, score DESC)`. The fixed instrument
check allows the short-lock top-parent attach to avoid rescanning retained
data. Validation requires both root indexes to attach to the corresponding
top-parent indexes and every root/child relation to resolve to `pg_default`.

Before detaching the original, `swap` adds and validates a run-owned exact
instrument check while the source is still attached to its known instrument
bound. The detached original retains that check, so rename-back rollback can
reattach without a full-table validation scan. Rollback removes the temporary
check only after the original is attached again.

Check validation runs during `build`, outside the short swap transaction, with
the configured long build timeout. `swap` only reverifies the already-validated
constraint before taking the top-parent lock.

Only protected rows are copied. The original remains attached throughout the
long copy/index work.

## Recurring retention ownership

The subpartition layout prevents future reclaim from requiring another
instrument-wide rewrite, but it does not prevent growth unless obsolete
children are actively retired.

A follow-up retention package must:

1. derive protected IDs from current/previous/working publication source maps,
   active snapshot state, and current projection sources;
2. inventory exact generation children and require the default child to remain
   empty;
3. create a custom archive for each nonempty unprotected child, including its
   heap/index/catalog/checksum and source ownership;
4. restore-prove that archive in isolated PostgreSQL before any live drop;
5. drop one exact child under short lock/statement timeouts, no `CASCADE`, and
   verify parent/index/publication/API parity plus returned filesystem bytes;
6. retain the archive until a separate product-retention decision.

Empty generation children may be dropped without a data archive only after
their exact zero-row and zero-reference state is recorded. No numeric
“latest-two” rule is sufficient because snapshot reuse can keep older physical
IDs behind current or previous publications.

## Capacity and emergency cancellation

Scratch preflight budgets 1.10 times source bytes for the custom archive, 1.25
times source bytes plus 10 GiB for isolated restore PGDATA, and a fixed
20 GiB scratch reserve.

The 4 TB build model uses the accepted pro-bass live profile with fixed
conservative margins:

- replacement: 1.50 times proportional retained source bytes, minimum 64 MiB;
- WAL: 1.50 times replacement, minimum 512 MiB;
- temp: 0.75 times replacement;
- failure reserve: one replacement;
- emergency 4 TB floor: `60,392,999,803` bytes.

Only one instrument is built at a time. A filesystem monitor samples through
archive, restore, and build. Crossing the scratch reserve or the 4 TB floor
writes `reports/emergency-floor-breach.json`, cancels/terminates only the
migration application backends (or stops the owned restore container), and
durably blocks that workspace. Do not delete or edit breach evidence; reconcile
PostgreSQL/WAL/storage and start a new run.

## Stages

| Stage | Mutation | Result |
|---|---|---|
| `check` | none | Claims the empty workspace, captures host/database/publication/API identity, and classifies the fixed source. |
| `plan` | none | Derives exact protected IDs, protected fingerprints/reference parity, source catalog/fence, and archive capacity. |
| `archive` | scratch only | Writes custom archive, checksum, TOC, catalog, and unchanged source fence. |
| `restore` | isolated scratch only | Restores in network-none PostgreSQL 17, validates all archived rows/catalog, then removes transient PGDATA/container. |
| `build` | creates detached 4 TB candidate | Copies only protected rows into exact generation children plus empty default; builds compatible partitioned indexes. |
| `swap` | short-lock DDL | Validates the original instrument check, detaches/renames the original, attaches the candidate, and writes committed-swap evidence with the real duration. |
| `validate` | none | Proves retained fingerprints, references, publication/API parity, child/index catalog, archive, and `pg_default`. |
| `rollback` | short-lock DDL | Accepts committed-swap evidence even if the terminal swap report tore, reattaches the checked original without a full scan, removes its temporary check, and retains the failed candidate. |
| `drop` | destructive DDL | Requires accepted validation and restore-proof archive, drops only the detached original, normalizes index names, and revalidates `pg_default`/API/archive. |

No stage uses `CASCADE`.

## Operator sequence

Create one new empty workspace per instrument on the authorized scratch
filesystem. The run ID and path below are examples; substitute the fixed key
being processed and a unique timestamp.

```bash
cd /home/sfenton/FortniteFestivalLeaderboardScraper

instrument=solo-guitar
run_id="snapshot-generation-${instrument}-20260818T120000Z"
scratch="/home/sfenton/fst-temporary/${run_id}"
mkdir -m 700 "$scratch"
device_id="$(findmnt -T "$scratch" -n -o MAJ:MIN)"

common=(
  --instrument "$instrument"
  --scratch-root "$scratch"
  --expected-device-id "$device_id"
  --run-id "$run_id"
)

tools/postgres-snapshot-generation-migration.sh \
  check "${common[@]}" \
  --claim-workspace \
  --api-base "<service-base-url>"

tools/postgres-snapshot-generation-migration.sh plan "${common[@]}"
tools/postgres-snapshot-generation-migration.sh \
  archive "${common[@]}" --execute
tools/postgres-snapshot-generation-migration.sh \
  restore "${common[@]}" --execute
tools/postgres-snapshot-generation-migration.sh \
  build "${common[@]}" --execute
tools/postgres-snapshot-generation-migration.sh \
  swap "${common[@]}" --execute
tools/postgres-snapshot-generation-migration.sh \
  validate "${common[@]}" --api-base "<service-base-url>"
```

At this point choose exactly one path.

Accepted finalization:

```bash
tools/postgres-snapshot-generation-migration.sh \
  drop "${common[@]}" \
  --execute \
  --api-base "<service-base-url>"
```

Rename-back rollback before `drop`:

```bash
tools/postgres-snapshot-generation-migration.sh \
  rollback "${common[@]}" \
  --execute \
  --api-base "<service-base-url>"
```

Do not start another instrument until the current target has either completed
`drop` with no migration artifacts or rollback artifacts have been separately
reconciled. Recheck 4 TB and scratch capacity from the next target's reports;
do not extrapolate the previous instrument.

## Resumption and evidence handling

Each success report is typed, dependency-checksummed, integrity-hashed, written
atomically in the workspace filesystem, and fsynced with its directory.
Archive/build start evidence is durable before long work begins.

If a final stage report is zero-length or malformed after a process
interruption, the next invocation moves it to `recovered-evidence/`, records a
recovery proof, inspects the archive/database state, and reconstructs the
report only when the committed state is exact. Complete restore validation and
cleanup evidence is reused; a partial restore evidence set is preserved before
the isolated restore is repeated. Valid JSON with a failed integrity hash is
not treated as a torn write and blocks automatically.

`swap.committed.json` is separate from the terminal stage report. It is written
immediately after the DDL transaction with the actual elapsed time and
duration-bound decision. A catalog-swapped state without measured committed
evidence is rollback-only; rerunning `swap` cannot replace the lost duration
with a near-zero idempotent measurement.

PostgreSQL statistics counters are not durable identity: crash recovery can
reset them. Stable rollback identity uses OID/relfilenode and physical sizes.
If mutation counters differ from the plan, rollback recomputes the complete
original fingerprint and per-snapshot distribution and requires exact equality
with the isolated restore report before reattachment.

Never edit reports, manifests, source fences, checksums, or workspace markers.

## Validation package

Structural tests:

```bash
bash -n \
  tools/postgres-snapshot-generation-migration.sh \
  tools/postgres-snapshot-generation-migration-drill.sh

PYTHONDONTWRITEBYTECODE=1 \
  python3 tools/postgres-snapshot-generation-migration.test.py
```

Isolated PostgreSQL 17 lifecycle:

```bash
PYTHONDONTWRITEBYTECODE=1 \
  python3 tools/postgres-snapshot-generation-migration-drill.py \
  --work-root \
  artifacts/snapshot-generation-migration-drills/<new-empty-run>
```

The drill runs independent rollback and final-drop lanes. It proves custom
archive/TOC, network-none restore and cleanup, exact protected children plus an
empty default, top-parent index attachment, rename-back rollback, final drop,
archive retention, and torn success-report recovery for archive, restore,
build, swap, validate, and drop. The rollback lane force-kills/restarts
PostgreSQL, resets cumulative statistics, and requires complete archive
fingerprint/distribution reproof before the original can be reattached.

The implementation validation on 2026-08-18 used 1,400 synthetic source rows
per lane (`1301` purge; `1302-1303` retained), PostgreSQL `postgres:17`, and
completed both lanes without a production connection or mutation. Synthetic
sizes and timings are correctness evidence only, not production performance
estimates.

`SnapshotGenerationPartitionTests` additionally proves that dropping one
generation child removes only that snapshot, leaves another generation
readable through the unchanged top parent, keeps the default child empty, and
preserves both remaining leaf-index attachments. This is layout evidence only;
it is not the recurring archive/drop owner described above.
