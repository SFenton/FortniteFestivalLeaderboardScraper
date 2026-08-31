---
status: canonical
owner: operations
last_verified: 2026-08-30
last_verified_commit: 21d7193c
sources:
  - AGENTS.md
  - .github/copilot-instructions.md
  - .github/instructions/fst-postgres.instructions.md
  - FSTService/Scraping/ScrapeLifecycleNotifier.cs
  - FSTService/ScraperOptions.cs
  - FSTService/Persistence/MetaDatabase.cs
  - FSTService/Persistence/MaxScoreMaintenanceService.cs
  - FSTService/Api/PublicationReadContext.cs
  - FSTService/Api/PublicReadGateMiddleware.cs
  - FSTService/Api/SongEndpoints.cs
  - FSTService/Scraping/PathArtifactResolver.cs
  - docs/operations/deployment.md
  - tools/fst-worker-compose-guard.sh
  - tools/fst-worker-no-progress-watchdog.mjs
  - tools/postgres-retire-ix-le-song-rank.sh
  - tools/postgres-pro-bass-snapshot-rewrite.sh
  - tools/postgres-snapshot-generation-archive.py
  - tools/postgres-snapshot-generation-archive.sh
  - tools/postgres-snapshot-generation-archive-drill.py
  - FSTService/Persistence/Maintenance/SnapshotGenerationQuarantineSchema.cs
  - tools/postgres-snapshot-generation-quarantine.sh
  - FSTService/Persistence/Maintenance/SnapshotGenerationDropSchema.cs
  - tools/postgres-snapshot-generation-drop.sh
  - tools/postgres-snapshot-generation-restore.py
  - tools/capture-snapshot-generation-drop-health.py
  - tools/postgres-snapshot-generation-drop-drill.py
  - docs/database/SnapshotGenerationDropRunbook.md
  - tools/capture-publication-route-contract.sh
  - docs/database/ProBassSnapshotRewritePilot.md
  - docs/database/SnapshotGenerationPartitionMigration.md
update_triggers:
  - Production ownership, preflight, maintenance, parity, publication, storage, or recovery rules change.
---

# Live safety

## Production boundaries

- The live Compose project is
  `/home/sfenton/Docker/FestivalServiceTracker`.
- Repository Compose files are templates.
- All database data, scratch, exports, migration artifacts, repacks, and
  retention work stay on the 4 TB FST drive unless the operator explicitly
  overrides the rule.
- The only current alternate-device override is temporary snapshot migration
  archive/restore/recovery work on `/dev/nvme2n1p2`, mounted at `/`. It does
  not authorize a permanent FST store or PostgreSQL tablespace there.
- Keep secrets out of commands, logs, documentation, artifacts, e-mail, and
  commits.

## Before broad probes, deploys, scrapes, or maintenance

Check:

1. Docker service health;
2. PostgreSQL readiness and cluster identity;
3. public-read freeze and publication state;
4. the current published scrape/generation;
5. locks and long-running queries;
6. disk headroom on the FST drive;
7. CPU and memory pressure.

Use bounded read-only probes first.

## Snapshot-generation archive-only boundary

The archive-only CLI is repository tooling, not a production-owned service or
automatic retention executor. A live invocation must be parent-controlled and
must use a new output directory under `/mnt/docker-storage`. It must never use
the repository Compose templates to mutate production and must not be combined
with any source partition maintenance.

`archive` fails closed unless the newest planner cycle is observed,
report-only, oracle-agreeing, and unblocked; its trigger is still the current
unfrozen publication; notifications are complete; no working publication,
running/resumable scrape, active target hold, or unreplayed target writer
failure exists; and the complete physical/catalog identity still matches the
immutable candidate observation. Solo Bass snapshot `1308` is an explicit
defense-in-depth rejection.

The source is fenced before and after the custom-format dump with exact
catalog, OID/relfilenode, mutation-counter, byte, row-count, and deterministic
row-fingerprint evidence. Full observation sets, exact planner versions,
canonical cycle hashes, summary evidence, and every hash-chain link are
independently rebuilt first; placeholders reject. Drift rejects the package.
Canonical JSON bytes and persisted ordering must match the C# repository,
including embedded-quote escaping and nonempty record-shaped validation
arrays. Preserve those arrays/order for hashing; use only exact
`comparisonKey` values for planner/oracle comparison.

Output is restricted to
`fst-data/evidence/snapshot-generation-archives`. Resolve PGDATA, tablespaces,
all source mounts, and Docker root before creating output, then reject any
equal/ancestor/descendant overlap, bind alias, mount-source/FS-root alias, or
nested mount boundary. Pin all later source commands to the discovered
container ID and re-inspect container/image/database/system/PGDATA/tablespace
identity at dump admission and after streaming. Archive and proof share an
exclusive same-drive reservation lock and recheck current physical size/free
space immediately before admission.

Pre-provision
`fst-data/evidence/.snapshot-generation-archive-operation.lock` as a regular
non-symlink file. The tool opens it read-only and never creates it. Both public
commands pin archive-root/protected-source mount identity before opening the
lock, revalidate immediately after acquisition, and check again before their
first archive/proof-path write. Unsafe archive-root aliases must receive zero
files, including lock and rejection evidence.

`prove` uses a transient PostgreSQL 17 container with network mode `none`,
zero published ports, bounded resources, read-only package access, and an
exact same-drive PGDATA bind. Extra/anonymous data mounts or
`data_directory` mismatch reject. Label-based cleanup after uncertain starts
proves container absence before touching PGDATA and always records final
cleanup plus rejection evidence when unsuccessful. Use structured `--mount`
binds. Remove owned containers with `docker rm -f -v`, record unexpected
anonymous volume names, and reject cleanup while any captured volume remains.
Before writing `proofs`, markers, PGDATA, cleanup, or rejection evidence,
validate the existing/prospective parent mount identity, nested boundaries,
and protected-source aliases. Revalidate immediately before atomic proof
directory creation; a rejected parent must receive zero files.

Five live report-only cycles are accepted: `5/1325`, `6/1326`, `7/1327`,
`8/1328`, and `9/1329`. They have exact planner/oracle agreement, zero
blockers, publication rotation, and genuine candidate-set changes.

The accepted live archive-only canary targeted Pro Cymbals snapshot `1314`
from cycle `9`. It produced and network-none restore-proved the checksummed
package under
`fst-data/evidence/snapshot-generation-archives/cycle9-pro-cymbals-1314/`
while the worker was offline and publication `1329` was idle and unfrozen.
The source child remained attached and unchanged, transient proof resources
were fully removed, and sampled public health remained HTTP `200`.

These facts complete the five-cycle observation prerequisite and accept the
archive-only tier. They do not authorize source detach, rename, quarantine,
drop, truncate, or row removal. Those actions still require a separately
implemented executor, exact matched parity, transactional reattach rollback
proof, soak evidence, and explicit operator approval.

## Snapshot-generation quarantine boundary

The repository contains a no-Docker-socket quarantine/reattach executor. Its
first production canary is accepted, but it is never automatic and must not
run from the API or worker process. There is no drop, truncate, row-delete, or
cleanup command.

Before `plan`, require:

1. a newest-cycle accepted archive and network-none restore proof;
2. a checksummed successful full-scrape evidence bundle for that same scrape;
3. two exact 55-route captures on the same current publication;
4. an idle, unfrozen publication with completed notifications;
5. zero running scrape, active target hold, or unreplayed target writer
   failure;
6. exact current PostgreSQL system/database identity, candidate
   OID/relfilenode/bound, row count, bytes, and row fingerprint.

Set only variable names in operator shell history; never print the connection
string:

```bash
export FST_SNAPSHOT_QUARANTINE_EVIDENCE_ROOT=<FST-drive-evidence-root>
export FST_SNAPSHOT_QUARANTINE_CONNECTION_STRING=<direct-Npgsql-connection>
```

`quarantine` additionally requires the sealed plan digest, an operator
identity, and an approval reference. It obtains bounded registration,
maintenance, publication, planner, generation-DDL, and executor locks in
order before beginning its serializable mutation transaction. The in-database
functions take the same transaction locks without waiting, so lock contention
forces a new transaction and a fresh snapshot. The exact DEFAULT partition
must be empty. The same transaction adds a validated
`snapshot_id <> G` DEFAULT exclusion, inserts the
`retention_in_flight` hold, detaches the child, structurally classifies exactly
its PK and score btree indexes, renames their existing OIDs to
`sgqi_<full-operation-id>_{pk|score}`, privately moves/renames the child, adds
`snapshot_id = G`, installs a mutation-rejection trigger, and stores immutable
operation plus per-index mapping evidence. The PK constraint follows its
index rename. A failed statement rolls back all of those changes.

Immediately capture and attest the original-publication post-quarantine
55-route result. During soak, preserve the archive and hold. If publication
rotates, take two new same-publication captures while the child remains
quarantined and attest that current publication. Reattach is allowed only
while the current publication is idle/unfrozen and the target has zero
active/projection/named-publication/writer-failure/additional-hold roots. It
requires a successful soak for that current publication.

Reattach validates zero target rows in the DEFAULT child, the validated
DEFAULT exclusion, exact private child identity/check/trigger/row count, and
the operation-scoped index mappings. For a pre-change operation it performs
the same exact role-based normalization as a repair before the schema move.
It never renames, drops, or rebuilds an unrelated destination object, then
restores the same table and index OIDs/relfilenodes and both required index
chains. It drops only the two temporary CHECK constraints and releases only
its own hold. Take a final capture and attest it against the latest successful
pre-reattach soak capture.

Any live quarantine still requires explicit operator approval after current
preflight and parity evidence. A successful reattach canary does not authorize
a later non-cascading drop.

The accepted canary used operation
`73bee4a09dc7648b98b7176c32616f2f` on Pro Cymbals snapshot `1314`,
publication `153`. It held the exact child in quarantine for 452 seconds,
returned HTTP 200 for all 11 health samples, passed zero-difference
`quarantined`, `soak`, and `reattached` 55-route attestations, and restored OID
and relfilenode `319748510`, 8,627 rows, 4,628,480 bytes, the original row
SHA-256, and both index links. The DEFAULT child remained empty for snapshot
1314. The exact hold was released and both temporary constraints were removed.
Evidence is under
`fst-data/evidence/snapshot-generation-quarantine-candidate/acceptance-cycle11-pro-cymbals-1314/`.

## Snapshot-generation DROP and restore boundary

The repository contains a separate DROP-only executable and a separate
logical-restore tool. They are not production services and are never scheduled
by the API or worker. Their presence is not authorization to run them.

Official confirmation scrape `1333` and retention cycle `13` are accepted.
Scrape `1333` completed 710 songs, 41,154,968 entries, 608,691 requests, and
92,821,715,390 bytes with 8,520/8,520 complete manifests across 12
instruments, zero critical/best-effort/writer failures, and no
retry-exhausted/failure reason. Publication `157` became current, unfrozen, and
idle with 6,390 published solo source bindings and first-attempt completed
notifications. Cycle `13` records 111 candidates, 174 protected, zero blocked,
194,754,322,432 candidate bytes, exact planner/oracle sets, and zero global
blockers. Solo Bass `1308` remains protected by
`unreplayed_writer_failure` with stable identity
`4e3310328261704da558e6d83f99cbc77bc01cef10abbac0840df471d33809cc`;
Pro Cymbals `1314` was the true smallest cycle-13 candidate at 4,628,480
bytes. Production continued automatically into scrape `1334`; cycle `15`
correctly recorded that target absent while Q1 was private.

Q1 operation `1b44941dc5d5ea806dabc2187c3cffed` subsequently passed the
scrape-1335/publication-159-to-162 rotation, exact cycle `15`, and
publication-162 55-route soak. Its first reattach failed transactionally with
`42P07` because a new Solo Guitar child had reused the private target's former
secondary-index name. No residue committed: the target remained private at
OID/relfilenode `319748510`, its then-active hold/checks/trigger and validated
DEFAULT fence were exact, the DEFAULT had zero target rows, and no
reattachment row existed at that incident boundary. Later live progression
reached a separately approved DROP function call; do not infer the exact
intervening acceptance state without re-reading operator evidence.

Before any DROP retry:

1. deploy and verify the reviewed empty-table upgrade for all nine DROP and
   seven restore semantic/index/authorization columns plus the six rebuilt
   constraints;
2. rebuild and pin the service/quarantine/DROP artifacts and exact hashes;
3. repeat current Docker/PostgreSQL/publication/worker/lock/resource safety
   checks and verify the production-owned invoker remains least-privilege;
4. reauthenticate the exact Q1/Q2/archive/proof/route/health/bundle evidence,
   regenerate any expired plan, and continue excluding Solo Bass `1308`;
5. obtain explicit operator authorization for the new attempt.

The database functions are `SECURITY INVOKER`, remain revoked from `PUBLIC`,
and have no repository-provisioned grants.

The DROP wrapper runs only a prebuilt SHA-256-verified DLL and has no Docker
access. It accepts no relation, schema, SQL, batch, force, or automatic
selection argument. The transaction keeps the worker offline, acquires the
existing six locks plus the dedicated drop lock, explicitly takes `SHARE` on
the exact DEFAULT child and `ACCESS EXCLUSIVE` on the private child, and
executes exactly one `DROP TABLE <derived-private-child> RESTRICT`. It retains
the hold and the already validated Q2 DEFAULT exclusion under its existing
deterministic name. The DROP transaction holds no relation lock on the top
table, instrument root, or sibling children.

Before rebuilding or retrying a live DROP, run the reviewed initializer and
verify the persisted evidence schema, not merely function presence. The first
approved attempt failed closed with `42703` before DDL because the empty live
drop-operation table predated nine semantic columns; no child was dropped.
The explicit upgrade may add those columns and replace the two named
constraints only when the table is empty. Missing semantic columns on a
nonempty drop or restore operation table are an unrecoverable provenance
ambiguity for automation and raise `55000`; never backfill asserted hashes.

The subsequent approved retry committed DROP operation
`333ba4b9fb69dbc098d127f0008ec709` with plan digest
`fa45ca20c2c975e543b7d539d3b27cb05c5d80ff16345665205f2355eb67d5dc`.
Do not attempt reattach or another DROP. Restore planning initially failed
before output or mutation because Python reserialization did not preserve the
C# canonical plan bytes. The validator must authenticate the original
canonical file and the immutable database row before any restore action.

The repair authorization is inserted only after the accepted post-DROP
monitor. It uses a separate no-Docker authorizer, distinct operator/reviewer,
and a sealed tool-only package; it never changes or duplicates the original
bundle. Before authorization, verify the restore-operation table is empty.
Before planning, immediately before `pg_restore`, and immediately before
attach, re-read the same authorization and committed DROP. Authorization age
only warns; it never expires and cannot strand confirm/attest/finalize after a
restore row commits. Mandatory restore remains required before canary
acceptance.

If commit acknowledgement is lost, run `confirm`; never issue another DROP
until the immutable operation row and old relation name/OID have been
reconciled. A mixed state stops all mutation.

The restore tool selects only the child table, table data, primary-key
constraint, and secondary index from the accepted custom archive for
authentication. It excludes both parent relations and all table/index
attachment entries and executes only table plus table data through
`pg_restore --single-transaction`. Archived index DDL is never executable.
After fixed-shape validation, a short transaction creates deterministic
`sgri_<full-restore-operation-id>_{pk|score}` indexes from repository-owned
SQL, attaches the exact table, and verifies both chains. Archived-name
collisions on unrelated objects are harmless because those objects remain
untouched. A new OID/relfilenode is expected. Raw archive/catalog/config
hashes remain independent provenance; cross-Q1/Q2 equality uses exact physical
identity and a versioned name-insensitive semantic catalog. The retained hold
is released only after exact restored route/data/catalog parity and explicit
finalization; the restore mutation guard is removed atomically with that
release.

The first live DROP canary must be restored after its 30-minute post-DROP
monitoring window. Permanent single-child DROP is a later approval; batch and
automatic retirement remain prohibited. Follow
[the dedicated runbook](../database/SnapshotGenerationDropRunbook.md).

## Startup auto-heal

The repository entry point is
`tools/fst-worker-compose-guard.sh --recover-start`; the live copy and boot-unit
wiring remain production-owned. The action assumes the production orchestrator
has already started core services and effective proxies without dependencies.
It must not be used as a general Compose reconciler.

Before proxy mutation it verifies:

- the merged continuous configuration and exact effective arrays;
- the final merged worker image whenever `--expected-worker-image` is supplied,
  including continuous actions without a data profile;
- the guard-only `worker` profile and continuous `on-failure:5` policy;
- the shared nonblocking worker start/recreate lock;
- PostgreSQL health and `fstservice` readiness;
- a stopped/absent worker container;
- `currentUpdate.status=idle`;
- unfrozen public reads.

The initial and post-recreate proxy windows are finite, proxy recreates are
effective-set-only and capped, and worker startup happens only after all runtime
probes pass. A 1,800-second total deadline also caps core readiness, proxy
convergence, runtime DNS/control/egress qualification, and worker readiness.
It never clears a freeze, rewrites publication state, restarts core services,
changes provider selectors, promotes spares, or installs static endpoint IPs.

Use the dual-lane run-once wrapper for a full-scrape candidate. The
`leaderboard-rivals-batch` profile pairs exact `800/32/4` network enforcement
with a four-account Leaderboard Rivals batch, retains the existing Song Rivals
account concurrency of two and learned CDN ceiling of `360`, and preserves
publication-safe path staging, snapshot reuse, notifications, and
publication-critical manifests.

Size the production unit timeout above the total deadline plus cleanup margin.
The shared lock defaults to `.fst-worker-compose-guard.lock` under the resolved
Compose directory. Every unit and operator must use that same resolved
directory and Unix owner, or configure the same explicit absolute lock path.

`on-failure:5` covers only bounded nonzero worker-process exits while the Docker
daemon remains running. It does not authorize daemon-boot restart; the profiled
worker remains excluded from generic Compose startup and the guarded host
handoff owns restart after reboot or daemon recovery. The guard explicitly
passes `--profile worker` for merged config inspection and worker-targeted
starts; proxy-only recovery never activates that profile.

### Post-start non-convergence

Before stopping an unaccepted worker, the guard re-reads `/api/service-info`.
It stops only when the update remains idle and public reads remain unfrozen. If
work has begun, reads are frozen, or the state cannot be verified, it leaves the
worker running rather than strand a candidate or freeze.

The canonical follow-up is the guarded no-progress watchdog:

```bash
node tools/fst-worker-no-progress-watchdog.mjs \
  --evidence-dir <FST-drive-evidence-path> \
  --dry-run
```

Full-scrape canaries with bounded worker memory may additionally enable:

```bash
node tools/fst-worker-no-progress-watchdog.mjs \
  --evidence-dir <FST-drive-evidence-path> \
  --monitor \
  --recover-worker-exit \
  --max-worker-memory-percent 90
```

Both resource options are disabled by default. `--recover-worker-exit` acts
only when the latest scrape is still running and public reads are frozen for
post-processing. OOM and nonzero exits trigger immediately; a clean exit gets
the configurable `--worker-exit-grace-seconds` window, defaulting to 120
seconds, so the run-once worker can publish its terminal state before a
recovery decision. `--max-worker-memory-percent` is an emergency hard-limit
guard: unlike an ordinary no-progress timeout, it may stop a worker that still
owns active queries. Resource recovery is rejected unless the resolved worker
restart policy is `no`, preventing a race with continuous `on-failure`
restarts. After the bounded Compose stop, the watchdog waits up to
`--worker-query-drain-seconds` (default `60`) for exact worker backends to
disconnect, terminates only remaining `fstworker-scraper` backends (also
the worker-owned `fst-path-generation-admission` lease backend, plus the
captured worker IP as an alternate identity when available), and rechecks zero
before the
existing recovery transaction requires zero candidate publication mappings,
an unchanged published pointer, no waiting locks, and no active database
maintenance. Any failure after the stop still writes the query-drain/error
evidence, renders the report, and attempts notification while publication
remains fail-closed. A failed Docker memory sample is recorded in the
observation and retried on the next poll; unexpected exit recovery remains the
fallback for an OOM kill.

Keep evidence on the 4 TB FST drive. Remove `--dry-run` only after the
watchdog's own observation proves its timeout, database-activity,
candidate-mapping, publication-pointer, and rollback gates. Do not manually
stop the worker or clear the freeze first.

Do not bypass a failed gate by relaxing `service_healthy`, enabling a candidate
continuous profile, or broad-recreating the canonical pool. Investigate the
reported sanitized stage while keeping API/web/PostgreSQL available.

## Public-read and publication safety

During a scrape the worker freezes public reads on the prior published
generation. Failed or incomplete candidates do not replace it. If durable
failure isolation is uncertain, the system remains fail-closed.

Preserve:

- historical leaderboard correctness;
- Epic/provider provenance;
- publication pointer and generation bindings;
- freeze/unfreeze behavior;
- replay and parity evidence;
- notification completion requirements.

## Destructive work

Destructive data/reclaim work is allowed only after a current live-scrape A/B
proves the new path has the same data as the old path. Record:

- exact affected objects;
- accepted parity evidence;
- rollback procedure and boundaries;
- maintenance window and monitoring;
- validation that the command cannot target a different cluster/project.

Removed completed runbooks and Git history are forensic evidence, not reusable
authorization.

### Completed pro-bass archive/rewrite

The guarded production transition completed on 2026-08-18 from commit
`4e2bcdc4`. It retained snapshot IDs `1301-1302`, removed `301,844,706`
historical rows from hot storage, returned `152,985,165,824` filesystem bytes,
and left a `2,811,404,288`-byte `pg_default` partition. Publication `1302`
remained unfrozen and public route parity passed.

Validation scrape `1303` subsequently published and unfroze with zero
best-effort failures. Snapshot reuse reused 1,717 scopes / 6,112,541 rows
globally and 350 scopes / 1,436,731 rows for pro bass. The worker is held
offline. Pro bass now contains `1301-1303`; only `1302-1303` remain protected,
so `1301` is owned by the next generation-retention migration.

The production `plan` stage must use recursive leading-index snapshot-ID
enumeration, metadata-only ownership joins, protected-only fingerprints, and
the checksummed verified-live-archive input for exact total rows/ranges. It
uses no `GROUPING SETS`, no parallel gather, `work_mem=64MB`, and
`temp_file_limit=256MB`. A prior unbounded planning query spilled about 61 GB
of temporary data before it was cancelled; PostgreSQL released the files and
no live data changed. Stop only the exact pilot backends if the temp limit,
filesystem monitor, or public-health monitor reports a violation.

The tool accepts only
`public.leaderboard_entries_snapshot_pro_bass`. Before each live stage it binds
the exact production Compose project/working directory, PostgreSQL container,
system identifier, database/parent OIDs, 4 TB data mount, current/previous
publication, unfrozen state, offline worker, and zero running scrape/phase,
worker/pilot backend, waiting lock, or target lock.

The explicitly operator-created `--scratch-root` must resolve to
`/dev/nvme2n1p2` with the recorded `MAJ:MIN` identity. Symlinks, non-local
filesystems, foreign files, Docker/FST roots, and temporary-system directories
are rejected. Scratch contains only:

- the custom archive and immutable manifest/catalog/TOC;
- isolated restore-drill PGDATA, removed after verification;
- immutable stage reports and measured capacity profile.

The retained live archive is
`/home/sfenton/fst-temporary/pro-bass-archive-20260817T223105Z/pro-bass-original.custom`,
`11,942,257,904` bytes, SHA-256
`3decc75ffe33e24dad72e379fb874c7b0c7b4a421121de6a227acd0fe344760f`.
The successful isolated restore proved `308,536,699` rows, 125 snapshot IDs,
exact per-snapshot row counts/content hashes, the full canonical parent/column/
default/nullability/owner/tablespace/constraint/index catalog, and an unchanged source
during archive. A first restore ordering was rejected for a duplicate child
primary key; the archive was preserved and the corrected detached-child build
plus final attach passed. The restore container and `130,771,858,177`-byte
PGDATA were removed after validation.

The replacement may use only the run-owned temporary scratch tablespace when
the measured dual-filesystem gate passes. That is an interim location, not an
accepted state. While the old rollback relation still exists, `repatriate`
must copy/swap the retained relation to `pg_default` and prove fingerprint/
reference/catalog/API parity while both original and scratch rollback
relations remain. Final drop removes both rollback relations, normalizes names,
and removes the tablespace. The archive remains on 8 TB
scratch through acceptance and a later explicit product-retention decision.

The production-owned Compose bind
`<scratch-root>/postgres-tablespace:/fst-pro-bass-scratch:rw` must be active
before `check`; `check` binds the recreated PostgreSQL container identity.
Critical state uses atomic fsynced publication. A disk-threshold breach writes
a no-resume marker and repeatedly cancels, then terminates, only exact pilot
backends until none remain.
Do not delete it merely because the detached source relation was dropped.

The temporary tablespace and Compose bind have been removed. The final
PostgreSQL container has only its PGDATA mount. Rename-back rollback ended at
final drop; recovery now uses the retained verified archive. Keep that archive
until a separate retention decision.

Production build requires the candidate-specific measured gate:

```text
60,392,999,803-byte emergency floor
+ replacement heap/indexes
+ measured WAL and temp
+ one replacement-sized failure reserve
```

Pre-execution capacity evidence showed a direct `pg_default` build did not fit:
the exact archived row ratio required
`69,713,820,289` bytes and is short by `1,168,706,177` at current
`68,545,114,112` free bytes; the conservative
sensitivity requires `72.19-73.06 GB`. The temporary-tablespace candidate
projects `63,889,690,620` required 4 TB bytes (`4,655,423,492` current
margin) and `17,260,886,072` scratch bytes. Pre-drop repatriation requires
`66,575,033,638` bytes and had `1,970,080,474` projected margin. The accepted
run completed without a threshold breach. This does not lower the global
`500 GiB` retention policy.

Swap/rollback/drop use maintenance and publication advisory locks, a `2s`
lock timeout, a `30s` statement timeout, and no `CASCADE`. The old detached
relation remains until validation. A mismatch or timeout aborts the attempt;
do not lengthen timeouts or retry blindly. Follow the
[pro-bass pilot runbook](../database/ProBassSnapshotRewritePilot.md).

### Completed pro-bass generation subpartition migration

The guarded generation migration completed on 2026-08-18 from repository
commit `f89d444b`. Run
`snapshot-generation-pro-bass-20260818T190019Z` converted
`leaderboard_entries_snapshot_pro_bass` from a regular instrument partition
into `LIST (snapshot_id)` children for protected IDs `1302-1303` plus an empty
default child.

The run archive/restore-proved `8,602,324` rows, retained `5,256,465`, removed
`3,345,859` obsolete `1301` rows, and returned `3,812,192,256` filesystem
bytes. The final tree is `2,214,182,912` bytes in `pg_default`; no
`sgm_pb_*` artifacts remain. Swap time was `0.054` seconds and finalization
was `79.669` seconds. Candidate/original fingerprints, generation
distributions, publication sources, active/projection references, index
attachments/tablespaces, and exact public song/ranking responses matched.

Rename-back rollback ended at final drop. Recovery now uses the independent
read-only archive/evidence package at
`/home/sfenton/fst-temporary/snapshot-generation-pro-bass-20260818T190019Z`;
its archive SHA-256 is
`94d499d94b21dcf17aee0ba3c006590176b17c4dd494c4b2ff8117f2d60c136e`.
Keep the package until a separate deletion decision. The worker remains held
until the generation-aware worker is deployed for the next scrape.

The guarded pro-guitar generation migration then completed on 2026-08-18 from
the same branch. Run `snapshot-generation-pro-guitar-20260818T191034Z`
archive/restore-proved `1,015,961,791` rows across 245 generations, retained
`9,239,429` rows from `1302-1303`, removed `1,006,722,362` rows, and returned
`588,232,740,864` filesystem bytes. The final `4,074,053,632`-byte tree is in
`pg_default`, its default child is empty, and no `sgm_pg_*` artifacts remain.

Swap time was `0.047` seconds. The complete locked original reproof plus final
drop/report took `5,988.277` seconds; 1,158 API-monitor samples had zero
failures and exact public song/ranking hashes remained unchanged. Rename-back
rollback ended at final drop. The independent recovery package is
`/home/sfenton/fst-temporary/snapshot-generation-pro-guitar-20260818T191034Z`;
archive SHA-256 is
`0cd7b95105959dc6618b94c2c283804f3aa1b521645746c94db7d5d35674f476`.
Keep it until a separate deletion decision.

Operator cadence now requires one successful scrape/publication window after
each instrument migration. Before that scrape, deploy the generation-aware
writer so the new snapshot ID receives a dedicated child instead of routing
to the default partition. Hold the worker again after terminal publication
before migrating the next instrument.

Validation scrape `1304` completed that full boundary on 2026-08-19 using
run-once worker image `fstservice:snapshot-generation-a682a16c`. All `8,448`
scope manifests completed and all `603,015` persisted page statuses were
successful. Pro bass routed `1,395,539` rows to its `1304` child and pro guitar
routed `3,674,245`; both default children remained empty across `1,214`
monitor samples.

Publication `92` advanced scrape `1304` after two deferred retries, public
reads unfroze, and representative shell, songs, rankings, pro-bass, and
pro-guitar requests returned HTTP `200`. Player notification run `220` and
band run `221` completed with `67` and `665` events. The post-publication
drain claimed `40` backfill accounts and completed history reconstruction for
`17` accounts before the worker exited `0`; no worker query or registered
queue remains.

Publication-critical projection cleanup and precompute completed. Three
best-effort retention cleanups were safely skipped because active vacuums and
dead-tuple pressure tripped their guard. Do not rerun those cleanups blindly
or treat their skip as permission to overlap maintenance with the next
migration.

Solo Guitar migration and validation scrape `1305` supersede that boundary.
Scrape `1305` completed `8,448/8,448` manifests, published generation `94`,
unfroze reads, completed `62` player and `107` band notification events,
drained registered history work, and exited `0`. Pro Bass, Pro Guitar, and
Solo Guitar `1305` child counts exactly match their published source sums and
all three default children are empty. PostgreSQL OOM recovery used the guarded
resume path; temporary memory headroom was restored to `16 GiB` memory /
`20 GiB` memory-swap after exit. The worker is offline and publication `1305`
is current. `solo-vocals` is the next single target; do not migrate another
instrument in the same scrape interval.

### Completed stale solo rank-index retirement

The guarded `ix_le_song_rank` package removed the exact parent plus nine leaves
on 2026-08-17. Catalog removal was `5,147,222,016` bytes and immediate
filesystem return was `5,147,246,592` bytes. Publication `1302` remained
unfrozen, all monitored public requests succeeded, and unrelated
indexes/constraints and the representative score-index plan remained exact.

The rollback DDL is retained in the checksummed execution evidence and was not
run. Check mode is now idempotent `already_absent`; a partial reappearance must
fail closed.

Any future restore/retirement cycle still requires:

- the exact checksummed check manifest, zero-use observation, and rollback;
- the production Compose project and PostgreSQL system identifier unchanged;
- the standard worker start/recreate host lock acquired nonblockingly;
- publication idle/unfrozen with no working publication;
- `fstworker` offline and no worker/maintenance backend, running scrape/phase,
  waiting lock, target relation lock, or matching active query;
- healthy PostgreSQL, service, web, and full public path;
- retained filesystem and catalog byte evidence before and after.

PostgreSQL 17 does not support concurrent drop of a partitioned parent. The
package uses a normal parent drop with a `2s` lock timeout and `30s` statement
timeout, no `CASCADE`, a shared publication fence, and the exclusive
registration mutation gate. A timeout must leave all ten family members
unchanged. Never drop attached leaves individually or lengthen the timeout to
force the window.

Post-action free space is `64,785,661,952` bytes: `4,392,662,149` above the
single-scrape floor but `56,000,337,654` below preferred two-window headroom.
The worker remains held despite the capacity guard's
`accepted_with_capacity_alert` result. See the
[retirement runbook](../database/StaleSoloRankIndexRetirementRunbook.md).

## Current-publication max-score correction

Use the
[max-score correction runbook](../database/MaxScoreCorrectionMaintenanceRunbook.md)
after the recurring path rule is fixed. Stage is pointer-free. Plan/apply
require a promotion-purpose v4 manifest (discovery is never promotable), the
worker offline, exact publication/catalog/path and notification state,
validated current rollback and staged artifact trees/hashes, the
path-generation/publication lock order, same-drive evidence, and a reviewed
manifest plus plan digest.

Apply owns a `max-score-maintenance:v1:<manifest-sha256>` freeze. Generic
scrape/publication freeze writers cannot overwrite or clear it. A failure
leaves public reads fail-closed and must be continued with the matching resume
command; do not manually unfreeze. Cache publication and freeze release commit
together only after derived, notification, rollback, and rank-history
validation. A `validated` checkpoint also protects its cache staging
generation from ordinary builders/writers; resume and the final locked
transaction require exact key/ETag/JSON-hash parity with immutable database
evidence. Never repair this by clearing the freeze or publishing a different
staging generation.

While that freeze owns the publication lock, max-score-dependent public
requests resolve before publication read-lease acquisition: serve an existing
outer cache, the stable songs cache, or an already-present immutable
current-generation path artifact; otherwise return `503` with
`Retry-After: 30`. This includes a valid previous generation ID retained by a
songs cache warmed before `paths_promoted`; never serve that old immutable
generation and never surface the temporary mismatch as `400` or `500`.
Malformed path identifiers remain invalid input. This exception is
max-score-only; ordinary publication commit/freeze read leases and their
stale-generation error behavior are unchanged.

For a reviewed long-running evidence scan, set the bounded per-command override
documented in the runbook; production currently uses
`Scraper__MaxScoreMaintenanceCommandTimeoutSeconds=1800`. The override keeps
Npgsql cancellation and transaction-local PostgreSQL statement timeouts. In
the final completion transaction it applies only to immutable cache validation
while the `5s` lock timeout remains active, then the server statement timeout
returns to `120s` before cache mutation and unfreeze. A timeout-transition
failure leaves the freeze and durable gate intact. The override does not
authorize running with an active worker, weakening source locks, or clearing a
post-freeze failure. Plan failures identify the evidence stage so operators
can distinguish publication-population, complete score-history, and other
evidence without exposing SQL or credentials.

For an incomplete post-promotion run, use only the canonical max-score
rollback dry-run/execute commands from the runbook. Rollback requires exact
manifest/plan/rollback digests, zero worker/maintenance backends and waiting
locks, the worker offline, and the original digest-owned freeze. It restores
paths atomically, rebuilds complete derived/notification/cache state, records
terminal `rolled_back`, and unfreezes only with exact final validation. Never
replace it with manual path SQL, phase/status edits, cache swaps, gate clearing,
or freeze clearing. A rollback failure keeps the freeze and resumes through the
same command/identities from its durable rollback phase. The executor keeps
the registration/path locks and durable freeze but yields the global
publication lock during long work. It takes that lock transactionally only at
each commit with the existing `5s` lock timeout; contention rolls back that
unit rather than authorizing prolonged public-read queuing. Keep cached and
cold route probes active throughout. A `rollback_captured` run is executable
only when exact current path identity proves promotion committed before the
missing checkpoint.
Scrape allocation remains forbidden in code while the max-score freeze or
durable mutation owner exists, even if the held worker is started accidentally.

Do not choose rollback from an obsolete phase assumption. Re-read the durable
run first. A phase at or after `notifications_quarantined` has already
checkpointed the complete forward derived rebuild and notification alignment;
the reviewed resume path may be materially smaller because it skips those
families and uses commit-only publication fences. Rollback remains the
correctness fallback when current derived validation fails, but it repeats the
full ranking/tier/rivals/band/cache workload.

The accepted publication-1302 phase-5 resume observed an 8.77 GB physical
free-space excursion despite only 584 MB of WAL growth because final validation
used large temporary files. Require at least 16 GiB free for a future
`notifications_quarantined` resume. This does not relax the independent 60.4 GB
next-scrape capacity gate or the 64 GiB full-rollback requirement.

## Service availability

`fstworker`, `fstservice`, and `festivalweb` may be restarted or briefly stopped
for useful maintenance, but recover the public experience promptly. Avoid
leaving the API or web role with worker-only flags, Docker access, or candidate
read ownership.
