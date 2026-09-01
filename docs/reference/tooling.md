---
status: canonical
owner: repository
last_verified: 2026-08-30
last_verified_commit: 21d7193c
sources:
  - tools/
  - FSTService/Persistence/Maintenance/DatabaseMaintenanceDryRunReporter.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionRepository.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.cs
  - FSTService/Api/PublicApiCacheTelemetry.cs
  - tools/fst-worker-compose-guard.sh
  - tools/fst-worker-compose-guard.test.mjs
  - tools/fst-worker-no-progress-watchdog.mjs
  - tools/postgres-tier1-replay-drill.sh
  - tools/postgres-tier1-replay-drill.test.mjs
  - tools/postgres-retire-ix-le-song-rank.sh
  - tools/postgres-retire-ix-le-song-rank.py
  - tools/postgres-retire-ix-le-song-rank.test.py
  - tools/postgres-pro-bass-snapshot-rewrite.sh
  - tools/postgres-pro-bass-snapshot-rewrite.py
  - tools/postgres-pro-bass-snapshot-rewrite-drill.sh
  - tools/postgres-pro-bass-snapshot-rewrite.test.py
  - tools/postgres-snapshot-generation-archive.py
  - tools/postgres-snapshot-generation-archive.sh
  - tools/postgres-snapshot-generation-archive.test.py
  - tools/postgres-snapshot-generation-archive.test.sh
  - tools/postgres-snapshot-generation-archive-drill.py
  - tools/testdata/postgres-snapshot-generation-archive-csharp-fixture/
  - tools/testdata/postgres-snapshot-generation-archive-extra-volume.Dockerfile
  - tools/FstSnapshotGenerationQuarantine/
  - tools/postgres-snapshot-generation-quarantine.sh
  - tools/FstSnapshotGenerationEvidence/
  - tools/FstSnapshotGenerationDrop/
  - tools/FstSnapshotGenerationRestoreAuthorization/
  - tools/postgres-snapshot-generation-drop.sh
  - tools/postgres-snapshot-generation-restore-authorize.sh
  - tools/postgres-snapshot-generation-restore.py
  - tools/capture-snapshot-generation-drop-health.py
  - tools/postgres-snapshot-generation-drop-drill.py
  - tools/capture-publication-route-contract.sh
  - deploy/fst-compose.sh
  - FortniteFestivalWeb/package.json
  - FortniteFestivalWeb/scripts/check-coverage-ignores.mjs
  - tools/check-coverage-ignores.test.mjs
  - .github/skills/
update_triggers:
  - A repository tool, wrapper, generated-artifact command, MCP surface, or agent skill is added, removed, or changes purpose.
---

# Tooling

Use repository tools through their documented wrapper instead of copying
implementation fragments into ad hoc commands.

## Main categories

| Area | Entry points |
|---|---|
| Compose/deployment | `deploy/fst-compose.sh`, `tools/fst-worker-compose-guard.sh`, `tools/fst-worker-dual-lane-runonce.sh`, `tools/scripts/gluetun-manage.sh` |
| PostgreSQL maintenance/evidence | `tools/postgres-*.sh`, `tools/sql/`, living database runbooks |
| Documentation | `tools/check-docs.mjs` |
| Secret/encoding/license/coverage checks | `tools/secret-scan.mjs`, `tools/check-encoding.mjs`, `tools/generate-license-manifest.mjs`, `FortniteFestivalWeb/scripts/check-coverage-ignores.mjs` |
| Autonomous reports | `tools/agent-report-email.mjs` |
| Production MCP adapters | `tools/mcp/` |
| Pak extraction | `tools/FortnitePakExtractor/` |

## Bundled CHOpt

`tools/chopt-cli-linux/` contains the pinned Linux CHOpt CLI, launcher,
runtime libraries, license, and provenance README used by FSTService path
generation. Changes under that directory are service-image changes even when
no `FSTService/` source file changes; the publish workflow and its contract
test enforce that classification.

See [Path generation](../components/path-generation.md) for the JSON contract,
profile invalidation, canaries, and regeneration gates.

## Web scripts

`FortniteFestivalWeb/package.json` is the command inventory for unit/shared
tests, coverage, Playwright, linting, manual screenshots/images, embedded
bundle checks, performance capture, icons, licenses, and encoding.

`corepack yarn check:coverage-ignores` runs the repository mutation tests and
then validates every web V8 ignore directive against the installed coverage
parser contract. It rejects non-comment markers, nesting, orphan/EOF ranges,
ranges over 50 lines, unsupported counted-next syntax, and stale verified-next
allowlist entries before coverage cleanup or shard execution begins.

Yarn 4 is authoritative for the web app. Standalone tools such as `tools/mcp/`
may use their own npm lockfile and commands. The web workspace ignores
`package-lock.json`, and both contributor and container installs use the
committed Yarn lock through `corepack yarn install --immutable`.
Web tests declare Node 20 types directly; KaTeX and jsdom use their maintained
package declarations rather than redundant `@types/katex` or `@types/jsdom`
dependencies.

## Database and deployment tools

Database scripts are not generic production authorization. Use the matching
runbook and live-safety gates. The worker Compose guard validates the standard
PIA overlay, role flags, aligned proxy arrays, dependencies, and supported data
profiles before a guarded recreate.

### Publication API cache evidence

The protected `GET /api/admin/public-cache-telemetry` snapshot reports the
existing route hit/miss counters plus a bounded 256-operation trace for the
two-tier cache. Trace rows contain route pattern, SHA-256-derived cache-key ID,
publication and content revision, L1/L2/miss/build/wait/error outcome, duration,
payload bytes, cached timestamp, and error type. They never expose the raw key,
account ID, team key, or response body.

Candidate performance evidence uses:

```bash
dotnet test FSTService.Tests/FSTService.Tests.csproj -c Release \
  --filter FullyQualifiedName~PublicationApiCacheBenchmarkTests \
  --logger 'console;verbosity=detailed'
```

The benchmark exercises a 723 KB songs payload against PostgreSQL L2 and L1,
then a 10,000-row publication surface write-through. Production read-only
probes separately measure every allowed lazy overview metric at page sizes 25
and 50. These artifacts are candidate evidence only, not deployment approval.

### Exact stale solo rank-index retirement

`tools/postgres-retire-ix-le-song-rank.sh` is the only supported retirement
entry point for `public.ix_le_song_rank` and its nine attached leaves. It has
no arbitrary index-name option.

`--check` is read-only and emits a checksummed manifest, dated zero-use
observation, exact drop plan, and exact rollback DDL beneath the FST evidence
root. `--execute` requires the reviewed manifest, observation, rollback, and
all three SHA-256 values. It revalidates production project/cluster identity,
idle/unfrozen publication state, offline worker state, locks/activity, exact
OIDs/definitions/dependencies/bytes, and zero scans before one short-timeout
normal parent drop. The package holds the standard nonblocking worker
start/recreate host lock for the complete execute lifecycle; check mode only
records whether the lock is available or already held by the external worker
hold.

The parent cannot use `DROP INDEX CONCURRENTLY` on PostgreSQL 17. Rollback
creates the parent `ON ONLY`, builds nine leaves concurrently, then attaches
them. Follow the
[living runbook](../database/StaleSoloRankIndexRetirementRunbook.md).

Production retirement completed on 2026-08-17. Current check mode returns
`already_absent`; execute is idempotent for that exact state. The checksummed
rollback remains retained but was not run.

Validate structure with:

```bash
bash -n tools/postgres-retire-ix-le-song-rank.sh
PYTHONDONTWRITEBYTECODE=1 \
  python3 tools/postgres-retire-ix-le-song-rank.test.py
```

### Max-score rollback one-shot

The canonical rollback executor is part of `FSTService.dll`, not an ad-hoc SQL
or shell script. Use only the exact command in the
[max-score correction runbook](../database/MaxScoreCorrectionMaintenanceRunbook.md).
It consumes canonical manifest/rollback files under `Scraper:DataDirectory`,
the three expected SHA-256 gates, and a new report path. The optional dry-run
performs no lease or mutation. Execution owns durable rollback phases and
separate immutable rollback cache evidence. There is no supported manual
freeze clear, phase edit, partial path update, cache swap, or generated SQL
fallback.

### Tier-1 isolated replay drill

`tools/postgres-tier1-replay-drill.sh` compares two immutable FSTService images
against the same sealed synthetic Tier-1 parent. It accepts only a new root
beneath the 4 TB FST evidence/replay directories.

Each lane receives:

- a separate fresh PostgreSQL 17 container with no published ports;
- a network-none namespace shared only with that lane's FSTService process;
- a non-superuser replay role rather than bootstrap-administrator credentials;
- read-only parent/input mounts and one lane-specific writable output mount;
- no Docker socket, provider credentials, normal production configuration, or
  access to the other lane's writable evidence.

PGDATA lives in a candidate-inaccessible sibling scratch directory and is
removed through the retained container/host-controlled path. The baseline image
is the comparator, every image reference is resolved once to its immutable ID,
and comparison enforces each lane's expected digest, Git commit, OCI revision,
and attempt before parity. Preserve the emitted sealed packages,
`comparison.json`, `run.json`, report, and checksums on the FST drive.
`productionComparableTiming=false` remains mandatory; elapsed/resource deltas
are diagnostic only.

Both lanes default to `deterministic-v1`. Optional `--baseline-profile` and
`--candidate-profile` accept only the three replay profile IDs documented in
[FSTService CLI](cli.md). Drill/report format version `3` records both profiles
and successful scope transaction metrics plus explicitly derived command,
round-trip, and member-stat aggregation-pass estimates. The
option-parity/batched candidate pair must keep the first three values equal and
reduce the final derived estimate before the drill succeeds. A profile-only
query-shape A/B should pass the same v3-capable
immutable image for both lane image arguments so the profile is the only
implementation variable. Exact output hashes remain mandatory regardless of
timing.

Validate tool structure with:

```bash
bash -n tools/postgres-tier1-replay-drill.sh
node --test tools/postgres-tier1-replay-drill.test.mjs
```

### Snapshot-generation report-only evidence and archive proof

There is no new public/protected API or source-mutation CLI. Planner visibility
uses worker logs plus the existing read-only PostgreSQL tooling pattern.

The durable relations are:

- `snapshot_generation_retention_cycles`;
- `snapshot_generation_retention_observations`;
- `snapshot_generation_retention_deferrals`;
- `snapshot_generation_retention_holds`;
- `snapshot_generation_retention_evidence`.

Read cycles by newest `created_at, cycle_id`, then read child observations by
`cycle_id, snapshot_id, instrument`. A clean report requires
`oracle_agreement=true`, no global/child blockers, exact planner/oracle JSON
sets, matching stable hashes, and matching primary/SQL-oracle named-publication
binding validation in the immutable summary evidence. Worker logs expose FIFO
enqueue, non-cancelling registration-drain waits, bounded yield with the queue
retained, retry retention, and terminal dequeue; a later publication never
replaces the queued head. `row_estimate` and `total_bytes` are observational
only and are intentionally outside the candidate identity hash.

Inspect the cycle's `anomalies` JSON alongside `global_blockers`. Unnamed
retained legacy publications appear there as structured
`unpointed_retained_publication` warnings and in the immutable summary payload.
They are included in `observation_hash` but are intentionally compatible with
a clean `observed` cycle and nonzero candidates. Planner-v3 terminal unnamed
failed publications with no live recovery artifact appear as
`unpointed_terminal_failed_publication`. Their nested `publicationFailure`
object records publication/scrape status and identity, terminal timestamps,
named/resume/state references, binding/cache/staging/catalog/path/band counts,
orphaned published-source rows, unreplayed writer failures, and canonical
recovery reasons. Source rows alone remain warning provenance; writer failures
still protect only exact `(instrument, scrape_id)` children. A failed
publication with any recovery reason remains in `global_blockers`. Historical
planner-v1/v2 cycles are not rewritten.

Do not count tests or repeated reads as rollout evidence. Archive-only
development requires two clean terminal cycles. Destructive enablement
requires five exact-agreement cycles including a publication rotation and a
real candidate-set change. See
[Snapshot generation retention safety](../database/SnapshotGenerationRetentionSafety.md).

The separate archive-only entry point is:

```bash
tools/postgres-snapshot-generation-archive.sh archive \
  --output /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-generation-archives/<new-package>

tools/postgres-snapshot-generation-archive.sh prove \
  --package /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-generation-archives/<completed-package> \
  --keep-proof-outputs
```

`archive` defaults to the deterministically oldest candidate ordered by
snapshot ID, instrument, and child OID; this is not a physical-size ranking.
The DROP tool's read-only `select-canary` command performs the true
`pg_total_relation_size` ranking. Exact archive selection requires both
`--instrument` and `--snapshot-id`; the
instrument is one of nine fixed keys and the pair must still be a candidate in
that newest cycle. It authenticates all cycle observations, exact versions,
canonical sets/hashes, summary validations, and evidence-chain links. The CLI
accepts no relation name or SQL text; source SQL is restricted to internal
single-statement read forms.

Canonical evidence uses exact C# `Utf8JsonWriter` escaping. Production
validation record arrays and their original order remain unchanged for
summary/evidence hashing; sorted exact `comparisonKey` values are used only for
planner/oracle agreement. Once source discovery finishes, queries, TOC,
fingerprints, and `pg_dump` use the immutable container ID; complete provenance
is checked again before and after streaming.

An accepted package contains `archive.custom`, `archive.toc`, `catalog.json`,
`manifest.json`, and `SHA256SUMS`. `prove` verifies those files before restore,
uses a PostgreSQL 17 network-none/no-port container, and always removes its
container and owned PGDATA after proving container absence. Packages must use
the dedicated archive root and cannot overlap source PGDATA, tablespaces,
mounts, or Docker root. A reservation lock and current physical-size/free-space
gate serialize admission. The optional flag retains detailed proof outputs;
cleanup and the checksummed proof manifest are retained either way.
Pre-provision
`fst-data/evidence/.snapshot-generation-archive-operation.lock` as a regular
non-symlink file. The CLI opens it read-only without creating it and validates
archive-root/protected mount identity before and after lock acquisition and
again before the first output write.
Mount fencing compares findmnt source, filesystem root, device, and target,
rejects bind aliases and nested mount boundaries, and uses structured Docker
`--mount` binds. Cleanup uses `docker rm -f -v` and verifies every captured
anonymous volume is absent.
Before creating `proofs`, a proof directory, marker, PGDATA, cleanup, or
rejection evidence, the tool checks existing/prospective parent mount identity,
nested boundaries, and protected-source aliases. It revalidates the parent
immediately before atomic proof-directory reservation.

The first accepted live invocation archived Pro Cymbals snapshot `1314` from
cycle `9` and restore-proved it with:

```bash
tools/postgres-snapshot-generation-archive.sh archive \
  --output /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-generation-archives/cycle9-pro-cymbals-1314 \
  --instrument pro-cymbals \
  --snapshot-id 1314

tools/postgres-snapshot-generation-archive.sh prove \
  --package /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-generation-archives/cycle9-pro-cymbals-1314 \
  --proof-id live-cycle9-1314 \
  --postgres-image fst-postgres:17-repack \
  --keep-proof-outputs
```

The archive and proof were accepted without source mutation. This command
pair is recovery evidence only, not a detach/drop executor.

Static/unit and disposable synthetic validation:

```bash
bash tools/postgres-snapshot-generation-archive.test.sh
python3 tools/postgres-snapshot-generation-archive-drill.py
```

The drill creates authentic planner hashes/evidence, confirms placeholder-hash
rejection, compares the full source row fingerprint and logical catalog, and
removes only synthetic containers and same-drive scratch. It never connects to
`fst-postgres`. The archive test references actual FSTService validation record
and canonical serializer types with multiple nonempty record arrays. The drill
rejects an image with an extra `VOLUME` and proves its anonymous volume is
removed.

### Snapshot-generation quarantine and rollback

`tools/postgres-snapshot-generation-quarantine.sh` builds and invokes the
standalone .NET executor. It connects directly with Npgsql and never invokes
Docker. Its only commands are `plan`, `quarantine`, `attest`, and `reattach`;
there is no drop/truncate/delete command and no arbitrary relation or SQL
argument.

All paths must be new or existing files below
`FST_SNAPSHOT_QUARANTINE_EVIDENCE_ROOT`, which itself must resolve below the
canonical FST evidence directory. Inputs reject symbolic links. The connection
string is read only from
`FST_SNAPSHOT_QUARANTINE_CONNECTION_STRING` or the environment variable named
by `--connection-env`.

Generate each immutable 55-route capture with:

```bash
FST_ROUTE_EVIDENCE_ROOT=<FST-drive-run-root> \
FST_ROUTE_SAMPLE_ACCOUNT_ID=<registered-account-id> \
API_BASE=http://127.0.0.1:3001 \
tools/capture-publication-route-contract.sh \
  <new-directory-below-FST_ROUTE_EVIDENCE_ROOT>
```

The capture requires an idle, unfrozen publication before and after all
requests. It records 55 route statuses, raw bodies, normalized JSON, timings,
hashed sample identifiers, and `SHA256SUMS`. The executor authenticates raw
sizes/hashes, normalizes JSON from raw bytes, compares deterministic binary
responses exactly, and recursively compares ZIP exports after excluding only
generated outer filename timestamps and Office core-property IDs/timestamps.
Workbook sheets and all other payload entries remain byte-compared.

Create a sealed read-only plan:

```bash
tools/postgres-snapshot-generation-quarantine.sh plan \
  --archive-package <newest accepted archive package> \
  --proof-manifest <accepted proof-manifest.json> \
  --source-evidence-manifest <matching full-scrape manifest.json> \
  --baseline-route-manifest <same-publication baseline/manifest.json> \
  --candidate-route-manifest <same-publication candidate/manifest.json> \
  --output <new plan.json>
```

The archive cycle, full scrape, both route captures, and current database
publication must be identical. `plan` also requires the latest accepted
five-cycle planner state and recomputes the exact archived row fingerprint.
Quarantine then structurally classifies exactly the supported PK and score
btree indexes, renames the existing index OIDs to
`sgqi_<full-operation-id>_{pk|score}`, and records immutable old/new
name/constraint, OID/relfilenode, semantic, phase, backend, and transaction
evidence. A PK-index rename also renames its constraint. Reattach verifies
that evidence or applies the same normalization as an atomic repair for a
pre-change operation; it never targets an unrelated relation by name.

After explicit operator approval:

```bash
tools/postgres-snapshot-generation-quarantine.sh quarantine \
  --plan <plan.json> \
  --expected-plan-digest <sha256> \
  --approved-by <operator> \
  --approval-reference <approval-evidence> \
  --output <new quarantine-report.json>
```

The first `quarantined` attestation compares the plan's candidate capture with
a post-detach capture. A later `soak` attestation uses two exact captures of
the then-current publication. Publication rotation is allowed only if the
target remains absent from every live/recovery root.

```bash
tools/postgres-snapshot-generation-quarantine.sh attest \
  --plan <plan.json> \
  --expected-plan-digest <sha256> \
  --stage quarantined \
  --baseline-route-manifest <plan candidate capture> \
  --candidate-route-manifest <post-detach capture> \
  --attested-by <operator> \
  --output <new attestation.json>

tools/postgres-snapshot-generation-quarantine.sh reattach \
  --plan <plan.json> \
  --expected-plan-digest <sha256> \
  --reattached-by <operator> \
  --reattach-reference <rollback-evidence> \
  --output <new reattach-report.json>
```

After reattach, capture again and record a `reattached` attestation whose
baseline is the candidate capture from the latest successful soak. The
database refuses reattach without successful `quarantined` and
current-publication `soak` evidence.

The first accepted live run used plan digest
`d7d9305ae11061d3ce88de892d0a248096ee35211f464ab9018e67c5f9849550`
and operation `73bee4a09dc7648b98b7176c32616f2f` for Pro Cymbals snapshot
`1314`. It passed all three attestations and exact physical rollback. This is
authorization evidence for the quarantine/reattach tier only, not a drop.

Q1 operation `1b44941dc5d5ea806dabc2187c3cffed` later passed scrape
`1335`, publication rotation `159` to `162`, cycle `15`, and the
publication-162 soak. Its first reattach failed closed with `42P07` because a
new Solo Guitar child reused the target secondary-index name while the target
was private. At that incident boundary, no residue committed and the exact
target remained private with its hold/fences and index OIDs intact. Later live
progression reached an independently approved DROP attempt; it failed before
DDL with `42703` because the empty initial operation table lacked semantic
columns. No child was dropped in that attempt. After the explicit upgrade,
operation `333ba4b9fb69dbc098d127f0008ec709` committed with plan digest
`fa45ca20c2c975e543b7d539d3b27cb05c5d80ff16345665205f2355eb67d5dc`;
restore is now mandatory.

### Snapshot-generation DROP and logical restore

`tools/postgres-snapshot-generation-drop.sh` runs a prebuilt
`FstSnapshotGenerationDrop` assembly only. It verifies the DLL against
`FST_SNAPSHOT_DROP_BINARY_SHA256`; it never builds at execution time and does
not invoke Docker. The executable depends only on Npgsql and the
`FstSnapshotGenerationEvidence` archive/quarantine evidence-contract library;
its emitted dependency graph contains no `Docker.DotNet` or FST service host.

Its command surface is exactly:

- `select-canary`: read-only selection by current physical bytes from the
  newest accepted cycle;
- `plan`: authenticate Q1/Q2, archive/proof, source, routes, health, binary,
  restore image/tool, database identity, and a new recovery bundle;
- `drop`: execute one exact private-child non-cascading DROP after independent
  approval;
- `confirm`: classify an uncertain commit without mutation;
- `attest`: record only `pre_drop`, `dropped`, or `post_publication` parity.

Official scrape `1333`, publication `157`, and immutable cycle `13` are
accepted; that cycle identified Pro Cymbals snapshot `1314` as the true
smallest candidate at 4,628,480 bytes. Current cycle `15` records it absent
at the historical Q1 incident boundary, so `select-canary` must not treat the
cycle-13 result as current. This does not live-accept the DROP tier.

The destructive commands accept expected IDs for confirmation but no relation,
schema, SQL, batch, force, or automatic-selection input. Required environment:

```bash
export FST_SNAPSHOT_DROP_EVIDENCE_ROOT=<FST-drive-run-root>
export FST_SNAPSHOT_DROP_CONNECTION_STRING=<direct-Npgsql-connection>
export FST_SNAPSHOT_DROP_BINARY_SHA256=<approved-dll-sha256>
```

The database functions are `SECURITY INVOKER` and revoked from `PUBLIC`; this
repository provisions no role grants. The DROP transaction retains the
existing Q2 DEFAULT fence so its behavioral lock set is exactly
`ShareLock` on that DEFAULT child and `AccessExclusiveLock` on the private
child, with no top/root/sibling relation lock.
Initializer deployment must complete the explicit empty-table DROP/restore
operation-schema upgrade before rebuilding or invoking the tool. A nonempty
pre-semantic table raises `55000`; there is no hash-backfill command.

`tools/capture-snapshot-generation-drop-health.py` records the fixed
30-minute/60-sample pre-DROP health contract.

`tools/postgres-snapshot-generation-restore.py` is a separate command surface:
`plan`, `restore`, `confirm`, `attest`, and `finalize`. It imports the archive
module rather than extending the archive-only parser. It authenticates exactly
the child `TABLE`, `TABLE DATA`, primary-key `CONSTRAINT`, and secondary
`INDEX` entries; parent and attach entries reject, while the executable list
contains only `TABLE` and `TABLE DATA`. Archived index DDL is never executed.
The PostgreSQL 17
client container receives the package/list/password file as read-only mounts
and receives no Docker socket. Restore planning remeasures the source PGDATA
filesystem and requires it to share the FST device with the sealed bundle.
The guarded database phase requires exact fixed btree semantics, creates
`sgri_<full-restore-operation-id>_{pk|score}` only from repository-owned SQL,
and leaves any unrelated object with an archived name untouched. Attestation
recomputes the exact row fingerprint and name-insensitive semantic catalog.
Raw archive/catalog/config hashes remain independent package provenance; Q1/Q2
equality binds stable child identity, fixed index semantics, and exact
physical root/top chains rather than leaf names or raw archive bytes. The
restored child remains hold- and trigger-protected until `finalize` removes
the trigger and releases the hold atomically.

`tools/postgres-snapshot-generation-restore-authorize.sh` invokes a separate
prebuilt .NET executable with only `prepare-repair-package`,
`authorize-repair-tool`, and `confirm-repair-tool`. It has no Docker,
restore, object-target, or arbitrary SQL argument. The tool-only repair
package exact-set binds the old pin, reviewed validator base, final executing
tool,
byte-identical archive helper, authorizer binary, source tree/diffs, tests,
original plan/report, and immutable bundle manifest. `restore.py` exposes no
authorization command and can consume only an explicit database authorization
for its own current SHA.
The rolling schema removes the observed 13-argument live and intermediate
16-argument restore overloads before installing the 21-argument signature.
Authorization reports expose both the client evidence hash and the
independently computed database JSONB hash.

The shared authorization resolver emits one lookup query for plan, restore,
confirm, attest, and finalize. Its table alias is the explicit non-keyword
`auth_row`; never use `authorization` as a PostgreSQL alias. H3 remains
historical failed-plan evidence. H4 used a separate package and immutable
authorization; the alias correction changed no schema or command surface.

H4 subsequently exposed a separate archive-shape compatibility defect:
cycle-16 opclass/collation OID arrays are canonical decimal strings. H5
normalizes only those arrays through a strict PostgreSQL OID parser before
fixed PostgreSQL-17 value and child/root/top equality checks. JSON integers
remain accepted; booleans, noncanonical strings, fractions/exponents,
overflow, and zero opclass OIDs reject. Counts, key attnums, and index options
remain number-only. H3/H4 authorizations are not reusable for H5, and the
existing schema admits a third exact-DROP authorization while no restore row
exists.

DROP plan/report validation reads the original C# canonical UTF-8 bytes.
Python object reserialization is not authoritative. The scanner requires
unique ordinal-sorted top-level properties, canonical key encoding, no
out-of-string whitespace, one final LF, and no trailing data. It removes only
the top-level identity member spans before hashing, preserving nested bytes,
escaping, property order, and numeric representation exactly.

Detailed command order, rollback, and acceptance gates are in
[Snapshot generation DROP and logical restore](../database/SnapshotGenerationDropRunbook.md).

Validate locally with:

```bash
dotnet test FSTService.Tests/FSTService.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~SnapshotGenerationDrop|FullyQualifiedName~SnapshotGenerationQuarantine|FullyQualifiedName~SnapshotGenerationPartition'

PYTHONDONTWRITEBYTECODE=1 \
  python3 tools/postgres-snapshot-generation-restore.test.py

tools/postgres-snapshot-generation-drop-drill.py \
  --work-root /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-generation-drop-drills/<new-run>
```

Drill report schema v2 records the exact regression names and source digest
covering archive/proof, Q1 rotation collision and repair, the post-reattach
cycle gate, Q2 DROP, and fixed-DDL restore with an unrelated archived-name
collision.

### Legacy snapshot rewrite evidence

Snapshot-retention report harnesses must call only
`DatabaseMaintenanceDryRunReporter.BuildSnapshotRetentionRewritePlansAsync`
through a read-only PostgreSQL transaction and a distinct application name.
Keep their source, build output, result, resource monitor, and checksums on the
FST drive.

Report-only estimates are not executable reclaim proof when `CanExecute=false`.
In particular, `EstimatedCandidatePurgeRows/Bytes` are informational evidence;
`EstimatedPurgeRows/Bytes` remain zero when protected IDs are missing from
MCV statistics or partition estimates are partial, stale, or inconsistent.
Never substitute candidate estimates for the exact execution preflight or the
`500 GiB` free-space gate.

The accepted live evidence pattern records the harness source, compiled output,
merged Compose command review, read-only verification, bounded resource
monitor, exact JSON result, and checksums on the FST drive. The current
publication-`1293` observation took `94 ms`, produced nine blocked plans, and
kept API/web/PostgreSQL healthy.

### Accepted pro-bass snapshot archive/rewrite

`tools/postgres-pro-bass-snapshot-rewrite.sh` is the only supported entry point
for the completed guarded rewrite of
`public.leaderboard_entries_snapshot_pro_bass`. It has no table, partition,
instrument, or SQL input.

The typed stages are `check`, `plan`, `archive`, `drill`, `build`, `swap`,
`validate`, `drop`, and `rollback`. Each successful stage writes one immutable
checksummed report; failures write separately typed failure reports.

The package:

- protects active, projection, rollback, and current/previous/working
  publication physical source IDs;
- enumerates production snapshot IDs through leading-index `MIN` probes,
  joins ownership metadata only, and fingerprints protected rows only;
- requires the checksummed verified live archive/restore/cleanup input for
  exact total rows and archive adoption;
- rejects incomplete/missing ownership unless exact verified-archive evidence
  covers the unchanged legacy ID/content and no named publication source map
  references it;
- streams a PostgreSQL custom archive directly to the explicitly authorized
  8 TB scratch device;
- restores and verifies the archive in network-none PostgreSQL 17;
- builds in only the exact run-owned temporary tablespace when its mount/gates
  pass, then requires `repatriate` back to `pg_default`;
- retains the detached original through exact validation;
- supports rename-back rollback before a separately guarded final drop;
- retains original and scratch rollback relations through repatriation parity,
  then removes both and the tablespace only in final drop;
- atomically publishes critical evidence, recovers catalog state from
  zero-length/truncated copy/swap evidence, and durably blocks a breached run;
- binds verified archive adoption to exact per-snapshot counts/content hashes
  and the full canonical restored catalog;
- never uses `CASCADE`.

Run structural tests with:

```bash
bash -n \
  tools/postgres-pro-bass-snapshot-rewrite.sh \
  tools/postgres-pro-bass-snapshot-rewrite-drill.sh

PYTHONDONTWRITEBYTECODE=1 \
  python3 tools/postgres-pro-bass-snapshot-rewrite.test.py
```

Run the scaled isolated lifecycle with:

```bash
mkdir -p artifacts/pro-bass-pilot-drills

tools/postgres-pro-bass-snapshot-rewrite-drill.sh \
  --work-root "$PWD/artifacts/pro-bass-pilot-drills/<utc-run>" \
  --image postgres:17 \
  --purge-rows 300000 \
  --retained-rows 30000
```

The final accepted isolated drill retained two restore-drilled archives and
proved rename-back, pre-drop repatriation, torn-evidence recovery, and
final-drop paths. It measured a 75,415,552-byte source, 19,636,224-byte
replacement, 20,423,824 scratch-build WAL bytes, 8,429,568 temp bytes,
19,689,472 peak scratch growth, and 95,043,584 filesystem bytes returned after
dropping the original plus scratch rollback. Repatriation returned the accepted
relation/catalog to `pg_default` while both rollback relations remained until
final drop.
The rollback lane also proves archive/build/swap/rollback resume after a
simulated missing terminal report while preserving the first checksummed
reports.
The generated production-planning profile is synthetic evidence; live
execution still needs an exact source plan.

The live read-only archive is `11,942,257,904` bytes with SHA-256
`3decc75ffe33e24dad72e379fb874c7b0c7b4a421121de6a227acd0fe344760f`.
Its corrected isolated restore validated `308,536,699` rows across 125
snapshot IDs and the exact child catalog, then removed the
`130,771,858,177`-byte restore PGDATA. The first restore attempt is retained as
a typed rejection: applying parent indexes before the archived child indexes
caused PostgreSQL to reject a duplicate child primary key. The successful
procedure restored child data/indexes while detached and attached afterward.

The exact archive row ratio plus measured profile projects a
`2,685,343,018`-byte replacement and `69,713,820,289` required free bytes,
still `1,168,706,177` short at current `68,545,114,112` free bytes. The
conservative sensitivity remains
`72.19-73.06 GB`. With the replacement/temp/failure reserve on the guarded
temporary tablespace, the candidate requires `63,889,690,620` free on 4 TB and
`17,260,886,072` on scratch, giving `4,655,423,492` current 4 TB margin.
The live run completed build, both swaps, validation, repatriation and final
drop without a threshold breach. It returned `152,985,165,824` filesystem
bytes and left a `2,811,404,288`-byte `pg_default` partition. The temporary
tablespace and Compose mount are absent; the archive remains retained.

Follow the
[living runbook](../database/ProBassSnapshotRewritePilot.md). The archive
retention/deletion decision remains deliberately outside this candidate.

### Worker Compose guard

`tools/fst-worker-compose-guard.sh` retains the purposes of `--check`,
`--check-runonce`, `--recreate`, and `--recreate-runonce`, but deliberately
tightens every action to require canonical effective-service membership and
reject effective static PIA endpoint-IP pins. Every action also requires the
guard-only `worker` Compose profile. Continuous actions require
`restart: on-failure:5`; run-once actions require `restart: no`. Its
`--recover-start` action is the continuous production startup handoff after the
production-owned boot orchestrator has started core services and effective
proxies.

Every worker-start/recreate action shares one nonblocking host lock; checks do
not take it. By default the lock is derived as
`<resolved-compose-dir>/.fst-worker-compose-guard.lock`; an explicit absolute
override remains available. All invokers must share the resolved directory or
override and Unix owner.

Pass `--expected-worker-image` for every candidate check or recreate. The guard
compares it with the final merged `fstworker.image` even when no data profile is
selected, so a later Compose overlay cannot silently replace the requested
candidate. Named data profiles continue to require the option.

Recovery is effective-set-only, capped by stage windows and a 1,800-second
default total deadline, and fail-closed. It does not accept `--config-only`,
run-once/data profiles, or any `candidate-*` throughput profile. It does not
recreate core services or non-effective proxies. The guard passes
`--profile worker` for every worker-dependent config resolution and
worker-targeted `up`; proxy-only recreates remain effective-name-only. Output
reports stages and counts without resolved endpoints, IPs, credentials, or
environment values.

Full-scrape run-once data profiles that enforce scope manifests and published
scope sources also require
`Features__UseLeaderboardScopeFingerprints=true`. Positive scopes receive
their current-scrape coverage identity from those fingerprints; the guard
rejects a profile that disables them instead of allowing a candidate to fail
after network collection. The dual-lane wrapper sets this invariant
explicitly, while snapshot reuse remains a separate opt-in.

The `leaderboard-rivals-batch` data profile is a one-scrape canary contract. It
requires the exact account batch size of four, publication-safe scrape-pass
path staging, the accepted snapshot-reuse write path, complete notification
lanes, and all publication-critical manifests. Pair it with
`candidate-800-32-4` to retain the proven production network rates while making
their enforcement exact. It also retains the accepted Song Rivals account
concurrency of two and learned CDN concurrency ceiling of `360`, so neither is
an unrelated candidate. The dual-lane wrapper assigns the supplied
expected image to the final run-once overlay before the guard resolves Compose;
the option is therefore both the selected image and the fail-closed assertion.

If post-start readiness fails, cleanup stops the worker only while
`currentUpdate` remains idle and public reads remain unfrozen. Otherwise it
leaves the worker running and directs the operator to
`tools/fst-worker-no-progress-watchdog.mjs` and the canonical
[live-safety procedure](../operations/live-safety.md).

### Worker safety watchdog sources and opt-in gates

`tools/fst-worker-no-progress-watchdog.mjs` detects the normalized
`scrape_phase_attempts` relation once at startup. When a running attempt
exists, its `last_progress_at` and start time take precedence over
`current_operation_json.UpdatedAtUtc`; `heartbeat_at` is deliberately excluded
from timeout progress. Older databases or windows without an active normalized
attempt retain the existing operation/outcome/registered-refresh fallback.

Guarded timeout recovery also marks running normalized attempts `interrupted`
and records their prior values in rollback SQL. Pointer, mapping, worker-query,
lock, and maintenance guards are unchanged.

The default behavior remains progress-only. Canary operators can opt into
`--recover-worker-exit`, which treats an exited or OOM-killed worker as a
recovery trigger only while the latest scrape is running under the
post-process freeze. OOM and nonzero exits trigger immediately. Exit code zero
uses `--worker-exit-grace-seconds S`, default `120`, before recovery so normal
run-once shutdown can become terminal. `--max-worker-memory-percent P` adds an
emergency Docker memory threshold; `0` disables it. A threshold breach
intentionally takes precedence over the ordinary active-query defer, stops the
worker through the existing Compose path, and then drains exact worker
backends for up to `--worker-query-drain-seconds` (default `60`). Any remainder
is terminated only by the worker-owned `fstworker-scraper` and
`fst-path-generation-admission` application names and, when available, the
captured worker IP as an alternate identity before the unchanged zero-query,
zero-mapping, publication-pointer, lock, and maintenance gates run. These
resource modes require the resolved worker restart policy `no`; they are not
valid for the continuous `on-failure` lane. Recovery failures still produce
query-drain/error evidence and a report while publication remains fail-closed.
Observations include container status, restart policy, OOM state, exit code,
memory percentage, and a sanitized memory-sample error when Docker statistics
are temporarily unavailable.

Accepted scrape `1296` used normalized attempts in all 392 watchdog
observations across network, post-process, rankings, cleanup, and publication.
In 358 samples `heartbeat_at` advanced beyond `last_progress_at` without
masking progress. The terminal decision was `scrape_completed`; old-schema
fallback remains covered for rolling deployments.

The action's host-side controls are documented in
[Configuration](configuration.md). Validate changes with:

```bash
bash -n tools/fst-worker-compose-guard.sh
bash -n tools/fst-worker-dual-lane-runonce.sh
node --test tools/fst-worker-compose-guard.test.mjs
node --test tools/fst-worker-no-progress-watchdog.test.mjs
```

## Pak extractor

[`tools/FortnitePakExtractor/README.md`](../../tools/FortnitePakExtractor/README.md)
documents a standalone, Windows-oriented utility that is not part of the main
solution. Treat its paths and external-data prerequisites as tool-specific.

## Agent skills

`.github/skills/` contains repository-specific execution and database advisor
contracts. Skills are operational instructions, not human architecture
documents. When a skill cites a schema, command, path, or deployment topology,
update it with the same source change and keep it linked to the canonical docs.
