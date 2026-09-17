---
status: decision
owner: data
last_verified: 2026-08-30
last_verified_commit: 21d7193c
sources:
  - docs/database/SnapshotGenerationRetentionSafety.md
  - docs/database/SnapshotGenerationDropRunbook.md
  - FSTService/Persistence/Maintenance/SnapshotGenerationDropSchema.cs
  - FSTService/Persistence/DatabaseInitializer.cs
  - tools/FstSnapshotGenerationEvidence/
  - tools/FstSnapshotGenerationDrop/
  - tools/FstSnapshotGenerationRestoreAuthorization/
  - tools/postgres-snapshot-generation-restore.py
update_triggers:
  - Snapshot-generation destructive retention, quarantine rollback, restore, or automation boundaries change.
---

# ADR 0007: Isolate snapshot-generation DROP and logical restore

## Decision

Implement snapshot-generation DROP as a separate operator-only executable and
additive evidence schema. Do not add destructive commands to the accepted
quarantine executable or automatic worker.

DROP accepts only an exact active quarantine operation. A distinct Q1
operation must first prove quarantine across publication rotation and exact
reattachment. Q2 then requires a fresh current-cycle archive/proof, a
30-minute/60-sample unchanged-publication soak, explicit independent approval,
and complete transaction-boundary revalidation.

The transaction retains the active hold and the already validated Q2 DEFAULT
exclusion under its existing deterministic name. It locks the DEFAULT child
only in `SHARE` mode and only the private target in `ACCESS EXCLUSIVE` mode;
it does not lock the top table, instrument root, or sibling children. The sole
destructive statement is `DROP TABLE <exact private child> RESTRICT`.

Restore is a separate tool using the accepted logical archive. It restores
and authenticates the child table/data/primary constraint/secondary index,
but executes only table and table data while detached. Archived index DDL is
never executed. After exact fixed-shape validation, repository-owned SQL
creates deterministic `sgri_<full-restore-operation-id>_{pk|score}` indexes
and promotes the PK with `USING INDEX`; a short transaction then attaches the
table. A new OID and relfilenode are expected; row, name-insensitive semantic
catalog, bound, and index-topology parity are mandatory. The restored child
keeps its mutation guard and hold through
attestation; finalization removes the guard and releases the hold atomically.
Later physical rewrites may change the recorded relfilenode while preserving
the restored relation OID; write-path admission therefore authenticates the
finalized restored OID and treats relfilenode as historical evidence.

After a committed DROP, a pinned restore-tool defect may be repaired only by
an additive immutable authorization linked to that exact DROP. A separate
authorizer creates a tool-only package and records old pin, reviewed validator
base, final executing hash, original bundle, helper, repository/diff/source,
test, and dual-approval provenance. The original DROP row and bundle remain
unchanged. Restore operations persist pinned/executing hashes and consume at
most one authorization through a composite FK. Authorization has no hard
expiry; age warns without stranding post-restore phases.
The migration removes the exact known 13- and 16-argument restore-function
overloads before creating the new signature. Authorization identity is
derived from both the
client canonical digest and an independently computed PostgreSQL JSONB digest
plus the complete substantive provenance chain.
One shared authorization resolver serves plan, restore, confirm, attest, and
finalize. Its generated SQL uses the explicit `auth_row` table alias. The
first authorized H3 plan lookup failed before output or mutation because the
former alias `authorization` is a PostgreSQL keyword in that context. The H3
authorization remains immutable and unused; H4 used a separate package and
authorization for the same DROP. No schema change was required.
H4 then failed at a later read-only boundary because the Q2 archive's
opclass/collation OID arrays use canonical decimal strings. H5 applies the
same strict integer-or-canonical-string parser as the reviewed C# evidence
path, normalizes only those arrays for fixed-value and attachment-chain
comparison, and leaves all other numeric fields number-only. H4 remains
immutable unused evidence; H5 used a third authorization, still without a
schema change.

H5 later committed the exact mandatory restore, but its Python route
attestation compared volatile ZIP bytes and stopped before evidence insertion.
Post-restore acceptance is now a separate continuation-only C# tier. It calls
the already accepted shared `QuarantineEvidenceValidator`, stores detailed
semantic binary evidence, and has no plan/load/attach/restore surface. A new
immutable authorization links the exact restore and predecessor H5
authorization to one continuation package/tool/evidence assembly and route
evidence set. The post-DROP historical pair may differ only on `/api/shop`
through the exact-manifest-pinned daily inventory rollover predicate; its
hash-only census proves the announced departures, catalog-consistent
arrivals, unchanged overlaps, one UTC midnight, and an authenticated shop
refresh timestamp that moves across that boundary. A distinct post-restore
pair must remain strict zero-difference 55-route parity. Attestation records
the stabilized pair plus its own semantic-evidence hash and the historical
bridge hash; the preflight-file hash remains separate. Both service identities
are supported by immutable build records and matching reviewed shop-source
hashes. Finalization requires
the same authorization and bridge before releasing the hold and trigger.

This separation is preferred over relaxing the pre-restore authorization or
adding continuation behavior to Python: lifecycle authorization remains
one-way, ZIP behavior is shared by construction, and the evidence tool is
structurally unable to repeat physical restore. The bridge is not a general
route-exception mechanism: route, predicate, census, captures, restore scope,
and runtime identities are fixed and hash-bound.

The first use of this continuation tier is accepted. Authorization
`0ed3cd7125af6fdf8748915318b0893d` bound the exact package, temporal bridge,
stabilized route hash, restore scope, and runtime identities. Finalization
released the hold and removed the restored-child mutation trigger without
changing its OID, rows, bound, or index chains. Scrape `1337`, publication
`171`, notifications, and cycle `17` then proved the restored generation under
ordinary worker operation. This accepts the manual single-child recovery
tier; it does not authorize permanent deletion or automatic pruning.

Quarantine structurally classifies the existing PK and score indexes and
renames those same OIDs to
`sgqi_<full-quarantine-operation-id>_{pk|score}` after detach and before the
schema move. Immutable evidence records old/new names and constraint names,
physical identity, semantic before/after projections, phase, and transaction
identity. Reattach verifies the mapping or applies the same normalization as
an atomic compatibility repair for pre-change operations. It never renames,
drops, or rebuilds an unrelated conflicting public index.

Leaf index names and index-backed constraint names are non-semantic. Each
archive's raw archive, logical-catalog, and stable-config hashes remain
independently authenticated provenance, but are not Q1/Q2 cross-package
equality keys. Equality instead binds stable child identity, exact
rows/table identity, a versioned name-insensitive catalog/index projection,
and exact leaf/root/top physical OID topology.

## Rationale

- Separate binaries preserve the proven archive-only and quarantine-only
  command surfaces.
- Quarantine makes public behavior observable while rollback still preserves
  the original physical relation.
- Retaining the hold and DEFAULT exclusion prevents metadata loss and ghost
  rows after irreversible DROP.
- An atomic immutable drop row makes a lost commit acknowledgement
  reconcilable.
- `RESTRICT` delegates unknown dependency refusal to PostgreSQL while the tool
  records and validates a complete dependency inventory.
- Avoiding top/root table locks preserves the isolation advantage of dropping
  an already-detached private table.
- Q2 inherits quarantine's five-cycle gate: its operation and source
  cycle/observation are immutable, and any cycle/publication advancement
  rejects DROP.
- All new database functions are `SECURITY INVOKER`; execution remains
  revoked from `PUBLIC`, and this change intentionally provisions no grants.
- Operation-scoped target names close both the public-to-private quarantine
  collision and the private-to-public reattach collision while preserving
  table/index OIDs and relfilenodes.
- Fixed restore DDL prevents archived textual SQL from becoming an arbitrary
  execution surface and makes unrelated archived-name collisions harmless.

## Alternatives

### Extend the quarantine executable

Rejected. It would invalidate the accepted structural guarantee that the
quarantine tool cannot destroy data.

### Drop immediately after quarantine

Rejected. It removes the independent approval and soak boundary.

### Preserve the old OID with physical file copying

Rejected. Relation files are not a supported independently restorable
PostgreSQL object format. Logical restore is portable and already
network-none-proven.

### Rename the unrelated public index

Rejected. It mutates a live object outside the approved target, adds avoidable
lock and rollback scope, and merely transfers the collision risk.

### Rebuild the target indexes

Rejected for quarantine/reattach. `ALTER INDEX ... RENAME` preserves the
accepted index OIDs/relfilenodes and avoids data/index rebuild cost. Logical
restore necessarily creates new index identities after physical deletion and
therefore uses fixed deterministic DDL instead.

### Change all generation index naming globally

Rejected for this incident tier. It expands worker/schema behavior and does
not repair already-quarantined operations. The operation-scoped target
normalization is narrower and backward compatible.

### Automatically retire candidates

Rejected for this tier. One explicitly approved child is the maximum scope.

## Consequences

- A committed drop is terminal for its quarantine operation; reattach and old
  quarantine attestations are rejected.
- Generation creation checks active retention/restore holds and committed
  DROP tombstones before returning or creating a child. A restored child
  remains unavailable to writers until its attestation and finalization.
- DEFAULT exclusion constraints accumulate for permanently dropped children;
  consolidation is a separate measured scalability decision.
- The first live canary must restore the child before permanent-drop
  promotion.
- Q1 operation `1b44941dc5d5ea806dabc2187c3cffed` was recoverable after its
  initial collision failed transactionally; its accepted rotation/soak did
  not require a second five-hour rehearsal.
- Additive evidence schemas require explicit empty-table ALTER upgrades.
  `CREATE TABLE IF NOT EXISTS` alone cannot evolve existing operator tables;
  missing semantic columns with committed rows fail closed rather than
  receiving synthesized provenance.
