---
status: decision
owner: data
last_verified: 2026-09-08
last_verified_commit: 2a7783a9
sources:
  - docs/database/SnapshotGenerationOfflineRetentionReport.md
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.Offline.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.Locks.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionRepository.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionSchema.cs
  - FSTService/Persistence/SnapshotRetentionSchemaCommand.cs
  - FSTService/Persistence/SnapshotRetentionSchemaDmlProof.cs
  - FSTService/Persistence/RegistrationMutationGuard.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionOfflineDiagnostics.cs
  - FSTService.Tests/Unit/SnapshotGenerationRetentionAuthenticationTests.cs
  - FSTService/Persistence/PublicationPathArtifactSchema.cs
  - FSTService/StartupPublicationReadOnlyState.cs
  - FSTService/StartupInitializer.cs
  - tools/FstSnapshotGenerationRetentionReport/
update_triggers:
  - Offline-report provenance, transaction admission, runtime attestation, or production promotion boundaries change.
---

# ADR 0009: Produce genuine retention evidence after an external idle stop

## Status

The host-report candidate received independent review. The source-preserving
and authenticated fresh-connection repairs remain subject to parent review. Scale adjudication permits
the bounded canary described below, but the first deployment was rejected at
its publication-mutation gate; production acceptance remains outstanding.
The later dedicated initializer refused at authentication before schema work.
Owned SCRAM/TCP validation of the repair is not a new live acceptance or retry
authorization.

## Context

The continuous worker queues a terminal report after publication but drains it
immediately before the next pass. That pass freezes public reads before its
synchronous, cancellation-free allocation lock. Holding the publication key
shared therefore prevents allocation without establishing a safe point to stop
the worker. Increasing polling frequency cannot repair that ordering.

The accepted planner's worker admission also consumes process-local
background-quiescence and scores-changed broadcast facts. An offline host
cannot honestly fabricate those facts or invoke notifications to reconstruct
them.

The full schema-only entry point also ran an unconditional current path-binding
upsert. Deployment after scrape `1362` applied the additive retention schema
but rewrote publication `223`'s binding timestamp/provenance metadata. Unchanged
public bodies and source relation identities did not waive the mutation gate.
Official images were restored; no manual binding repair was performed.

Npgsql's default `PersistSecurityInfo=false` sanitizes
`NpgsqlDataSource.ConnectionString`. Recovering an original password from that
property is impossible even when direct data-source connections authenticate.
Reconstruction at the initializer, offline fence or reconciliation boundary
can therefore fail only when a fresh backend is required, which trust fixtures
cannot detect.

## Decision

Add a separate pinned host command that observes only the current publication
after an externally established stopped-worker boundary. It creates no service
host and invokes the real planner's shared observation/oracle/classification/
hashing/persistence primitives.

Use the distinct `operator_offline_post_publication` safe-point kind. This is
provenance, not identity: one canonical cycle is keyed by trigger
scrape/publication across both kinds. Cycle and deferral checks are exact,
and upgrade refuses existing duplicates instead of rewriting immutable rows.
This is not a reinterpretation
of `terminal_worker_post_publication` or a new pending-work flag. Existing
worker requests retain every original admission requirement. Existing
persisted rows and their canonical hash encoding remain unchanged.

Require an immutable receipt of the stopped worker's actual deployed
report-only configuration and canonical lookup protocol version, bound to its
instance and service code hash. The CLI cannot self-enable. New offline
reporting is gated until the compatible worker has published that receipt.

Use the exact service flag `--initialize-snapshot-retention-schema-only` for
deployment. It dispatches before host or dotenv construction and accepts no
other arguments. Its internal wrapper executes only the same bounded retention
schema step used by general initialization, without evaluating or invoking
the other schema steps. It cannot run catalog, notification, registration,
path, publication, scrape or worker lifecycle work.

Keep normalized credential-bearing input only in fixed-purpose host process
memory/private non-record factories. The dedicated CLI passes that original
configuration directly to its initializer helper; the reporter supplies an
explicit private factory to the offline-only planner path. Every fresh
inspection/data/fence/reconciliation connection uses this source, including
after disposal. Missing offline factories refuse; no sanitized-data fallback
or `PersistSecurityInfo=true` workaround is permitted. Preserve the worker's
ordinary planner behavior, per-purpose timeouts and all commit/ownership
assertions. A reconnect remains evidence only for the exact cycle and ended
owners, never proof of an idempotent schema commit.

Require independent password-authenticated PG17 TCP coverage with a nonempty
random in-memory password and default security-information persistence off.
Retain trust/socket coverage separately. Executable drills validate
sanitization, actual initializer/reporting authentication and wrong-password
refusal. Integration fault injection validates authenticated disposal and
commit reconciliation without inventing a network-fault proof. Runtime
sentinel scans and in-memory output capture prevent credential artifacts.

Make source-DML evidence causal, not a comparison of asynchronously
flushed/cached database-wide counters. The dedicated path owns one fresh
unpooled backend and one bounded transaction, checks a zero entry baseline,
takes schema admission before exclusive canonical registration admission,
and asserts `pg_stat_xact_user_tables` before commit. Its exact DML allowlist
is empty because the accepted SQL performs no user-table seed/repair DML.
Any nonzero disallowed insert/update/delete count rolls back the schema step.
The structured proof records its fresh-session scope, exact allowlist,
totals and digest. A fresh session is necessary because PG17's xact view also
exposes pending backend counts from earlier unflushed work on reused sessions.

Version 2 combines that DML evidence with a same-transaction before/after
inventory of non-retention user table names, schemas, OIDs, relfilenodes and
kinds (`r`, `p`, `m`, `f`). Only system/temp objects and six exact public
retention DDL families are excluded; the DML allowlist remains empty.
Identity drift catches TRUNCATE and heap rewrites even with zero tuple
counters. A line-independent static step backstop covers TRUNCATE/COPY FROM
as well as INSERT/UPDATE/DELETE/MERGE.

Do not reinterpret a COMMIT transport/cancellation exception as rollback.
An attempted but unacknowledged commit emits nonzero uncertainty with null
committed state and possible schema/proof identities. This slice does not
add automatic retries or a speculative reconnect-success path: an idempotent
schema match alone cannot prove which transaction committed. Known commit
followed by cleanup failure remains committed but unsuccessful as a complete
operation. Precommit failure and explicit rollback remain distinct.

Retain cumulative counters as non-causal telemetry only. A later registration
counter delta neither proves initializer DML nor identifies the exact ambient
source, even when row timestamps and common registration statements provide
context. Accept only with the causal zero proof, unchanged actual
row/schema/source/path/control/public data, and independent lock/resource
gates. Ambient counter drift alone is not a rejection; unexplained actual
state drift remains one.

Make general path bootstrap insert-only without changing explicit maintenance
rebinding. Existing current-version bindings remain byte-for-byte equivalent,
including `built_at` and nonlegacy provenance; only missing current bindings
bootstrap and genuinely older/unversioned bindings enter the upgrade path.
Unsupported future or malformed versions are not silently downgraded.
They are explicit fail-closed mutation-startup diagnostics, not silent skips. Bounded
post-bootstrap validation shares the canonical binding validator with release
readiness and refuses invalid ready identities/counts/hashes without rewriting
source rows. The full executable's no-op catalog, publication-generation and
disabled-notification normalization is guarded against no-op row updates
during repeat initialization of already-correct state.
The dedicated command pins `pg_catalog,public` and schema-qualified DDL/built-ins
to prevent public-shadow name-resolution attacks.

Separate serving availability from mutation admission. Current/working path
refusals keep the explicit schema command's exit-2 contract, but ordinary
startup must not send them through the generic API shutdown path. Previous
is non-serving and therefore warns without mutation or startup abort.

Adapt only the reviewed foundation's one-way startup state, read-only
connection policy and HTTP/selected-profile/recovery gates. Do not import
execution admission, durable abort journals, lease/fence tables or worker
execution state. Select the mode with a private unpooled bootstrap source
before runtime pools. A bounded five-table SHARE-lock transaction revalidates
the path contract before the repeatable-read snapshot is used and retains
ownership through pool-policy construction; loss of that selection fence
forces read-only. No long-lived publication or archive admission lock is added.
The runtime data source is eagerly resolved immediately after host build,
before pipeline/hosted-service construction. Selection and fence release
therefore share one construction path rather than holding a transaction
across lazy DI resolution or an arbitrary pipeline delay.

Use the distinct `FSTService.StartupPublicationReadOnlyState` and
publication-prefixed registration/status APIs. Do not introduce an alias,
compatibility stub or duplicate of the separate foundation's
`FSTService.StartupReadOnlyState`. Future composition is deliberate work:
evaluate execution/recovery admission before allowing publication bootstrap
to mutate schema, perform read-only publication diagnosis when already
latched, and combine both outcomes monotonically before constructing shared
runtime pools or writers. Both gates must allow mutation; neither may clear
the other. This branch does not implement that composition or merge the
foundation's admission schema.

Replace mutation-capable hosted services before construction rather than
trying to latch after they have started. In degraded mode every runtime
connection is PostgreSQL read-only, selected-profile writes and mutation
requests reject, recovery/providers/timers are suppressed, and persisted
public GETs/caches retain their existing source gates. Expose separate read
and mutation readiness, with exact reasons and previous warnings. A fresh
guarded restart is the only path back to mutation-capable operation. This
rejects both swallowing the exception while setting normal readiness and an
unreviewed dynamic pool-replacement/whole-foundation merge.
Read-serving publication degradation is a `Healthy` health-check result with
explicit `degraded_read_only` details, not a change to aggregate health policy.
Genuine `Degraded` checks still return HTTP 503. The readiness JSON and
service-info retain separate mutation readiness and exact diagnostic reasons.

Compose bounded transaction-scoped advisory admission in canonical order.
PostgreSQL scopes the schema key by database; its initializer acquisition is
nonblocking. Acquire mutable-state locks
before the offline transaction's first snapshot, and retain them through
normal report persistence and final admission validation. A shared schema
fence prevents supported initialization from crossing attestation. A separate
advisory transaction releases immediately after the real data transaction
commits, before disposal; no publication session lock, unbounded exclusive
waiter, or durable execution lease is added.

An already-current accepted cycle is genuinely reobserved and hash-compared;
it is returned unchanged or refused, never silently replaced. Failed/mismatched
observations retain ordinary report-only evidence semantics.

Explicit binary, source-bundle, wrapper, Git-base, database/role, and schema
assertions identify the actual candidate. Uncommitted reviewed source is
reported as such rather than assigned a fictional commit. The existing
retirement policy's separate clean-tree contract is unchanged.

Budget exhaustion is a measured, explicit rollback outcome; the existing
15/120-second bounds are not raised speculatively. A committed result can
survive cleanup failure only with authoritative exact-cycle and ownership-
absence confirmation. Loader variables cannot redirect child execution or
PATH utilities; a trusted host launcher remains required.

## Scale measurement decision

Use one bounded first live offline-report canary after reviewed deployment and
an externally established idle stop. Do not require a full physical duplicate
that would consume most available ext4 FST-drive capacity including operating
reserve. The old partial restores are not scale evidence. Full-scale worker
observations provide a useful baseline for shared planner work, not proof of
the new host admission path.

Keep all existing identity, receipt, publication, notification, source-parity
and timeout gates. A current accepted cycle is fully revalidated and reused;
only a publication without a canonical cycle may acquire one new offline
cycle. Local commit/build can precede measurement, but no deployment, budget
increase, automatic lifecycle action or archive capability follows from that.

## Rejected shortcuts

- Fabricating a worker broadcast/quiescence request or inserting a cycle
  manually.
- Starting an API/worker host merely to reach the planner.
- Deliberately failing a completed scrape's resume validation.
- Stopping a worker blocked after it has already frozen reads.
- Duplicating planner SQL or weakening its independent liveness oracle.
- Treating this bounded report transaction as archive/execution admission.
- Treating a data-source display string as credentials, enabling persisted
  security information to recover it, or calling trust-only tests authentication
  evidence.

## Consequences and rollback

The operator can use the prior idle interval to stop safely, then obtain the
current report without another scrape. No production lifecycle action is
implemented or authorized by the command.

At a natural terminal stop, retain external restart exclusion, run the
dedicated retention-only initializer, require its version-2 combined
zero-DML/table-identity proof with acknowledged commit and
exact non-retention row/schema/source parity, recreate the candidate service
and restore public health, then use the
canonical guard to start the compatible worker and obtain its genuine receipt.
Require non-degraded startup/mutation readiness as well as HTTP health;
degraded read-serving is a rollback/admission refusal, not deployment success.
The API-only role still skips general schema initialization. The host reporter
refuses a missing or older shape and never repairs it. Rolling
back the host binary does not require deleting reports or narrowing the cycle
constraint. The additive schema already applied in production is compatible
and must remain in place; this change does not restore the previously mutated
path binding. Preserve offline-kind evidence. Retain a canonical-aware mutation worker after offline evidence
exists; an older kind-scoped worker cannot interpret its uniqueness conflict.
Production canary/scale acceptance and later archive execution remain separate
gates. The report-only scale decision does not waive normal backup/recovery or
destructive parity requirements.
