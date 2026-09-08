---
status: canonical
owner: data
last_verified: 2026-09-08
last_verified_commit: 2a7783a9
sources:
  - tools/FstSnapshotGenerationRetentionReport/
  - tools/postgres-snapshot-generation-retention-report.sh
  - tools/postgres-snapshot-generation-retention-report-drill.py
  - tools/snapshot_retention_schema_proof.py
  - FSTService/Persistence/SnapshotRetentionSchemaCommand.cs
  - FSTService/Persistence/SnapshotRetentionSchemaDmlProof.cs
  - FSTService/Persistence/SnapshotRetentionSchemaRelationIdentity.cs
  - FSTService/Persistence/SnapshotRetentionSchemaSqlBackstop.cs
  - tools/snapshot_retention_deployment_parity.py
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/RegistrationMutationGuard.cs
  - FSTService.Tests/Helpers/AuthenticatedPostgresScope.cs
  - FSTService.Tests/Unit/SnapshotGenerationRetentionAuthenticationTests.cs
  - FSTService.Tests/Unit/HostConnectionOwnershipTests.cs
  - tools/owned_postgres_auth.py
  - FSTService/Persistence/PublicationPathArtifactSchema.cs
  - FSTService/Persistence/PublicationPathArtifactReleaseGate.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.Offline.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.Locks.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionRepository.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionSchema.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionWorkerConfiguration.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionOfflineDiagnostics.cs
  - FSTService.Tests/Unit/SnapshotGenerationRetentionOfflineTests.cs
  - FSTService.Tests/Unit/OfflineReportAttestationTests.cs
  - FSTService.Tests/Unit/OfflineReportCommandTests.cs
  - docs/decisions/0009-offline-retention-report-admission.md
update_triggers:
  - Offline report admission, provenance, identity assertions, lock scope, command surface, or promotion gates change.
---

# Offline snapshot-generation retention report

## Capability and production gate

`FstSnapshotGenerationRetentionReport` is a fixed-purpose host executable. It
references the service's planner types as a library, not its executable entry
point. It creates no host, HTTP endpoint, provider client, notification sender,
background writer, or Docker client. It neither initializes nor repairs schema.

`inspect` reads code/database/schema identity. `observe-current` invokes the
real `SnapshotGenerationRetentionPlanner` observation, liveness oracle,
classification, canonical hashing, and immutable persistence path. Its only
database writes are normal retention cycles, observations, and evidence.
Refused offline admissions do not create pending work or deferral rows.

It cannot start/resume a scrape, freeze/unfreeze reads, run publication or
notifications, stop/start a worker, authorize a policy, select a target, or
archive/prove/detach/quarantine/drop/delete/truncate/restore data. There are no
scrape, cycle, relation, SQL, output-path, force, batch, or timeout selectors.

This capability is a candidate prerequisite for the plan-only production gate,
not a live acceptance claim. Parent independent review and an explicit
candidate canary remain required. It does not authorize automatic retirement.

## Genuine offline provenance

The existing worker route still requires its own background-work quiescence
and completed scores-changed broadcast. Those process-local facts must not be
invented by an offline command.

Offline observation therefore has a distinct
`operator_offline_post_publication` safe-point kind. It derives the current
scrape/publication identity from PostgreSQL and proves its own stopped-worker
and mutation-quiescence boundary. It does not construct a worker request with
fabricated broadcast or background-work flags, and it makes no claim that a
scores-changed broadcast was delivered.

Cycle and deferral kind checks explicitly admit both provenances. Canonical
cycle identity is the trigger scrape/publication pair, independent of kind.
The new unique constraint and kind-independent worker/repository lookup
prevent offline-then-worker and worker-then-offline duplicates. The older
three-column uniqueness remains for old same-kind insert compatibility.
Migration refuses pre-existing cross-kind duplicates for operator
adjudication rather than choosing a winner or rewriting immutable evidence.
Existing planner version `3`, configuration version `1`, row contents and hash
encoding remain valid; the host command never performs this migration.

## Deployed report-only authorization and rolling upgrade

The CLI cannot enable reporting. After normal schema readiness, the mutation
worker publishes an immutable configuration receipt containing its instance,
actual deployed report-only flag, service-assembly SHA-256, planner/config
versions, and canonical-cycle lookup protocol version `2`. Publication is
bounded and failure leaves offline reporting unauthorized, without blocking
ordinary ingestion indefinitely.

The offline command requires the receipt for the exact stopped worker,
recomputes its digest, requires enabled reporting and compatible versions, and
matches `--expected-worker-configuration-sha256`. Missing, disabled, malformed,
stale-instance or mismatched configuration refuses. No CLI option publishes or
changes that receipt. Production-owned Compose/configuration remains the
source of the worker's setting; a pending file edit is not a deployed
configuration receipt.

The planner consumes cached `IOptions<DatabaseMaintenanceOptions>`, not a
monitor or scoped snapshot, and no production code mutates this property.
Configuration-provider reload does not change an existing worker's option or
receipt. Enabling/disabling reporting requires a guarded worker restart and a
new instance-bound receipt; a pending file edit is not an immediate kill switch.

The first canonical-uniqueness migration is an offline schema-only stage.
For an existing report schema without the canonical constraint, initialization
acquires the legacy admission chain nonblockingly, locks worker/publication
state, and refuses active, unknown, stale or nonterminal boundaries. New service
databases use their normal complete initialization. An old worker cannot race its
three-column conflict target through this migration. The dedicated initializer
requires the existing FST database prerequisites; it does not bootstrap a new
service database.

The exact order is: finish publication/notifications and unfreeze; perform the
externally guarded stop and exclude restarts; run the reviewed
`dotnet FSTService.dll --initialize-snapshot-retention-schema-only` command
from the candidate binary within the fresh offline window; require its
version-2 combined zero-DML/table-identity proof with acknowledged commit and unchanged
non-retention rows/schema and publication/path/catalog/source/control
identities; recreate only the candidate service and restore full public
health; then start the compatible canonical-lookup-aware worker through the
guard. Only its new receipt can
authorize later offline reporting. The old same-kind conflict target remains
valid; an old cross-kind insert fails immediately on canonical uniqueness,
without rewriting evidence. This bounded refusal is not compatibility with an
old kind-scoped lookup. Once an offline cycle
exists, do not roll the mutation worker back to a kind-scoped lookup binary:
the canonical constraint will reject a conflicting old insert rather than
duplicate evidence, and that old worker cannot interpret the cross-kind
conflict. Keep the compatible worker or backport its lookup contract. API-only
readers do not acquire offline execution authority.

### Source-preserving deployment initialization

The dedicated service command is separate from the host reporter's command
surface. It dispatches before replay, `.env` loading, configuration/host
construction or hosted-service registration. It reads only
`ConnectionStrings__PostgreSQL` from the process environment and accepts no
additional argument, including other schema, maintenance or hosting flags.
It executes only the shared `SnapshotGenerationRetentionSchema.Sql` step:
2-second lock timeout, 15-second statement timeout, 20-second command timeout,
10-second connection timeout and a 30-second cancellation deadline. Its
fresh unpooled connection is disposed before secret-free success JSON is written.
It neither runs the general initializer nor creates a worker receipt.
Its connection search path is exactly `pg_catalog,public`. Retention DDL
explicitly qualifies application objects with `public` and catalog functions
with `pg_catalog`, including variadic-overload-sensitive formatting, so public
shadow objects cannot redirect initialization.

### Authenticated fresh-connection ownership

`NpgsqlDataSource.ConnectionString` is a sanitized display/configuration
view, **not a credential source**. With default `PersistSecurityInfo=false`,
it does not contain the password originally supplied to the data source.
Opening that data source can succeed while reconstructing a new connection
from its displayed string fails authentication. Never enable
`PersistSecurityInfo` to recover credentials from a data source.

The dedicated CLI passes its original normalized environment configuration
directly to `DatabaseInitializer`'s dedicated helper. The helper creates a
fresh backend through `PostgresUnpooledConnectionFactory` from that private
source; it does not create or inspect a data source to recover credentials.
The command normalizes security-information persistence off and keeps
`pg_catalog,public`, nonpooling/nonmultiplexing and every existing timeout.

`OfflineReportDatabase` likewise owns a private normalized factory. It uses
that source for inspection and explicitly supplies it through
`SnapshotGenerationRetentionPlanner.CreateForOffline`. Its data source and
planner-construction method are internal to the tool assembly (with test-only
friend access), not a public route to sanitized credential reconstruction. Identity/data,
advisory-fence and cleanup-reconciliation connections all come from that
factory, including reconciliation after data-source disposal. Calling offline
observation on an ordinary planner without the dedicated factory refuses with
`dedicated_connection_factory_required`; there is no sanitized-string
fallback. The ordinary worker constructor and `PlanAsync` behavior remain
unchanged.

Connection-purpose variants preserve the existing bounds: offline data and
fence connect/command limits are 10/15 seconds, and authoritative cleanup
reconciliation uses 5/5 seconds with a 15-second transaction limit. A successful
reconnect alone proves nothing about commit: the exact immutable cycle,
database signature and ended transaction/advisory ownership must still match.
Wrong authentication or unavailable confirmation retains uncertainty.
Every host-connection variant unconditionally sets exactly one
`-c row_security=off`. Option composition accepts only unique positive-second
timeout settings; redundant exact `row_security=off` settings normalize away,
while overrides, unsupported syntax and conflicting timeouts refuse before
connection creation. This does not bypass RLS or grant privileges: a query that
would silently hide retention rows must raise an error instead. Owned SCRAM
fixtures prove this on real fence and reconciliation connections with a
non-owner, non-superuser, non-bypass role, alongside their unchanged timeouts.

Credentials remain only in the host's environment/process memory and private
non-record factory. They are never returned as identity fields, serialized,
logged, put in command-line arguments or persisted in evidence. Factory
construction does not extend their lifetime into another host or artifact.

The host-tool audit also covers retirement, quarantine, DROP, restore
authorization/continuation and stored-rank tools. Their sources are constructed
from original normalized input and reopened with `OpenConnectionAsync`, which
retains authentication. Metadata-only `PostgresRuntimeTarget` and pool-size
inspection are safe display-string uses. The service injects the original
runtime factory into `MetaDatabase`; its legacy optional-constructor fallback
is not an authenticated host credential source, and the owned fixture seeder
now supplies an explicit factory too. Source and API-visibility contracts guard
host-tool reconstruction and public data-source exposure. The sole deliberate
reconstruction exception is the owned
`authentication-probe`, which must prove sanitized reconstruction fails.

### Causal DML and live parity

The dedicated wrapper executes the unchanged schema step in one short
transaction on a **fresh, unpooled, nonmultiplexed backend**. PostgreSQL 17
and enabled `track_counts` are required. It verifies a zero user-DML entry
baseline, acquires the database-scoped retention schema advisory lock first
and the canonical registration mutation lock exclusively second, then runs
the schema step and reads `pg_catalog.pg_stat_xact_user_tables` before commit.
Both locks remain held through the assertion and commit/rollback. Existing
registration/selected-profile writers cannot cross that transaction; queued
work may proceed after it ends. Schema/registration contention refuses rather
than skipping admission.

The exact DML allowlist is **`[]` (no relations)**. The reviewed
`SnapshotGenerationRetentionSchema.Sql` installs DDL but seeds or repairs no
user-table rows. Its six retention table families are not implicitly allowed
DML targets, and a retention-like name prefix grants no permission. Any
insert/update/delete count outside that exact list raises
`non_retention_dml_detected` and rolls back the entire schema transaction.
The existing helper index on publication scope sources remains part of the
unchanged step; no new extension, configuration or source-DML surface is added.

Tuple counters alone do not cover `TRUNCATE` or heap-rewriting DDL. Before
executing the schema SQL, the same transaction captures every non-retention
user table's schema, relation name, OID, relfilenode and relation kind. Before
commit the exact set and identities must still match; drift returns
`non_retention_relation_identity_changed` and rolls back. Scope includes
ordinary tables/partition leaves (`r`), partitioned parents (`p`),
materialized views (`m`) and foreign-table metadata (`f`) across all user
schemas. Partitioned/foreign relations normally have relfilenode zero;
foreign metadata identity is not a claim about external foreign data.
System schemas (`pg_catalog`, `information_schema`, `pg_toast`) and temporary
relations are excluded. Sequences, indexes and ordinary views are not table
identities and remain covered by separate source/schema parity.

The only non-system, non-temporary exclusions are the six exact DDL-managed
`public.snapshot_generation_retention_` relations: `cycles`, `deferrals`,
`evidence`, `holds`, `observations`, and `worker_configuration`. These are
identity-inventory exclusions, **not** a DML allowlist. Same-name relations in
other schemas and similarly prefixed user tables remain protected.
The static SQL backstop independently rejects executable INSERT/UPDATE/
DELETE/MERGE, TRUNCATE and relation COPY FROM forms irrespective of line
position, semicolon placement or DO-body formatting. Trigger-definition
event clauses and COPY TO remain legal. This is a backstop for the exact
reviewed step, not a general-purpose SQL sandbox.

Fresh-session enforcement is essential: PG17's xact accessors read backend
pending counts plus active transaction/subtransaction counts, so a reused
session can retain earlier unflushed work. The dedicated wrapper uses the
existing unpooled connection factory even if the original configuration enables
pooling; it never resets statistics to manufacture a zero. The proof
declares `backendScope=fresh_unpooled_single_transaction`.
See PG17's [xact accessor](https://github.com/postgres/postgres/blob/REL_17_STABLE/src/backend/utils/adt/pgstatfuncs.c#L1588-L1632)
and [pending/current-transaction aggregation](https://github.com/postgres/postgres/blob/REL_17_STABLE/src/backend/utils/activity/pgstat_relation.c#L475-L583).

Successful `schema_current` JSON includes `transactionCommitted=true` and
`dmlProof`: version `2`, statistics source, backend scope, exact schema SQL
SHA-256, exact allowed DML relations, `nonRetentionDml` inserted/updated/deleted
totals (all zero), `allowedRetentionChanges` (empty), and
`nonRetentionRelationIdentity` scope/kinds/exclusions, before/after counts and
digests, and `unchanged=true`. A deterministic combined SHA-256 covers all
ordered proof fields. Nonzero-DML or identity refusal includes attempted
evidence and
proof digest with `transactionCommitted=false`; it does not echo arbitrary
relation names or SQL. Missing/disabled statistics or a nonzero entry baseline
refuses. The 2/15/20-second lock/statement/command limits and 30-second deadline
remain unchanged; the transaction also has 20-second idle and 30-second total
limits.

Commit acknowledgement is a separate boundary. A failure before COMMIT is
attempted is a refusal/rollback. If COMMIT is attempted but its acknowledgement
is lost or unconfirmed, the CLI exits nonzero with `outcome=uncertain`,
`code=commit_acknowledgement_unknown`, `transactionCommitted=null`, and
`possibleSchemaProof` containing the schema-step and combined-proof identities.
It never claims `false` for that case and never automatically retries.
Matching schema objects alone cannot resolve an idempotent migration's
commit outcome. This slice deliberately performs no automatic reconnect
resolution; any later adjudication must prove the exact schema/proof identity
and absence of the owning backend/locks. If COMMIT was acknowledged but later
cleanup fails, `committed_cleanup_unconfirmed`/`post_commit_cleanup_failed`
retains `transactionCommitted=true` while still exiting nonzero.

`pg_stat_user_tables` counters, including copies embedded in topology
inventories, are **non-causal telemetry only**: updates are asynchronously
flushed/cached, and ambient traffic cannot be attributed to the initializer
from a later cumulative delta. The previously observed registration-family
delta remains unattributed; absence of matching row timestamps and ordinary
registration statements does not establish its exact origin.

Live acceptance requires the causal initializer proof **and** byte-stable
non-retention row hashes/schema/source/path/control identities, public-body
parity and no unexpected locks/resource pressure. Cumulative counter drift
alone cannot reject when those invariants pass. Unexplained actual row,
schema or source drift still rejects; do not filter away registration rows or
timestamps to force a pass. General service startup remains a separate exact
row/schema/public-data gate, not an excuse to infer causality from counters.
The read-only artifact comparator requires the combined version-2 proof and
`transactionCommitted=true`; null/uncertain and version-1 proofs cannot pass.
The comparator
`tools/snapshot_retention_deployment_parity.py` enforces the proof and source
dimensions, records cumulative deltas as telemetry, and never authorizes a
deployment or replaces independent ownership/resource admission.

Do not substitute full `--initialize-schema-only` for this deployment step.
That general command still owns other schema/data initialization. Its path
bootstrap is now insert-only: an existing current-version binding retains
exact JSON, provenance, kind, count, hash, status and `built_at`, including
nonlegacy sources. Missing current bindings bootstrap; unversioned legacy or
strictly older positive-integer manifest versions use the explicit upgrade
path. Future/malformed versions are not silently downgraded or silently skipped:
bounded current/working validation after bootstrap/upgrade fails the path-schema transaction
with publication IDs and stable diagnostic codes. Explicit null, nonpositive,
fractional and nonnumeric versions are invalid, not legacy upgrades.
Ready bindings must satisfy the same canonical release contract used by
startup readiness, including JSON identity/authority, publication/scrape,
versions, expected/actual/binding counts and the canonical content hash.
Invalid rows are never rewritten to hide the refusal. Deliberate
runtime path/publication maintenance retains its explicit rebinding behavior.
Previous invalid bindings are non-serving warnings, not startup aborts.
Ordinary service startup uses `StartupPublicationReadOnlyState`, distinct from
the separate execution-admission foundation. The main data source is eagerly
resolved immediately after host build, completing selection and fence release
before pipeline or hosted-service construction. A current/working refusal
selects sticky `degraded_read_only` with
PostgreSQL read-only connections, mutation HTTP/selected-profile rejection
and no hosted writer, provider sync or publication recovery. Persisted public
reads/caches remain available without releasing invalid path data. Its
readiness check reports `Healthy`/HTTP 200 with explicit `degraded_read_only`
details in structured JSON; unrelated `Degraded`/`Unhealthy` checks remain
HTTP 503. Read availability is not deployment acceptance: require
`startup.mutationReady=true` and no current/working diagnostics before guarded
worker acceptance. Only a fresh guarded restart can clear the latch.
API-only startup still skips general schema initialization; recreating the API
is not evidence that a migration ran.

The rejected deployment after scrape `1362` already applied the additive
canonical retention schema in production. The full initializer also refreshed
publication `223`'s path binding, so the no-publication-mutation gate rejected
promotion and official runtime images were restored. Keep that compatible
additive schema; do not roll it back to retry. This repair prevents subsequent
bootstrap overwrites and does **not** repair or claim to restore that existing
binding mutation. Parent review and a new deployment/canary gate remain
required.

## Admission and transaction boundary

Before observation, require:

- PostgreSQL 17 and exact expected database identity, including database OID,
  system identifier, data directory, postmaster start, session/current roles,
  and row-security bypass posture;
- required initialized schema, enabled immutability triggers, report-only
  checks, exact cycle/deferral kind definitions, canonical pair uniqueness,
  and the expected logical schema fingerprint;
- the exact authentic enabled worker-configuration receipt described above;
- the newest scrape completed, no running scrape, and that same scrape bound
  to the current publication generation;
- unfrozen reads, no working publication, commit intent, or max-score gate;
- canonical completed/disabled notification state;
- `service_worker_status.scraper` offline, mode `scraper`, SQL-null current
  operation, and a complete consistent worker identity;
- an offline transition no more than 900 seconds old, after publication and
  notification completion, with ordered start/stop/heartbeat/update timestamps
  and no more than two minutes between the offline transition and final
  heartbeat;
- the existing planner's publication, registration, topology, hold, writer
  failure, and oracle validations.

Legitimate holds and unreplayed writer failures remain protection roots, not
permission to erase evidence. In particular, Solo Bass `1308` cannot become a
candidate merely because the caller is offline.

The shared report-schema fence is outermost. The existing lock order remains
registration mutation, centralized maintenance, shared publication, then
exclusive report planner, followed by the shared supported snapshot-partition
DDL fence. Offline admission uses only transaction-scoped
`pg_try_advisory_xact_lock*` calls with short retries. There is no session-owned
publication lock.

The schema key is PostgreSQL database-scoped, not cluster-wide: advisory lock
identity includes the database OID. Schema initialization uses a nonblocking
transactional exclusive acquisition and raises SQLSTATE `55P03` on contention.
Its existing short initialization transaction rolls back; startup remains
unready and the owning startup/recovery workflow may retry. It never waits
indefinitely or silently skips migration. Another database does not contend
on this key.

After preflight identity/configuration checks, a dedicated bounded transaction
holds the canonical advisory chain. The real repeatable-read transaction on a
second connection holds report-schema tables and the mutable
writer-failure, retention-hold, worker-status, and publication-state surfaces.
These locks precede its first data snapshot. Admission, the real planner/oracle
read, normal report persistence, and final boundary validation remain inside
that data transaction. The advisory transaction commits immediately after the
data commit, before connection/disposal cleanup. This limits a queued
exclusive publication waiter's convoy to the bounded observation transaction;
public readers do not acquire a new private maintenance lock.
The worker path continues using its existing read/persist
flow, sharing extracted lock and observation/persistence primitives rather
than acquiring the same lock twice.

The ordering test observes every required lock on the separate fence backend,
commits a probe row from another connection only after all locks are held, and
proves that the real repeatable-read transaction sees that row but not a later
probe commit. It makes both first-snapshot ordering and snapshot stability
observable rather than relying on source-code order alone.

Statement and row-lock limits remain 15 and 2 seconds. The data connection's
idle session/transaction limits are 20 seconds. The advisory transaction has
an absolute 120-second transaction limit and a matching idle limit while the
data connection executes. The observation retains its 120-second linked
deadline; the CLI retains its 150-second watchdog. No larger production
budget is inferred from correctness tests.
`row_security=off` prevents silent RLS filtering; role and RLS/policy metadata
are also included in attestation.

The schema fingerprint covers public logical relation, column, constraint,
index, trigger, policy, owner, and function definitions. Numeric snapshot
children are excluded from that configuration fingerprint: their actual
topology and identities are observed by the existing planner/oracle under the
DDL fence.

## Idempotency and failure

An accepted newest cycle for the current scrape/publication is fully
reobserved using its original safe-point identity. Matching hashes return
`Existing` without inserting another cycle. Drift, an unaccepted current
cycle, or invalid admission refuses; callers must not edit or delete durable
evidence to force a retry.

Real oracle mismatch/global blockers retain normal fail-closed report
classifications. Observation exceptions can persist a sanitized normal failed
cycle under the still-held admission transaction. Cancellation rolls back
instead of manufacturing a successful or pending cycle.

Budget exhaustion is explicitly `observation_budget_exceeded`, with the
current phase, actual elapsed time and recorded phase timings. The partial
transaction rolls back; a timeout is not converted into a failed cycle to
hide budget exhaustion.

A cleanup/disposal error after commit can return a warning-bearing accepted
result only after a fresh connection confirms the exact immutable cycle,
unchanged database identity, and absence of the recorded ownership
transactions/advisory locks. Otherwise the command returns
`commit_outcome_uncertain` with the possible cycle ID. It never prints an
ordinary failure-shaped result claiming no commit occurred.

Success JSON includes cycle/scrape/publication IDs, actual safe-point kind,
versions, disposition/status, oracle agreement, counts/bytes, hashes,
blockers/anomalies, stopped-boundary timestamps, runtime identity, and the
honest capability claim. Refusals expose stable error codes, never connection
strings, provider credentials, SQL text, or exception messages containing
operator input.

## Pinned host invocation

Build on the FST drive:

```bash
dotnet publish \
  tools/FstSnapshotGenerationRetentionReport/FstSnapshotGenerationRetentionReport.csproj \
  -c Release

export FST_SNAPSHOT_RETENTION_REPORT_BINARY_SHA256="$(
  sha256sum \
    tools/FstSnapshotGenerationRetentionReport/bin/Release/net9.0/linux-x64/publish/FstSnapshotGenerationRetentionReport |
  cut -d' ' -f1
)"
export FST_SNAPSHOT_RETENTION_REPORT_CONNECTION_STRING='<operator-provided>'

tools/postgres-snapshot-generation-retention-report.sh inspect
```

The wrapper verifies the prebuilt self-contained single-file hash; the process
independently verifies its exact wrapper-provided executable path and hash.
CLR native extraction is fixed under
`artifacts/offline-retention-report-runtime` in that FST-drive worktree.
The wrapper uses `/bin/bash`, rejects nonempty `LD_PRELOAD`,
`LD_LIBRARY_PATH`, and `LD_AUDIT` before external utilities, clears
`DOTNET_ROOT` variants and managed startup/profiler hooks, and fixes PATH to
`/usr/bin:/bin`. Decoy utilities cannot affect hashing or Git identity. The
host launcher must itself be trusted: a shell cannot undo native code already
loaded into its interpreter before the script begins.

The code identity provider independently validates and invokes exact
`/usr/bin/git`, with its subprocess PATH fixed to `/usr/bin:/bin`. This applies
even to direct binary invocation; a fake Git earlier in the caller's PATH
cannot forge repository identity.

Review, do not automatically approve, the inspected values:

```bash
tools/postgres-snapshot-generation-retention-report.sh observe-current \
  --expected-repository-commit <40-hex> \
  --expected-repository-tree <40-hex> \
  --expected-source-sha256 <sha256> \
  --expected-wrapper-sha256 <sha256> \
  --expected-schema-sha256 <sha256> \
  --expected-database-identity-sha256 <sha256> \
  --expected-worker-configuration-sha256 <sha256>
```

Commit/tree identify the Git base; the mandatory source-bundle hash covers the
actual tool/service/core source and wrapper. A reviewed uncommitted candidate
can be pinned without inventing a commit: `workingTreeClean=false` is emitted
honestly. Binary and source assertions still must match exactly. This does not
weaken the separate retirement policy tool's clean-tree requirements.

## Operator ownership

The operator, not this command, must prove the mutation worker container is
stopped and exclude concurrent restart using the existing guarded host
workflow. Database status is necessary evidence, not a Docker-container probe.
The command's transactional fences end at completion; they are not a durable
archive/execution admission lease.

After approval, stop only during a verified natural idle/unfrozen interval,
then collect the offline report and revalidate the new cycle before using the
separately pinned retirement `plan-cycle`. Resume only through the canonical
worker guard after all offline work is finished. Production Compose remains
owned by `/home/sfenton/Docker/FestivalServiceTracker`; repository Compose
files are templates and this tool changes neither.

Do not use the shared publication advisory lock as a stop gate: the current
worker freezes reads before its blocking allocation lock. Do not simulate a
completed-resume failure or clear durable worker/freeze state to obtain an
offline report.

## Disposable validation

The authenticated lane is required for credential-boundary acceptance:

```bash
python3 tools/postgres-snapshot-generation-retention-report-drill.py \
  --work-root artifacts/offline-retention-report-drills/<new-auth-report> \
  --scram-tcp

python3 tools/postgres-snapshot-generation-retention-report-drill.py \
  --work-root artifacts/offline-retention-report-drills/<new-auth-schema> \
  --scram-tcp --schema-only-repair
```

This lane binds only an owned loopback TCP port and installs SCRAM host rules.
Its random nonempty password stays in process memory/owned child environments;
only a SCRAM verifier is supplied to private fixture administration, with
statement/parameter logging suppressed. No password is placed in Docker
configuration, a password file or SQL text. The actual executable probe proves
default security-information persistence is false, the data-source password
is absent, sanitized reconstruction refuses, and direct/private-factory TCP
authentication succeeds. Actual initializer and reporter wrong-password
invocations retain secret-free refusals. All ordinary source-preserving
schema/degraded-serving proofs also run over this password-authenticated
client path.

The original trust/socket lane remains separate coverage:

```bash
python3 tools/postgres-snapshot-generation-retention-report-drill.py \
  --work-root artifacts/offline-retention-report-drills/<new-run> \
  --run-focused-tests
```

After building the extracted base fixture, add `--upgrade-from-880802ec` to
exercise the exact prior initializer, record both legacy kind constraints,
upgrade through the current initializer, and prove source rows unchanged.
The configuration fixture invokes the real worker-configuration publisher;
the production reporter never creates its own authority.

The source-preserving deployment proof uses the same owned fixture:

```bash
python3 tools/postgres-snapshot-generation-retention-report-drill.py \
  --work-root artifacts/offline-retention-report-drills/<new-schema-run> \
  --schema-only-repair
```

It seeds a live-like publication/catalog/path snapshot with nonlegacy
provenance, compares the complete binding through repeated full initialization,
and launches the actual `--initialize-schema-only` executable repeatedly,
comparing all non-retention row hashes and schema, not just the
binding. Cumulative counters are retained with explicit non-causal
classification. Fixture AFTER-row DML and BEFORE-TRUNCATE traps preserve
the full executable's no-source-write/no-op-update regression without
misclassifying zero-row statements as row mutations. Catalog and publication/disabled-notification compatibility
normalization skip already-correct values. The drill also runs the actual
retention-only CLI against a simulated older retention
schema and then its already-current shape. All non-retention tables have
statement-level mutation traps, row hashes and physical identity;
non-retention schema definitions are also compared. Both dedicated runs must
return the exact causal zero-DML proof. A test-only DDL hook injects source DML
into an actual CLI process and must receive a structured refusal with complete
row/transaction rollback. Additional actual CLI hooks truncate or rewrite a
source heap and require identity-drift refusal and exact source restoration.
Unit hooks distinguish precommit failure, definite rollback, and a real
server commit followed by a simulated lost client acknowledgement.
Mixed commands refuse before any workload. An optional `--baseline-service`
plus `--baseline-service-sha256` pins an existing FST-drive baseline binary to
reproduce the prior mutation inside the disposable database only.
Future-version, malformed-version and invalid-ready-binding cases also execute
the full CLI and require nonzero exit plus structured diagnostics, while the
exact invalid binding remains unchanged.
Reachable current-ready/inexact-catalog, current-ready/missing-catalog and
invalid-working scenarios additionally launch the ordinary service executable.
They require exact persisted GET/cache bytes, explicit degraded health/status,
HTTP mutation refusal, suppressed hosted writers, no rollout-violation
misclassification and unchanged non-retention rows. Counter telemetry is not
treated as transaction attribution. Actual service
process groups, database backends and per-case HOME/XDG/data paths must be
absent before fixture cleanup is accepted.

The drill requires an FST-drive worktree and uses only a new labelled
PostgreSQL 17 container with bounded resources, owned PGDATA and a unique short
Unix socket. The default trust lane has network `none` and no ports; only
`--scram-tcp` enables an exact loopback port. Only its isolated fixture runs
the real schema initializer. It seeds genuine baseline source state, invokes
the pinned host wrapper, proves one real report and idempotency, compares
source OID/relfilenode/bytes and all scrape/publication/freeze/worker rows, and
rejects source DML with fixture triggers. It removes owned containers and
PGDATA/socket scratch before sealing evidence. Runtime/build artifacts remain
under the worktree's ignored FST-drive `artifacts` directory.

Fixture evidence includes authoritative per-instance startup inventory,
loopback-only TCP bindings where applicable, exact data/socket mounts,
disabled Docker logging, PostgreSQL log hashes on the FST drive, disabled Ryuk,
and final absence of labelled containers/volumes and owned paths. Console/TRX
evidence also remains on the FST drive. Stdout/stderr are captured in memory
and checked against runtime password/connection/verifier sentinels before
artifact writes; PostgreSQL logs and final evidence are scanned before
cleanup/sealing. The controlled service runner separately supplies an
in-memory authentication sentinel, scans console/TRX/PostgreSQL evidence and
does not copy operator credential environments. Authenticated integration
tests cover real planner/fence/oracle/persistence, data-source disposal,
exact-cycle/ownership reconciliation and unresolved commit outcomes. Legacy
shared/trust fixtures are not credential-propagation proof.
Earlier runs that did not inventory
Docker logging are not proof of log placement; rerun only the minimal evidence
in a production-idle/headroom-safe window rather than competing with a scrape.

The optional bounded `--hold-for-tests` development mode writes `runtime.json`
then waits for `continue.requested`; `stop.requested` or the deadline cleans
up without claiming proof success. Existing tests can use the scope-validated
test-only Unix-socket override described in the [testing guide](../testing/README.md).

## Consumer audit and production-scale budget gate

The worker and repository resolve canonical cycles by trigger pair.
Retirement planning and its insert trigger select/pin cycle ID and newest
cycle without filtering kind. Archive verification rebuilds hashes using the
stored provenance; quarantine, DROP and restore bind existing cycle IDs and
artifact hashes. None treats kind as a target selector or rewrites historical
hash inputs. The canonical database constraint is the shared uniqueness
boundary for all of these consumers.

Scale adjudication permits one bounded first live offline-report canary after
reviewed candidate deployment, rather than requiring a full physical duplicate
on the production-owned ext4 drive. The sealed scale inventory contained
1,593 children and roughly 1.7 TB of database data against roughly 2.15 TB free;
copying it plus WAL, temporary space and operating reserve is not an approved
prerequisite. Older partial artifacts do not substitute for production-scale
evidence. Accepted worker cycles exercised the shared planner/oracle/persistence
in about 1.4-1.6 seconds, but do not prove the new host attestation/admission
path, cold-cache behavior or worst-case latency.

Local candidate commits and builds may precede this measurement. They do not
authorize deployment or a live invocation. Before the canary, complete the
reviewed schema/compatible-worker ordering above, establish a fresh externally
stopped worker boundary and authentic receipt, and retain parent-owned restart
exclusion. Require current completed publication and terminal notifications,
unfrozen reads, no worker operation or running scrape, and safe resource/lock
conditions. The reporter remains host-only and cannot create its own boundary.

Use unchanged 2-second lock, 15-second statement, 120-second observation/
transaction and 150-second CLI watchdog limits. Capture phase timings,
CPU/memory/I/O, locks, source/catalog/control parity and public-read behavior.
Only normal report-evidence writes are allowed; retirement policy/jobs/events,
source identities, scrape/publication/freeze and worker/configuration state
must remain unchanged. Never flush shared caches or increase a timeout to make
the canary pass. Confirm owned transaction/backend/lock release after any
failure; the cancellation watchdog is not itself an absence proof.

An accepted existing worker cycle for the current trigger pair is fully
reobserved and returned as `Existing`, with its original provenance and no new
offline cycle. This measures the host path, not new-row persistence. If no
canonical current cycle exists, the canary may create exactly one genuine
`operator_offline_post_publication` cycle. Drift, unaccepted cycles or duplicate
evidence refuse; never replace immutable evidence to obtain another attempt.
After an accepted report, separately revalidate plan-only policy pins/budgets,
reconcile stale plans and run `plan-cycle` while the boundary remains valid.

Budget exhaustion must roll back; a genuine failed observation or uncertain
commit remains evidence to investigate, not permission to retry blindly.
Preserve the additive schema, reports and canonical-aware worker on rollback.
The production canary, measured scale acceptance and later archive execution
remain separate gates; a small fixture or one warm success does not establish
production-wide performance.
