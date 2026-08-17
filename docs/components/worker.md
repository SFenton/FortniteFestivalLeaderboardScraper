---
status: canonical
owner: worker
last_verified: 2026-08-17
last_verified_commit: dffca41c
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
  - FSTService.Tests/Unit/GlobalLeaderboardScraperTests.cs
  - FSTService.Tests/Unit/RankingsCalculatorTests.cs
  - FSTService/Scraping/PhaseProgressCatalog.cs
  - FSTService/Scraping/DurablePhaseProgressSink.cs
  - FSTService/Scraping/MaxScoreMaintenanceDerivedStateService.cs
  - FSTService/Scraping/LeaderboardRivalsCalculator.cs
  - FSTService/Persistence/MaxScoreMaintenanceModels.cs
  - FSTService/Scraping/PlayerStatsTierRebuilder.cs
  - FSTService/Persistence/MaxScoreMaintenanceArtifactValidator.cs
  - FSTService/Persistence/MaxScoreMaintenanceCacheEntryEvidenceStore.cs
  - FSTService/Persistence/GlobalLeaderboardPersistence.cs
  - FSTService/Persistence/PublishedSoloScopeSql.cs
  - FSTService/Scraping/ScrapeTimePrecomputer.cs
  - FSTService/Api/PublicationApiResponseCachePolicy.cs
  - FSTService/Persistence/MetaDatabase.cs
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/BandCurrentProjectionBuilder.cs
  - FSTService/Scraping/Replay/
  - FSTService/Program.cs
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
support. Gate holders and cancellable waiters use isolated unpooled,
non-multiplexed PostgreSQL sessions, leaving the normal service pool available
for the guarded work itself. The gate holds no transaction, so long
Epic/history waits cannot be expired by
`idle_in_transaction_session_timeout`. Before each guarded mutation the
worker verifies the owning backend/session token; database triggers fence
registration, leaderboard entry/population, and score-history writes if a lost
shared backend allows exclusive maintenance to claim its durable owner token.
This covers registration-only hosting, including the interval before a
publication monitor observes a same-publication release. Exclusive maintenance
admission waits for active holders, blocks later holders, and remains
fail-closed across cancellation/resume. Ordinary scrape freezes continue to
use the existing background-work boundary rather than this max-score-only
rejection.

Optimal-path generation is a separate coordinated workload. Automatic path
generation remains disabled by default and selects only pending songs; the
protected admin route accepts one song at a time. CHOpt outputs are validated
and promoted as immutable generations, and complete catalogue migrations must
remain sequential and resumable. See [Path generation](path-generation.md).

Max-score correction is a separate CLI-only one-shot mode. It registers no
hosted scraper/background services and requires the real `fstworker` offline.
Discovery and promotion stages share the path-generation admission lock
without promoting; only the second produces a plan/apply-eligible manifest.
Plan/apply reject plastic-drums v3, revalidate current rollback and staged
artifact trees/hashes, then take the exclusive mutation gate before the
path-generation and global publication locks. Apply establishes or revalidates
the freeze before taking
solo overlay/entry, score-history, band-member-stat, and
leaderboard-population share locks in fixed order, then rechecks that the
worker remains offline around each mutable phase. Maintenance ranking mode
suppresses `WorkerStatusPublisher`, bypasses the mutable current projection,
resolves the exact published snapshot/empty source plus supplemental overlay,
and holds that strict no-active/no-legacy context through cache staging and
final validation. It snapshots publication-bound population once and passes
it to rankings, player stats, cache construction, and validation rather than
reading mutable `leaderboard_population`. It rebuilds changed solo instruments
by replacing the exact frozen, zero-inclusive `song_stats` scope and affected
ranking partitions, then rebuilds aggregate dependencies. Affected accounts'
player-stat tier rows are replaced as a complete set while unrelated accounts
remain untouched; maintenance cache serialization filters tiers to `Overall`
plus frozen publication instruments. It recalculates
target-song band validity, refreshes affected band current-projection scopes,
rebuilds dependent band rankings, and explicitly skips
solo/composite/band rank-history snapshots.
Blank affected account IDs are excluded consistently after maintenance proves
that they have no score-history, registration, or account-cache identity; this
does not version or alter plan-digest v6 inputs. Leaderboard rivals rebuild
only manifest-changed instruments. Each changed instrument uses one
authoritative profile batch for registered users plus deduplicated ranking
neighbors, retains all five methods/directions/top-200 semantics, and persists
each user/instrument atomically without touching unrelated rival state.
Before any post-freeze mutation and again on resume, maintenance reloads each
mapped raw highest score, highest score eligible at or below
`floor(newMaximum × 21 / 20)`, and above-cutoff row count, then reconstructs
the approved plan-digest v6 evidence. The CHOpt maximum remains the ratio
denominator; the exact `21 / 20` threshold is the separate 105% ranking
eligibility cutoff. Raw rows above it are reported as ranking-invalid outliers
and do not block compatibility. Stage requests and manifests reject every
target maximum above `2,045,222,521`, as do actual current/staged path and
report validation. Unrelated frozen-catalog maxima use a saturated
`int.MaxValue` threshold, which is equivalent for PostgreSQL `INTEGER` scores
and keeps evidence parameters representable without admitting the value as a
maintenance target. A missing source, invalid maximum/cutoff, eligible score
above its cutoff, or any raw/eligible/count drift keeps the workflow frozen
and resumable.
See the
[max-score correction runbook](../database/MaxScoreCorrectionMaintenanceRunbook.md).
The rollback action is the same strict one-shot boundary: it registers no
hosted initializer, scraper, catalog refresh, registration, publication
monitor, or Docker worker and performs no provider traffic. Dry-run performs exact read-only
admission. Execution restores paths in one atomic checkpoint, then resumes
complete rollback-derived/notification/cache phases until terminal
`rolled_back`; apply/resume cannot continue after rollback starts. Every
failure leaves the worker offline and public reads frozen.
Every max-score database mutation and checkpoint commits through a bounded
source-locked transaction on the live unpooled advisory-lock session; ordinary
pooled connections are read-only for that workflow. Rollback keeps the
registration/path locks and durable gate but yields the global publication
lock between transactions, reacquiring it transactionally only at commit so
cached API reads do not queue behind long reconciliation. The final cache swap,
completed checkpoint, and unfreeze use one such transaction while the durable
gate remains set. That transaction keeps a `5s` lock timeout, uses the
configured maintenance statement timeout only for final immutable cache
validation, and restores the `120s` bounded mutation timeout before the cache
swap, checkpoint, verification, and unfreeze. Any failure rolls the
transaction back. Disposal releases all advisory locks before clearing the
gate, so backend loss during handoff leaves mutations fail-closed and requires
a new validated lease to finish release.

The worker's scrape, pruning, ranking, and statistics paths consume distinct
CHOpt maxima for all eight generated instruments, including separate Pro Drums
and Pro Drums + Cymbals thresholds.

Solo scrape admission treats an omitted per-instrument provider difficulty as
unknown rather than unsupported, because Epic can expose a real leaderboard
without the matching difficulty property. A present provider property must
still contain a charted difficulty, so explicit sentinels such as Pro Vocals
`bd=99` remain excluded. Current promoted `path_expected_instruments`
independently supplement provider support only when the immutable generation
is complete, non-pending, and bound to the same song/catalog timestamp.

Normal ranking denominators use a deduplicated union over the exact current
catalog: provider-admitted songs, matching promoted path instruments, and
songs with positive population for that exact song/instrument. Population
outside the current catalog cannot enlarge the denominator. Positive
current-catalog population can retain a denominator scope even when a present
provider sentinel currently blocks refresh, because persisted ranking inputs
can still own that scope.

Ranking materialization includes only current-catalog song IDs; retained
leaderboard and `song_stats` rows for removed songs remain historical source
data but do not contribute to current songs played, Full Combos, scores, or
rates. After a partition is successfully rebuilt, the already-required summary
load rejects mixed denominators, counts above the denominator, and non-finite
or out-of-range coverage/FC rates before aggregate rankings or publication can
continue. Instruments deliberately skipped because their selected denominator
is zero retain the prior warn-and-skip behavior and are not treated as a new
ranking generation.
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
`--band-post-scrape` is the supported direct legacy `BandScrapePhase` mode.
Normal full and `--band-scrape` passes already fetch band data through
`BandScrape` and therefore do not launch the legacy fetcher.

## Post-scrape timing

Terminal phase names and publication criticality remain recorded through
`scrape_phase_outcomes`. Only explicitly best-effort phases may record
`status=skipped`, with the reason in durable progress. Pressure-gated history
cleanup, notification gating, and service-level retention use that contract.
Publication-critical phases may never be skipped: snapshot-only legacy rank
recompute completes its critical contract without running the legacy update,
and any persisted critical `skipped` row is invalid and blocks resume or
publication regardless of the critical-failure rollout switch.

PostgreSQL has no per-wrapper cache warm or manual checkpoint implementation.
The worker no longer schedules those retired calls at startup, after network
writes, or during finalization.

Matched production scrapes `1299` (control) and `1300` (candidate) accepted
this cleanup. Both covered 702 songs, 8,424 complete scope manifests, and 6,318
complete published solo-source mappings; both published, unfroze, completed
notification recovery, and had zero writer, critical-phase, or best-effort
failures. Candidate `1300` created no `Checkpoint` or
`DeferredRegistrationSync` attempt/outcome and no critical skipped row. Its
three pressure-gated retention skips were best-effort, carried durable reasons,
and did not block publication. End-to-end publication time changed by
`-0.117%`; this is correctness acceptance only, not a speed claim. The matched
800/32/4 network configuration remains a control because its network wall clock
varied by `+30.16%`. Evidence is under
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/pr38-matched-candidate-20260815T162415Z`.

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

Accepted scrape `1302` confirmed that current projection remains the dominant
BandMaintenance cost: `6,495,632 ms` of `8,217,883 ms` (`79.043%`), with
`53,790` scopes considered, `9,048` refreshed, `15,998,027` rows inserted, and
`15,983,000` deleted.

The current projection row query derives seven ordered member arrays through
seven correlated `band_member_stats` aggregate subqueries with identical scope
predicates. PR #47 merges a default-off implementation that replaces only
those aggregates with one `LEFT JOIN LATERAL` pass that produces the same
seven arrays in `member_index` order. It does not change scope discovery,
per-scope transactions, candidate deletion, generation publication, cleanup,
ordering, or failure handling.

`Scraper:BandCurrentProjectionUseBatchedMemberStatsAggregation` controls the
candidate and defaults to `false`. Normal and chunk-fallback refreshes receive
the same option, and durable phase configuration identity includes it.
Structured refresh logs record the selected query shape and successful scope
transaction count. Command, round-trip, and logical aggregate-pass fields are
explicitly labeled `derived`; they are formulas from the current query and
transaction structure, not runtime instrumentation. Setting the switch back
to `false` is the code-path rollback.

Bounded isolated PostgreSQL tests preserve exact projection, scope-state, and
global-state hashes for zero, all-unchanged, one-changed, mixed, missing-member,
nullable-stat, and 64-scope/2,048-row fixtures, plus failure, retry, and
cancellation cases. Schema tests lock the primary-key uniqueness that makes
`member_index` ties impossible for a projection source key. The large fixture
keeps 64 successful transactions and derives the same 192 commands and 320
round trips while reducing derived logical member-stat aggregate passes from
`14,336` to `2,048` (`-85.714%`). Independent PostgreSQL `EXPLAIN` evidence
still measures seven baseline scans versus one candidate scan. Local elapsed
reductions are diagnostic only; no production improvement is accepted. A
matched full-scrape A/B remains blocked until the FST capacity guard again has
at least one `60.4 GB` scrape window, preferably two (`120.8 GB`).

## Durable phase progress

Plan `fst.scrape-plan.v2` assigns 28 test-locked IDs to the existing
leaderboard scrape, named post-scrape phases, and publication commit. The
catalog does not add a DAG, reorder work, or replace legacy labels; descriptors
carry both the stable ID and the current human-readable phase name.
`post.checkpoint` and `post.deferred_registration_sync` remain reserved for
historical manifests and persisted progress rows but have no current execution
policy. Service-info descriptors expose `reserved: true`; the active catalog
and durable progress sink exclude/reject those descriptors so retired phases
cannot contribute to active phase counts or future overall-progress models.
Registration backlog/history work is owned by the dedicated recurring and
run-once drain paths; recurring registered-user refresh remains in
`RefreshRegisteredUsers`.

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
also distinguishes the default-off batched member-stat candidate. It never
stores credentials or resolved provider endpoints.

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

## Tier-0 replay evidence contract

The accepted PR-4 library adds versioned Tier-0 package, canonical JSON,
hashing, sealing, resume-journal, path-safety, configuration-fingerprint, and
verification contracts under `FSTService.Scraping.Replay`.

The phase-plan projection calls `PhaseProgressCatalog` directly; it does not
duplicate or reorder the 28 stable descriptors. The manifest can describe
scope/fingerprint and phase outcome/timing summaries, but the current worker
does not create packages, capture a scrape, export PostgreSQL, import an
isolated database, invoke replay, or alter publication.

The accepted PR-5 repository capability dispatches replay before `.env`
loading and before `WebApplication`, API, hosted-worker, provider,
notification, cache, Docker, or publication registration. Replay therefore is
not a new worker mode and cannot schedule or host background mutation.
Protocol v1 invokes only the existing BandMaintenance current-projection
refresh builder against a marker-owned isolated PostgreSQL database; every
other post-scrape phase remains explicitly unsupported.

Replay defaults to the accepted deterministic overrides, while explicit
option-parity profiles can run unchanged-scope discovery and the default-off
batched member-stat query shape with production skip/commit/DOP/cleanup choices
inside Tier-1 bounds. Output/comparison manifests bind the profile and still
declare `productionComparableTiming=false`; isolated timing cannot support a
production phase-wall claim.

Future worker capture must remain a separately gated change with explicit FST
drive capacity/retention ownership and must preserve PostgreSQL authority,
historical correctness, Epic provenance, freeze/publication semantics, and
rollback. See
[Replay evidence artifacts](../architecture/replay-artifacts.md).

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
freeze: covered publication-bound cache misses, including `/api/songs`, return
`503`; immutable path files keep their established endpoint ownership.
After derived validation a complete cache swap, workflow completion, and
unfreeze commit together. Maintenance precompute uses only frozen-catalog
publication scopes and their captured populations for song keys and completion
denominators.

Precompute now stages the canonical `/api/songs` bytes from the same serializer
as the endpoint and one top-10 per-song/per-instrument leaderboard payload from
data already loaded for leaderboard-all. Existing overview, composite, generic
band, registered-player, leaderboard-all, and song-band rows are reused by
request aliases rather than duplicated. The extra eager surface is bounded by
the publication catalog/scope set and adds no ranking/query pass.

The `caches_staged` checkpoint and every
later pre-complete state make both staging tables immutable to ordinary cache
builders/writers; exact maintenance-owner access remains available for resume
and final publication. Resume and the final
source-locked transaction compare every staged key/ETag/JSON hash with durable
entry evidence before swap. API processes invalidate
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
