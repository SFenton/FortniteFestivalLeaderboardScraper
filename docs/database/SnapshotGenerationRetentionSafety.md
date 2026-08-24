---
status: living-runbook
owner: data
last_verified: 2026-08-23
last_verified_commit: 4c36926a
sources:
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

`tools/postgres-snapshot-generation-retention-drill.py` is a reusable,
isolated PostgreSQL 17 proof package for recurring whole-generation retention.
It proves the safety mechanisms needed before production implementation, but
it is not a production retention executor.

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

Recurring production automation, Compose roles, durable retention jobs,
archive lifecycle ownership, canaries, and promotion remain unimplemented and
unauthorized.

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

The drill resolves the isolated mechanics only. Production Phase 1 still
requires:

1. durable cycle/job/attempt/evidence schema and idempotent state transitions;
2. a bounded worker safe-point intent after terminal publication,
   notifications, registration drain, and normal worker exit;
3. exact per-instrument protected-set and candidate fences against the live
   catalog, current/previous/working publication sources, active state, and
   projection state;
4. a separate no-Docker-socket executor and network-none restore prover,
   initially disabled and report-only;
5. archive capacity/runway, retention, nonwritable/integrity-sealed storage,
   expiry, and restore ownership on the 4 TB drive;
6. archive-only, smallest-child, and large-child canaries with public API,
   lock, CPU, memory, disk, WAL, and recovery monitoring;
7. one-active-child scheduling, crash recovery, operator stop controls, and
   safe retry/abandon semantics;
8. a live-scrape A/B parity gate and explicit production promotion;
9. separately gated sparse-child compaction before claiming bounded
   steady-state storage.

## Current production evidence is not authorization

The first verified production inventory after publication `103` found six
wholly unreferenced failed-scrape `1308` leaves totaling
`12,908,355,584` bytes. The nine new `1310` leaves totaled
`15,870,648,320` bytes. These measurements establish candidate scale and
growth only. They do not authorize archive, detach, drop, scheduling, or a
retention policy.

The worker remains held until recurring retention is implemented, tested,
canaried, parity-gated, and explicitly accepted.
