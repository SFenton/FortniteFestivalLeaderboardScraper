---
status: living-runbook
owner: operations
last_verified: 2026-09-03
last_verified_commit: e4b892e3
sources:
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationDropSchema.cs
  - tools/FstSnapshotGenerationEvidence/
  - tools/FstSnapshotGenerationDrop/
  - tools/FstSnapshotGenerationRestoreAuthorization/
  - tools/FstSnapshotGenerationRestoreContinuation/
  - tools/postgres-snapshot-generation-drop.sh
  - tools/postgres-snapshot-generation-restore-authorize.sh
  - tools/postgres-snapshot-generation-restore.py
  - tools/postgres-snapshot-generation-restore-continuation.sh
  - tools/postgres-snapshot-generation-restore.sh
  - tools/capture-snapshot-generation-drop-health.py
  - tools/postgres-snapshot-generation-drop-drill.py
  - FSTService.Tests/Unit/SnapshotGenerationDropSchemaTests.cs
  - FSTService.Tests/Unit/SnapshotGenerationDropToolTests.cs
  - FSTService.Tests/Unit/SnapshotGenerationQuarantineSchemaTests.cs
  - tools/testdata/snapshot-generation-live-drop/
update_triggers:
  - Snapshot-generation DROP, quarantine fencing, logical restore, evidence, or rollout gates change.
---

# Snapshot generation DROP and logical restore

This runbook describes a separately gated operator-only capability. It is not
a scheduler, worker feature, API, or authorization to mutate production.
Official confirmation scrape `1333` and cycle `13` are accepted. Production
execution remains blocked until all remaining gates below are independently
accepted.

Q1 operation `1b44941dc5d5ea806dabc2187c3cffed` passed its scrape-1335,
publication-`159` to `162` rotation, cycle-15, and publication-162 soak
evidence. Its first reattach failed transactionally with `42P07` because an
unrelated new public child had reused the quarantined secondary-index name.
At that incident point, no reattachment row or other residue committed; the
exact target remained private at OID/relfilenode `319748510` with its hold and
fences intact. That incident evidence did not itself claim recovery.

An independently approved later DROP attempt also failed closed before DDL
with PostgreSQL `42703`: the live
`snapshot_generation_drop_operations` table was the empty initial additive
revision and lacked the nine semantic projection/catalog/index columns used by
the current function. `CREATE TABLE IF NOT EXISTS` did not alter the existing
table. No drop-operation row or table DROP occurred.

After the schema upgrade and a new approved execution, DROP operation
`333ba4b9fb69dbc098d127f0008ec709` committed under plan digest
`fa45ca20c2c975e543b7d539d3b27cb05c5d80ff16345665205f2355eb67d5dc`.
That committed state is now authoritative: rollback is logical restore, not
reattach. The first restore-plan attempt performed no mutation and emitted no
plan/bundle output because Python incorrectly reserialized the parsed C# plan
and calculated digest
`2536d932d3c0009eb748354f08d221f6a87f9dc49b5529cdb8a932800baaad5a`.
The reviewed H3 validator was then authorized for the exact DROP, but its
first live plan lookup also failed before creating a plan, restore list, or
restore row: the generated PostgreSQL query used reserved word
`authorization` as a table alias. Preserve that immutable unused H3
authorization (`5e807623...`) as failed-plan evidence. H4 corrected that
lookup and passed the authorization query, but its read-only plan then failed
before creating a plan, list, or restore row because the live Q2 catalog
serialized `opclassOids` and `collationOids` as canonical decimal strings
while the Python fixed-shape check compared them directly with integer
arrays. Preserve the unused H4 authorization too. H5 strictly normalizes only
those PostgreSQL OID arrays before supported-shape and attachment-chain
comparison. It requires a third package and authorization for the same DROP;
no database schema migration is needed.

H5 authorization `b20cf7aa76fc8648172db08df58c7ee7` then restored the exact
child successfully. Restore operation `da07c4ce4d07b9692a45a1498313f8b3`
committed with new OID/relfilenode `321906645`, all `8,627` rows and their
ordered SHA-256, exact bound/semantic hashes, and both index chains. The hold
and mutation trigger remain active. Its first route attestation failed before
database write because Python compared raw ZIP bytes for `band-export`.
Authenticated captures differ only in volatile Office package metadata. The
accepted C# canonical ZIP algorithm proves identical semantic hashes for
`band-export`
(`6e84def5c11574cfbfee8803fd61ea13cad160e62d61f216b66b64c611bc966d`)
and `player-export`
(`b2ce01a0f0f5e9e60e844d485622ff8862bb41ba34551813f04a8e9d4ca0f04b`).
Do not modify or rerun H5. H6 is a separate continuation-only C# evidence
tool; it cannot plan, load, attach, or restore.

## Boundaries

- DROP accepts only an immutable active quarantine operation. It accepts no
  schema, relation, SQL, batch, force, or automatic-selection argument.
- The DROP executable has no Docker dependency and runs only as a prebuilt
  assembly whose SHA-256 is supplied through
  `FST_SNAPSHOT_DROP_BINARY_SHA256`.
- The accepted archive and network-none proof, full-scrape evidence, Q1
  rehearsal, Q2 quarantine, route parity, health samples, tool hashes, and
  database identity are copied into a checksummed recovery bundle below the
  canonical FST evidence root.
- The restore path is a separate Python tool. It imports the accepted archive
  module for package, Docker, mount, PostgreSQL 17, and catalog validation. It
  never changes the archive-only command surface.
- Quarantine and backward-compatible reattach repair structurally classify the
  exact PK and score indexes, preserve their OIDs/relfilenodes, and normalize
  them to `sgqi_<full-quarantine-operation-id>_{pk|score}`. Immutable mapping
  evidence makes leaf names auditable without treating them as logical
  identity.
- No command performs row deletion, truncation, cascading DROP, multi-object
  DROP, or automatic retirement.

## Prerequisites

Before any live use:

1. complete independent final implementation review;
2. define and verify any required production caller role/grant procedure;
3. deploy the reviewed schema/tool build without running a destructive
   command;
4. approve and complete the patched reattach of Q1 operation
   `1b44941dc5d5ea806dabc2187c3cffed`, then record exact reattached parity on
   publication `162`; do not repeat the accepted five-hour rotation;
5. complete a new successful post-reattach scrape/publication/cycle that
   observes the child before Q2;
6. verify Docker/service health, PostgreSQL identity, publication/freeze
   state, locks and long queries, FST-drive capacity, CPU, and memory;
7. stop the worker cleanly while leaving the API available;
8. run the separately approved Q2 30-minute/60-sample mandatory-restore
   canary; ensure no unrecovered drop exists and never select Solo Bass
   snapshot `1308`.

Accepted scrape `1333` completed 710 songs, 41,154,968 entries, 608,691
requests, 92,821,715,390 bytes, and 8,520/8,520 manifests across 12
instruments with zero critical, best-effort, writer, retry-exhausted, or
failure-reason outcomes. Publication `157` became current and healthy with 6,390
published solo source bindings and first-attempt completed notifications.
Cycle `13` records exact planner/oracle sets, 111 candidates, 174 protected,
zero blocked/global blockers, and 194,754,322,432 candidate bytes. Production
continued automatically into scrape `1334`.

All evidence, archive, restore-list, and drill paths must stay below:

```text
/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence
```

The repository Compose files are templates. Live ownership remains
`/home/sfenton/Docker/FestivalServiceTracker`.

The partition write helper reads optional hold/drop/restore tables only
through `to_regclass`-gated dynamic SQL, so a rolling startup before those
additive tables exist still permits normal generation creation.

Additive operator schemas require explicit rolling upgrades; a current
`CREATE TABLE IF NOT EXISTS` definition is not a migration. Initialization now
detects any missing DROP semantic column before functions are replaced. The
nine columns and current hash/identity constraints are added only when
`snapshot_generation_drop_operations` is empty. A nonempty pre-semantic table
raises `55000`; operators must not synthesize hashes for committed evidence.
The same audit found four evolved restore-operation fields
(`semantic_catalog_sha256`, `logical_index_shape_sha256`,
`archived_index_names`, and `restored_index_evidence`), which use the same
empty-only upgrade and constraint replacement. Repair authorization adds three
more empty-only identity fields: pinned/executing tool hashes and a nullable
authorization FK. The attestation, finalization,
and hash-chain tables match the initial revision and require no column
upgrade.

The initializer removes both known historical
`fst_restore_snapshot_generation` overloads before creating the
authorization-aware 21-argument function:

- the original live 13-argument signature
  `(text,text,text,bigint,bigint,bigint,text,text,text,text,text,text,jsonb)`;
- the intermediate 16-argument signature
  `(text,text,text,bigint,bigint,bigint,text,text,text,text,jsonb,text,text,text,text,jsonb)`.

These two exact non-cascading `DROP FUNCTION IF EXISTS` statements are the
only function-drop surface. Repeated initialization leaves exactly one current
restore function, with `PUBLIC` execution revoked.

All new DROP/restore functions are `SECURITY INVOKER`, and execution remains
revoked from `PUBLIC`. This repository intentionally adds no role grants.
Before a live canary, define and verify a production-owned caller with the
underlying catalog/table/DDL privileges required by the exact functions. The
isolated restore loader also needs enough temporary privilege to create the
exact archived table/data and the fixed repository-owned constraint/index
shapes in `public`; remove that schema-creation privilege when restoration is
complete.

## Pending Q1 recovery sequence

This is an operator procedure, not authorization to execute it:

1. Reconfirm that publication `162` is still current, idle, unfrozen, and
   notification-complete; the worker is offline; APIs are healthy; and no
   conflicting locks/long queries or production-owned maintenance state exist.
2. Reconfirm operation `1b44941dc5d5ea806dabc2187c3cffed`, private table
   OID/relfilenode `319748510`, both exact index OIDs/relnodes, active hold,
   mutation trigger, child check, validated DEFAULT exclusion, zero DEFAULT
   target rows, and absence of a reattachment row.
3. Deploy the independently reviewed schema and prebuilt quarantine tool.
   Verify the production-owned invoker procedure without granting `PUBLIC`.
4. Rerun only `reattach` with the original sealed Q1 plan/digest and a new
   recovery approval reference. The transaction must create exactly two
   `reattach_repair` mappings, preserve all table/index physical identities,
   move the target with operation-scoped names, attach both index chains, and
   release only the exact hold/fences.
5. On any identity, mapping, publication, lock, or DDL error, stop. Do not
   rename the unrelated Solo Guitar index and do not manually replay partial
   SQL; rollback must leave the original private state.
6. Capture publication-162 routes after reattach and record the `reattached`
   attestation against the accepted publication-162 soak candidate.
7. Only after exact database/route confirmation may the worker resume. Require
   a new successful scrape, publication, and immutable planner/oracle cycle
   that observes the child before planning Q2.

## Select the canary

Build the project explicitly, calculate the DLL SHA-256, then run the wrapper:

```bash
dotnet build tools/FstSnapshotGenerationDrop/FstSnapshotGenerationDrop.csproj \
  -c Release

export FST_SNAPSHOT_DROP_BINARY_SHA256=<approved-dll-sha256>
export FST_SNAPSHOT_DROP_EVIDENCE_ROOT=<new-run-root-below-canonical-evidence>
export FST_SNAPSHOT_DROP_CONNECTION_STRING=<direct-Npgsql-connection>

tools/postgres-snapshot-generation-drop.sh select-canary \
  --output <new-candidate.json>
```

Selection uses current `pg_total_relation_size`, then snapshot ID, instrument,
and child OID. It requires a nonempty candidate in the newest accepted cycle
and excludes Solo Bass `1308`.

## Required Q1 and Q2 evidence

Q1 proves rollback for the exact physical child:

1. archive and network-none prove the selected child;
2. quarantine and attest publication A;
3. keep the child absent through at least one successful full scrape and
   publication rotation;
4. attest soak on publication B, where B differs from A;
5. reattach the same OID/relfilenode;
6. attest the restored result on publication B.

The accepted Q1 operation above may resume at step 5 after the reviewed repair
is deployed. Its immutable rotation and soak evidence remains usable only
while publication `162` remains current, idle, and unfrozen. If that
publication changes before recovery, reattach fails closed and the operator
must adjudicate a new same-publication recovery observation; do not silently
reinterpret the old attestation.

Q2 is a new operation:

1. create a fresh current-cycle archive and network-none proof;
2. quarantine the same exact physical child;
3. retain it for at least 30 minutes;
4. capture at least 60 successful 30-second health samples;
5. keep its cycle and publication unchanged;
6. take fresh same-publication 55-route parity;
7. rerun network-none proof against the pinned archive after the health
   window.

Q2 already passed quarantine's five-cycle gate. Quarantine operations,
retention cycles, and observations are immutable, and DROP rejects any cycle
or publication advancement. The five-cycle prerequisite is therefore
preserved transitively rather than duplicated or weakened.

Each Q1 and Q2 archive/manifests/proofs is independently authenticated.
PostgreSQL custom-format archive bytes are not reproducible, so raw archive
SHA-256 values need not equal across packages. Raw logical-catalog and
stable-config hashes likewise remain immutable provenance but are not
cross-operation equality keys. Their database/system identity,
instrument/snapshot, root OID, child OID/relfilenode, stable-child identity,
exact rows, row SHA-256, physical bytes, versioned name-insensitive semantic
catalog, logical index roles/shapes, and exact index OID/relfilenode plus
root/top OID chains must remain identical. Leaf index names, parent-index
display names, index-backed constraint names, and name-bearing raw
`CREATE INDEX` prefixes are the only excluded fields.

The restore validator authenticates the original C# canonical file bytes; it
does not reserialize the parsed object in Python. The file must contain one
UTF-8 canonical object plus the C# writer's final LF. Every top-level property
must be unique, use canonical key escaping, and be ordinal-sorted, with no
out-of-string whitespace or trailing data. The validator removes only the raw
top-level `planDigest` and `dropOperationId` member byte spans, rejoins the
remaining original members, and hashes those exact unsigned object bytes.
It then derives the operation ID from the fixed canonical
`{planDigest,toolId}` object. Malformed JSON, duplicates, reordering,
whitespace, identity edits, nested edits, and trailing data fail closed. DROP
reports use the same raw canonical-member verification for
`reportSha256`.

## Health evidence

```bash
tools/capture-snapshot-generation-drop-health.py \
  --output <new-health.json>
```

The command records exactly 60 successful 30-second samples. Every sample
requires stable publication identity, idle/unfrozen public state, completed
notifications, no running scrape, no lock waiter, `/readyz` success, and
healthy service state.

## Plan

The `plan` command requires explicit Q1 and Q2 plan/report/attestation paths,
the fresh Q2 archive and proof, health evidence, a fresh pre-DROP route pair,
the fixed repository restore tool and exact restore image ID, capacity
reserve, and new plan/recovery-bundle paths. The sealed bundle records the
filesystem, measurement time, free bytes before and after the copy, copy
bytes, formula result, and reserve. Restore planning and execution measure
free space again and fail before `pg_restore` if the formula plus reserve is
no longer available.

The full option list is emitted by:

```bash
dotnet tools/FstSnapshotGenerationDrop/bin/Release/net9.0/FstSnapshotGenerationDrop.dll --help
```

Planning is read-only against PostgreSQL. It authenticates the upstream files
against immutable database rows, copies recovery inputs into a new
content-addressed bundle (including each proof directory and its checksum and
cleanup evidence), and seals the plan. A path or a manifest declaring itself
accepted is never sufficient. The package checksum inventory authenticates
the actual `catalog.json`, and `manifest.catalog.sha256` must equal that exact
file digest; the immutable quarantine operation separately binds the manifest
bytes. Destructive execution rejects a plan more than ten minutes old or more
than one minute in the future.

Archive catalog compatibility is strict but version-tolerant. PostgreSQL OID
and relfilenode fields may be JSON numbers or canonical ASCII decimal strings,
matching both cycle-14 and cycle-16 package serialization. Optional
`opclassOids`/`collationOids` arrays have the same compatibility. Signs,
whitespace, leading zeroes, non-digits, zero where a positive OID is required,
and values above PostgreSQL's unsigned OID range reject. Counts,
`indNKeyAtts`/`indNAtts`, key attnums, and index options remain JSON numbers
only and are never coerced. The restore tool normalizes accepted OID-array
representations before both PostgreSQL-17 fixed-value checks and exact
child/root/top attachment-chain equality. Cycle-14 catalogs may omit the
optional index metadata; cycle-16 catalogs must validate it when present.

## DROP

After separate approval:

```bash
tools/postgres-snapshot-generation-drop.sh drop \
  --plan <plan.json> \
  --expected-plan-digest <sha256> \
  --expected-operation-id <id> \
  --approved-by <operator> \
  --approval-reference <distinct-approval> \
  --output <new-drop-report.json>
```

Both the DROP approver and reference must be independent of the Q1/Q2
quarantine approvals; the reference also cannot reuse the Q1 reattach
reference, plan digest, or operation identity.

The executable obtains the existing six advisory locks followed by the
dedicated drop lock before opening a serializable transaction. The database
function repeats all seven locks without waiting. It locks only the exact
DEFAULT child in `SHARE` mode and the exact private child in
`ACCESS EXCLUSIVE` mode. The existing deterministic Q2 DEFAULT constraint is
retained without rename, so the transaction takes no stronger DEFAULT lock
and no top/root, recursive-tree, or sibling-child relation lock.

Inside the transaction it repeats cycle, publication, notification, worker,
liveness, writer-failure, hold, DEFAULT, identity, row, topology, and
dependency checks. After the private child holds `ACCESS EXCLUSIVE`, it
recomputes the exact detached PK/score inventory and requires exactly two
role/OID/relfilenode/name matches against the active operation's immutable
`snapshot_generation_quarantine_index_renames` rows. A renamed, rebuilt,
reindexed, role-swapped, extra, or missing index fails before evidence or DROP
with the private relation, hold, and DEFAULT fence unchanged. It records the
existing Q2 DEFAULT exclusion as the durable fence, validates and persists the
supplied pre-DROP route count/status/semantic/difference values, writes
immutable evidence, and executes exactly one:

```sql
DROP TABLE fst_snapshot_quarantine.<derived-name> RESTRICT;
```

There is no `IF EXISTS` or `CASCADE`. The hold and DEFAULT exclusion remain,
and the committed operation is an independent write-path tombstone if the
hold is ever released incorrectly.

## Unknown commit

Never repeat DROP merely because the client did not receive the commit
acknowledgement:

```bash
tools/postgres-snapshot-generation-drop.sh confirm \
  --plan <plan.json> \
  --expected-plan-digest <sha256> \
  --expected-operation-id <id> \
  --confirmed-by <operator> \
  --confirmation-reference <incident-or-run-reference> \
  --output <new-confirmation.json>
```

- operation row present and old name/OID absent: committed;
- operation row absent and exact private OID present: not committed;
- any mixed state: stop without mutation.

## Logical restore

### Repair-tool authorization

The committed DROP continues to pin the original restore tool and immutable
recovery bundle. A validator defect is repaired only through the separate
no-Docker authorizer and a tool-only repair package; neither the DROP row nor
the original bundle is changed or duplicated.

Prepare the package after building the final authorization-aware restore tool
and authorizer:

```bash
export FST_SNAPSHOT_RESTORE_AUTHORIZATION_EVIDENCE_ROOT=<FST-drive-run-root>
export FST_SNAPSHOT_RESTORE_AUTHORIZER_BINARY_SHA256=<approved-authorizer-dll-sha256>

tools/postgres-snapshot-generation-restore-authorize.sh \
  prepare-repair-package \
  --drop-plan <drop-plan-v2.json> \
  --drop-report <drop-report-v2.json> \
  --original-bundle <recovery-bundle-v2> \
  --expected-drop-plan-digest <fa45...d5dc> \
  --expected-drop-operation-id <333b...c709> \
  --validator-base-tool <reviewed-acb358-tool> \
  --pinned-to-base-diff <reviewed.patch> \
  --base-to-final-diff <reviewed.patch> \
  --source-manifest <checksummed-source.json> \
  --test-evidence-manifest <checksummed-tests.json> \
  --test-results <test-results.json> \
  --output <new-tool-only-repair-package>
```

The exact package contains only the final restore tool, the archive helper
copied byte-for-byte from the original bundle, source/diff/test evidence, its
repair manifest, and `SHA256SUMS`. It contains no archive, TOC, catalog,
proof, credential, or copy of the original recovery bundle.
Preparation refuses a dirty or uncommitted repository so the recorded commit,
tree ID, source manifest, final replacement tool, and reviewed diffs describe one exact
source state.

After the post-DROP soak is accepted, authorize with distinct operator and
reviewer identities:

```bash
export FST_SNAPSHOT_RESTORE_AUTHORIZATION_CONNECTION_STRING=<direct-Npgsql-connection>

tools/postgres-snapshot-generation-restore-authorize.sh \
  authorize-repair-tool \
  --drop-plan <drop-plan-v2.json> \
  --drop-report <drop-report-v2.json> \
  --original-bundle <recovery-bundle-v2> \
  --expected-drop-plan-digest <fa45...d5dc> \
  --expected-drop-operation-id <333b...c709> \
  --repair-package <tool-only-repair-package> \
  --reason-code pinned_restore_validator_defect \
  --reason-text <reviewed-reason> \
  --approved-by <operator-not-drop-approver> \
  --reviewed-by <independent-reviewer> \
  --approval-reference <new-approval> \
  --output <authorization-report.json>
```

On uncertain commit, use `confirm-repair-tool`; never insert another
authorization blindly. Authorization has no hard expiry. Every consumer emits
a warning after 24 hours, but once a restore row consumes the authorization,
age cannot strand confirm, attestation, or finalization.

Authorization stores two distinct evidence digests. `evidence_sha256` is the
C# canonical request digest. PostgreSQL independently computes
`canonical_evidence_db_sha256` from the stored JSONB text representation. The
deterministic authorization ID hashes both digests plus the complete
drop/plan/bundle/old-base-final/helper/authorizer/package/repository/diff/source/
test chain. Confirmation recomputes that ID; it never compares incompatible
C# and JSONB encodings as if they were identical.

Restore planning requires:

```text
--authorization-id <32-hex>
--repair-package <tool-only-package>
--expected-repair-package-manifest-sha256 <sha256>
```

The original bundle tool must still equal the old pin; the executing tool must
equal the authorized final replacement hash; the archive helper must match
both packages.
The authorization is re-read during planning, immediately before
`pg_restore`, and immediately before SQL attach. The immutable restore row
stores pinned/executing hashes and the consumed authorization FK.

Historical restore build identities:

- validator base: `acb358604d9f642da3d4809581328f76118cb912c32765353b8594cc68a1522d`;
- historical H3 whose immutable authorization failed during read-only
  planning:
  `032a86272bdcf0e2586376d3f34f1fb5b89b77b2fb904c966aebc6eec97eff91`;
- corrective H4 with the PostgreSQL-safe `auth_row` lookup:
  `297a8118c2ad9e62cb12d1dccc62ee81a5b57ea57885329b365b6ac6bf1e62dd`;
- corrective H5 with strict PostgreSQL OID-array normalization:
  `e9a2214f57efe24ff29a60a7721a487e8d14b4508a84de69dea180a80dbe14cc`;
- original/final archive helper:
  `f1d9f9169169d60ff16b703fce7dca79784fc50fcf211374e1a63e46c08c3eeb`;
- authoritative H4 clean-commit authorizer DLL:
  `fb7b0fb77fc2fe17fb781432dd6cd707d24126320d78976d40ac3a72b5da819e`.
  H5 restore authorization and execution are now immutable committed evidence.

Generate a restore plan from the committed drop and pinned bundle:

```bash
tools/postgres-snapshot-generation-restore.sh plan \
  --drop-plan <drop-plan.json> \
  --drop-report <drop-report.json> \
  --expected-drop-plan-digest <sha256> \
  --expected-drop-operation-id <id> \
  --restore-list <new-restore.list> \
  --postgres-image <exact-postgresql-17-image-id-or-tag> \
  --output <new-restore-plan.json>
```

`--drop-report` may name the sealed `drop` report or, after an uncertain
acknowledgement, the sealed committed-state `confirm` report.
The restore list must contain exactly the selected child's `TABLE`, `TABLE
DATA`, primary-key `CONSTRAINT`, and secondary `INDEX` entries. Parent-table,
`TABLE ATTACH`, and `INDEX ATTACH` entries are excluded. Those four selected
entries are authenticated provenance; the executable restore list contains
only `TABLE` and `TABLE DATA`.

The accepted production-derived disposable probe selected TOC IDs
`820/5466/5311/5312` for Pro Cymbals snapshot `1314`. It restored `8,627`
rows with SHA-256
`89bb111ca53eb905c344f113a3668102b8ad9a0fc5581cb585d6fb5004a81c29`,
then attached with logical catalog SHA-256
`56a2d37dc79b255d1af8d4c11327cf543f2970bc04d07659e8f36b190e535597`
and both required index links. The restored OID/relfilenode changed from
`319748510` to `16414`, which is expected after physical deletion.

Execution requires an operator-provided mode-`0600` regular pgpass file:

```bash
export FST_SNAPSHOT_RESTORE_PGPASSFILE=<regular-secret-file>

tools/postgres-snapshot-generation-restore.sh restore \
  --plan <restore-plan.json> \
  --expected-plan-digest <sha256> \
  --expected-operation-id <id> \
  --postgres-image <same-approved-image> \
  --execute \
  --restored-by <operator> \
  --restore-reference <approval> \
  --output <new-restore-report.json>
```

The restore reference must be a separate approval from the DROP reference.
The PostgreSQL 17 client container receives no Docker socket. It restores only
the authenticated table and table-data entries through
`pg_restore --single-transaction`; archived PK/index DDL is never executed.
Before mutation, the authenticated archive index specifications must match the
fixed supported btree shapes exactly and reject expressions, predicates,
INCLUDE columns, non-default opclass/collation/options/tablespace, alternate
ordering, or extra indexes. The table remains detached while row and
name-insensitive semantic catalog evidence is checked. A short database
transaction then creates:

```text
sgri_<full-restore-operation-id>_pk
sgri_<full-restore-operation-id>_score
```

The first is a fixed unique btree over
`(snapshot_id,song_id,instrument,account_id)` and is promoted to a same-named
primary-key constraint with `USING INDEX`; the second is a fixed btree over
`(snapshot_id,song_id,instrument,score DESC)`. The transaction attaches the
table to the exact root, verifies both index chains, removes only the
temporary exact-child check and durable DEFAULT exclusion, and records the
archived names plus new names/OIDs/relfilenodes. Existing unrelated objects
with archived names are neither renamed nor dropped. The original OID cannot
and need not be preserved.
The restore mutation trigger and retention hold both remain active while route,
row-fingerprint, and logical-catalog attestation runs; finalization removes the
trigger and releases the hold atomically.
After finalization, write-path identity matches the recorded restored OID.
The stored relfilenode remains historical evidence and may legitimately change
after `VACUUM FULL`, `CLUSTER`, or a supported repack.
`pg_restore --single-transaction` prevents partial archive loads. If a later
validation or attach step fails, the exact detached relation remains
hold-protected and can be revalidated and resumed; it is never broadly cleaned
or overwritten. The labelled client container is removed and its absence is
verified after success, failure, or an uncertain client exit.

After a committed restore, evidence acceptance is owned by the separate
continuation tool. The physical restore plan/report/package and predecessor
authorization remain unchanged. Prepare a clean-commit continuation package:

```bash
tools/postgres-snapshot-generation-restore-authorize.sh \
  prepare-continuation-package \
  --restore-plan <restore-plan-v5.json> \
  --restore-report <restore-report-v5.json> \
  --predecessor-repair-package <repair-package-v5> \
  --recovery-bundle <recovery-bundle-v2> \
  --baseline-route-manifest <routes-post-drop/manifest.json> \
  --post-restore-route-manifest <routes-post-restore/manifest.json> \
  --candidate-route-manifest <routes-post-restore-repeat/manifest.json> \
  --service-runtime-isolation-evidence <service-runtime-isolation.json> \
  --historical-service-build-evidence <historical-service-build-evidence.json> \
  --stabilized-service-build-evidence <stabilized-service-build-evidence.json> \
  --predecessor-to-continuation-diff <reviewed-repository.diff> \
  --source-manifest <source-manifest.json> \
  --test-evidence-manifest <test-evidence-manifest.json> \
  --test-results <test-evidence-results.json> \
  --expected-plan-digest <149b...12e0> \
  --expected-operation-id <da07...f8b3> \
  --output <new-continuation-package>
```

The authorizer validates all three route checksum trees with
`QuarantineEvidenceValidator`. The post-restore and repeated post-restore
captures must still pass the unmodified strict 55-route validator with zero
differences. The historical post-DROP to post-restore pair may use only the
fixed `fst.shop-daily-inventory-rollover.v1` bridge. That bridge is accepted
only when:

- publication and published scrape remain exact;
- exactly one UTC midnight is crossed;
- the authenticated shop `lastUpdated` moves from before that midnight to
  after it, remains no later than its capture, and is unchanged in the
  stabilized pair;
- `shop:semantic-json` is the only historical route difference;
- the shop count remains `117`, with exactly `100` removals, `100` additions,
  and `17` overlapping songs;
- the removed set exactly equals the baseline `leavingTomorrow` set;
- all overlapping entries are identical;
- all arrivals and departures exist with matching public metadata in the
  unchanged `/api/songs` catalog;
- shop URLs reproduce from the catalog title and song identifier;
- `newSongs` is empty and `isNew` is false throughout; and
- the post-restore repeat remains strict zero-difference parity.
- the stabilized baseline and candidate are distinct manifests with increasing
  `capturedAtUtc`, and both authenticate the same shop refresh timestamp no
  later than their own capture.

The package binds the exact historical and stabilized manifest/checksum
hashes, the hash-only rollover census, an exact restore-write-scope proof
derived from the H5 plan/report, two immutable service-image build records,
and a sealed service-runtime isolation file. The runtime file identifies both
service images and DLLs, binds the historical and stabilized source commits,
and hashes the reviewed shop source files at both commits and the current
tree. The authorizer derives the no-snapshot dependency from those fixed
sources instead of trusting an operator assertion. No route, field, count, or exception is
operator-configurable. A future temporal exception requires a new reviewed
implementation and authorization; changing either capture invalidates this
authorization. The package contains no route/export body, song identifier,
archive, or H5 executable.

Build/runtime evidence inputs are read into memory once before validation.
Those exact bytes, not a later path re-read, are written to the package.
Package validation independently parses the packaged build records and runtime
record and rejects any mismatched image, DLL, commit, worktree state, timing,
or build-file hash even if an attacker recomputes the outer package
checksums.
Authorize and confirm with a new approval:

```bash
tools/postgres-snapshot-generation-restore-authorize.sh \
  authorize-continuation-tool \
  --restore-plan <restore-plan-v5.json> \
  --restore-report <restore-report-v5.json> \
  --continuation-package <continuation-package> \
  --expected-continuation-package-manifest-sha256 <sha256> \
  --expected-plan-digest <149b...12e0> \
  --expected-operation-id <da07...f8b3> \
  --reason-code post_restore_route_parity \
  --reason-text <reviewed-reason> \
  --approved-by <operator> \
  --reviewed-by <independent-reviewer> \
  --approval-reference <new-reference> \
  --output <authorization-report.json>

tools/postgres-snapshot-generation-restore-authorize.sh \
  confirm-continuation-tool \
  --restore-plan <restore-plan-v5.json> \
  --continuation-package <continuation-package> \
  --expected-continuation-package-manifest-sha256 <sha256> \
  --expected-plan-digest <149b...12e0> \
  --expected-operation-id <da07...f8b3> \
  --continuation-authorization-id <id> \
  --output <authorization-confirm.json>
```

Then export the direct connection and reviewed binary hash and run:

```bash
export FST_SNAPSHOT_RESTORE_CONTINUATION_CONNECTION_STRING=<direct-connection>
export FST_SNAPSHOT_RESTORE_CONTINUATION_BINARY_SHA256=<reviewed-H6-sha256>

tools/postgres-snapshot-generation-restore-continuation.sh confirm ...
tools/postgres-snapshot-generation-restore-continuation.sh attest ...
tools/postgres-snapshot-generation-restore-continuation.sh finalize  ...
```

All three commands require the exact package, original plan/report identities,
and one continuation authorization. Route paths are package-pinned rather
than accepted from the continuation CLI. Attestation records the strict
post-restore-to-repeat pair, semantic binary parity, algorithm ID, route
semantic evidence hash, temporal-bridge evidence hash, H6 tool hash, and
authorization ID. The stabilized semantic-evidence hash is distinct from the
preflight file hash and both are authorization-bound. The historical pair is
attribution evidence and is never
recorded as zero-difference parity. Finalization must use the same
tool/authorization and matching temporal-bridge hash; only its transaction
removes the trigger and releases the hold. Unknown commits are reconciled
with `confirm`, never by rerunning physical restore.

The Python physical-restore tool no longer owns evidence acceptance. Invoking
its legacy `attest` or `finalize` command fails immediately with a deterministic
redirect to `tools/postgres-snapshot-generation-restore-continuation.sh`.
Those redirects run before argument parsing, route reads, output creation, or
database access; the old 9-/4-argument SQL function names are absent from the
Python source.

### Accepted H6 continuation canary

The first mandatory restore continuation is accepted. The immutable identities
are:

- repository commit `83b75e3fb09461ca907d80e32f45911c4d0d3b86`;
- candidate service image
  `sha256:102d08fdfb608f07620e03eab7317bb7cbc93295a22cf0182b868399f87b26d3`;
- continuation package manifest
  `b1751ebfc56db36eb6268b2bf149ffdb47d832bc7c9db192885680d36dbafeeb`;
- continuation authorization `0ed3cd7125af6fdf8748915318b0893d`;
- continuation tool
  `d5f75c9744ca983bb9c81bb1d2d2de19460b9818d13b2191536e4b03d13572e0`;
- shared evidence assembly
  `a8ddccdcf4139bf9c7883c4e1ea546138372ecb9778593f61a0ba5b285ed765a`;
- temporal bridge
  `1a771bf076394800e4e27c1096c2ee331546358af95f646b53276b08954b703d`;
- stabilized route semantics
  `37b55f7580f69a86b10a00c9140438bc440a5f517cc71348cf3a0b95f02cb972`.

The live additive schema exposed only the 40-argument authorization,
13-argument attestation, and 6-argument finalization functions, all
`SECURITY INVOKER` with `PUBLIC` execution revoked. Attestation recorded the
strict post-restore pair on publication `165`/scrape `1336`. Finalization then
inserted the authoritative
`snapshot_generation_restore_finalizations.finalized_at` row, removed the
mutation trigger, and released hold 3 atomically. The legacy nullable
`snapshot_generation_restore_attestations.finalized_at` column remains null;
it is not the finalization authority.

Candidate confirmation scrape `1337` completed with 710 songs, 41,182,325
entries, 609,135 requests, 92,896,641,917 bytes, zero writer failures, and
zero best-effort failures. Publication `171`, improvement notification
recovery, all 8,520 scope manifests, and report-only cycle `17` completed.
Cycle 17 had exact planner/oracle agreement, 122 candidates, 199 protected
children, zero blockers, and zero global blockers. It observed Pro Cymbals
snapshot `1314` as a candidate at restored OID/relfilenode `321906645`.
Scrapes `1338` through `1341` also completed with zero best-effort failures.

The hash-only terminal acceptance bundle is
`continuation-acceptance-v2/acceptance-manifest.json`, SHA-256
`0ee12d9e9c6d0e2dd8230eca359b0a807106ef128698c9e83ef203756bea3f56`.
PR #75 merged the DROP/restore tier as master commit `3bf8c6e3`. The official
service/worker image
`sha256:6a432ac73fcca49aa6a3caf5d049eaa953d2ea7ca79ed79219230255afdfc5b5`
and web image
`sha256:d0174bcc79e00b91155db143c2421cc26d99352ab4f0cf9e0d5f684bf6957b84`
were deployed at a terminal boundary.

Official scrapes `1343` and `1344` completed their core scrape, writer,
publication, notification, and report-only retention paths, but each was
rejected as the final confirmation because the unrelated
`RegisteredBandTargetedProcessing` best-effort phase timed out after `300 s`
with `0/10` units. The defect counted only successful lookups against the
ten-band pass cap and ordered the persisted status `failed` even though the
stored value is `error`.

PR #76 corrected attempted-band budgeting and pending-before-error ordering,
then merged as master commit `e4b892e3`. Local candidate scrape `1345`
completed the targeted phase in `5.333779 s`; official-image scrape `1346`
completed it in `6.319659 s`. Both processed exactly the next ten pending
bands, published with zero best-effort/writer failures, completed
notifications, passed their 55-route published captures, and produced
oracle-agreeing report-only cycles `25` and `26` with zero blockers.
Publication `190` now serves scrape `1346`.

The restored Pro Cymbals snapshot `1314` remains attached with `8,627` rows at
OID/relfilenode `321906645`; planner and oracle both classify it as an
unreferenced candidate under the current publication. The single-child
DROP/restore tier is therefore terminally accepted and deployed. Automatic
and multi-child pruning remain disabled and require their own rollout gate.

## Acceptance and rollback

The first live DROP canary is intentionally restored after at least 30 minutes
of successful post-DROP monitoring. Acceptance requires:

- immutable drop and restore rows;
- no unexplained dependency or catalog change;
- exact row SHA-256 and name-insensitive semantic catalog, with raw catalog
  hashes retained as provenance;
- exact original schema/name/bound and both index chains, with a new physical
  OID/relfilenode;
- 54 unchanged historical routes plus the exact hash-pinned midnight shop
  rollover attribution above;
- a separate zero-difference 55-route post-restore stabilization attestation;
- no service readiness, lock, resource, notification, or publication
  regression;
- a successful candidate scrape and exact planner/oracle cycle;
- after promotion, a separate official-image confirmation scrape.

Before DROP, rollback is ordinary Q2 reattach. After committed DROP, rollback
is logical archive restore only. Any mixed commit state, evidence mismatch,
unexplained relation, restore mismatch, or health regression blocks promotion.
Automatic retirement and multi-child execution remain out of scope.
