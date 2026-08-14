---
status: canonical
owner: worker
last_verified: 2026-08-14
last_verified_commit: eb593898
sources:
  - FSTService/ScraperWorker.cs
  - FSTService/ScrapePhase.cs
  - FSTService/Scraping/ScrapeOrchestrator.cs
  - FSTService/Scraping/PostScrapeOrchestrator.cs
  - FSTService/Scraping/GlobalLeaderboardScraper.cs
  - FSTService/Scraping/RegistrationBackfillWorker.cs
  - FSTService/Scraping/BackfillOrchestrator.cs
  - FSTService/Scraping/RegistrationMutationCoordinator.cs
  - FSTService/Scraping/RankingsCalculator.cs
  - FSTService/Scraping/PhaseProgressCatalog.cs
  - FSTService/Scraping/DurablePhaseProgressSink.cs
  - FSTService/Scraping/MaxScoreMaintenanceDerivedStateService.cs
  - FSTService/HostedWorkerMode.cs
  - FSTService/Persistence/Maintenance/DatabaseRetentionMaintenanceService.cs
  - deploy/config/fstworker-role.env
  - tools/fst-worker-compose-guard.sh
  - tools/fst-worker-no-progress-watchdog.mjs
update_triggers:
  - Worker registration, phase selection, scrape sequencing, background coordination, recovery, or publication changes.
---

# Worker

The full worker is the mutation owner for scheduled leaderboard collection and
derived publication. Production runs it from the FSTService image as a separate
`fstworker` container.

## Hosted services

Full-worker mode registers:

- startup initialization and readiness;
- worker heartbeat/status;
- `ScraperWorker`;
- registration backfill when allowed by the run mode;
- band-rank-history background work.

API/frontend modes register only the background services appropriate to those
roles. Registration-sync mode omits scheduled scrape and band-history work.

## Production startup

The production startup contract has the boot orchestrator start the database,
API/web roles, and effective proxies without starting `fstworker`. Docker can
otherwise leave a worker Created forever when a `service_healthy` dependency
misses boot; its restart policy does not apply to a container that never
started.

Repository Compose templates put `fstworker` in the `worker` profile, so bare
Compose startup cannot include it. The continuous policy is
`restart: on-failure:5`: Docker may retry a nonzero process exit up to five
times while the daemon remains running, but daemon/host restart does not start
the worker. The guarded host startup path owns that transition. Run-once
merges retain `restart: no`.

The host then runs `tools/fst-worker-compose-guard.sh --recover-start`. That
action validates the continuous baseline and exact effective arrays, refuses
active/frozen work, requires the worker profile and restart policy, performs
bounded effective-proxy recovery and qualification, and recreates only
`fstworker` with `--no-deps`. The guard explicitly supplies `--profile worker`
both when resolving merged config and when targeting the worker start. Success
additionally requires a healthy worker container and a new fresh heartbeat
through `/api/service-info`.

The in-worker Gluetun recycler remains responsible for tunnel failures after
startup; it is not the boot healer. Recovery failure keeps or returns the
worker to a stopped state only if work remains idle and public reads remain
unfrozen. Once work or a freeze begins, the guard leaves the worker running and
directs the operator to the no-progress watchdog instead of risking a stranded
candidate. PostgreSQL, API, and web roles are never restarted. Candidate
profiles are run-once-only and are not continuous startup authorization.

## Continuous loop

After startup the worker:

1. resumes deferred publication;
2. ensures notification recovery is complete;
3. authenticates with Epic;
4. runs a scrape pass;
5. retries deferred publication/recovery;
6. exits in run-once mode or sleeps for `ScrapeInterval`.

Background registration and band work is paused and drained at scrape
boundaries so it cannot race publication-critical work.

Registration-sync work also observes the durable max-score maintenance freeze.
The worker reports a pause before invoking a writer, and each backfill/history
orchestrator entry acquires the shared session advisory mutation gate before
its first account/seasonal lookup or persistence mutation. Registered-user
refresh, registered-band discovery/processing, and stale registration pruning
use the same gate. Immediately after acquisition it invalidates path-maxima
state and synchronously refreshes the singleton scraper's song/instrument
support. The gate holds no transaction, so long Epic/history waits cannot be
expired by `idle_in_transaction_session_timeout`. This covers
registration-only hosting, including the interval before a publication monitor
observes a same-publication release. Exclusive maintenance admission waits for
active holders, blocks later holders, and remains fail-closed across
cancellation/resume. Ordinary scrape freezes continue to use the existing
background-work boundary rather than this max-score-only rejection.

Optimal-path generation is a separate coordinated workload. Automatic path
generation remains disabled by default and selects only pending songs; the
protected admin route accepts one song at a time. CHOpt outputs are validated
and promoted as immutable generations, and complete catalogue migrations must
remain sequential and resumable. See [Path generation](path-generation.md).

Max-score correction is a separate CLI-only one-shot mode. It registers no
hosted scraper/background services and requires the real `fstworker` offline.
Stage shares the path-generation admission lock without promoting. Plan/apply
take the exclusive mutation gate before the path-generation and global
publication locks. Apply establishes or revalidates the freeze before taking
solo source and band-member-stat share locks, then rechecks that the worker
remains offline around each mutable phase. Maintenance ranking mode suppresses
`WorkerStatusPublisher`, rebuilds changed solo instruments plus aggregate
dependencies, recalculates target-song band validity, refreshes affected band
current-projection scopes, rebuilds dependent band rankings, and explicitly
skips solo/composite/band rank-history snapshots. See the
[max-score correction runbook](../database/MaxScoreCorrectionMaintenanceRunbook.md).

The worker's scrape, pruning, ranking, and statistics paths consume distinct
CHOpt maxima for all eight generated instruments, including separate Pro Drums
and Pro Drums + Cymbals thresholds.

Solo scrape admission and ranking denominators use the union of charted
provider properties and current promoted `path_expected_instruments`.
Path-derived support is admitted only when the immutable generation is
complete, non-pending, and bound to the same song/catalog timestamp. This
preserves MIDI-inferred charts when provider metadata omitted them without
turning path state into a second song catalog.
Promotion refreshes the singleton scraper's cached path support before derived
work. Same-publication freeze release invalidates that cache in monitoring
roles. Newly usable path-backed pairs also clear only prior negative backfill
checks and matching successful-empty history checkpoints, return affected
history status to `pending`, and requeue only affected accounts. Unrelated
pairs remain resumable, so a previous unsupported/null all-time or seasonal
lookup cannot suppress Lead or Pro Lead indefinitely.

## Two phase views

The pipeline has two useful abstractions:

- **Orchestration lifecycle:** authentication, exact catalog selection,
  leaderboard network/writer work, enrichment, registered-user refresh,
  rankings/rivals/statistics/precomputation, publication, and cleanup.
- **Selectable launch phases:** eight ordered solo flags and three band flags in
  `ScrapePhase`.

`ScrapePhaseResolver` expands `--solo-scrape`, `--solo-leaderboards`, and
`--band-scrape`, and fills intermediate solo phases. Selective flags affect the
first launch pass only; later scheduled cycles use the full pipeline.

## Post-scrape timing

Terminal phase names and publication criticality remain recorded through
`scrape_phase_outcomes`.

Band maintenance additionally records three stable timing subphases under the
`BandMaintenance` phase:

- `prune`;
- `search_projection_refresh`;
- `current_projection_refresh`.

The timing rows reuse counts already returned by those operations: deleted
band/member rows and affected scopes for prune, inserted/deleted projection
rows and impacted teams for search, and inserted/deleted rows and refreshed
scopes for the current projection. For `current_projection_refresh`,
`rows_read` is the number of impacted scopes presented to the builder, while
`scope_count` is the number selected for refresh after unchanged-scope
filtering. This distinguishes no impacted scopes (`0`/`0`) from impacted but
unchanged scopes (`N`/`0`). They add no discovery query.

Timing persistence is best effort. A timing-write failure cannot replace a
phase exception or cancellation and cannot change candidate publication.
`success=false` means the subphase did not complete successfully, including
cancellation. Row/scope metrics are null on exception or cancellation because
partial work may have occurred; a successful no-work subphase records zero.
BandExtraction membership/configuration work is not part of these
BandMaintenance timings.

Corrected live candidate scrape `1293` accepted this contract. It emitted
exactly the three successful rows above, with no extras, and published normally.
BandMaintenance took `7,939,927 ms`; the subphases accounted for
`7,939,670 ms`, leaving `257 ms` (`0.00324%`) for all orchestration including
timing persistence. `current_projection_refresh` dominated at `6,049,933 ms`
(`76.20%`), followed by prune at `1,144,264 ms` and search refresh at
`745,473 ms`. The current refresh considered `53,543` scopes, selected `8,020`,
wrote `14,179,946` rows, and deleted `14,189,655`.

## Durable phase progress

Plan `fst.scrape-plan.v2` assigns 28 test-locked IDs to the existing
leaderboard scrape, named post-scrape phases, and publication commit. The
catalog does not add a DAG, reorder work, or replace legacy labels; descriptors
carry both the stable ID and the current human-readable phase name.

The worker writes additive `scrape_phase_attempts` rows:

- start, subphase transition, retry/new attempt, failure, cancellation, and
  completion persist immediately;
- active counters persist only after meaningful advancement and at most once
  per five seconds;
- persisted `last_progress_at` is monotonic even if the worker clock moves
  backward;
- the 15-second liveness heartbeat updates `heartbeat_at` without advancing
  `last_progress_at`;
- exact `phase_percent` exists only when the denominator is final; totals that
  grow through `AddPhaseItems` remain indeterminate until explicitly finalized;
- a new worker instance marks orphaned running attempts `interrupted` before
  creating its attempt;
- persistence failures log a warning and do not replace phase exceptions,
  cancellation, or publication decisions.

One current-operation bridge preserves all version-1 JSON fields and adds
contract version 2 identifiers, units, exact phase percent, conservative
overall/ETA metadata, heartbeat, and last-progress timestamps. Overall progress
starts as `indeterminate`. ETA is omitted unless at least five successful
same-plan/same-config durations have the same final units kind and a workload
total within 10%, then pass the `0.35` coefficient-of-variation gate. Emitted
ranges are monotonic and carry model version, confidence, and sample count.
The configuration fingerprint covers an allowlist of phase, network,
persistence, publication, ranking, notification, and retention controls; it
never stores credentials or resolved provider endpoints.

Matched control scrape `1295` and accepted candidate `1296` validated the
contract under identical `800/32/4` network enforcement. Candidate wall time
was `+16.383 seconds` (`+0.0696%`), and summed terminal phase outcomes were
`+0.736%`. The ledger used 24 inserts and 2,068 updates over 6.54 hours
(about one update per 11.39 seconds including heartbeats), and occupied
212,992 bytes. All attempts reached a terminal state with no timestamp or
percentage regression, no exact percentage for an unknown denominator, and no
false ETA/overall precision.

Terminal status is authoritative. Some bounded or parent-tracker phases
complete with a truthful observed fraction below 100%; browser code must not
rewrite those counters to 100 or interpret them as remaining publication work.
Ready-publication deferral also creates distinct failed attempts followed by a
successful retry, preserving the actual retry history.

## Publication safety

The worker freezes public reads before candidate work, requires an exact
catalog token, skips full-data derived phases after an incomplete scrape,
validates publication-critical outcomes, prepares the generation, drains
readers, commits atomically, and notifies clients only afterward.

Role defaults intentionally differ:

- worker writes candidate/published sources and enforces completeness/writer/
  critical-phase gates;
- service resolves public reads through published sources;
- publication read-context rollout remains disabled until every bound surface
  is generation-addressable.

A digest-owned max-score maintenance freeze is stricter than a normal scrape
freeze: affected publication-bound cache misses, including `/api/songs` and
both path routes, return `503`. After derived validation a complete cache swap,
workflow completion, and unfreeze commit together. API processes invalidate
response, path-maxima, and song caches and force a same-publication client
refresh.

## Service-level retention planning

The service-level database maintenance worker may produce snapshot-retention
plans while rewrite execution remains disabled. Planning uses bounded
PostgreSQL catalog/statistics queries and does not scan snapshot partitions.

Plans retain active, projection-source, rollback, and policy-blocked IDs.
Missing protected-ID estimates, partial MCV coverage, unknown or negative
`n_distinct` semantics, stale row estimates, or row/byte reconciliation gaps
make the plan non-executable. In that state purge rows/bytes are withheld and
the full partition is treated as retained workspace. The later execution path
still requires its exact row-count preflight, free-space gate, advisory lock,
and explicit rewrite enablement.

On publication `1293`, the report-only path evaluated all nine snapshot
partitions in `94 ms`, emitted zero executable plans, held publication
`1293` unfrozen, and left the worker offline. Every partition was blocked by
missing protected-ID MCV estimates and incomplete/stale statistics; no rewrite
or metadata cleanup ran.

See [Scrape and publication flow](../architecture/data-publication-flow.md),
[CLI reference](../reference/cli.md), and
[VPN proxy pool](../operations/vpn-proxy-pool.md).
