---
status: canonical
owner: worker
last_verified: 2026-08-14
last_verified_commit: eb593898
sources:
  - FSTService/ScraperWorker.cs
  - FSTService/Scraping/ScrapeOrchestrator.cs
  - FSTService/Scraping/PostScrapeOrchestrator.cs
  - FSTService/Scraping/ScrapeLifecycleNotifier.cs
  - FSTService/Api/PublicationRouteSurfaceContract.cs
  - FSTService/Api/PublicReadGateService.cs
  - FSTService/Api/PublicReadGateMiddleware.cs
  - FSTService/Persistence/MaxScoreMaintenanceService.cs
  - FSTService/Persistence/MaxScoreMaintenanceNotificationService.cs
  - FSTService/Persistence/RegistrationMutationGuard.cs
  - FSTService/Scraping/MaxScoreMaintenanceDerivedStateService.cs
  - FSTService/Scraping/GlobalLeaderboardScraper.cs
  - FSTService/Scraping/RegistrationBackfillWorker.cs
  - FSTService/Scraping/BackfillOrchestrator.cs
  - FSTService/Scraping/RegistrationMutationCoordinator.cs
update_triggers:
  - Scrape allocation, phase ordering, failure isolation, publication, freeze, recovery, or client notification changes.
---

# Scrape and publication flow

The worker separates candidate work from public state. A scrape can persist
diagnostic or replay data without becoming the published generation.

## Normal pass

1. **Startup and recovery**
   - Wait for startup initialization and load the song catalog.
   - Resume a publication that was durably prepared but deferred.
   - Complete required improvement-notification recovery before another scrape.
2. **Authentication**
   - Acquire or refresh the Epic session before entering the scheduled loop.
3. **Exact catalog selection**
   - A new pass requires a successful, fully parsed provider catalog capture.
   - An inexact/safety-merged capture aborts before scrape allocation.
   - Resume mode reloads the immutable catalog bound to the resumed scrape.
4. **Freeze public reads**
   - Persist the public-read freeze and freeze response-cache expiry.
   - Existing published cache hits remain usable; candidate state is not public.
5. **Network and writer phases**
   - `ScrapeOrchestrator` performs enabled solo/band work and persists candidate
     results through disk-spool or bounded online writers.
   - Authentication failure, escaped CDN block, cancellation, or writer failure
     prevents normal derived publication work.
6. **Post-processing**
   - `PostScrapeOrchestrator` owns enrichment, registered-user refresh,
     projections, rankings, rivals, statistics, precomputation, and cleanup.
   - Per-instrument validity, leeway, and ranking calculations consume the
     eight persisted CHOpt maxima, including distinct plastic-drums modes.
   - Publication-critical outcomes can reject the candidate; best-effort
     failures remain visible without silently changing their classification.
7. **Prepare publication**
   - Validate scrape and phase outcomes.
   - Build required published scope-source mappings and notification plans.
   - Prepare the next publication generation outside the final commit.
8. **Commit publication**
   - Record commit intent, drain bounded readers, and atomically advance the
     publication pointer and generation-owned state.
   - Contention can defer a prepared publication for retry without exposing it.
9. **Release and notify**
   - Unfreeze public reads and invalidate in-process caches.
   - Run post-publication notification detection.
   - Notify connected clients only after the new generation can be served.

## Failure behavior

- An incomplete network pass skips full-data post-processing and publication.
- A rejected candidate leaves the prior publication current.
- If durable failed-candidate isolation cannot be confirmed, reads remain
  fail-closed rather than unfreezing optimistically.
- Publication lookup/read-gate failures fail closed.
- Run-once exits only after its publication decision and any permitted
  registration drain.

## Same-publication max-score maintenance

The CLI-only max-score workflow changes path metadata and maximum-dependent
derived rows while retaining the same published scrape/publication ID.

1. Stage writes complete immutable path directories only.
2. Plan binds the exact current publication/catalog/path revisions and
   fingerprints published score sources, notification state, and rank history.
3. Apply first acquires the exclusive registration mutation advisory gate and
   waits for active registration/backfill/history lifecycles to drain. It then
   takes path-generation and publication locks, creates or revalidates the
   manifest-digest-owned public-read freeze, and only afterward takes source
   table locks and persists subsequent checkpoints in fixed order.
4. One transaction promotes every listed song generation. The in-process
   scraper admission cache refreshes immediately. Prior negative backfill
   checks and matching successful history-reconstruction checkpoints are
   removed only for newly usable path-backed pairs. Affected history status is
   fenced and returned to `pending`; unrelated pairs remain complete and only
   affected accounts are requeued.
5. Maintenance ranking mode rebuilds affected instruments plus composite,
   family, and combo dependencies. Target-song band over-threshold flags are
   recalculated, prior/current affected band projection scopes are refreshed,
   and dependent band rankings are rebuilt. Solo/composite/band rank history is
   not written. Chart denominators include matching promoted path-expected
   instruments when provider metadata omitted the real MIDI chart. Affected
   player-stat tiers and registered-player leaderboard rivals follow.
6. Routine notification dry-run candidates are accepted only for player ranks
   in changed instruments, target-song band rows, and their dependent band
   ranks. Parity uses routine delivery grouping: player ranks per
   player/instrument, band songs per play, and rank metrics per band
   subject/scope, while progress metrics stay individual. Raw audit rows remain
   available; max-score-percent changes and `band_rank_state_missing` are
   alignment-only and excluded from visible parity. Missing band subjects and
   their current state are baselined transactionally before candidate
   collection. Candidates are persisted in the maintenance quarantine,
   relevant state is aligned, visible delivery remains zero, and the
   publication's completed notification marker is not reopened.
7. A complete current-publication API cache is built in staging. Final
   validation requires unchanged rank-history and source fingerprints, exact
   paths/maxima/song stats, rollback coverage, zero visible delivery, and the
   expected staged-cache count.
8. Cache swap, workflow completion, and freeze release commit atomically while
   the exclusive mutation gate is still held. No queued registration write can
   commit between that cache cutover and lease release.
   Service processes invalidate path/song/response and scraper-admission caches
   and force connected clients to refresh the unchanged publication ID.
   Registration lease acquisition independently refreshes path/instrument
   support before lookup work, closing the interval before the monitor pass.

During this maintenance freeze, publication-bound path and song routes that
have no safe published response cache return `503`; cacheable ranking/player/
band routes serve the prior published cache or return `503`. Exact solo
leaderboards follow this rule rather than falling through to current
max-score/leeway reads. Player tracking, selected-profile registration
activity, manual `POST /api/backfill/{accountId}`, and band sync registration
are paused across resume attempts. Database triggers reject
registration/backfill scope writes, while registration-only workers and the
manual all-time/history workflow, registered-user refresh, registered-band
discovery/processing, HTTP tracking/activity, and stale-registration pruning
hold the shared session advisory gate for their complete mutation lifetime.
The lease has no transaction or publication-row lock and remains valid across
long external async work under the production idle-transaction timeout. A
freeze that wins the race returns `503` before score mutation; cancellation
releases a waiting/held session safely, normal scrape freezes are unchanged,
and a post-freeze failure leaves reads and registration mutation frozen for
resume.

## Publication-aware API and browser

Publication-bound routes are classified and mapped to required data surfaces by
`PublicationRouteSurfaceContractCatalog`. Operational-live and admin/private
routes have separate behavior.

The browser bootstraps through `/api/publication`. When publication changes it
clears React Query and song caches, reconnects the application WebSocket, and
remounts the application against the new publication. Request pinning remains
effective only when configured and every required surface reports ready.

## Selective execution

The phase flags select the first launch pass. `ScrapePhaseResolver` expands
groups and fills intermediate solo phases; later scheduled cycles revert to the
full pipeline. See [the worker document](../components/worker.md) and
[CLI reference](../reference/cli.md).
