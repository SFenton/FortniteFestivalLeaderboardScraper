---
status: canonical
owner: worker
last_verified: 2026-08-14
last_verified_commit: 80346e04
sources:
  - FSTService/ScraperWorker.cs
  - FSTService/Scraping/ScrapeOrchestrator.cs
  - FSTService/Scraping/PostScrapeOrchestrator.cs
  - FSTService/Scraping/ScrapeLifecycleNotifier.cs
  - FSTService/Api/PublicationRouteSurfaceContract.cs
  - FSTService/Api/PublicReadGateService.cs
  - FSTService/Api/PublicReadGateMiddleware.cs
  - FSTService/Persistence/MaxScoreMaintenanceService.cs
  - FSTService/Persistence/MaxScoreMaintenanceCacheEntryEvidenceStore.cs
  - FSTService/Persistence/MaxScoreMaintenanceArtifactValidator.cs
  - FSTService/Persistence/MaxScoreMaintenanceNotificationService.cs
  - FSTService/Persistence/PublishedSoloScopeSql.cs
  - FSTService/Persistence/GlobalLeaderboardPersistence.cs
  - FSTService/Persistence/RegistrationMutationGuard.cs
  - FSTService/Persistence/MetaDatabase.cs
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Scraping/MaxScoreMaintenanceDerivedStateService.cs
  - FSTService/Scraping/RankingsCalculator.cs
  - FSTService/Scraping/PlayerStatsTierRebuilder.cs
  - FSTService/Scraping/ScrapeTimePrecomputer.cs
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

1. Discovery stage writes complete immutable path directories only and emits a
   non-promotable manifest bound to exact v4 runtime and instrument constraints.
2. Promotion stage requires complete old/new eight-field maxima copied from
   discovery and emits the only manifest class accepted by plan/apply.
3. Plan binds the exact current publication/catalog/path revisions, validates
   current rollback and staged artifact trees/hashes plus observed-score
   bounds through the authoritative published snapshot/empty source plus
   supplemental overlay, and fingerprints published score sources,
   notification state, rank history, publication-bound population, and the
   complete score-history input consumed by registered caches, affected player
   stats, and all-song rankings for rebuilt instruments. The bounded evidence
   includes counts/ranges/hashes and never falls back to mutable population.
4. Apply first acquires the exclusive registration mutation advisory gate and
   waits for active registration/backfill/history lifecycles to drain. Its
   isolated lock session records a durable random owner token/backend identity,
   then takes path-generation and publication locks, creates or revalidates the
   manifest-digest-owned public-read freeze, and persists every later mutation
   and checkpoint through bounded transactions on that same session. Each
   dependent transaction takes source table locks in fixed order, including
   `score_history` after the solo entry tables, and verifies the lease again
   immediately before commit.
5. One lock-session transaction promotes every listed song generation. The in-process
   scraper admission cache refreshes immediately. Prior negative backfill
   checks and matching successful history-reconstruction checkpoints are
   removed only for newly usable path-backed pairs. Affected history status is
   fenced and returned to `pending`; unrelated pairs remain complete and only
   affected accounts are requeued.
6. Maintenance ranking mode bypasses `current_leaderboard_entries` and rebuilds
   affected instruments from the exact published source plus supplemental
   overlay. For each affected instrument it atomically replaces `song_stats`
   with every frozen published scope, including zero-entry scopes, deletes
   active-only old rows, and replaces the account-ranking partition before
   rebuilding composite, family, and combo dependencies. One
   immutable publication-population snapshot is passed to rankings, player
   stats, and all later cache/validation work. The frozen catalog and exact
   scope set, not active/legacy song tables or cached totals, determine
   per-instrument/overall completion denominators and cache song/instrument
   inventory.
   Target-song band over-threshold flags are
   recalculated, prior/current affected band projection scopes are refreshed,
   and dependent band rankings are rebuilt. Solo/composite/band rank history is
   not written. Chart denominators include matching promoted path-expected
   instruments when provider metadata omitted the real MIDI chart. Affected
   accounts' complete player-stat tier sets are atomically replaced, removing
   stale active-only instruments while preserving unrelated accounts;
   registered-player leaderboard rivals follow.
7. Routine notification dry-run candidates are accepted only for player ranks
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
8. The strict published-source-plus-overlay read context remains active while
   a complete current-publication API cache is built and validated in staging;
   active snapshots, worker projection rows, and legacy fallback are forbidden.
   Base, leeway, and rank-offset keys must exactly match the frozen
   publication scopes. Both staging tables must match, and every key, ETag,
   and JSON SHA-256 is captured in immutable
   `max_score_maintenance_cache_entries`.
   The committed `caches_staged` checkpoint immediately rejects ordinary
   cache-build leases and staging DML/truncation for that exact frozen
   generation; only the matching maintenance lease owner remains authorized.
   Player-stat cache payloads include `Overall` plus only frozen
   publication-scope instruments.
   Final validation requires unchanged rank-history, complete consumed
   score-history and population evidence, exact paths/maxima and complete
   zero-inclusive song-stat/ranking scope,
   canonical rollback file SHA/identity matching immutable database rows, zero
   visible delivery, the whole staged-cache hash, and semantic target-scope,
   affected-account, and overlay-only-account cache fingerprints.
9. A resume from `caches_staged` or `validated` rechecks semantic cache
   evidence and both staging tables. Cache-build leases and staging writers
   cannot replace that evidence-owned generation. Cache swap, workflow
   completion, and freeze release then commit
   atomically in a source-locked transaction on the live advisory-lock session
   while its durable mutation token remains set; staging share locks and an
   exact immutable-entry comparison run before the swap. Disposal releases the
   publication, path-generation, and exclusive mutation advisory locks before
   clearing the token. Queued holders cannot pass the advisory gate early, and
   stale direct entry or population writers remain durably blocked throughout
   the handoff.
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
Gate holders and waiters use unpooled, non-multiplexed sessions rather than
normal service-pool slots. Background/manual workers may wait with
cancellation; HTTP tracking, manual backfill, and band sync use a bounded
shared try-lock and return `503`/`Retry-After: 30` as soon as exclusive
maintenance owns the gate, including before freeze creation. The lease has no
transaction or publication-row lock and remains valid across long external
async work under the production idle-transaction timeout.

Each max-score mutation is explicitly submitted to the unpooled lease session;
its transaction revalidates the random token, backend PID, advisory/durable
owner, and all five source locks before work and immediately before commit.
The durable
band-write gate is unconditional for `band_entries`, members, member stats, and
membership state, including memberless entries. Registration/source-table
triggers, including the `leaderboard_population` statement guard, also reject
the durable exclusive owner so a write surviving shared-backend loss cannot
cross a new exclusive claim. Backend loss before final commit leaves the old
cache, durable gate, and freeze in place and refuses later checkpoints,
publication, and unfreeze. Loss during post-commit advisory-release/token-clear
handoff leaves the completed cache coherent and guarded mutations fail-closed;
only a newly validated lease may replace the stale owner and finish release.
Normal scrape freezes are unchanged.

After `rollback_captured`, every resume validates the persisted rollback file
before marking the run active or entering a later phase. Final completion
repeats that validation immediately before cache publication/unfreeze.
Deletion, corruption, noncanonical bytes, SHA mismatch, or a snapshot from a
different manifest/plan/run/publication/catalog/database rollback identity
fails resumably and leaves public reads frozen.

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
