---
status: living-runbook
owner: data
last_verified: 2026-08-17
last_verified_commit: c79d39f
sources:
  - FSTService/Persistence/Maintenance/DatabaseMaintenanceDryRunReporter.cs
  - tools/postgres-pro-bass-snapshot-rewrite.py
  - tools/postgres-pro-bass-snapshot-rewrite.sh
  - tools/postgres-pro-bass-snapshot-rewrite-drill.py
  - tools/postgres-pro-bass-snapshot-rewrite.test.py
  - docs/operations/live-safety.md
update_triggers:
  - Pro-bass snapshot ownership, archive, rewrite, capacity, swap, validation, rollback, drop, or archive-retention behavior changes.
---

# Pro-bass snapshot archive and rewrite pilot

## Current decision

The repository package is a **candidate only**. It has not archived, rewritten,
swapped, or dropped the production relation.

The exact target is fixed in code:

- parent: `public.leaderboard_entries_snapshot`;
- partition: `public.leaderboard_entries_snapshot_pro_bass`;
- bound: `Solo_PeripheralBass`.

There is no table, relation, instrument, or arbitrary-SQL argument. The package
must not be generalized during a live window.

The final scaled PostgreSQL 17 drill used 360,000 rows: 300,000 archive/purge
rows and 60,000 retained rows. It proved custom-archive restore, exact catalog
restore, retained-only build, short-lock swap, validation, rename-back rollback,
and separate final drop. Measurements were:

| Measurement | Bytes |
|---|---:|
| Original relation | 144,318,464 |
| Replacement relation | 19,636,224 |
| Build WAL | 20,453,512 |
| Build temp | 8,421,376 |
| Observed build filesystem peak | 19,664,896 |
| Custom archive | 6,682,304 |
| Restore workspace peak | 264,653,337 |
| Dropped relation | 144,318,464 |
| Immediate filesystem return | 144,322,560 |

The swap took `0.047-0.066s` in the scaled drill. That is isolated evidence,
not a production lock-duration promise.

The rollback lane also removed each archive/build/swap/rollback success report
after the database or archive action completed, then reran the stage. Every
stage recognized and verified its already-committed state and produced a
truthful resumed report; the first reports remain checksummed as interruption
evidence.

Committed-code evidence is mirrored at
`/home/sfenton/FortniteFestivalLeaderboardScraper/artifacts/pro-bass-pilot-implementation-a66ff41b-20260817T2220Z/`.
Key SHA-256 values are:

- `drill-summary.json`:
  `e041c9eba1f65029577508667f28c47beddbd722296fecfdb0e02393a833bb72`;
- `measured-profile.json`:
  `740330f1f2bad5e3f2bc440421805554f3e19db27d71224ed6a96a6959fe92a0`;
- `live-capacity-projection.json`:
  `4a10a8c8a7564c03a20b620227d4ecd1ea0b403729c297f742aa6cf44f316c1f`;
- `cleanup-proof.json`:
  `90c14fb44a3a2c3230c27496182fb48ab65c653af6f96e70a440c6e21681a3f6`.

The measured ratios applied to the accepted approximately `3.4 GB` retained
relation estimate require about `72.19-73.06 GB` free, including the
`60,392,999,803`-byte emergency floor, WAL, temp, replacement, and one complete
replacement-sized failure reserve. At `66 GB` free, the projected shortfall is
about `6.19-7.06 GB`. **Production build/swap remains blocked until a fresh
exact plan passes the candidate-specific gate.**

## Physical-source retention policy

The retention policy protects:

1. `leaderboard_snapshot_state.active_snapshot_id`;
2. `solo_current_projection_scope.source_snapshot_id`;
3. rollback completed scrape IDs;
4. physical `source_snapshot_id` values referenced by
   `leaderboard_published_scope_source` for only the publication generations
   named by `scrape_publication_state.current_publication_id`,
   `previous_publication_id`, and `working_publication_id`.

Publication IDs are resolved through `publication_generations.scrape_id`
before joining `leaderboard_published_scope_source.published_scrape_id`.
Source-map rows belonging only to older, unnamed publication generations do
not remain hot forever. They are archive candidates only after their
`scrape_log` ownership is present and terminal.

Report-only planning remains statistics-based and fail-closed. It is not the
pilot execution plan. The pilot `plan` stage performs exact target-local ID,
row-count, range, ownership, reference, and content fingerprints.

### Production plan-query safety

The first live `plan` attempt from commit `05bf8d1f` was cancelled before it
completed. Its `GROUPING SETS` query hashed the full partition twice and
spilled temporary files on the 4 TB PostgreSQL filesystem, reducing free space
from about 66 GB to about 5 GB in roughly three minutes. The exact planning
backends were cancelled, PostgreSQL released the temporary files immediately,
free space returned to about 66 GB, public probes remained HTTP 200, and no
archive or database mutation occurred.

The corrected plan query:

- aggregates each snapshot ID once;
- derives the whole-partition fingerprint from those per-snapshot aggregates;
- disables parallel gather for the plan session;
- uses `work_mem=64MB`;
- enforces `temp_file_limit=256MB`.

Any future query-shape regression must therefore fail at the bounded temp limit
instead of consuming the emergency scrape reserve. Do not use the original
`05bf8d1f` planning query for production work.

## Temporary 8 TB scratch exception

The operator has authorized one temporary workspace on
`/dev/nvme2n1p2`, mounted at `/`. This exception is only for:

- the PostgreSQL custom archive;
- archive catalog/list/manifest files;
- the isolated restore drill and its transient PGDATA;
- immutable stage reports and the measured capacity profile.

The accepted replacement is always built in `pg_default`, on the 4 TB FST
PostgreSQL data filesystem. No live or accepted relation uses an 8 TB
tablespace.

The explicit `--scratch-root` must be an empty, operator-created directory.
`--expected-device-id` must equal `findmnt -T <root> -n -o MAJ:MIN`. The tool
rejects:

- `/`, `/tmp`, `/var/tmp`, `/mnt/docker-storage`, and `/var/lib/docker`;
- descendants of the FST storage or Docker roots;
- symbolic-link path components;
- NFS, CIFS, SMB, FUSE, 9P, Ceph, or Gluster filesystems;
- a different device or mount;
- unclaimed foreign content.

The workspace marker records device identity, repository commit, exact tool
source hash, run ID, temporary-only ownership, expiry, and that archive
deletion needs a separate operator decision. Production execution requires a
clean tracked worktree. Reports and archives use restrictive permissions.
Reports contain bounded account-identifier hashes rather than raw account IDs.
The restorable archive itself remains mode-restricted inside the claimed
workspace.

The archive remains on the 8 TB device through production acceptance. This
runbook does not authorize deleting it. A later retention decision must retain
the manifest/checksum and write cleanup proof outside the removed workspace.

## Stage model

Every successful stage writes one immutable
`<scratch-root>/reports/<stage>.json`. A failure writes a typed
`<stage>.failed-*.json`; it never writes a success-shaped partial report.
Completed stages are checksum-linked as dependencies and may be resumed.

| Stage | Purpose | Mutation |
|---|---|---|
| `check` | Claim empty scratch; bind repository, device, Compose, container, cluster, publication, worker, target OIDs, and API baseline | Scratch marker/reports only |
| `plan` | Exact protected/purge IDs, ownership, counts, ranges, fingerprints, references, catalog, and capacity inputs | None |
| `archive` | Stream `pg_dump -Fc` directly from PostgreSQL to 8 TB scratch; record SHA-256, TOC, catalog, bytes, rows, IDs, and restore command | Scratch only |
| `drill` | Restore into a network-none PostgreSQL 17 container, verify exact bytes/catalog, then remove transient PGDATA | Isolated scratch only |
| `build` | Build retained-only heap, primary key, and score index in `pg_default`; measure WAL, temp, relation and filesystem peak | New FST relation only |
| `swap` | Short transaction: detach original, retain it under a run-owned name, rename/attach replacement | Catalog only; original remains |
| `validate` | Exact candidate/original fingerprints, publication/source/projection/max-score/reference/API parity, archive checksum | None |
| `rollback` | Before final drop, detach candidate and rename/attach retained original; keep failed candidate and archive | Catalog only |
| `drop` | Only after accepted validation: drop exact detached original without `CASCADE`, normalize index names, verify catalog and returned bytes | Destructive, separately gated |

## Common guards

Every production stage rechecks:

- exact Compose project and working directory;
- clean tracked repository, exact commit, and unchanged tool-source hash;
- exact PostgreSQL container, database, user, system identifier, database OID,
  parent OID, and 4 TB data mount;
- `fstworker` stopped plus durable worker status offline/idle/stopped;
- no running scrape or phase attempt;
- no working publication and public reads unfrozen;
- current/previous publication fence unchanged;
- no worker/pilot backend, waiting lock, or target-relation lock.

Database-mutating transactions acquire the maintenance and publication
advisory locks. Swap, rollback, and drop use a `2s` lock timeout and `30s`
statement timeout. There is no `CASCADE`.

## Isolated drill

Use a new ignored repository artifact directory on the authorized root
filesystem:

```bash
mkdir -p artifacts/pro-bass-pilot-drills

tools/postgres-pro-bass-snapshot-rewrite-drill.sh \
  --work-root "$PWD/artifacts/pro-bass-pilot-drills/<utc-run>" \
  --image postgres:17 \
  --purge-rows 300000 \
  --retained-rows 30000
```

The harness runs two independent paths against isolated PG17:

1. archive, restore, build, swap, validate, and rename-back rollback;
2. archive, restore, build, swap, validate, and final drop.

It removes temporary source containers and PGDATA, preserves both archives and
reports, preserves first/resumed interruption reports, and emits
`drill-summary.json`, `measured-profile.json`, and `cleanup-proof.json`.
Production capacity planning accepts only a checksummed,
promotion-eligible profile with at least 100,000 total and 10,000 retained
synthetic rows.

## Production command sequence

Do not run these commands until the branch is merged, the live parity gate is
accepted, the worker is held, and the exact live preflight is approved.

Create an empty dedicated scratch directory on `/dev/nvme2n1p2`, then record:

```bash
RUN_ID="pro-bass-<utc-run>"
SCRATCH_ROOT="<operator-created-empty-directory>"
DEVICE_ID="$(findmnt -T "$SCRATCH_ROOT" -n -o MAJ:MIN)"
API_BASE="<direct-fstservice-base-url>"
PROFILE="<accepted-measured-profile.json>"
PROFILE_SHA256="$(sha256sum "$PROFILE" | awk '{print $1}')"
```

Run one stage at a time and review every report:

```bash
tools/postgres-pro-bass-snapshot-rewrite.sh check \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID" \
  --claim-workspace \
  --api-base "$API_BASE"

tools/postgres-pro-bass-snapshot-rewrite.sh plan \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID"

tools/postgres-pro-bass-snapshot-rewrite.sh archive \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID" \
  --execute

tools/postgres-pro-bass-snapshot-rewrite.sh drill \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID" \
  --execute

tools/postgres-pro-bass-snapshot-rewrite.sh build \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID" \
  --measured-profile "$PROFILE" \
  --expected-profile-sha256 "$PROFILE_SHA256" \
  --execute

tools/postgres-pro-bass-snapshot-rewrite.sh swap \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID" \
  --execute

tools/postgres-pro-bass-snapshot-rewrite.sh validate \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID" \
  --api-base "$API_BASE"
```

If validation fails and the old detached relation remains:

```bash
tools/postgres-pro-bass-snapshot-rewrite.sh rollback \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID" \
  --execute
```

Final drop is a separate decision after reviewing validation and monitoring:

```bash
tools/postgres-pro-bass-snapshot-rewrite.sh drop \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID" \
  --execute
```

## Capacity gates

Scratch preflight requires:

```text
source relation archive budget
+ source relation restore budget
+ 1 GiB restore-cluster base
+ max(25% of source relation, 10 GiB) reserve
```

For the accepted `150,098,894,848`-byte source, that is approximately
`338,796,255,232` bytes. The measured 8 TB free space is sufficient.

FST preflight requires:

```text
60,392,999,803 emergency floor
+ measured replacement heap and indexes
+ measured WAL
+ measured temp
+ one complete replacement-sized failure reserve
```

This candidate-specific model does not lower or replace the global `500 GiB`
retention policy. It only decides whether this exact premeasured pilot can run.

## Interruption and rollback rules

- Archive output is written as `.partial`, fsynced, then renamed. A partial
  archive is never accepted.
- Build writes immutable `build.started.json` capacity/free/WAL/temp/profile
  baselines before its one transaction. On resume, an existing replacement is
  accepted only when that baseline and its exact fingerprint match the retained
  plan; WAL/temp remain measurable while an interrupted filesystem-peak sample
  is labeled incomplete.
- Swap is one transaction. Resume detects either complete pre-swap or complete
  committed-swap catalog state; mixed state fails closed.
- Original data remains in the detached relation until validation passes.
- Before `drop`, rollback is an exact rename-back/attach operation.
- After `drop`, rename-back rollback is impossible; recovery uses only the
  retained and restore-drilled archive.
- A lock timeout, publication drift, OID drift, checksum mismatch, API
  difference, missing source, or capacity failure is terminal for that attempt.
  Do not lengthen timeouts or retry blindly.

## Validation before any final drop

Require:

- exact original whole-partition fingerprint;
- exact retained candidate fingerprint and row count;
- exact schema, columns, defaults, owner, tablespace, constraints, primary key,
  score index, parent key, and partition bound;
- no missing current/previous/working source ID;
- no missing active/projection row;
- unchanged publication, source-map count, protected max-score fingerprint,
  and reference count;
- healthy readiness/service-info status and content type, plus exact
  publication-route bytes, content type, ETag, and SHA-256 for songs and
  rankings overview;
- verified archive SHA-256 and successful isolated restore report;
- old relation detached and present;
- no public-health, lock, WAL/temp, or capacity gate failure.

No production archive, build, swap, rollback, or drop occurred while creating
this candidate package.
