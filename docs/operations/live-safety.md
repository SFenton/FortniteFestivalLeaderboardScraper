---
status: canonical
owner: operations
last_verified: 2026-08-17
last_verified_commit: bd11b749
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
  - docs/database/ProBassSnapshotRewritePilot.md
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
- The only current alternate-device override is the temporary pro-bass pilot
  archive/restore workspace on `/dev/nvme2n1p2`, mounted at `/`. It does not
  authorize a permanent FST store or PostgreSQL tablespace there.
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

## Startup auto-heal

The repository entry point is
`tools/fst-worker-compose-guard.sh --recover-start`; the live copy and boot-unit
wiring remain production-owned. The action assumes the production orchestrator
has already started core services and effective proxies without dependencies.
It must not be used as a general Compose reconciler.

Before proxy mutation it verifies:

- the merged continuous configuration and exact effective arrays;
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

### Temporary pro-bass archive/rewrite pilot

The repository candidate is not rewrite/drop authorization. A read-only
production archive and isolated restore drill completed; no production
replacement, rewrite, swap, rollback, or drop occurred.

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

Production build requires the candidate-specific measured gate:

```text
60,392,999,803-byte emergency floor
+ replacement heap/indexes
+ measured WAL and temp
+ one replacement-sized failure reserve
```

A direct `pg_default` build does not fit: the exact archived row ratio requires
`69,713,820,289` bytes and is short by `3,713,820,289`; the conservative
sensitivity requires `72.19-73.06 GB`. The temporary-tablespace candidate
projects `63,889,690,620` required 4 TB bytes (`2,110,309,380` margin at
`66 GB`) and `17,260,886,072` scratch bytes. That is capacity readiness only:
pre-drop repatriation requires `66,575,033,638` bytes and is still
`575,033,638` short at the same assumption. Do not run `build`, `swap`,
`repatriate`, or `drop` before candidate commit/push and clean review, exact
production mount/recreate validation, fresh preflight/parity, sufficient
repatriation headroom, and the separately approved maintenance turn. Local
live validation precedes PR merge. This does not lower the global
`500 GiB` retention policy.

Swap/rollback/drop use maintenance and publication advisory locks, a `2s`
lock timeout, a `30s` statement timeout, and no `CASCADE`. The old detached
relation remains until validation. A mismatch or timeout aborts the attempt;
do not lengthen timeouts or retry blindly. Follow the
[pro-bass pilot runbook](../database/ProBassSnapshotRewritePilot.md).

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
