---
status: living-runbook
owner: data
last_verified: 2026-08-27
last_verified_commit: c35b7f47
sources:
  - FSTService/Persistence/Maintenance/DatabaseMaintenanceDryRunReporter.cs
  - tools/postgres-pro-bass-snapshot-rewrite.py
  - tools/postgres-pro-bass-snapshot-rewrite.sh
  - tools/postgres-pro-bass-snapshot-rewrite-drill.py
  - tools/postgres-pro-bass-snapshot-rewrite.test.py
  - docs/operations/live-safety.md
  - docs/database/SnapshotGenerationRetentionSafety.md
update_triggers:
  - Pro-bass snapshot ownership, archive, rewrite, capacity, swap, validation, rollback, drop, or archive-retention behavior changes.
---

# Pro-bass snapshot archive and rewrite pilot

## Current decision

The pro-bass transition was accepted live on 2026-08-18 from commit
`4e2bcdc4`. The final attached relation is in `pg_default`, contains only
snapshot IDs `1301` and `1302`, and had exact retained fingerprint parity.
The original and scratch rollback relations were dropped, the temporary
tablespace and Compose mount were removed, and the verified archive remains.
Guarded validation scrape `1303` then published successfully. The relation now
contains `1301-1303`; source maps reuse 350 pro-bass scopes / 1,436,731 rows
from `1302` and write 352 scopes / 1,910,331 rows for `1303`. Snapshot `1301`
was physically obsolete for that pilot, but no pilot conclusion is automatic
recurring-retention eligibility. The default-off generation observer is
report-only; archive/drop remains behind its evidence and live-safety gates.

The exact target is fixed in code:

- parent: `public.leaderboard_entries_snapshot`;
- partition: `public.leaderboard_entries_snapshot_pro_bass`;
- bound: `Solo_PeripheralBass`.

There is no table, relation, instrument, or arbitrary-SQL argument. The package
must not be generalized during a live window.

The final PostgreSQL 17 drill used 180,000 rows: 120,000 archive/purge rows
and 60,000 retained rows. It proved custom-archive restore, exact catalog and
content-distribution restore, retained-only scratch build, short-lock swaps,
validation, pre-drop `pg_default` repatriation, rename-back rollback,
zero-length/truncated internal-evidence recovery, and separate final drop.

| Measurement | Bytes |
|---|---:|
| Original relation | 75,415,552 |
| Replacement relation | 19,636,224 |
| Scratch build WAL | 20,423,824 |
| Build temp | 8,429,568 |
| Observed scratch-build peak | 19,689,472 |
| Custom archive | 3,340,833 |
| Restore workspace peak | 156,650,009 |
| Dropped original plus scratch rollback | 95,051,776 |
| Immediate filesystem return | 95,043,584 |

The swap took `0.047-0.066s` in the scaled drill. That is isolated evidence,
not a production lock-duration promise.

The rollback lane also removed each archive/build/swap/rollback success report
after the database or archive action completed, then reran the stage. Every
stage recognized and verified its already-committed state and produced a
truthful resumed report; the first reports remain checksummed as interruption
evidence.

Current evidence is under
`artifacts/pro-bass-pilot-drills/torn-evidence-final-20260818T013758Z/`.
Key SHA-256 values are:

- `drill-summary.json`:
  `1fe949a305a5957a005b03b7d6405e0948286102e0d6e71f1284a4bf5cbc2a74`;
- `measured-profile.json`:
  `244aebd878e6b41af20ec22d9e21f57ccbba953dabadf5d6af255f71643f7111`;
- `cleanup-proof.json`:
  `4330cb633db88498e16f95da00cf71ef1cf1b9b92666faf9f28b591d75a284f7`.
- `verified-live-archive-input.json`:
  `483cf15e12df3f0fcda370f6fc5ee969b450b8c4f1eeb2c291f7ec2201326c15`.

The verified archive contains `308,536,699` rows across 125 snapshot IDs
(`769-1302`). Applying the measured ratios to the exact row ratio and live
heap/index bytes estimates a `2,685,343,018`-byte replacement and requires
`69,713,820,289` free bytes after WAL, temp, failure reserve, and the
`60,392,999,803`-byte emergency floor. At the current
`68,545,114,112` free bytes, the direct-build shortfall is
`1,168,706,177` bytes. The older approximately `3.4 GB` retained-size
sensitivity remains a conservative `72.19-73.06 GB` requirement.

The temporary-tablespace model keeps replacement/temp/failure bytes on 8 TB
scratch and budgets only WAL plus the emergency floor on 4 TB. It projects a
4 TB requirement of `63,889,690,620` bytes, leaving `2,110,309,380` bytes at
the original `66 GB` assumption, and a scratch requirement of
`17,260,886,072` bytes. Current scratch-build margin is `4,655,423,492`.
Capacity therefore passes narrowly for that candidate mode, but production
build/swap originally depended on the complete sequence: copying the measured
replacement back to `pg_default` while the original rollback relation still
exists requires `66,575,033,638` bytes. After relocating 17 unreferenced
evidence directories to temporary 8 TB staging, observed 4 TB free space is
`68,545,114,112`, leaving `1,970,080,474` bytes of projected repatriation
margin. The live run passed those gates; this calculation remains the
pre-execution record.

### Accepted live result

Evidence root:
`/home/sfenton/fst-temporary/pro-bass-live-20260818T0205Z-v2/`.
`execution-summary.json` SHA-256 is
`613f081cf027d72290905fde68c962ec518a4ab6095f3eed734337aaa771f82a`.

- source: `308,536,699` rows / `150,098,894,848` bytes;
- retained: `6,691,993` rows across snapshot IDs `1301-1302`;
- purged from hot storage: `301,844,706` rows;
- scratch build: `20.696s`, `2,811,355,136` bytes,
  `2,868,112,424` WAL bytes, `1,341,054,976` temp bytes, and only `4,096`
  peak FST bytes;
- first swap: `0.052s`; repatriation build: `24.731s`; second swap:
  `0.034s`;
- final drop: `1.224s`, with `152,985,165,824` immediate filesystem bytes
  returned;
- final relation: `1,454,727,168` heap bytes, `1,356,242,944` index bytes,
  `2,811,404,288` total bytes;
- FST free after cleanup: `221,605,724,160` bytes;
- publication stayed at scrape `1302`, unfrozen, with exact songs/overview
  body and ETag parity and zero waiting locks.

### Validation scrape 1303

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/pro-bass-validation-scrape-20260818T0235Z/`.
`decision-summary.json` SHA-256 is
`9f7edfb0f1ddea85980fe6e93ba34a28546a686fe6d2a23068f22644f3661145`.

- network phase: `4,806.752s`, `+2.04%` versus scrape 1302, 62 recovered
  timeouts, zero HTTP 429/403/503, retry amplification `1.031`;
- snapshot reuse: 1,717 scopes and 6,112,541 rows globally; pro bass reused
  `49.86%` of scopes and `42.93%` of rows;
- pro-bass physical growth: `1,000,898,560` bytes;
- complete scrape-to-publication wall clock: `26,224.892s`;
- BandMaintenance: `12,796.067s`, including `11,002.075s` current projection;
- rankings: `5,295.526s`;
- publication succeeded on attempt 3 after two deferred-ready handoffs; commit
  `916a727b` fixes that release sequencing;
- publication `1303` is current, `1302` previous, reads are unfrozen, public
  API fingerprints pass, and the worker is offline.

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
not remain hot forever. Modern rows require terminal `scrape_log` ownership.
Legacy IDs with missing or failed ownership may also be archive candidates only
when the verified archive proves the exact unchanged source fence, exact ID,
row count and content hashes, and the ID has no named current/previous/working
source map. Without that archive proof, those ownership caveats remain blocked.

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

- enumerates snapshot IDs with recursive `MIN(snapshot_id)` probes on the
  leading primary-key column rather than scanning historical rows;
- joins only those IDs to scrape/source-map ownership metadata;
- calculates exact counts/ranges/fingerprints only for protected IDs;
- takes exact total rows and snapshot-ID bounds from the checksum-verified live
  archive restore input;
- disables parallel gather for the plan session;
- uses `work_mem=64MB`;
- enforces `temp_file_limit=256MB`.

Any future query-shape regression must therefore fail at the bounded temp limit
instead of consuming the emergency scrape reserve. Do not use the original
`05bf8d1f` planning query for production work.

### Live archive and restore evidence

The source partition was streamed read-only to
`/home/sfenton/fst-temporary/pro-bass-archive-20260817T223105Z/`:

- archive: `11,942,257,904` bytes;
- SHA-256:
  `3decc75ffe33e24dad72e379fb874c7b0c7b4a421121de6a227acd0fe344760f`;
- source before/after: same OID/relfilenode, `150,098,894,848` bytes,
  `40,139,479` inserts, zero updates/deletes;
- source changed during archive: false.

The first isolated restore attempted to apply the parent primary/score indexes
before the archived child indexes. PostgreSQL auto-created child indexes at
partition attach, then correctly rejected the duplicate child primary key.
The source archive remained unchanged and production was not mutated.

The successful retry restored the child table, data, primary key, and score
index while detached, then attached the complete child to the indexed parent.
Bounded validation proved:

- exact row count `308,536,699`;
- exact per-snapshot row counts and record-content XOR/sum fingerprints for
  all 125 snapshot IDs, minimum `769`, maximum `1302`;
- score range `0-748,234`;
- exact parent definition, all 23 columns/defaults/nullability values, owner,
  tablespace, partition bound, primary constraint, and primary/score indexes;
- restored heap `67,192,225,792` bytes;
- restored indexes `62,457,389,056` bytes;
- restored total `129,666,588,672` bytes;
- archive checksum exact and zero validation temp bytes.

The smaller restored index footprint is restore/compaction evidence, not
authorization to rewrite production. After validation the isolated container
and `130,771,858,177`-byte restore PGDATA were removed, returning
`130,773,172,224` filesystem bytes. The archive and checksummed validation/
cleanup reports remain. `restore-snapshot-distribution.json` SHA-256 is
`175755e468bd2eeb034898d6ca30971d4da629511de0aa012023a12af97bbe0a`;
`restore-catalog.json` SHA-256 is
`b4d9528f592592a37d2d1d89b8a4b6070e057b4bc677313131a3911b2ce9e3cf`;
`restore-validation.json` SHA-256 is
`024c7c714d8c602102eb6535db8d0a42b1cc8187403e88ec3b55ca2b163d71f2`;
`restore-cleanup-proof.json` SHA-256 is
`211f8056417c6d36bff81aa28fff3afac6a1a642358a2c11649f2f1544eed4a0`.

## Temporary 8 TB scratch exception

The operator has authorized one temporary workspace on
`/dev/nvme2n1p2`, mounted at `/`. This exception is only for:

- the PostgreSQL custom archive;
- archive catalog/list/manifest files;
- the isolated restore drill and its transient PGDATA;
- immutable stage reports and the measured capacity profile.

When the 4 TB direct-build gate fails but the measured dual-filesystem gate
passes, the replacement may be built in one run-owned temporary tablespace
backed by `<scratch-root>/postgres-tablespace` and mounted into PostgreSQL at
`/fst-pro-bass-scratch`. It is never an accepted final location. While the
original rollback relation still exists, `repatriate` must copy the retained
relation to `pg_default`, perform another short rollback-safe swap, and prove
fingerprint/reference/catalog/API parity while both original and scratch
rollback relations remain. Final `drop` removes both rollback relations,
normalizes names, and removes the temporary tablespace atomically enough to
preserve rollback until its destructive transaction begins.

The explicit `--scratch-root` must be operator-created. Before `check`, it may
contain only one empty real `postgres-tablespace/` directory so the
production-owned Compose override can mount it. `check` runs only after the
PostgreSQL recreate and binds the new container identity plus exact read-write
mount.
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
When the verified live archive input is supplied, `archive` and `drill`
revalidate/adopt the existing checksum, restore and cleanup evidence instead
of streaming or restoring production data again.

| Stage | Purpose | Mutation |
|---|---|---|
| `check` | Claim empty scratch; bind repository, device, Compose, container, cluster, publication, worker, target OIDs, and API baseline | Scratch marker/reports only |
| `plan` | Exact protected/purge IDs, ownership, counts, ranges, fingerprints, references, catalog, and capacity inputs | None |
| `archive` | Stream `pg_dump -Fc` directly from PostgreSQL to 8 TB scratch; record SHA-256, TOC, catalog, bytes, rows, IDs, and restore command | Scratch only |
| `drill` | Restore into a network-none PostgreSQL 17 container, verify exact bytes/catalog, then remove transient PGDATA | Isolated scratch only |
| `build` | Build retained-only heap, primary key, and score index in the guarded temporary tablespace when mounted, otherwise `pg_default` for isolated tests; measure WAL, temp and both filesystems | New candidate only |
| `swap` | Short transaction: detach original, retain it under a run-owned name, rename/attach replacement | Catalog only; original remains |
| `validate` | Exact candidate/original fingerprints, publication/source/projection/max-score/reference/API parity, archive checksum | None |
| `rollback` | Before final drop, detach candidate and rename/attach retained original; keep failed candidate and archive | Catalog only |
| `repatriate` | Before destructive drop, copy retained rows to logged `pg_default`, short-swap, retain original plus scratch rollback relations, and prove fingerprint/reference/catalog/API parity | New `pg_default` candidate only |
| `drop` | Only after accepted validation and durable repatriation evidence: drop original plus scratch rollback relations, remove the attach check, normalize names, remove the tablespace, and verify returned bytes | Destructive, separately gated |

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

The harness runs three independent paths against isolated PG17:

1. archive, restore, build, swap, validate, and rename-back rollback;
2. archive, restore, build, swap, validate, repatriate, and final drop;
3. committed repatriation with zero-length/truncated copy/swap evidence,
   catalog-driven scratch restoration, and original rollback.

It removes temporary source containers and PGDATA, preserves both archives and
reports, preserves first/resumed interruption reports, and emits
`drill-summary.json`, `measured-profile.json`, and `cleanup-proof.json`.
Production capacity planning accepts only a checksummed,
promotion-eligible profile with at least 100,000 total and 10,000 retained
synthetic rows.

## Production command sequence

Do not run these commands until the candidate branch is committed/pushed and
review-clean, the worker is held, and the exact live preflight is accepted.
The operator-directed local live validation occurs before PR merge; merge is
allowed only after that evidence is accepted.

Before `check`, the production-owned PostgreSQL service must expose exactly
`<scratch-root>/postgres-tablespace:/fst-pro-bass-scratch:rw`. Adding that bind
mount requires its own reviewed PostgreSQL container-recreate window and
readiness/public-health validation; this repository-only task does not change
the live Compose project.

Create a dedicated scratch directory with only an empty
`postgres-tablespace/` child on `/dev/nvme2n1p2`, apply the mount/recreate,
verify the public path, then record:

```bash
RUN_ID="pro-bass-<utc-run>"
SCRATCH_ROOT="<operator-created-empty-directory>"
DEVICE_ID="$(findmnt -T "$SCRATCH_ROOT" -n -o MAJ:MIN)"
API_BASE="<direct-fstservice-base-url>"
PROFILE="<accepted-measured-profile.json>"
PROFILE_SHA256="$(sha256sum "$PROFILE" | awk '{print $1}')"
ARCHIVE_INPUT="/home/sfenton/fst-temporary/pro-bass-archive-20260817T223105Z/verified-live-archive-input.json"
ARCHIVE_INPUT_SHA256="483cf15e12df3f0fcda370f6fc5ee969b450b8c4f1eeb2c291f7ec2201326c15"
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
  --run-id "$RUN_ID" \
  --verified-live-archive-input "$ARCHIVE_INPUT" \
  --expected-live-archive-input-sha256 "$ARCHIVE_INPUT_SHA256"

tools/postgres-pro-bass-snapshot-rewrite.sh archive \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID" \
  --verified-live-archive-input "$ARCHIVE_INPUT" \
  --expected-live-archive-input-sha256 "$ARCHIVE_INPUT_SHA256" \
  --execute

tools/postgres-pro-bass-snapshot-rewrite.sh drill \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID" \
  --verified-live-archive-input "$ARCHIVE_INPUT" \
  --expected-live-archive-input-sha256 "$ARCHIVE_INPUT_SHA256" \
  --execute

tools/postgres-pro-bass-snapshot-rewrite.sh build \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID" \
  --measured-profile "$PROFILE" \
  --expected-profile-sha256 "$PROFILE_SHA256" \
  --verified-live-archive-input "$ARCHIVE_INPUT" \
  --expected-live-archive-input-sha256 "$ARCHIVE_INPUT_SHA256" \
  --execute

tools/postgres-pro-bass-snapshot-rewrite.sh swap \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID" \
  --verified-live-archive-input "$ARCHIVE_INPUT" \
  --expected-live-archive-input-sha256 "$ARCHIVE_INPUT_SHA256" \
  --execute

tools/postgres-pro-bass-snapshot-rewrite.sh validate \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID" \
  --verified-live-archive-input "$ARCHIVE_INPUT" \
  --expected-live-archive-input-sha256 "$ARCHIVE_INPUT_SHA256" \
  --api-base "$API_BASE"
```

If validation fails and the old detached relation remains:

```bash
tools/postgres-pro-bass-snapshot-rewrite.sh rollback \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID" \
  --verified-live-archive-input "$ARCHIVE_INPUT" \
  --expected-live-archive-input-sha256 "$ARCHIVE_INPUT_SHA256" \
  --execute
```

Repatriation is a separate decision after reviewing validation and monitoring.
It must complete before final old-relation drop:

```bash
tools/postgres-pro-bass-snapshot-rewrite.sh repatriate \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID" \
  --verified-live-archive-input "$ARCHIVE_INPUT" \
  --expected-live-archive-input-sha256 "$ARCHIVE_INPUT_SHA256" \
  --api-base "$API_BASE" \
  --execute

tools/postgres-pro-bass-snapshot-rewrite.sh drop \
  --scratch-root "$SCRATCH_ROOT" \
  --expected-device-id "$DEVICE_ID" \
  --run-id "$RUN_ID" \
  --verified-live-archive-input "$ARCHIVE_INPUT" \
  --expected-live-archive-input-sha256 "$ARCHIVE_INPUT_SHA256" \
  --api-base "$API_BASE" \
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

For a direct 4 TB build, FST preflight requires:

```text
60,392,999,803 emergency floor
+ measured replacement heap and indexes
+ measured WAL
+ measured temp
+ one complete replacement-sized failure reserve
```

This candidate-specific model does not lower or replace the global `500 GiB`
retention policy. It only decides whether this exact premeasured pilot can run.

For a temporary tablespace build, the 4 TB gate retains the emergency floor
plus a conservative WAL budget; the 8 TB gate requires measured replacement
heap/indexes, temp, one replacement-sized failure reserve, and 10 GiB reserve.
`repatriate` uses the live scratch build's actual relation and WAL bytes and
must fit while the old rollback relation still exists. The current
`68,545,114,112` free bytes give `1,970,080,474` bytes of projected margin.
The workflow is incomplete while any accepted relation or tablespace remains
under the scratch root, and final old-relation drop cannot precede
repatriation.

## Interruption and rollback rules

- Critical reports and state markers use fsynced temporary inodes, atomic
  publication, and a directory fsync. Malformed copy/swap evidence triggers
  catalog-driven scratch restoration and a new-run requirement.
- Build writes immutable `build.started.json` capacity/free/WAL/temp/profile
  baselines before its one transaction. On resume, an existing replacement is
  accepted only when that baseline and its exact fingerprint match the retained
  plan; WAL/temp remain measurable while an interrupted filesystem-peak sample
  is labeled incomplete.
- Swap is one transaction. Resume detects either complete pre-swap or complete
  committed-swap catalog state; mixed state fails closed.
- Repatriation persists separate attempt, completed-copy, and bounded-swap
  evidence. Final drop verifies those checksums and rejects any breached or
  incomplete capacity evidence.
- A filesystem threshold breach writes a durable no-resume marker, repeatedly
  cancels exact pilot backends, escalates to termination, and succeeds only
  after no pilot backend remains.
- The attach-validating instrument check remains through repatriation parity
  and rollback; final drop removes it only with both rollback relations still
  validated.
- Original data remains in the detached relation until validation passes.
- Before `drop`, rollback is an exact rename-back/attach operation.
- After `drop`, rename-back rollback is impossible; recovery uses only the
  retained and restore-drilled archive.
- A lock timeout, publication drift, OID drift, checksum mismatch, API
  difference, missing source, or capacity failure is terminal for that attempt.
  Do not lengthen timeouts or retry blindly.

## Validation before any final drop

Require:

- unchanged original OID/relfilenode/sizes/DML fence;
- verified archive checksum, exact whole row count, exact sorted snapshot IDs,
  per-snapshot row counts/content XOR/sum fingerprints, and full canonical
  parent/column/default/nullability/owner/tablespace/constraint/index catalog;
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

Production build, both swaps, validation, repatriation, final drop, and mount
cleanup completed successfully. Rename-back rollback was not required and is
no longer available after final drop; recovery now uses the retained verified
archive. Validation scrape 1303 passed; durable generation retention and
broader instrument migration remain.
