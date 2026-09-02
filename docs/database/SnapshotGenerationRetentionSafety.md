---
status: canonical
owner: data
last_verified: 2026-08-30
last_verified_commit: 21d7193c
sources:
  - FSTService/DatabaseMaintenanceOptions.cs
  - FSTService/appsettings.json
  - FSTService/Program.cs
  - FSTService/ScraperWorker.cs
  - FSTService/StartupInitializer.cs
  - FSTService/SnapshotGenerationRetentionSafePointQueue.cs
  - FSTService/Api/NotificationService.cs
  - FSTService/Api/PublicationReadContext.cs
  - FSTService/Api/PublicationReadiness.cs
  - FSTService/Api/PublicApiResponseCacheMiddleware.cs
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/PublicationGeneration.cs
  - FSTService/Persistence/MetaDatabase.cs
  - FSTService/Persistence/MetaDatabase.Publication.cs
  - FSTService/Persistence/GlobalLeaderboardPersistence.cs
  - FSTService/Persistence/Maintenance/ServiceMaintenanceLock.cs
  - FSTService/Persistence/Maintenance/DatabaseRetentionMaintenanceService.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionSchema.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionModels.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionRepository.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.Reads.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionOracle.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationQuarantineSchema.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationDropSchema.cs
  - FSTService.Tests/Unit/SnapshotGenerationRetentionSchemaTests.cs
  - FSTService.Tests/Unit/SnapshotGenerationRetentionPlannerTests.cs
  - FSTService.Tests/Unit/DatabaseRetentionMaintenanceServiceTests.cs
  - FSTService.Tests/Unit/DatabaseInitializerTests.cs
  - FSTService.Tests/Unit/MetaDatabaseTests.cs
  - FSTService.Tests/Unit/GlobalLeaderboardPersistenceTests.cs
  - FSTService.Tests/Unit/PublicationApiResponseCacheMiddlewareTests.cs
  - FSTService.Tests/Unit/PublicReadGateTests.cs
  - FSTService.Tests/Unit/NotificationServiceTests.cs
  - FSTService.Tests/Unit/ScraperWorkerStatefulTests.cs
  - FSTService.Tests/Unit/SnapshotGenerationRetentionSafePointQueueTests.cs
  - FSTService.Tests/Unit/SnapshotGenerationQuarantineSchemaTests.cs
  - FSTService.Tests/Unit/SnapshotGenerationQuarantineToolTests.cs
  - FSTService.Tests/Unit/SnapshotGenerationDropSchemaTests.cs
  - FSTService.Tests/Unit/SnapshotGenerationDropToolTests.cs
  - tools/postgres-snapshot-generation-archive.py
  - tools/postgres-snapshot-generation-archive.sh
  - tools/postgres-snapshot-generation-archive.test.py
  - tools/postgres-snapshot-generation-archive.test.sh
  - tools/postgres-snapshot-generation-archive-drill.py
  - tools/testdata/postgres-snapshot-generation-archive-csharp-fixture/
  - tools/testdata/postgres-snapshot-generation-archive-extra-volume.Dockerfile
  - tools/FstSnapshotGenerationQuarantine/
  - tools/postgres-snapshot-generation-quarantine.sh
  - tools/FstSnapshotGenerationDrop/
  - tools/postgres-snapshot-generation-drop.sh
  - tools/postgres-snapshot-generation-restore.py
  - tools/capture-snapshot-generation-drop-health.py
  - tools/postgres-snapshot-generation-drop-drill.py
  - docs/database/SnapshotGenerationDropRunbook.md
  - tools/capture-publication-route-contract.sh
update_triggers:
  - Snapshot-generation report planning, liveness roots, provenance TTL, maintenance locks, observation gates, or later archive/destructive tiers change.
---

# Snapshot generation retention safety

## Current capability

The service-owned automatic snapshot-generation pruning slice remains
**default-off and report-only**. It can observe exact physical children and
persist immutable evidence. It cannot archive, detach, rename, drop, truncate,
or delete a snapshot child.

This is a structural boundary, not an option convention:

- there is no retention job table;
- there is no operation-kind column;
- there are no planned, leased, executing, succeeded, or destructive states;
- there is no worker/executor API and no delete-trigger API;
- the repository exposes reads plus immutable cycle/observation/deferral
  persistence only;
- report rows require `report_only=true`;
- cycles, child observations, deferrals, and hash-chain evidence reject update,
  delete, and truncate.

The repository also contains a separate operator-facing archive-only tool.
It reads one immutable candidate selected from the newest accepted cycle and
creates a checksummed recovery package. It has no retention job, service
executor, source relation mutation, or arbitrary relation/SQL argument. Its
isolated `prove` subcommand restores only into a transient PostgreSQL 17
container with network mode `none` and no published ports.

An additional operator-only quarantine/reattach tier is implemented and has
completed its first live production canary. It is a separate .NET command, has
no Docker access, is not hosted by the service or worker, and has no `drop`,
`truncate`, row-delete, or automatic-retirement command. It can move exactly
one already archived and currently eligible numeric child into a private
quarantine schema, collect immutable soak evidence, and reattach that same
OID/relfilenode as rollback. Quarantine structurally identifies the exact
primary-key and score indexes, then renames them without rebuilding to
`sgqi_<full-operation-id>_pk` and
`sgqi_<full-operation-id>_score` before moving the table. The primary-key
constraint follows its index name. Immutable per-index evidence records the
old/new names, exact OID/relfilenode, role, structural semantics, phase, and
transaction identity. Reattach applies the same normalization as a
backward-compatible repair for pre-change operations.

A separate operator-only DROP and logical-restore tier is now implemented but
is not live-accepted or automatically invoked. DROP can target only a
currently detached child named by an authenticated quarantine operation. It
requires a distinct publication-rotation Q1 rehearsal, a fresh Q2 operation,
30 minutes and 60 successful health samples, unchanged latest
cycle/publication state, a fresh network-none proof, a sealed recovery bundle,
and separate approval. The active hold, durable DEFAULT fence, and committed
DROP tombstone independently prevent generation recreation. Logical restore
is a sibling tool and records the new physical OID/relfilenode while requiring
exact data and name-insensitive logical topology.

The legacy whole-instrument `SnapshotRetentionPolicy`/rewrite path remains
disabled and is not used as the generation-child deletion oracle.

## Live report-only acceptance

The archive-only development entry gate is satisfied. Five distinct terminal
production cycles exist on planner v3:

- cycle `5` observed scrape `1325` / publication `140`;
- cycle `6` observed scrape `1326` / publication `142`;
- cycle `7` observed scrape `1327`;
- cycle `8` observed scrape `1328`;
- cycle `9` observed scrape `1329` / publication `148`.

All five cycles had exact planner/oracle child, live, and candidate-set
agreement with zero blockers. The set includes publication rotation and a
genuine candidate-set change. Solo Bass snapshot `1308` remained protected by
its unreplayed writer failure throughout.

The first two cycles used planner v3 image
`sha256:d11a7d27c018efa160009533f88ad759b4b61ce8c3c79e1f27b8208b99386133`:

Both cycles had exact planner/oracle child, live, and candidate-set equality,
zero blocked children, the same 89-child candidate identity, and immutable
evidence chains with valid links. Publication rotation increased protected
children from 124 to 133 without changing the candidate set. Solo Bass scrape
`1308` remained protected by its unreplayed writer failure.

Both publications carried 6,390 complete v1 source bindings with key hash
`f94d0b6cc67b983bb36fb1778c91d2187d94bcd713b9f3ac97906c923413a1fe`.
Publication `142` also passed two same-publication 55-route captures with exact
status and normalized JSON parity. A direct 55-route capture was not taken
before publication `140` rotated out; exact-image route parity had already been
captured on publication `138`, and publication `142` reconfirmed the contract.

The checksummed acceptance bundle is stored on the FST drive at
`fst-data/evidence/snapshot-pruning-report-only-v3-candidate/acceptance-1325-1326/`.
This evidence authorizes a separate archive-only implementation and isolated
restore proof. It does not authorize detach, rename, drop, truncate, or delete.

Cycle `9` classified 99 candidate and 150 protected children with
`170,139,426,816` candidate bytes. Its 250-row evidence chain is contiguous,
and its planner/oracle child, live, and candidate sets are exactly equal. A
planner-only run persisted the cycle before deliberately rejecting completed
scrape `1329` as a resume target; no scrape `1330` was allocated during that
archive window.

Official confirmation scrape `1333` is also accepted. It completed with 710
songs, 41,154,968 entries, 608,691 requests, 92,821,715,390 bytes, zero
critical or best-effort phase failures, and zero writer failures. All
8,520/8,520 manifests across 12 instruments completed with no retry-exhausted
or failure reason. Publication `157` became current with 6,390 published solo
source bindings, first-attempt completed notifications, unfrozen reads, and no
working publication, commit intent, or max-score gate. Production continued
automatically into scrape `1334`.

Cycle `13`, triggered by scrape `1333` and publication `157`, is
observed/report-only with exact planner/oracle agreement: 111 candidates, 174
protected, zero blocked, 194,754,322,432 candidate bytes, exact child/live/
candidate sets, and zero global blockers. Solo Bass `1308` remains protected
by `unreplayed_writer_failure` with stable identity
`4e3310328261704da558e6d83f99cbc77bc01cef10abbac0840df471d33809cc`.
At the cycle-13 confirmation point, the true smallest candidate was Pro
Cymbals `1314` at 4,628,480 bytes. Cycle `15` correctly records it absent
while Q1 remains private; a new post-reattach cycle must establish the next
current candidate set.

## Archive-only package and proof

`tools/postgres-snapshot-generation-archive.sh archive` selects the
deterministically oldest candidate by
`(snapshot_id, instrument, child_oid)` from the newest immutable cycle; this
ordering is not a physical-size comparison. The separate drop tool's
`select-canary` command orders eligible nonempty children by current
`pg_total_relation_size` for a true smallest-child canary. An optional exact
archive `--instrument` plus `--snapshot-id` pair is accepted
only from the fixed nine-instrument allowlist and must resolve to a candidate
in that same newest cycle. Partial selection, relation names, and SQL text are
not accepted.

Before and after `pg_dump`, the tool rechecks the accepted cycle, current
publication, public-read state, notifications, running/resumable scrapes,
holds, unreplayed writer failures, PostgreSQL 17 identity, container/image and
PGDATA identity, same-drive capacity, exact root/parent/child attachments,
catalog configuration, stable observation hashes, mutation counters, row
count, and deterministic SHA-256 row fingerprint. Any drift removes the
partial archive acceptance files and leaves a `rejected.json` record.

The cycle check loads every latest-cycle observation and evidence row. It
requires exact planner version `3` and config version `1`, rebuilds the
physical child/live/candidate sets, recomputes stable child/config/metrics and
cycle candidate/observation hashes with `TierZeroCanonicalJson` rules, and
verifies the complete summary/child evidence sequence and SHA-256 linkage.
Stored planner/oracle sets and summary validation sets must agree exactly.
Canonical encoding matches `Utf8JsonWriter` and the default
`JavaScriptEncoder`, including `\u0022` for embedded quotes. Persisted blocker,
anomaly, evaluation, validation, and evidence ordering is retained exactly.
Summary publication/index validation arrays remain production record objects
for evidence hashing; only each record's exact `comparisonKey` is extracted
and sorted for planner/oracle agreement.

The custom archive contains only the top partitioned snapshot table, selected
instrument root, and selected numeric child. It uses strict names,
`--no-owner`, `--no-privileges`, compression, and a bounded dump lock wait.
The accepted package retains the archive, TOC, full source catalog,
`manifest.json`, and `SHA256SUMS`; it never retains a plaintext row export.
`SHA256SUMS` authenticates the actual `catalog.json`, and the quarantine
validator also requires `manifest.catalog.sha256` to equal that exact digest.
The immutable operation binds the manifest bytes, so a self-consistent
replacement checksum/proof set cannot substitute a false manifest-to-catalog
link.

Packages are accepted only below
`fst-data/evidence/snapshot-generation-archives`. Before package creation, the
tool resolves source PGDATA, every tablespace, every source-container mount,
and Docker root. Equal, ancestor, or descendant overlap is rejected with
path-relative and mount-source/FS-root identity checks, including bind aliases.
Nested mount boundaries beneath archive/proof roots reject. Archive and proof
share an exclusive reservation lock. Capacity uses the current physical
catalog/archive and is rechecked immediately before admission rather than
trusting planner byte estimates.

The lock is the pre-provisioned regular file
`fst-data/evidence/.snapshot-generation-archive-operation.lock`; public
commands open it read-only without `O_CREAT`. They validate and pin
archive-root/protected-source mount identity before lock acquisition, recheck
immediately after locking, and revalidate again before their first output
write. An unsafe archive-root alias receives no lock, rejection, or proof file.

`prove` verifies every package checksum before starting a transient
PostgreSQL 17 container. The package is mounted read-only, PGDATA is on the FST
drive, network mode is `none`, no ports are published, and CPU, memory, PID,
and shared-memory limits are explicit. Restore uses `pg_restore
--exit-on-error --no-owner --no-privileges`. The proof compares hierarchy,
partition bounds, columns/defaults/nullability, constraints, indexes, relation
options, access methods, tablespaces, expected restore ownership, exact row
count, and row SHA-256. It then removes the owned container and guarded scratch
PGDATA and retains a checksummed proof manifest. `--keep-proof-outputs` also
retains the detailed restored catalog and container evidence.

The proof forces `data_directory` to its exact owned read-write bind, rejects
anonymous or unexpected writable data mounts, and independently rechecks the
device and free-space reserve. Cleanup targets are discovered by unique
tool/proof/package labels even after an uncertain `docker run`; PGDATA is not
cleaned until container absence is proven. Every completed attempt writes
final cleanup evidence, and failures also write a checksummed rejection.
Source discovery resolves the immutable container ID; every source query and
dump uses that ID, with container/image/database/system/PGDATA/tablespace
provenance re-inspected at dump admission and after streaming.

Before creating `proofs`, a proof directory, marker, or PGDATA, the tool checks
the existing package/prospective parent mount identity, nested boundaries, and
all protected-source aliases. It rechecks parent mount identity immediately
before atomic proof-directory reservation. Proof and cleanup bind mounts use
Docker's structured `--mount` form.
Unexpected image-declared volumes are captured before `docker rm -f -v` and
must be absent before cleanup can be accepted.

Repository unit/static tests and the disposable network-none PostgreSQL 17
drill prove this mechanism without contacting `fst-postgres`. The drill rejects
a placeholder planner hash and compares the complete source row fingerprint
and logical catalog before/after. A C# fixture references the actual
FSTService record and canonical serializer types and emits multiple nonempty
record-shaped publication/index/numeric-child validations. The drill also rejects and cleans an image-declared extra volume.

### Accepted smallest-child live canary

The first live archive-only canary completed on cycle `9` without changing the
source database. The exact target was
`Solo_PeripheralCymbals` snapshot `1314`,
`public.leaderboard_entries_snapshot_pro_cymbals_s1314`:

- source OID and relfilenode: `319748510`;
- source bytes: `4,628,480`;
- exact rows: `8,627`;
- stable child identity:
  `7167d2b6b5a01e73d3ca8e5e49378a51a61f0e1b1753b1e12011c5dd05f1201b`;
- row fingerprint:
  `89bb111ca53eb905c344f113a3668102b8ad9a0fc5581cb585d6fb5004a81c29`.

The custom archive is `359,470` bytes with SHA-256
`0187f8894222846c9040c60461001643c9cd908cd830b1c0fad5c190dba8e5de`.
The PostgreSQL `17.9` proof ran with network mode `none`, zero published ports,
one CPU, 1 GiB memory, and a read-only package mount. It reproduced the exact
row fingerprint and logical catalog hash
`dce534bec2cd70afe873ccd5cc0c327d636bc93137839b07f20ee57631908501`.
Cleanup proved container absence and removed all owned volumes, PGDATA, and
scratch. The live child remained attached with the same OID, relfilenode,
bytes, and row count, and every sampled public-health request returned HTTP
`200`.

The checksummed package is:

```text
/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/
  snapshot-generation-archives/cycle9-pro-cymbals-1314/
```

This accepts the archive-only package and restore-prover tier. It does not
authorize source detach, quarantine, rename, drop, truncate, or deletion.

## Quarantine and reattach tier

`tools/postgres-snapshot-generation-quarantine.sh` exposes only:

- `plan`, which is read-only;
- `quarantine`, which requires the exact sealed plan digest plus explicit
  operator identity and approval reference;
- `attest`, which records an immutable 55-route/database-state observation;
- `reattach`, which restores the same child after successful quarantine and
  soak attestations.

The tool connects directly through Npgsql. It never invokes Docker, accepts no
relation name or SQL text, and maps only the fixed nine instrument families.
Every input and output path must remain beneath the configured FST evidence
root without symbolic-link traversal. `plan` authenticates the complete
archive package and network-none restore proof, a checksummed successful
full-scrape evidence bundle, and two same-publication 55-route captures. Raw
route bodies must match their manifest sizes and SHA-256 values. JSON is
normalized from the authenticated raw bytes; deterministic binary responses
compare exact hashes. ZIP exports compare sorted entry names and contents
recursively; only timestamp suffixes on generated outer workbook names,
random Office core-property part names/relationship IDs, and Office
created/modified metadata are excluded. Workbook sheets and all other payload
entries remain byte-compared.

The sealed plan binds the PostgreSQL system/database identity, current
publication, latest accepted planner cycle, exact candidate observation,
OID/relfilenode/bound, stable child/config hashes, physical bytes, exact row
count, and the archive row fingerprint. `quarantine` revalidates all evidence,
then acquires the registration, service-maintenance, publication, planner,
snapshot-generation DDL, and executor session advisory locks in that order
before opening the `SERIALIZABLE` transaction. The database functions repeat
the chain with nonblocking transaction locks and fail with `55P03` rather than
wait on an older MVCC snapshot. The child receives a shared table lock while
the CLI streams the same ordered row fingerprint used by the archive tool.

The mutation transaction:

1. inserts one `retention_in_flight` hold;
2. requires the instrument DEFAULT child to be attached and empty;
3. adds and validates an exact `CHECK (snapshot_id <> G)` constraint on that
   DEFAULT child so writes for the quarantined generation fail rather than
   becoming ghost rows;
4. detaches the numeric child and structurally proves its exact two btree
   index roles and both root/top attachment chains;
5. renames the existing index OIDs to
   `sgqi_<full-operation-id>_{pk|score}`, recording immutable before/after
   semantic and physical evidence; the PK constraint is renamed with its
   index;
6. moves and renames the table under `fst_snapshot_quarantine`, adds an exact
   `CHECK (snapshot_id = G)`, and installs a trigger rejecting
   insert/update/delete/truncate;
7. inserts one immutable operation row containing all evidence digests and
   physical identities.

Any failure rolls back the hold, both constraints, relation moves, and
operation/index evidence together. Index rename is catalog-only: table and
index OIDs/relfilenodes remain exact. Quarantine operations, index mappings,
reattachments, and attestations are append-only; active state is derived from
the absence of a reattachment row.

The first post-detach `quarantined` attestation must compare the sealed
pre-quarantine candidate capture with a new capture on the original
publication. Later `soak` evidence may follow publication rotation, but it
must use two exact captures of the same then-current idle/unfrozen
publication. Before reattach, the database requires that current publication's
successful soak and proves the target has no active snapshot, current
projection, named current/previous/working publication source, unreplayed
writer failure, or additional active hold. Reattach verifies the private
relation, exact check, mutation trigger, DEFAULT exclusion, zero DEFAULT rows
for `G`, row count, OID, and relfilenode. Before moving the table, it verifies
the operation-scoped index mappings or performs the same role-based rename as
a one-transaction repair for pre-change operations. It never renames or drops
an unrelated destination object. It then attaches the same child, proves both
required child -> root -> top index links by exact index OIDs, removes the two
temporary checks, releases the hold, and writes one immutable rollback row in
the same transaction. A failed rename, move, or attach rolls back without
residue; behavioral lock coverage proves no strong lock is taken on unrelated
tables or indexes.

A final `reattached` attestation must compare the latest successful soak
capture with the post-reattach capture on that same publication. Publication
rotation therefore cannot strand rollback, but it also cannot weaken liveness
or same-publication parity.

### Accepted live quarantine/reattach canary

The first live canary used cycle `11`, scrape `1331`, publication `153`, and
Pro Cymbals snapshot `1314`. Candidate scrape `1331` completed all 21 terminal
phases with zero critical/best-effort failures, zero unreplayed writer
failures, 6,390 complete published source rows, notifications complete, and a
clean run-once exit. Cycle `11` observed 107 candidates, 160 protected
children, zero blocked children, and `186,638,991,360` candidate bytes with
exact planner/oracle agreement.

The fresh archive/proof retained:

- child OID and relfilenode `319748510`;
- `4,628,480` physical bytes;
- `8,627` rows;
- row SHA-256
  `89bb111ca53eb905c344f113a3668102b8ad9a0fc5581cb585d6fb5004a81c29`;
- a network-none PostgreSQL 17 restore with exact row/catalog parity and
  complete cleanup.

Operation `73bee4a09dc7648b98b7176c32616f2f`, sealed by plan digest
`d7d9305ae11061d3ce88de892d0a248096ee35211f464ab9018e67c5f9849550`,
quarantined the child for 452 seconds. Eleven 30-second soak samples returned
HTTP 200 for readiness, songs, and a representative leaderboard while
publication 153 remained idle/unfrozen. The private relation retained the same
OID, relfilenode, and 8,627 rows; the DEFAULT child retained zero rows for
snapshot 1314; the hold and both safety constraints remained present; and no
lock waiter appeared.

All three 55-route attestations (`quarantined`, `soak`, and `reattached`) had
zero status, normalized JSON, deterministic-binary, or normalized-export
differences. Reattach restored the original public relation with the same
OID/relfilenode, physical bytes, row count, row SHA-256, parent attachment, and
two required index chains. It removed both temporary constraints, removed the
private relation, released the exact hold, and persisted the immutable
reattachment.

The acceptance bundle is:

```text
/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/
  snapshot-generation-quarantine-candidate/
  acceptance-cycle11-pro-cymbals-1314/
```

This accepts the bounded quarantine/soak/reattach tier. It does not implement
or authorize `DROP`, automatic retirement, or sparse-child compaction.
At the scrape `1333` confirmation point, operation
`73bee4a09dc7648b98b7176c32616f2f` remained reattached at original
OID/relfilenode `319748510`, with no private relation and its hold released.

### Q1 publication-rotation incident

Q1 operation `1b44941dc5d5ea806dabc2187c3cffed` later quarantined the same
Pro Cymbals `1314` child across the successful scrape `1335` and publication
rotation `159` to `162`. Scrape `1335` completed 710 songs and 41,165,867
entries with zero failures. Cycle `15` retained exact planner/oracle
agreement, classified the target absent, and had zero blockers. The
publication-162 55-route soak attestation passed.

The initial reattach then failed closed with PostgreSQL `42P07`: while the
child was private, scrape `1335` created a Solo Guitar child whose public
secondary index reused the quarantined child's former name
`leaderboard_entries_snapshot_snapshot_id_song_id_instrum_idx121`.
`ALTER TABLE ... SET SCHEMA public` moves a table's indexes and therefore
encountered the schema-wide relation-name collision. The transaction left no
residue: the target remained private at table OID/relfilenode `319748510`, its
then-active hold, exact checks, mutation trigger, validated DEFAULT fence,
zero DEFAULT rows, and both index OIDs were intact; no reattachment row
existed at that incident boundary.
The API remained healthy and the worker remained intentionally stopped.
The colliding private score index is OID `319748518`; the unrelated public
Solo Guitar index is OID `321720750`.

The operation was recoverable without repeating the five-hour rotation. Later
live progression reached a separately approved DROP function call, so the
earlier pending-recovery state is historical; exact intervening
reattach/cycle/Q2 acceptance remains operator-owned evidence. The DROP still
did not occur because the live empty operation table lacked nine semantic
columns and failed closed with `42703`.

## DROP and logical-restore tier

The repository now contains a separately gated implementation with no runtime
caller:

- `SnapshotGenerationDropSchema` adds immutable drop, attestation, restore,
  restore-finalization, and evidence-chain relations;
- `FstSnapshotGenerationDrop` exposes only `select-canary`, `plan`, `drop`,
  `confirm`, and `attest`;
- its shell wrapper runs only a prebuilt DLL after verifying the
  operator-supplied SHA-256 and never invokes Docker;
- `postgres-snapshot-generation-restore.py` is a separate recovery surface
  that reuses the accepted archive package and PostgreSQL 17 container/mount
  helpers without adding mutation commands to the archive-only tool;
- the service and worker do not invoke the drop function;
- all new DROP/restore functions are `SECURITY INVOKER`, remain revoked from
  `PUBLIC`, and receive no repository-defined grants.

The Q1 rehearsal and Q2 destructive operation must be distinct. Q1 proves
quarantine on publication A, soak through a successful full scrape and
completed rotation to publication B, exact reattachment, and post-reattach
parity. Q2 uses a fresh archive and
proof for its unchanged current cycle/publication, remains quarantined for at
least 30 minutes, and supplies 60 successful 30-second health samples plus
fresh route parity and a proof completed after that health window.

The DROP boundary does not weaken or independently reinterpret the five-cycle
gate. Q2 can be created only after quarantine validates five accepted recent
cycles; quarantine operations plus retention cycles/observations are
immutable; and DROP requires Q2's cycle to remain the exact latest accepted
cycle with the same publication. Any appended cycle or publication rotation
therefore rejects DROP and requires a fresh Q2.

The DROP transaction obtains the existing six locks followed by a dedicated
drop lock, repeats all seven as nonblocking transaction locks, takes
`SHARE` only on the exact DEFAULT child and `ACCESS EXCLUSIVE` only on the
private child, and repeats publication, liveness, topology, identity,
writer-failure, hold, DEFAULT, dependency, row-count, and row-fingerprint
gates. A behavioral `pg_locks` regression proves the private child alone holds
`AccessExclusiveLock`, the exact DEFAULT child holds `ShareLock`, and the top,
root, and sibling children remain unlocked. Under that private-child lock it
also recomputes the detached two-index inventory and requires exact
role/OID/relfilenode/operation-scoped-name joins to the active operation's
immutable rename evidence. Wrong, rebuilt, reindexed, role-swapped, extra, or
missing indexes reject before DROP with no operation/attestation residue. It
retains the already validated Q2 DEFAULT exclusion under its deterministic
quarantine-operation name,
inserts immutable
operation/evidence rows, and executes one
`DROP TABLE <derived-private-relation> RESTRICT`. It never accepts a relation
name or SQL text. The pre-DROP attestation passes its observed route count,
status parity, semantic JSON parity, and difference count into PostgreSQL;
the function validates and persists those values rather than writing asserted
success literals.

The immutable operation row and relation absence classify a lost commit
acknowledgement. The hold and durable DEFAULT exclusion remain after DROP.
The generation-ensure function checks active retention/restore holds both
before and after its DDL lock using rolling-schema-safe dynamic queries,
preventing an impostor public child during quarantine or recovery. A finalized
restore is authenticated by stable OID; relfilenode remains historical because
supported rewrites can change it. New drop-owned insert guards make the source
quarantine operation terminal for later reattach/attestation writes.

Restore selects exactly four archive TOC entries: the child table, table data,
primary-key constraint, and secondary index. Parent and table/index attachment
entries are excluded. All four entries remain authenticated provenance, but
only `TABLE` and `TABLE DATA` are executable through
`pg_restore --single-transaction`. Archived index DDL is never executed.
After exact archive and detached-table validation, the guarded database phase
creates only fixed repository-owned btree shapes named
`sgri_<full-restore-operation-id>_pk` and
`sgri_<full-restore-operation-id>_score`, promotes the unique PK index with
`PRIMARY KEY USING INDEX`, attaches the table to the exact parent, and proves
both index chains. Expressions, predicates, INCLUDE columns, alternate
opclasses/collations/options/order, extra indexes, and non-default tablespace
drift fail closed. Existing unrelated objects with archived index names are
neither renamed nor dropped.

Leaf index names, PK constraint names, parent display names, and the
name-bearing prefix of raw `CREATE INDEX` text are non-semantic. Raw archive
SHA-256, raw logical-catalog SHA-256, and stable-config hashes remain
independently authenticated provenance for each Q1/Q2 package, but are not
cross-package equality gates: PostgreSQL custom archives are not
byte-reproducible. Q1/Q2 equality instead binds stable child identity, exact
rows/table identity, a versioned name-insensitive semantic catalog, fixed
logical index roles/shapes, and exact physical index OID/relfilenode plus
root/top OID chains. The hold remains until exact same-publication restored
parity is recorded and separately finalized.

Official scrape `1333` and cycle `13` satisfy the confirmation prerequisite.
The remaining live blockers are independent final review, any required
production role/grant procedure, the approved Q1 repair/reattach and
reattached attestation, a new post-reattach scrape/publication/cycle, explicit
Q2 authorization, and the mandatory-restore Q2 canary. The first live canary
must restore the child; a permanent drop is a later independent promotion
decision.
See the
[DROP and logical restore runbook](SnapshotGenerationDropRunbook.md).

## Scheduling boundary

Only `ScraperWorker` schedules the observer. It is not a
`PostScrapeOrchestrator.RunCleanupAsync` phase because cleanup occurs before
publication and is best effort.

The worker keeps a bounded FIFO keyed by `(scrape_id, publication_id)`. It
deduplicates re-entry but never replaces an older item with a later
publication. Startup recovery queues a publication it commits, and normal
worker startup requeues the current publication so an immutable existing cycle
can close restart/re-entry safely. Queue capacity fails closed rather than
discarding an item.

Run-once observes after its registration drain. Continuous mode checks the
queue at the next pre-allocation boundary after the normal interval. Before
pausing any background work, one command-timeout-bounded aggregate query
classifies registration state. Runnable work signals the registration worker
and gets an adaptive `250 ms`-to-`2 s` poll for at most 30 seconds. Expiry keeps
the FIFO unchanged and yields to the scheduled scrape without recording a
cycle. Once the durable registration drain is complete, retryable planner
deferrals or invocation failures remain at the head until a terminal persisted
cycle exists. A non-runnable registration error/missing state bypasses the
drain wait and is instead a terminal cycle blocker: the immutable blocked cycle
records bounded counts, the FIFO removes that head, and later publication safe
points can proceed.
Before invoking the planner the worker:

1. has completed publication and released the public-read freeze;
2. has completed or recovered improvement notifications;
3. has attempted the post-publication scores-changed broadcast and supplies the
   exact successfully broadcast scrape ID; a missing confirmation is retried,
   while an already persisted cycle is idempotently accepted after restart;
4. has observed no runnable registration work, or an explicit non-runnable
   registration blocker, without cancelling an active registration batch;
5. pauses background work once and waits for quiescence;
6. then lets the planner recheck publication, registration, notification, and
   maintenance state.

Recoverable broadcast, background, notification, registration, freeze,
publication-intent, working-publication, or max-score state writes an explicit
immutable deferral. Runnable backfill states are `pending`, `in_progress`, and
`deferred`; missing/error history state remains retryable because the existing
history worker durably admits it again. Missing backfill state, backfill
`error`, unknown registration state, and malformed terminal notification state
instead persist cycle-global blockers. Neither outcome creates a candidate
cycle.

## Lock order

The report-only observer uses one PostgreSQL session and the fixed order:

1. exclusive registration mutation advisory lock;
2. exclusive centralized service-maintenance advisory lock;
3. shared publication advisory lock;
4. exclusive report-planner advisory lock;
5. bounded read transaction and catalog/table locks acquired by PostgreSQL.

`DatabaseRetentionMaintenanceService` uses the same centralized
service-maintenance lock. Metadata TTL and generation observation therefore
cannot overlap. Acquisition uses bounded `pg_try_advisory_lock` retries and
records a retryable deferral on contention.

The observer does not acquire the snapshot-generation DDL or executor lock.
The separate quarantine CLI acquires both after the four observer locks while
holding its serializable transaction.

## One-snapshot observation

All topology, liveness, publication, notification, registration, hold, writer
failure, and oracle reads use one bounded PostgreSQL `REPEATABLE READ`, `READ
ONLY` transaction with short lock, statement, and idle-transaction timeouts.
The immutable control-plane write occurs afterward in a separate short
transaction while the session advisory locks remain held.

Each physical child captures:

- instrument and snapshot ID;
- top-parent/root/child schema/name and OIDs;
- child relfilenode;
- exact partition bound;
- relation kind, persistence, access method, tablespace, relation options, and
  attached-index configuration;
- the complete top-parent -> instrument-root -> default/numeric-child index
  hierarchy, including exact per-child parent cardinality, valid/ready state,
  and matching primary/unique/access-method attributes;
- separate row-estimate and total-byte observations.

The stable child identity and stable config/schema hashes include no row or byte
estimate. The observational metrics hash includes those volatile values.
Canonical ordering makes hashes independent of query/collection order.

## Child-scoped liveness

Identity and protection are `(instrument, snapshot_id)` plus the exact physical
catalog identity. A reference for one instrument does not protect another
instrument child with the same scrape ID.

The primary classifier protects:

- active snapshot rows for the same instrument;
- current-projection source rows for the same instrument;
- physical publication-source rows belonging only to the named current,
  previous, or working publications;
- children belonging to running scrapes;
- the explicitly configured resume scrape;
- unreplayed writer failures for the same instrument;
- active operator, retention-in-flight, or restore-in-flight holds.

Unreplayed writer failures are mandatory. There is no disable option. In
particular, scrape `1308` remains protected wherever retained unreplayed
writer-failure evidence identifies the child; report output cannot classify
that child as a candidate until the evidence is explicitly replayed/proven.

The following fail closed as blockers or terminal deferrals:

- missing or malformed parent/root/default/child/index topology; required top
  indexes and every root/default/numeric index attachment must be present,
  one-to-one, valid, and ready, and failures are cycle-global even when that
  root has no numeric child on which to attach a blocker;
- nonempty or unresolved default children;
- missing scrape provenance or nonterminal scrape state for the trigger, every
  named publication, and every physical child, independent of whether another
  liveness root protects that child;
- invalid/missing physical roots;
- duplicate, missing, or mismatched named publication state;
- unpointed `building`, `ready`, or `current` publication state;
- failed publication state with malformed/nonterminal scrape identity, a named
  pointer, configured resume ownership, a publication freeze/commit/max-score
  or notification reference, live/building surface binding, cache/cache
  staging, catalog, path artifact, scrape staging, deep-scrape work, or
  prepared/retained band relation;
- freeze or publication commit intent;
- max-score mutation gate state;
- recoverably incomplete notifications, scores-changed broadcast,
  registration drain, or background quiescence;
- non-runnable missing/error/unknown registration state and malformed,
  missing, or internally inconsistent terminal notification state;
- primary/oracle disagreement.

An unnamed `publication_generations` row stuck at `retained` is stale
bookkeeping, not a liveness root or blocker. Planner version 2 introduced the
cycle's separate immutable `anomalies` collection and summary hash-chain
payload for that evidence.

Planner version 3 applies the same warning-only treatment to an unnamed
`failed` publication only when both generation and scrape have exact terminal
failed identity and no recovery owner or live artifact listed above remains.
Failed/retired binding rows are terminal provenance and do not by themselves
restore recovery ownership. Orphaned
`leaderboard_published_scope_source.published_scrape_id` rows are counted and
reported but are not liveness roots for an unnamed failed publication.
Unreplayed writer failures are also counted in the publication anomaly, while
their existing independent `(instrument, scrape_id)` root continues to protect
only the exact physical generation child.

Every failed-publication anomaly or blocker carries publication/scrape status
and identity, terminal timestamps, pointer/state references, per-artifact
counts, source-row count, unreplayed-writer-failure count, and canonical
recovery reasons. That structured evidence participates in
`observation_hash` and the immutable summary payload. An anomaly does not enter
global/child blockers, change candidate classification, or turn an otherwise
`observed` cycle into `blocked`. Planner version 3 never rewrites version 1 or
2 cycles, and the observer does not mutate legacy generation, binding, source,
or writer-failure rows to clear a warning.

The temporary `fst_max_score_evidence_sources` table is not durable provenance
and is never queried.

## Independent SQL oracle

For each current/previous/working publication, the primary planner also
requires the authoritative `solo_scope_sources` binding to be `ready`, tied to
the exact publication and scrape, and backed by positive preparation metadata.
The binding row count and SHA-256 canonical `(instrument, song_id, scope_kind)`
key-set hash must exactly match all source rows. Missing, extra, duplicate,
incomplete, malformed, or identity-mismatched rows block the whole cycle.
The existing publication source-evidence and service-readiness probes use the
same authoritative binding contract, so partial loss cannot remain
success-shaped outside the planner.

`SnapshotGenerationRetentionOracle` independently derives:

- the exact attached numeric physical child set;
- the exact live child set from active, projection, named-publication,
  running/resume, writer-failure, and active-hold state;
- the exact unreferenced set.

It also independently reads and validates every named publication binding,
expected count, source key set, and identity in SQL. A separate
`pg_partition_tree` catalog traversal inventories the top, instrument-root,
default-child, and every numeric-child index layer independently of the primary
planner's `pg_inherits` traversal. Per numeric child it records expected parent
count, missing/duplicate/detached indexes, valid/ready state, and
primary/unique/access-method agreement. The planner compares all three exact
canonical child sets plus both independently produced publication-source and
index-topology validation fact sets. Any difference persists both sides, marks
the cycle
`oracle_mismatch`, marks every child observation `oracle_mismatch`, and forces
candidate count/bytes to zero.

When `UsePublishedScopeSources=true`, the same authoritative current binding
contract is an actual serving gate rather than planner-only evidence.
Startup must validate it before signalling ready; `/readyz` rechecks it through
a one-second keyed cache; publication-bound cache hits, ordinary HTTP reads,
and WebSocket admission fail with `503` when it is missing, partial, malformed,
legacy, or identity-mismatched. Publication commit repeats the exact validation
immediately before pointer movement. Lazy cache waiters revalidate after their
single-flight lease and immediately before serving bytes. WebSocket admission
records and rechecks the validated publication even when full request pinning
is disabled. Final admission plus every `subscribe_sync`/`unsubscribe_sync`
rebind validates pointer/source identity and atomically moves the connection
under one bounded shared publication lease. Publication-change snapshots share
the in-process mutation gate, so commit either precedes the operation and the
stale socket is rejected, or follows registration and its transition
notification sees the socket. The lease and mutation gate are released before
any WebSocket I/O or socket lifetime; null or stale identities close on
publication change.
Disabling the read-source feature
preserves rolling compatibility for roles that intentionally still use the
legacy read path; it does not weaken planner validation.

## Durable evidence

The additive schema contains:

- `snapshot_generation_retention_cycles`: one immutable observation per
  terminal scrape/publication safe point, exact planner/oracle sets, hashes,
  counts, blockers, nonblocking anomaly warnings, and error state;
- `snapshot_generation_retention_observations`: one exact physical-child row
  with stable identity/config hashes, separate metrics, root reasons, and
  classification;
- `snapshot_generation_retention_deferrals`: immutable bounded-lock and
  terminal-state deferrals;
- `snapshot_generation_retention_holds`: explicit operator and future
  retention/restore safety holds; this slice has no service method that creates
  executable work from them;
- `snapshot_generation_retention_evidence`: append-only per-cycle SHA-256 hash
  chain.

Worker logs and these read-only relations are the visibility surface. There is
no public HTTP route or mutation CLI.

## Metadata TTL provenance and publication retirement

Each prepared publication now records a SHA-256 canonical scope-source key-set
hash in its authoritative ready binding. The `scrape_log` TTL predicate no
longer treats physical snapshot rows as the only final guard. It explicitly
excludes a scrape while referenced by:

- either `leaderboard_published_scope_source.published_scrape_id` or
  `.source_scrape_id`;
- a nonretired or incompletely retired
  `publication_generations.scrape_id`;
- an unreplayed `scrape_writer_failures.scrape_id`;
- current publication/freeze/notification state;
- retention cycles, deferrals, child observations, or holds.

Normal publication rotation now owns servable publication retirement separately
from scrape/evidence retention. When a third publication moves an older
generation outside current/previous/working, post-commit cleanup:

- confirms completed terminal scrape state, no freeze/commit/max-score intent,
  and no remaining cache, staging, catalog, path, or retained band surface;
- revalidates the exact preparation identity, positive expected count, ready
  source binding, canonical key hash, and complete source row set;
- marks every binding terminally retired, removes only that publication's
  source-map rows, stores `retired_at` and `retired_scrape_id`, and clears the
  live `scrape_id` reference.

Startup cleanup and metadata TTL can complete the same validated transition
after an interrupted post-commit cleanup. Immutable cycles, deferrals,
observations, holds, unreplayed writer failures, and newer source rows that
still cite the scrape remain untouched and continue to protect `scrape_log` or
physical children through their own restrictive references. Thus ordinary four-publication rotation remains eligible for validated v1
retirement. Historical retained generations whose legacy bindings cannot pass
that validator remain untouched and appear as
`unpointed_retained_publication` anomaly evidence on every applicable cycle;
they do not suppress candidates. Unpointed building/ready/current generations
and failed generations with genuine recovery ownership or malformed terminal
identity still fail closed as blockers. Terminal unnamed failed generations
with no live recovery artifact appear as
`unpointed_terminal_failed_publication` anomaly evidence even when orphaned
source-map rows remain; unreplayed writer failures continue to protect only
their exact instrument/generation children.

Retirement columns are added only in a short bounded transaction. Their partial
lookup index uses an advisory-lock-serialized, bounded
`CREATE INDEX CONCURRENTLY`; exact validation makes a healthy index a no-op and
retry removes an invalid interrupted artifact before rebuilding.

The legacy generation-to-scrape FK name remains compatible with old binaries
and may be CASCADE. A separately named validated restrictive FK and a
`BEFORE DELETE` `scrape_log` guard enforce the terminal invariant even if a
`c35b7f47` initializer restarts and rewrites the legacy FK. Both are additive
inside the bounded migration transaction, so the existing FK is never dropped
during cutover. Previous-publication FK repairs remain transactional and
bounded.

This prevents a later child removal from cascading away publication,
source-lineage, or unreplayed writer-failure evidence and then making the same
child appear safe. Tests use publication rotation and retirement, remove the
physical-row guard, age metadata, and prove that TTL cannot launder blockers.

## Promotion gates and future tiers

Repository tests are not live evidence. The coordinator-owned report-only
window produced accepted cycles `5` through `9`.

- The **two clean terminal report-only cycle** gate for archive-only
  development is satisfied.
- Archive-only implementation, isolated synthetic proof, and the smallest-child
  live archive/restore canary are accepted.
- Destructive enablement requires **five exact planner/oracle agreement
  cycles**.
- The five-cycle set must include at least one publication rotation and one
  genuine candidate-set change.

The accepted five-cycle set includes publication rotation and genuine
candidate-set changes. The separate quarantine/reattach executor and its first
smallest-child live canary are accepted.

The separate non-cascading DROP/restore implementation is repository-ready but
not live-accepted. Official scrape `1333`, cycle `13`, and the disposable
drill are accepted. Q1 operation
`1b44941dc5d5ea806dabc2187c3cffed` passed its rotation and publication-162
soak, and later live work progressed to an independently approved DROP
attempt. That attempt failed before DDL with `42703`; no child was dropped.
After the empty-table upgrade, operation
`333ba4b9fb69dbc098d127f0008ec709` committed under plan digest
`fa45ca20c2c975e543b7d539d3b27cb05c5d80ff16345665205f2355eb67d5dc`.
The current mandatory action is authenticated logical restoration; its first
plan attempt failed before output or mutation on non-authoritative Python
reserialization. The later authorized H3 attempt also stopped before
plan/list output, restore evidence, or mutation because its generated
PostgreSQL lookup used reserved word `authorization` as an alias. Promotion
remains blocked on restore, parity, health, and later confirmation evidence.
H4 corrected the lookup but then failed before output or mutation because its
fixed-shape check compared the live Q2 catalog's decimal-string
opclass/collation OID arrays directly with integer arrays.

The corrective path preserves that row and bundle through a separate
immutable restore-tool authorization and exact-set tool-only package. The
authorization binds the old pin, reviewed validator base, final executing
tool, original
bundle, byte-identical helper, authorizer, source/diffs/tests, and independent
approval. The empty restore-operation table adds pinned/executing hashes and a
one-use authorization FK. Authorization is rechecked at plan, load, and attach
without weakening any existing recovery invariant.
The unused H3 authorization remains immutable historical evidence. H4 uses
the safe `auth_row` alias, but its now-unused authorization is also immutable
historical evidence. H5 strictly normalizes only supported OID arrays and must
receive a third exact-DROP authorization. The existing schema already
supports this and needs no migration.

H5 subsequently committed the mandatory logical restore at new
OID/relfilenode `321906645`, preserving all `8,627` rows, their fingerprint,
bound, semantic hashes, and both index chains. Its route attestation failed
before database write because Python compared raw ZIP exports. Exact shared
C# normalization proves equal band/player export semantics; no export body is
committed as a fixture.

The corrective post-restore tier is a distinct
`FstSnapshotGenerationRestoreContinuation` C# tool with only
confirm/attest/finalize commands. An immutable continuation authorization
links the restore row, predecessor H5 authorization, exact plan/report/package,
shared evidence assembly, three route manifests/checksum trees, exact restore
scope, both service runtime identities, repository diff/source/tests, and
independent approval. The historical post-DROP pair is not treated as equal:
exactly 54 routes match, while `/api/shop` is accepted only through the fixed,
hash-pinned daily-inventory bridge. The bridge proves one UTC midnight,
`117 -> 117` songs, the exact `100` announced departures, `100`
catalog-consistent arrivals, `17` unchanged overlaps, and no new-song or
other-field drift. Its authenticated `lastUpdated` must cross the same UTC
midnight and then remain unchanged. A separate post-restore-to-repeat pair must pass the
unchanged strict 55-route validator. Empty downstream tables add the
evidence-tool/authorization FKs and temporal-bridge hash; finalization must use
the same authorization and bridge as attestation. No route exception is a CLI,
configuration, environment, or database collection. The H5 restore row, plan,
package, authorization, and DROP evidence are never updated.

H6 is now live-accepted under continuation authorization
`0ed3cd7125af6fdf8748915318b0893d`. The bridge-bound attestation recorded
strict 55-route stabilization, and finalization released hold 3 and removed
the mutation trigger while preserving restored OID/relfilenode `321906645`,
8,627 rows, and both index chains. Candidate scrape `1337`, publication `171`,
notifications, and cycle `17` completed with zero failures and exact
planner/oracle agreement. The restored snapshot was observed as an eligible
candidate under its new physical identity. The acceptance bundle manifest is
SHA-256
`0ee12d9e9c6d0e2dd8230eca359b0a807106ef128698c9e83ef203756bea3f56`.
Automatic bounded retirement and sparse-child compaction remain unimplemented.

Rollback for this slice is to keep
`DatabaseMaintenance:SnapshotGenerationRetentionReportOnlyEnabled=false`.
The additive immutable schema may remain; it has no executable consumer.
