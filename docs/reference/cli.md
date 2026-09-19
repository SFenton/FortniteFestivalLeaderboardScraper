---
status: canonical
owner: service
last_verified: 2026-09-18
last_verified_commit: c7488355
sources:
  - FSTService/Program.cs
  - FSTService/Persistence/InterruptedAcquisitionNormalizationCommand.cs
  - FSTService/Persistence/InterruptedAcquisitionNormalizationModels.cs
  - FSTService/Persistence/MetaDatabase.InterruptedAcquisitionNormalization.cs
  - FSTService/Scraping/Capture/CaptureOnlyCommand.cs
  - FSTService/Scraping/Capture/CaptureOnlyEntryPoint.cs
  - FSTService/Scraping/Capture/CaptureOnlyRunner.cs
  - FSTService/Scraping/Capture/CapturePaginationMaximums.cs
  - FSTService/Scraping/LeaderboardPaginationPlanner.cs
  - FSTService/Persistence/SnapshotRetentionSchemaCommand.cs
  - FSTService/ScraperOptions.cs
  - FSTService/ScrapePhase.cs
  - FSTService/Scraping/PostScrapeOrchestrator.cs
  - FSTService/Persistence/PublishedScrapeIdArgument.cs
  - FSTService/Persistence/MaxScoreMaintenanceCommand.cs
  - FSTService/Persistence/MaxScoreMaintenanceModels.cs
  - FSTService/Persistence/MaxScoreMaintenanceFileStore.cs
  - FSTService/Persistence/MetaDatabase.cs
  - FSTService/Persistence/ScoreHistoryDedupMaintenanceCommand.cs
  - FSTService/Scraping/RankingsCalculator.cs
  - FSTService/Scraping/SoloFamilyRankingBackfillCommand.cs
  - FSTService/Scraping/LeaderboardRivalsRecomputeCommand.cs
  - FSTService/Scraping/Replay/ReplayCommand.cs
  - FSTService/Scraping/Replay/ReplayEntryPoint.cs
  - tools/FstSnapshotGenerationRetentionReport/Program.cs
  - docs/database/SnapshotGenerationOfflineRetentionReport.md
update_triggers:
  - A command-line flag, combination rule, one-shot mode, or phase expansion changes.
---

# FSTService CLI

Use `dotnet FSTService.dll <flags>` in a built image or the equivalent
`dotnet run --project FSTService/FSTService.csproj -- <flags>` locally.

## Hosting and setup

| Flag | Behavior |
|---|---|
| `--setup` | Device-code authentication setup, then exit |
| `--api-only` | API-only mode; no scraper pipeline |
| `--no-scraper-worker` | Frontend/API role without scheduled scraper mutation |
| `--registration-sync-worker` | Registration refresh/backfill worker without scheduled scrape/band history |
| `--once` | One scrape/publication pass, then exit |
| `--rollout-read-only-startup` | Register only persisted-state loading/HTTP serving |
| `--rollout-postgres-read-only` | Enforce the paired PostgreSQL read-only rollout mode |

The two rollout read-only flags must be enabled together.

The separate host-only
[offline retention report executable](../database/SnapshotGenerationOfflineRetentionReport.md)
is not an FSTService hosting flag. Its only commands are `inspect` and
identity-asserted `observe-current`; it never starts the service/worker
entry point, initializes schema, resumes a scrape, or sends notifications.
`--once` remains a full scrape/publication pass, not an offline report mode.

## Manual capture-only command

`--capture-only` performs one provider capture, seals one
`fst.capture-package.v2` package, prints one sanitized JSON result, and exits.
It is default-off and manual. It is not a hosted worker mode, overlap
scheduler, production candidate, publication action, or database import.

Capture dispatch is the first `FSTService` command check. The dedicated entry
point parses the complete argument list before loading `.env`, then loads only
normal configuration needed for Epic authentication, enabled leaderboard
types, pacing, and proxy routing. It does not construct `WebApplication`,
register hosted services, create an Npgsql data source, initialize schema,
publish worker status, allocate a publication, freeze reads, build caches,
generate paths, run cleanup/post-process, or notify clients.

The strict command shape is:

```text
--capture-only
--capture-output <new-package-root>
--capture-id <capture-id>
```

The output must be a nonexistent direct child of the
`FST_CAPTURE_APPROVED_ROOT` directory. Duplicate flags, missing/empty values,
unknown options, positional arguments, unsafe capture IDs, and every normal,
replay, maintenance, setup, or hosting flag are rejected. The command cannot
be combined with `--once`, phase-selection flags, `--setup`, API/worker role
flags, replay flags, schema commands, or maintenance commands.

The command requires the capture environment described in
[Configuration](configuration.md#manual-capture-only-environment). It uses
the configured full-scrape solo instrument switches and
`Scraper:EnableBandScraping`. Both active solo and band paths use
`Scraper:MaxPagesPerLeaderboard`; parallel solo mode additionally reproduces
the active CHOpt deep-scrape/valid-entry rules, while sequential solo mode ends
at the initial configured page range. The legacy direct band phase's separate
page/valid-entry settings are not part of the normal worker capture plan.
Concurrent page completion is sorted back into canonical song,
solo-instrument, band-type, and page order. Successful HTTP responses must
contain typed `page`, `totalPages`, `totalEntries`, and `entries` fields.
Scope finalization cancels and awaits any detached CDN probe before binding the
monotonic physical-send total to retained request metadata.
An exact page-zero `event_not_found` response is represented separately from
an HTTP-success empty page. When a parallel solo scope needs CHOpt-aware pagination,
the command requires the catalog-bound maximum-score snapshot configured by
`FST_CAPTURE_PAGINATION_MAX_SCORES_PATH`; it never queries PostgreSQL for that
state. The snapshot must itself reside on the approved filesystem device. A
valid existing device-auth credential is required; capture mode never starts
interactive setup. Live capture also requires an absolute same-device
`Scraper:ProxyCurlTempDirectory` even when curl is only the .NET HTTP fallback;
fallback response files are transfer-bounded and removed after each attempt.
Capture curl invocations disable ambient curl configuration, and proxy
concurrency leases remain held until response bodies are consumed or disposed.
Every page has a fixed ten-minute cumulative transport deadline across proxy
waits, network retries, CDN recovery, and response transfer. Deadline
exhaustion returns the typed capture failure code rather than cancellation.

Exit codes are:

| Code | Meaning |
|---:|---|
| `0` | One package sealed successfully |
| `1` | Unexpected sanitized failure |
| `2` | CLI or configuration usage failure |
| `3` | Approved root, device, or output path rejected |
| `4` | Package/shard capacity, free-space reserve, retained-count, or root-lock admission rejected |
| `5` | Authentication unavailable or rejected |
| `6` | Initial/final catalog was inexact, malformed, safety-merged, reconstructed, or changed |
| `7` | A leaderboard page/scope was failed, incomplete, or inconsistent |
| `8` | Package creation, artifact write, validation, or atomic seal failed |
| `130` | Caller or process-signal cancellation |

Success output contains only the capture ID, package root hash, bounded counts,
and `noPublication=true`. Failure output contains only a typed failure, exit
code, and fixed sanitized message. Credentials, tokens, request headers, the
authenticated caller configuration, and configured addresses are never
written to the package or terminal output. Leaderboard participant account
identifiers are intentionally retained in the response artifacts and require
the same access and retention controls as leaderboard history. After output
admission, any
authentication, catalog, page, cancellation, reserve, or seal failure leaves
the attempt unsealed and marked interrupted. Existing packages are never
overwritten or deleted. Both Ctrl-C and `SIGTERM` request cancellation; either
returns `130` and uses the fixed `capture-cancelled` interruption reason.
OAuth and transport timeouts without caller cancellation retain their
authentication, catalog, or capture failure classification.

## Isolated phase replay candidate

Replay dispatch occurs before `.env` loading, `WebApplication` construction,
HTTP hosting, hosted-worker registration, provider clients, notifications, and
publication services.

The protocol-v1 execution command requires all of:

```text
--replay-parent-package <sealed-tier0-root>
--replay-package <sealed-tier1-input-root>
--replay-phase post.band_maintenance
--replay-subphase current_projection_refresh
--replay-output <new-attempt-directory>
--replay-id <manifest-replay-id>
--replay-attempt <positive-integer>
[--replay-profile <profile-id>]
--no-publication
```

Only the bounded BandMaintenance current-projection refresh kernel is
replayable. Unknown phase IDs, other stable phases, other BandMaintenance
subphases, provider/network phases, phase ranges, and publication are rejected.
The profile IDs are:

| Profile | Behavior |
|---|---|
| `deterministic-v1` | Default accepted replay overrides: no unchanged skipping, DOP 1, synchronous commit, no cleanup |
| `production-option-parity-v1` | Production skip/commit/DOP/cleanup choices within Tier-1 bounds: unchanged filtering, DOP 2, asynchronous commit, bounded cleanup |
| `production-option-parity-batched-member-stats-v1` | Option parity plus the default-off one-pass member-stat aggregation candidate |

Unknown profiles fail before target database use.

Comparison is a separate no-database command. It requires baseline/candidate
package paths, report output, exact expected image digest, Git commit, OCI
revision, attempt number for both lanes, and `--no-publication`:

```text
--replay-compare-baseline <package>
--replay-compare-candidate <package>
--replay-comparison-output <new-json-file>
--replay-baseline-image-digest <sha256:...>
--replay-baseline-git-commit <commit>
--replay-baseline-revision <revision>
--replay-baseline-attempt <positive-integer>
--replay-candidate-image-digest <sha256:...>
--replay-candidate-git-commit <commit>
--replay-candidate-revision <revision>
--replay-candidate-attempt <positive-integer>
--no-publication
```

Exit codes distinguish usage, root, package, target, import, phase, output,
comparison, cancellation, and unexpected failures. Output/comparison format
version `3` binds each lane's profile and operation counts and always emits
`productionComparableTiming=false` with a profile-specific reason. Command,
round-trip, and member-stat aggregation-pass counts are named `derived`; only
scope transactions are observed from successful results. CLI
availability does not authorize
production-derived capture, full BandMaintenance, provider access, live
replay, or deployment.

## Focused execution

| Flag | Behavior |
|---|---|
| `--test` `<song>` | Fetch a single-song diagnostic and exit |
| `--resolve-only` | Resolve unresolved account names |
| `--backfill-only` | Run existing-entry rank/percentile enrichment |
| `--precompute` | Rebuild API response precomputation from a complete current projection |

## Selectable scrape phases

Primary groups:

- `--solo-scrape`
- `--solo-leaderboards`
- `--band-scrape`

Individual flags:

- `--solo-enrichment`
- `--solo-refresh-users`
- `--solo-rivals`
- `--solo-player-stats`
- `--solo-precompute`
- `--solo-finalize`
- `--band-post-scrape`
- `--band-extraction`

`ScrapePhaseResolver` expands groups and fills intermediate solo phases. No
phase flags means the full pipeline. Launch selections apply only to the first
pass of a continuous worker. `--band-post-scrape` alone is the supported
direct legacy band-fetch mode. `--band-scrape` includes the legacy phase flag
for compatibility but the modern `BandScrape` result suppresses the duplicate
legacy fetch.

## Schema, recovery, and maintenance

| Command | Default behavior | Additional flags |
|---|---|---|
| `--initialize-schema-only` | Apply idempotent schema and exit | Cannot combine with maintenance/recovery commands |
| `--initialize-snapshot-retention-schema-only` | Apply only the bounded snapshot-retention schema step and exit, without a host | Exactly one argument; all other flags/selectors are rejected |
| `--recover-improvement-notifications` | Execute recovery for one exact published scrape | Required `--published-scrape-id`; optional `--notification-dry-run`, `--notification-baseline-only`, `--notification-skip-projection-refresh`, `--notification-force` |
| `--interrupted-acquisition-normalization` | Read-only exact-state check or atomic normalization of one interrupted acquisition attempt before official failure isolation | Requires exactly one of `--interrupted-acquisition-normalization-check` or `--interrupted-acquisition-normalization-execute` plus every namespaced identity flag described below |
| `--active-scrape-failure-isolation` | Read-only readiness/check report or explicit failure-isolation execution for one exact frozen active candidate | Required `--active-scrape-id`, `--published-scrape-id`, and exactly one of `--active-scrape-failure-isolation-check` or `--active-scrape-failure-isolation-execute`; execute mode also requires `--active-scrape-failure-phase` and `--active-scrape-failure-message` |
| `--score-history-dedup-maintenance` | Read-only deterministic report | Execute also requires `--score-history-dedup-execute` and `--expected-score-history-dedup-digest` `<sha256>` |
| `--solo-family-ranking-backfill` | Dry-run report | `--solo-family-ranking-backfill-execute` |
| `--leaderboard-rivals-recompute-account` `<id>` | Recompute one account and exit | Accepts `--flag=value` form |

Maintenance commands are mutually exclusive where enforced by `Program.cs`.
Use the matching living runbook; CLI availability is not authorization to run
against production.

`--initialize-snapshot-retention-schema-only` dispatches before replay,
`.env`, `WebApplication`, options/host registration or startup work. Supply
the existing database connection through `ConnectionStrings__PostgreSQL` in
the process environment; command-line connection/target/path/SQL overrides
are not accepted. It does not initialize FST prerequisites, mutate
publication/path/catalog/registration data, operate Docker, start workers or
publish their configuration receipts. It shares the exact retention schema
step, with 2-second lock/15-second statement/20-second command limits, a
10-second connect limit and a 30-second cancellation deadline.
Its original normalized credential-bearing configuration passes directly to
the dedicated initializer's private unpooled factory. It is never recovered
from `NpgsqlDataSource.ConnectionString`, which omits passwords with default
`PersistSecurityInfo=false`. The command keeps that setting off; credentials
remain process-memory only and never enter output/evidence.

Output is secret-free JSON with scope `snapshot_generation_retention`.
Exit `0` means `schema_current`; `64` rejects arguments, `2` reports missing or
invalid connection configuration or database refusal (including SQLSTATE),
and `130` reports cancellation/deadline exhaustion. The fresh connection is disposed
before success is emitted. Success additionally requires
`transactionCommitted=true` and a version-2 combined `dmlProof` with
`statisticsSource=pg_stat_xact_user_tables`,
`backendScope=fresh_unpooled_single_transaction`,
`allowedRetentionRelations=[]`, zero `nonRetentionDml` inserted/updated/deleted
totals, `allowedRetentionChanges=[]`, exact schema SQL SHA-256,
`nonRetentionRelationIdentity` before/after set-count and identity-digest
parity, and a deterministic SHA-256 of all ordered proof fields. The identity
scope covers non-system/non-temporary ordinary, partitioned, materialized and
foreign-table metadata across user schemas, excluding only the six exact
DDL-managed public retention tables. The exact step still has no user-table
DML allowlist entries.
Non-retention DML refuses/rolls back with code `non_retention_dml_detected`,
attempted totals and `transactionCommitted=false`.
`non_retention_relation_identity_changed` likewise refuses/rolls back
TRUNCATE, rewrite or set/OID/relfilenode drift. The static step backstop rejects
TRUNCATE/COPY FROM and INSERT/UPDATE/DELETE/MERGE without line-position
assumptions; it exposes no operator SQL selector.
`transaction_statistics_unavailable` and `transaction_dml_baseline_not_zero`
refuse unusable evidence. No SQL/table selectors or test hooks are exposed
by the CLI.

Commit-attempt acknowledgement loss exits nonzero with `outcome=uncertain`,
`code=commit_acknowledgement_unknown`, `transactionCommitted=null`, the
precommit combined proof and `possibleSchemaProof` identities. There is no
automatic retry or inference of success from already-existing schema.
Acknowledged commit followed by cleanup failure instead retains
`transactionCommitted=true` with nonzero
`committed_cleanup_unconfirmed`/`post_commit_cleanup_failed`.

The fresh backend prevents pending counts from earlier pooled-session work
from contaminating PG17 xact statistics. Schema admission precedes exclusive
canonical registration admission, held through the pre-commit assertion.
Normal migration admission and incompatible-worker
refusals remain those of `SnapshotGenerationRetentionSchema.Sql`; the external
idle stop and restart exclusion are still operator responsibilities.
Name resolution is pinned to `pg_catalog,public`, with explicit schema-qualified
DDL and built-ins.

General initialization now preserves a current valid path binding's complete
provenance and `built_at`; it is still broader than this dedicated command.
Future/malformed current/working manifest versions and invalid ready binding
contracts fail closed without rewriting those bindings. The full
`--initialize-schema-only` CLI returns exit `2` with
`path_artifact_initialization_rejected` and publication/code pairs on stderr.
Previous invalid bindings emit structured `previous_path_binding_invalid`
warnings without rewriting rows or refusing normal initialization. Ordinary
service startup handles current/working refusal before runtime pools by
selecting sticky degraded/read-only serving, not by stopping the API. This
does not change explicit schema CLI exit codes or expand the dedicated
retention-only command. Read-serving health alone cannot accept a deployment:
require mutation readiness and a fresh guarded restart after any correction.
Catalog/publication/disabled-notification compatibility writes are skipped
when their values are already correct.
See the [source-preserving deployment order](../database/SnapshotGenerationOfflineRetentionReport.md).

`--active-scrape-failure-isolation` is the code-only operator path for an
active candidate that cannot resume. It accepts only one of three exact
states: the requested scrape is still the frozen `post-process` candidate; its
publication was already failed and unfrozen but runtime convergence remains;
or acquisition failed before the durable acquisition checkpoint committed,
reads are unfrozen, the worker is offline with no current operation or running
phase attempt, and the candidate still owns the noncurrent working
publication. The frozen state additionally requires the normal post-process
freeze ID to name the preserved published scrape. Every state requires the
requested published scrape to match the live pointer and the active candidate
to differ from the published scrape. Zero published-scope source rows may belong to the candidate,
zero worker-owned database queries may remain, no waiting/advisory locks may
remain, and no maintenance progress may be active in the current FST database.
`--active-scrape-failure-isolation-check` is the machine-readable no-op mode
used by the watchdog before it stops `fstworker`; shell failure, malformed
JSON, or any identity/blocker mismatch must fail closed. Both check and execute
modes write exactly one JSON document to stdout and route startup diagnostics
and logs to stderr. Execute mode then
acquires the exclusive publication mutation fence, re-reads those exact
identity and blocker invariants under that fence, refuses any raced change,
does not fall back to shared-lock isolation, and only succeeds after the
candidate is durably failed, the working publication is released, public reads
are unfrozen, running phase attempts are interrupted, the persisted scraper
operation is moved to failed history, worker status is `offline`, and the final
blocker counts are zero.
If publication isolation already completed but those runtime fields did not,
check mode accepts only that exact failed/unfrozen/no-working-publication
state, and execute mode idempotently finishes runtime convergence under the
same exclusive fence without repeating publication mutation.
That runtime-only path also skips failed-publication artifact cleanup and
orphan sweeping.
The acquisition-failure state additionally requires a durable failed
`scrape.leaderboards` attempt and a null `acquisition_completed_at`; a valid
checkpoint rejects isolation in favor of guarded resume. Execution marks the
candidate and its publication failed, releases the working publication, keeps
reads unfrozen, and never reconstructs or fabricates acquisition metrics.
Runtime convergence is bound to the persisted worker instance ID and
freshness timestamp observed under the fence. A newer worker row or a running
phase attempt owned by another instance aborts and rolls back the convergence
transaction.

`--active-scrape-failure-phase` accepts only
`post_process_no_progress_abandoned` and
`capacity_watchdog_abandoned`, or `scrape_acquisition_failed`. The operator
must supply an explicit phase matching the fenced state:
`scrape_acquisition_failed` is exclusive to acquisition failure, while the
watchdog/capacity phases are exclusive to frozen post-process isolation.
Runtime-only convergence requires the phase already persisted on the failed
candidate. The operator must supply an explicit
`--active-scrape-failure-message`; the command does not infer acquisition or
publication metrics.

`--published-scrape-id` is parsed once for improvement-notification recovery,
active-scrape failure isolation, and max-score maintenance. Both
`--published-scrape-id 1296` and
`--published-scrape-id=1296` are accepted. The owning command requires exactly
one positive value; duplicates, blank/malformed values, and an orphaned scrape
ID without either owning command are startup errors. The shared option does not
activate max-score parsing by itself.

### Interrupted acquisition normalization

`--interrupted-acquisition-normalization` is a narrower, one-shot handoff into
the unchanged official active-scrape failure-isolation command. It does not
terminalize a scrape or publication. It only converts one exact stale
`scrape.leaderboards` attempt from `interrupted` to `failed` and moves that
same stale worker current operation into failed last-operation history.

Both modes require all of:

```text
--interrupted-acquisition-normalization
--interrupted-acquisition-normalization-check
  or --interrupted-acquisition-normalization-execute
--interrupted-acquisition-scrape-id <positive-id>
--interrupted-acquisition-published-scrape-id <positive-id>
--interrupted-acquisition-current-publication-id <positive-id>
--interrupted-acquisition-previous-publication-id <positive-id>
--interrupted-acquisition-working-publication-id <positive-id>
--interrupted-acquisition-worker-instance-id <exact-instance>
--interrupted-acquisition-worker-freshness-utc <UTC-microsecond-timestamp>
--interrupted-acquisition-phase-id scrape.leaderboards
--interrupted-acquisition-attempt <positive-attempt>
```

The command rejects unknown, duplicate, missing, blank, non-UTC, and
sub-microsecond arguments. Current, previous, and working publication IDs must
be distinct. The worker freshness value binds `updated_at`,
`last_heartbeat_at`, and `last_status_change_at`; execute never advances those
timestamps.

Check mode starts one repeatable-read, read-only transaction, acquires the
shared publication advisory fence, and writes exactly one JSON document. It
requires the exact published/current/previous/working pointers, an unfrozen
singleton with no commit intent, one running uncheckpointed candidate, its
single building generation, no newer or other running scrape, no candidate
source mappings, no worker query, waiting/advisory lock, maintenance progress,
or running phase attempt, and exactly one terminally timestamped interrupted
attempt. The scraper worker must be offline in scraper mode with the exact
instance/freshness identity. Its canonical version-2 current operation must
name the same scrape, operation ID, phase ID, phase ordinal, plan version, and
attempt.

Execute first performs that read-only check, then independently acquires the
exclusive publication fence and row-locks/revalidates the singleton, scrape,
generation, attempt, and worker identities. One transaction changes only:

- the exact attempt `status` to `failed` and its `error_message` to the fixed
  normalization message; and
- the exact worker `current_operation_json` to `NULL`, with that operation
  copied to `last_operation_json` as failed at the preserved attempt terminal
  timestamp.

Scrape status/metrics, publication and generation rows, freeze state, source
mappings, caches, schema, files, and every other phase/worker row remain
unchanged. Missing schema rejects before mutation; this mode never initializes
schema or registers hosted services. An exact retry reports
`already_normalized`; a replacement worker, freshness change, foreign/current
operation, running/new attempt, or pointer/checkpoint/generation drift rejects.

After commit, the result includes a fresh call to the existing
`GetActiveScrapeFailureIsolationReadiness`, followed by a final exact-state
reread under the shared publication fence. Success requires that unchanged
official readiness to report
`AcquisitionFailureMutationRequired=true` and that the final reread still
matches every supplied scrape, generation, freeze, pointer, attempt, worker
instance/freshness, and offline/current-operation identity, with exactly one
normalized failed attempt and no other or running attempt. A replacement
worker, newer attempt, or any other drift fails closed; an already-normalized
retry is not exempt. The operator then runs the ordinary
`--active-scrape-failure-isolation-check` and explicit
`--active-scrape-failure-isolation-execute` commands with failure phase
`scrape_acquisition_failed`. CLI availability is not production authorization;
follow [live safety](../operations/live-safety.md).

### Max-score correction

All max-score files must be `.json` paths below `Scraper:DataDirectory`.
Stage requests and manifests use canonical strict JSON; unknown properties,
noncanonical encoding, unsupported versions, duplicate/unsorted song IDs, and
more than 32 songs are rejected. Request version 2 binds a discovery or
promotion purpose, exact runtime, exact generated/changed instrument sets, and
per-song maximum constraints. Every non-null complete maximum or partial
constraint must be at most `2,045,222,521`, the largest value whose exact
`1.05` ranking cutoff fits a PostgreSQL `INTEGER`. Unscoped repeated song IDs
are rejected.

| Action | Required flags | Behavior |
|---|---|---|
| `--max-score-maintenance-stage` | `--published-scrape-id`, `--max-score-maintenance-stage-request`, `--max-score-maintenance-manifest-output`, `--max-score-maintenance-report-output` | Serially stage complete immutable generations without pointer mutation; discovery permits explicit partial maximum constraints, while promotion requires complete old/new eight-field maxima |
| `--max-score-maintenance-plan` | `--published-scrape-id`, promotion-purpose `--max-score-maintenance-manifest`, `--expected-max-score-manifest-digest`, `--max-score-maintenance-report-output` | Read-only fail-closed preflight; rejects discovery/v3 plastic manifests, validates current rollback and staged artifact trees/hashes, records mapped raw/eligible/outlier observed-score evidence plus publication-population and complete consumed score-history count/range/hash evidence, and emits the deterministic `planDigest` |
| `--max-score-maintenance-apply` | plan flags plus `--expected-max-score-plan-digest` and `--max-score-maintenance-rollback-output` | Freeze, persist rollback evidence, atomically promote all songs, rebuild derived state, quarantine notifications, stage/publish caches, validate, and unfreeze |
| `--max-score-maintenance-resume` | apply manifest/scrape/digest flags and a new report output; rollback output is required only before it has been durably captured | Resume only the same digest/phase identities; phase checkpoints skip completed mutation families, failures remain frozen, and the recovery lease yields the publication lock between bounded commit fences |
| `--max-score-maintenance-rollback` | `--published-scrape-id`, manifest, expected manifest/plan digests, `--max-score-maintenance-rollback-file`, `--expected-max-score-rollback-digest`, and a new report output; optional `--max-score-maintenance-rollback-dry-run` | Validate or execute exact resumable rollback: restore pre-apply paths, rebuild complete affected derived state, quarantine notifications, restage/validate caches, record `rolled_back`, and atomically unfreeze |

Every action writes a versioned report. Apply/resume exit `2` with
`resumable=true` after a post-freeze failure. Do not manually clear the freeze;
rerun `--max-score-maintenance-resume` with the same manifest and digests. The
rollback snapshot timestamp comes from the persisted maintenance run, so a
crash after file creation but before its database checkpoint reproduces and
validates the same canonical bytes.

Rollback report version `2` records dry-run/validation/terminal state, durable
rollback phase, exact manifest/plan/rollback digests, publication IDs,
before/after path fingerprints, restored/rebuilt/quarantined/cache counts,
aggregate cache evidence, per-stage timestamps/status, and failure detail.
`cleanupPending=true` means rollback data committed and reads are unfrozen, but
the durable mutation gate still requires a retry; it is validated,
non-successful, and resumable until cleanup is verified.
Dry-run validates without taking the maintenance lease or mutating state.
Execution is resumable from `rollback_validating` through
`rollback_validated`; only terminal `rolled_back` is successful and unfrozen.
Rollback rejects completed applies, pre-promotion path state, changed paths,
rollback-file/database divergence, active maintenance/worker backends, waiting
locks, or any publication/freeze mismatch. A `rollback_captured` run is
accepted only when exact promoted path identity proves the path transaction
committed before its phase checkpoint. Execution validates both accepted
post-promotion and restored-maximum score-history selectors, revalidates the
canonical rollback file before terminal unfreeze, and holds the publication
lock only at bounded transaction commit fences. Terminal and already-rolled-back
invocations verify the durable mutation-owner fields are cleared, reacquiring a
cleanup lease after backend loss when necessary.
Dry-run against terminal `rolled_back` is a read-only rejection and does not
perform that cleanup. Apply/resume after any rollback-owned phase returns a
non-resumable rejection with the actual freeze state.
The report output path is normalized and atomically reserved before rollback
preflight, so an existing path or collision with the manifest/rollback input
fails before any database mutation.
An unwritten reservation is removed when manifest parsing prevents creation of
a typed failure report.
Once a run enters `rollback_validating`, apply/resume is rejected; interruption
must continue with the same rollback command and identities plus a new report
path.

Plan report version 6 includes `populationEvidence`, `scoreHistoryEvidence`,
and, on every `observedScoreChecks` row, `validCutoff`,
`highestObservedScore`, `highestEligibleObservedScore`, and
`aboveValidCutoffCount`. The CHOpt maximum is the score-percent denominator.
The separate eligibility cutoff is exactly
`RankingsCalculator.ComputeMaxScoreThreshold(newMaximum)`, currently
`floor(newMaximum × 21 / 20)`. Rows above the denominator but at or below the
cutoff remain eligible. Rows above the cutoff remain visible in the raw maximum
and count but are ranking-invalid evidence, not plan blockers; the eligible
maximum is null when no resolved row qualifies. Target request, actual
current/staged, manifest, and report validation rejects
`newMaximum > 2,045,222,521`. Unrelated frozen-catalog maxima use the general
exact computation saturated at `int.MaxValue`, so plan relevance selection
and PostgreSQL `INTEGER` parameters remain safe without admitting an
overflowing target. Missing source mappings, invalid maximum/cutoff evidence,
and eligible scores above the cutoff fail closed. Plan-digest contract version
6 binds the raw maximum, eligible maximum, and outlier count. Apply rebuilds
the plan before freeze; apply and resume reload the exact observed evidence
and reconstruct the approved digest before mutation, so outlier-population
drift rejects apply.

Apply/resume report version 3 includes
`cacheEvidence` after cache staging, including the exact
publication-scope key count/fingerprint. Exact per-entry key/ETag/JSON hashes
remain durable database evidence rather than expanding the report. Plan may scan all registered-account
history plus affected-instrument fallback candidates; its aggregates are
constant-memory, but operators must allow the documented maintenance-window
cost. The strict apply-report parser rejects legacy version 2, unknown
properties, and version 3 reports missing `cacheEvidence` at
`caches_staged` or any later phase. Version 3 failures before cache staging
retain `cacheEvidence=null`.

Plan/apply/resume evidence and revalidation use
`Scraper:MaxScoreMaintenanceCommandTimeoutSeconds` uniformly. The default is
`600`; production may pass
`Scraper__MaxScoreMaintenanceCommandTimeoutSeconds=1800`, and startup rejects
values outside `1`-`86400`. This does not alter normal scrape timeouts. A
final completion transaction uses the configured server timeout only for
immutable cache validation, keeps its `5s` lock timeout, and restores the
`120s` mutation timeout before swap/checkpoint/unfreeze. Any failure remains
frozen. A failed plan's `plan` check identifies the sanitized evidence stage
and the base exception message without serializing SQL or connection data.

The retired `--path-repair-*` and
`--notification-maintenance-pro-lead-max-score-repair` families remain startup
errors in every supported prefix/value form.

See
[Max-score correction maintenance](../database/MaxScoreCorrectionMaintenanceRunbook.md).
