---
status: canonical
owner: data
last_verified: 2026-09-04
last_verified_commit: afb562d2
sources:
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetirementSchema.cs
  - tools/FstSnapshotGenerationRetirement/
  - tools/postgres-snapshot-generation-retirement.sh
  - tools/postgres-snapshot-generation-retirement-drill.sh
  - FSTService.Tests/Unit/SnapshotGenerationRetirementPlanTests.cs
  - docs/database/SnapshotGenerationRetentionSafety.md
  - docs/decisions/0008-snapshot-generation-retirement-plan-control-plane.md
update_triggers:
  - Retirement policy, plan selection, reconciliation, command boundaries, or later execution stages change.
---

# Snapshot generation retirement plan control plane

## Capability boundary

`FstSnapshotGenerationRetirement` is a separate host-run executable. It is not
registered in `fstservice` or `fstworker`, is not an HTTP surface, and does not
receive Docker access.

The first accepted slice is deliberately **plan-only**. It can:

- report the observed code/database identity and durable state with `status`;
- create one bounded immutable plan authorization with
  `authorize-policy-epoch`;
- select and persist one deterministic largest eligible child with
  `plan-cycle`;
- expire or supersede stale plans with `reconcile`;
- immediately stop an active policy with `deactivate-policy-epoch`.

It cannot archive, prove, detach, rename, quarantine, drop, truncate, delete,
restore, start or stop a worker, accept a target relation, accept SQL, or
control more than one planned job. It creates no filesystem artifacts and
does not mutate a snapshot child or any publication-owned source data.

The honest capability claim emitted by every command is:

```text
largest-first planning only; no archive, detach, quarantine, drop, delete, truncate, restore, worker lifecycle, or steady-state storage claim
```

## Durable state

The existing report-only planner schema and evidence bytes are unchanged. The
schema initializer adds four separate control-plane relations:

| Relation | Purpose |
|---|---|
| `snapshot_generation_retirement_policy_epochs` | Immutable, time-bounded, identity-pinned planning authorizations |
| `snapshot_generation_retirement_control` | Default-off singleton and active-policy pointer |
| `snapshot_generation_retirement_jobs` | One immutable target with only `planned -> expired/superseded` transitions |
| `snapshot_generation_retirement_events` | Append-only per-policy SHA-256 event chain |

Policy and event rows reject update, delete, and truncate. Job identity rejects
mutation, deletion, and truncation. A job may leave `planned` only once and
must record a terminal reason and timestamp. A partial unique index permits at
most one globally planned job.

The database insert trigger independently requires:

- the enabled singleton to point at the job policy;
- the policy window and aggregate job/byte budgets to admit the job;
- the newest retention cycle to be planner v3/config v1, report-only,
  `observed`, oracle-agreeing, and free of global or child blockers;
- the planner/oracle child, live, and candidate sets to match exactly;
- the target observation to remain a non-live candidate;
- the target scrape to remain completed, with no active hold or unreplayed
  writer failure, and the scraper worker to report no current operation;
- the current publication and published scrape to match the cycle while reads
  are idle and unfrozen, with notifications in the canonical completed or
  disabled terminal state;
- the canonical root/child names, parent attachments, partition keys/bounds,
  tablespaces, relation options, index inventories, OIDs, relfilenode, and
  current `pg_total_relation_size` to match immutable planner evidence;
- Solo Bass snapshot `1308` to remain excluded.

All control SQL sets `search_path` to `pg_catalog,public` and qualifies control
relations and catalog functions. Plan/reconcile transactions participate in
the existing shared publication, retention-planner, and supported
snapshot-partition DDL advisory-lock protocol, then hold the publication-state
row plus the mutable hold, writer-failure, and worker-status surfaces through
commit. Control mutations take row locks in one order: control, policy, planned
job. Before insertion or a current-job result, `ONLY` the exact root is locked
in `SHARE ROW EXCLUSIVE` mode and `ONLY` the exact child in
`SHARE ROW EXCLUSIVE` mode through commit; sibling generations are not locked
and ordinary reads remain compatible. Concurrent publication changes, planner
cycles, liveness-root changes, target DDL/DML, index builds, and `plan-cycle`
calls cannot cross the final validation-to-commit interval.

Every transaction has a 2-second lock timeout, 15-second statement timeout,
20-second idle-in-transaction timeout, and PostgreSQL 17 30-second transaction
timeout. A suspended or disconnected host therefore cannot retain global
planning or relation locks indefinitely.

## Authorization and identity

The control row starts disabled with no active policy. Authorization requires a
clean committed repository and exact reviewed values for:

- repository commit and tree;
- published self-contained single-file supervisor executable SHA-256;
- deterministic supervisor source-bundle SHA-256;
- wrapper SHA-256;
- canonical installed control-schema SHA-256 covering its managed tables,
  columns, defaults, constraints, indexes, triggers, and functions;
- connected PostgreSQL identity SHA-256;
- UTC activation/expiry timestamps, at most seven days apart;
- maximum planned jobs and aggregate planned bytes;
- distinct approver/reviewer identities and an approval reference.

The PostgreSQL identity digest includes database name/OID, system identifier,
server version, `data_directory`, and `pg_postmaster_start_time()`. A restart or
physical clone therefore requires a new authorization.

The installed-schema fingerprint includes trigger enablement. Schema
initialization takes an exclusive control-schema advisory lock and re-enables
all managed safety triggers. It also rebuilds an invalid same-name
nonconstraint control index; index validity, readiness, liveness, uniqueness,
and primary flags are part of the fingerprint. Every tool transaction takes
the shared schema lock, so a schema replacement cannot cross runtime
attestation or planning. A changed schema fingerprint invalidates the existing
policy until it is explicitly deactivated and reauthorized.

This identity is sufficient only for metadata planning. It does not claim the
direct connection is bound to the exact Docker container instance that a
future archive subprocess would read. Exact container/endpoint binding remains
a mandatory gate before adding any archive command.

The wrapper requires
`FST_SNAPSHOT_RETIREMENT_BINARY_SHA256` and executes only the matching prebuilt
self-contained single-file Release executable. The process independently
verifies the same executable path and hash, so Git-ignored managed
dependencies and shared-framework updates cannot change the approved runtime.
The connection string is read from
`FST_SNAPSHOT_RETIREMENT_CONNECTION_STRING` and is never included in output or
durable state.

Authorization timestamps must already be UTC and aligned to PostgreSQL
microsecond precision. Sub-microsecond values are rejected so the immutable
policy digest, event payload, returned policy, and stored `TIMESTAMPTZ` values
remain reproducible.

## Deterministic planning

`plan-cycle` accepts no cycle, target, relation, or SQL argument. It always
reads the newest retention cycle and orders eligible children by:

1. planner-observed physical bytes descending, accepted only when the locked
   live child still has exactly that byte count;
2. snapshot ID ascending;
3. canonical instrument order;
4. child OID ascending.

There is no fallback target. If the largest eligible target exceeds the
remaining policy byte budget, planning deactivates the policy and stops. If a
planned job already remains current, repeated calls are idempotent and return
that job.

`reconcile` terminalizes a plan when:

- its policy expires;
- a newer retention cycle exists;
- the publication/scrape binding changes;
- the observation ceases to be a candidate;
- the exact child OID, relfilenode, or measured bytes change.

If `plan-cycle` itself discovers that the existing plan became stale after a
preceding reconciliation, it commits the superseding transition and stops. A
fresh invocation is required to select from the new state; the same
transaction never replaces a stale plan with another target.

An exhausted job budget deactivates the policy. Operator deactivation
terminalizes a planned job with `operator_deactivated`, appends immutable
events, and clears the active-policy pointer in the same transaction.

## Operator workflow

Build the fixed-purpose tool:

```bash
dotnet publish \
  tools/FstSnapshotGenerationRetirement/FstSnapshotGenerationRetirement.csproj \
  -c Release
```

Hash the published executable, export that value, and use the wrapper:

```bash
export FST_SNAPSHOT_RETIREMENT_BINARY_SHA256="$(
  sha256sum \
    tools/FstSnapshotGenerationRetirement/bin/Release/net9.0/linux-x64/publish/FstSnapshotGenerationRetirement |
  cut -d' ' -f1
)"
export FST_SNAPSHOT_RETIREMENT_CONNECTION_STRING='<operator-supplied>'

tools/postgres-snapshot-generation-retirement.sh status
```

Review the emitted identity values before authorizing. The authorization
command requires every expected identity explicitly:

```bash
tools/postgres-snapshot-generation-retirement.sh \
  authorize-policy-epoch \
  --not-before <UTC-round-trip> \
  --expires-at <UTC-round-trip> \
  --max-jobs 3 \
  --max-total-bytes <bytes> \
  --approved-by <identity> \
  --reviewed-by <different-identity> \
  --approval-reference <review-reference> \
  --expected-repository-commit <40-hex> \
  --expected-repository-tree <40-hex> \
  --expected-supervisor-binary-sha256 <sha256> \
  --expected-supervisor-source-sha256 <sha256> \
  --expected-wrapper-sha256 <sha256> \
  --expected-control-schema-sha256 <sha256> \
  --expected-source-identity-sha256 <sha256>
```

Then use only:

```bash
tools/postgres-snapshot-generation-retirement.sh reconcile
tools/postgres-snapshot-generation-retirement.sh plan-cycle
tools/postgres-snapshot-generation-retirement.sh deactivate-policy-epoch
```

These commands write only the four control-plane relations. They do not
authorize or invoke the existing standalone archive, quarantine, DROP, or
restore tools.

The disposable network-none drill requires a new evidence directory on the
FST drive and a clean committed repository:

```bash
tools/postgres-snapshot-generation-retirement-drill.sh \
  --work-root \
  /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-generation-retirement-plan-drills/<new-run>
```

It initializes a fresh PostgreSQL 17 database, exercises the published
single-file wrapper through status/authorization/plan/deactivation, confirms
largest-first selection, and proves the candidate relation OID, relfilenode,
and bytes are unchanged. Both the database and privileged scratch-cleanup
containers carry a unique operation label; container absence is proven before
the PostgreSQL-owned bind directories are removed. The socket uses the
operation-scoped short path
`/mnt/docker-storage/.fst-retirement-plan-sockets/<id>` on the same FST drive;
the script rejects a path at or above the Linux Unix-socket limit.

## Promotion and next gate

Merging and deploying this slice initializes empty default-off tables only.
Production planning still requires an explicit reviewed policy epoch.

Before any archive execution is added, a separate change must prove:

- the Npgsql control connection and archive source resolve to the exact same
  running PostgreSQL container instance;
- cancellation cannot release admission while `pg_dump`, proof containers,
  volumes, PGDATA, or scratch survive;
- process-start evidence is durable only after successful start and all
  interruption paths reconcile one provenance-safe state machine;
- policy expiry, lock loss, and transition failures cannot leave a live lease
  or nonterminal execution job;
- worker/service admission changes, if still necessary, are independently
  tested and production-safe.

Until those gates pass, use the already accepted standalone archive/prove,
quarantine/reattach, and DROP/restore runbooks manually. This control plane is
planning evidence only.
