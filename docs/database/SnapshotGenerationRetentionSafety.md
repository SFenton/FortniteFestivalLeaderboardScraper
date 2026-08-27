---
status: living-runbook
owner: data
last_verified: 2026-08-23
last_verified_commit: 4c36926a
sources:
  - FSTService/DatabaseMaintenanceOptions.cs
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionSchema.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionModels.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionRepository.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.cs
  - FSTService/Persistence/RegistrationDrainState.cs
  - FSTService/ScraperWorker.cs
  - FSTService.Tests/Unit/SnapshotGenerationRetentionPlannerTests.cs
  - FSTService.Tests/Unit/SnapshotGenerationRetentionSchemaTests.cs
  - FSTService.Tests/Unit/SnapshotGenerationRetentionAdmissionTests.cs
  - tools/postgres-snapshot-generation-retention-drill.py
  - tools/postgres-snapshot-generation-retention.test.py
  - tools/postgres-snapshot-generation-migration.py
  - docs/architecture/data-storage.md
  - docs/components/worker.md
  - docs/decisions/0006-snapshot-generation-subpartitions.md
  - docs/roadmap/data.md
  - docs/roadmap/post-scrape-processing.md
update_triggers:
  - Generation-leaf candidate selection, archive/restore, mailbox/prover, drop strategy, production ownership, canary, or retention gates change.
---

# Snapshot generation retention safety

## Status and boundary

The repository now contains a durable, default-off, report-only
snapshot-generation retention control plane. Normal database initialization
owns its additive schema, and the full worker may invoke its bounded planner at
a post-publication safe point only when the feature is enabled.

This phase does **not** implement archive creation, restore execution, child
detach/drop, sparse-child compaction, an executor container, a restore-prover
container, or any production Compose role. Report-only cycles and eligible
jobs are durably typed `report_only=true` and use the non-executable
`observed` status. `planned` is reserved for explicitly non-report-only cycles,
but no executor consumes it in this phase.

`tools/postgres-snapshot-generation-retention-drill.py` is a reusable,
isolated PostgreSQL 17 proof package for recurring whole-generation retention.
It proves the later executor/prover safety mechanisms, but it is not a
production retention executor.

The accepted drill:

- builds only synthetic data in uniquely named, network-none containers;
- accepts one of the migration package's exact nine instrument keys, never a
  database, container, relation, or SQL target;
- rejects `DOCKER_HOST`/`DOCKER_CONTEXT`, requires the explicitly expected
  active context, the exact local Unix-socket endpoint, a real socket, and the
  expected daemon ID before authorizing Docker mutation;
- pins every Docker Engine command, including measured-transaction
  `Popen` calls, to `unix:///var/run/docker.sock` after that authorization;
- resolves the requested local image only after authorization, requires its
  `sha256:` image ID, and uses only that ID plus `--pull=never` for every
  container create/run;
- revalidates the exact context, socket device/inode, daemon ID, requested
  image resolution, and image-ID presence immediately before acceptance;
- requires the fixed drive root to be the exact `/mnt/docker-storage` ext4
  mount, at least `3,500,000,000,000` bytes, with operator-supplied device ID
  and filesystem UUID;
- requires all PGDATA, archive, mailbox, proof, and report files beneath
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/`
  on the 4 TB FST drive;
- uses only a pre-existing local `postgres:17` image and never pulls;
- never mounts the Docker socket into PostgreSQL or the prover;
- gives every source, restore, TOC, cleanup, and mailbox-prover container an
  explicit run-owned PGDATA bind beneath the work root;
- removes owned containers with anonymous-volume cleanup as defense in depth,
  attempts every discovered container across repeated aggregate cleanup
  passes, removes every controlled PGDATA path, and requires an empty final
  owned inventory plus an unchanged exact Docker volume set;
- publishes the integrity-protected report and checksum manifest first,
  publishes `seal.json` strictly last, verifies the set, and only then makes
  the complete tree nonwritable; any publication, permission, symlink, or
  final-verification failure removes all success markers and writes
  `seal-failure.json`;
- does not touch production Compose, production PostgreSQL, live containers,
  GitHub, or the temporary 8 TB device.

Recurring execution, Compose roles, archive lifecycle ownership, canaries, and
promotion remain unimplemented and unauthorized.

## Implemented durable control plane

### Schema ownership and state

`DatabaseInitializer` applies
`SnapshotGenerationRetentionSchema.Sql` after the main publication schema in a
short transaction with the existing `2s` lock timeout, `15s` statement
timeout, and `20s` command bound. It creates no backfill and rewrites no
existing table.

The schema owns:

- `snapshot_generation_retention_cycles`: one idempotent row per
  `(post_publication, trigger_publication_id)`, with trigger scrape/publication,
  safe-point time, planner/config versions, typed `report_only` mode, plan
  digest, status, candidate and blocked counts/bytes, completion, and bounded
  error text;
- `snapshot_generation_retention_jobs`: fixed operation, instrument,
  root/child, snapshot ID, OID, relfilenode, partition bound, tablespace,
  typed cycle-matching `report_only` mode, catalog row estimate/bytes,
  protected/reference evidence, blocker codes/details, status, attempt fields,
  and future lease fields;
- `snapshot_generation_retention_evidence`: an append-only per-cycle sequence
  containing canonical JSONB plus previous/current SHA-256 hashes. A trigger
  rejects update, delete, and truncate.

Kinds, statuses, mode/status combinations, cycle/job mode identity, and all
nine instruments are constrained. The job schema reserves
`compact_sparse_child`, but this phase writes only `drop_whole_child`.
Report-only rows cannot be `planned`, leased, executing, or safety-failed, and
their future-executor fields remain empty. The executor index excludes
`observed` and all report-only rows. A partial unique index permits at most one
future non-report-only `leased`/`executing` destructive child globally. Only
future non-report-only `leased`, `executing`, or `safety_failed` state blocks
scrape allocation. A job-level or cycle-level `safety_failed` state also owns
the global destructive placeholder, so later non-report-only cycles can record
evidence but cannot plan another child until the failure is reconciled.
A second partial unique index permits only one nonterminal
`planned`/`leased`/`executing`/`safety_failed` intent for an exact
instrument/OID/relfilenode identity across all cycles. Later cycles record that
leaf as `deferred` with `existing_job_intent` and may plan another eligible
child instead of duplicating executor work.

### Exact planner

`SnapshotGenerationRetentionPlanner` first takes the exclusive registration
mutation session lock, then the global publication **shared** session lock,
then its exclusive planner session lock, all nonblocking. Only after all three
are held does it
begin the repeatable-read transaction and apply bounded PostgreSQL
lock/statement/idle-transaction timeouts. The registration-first order matches
exclusive max-score maintenance and fences registration/backfill creation;
publication allocation/commit cannot race or become invisible to the snapshot,
while ordinary API publication readers remain compatible and cannot starve
planning. Repeating the same publication safe point returns the existing cycle
instead of adding jobs or evidence.

Current, previous, and working pointers must be distinct. A non-null previous
slot must resolve to a `retained` generation, and the current generation's own
`previous_publication_id` must equal the singleton previous pointer. Any
duplicate or inconsistent predecessor state is durable blocked evidence, so an
older retained generation cannot be omitted from the protected set.

Discovery is limited to direct numeric generation leaves beneath the compiled
nine instrument roots. The planner verifies:

- the top parent is exactly `LIST (instrument)` and every compiled instrument
  root is exactly `LIST (snapshot_id)`, using `pg_partitioned_table` and
  `pg_get_partkeydef`;
- exact instrument roots, root bounds, one exact default child per root,
  direct leaf names/bounds, regular/partitioned relation kinds, and positive
  leaf OID/relfilenode identity;
- exactly the expected parent primary-key and `score DESC` index definitions,
  including uniqueness, key order/options, access method, expressions,
  predicates, INCLUDE shape, operator classes, and collations; every root/leaf
  index must be attached to and definition-equivalent to its parent;
- `pg_default` for the parent, roots, defaults, leaves, and indexes;
- an exact zero-row count in every default child before any leaf can be
  eligible.

Malformed or unexpected children are never candidates. Catalog/query failure
creates a failed cycle/evidence record when persistence remains available; it
is never translated into an eligible plan.

Before an executable plan can exist, the same transaction independently
revalidates that the requested scrape/publication is still exactly current,
the generation's `scrape_id` equals `published_scrape_id`, public reads are
unfrozen, the working pointer and publication commit-intent fields are null,
improvement notifications are either completed for that scrape or structurally
disabled, registration backfill/history queues are durably drained, and no
scrape is running. Any failed gate is persisted as blocked evidence and yields
no `planned` or `observed` job.

Protection is derived independently per instrument from:

- every active snapshot-state source;
- ready/building projection source IDs and any still-recorded source ID;
- physical `source_snapshot_id` rows belonging only to the publication
  generations named by current, previous, and working slots.

Named generations must resolve to a positive scrape identity and an allowed
slot status. Each resolved current/previous/working publication must have an
exact ready `solo_scope_sources` binding, an exact canonical
`publication_song_catalog`, and exactly the catalog song IDs multiplied by the
nine supported instruments at `alltime`. Missing, extra, duplicate,
wrong-scope, incomplete, or malformed rows and binding count/status/JSON
identity mismatches fail closed. Current rows must also match their complete
current `leaderboard_scope_fingerprints` evidence and published scrape
identity; retained previous maps are validated from their own catalog,
binding, key set, and stored content/coverage evidence rather than compared to
the mutable current fingerprint table.

Snapshot source rows require a positive matching physical source ID and
nonzero complete evidence. Authoritative empty rows are allowed only for
`alltime`, with null physical ID and exact zero row/reported-entry/page
evidence, plus a ready zero-row `snapshot` projection whose source ID equals
the finalized active-state ID. The planner fingerprints each named source map,
the active-state rows, the projection rows, and current fingerprint rows with
canonical SHA-256; those hashes are included in cycle evidence, every job's
reference evidence, and the plan digest.

Lifecycle generation pins and physical-required pins are distinct. Named
current/previous/working scrape IDs protect a leaf only when that leaf exists.
An all-unchanged or authoritative-empty generation may therefore have no leaf
without producing `protected_leaf_missing`. Nonempty publication sources and
other references with positive row evidence still require their exact
per-instrument physical leaf.

Each discovered leaf additionally remains blocked when it is:

- a current, previous, or working generation/source;
- active or a projection source;
- owned by a running scrape or configured `Scraper:ResumeScrapeId`;
- among the newest configured generation IDs for its instrument;
- missing a terminal `scrape_log` identity;
- younger than the configured minimum count of later successful
  publications;
- associated with an unreplayed `scrape_writer_failures` row when that default
  fence is enabled;
- under a root with a nonempty/unresolved default or any catalog/index/
  tablespace/topology blocker.

Eligible ordering is oldest snapshot first. Ties rotate deterministically by
instrument using the trigger publication ID, avoiding a permanent
same-instrument preference. Every discovered numeric leaf receives one
cycle-local job row. In report-only mode every eligible row is `observed`;
none is `planned`. In non-report-only mode only the bounded selected set is
`planned` and additional eligible rows are `deferred`. Every ineligible row
retains its blocker evidence.

### Safe-point timing and failure semantics

The normal worker does not use `DeferredRetentionMaintenanceRunner` for this
control plane.

- After notification recovery, the worker derives the safe point from the
  current publication rather than from an in-memory publication transition.
- On startup and before every scrape allocation, an enabled planner pauses
  background work, waits for quiescence, checks the shared durable
  registration-drain query, and makes one idempotent best-effort attempt for
  the current publication when it lacks a cycle.
- Run-once mode drains queued registration backfill/history work and invokes
  the same current-publication retry only when that drain is complete,
  immediately before exit.
- Continuous mode allows the normal inter-scrape registration interval, then
  attempts again at the next pre-allocation boundary while that publication
  remains current. Retention planning never delays normal scrape cadence; if a
  newer publication supersedes it first, the newer safe point rediscovers the
  same physical leaves while the old publication remains protected as
  `previous`.
- The shared drain counts every registered account with no backfill row or any
  non-complete backfill as outstanding. History is eligible for completion only
  after that account's backfill is complete. The planner repeats this check
  inside the repeatable-read snapshot while holding the exclusive registration
  mutation session lock.

Safe-point preparation, quiescence, drain reads, and planner invocation share
one exception-isolation boundary. Requested cancellation propagates; other
database/transient failures are logged and deferred so they cannot terminate
continuous service. Disabled planning returns before a drain query or database
write. A planner error is logged and, where PostgreSQL remains available,
persisted as a failed cycle with append-only evidence. It never rolls back the
already accepted publication, refreezes reads, or triggers an immediate scrape
retry.

Retryable terminal gates do not consume the safe point's unique cycle:
frozen reads, a working publication, commit intent, incomplete notifications,
incomplete registration drain, or a running scrape return `Deferred` with no
cycle/job rows. A later boundary re-evaluates it only while it remains current;
the planner does not promise a durable audit receipt for every superseded
publication. Only non-retryable structural/publication-identity failures become
durable blocked cycle/job evidence.

The worker's final registration-drain and publication-pointer reads use
cancellation-aware async commands with a 30-second command timeout. A blocked
metadata read therefore defers planning or responds to shutdown instead of
holding the next scrape boundary indefinitely.

### Configuration and rollback

All controls are backend `DatabaseMaintenance` options owned by the full
worker:

| Key | Default |
|---|---:|
| `SnapshotGenerationRetentionPlannerEnabled` | `false` |
| `SnapshotGenerationRetentionReportOnly` | `true` |
| `SnapshotGenerationRetentionNewestGenerationsToKeep` | `2` |
| `SnapshotGenerationRetentionMinimumLaterSuccessfulPublications` | `2` |
| `SnapshotGenerationRetentionMaxPlannedChildrenPerCycle` | `1` |
| `SnapshotGenerationRetentionBlockUnreplayedWriterFailures` | `true` |

Counts are clamped to bounded nonnegative values; the per-cycle planned limit
is at least one and applies only when `ReportOnly=false`. Rollback is
configuration-only: set
`SnapshotGenerationRetentionPlannerEnabled=false`. Existing cycle/job/evidence
rows remain immutable audit evidence and have no executor in this phase.

## Rejected forensic runs

These sealed packages remain unchanged for forensic review, but they are not
acceptance evidence:

| Run | Terminal hashes | Rejection |
|---|---|---|
| `snapshot-generation-retention-phase1-accepted-20260824T001500Z` | report `f4d2d7833dbeaff935411632a4af5a72ae61374984b5af72ad6a837fa3b0fb83`; checksums `6daa7a503fdd42df3f1ae0a5120d56e3e46927bb0a1294d7867758c107bc4275`; seal `fd5996b9667612af214a66152fc0d799f886f4e82b698ae070f9546985b13558` | Four mailbox-prover runs received anonymous PostgreSQL data volumes. |
| `snapshot-generation-retention-phase1-recheck-20260823T230630Z` | report `59f6e2a079ed68316345c099dc6b65e1518ad7878719afb57f37550363efb37b`; checksums `20621a928fbc0513dad87ae3af2db132b19e6ca890c8a6647c509b7fe09dba15`; seal `e65d12e199ca0b19d8f3e8c5327edbb7463492df50f49b24a75af7877beda71a` | Four mailbox-prover runs received anonymous PostgreSQL data volumes. |
| `snapshot-generation-retention-phase1-fixed-20260823T235436Z` | report `4e7f94834cdd722c5b93271a8c3ddca28ab1f98760a60f44fab6eab03474f720`; checksums `89736b5f9dedfb5fa8fb6019d02c5d70a50824ac5111c665abea1bb004bb21a4`; seal `662790ab941dd1eb30d5bfddbc6816dd68b726ecdf17ddd759e138753b59a709` | The zero-volume repair succeeded, but Docker Engine calls still depended on the mutable active context/tag, success artifacts were published before terminal permission verification, and cleanup could stop at the first container error. |
| `snapshot-generation-retention-phase1-finalcheck-20260824T000931Z` | report `fe9e6bceb1717f4a8084a8f501b5101267f25393762075b8ec860a2acca83606`; checksums `d0e7a46eba086d9d6090023484e69e0c3ce5575475ceb9f95e00b071b796b8fe`; seal `f91b00d447b671ec29194631ff18d3fb1a4dbd0b66ce6a037837c15c10471a89` | The zero-volume recheck succeeded, but the same Docker/image TOCTOU, premature success publication, and incomplete multi-container cleanup semantics remained. |

The first two runs leaked eight anonymous volumes in total onto the Docker
root, not the fixed 4 TB work root. The operator removed exactly those eight
unreferenced volumes. The later repaired-run preflight and independent
post-run inventories confirmed all eight are absent. All four old packages
remain unchanged, nonwritable, integrity-sealed forensic evidence only; their
names, `passed=true` reports, or seals do not override these rejections.

## Accepted isolated proof

The accepted run is:

```text
/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/
  snapshot-generation-retention-phase1-final-20260824T004250Z
```

Its terminal evidence is:

| Artifact | SHA-256 |
|---|---|
| `drill-report.json` | `24d9a29ee1464f26abcf9f2728de0914ca997aabc6e3e9611324537ed5e70838` |
| `checksums.json` | `aa6a96d5dd3beb511dfc164db710a65b9afeb1a1b072a17ec8a863b9124e88b1` |
| `seal.json` | `993219b170a58f7ee0e1687118fc740b63a2bfd33fbf8345b593849b324e54ac` |

The run completed in `15.964457` seconds and sealed 15 proof files. No file or
directory in the nonwritable, integrity-sealed package has a write bit, and
the tree contains no symlink. Its exact Docker volume inventory contained 304
names both before and after, with identical canonical-set SHA-256
`7e80164330a7fd5cec129ab43fa52f772600984fdad2ca6bf6987fad523bf8fb`;
the added and removed sets were both empty.

All 176 Docker Engine invocations were pinned to
`unix:///var/run/docker.sock`, including all 10 measured-transaction
`Popen` invocations. All nine create/run operations used `--pull=never` and
the resolved image ID
`sha256:5f050f770b427fbd477edee6c3968a72e5c6be97e050a7e368b2b74a9494a285`.
The initial and final context, endpoint, socket device/inode, daemon ID, tag
resolution, and image ID matched exactly. Both repeated terminal cleanup
passes found an empty owned-container inventory.
The focused retention suite passed `39/39`, including the five terminal
publication fault points and multi-container cleanup faults; the unchanged
migration regression passed `32/32`.

### Synthetic fixture

The fixture contains:

- top parent `leaderboard_entries_snapshot`;
- one allowlisted instrument root partitioned by `snapshot_id`;
- four generation leaves `1401-1404`;
- an empty default child;
- the canonical 23 snapshot columns, primary-key hierarchy, and score-index
  hierarchy;
- active state, current projection, publication generations, publication
  state, and named publication-source tables;
- non-null previous, current, and working publication slots;
- one unreferenced nonempty target leaf, `1401`.

The bounded row distribution was:

| Generation | Role | Rows |
|---:|---|---:|
| `1401` | Unreferenced retirement candidate | `40,000` |
| `1402` | Previous publication source | `12,000` |
| `1403` | Current publication and active source | `14,000` |
| `1404` | Working publication source | `16,000` |

The exact protected set was `1402-1404`; candidate selection returned only
`1401`; and the default child contained zero rows.

### Single-leaf archive and restore

The custom archive selects exactly:

1. the top snapshot parent;
2. the selected instrument root;
3. generation leaf `1401`;
4. the root's empty default child.

The TOC allowlist rejects another instrument root or data entry. The source
relation OID, relfilenode, heap/index/total sizes, and insert/update/delete
counters were fenced before and after streaming.

The accepted archive is `1,040,111` bytes with SHA-256
`da4e37b5e458f84f49cd11b45f9bf5bdb51f86f785833109f7ac733bc6ffaf15`.
The network-none PostgreSQL 17 restore:

- restored exactly `40,000` target rows;
- matched the deterministic whole-leaf fingerprint and per-generation
  distribution;
- matched columns, defaults/nullability, constraints, indexes, partition
  bounds, owners, and `pg_default` tablespaces for all four selected
  relations;
- retained an empty default child;
- used `78,187,033` bytes of transient PGDATA;
- completed in `4.770731` seconds;
- had no Docker socket, network dependency, or credential dependency;
- removed its container and PGDATA while retaining the checksummed archive.

### Filesystem mailbox and prover

Requests use a same-directory temporary file, file `fsync`, atomic rename, and
directory `fsync`. Each complete request binds:

- an unguessable request token;
- the exact archive basename and SHA-256;
- the exact source-fence digest.

The one-shot synthetic prover runs with:

- `--network none`;
- a read-only root filesystem;
- all capabilities dropped and `no-new-privileges`;
- request and archive mounts read-only;
- proof output mounted separately read-write;
- a unique read-write PGDATA bind beneath the fixed 4 TB work root, despite
  the Perl entry point not using PostgreSQL;
- no Docker socket.

The accepted evidence proves:

- a torn request without its final renamed file was ignored with exit `4` and
  produced no proof;
- a complete request with a false archive digest was rejected with exit `5`
  and an atomic integrity-protected rejection document;
- a torn proof temporary file was never accepted as terminal evidence;
- the complete request produced an atomic integrity-protected proof;
- a second one-shot prover resumed the existing proof without changing its
  checksum.

This is a filesystem contract demonstration only. It does not add production
executor or prover containers.

### Attached DROP and ordinary DETACH comparison

Both paths execute the exact full reference-parity JSON recheck and empty
default-child check inside the same transaction immediately before catalog
DDL. Neither path uses `CASCADE`.

The direct attached-child `DROP TABLE` was executed and rolled back so the
ordinary DETACH path could use the identical physical input. The DETACH path
added and validated `CHECK (snapshot_id = 1401)`, committed an ordinary
detach, reattached the leaf with the exact bound, proved exact parent-query
and leaf-fingerprint parity, detached it again, and dropped the detached
table. The final fixture retained only `1402-1404`, preserved exact references,
and left the default empty.

Measured timings include the short evidence-sampling interval while the
transaction deliberately waits on a run-owned advisory lock:

| Path | DDL ready | Database lock hold | Observation interval | Wall clock |
|---|---:|---:|---:|---:|
| Attached `DROP TABLE`, rolled back | `0.004602 s` | `0.161151 s` | `0.156549 s` | `0.206313 s` |
| Ordinary final `DETACH PARTITION` | `0.005151 s` | `0.150007 s` | `0.144856 s` | `0.204960 s` |

Both paths held `AccessExclusiveLock` plus the existing
`AccessShareLock` on the instrument root and `AccessShareLock` on the top
snapshot parent. This bounded synthetic measurement does not choose a
production strategy; both paths remain evidence for later adjudication.

`DETACH PARTITION ... CONCURRENTLY` is rejected:

- PostgreSQL reports that it cannot run inside the required exact-reference
  transaction block;
- PostgreSQL reports that it cannot detach concurrently while the instrument
  root has a default partition.

## Running the isolated drill

Prerequisites:

1. Docker is available.
2. `DOCKER_HOST` and `DOCKER_CONTEXT` are unset/empty.
3. The expected active Docker context resolves exactly to
   `unix:///var/run/docker.sock`, that path is a Unix socket, and the expected
   daemon ID has been captured from read-only
   `docker --host unix:///var/run/docker.sock info`.
4. `postgres:17` already exists locally.
5. `/mnt/docker-storage` is the exact local ext4 mount, is at least
   `3,500,000,000,000` bytes, and its expected device ID and UUID have been
   captured from read-only `findmnt`.
6. The run path is new or empty and is a real, nonsymlinked child of the fixed
   4 TB artifact root.
7. No production container, Compose project, database connection, credential,
   or alternate-drive path is supplied; the CLI has no such options.

Run:

```bash
python3 tools/postgres-snapshot-generation-retention-drill.py \
  --work-root \
  /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/\
autonomous-artifacts/<unique-run> \
  --expected-device-id <maj:min> \
  --expected-device-uuid <filesystem-uuid> \
  --expected-docker-context default \
  --expected-daemon-id <docker-info-id>
```

The default fixture contains `82,000` rows. Optional row-count flags are
bounded to `1,000-250,000` rows each and `500,000` total.

A successful run prints the report and seal paths and hashes. `seal.json` is
the only terminal success marker: a run is not accepted when that file is
absent, fails its embedded integrity digest, disagrees with the report or
checksum manifest, or coexists with `seal-failure.json`. Before accepting
evidence, require:

- `passed=true` in the integrity-protected report;
- the present terminal seal, report, and checksum-manifest hashes and embedded
  integrity digests to match;
- no `seal-failure.json`, success-document partial, extra manifest entry,
  symlink, or writable file/directory;
- exact archive/restore parity;
- mailbox torn/digest/resume proof;
- both measured catalog paths and reattach parity;
- every recorded Docker Engine invocation to use the exact
  `unix:///var/run/docker.sock` host, including measured `Popen` calls;
- every create/run to use `--pull=never` and the authorized `sha256:` image ID;
- exact initial/final context, socket device/inode, daemon ID, requested image
  resolution, and immutable image-ID presence;
- every container's `/var/lib/postgresql/data` mount recorded as a run-owned
  bind beneath the fixed 4 TB work root, with no volume-type mounts;
- exact before/after Docker volume sets and an empty added/removed delta;
- repeated cleanup-pass evidence, every transient per-container failure
  reported, and zero remaining run-labeled containers;
- no source or restore PGDATA directory;
- every integrity-sealed file and directory to be nonwritable.

Terminal publication validates the source tree and prepares all three
integrity documents before writing. It atomically publishes the report and
checksum manifest, publishes `seal.json` last, verifies the complete set, then
removes all write bits and verifies again. Any report, checksum, seal,
permission, symlink, or final-verification failure restores enough owner
permissions for cleanup, removes every success-named file and its partials,
fsyncs the directory, and emits integrity-protected `seal-failure.json`.

## Production gates still open

The isolated drill and durable report-only planner now cover evidence capture
and intent, not execution. Production Phase 1 still requires:

1. independent report-only parity against bounded SQL across at least two
   accepted publications, including the known six `1308` leaves without
   treating that measurement as authorization;
2. a separate no-Docker-socket executor and network-none PostgreSQL 17 restore
   prover using the durable jobs/evidence contract;
3. archive capacity/runway, retention, nonwritable/integrity-sealed storage,
   expiry, and restore ownership on the 4 TB drive;
4. executor lease/retry/abandon transitions, hard-safety state, operator stop
   controls, and crash/torn-state recovery while preserving one active child
   globally;
5. archive-only, smallest-child, and large-child canaries with public API,
   lock, CPU, memory, disk, WAL, and recovery monitoring;
6. a live-scrape A/B parity gate and explicit production promotion;
7. separately gated sparse-child compaction before claiming bounded
   steady-state storage.

## Current production evidence is not authorization

The first verified production inventory after publication `103` found six
wholly unreferenced failed-scrape `1308` leaves totaling
`12,908,355,584` bytes. The nine new `1310` leaves totaled
`15,870,648,320` bytes. These measurements establish candidate scale and
growth only. They do not authorize archive, detach, drop, scheduling, or a
retention policy.

The worker remains held until report-only parity is accepted and the separate
executor/prover lifecycle is implemented, tested, canaried, parity-gated, and
explicitly promoted.
