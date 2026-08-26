---
status: canonical
owner: repository
last_verified: 2026-08-17
last_verified_commit: dffca41c
sources:
  - tools/
  - FSTService/Persistence/Maintenance/DatabaseMaintenanceDryRunReporter.cs
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

### Snapshot retention evidence

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
their enforcement exact. The dual-lane wrapper also assigns the supplied
expected image to the final run-once overlay before the guard resolves Compose;
the option is therefore both the selected image and the fail-closed assertion.

If post-start readiness fails, cleanup stops the worker only while
`currentUpdate` remains idle and public reads remain unfrozen. Otherwise it
leaves the worker running and directs the operator to
`tools/fst-worker-no-progress-watchdog.mjs` and the canonical
[live-safety procedure](../operations/live-safety.md).

### No-progress watchdog progress source

`tools/fst-worker-no-progress-watchdog.mjs` detects the normalized
`scrape_phase_attempts` relation once at startup. When a running attempt
exists, its `last_progress_at` and start time take precedence over
`current_operation_json.UpdatedAtUtc`; `heartbeat_at` is deliberately excluded
from timeout progress. Older databases or windows without an active normalized
attempt retain the existing operation/outcome/registered-refresh fallback.

Guarded timeout recovery also marks running normalized attempts `interrupted`
and records their prior values in rollback SQL. Pointer, mapping, worker-query,
lock, and maintenance guards are unchanged.

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
