---
status: canonical
owner: worker
last_verified: 2026-08-23
last_verified_commit: 4c36926a
sources:
  - FSTService/ScraperWorker.cs
  - FSTService/Scraping/ScrapeOrchestrator.cs
  - FSTService/Scraping/PostScrapeOrchestrator.cs
  - FSTService/Scraping/ScrapeLifecycleNotifier.cs
  - FSTService/Api/PublicationRouteSurfaceContract.cs
  - FSTService/Api/PublicReadGateService.cs
  - FSTService/Api/PublicReadGateMiddleware.cs
  - FSTService/Api/PublicationApiResponseCacheService.cs
  - FSTService/Api/PublicationApiResponseCachePolicy.cs
  - FSTService/Persistence/MaxScoreMaintenanceModels.cs
  - FSTService/Persistence/MaxScoreMaintenanceService.cs
  - FSTService/Persistence/MaxScoreMaintenanceCacheEntryEvidenceStore.cs
  - FSTService/Persistence/MaxScoreMaintenanceArtifactValidator.cs
  - FSTService/Persistence/MaxScoreMaintenanceNotificationService.cs
  - FSTService/Persistence/PublishedSoloScopeSql.cs
  - FSTService/Persistence/GlobalLeaderboardPersistence.cs
  - FSTService/Persistence/RegistrationMutationGuard.cs
  - FSTService/Persistence/MetaDatabase.cs
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/PublicationPathArtifactSchema.cs
  - FSTService/Persistence/MetaDatabase.PathPromotion.cs
  - FSTService/Scraping/ScrapePassPathIngestion.cs
  - FSTService/Api/PublicationReadContext.cs
  - FSTService/Scraping/PathDataStore.cs
  - FSTService/Scraping/MaxScoreMaintenanceDerivedStateService.cs
  - FSTService/Scraping/LeaderboardRivalsCalculator.cs
  - FSTService/Scraping/RankingsCalculator.cs
  - FSTService.Tests/Unit/RankingsCalculatorTests.cs
  - FSTService/Scraping/PlayerStatsTierRebuilder.cs
  - FSTService/Scraping/ScrapeTimePrecomputer.cs
  - FSTService/Scraping/GlobalLeaderboardScraper.cs
  - FSTService/Scraping/RegistrationBackfillWorker.cs
  - FSTService/Scraping/BackfillOrchestrator.cs
  - FSTService/Scraping/RegistrationMutationCoordinator.cs
  - FSTService/Persistence/Maintenance/DatabaseMaintenanceDryRunReporter.cs
update_triggers:
  - Scrape allocation, phase ordering, failure isolation, publication, freeze, recovery, or client notification changes.
  - Publication-bound path artifact capture, binding, read-scope, staging, or
    commit-time promotion changes.
---

# Scrape and publication flow

The worker separates candidate work from public state. A scrape can persist
diagnostic or replay data without becoming the published generation.

## Normal pass

1. **Startup and recovery**
   - Wait for startup initialization and load the song catalog.
   - Resume a publication that was durably prepared but deferred.
   - Complete required improvement-notification recovery before another scrape.
   - A marker already completed for the exact published scrape remains terminal
     while a later candidate is frozen only in explicit run-once resume mode
     with a positive different scrape ID and exact resume phases; incomplete,
     mismatched, or ordinary-worker recovery still waits for the freeze to
     clear.
2. **Authentication**
   - Acquire or refresh the Epic session before entering the scheduled loop.
3. **Exact catalog selection**
   - A new pass requires a successful, fully parsed provider catalog capture.
   - An inexact/safety-merged capture aborts before scrape allocation.
   - Resume mode reloads the immutable catalog bound to the resumed scrape.
   - Allocation also captures the complete publication-bound path artifact
     snapshot for the new working publication and emits a ready
     `path_artifacts` binding. See
     [Publication path artifact snapshots](../database/PublicationPathArtifactSnapshots.md).
   - With `Scraper:EnableScrapePassPathGeneration` enabled, the pass then
     stages pending-song path generations into that candidate snapshot, before
     the publication read scope opens and before instrument support and scrape
     requests are built. Staging never writes live `songs` rows.
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
   - Snapshot-only production workers complete the publication-critical legacy
     `RankRecompute` contract without executing the legacy update; enabling the
     legacy-write rollback flag restores the existing recompute path.
   - Only best-effort phases may persist `skipped`. Resume rejects every
     non-completed critical outcome. Publication rejects corrupt critical
     `skipped` rows regardless of the rollout enforcement switch; normal
     critical failures retain the existing enforcement policy.
   - PostgreSQL finalization does not schedule retired wrapper checkpoint or
     rankings-cache-warm calls.
   - Publication precompute stages canonical freeze-critical JSON: songs,
     page-1 rankings, registered-player defaults, leaderboard bootstrap rows,
     and existing derived cache families. Request aliases project contained
     ranking windows from canonical rows instead of duplicating payloads.
   - Dedicated registration workers and the run-once drain own durable
     registration backlog/history work; the retired deferred post-scrape sync
     is not a publication phase.
   - Per-instrument validity, leeway, and ranking calculations consume the
     eight persisted CHOpt maxima, including distinct plastic-drums modes.
   - A missing provider difficulty remains scrape-eligible unless an explicit
     non-charted value is present. Ranking denominators stay bounded to the
     exact current catalog while unioning provider support, promoted path
     support, and positive population for the same song/instrument.
   - Current ranking materialization filters retained score/stat sources to
     exact current-catalog song IDs without deleting their historical rows.
     Positive current-catalog population may retain a denominator scope even
     when an explicit provider sentinel blocks its current refresh.
   - For each successfully rebuilt instrument, the summary pass fails
     publication-critical ranking work before aggregate calculation when
     denominators differ by account, counts exceed the denominator, or
     coverage/FC rates are non-finite or outside the valid range. A
     zero-denominator instrument remains an explicit warn-and-skip, not a
     rebuilt partition.
   - Publication-critical outcomes can reject the candidate; best-effort
     failures and intentional skips remain visible without silently changing
     their classification.
7. **Prepare publication**
   - Validate scrape and phase outcomes.
   - Re-emit the `path_artifacts` binding from the candidate snapshot instead of
     overwriting it with the legacy live binding. An incomplete snapshot leaves
     the surface closed rather than falsely ready.
   - Build required published scope-source mappings and notification plans.
   - Physical snapshot IDs in those mappings are retention pins only while
     their generation is named as current, previous, or working. Publication
     IDs resolve through `publication_generations.scrape_id`; older unnamed
     source maps remain historical evidence but do not pin hot partitions
     forever.
   - Prepare the next publication generation outside the final commit.
8. **Commit publication**
   - Record commit intent, drain bounded readers, and atomically advance the
     publication pointer and generation-owned state.
   - Staged path promotions are compare-and-swapped into live `songs` rows in
     the same transaction, immediately before the pointer advances. The CAS
     owns the live revision and current generation ID, not the provider
     timestamp; a song whose provider timestamp changed mid-scrape stays
     pending. An unexpected CAS mismatch is nonretryable, rolls the commit
     back, and fails and isolates the candidate instead of deferring it.
   - Contention can defer a prepared publication for retry without exposing it.
     A deferred or restarted commit reconstructs promotion inputs from
     `publication_path_artifacts` alone.
9. **Release and notify**
   - Unfreeze public reads and invalidate in-process caches.
   - Run post-publication notification detection.
   - Notify connected clients only after the new generation can be served.

Matched control scrape `1299` and candidate scrape `1300` accepted the retired
post-scrape path cleanup. Their manifest and published-source key sets were
exactly equal (8,424 and 6,318 respectively), every source contract validated,
both publication generations became ready/current normally, and the
publication cache retained the same 9,251-key surface. Live score movement
changed values and ETags as expected, but the song-catalog hash, route/cache
shape, critical outcomes, freeze/unfreeze behavior, and notification outbox
shape remained exact. Candidate `1300` emitted no retired phase
attempt/outcome and no critical skip; its three best-effort pressure skips were
nonblocking and reasoned in durable progress.

## Failure behavior

- An incomplete network pass skips full-data post-processing and publication.
- A rejected candidate leaves the prior publication current.
- If durable failed-candidate isolation cannot be confirmed, reads remain
  fail-closed rather than unfreezing optimistically.
- Publication lookup/read-gate failures fail closed.
- Run-once exits only after its publication decision and any permitted
  registration drain.

Scrape `1305` exercised the interrupted-candidate resume contract after a
PostgreSQL backend OOM. Public reads stayed frozen on scrape `1304`; the guard
required the exact stalled candidate, positive persisted metrics, and
resume-only phases. The worker reloaded publication `94`'s immutable catalog,
ran no network/writer phases, completed the solo-leaderboards chain, published
scrape `1305`, unfroze reads, completed notification recovery and registration
history drain, then exited `0`. Manual freeze clearing or direct candidate
promotion is not equivalent to this fail-closed recovery path.

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
   supplemental overlay. The CHOpt maximum is the ratio denominator; the exact
   ranking eligibility threshold is separately
   `floor(newMaximum × 21 / 20)`. All target request, actual current/staged,
   manifest, and report maxima are bounded at `2,045,222,521`, keeping their
   exact `1.05` cutoffs representable as PostgreSQL `INTEGER`. Unrelated
   frozen-catalog maxima above that admission bound do not invalidate the
   target plan; general threshold computation saturates them at `int.MaxValue`,
   which is equivalent for the stored `INTEGER` score domain and keeps SQL
   parameters representable. Plan report/digest contract v6 records each
   target cutoff, raw highest score, highest score eligible at or below that
   cutoff, and above-cutoff row count. Above-cutoff rows are retained as
   ranking-invalid evidence rather than blocking the plan. The contract also
   fingerprints published score sources, notification state, rank history,
   publication-bound population, and the complete score-history input consumed
   by registered caches, affected player stats, and all-song rankings for
   rebuilt instruments. The bounded evidence includes counts/ranges/hashes and
   never falls back to mutable population.
4. Apply first acquires the exclusive registration mutation advisory gate and
   waits for active registration/backfill/history lifecycles to drain. Its
   isolated lock session records a durable random owner token/backend identity,
   then takes path-generation and publication locks, creates or revalidates the
   manifest-digest-owned public-read freeze, and persists every later mutation
   and checkpoint through bounded transactions on that same session. Each
   dependent transaction takes source table locks in fixed order, including
   `score_history` after the solo entry tables, and verifies the lease again
   immediately before commit. After freeze and on every resume, it reloads the
   observed-score maxima/counts and reconstructs the approved v6 plan digest
   before any mutation continues. Any outlier-population drift therefore
   rejects apply/resume even though above-cutoff rows are not compatibility
   blockers.
   A later resume keeps the mutation/path locks and durable owner but yields the
   global publication lock between transactions, taking it transactionally
   only at commit. Frozen cached reads therefore continue during long recovery
   reads, derived work, or cache generation while MVCC hides uncommitted state.
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
   blank affected account IDs are excluded only after proving that they have no
   history, registration, or account-cache identity. Registered-player
   leaderboard rivals then rebuild only the changed instruments. One
   authoritative profile batch per changed instrument covers all registered
   users and deduplicated ranking neighbors; the existing five methods,
   directions, and top-200 C# sample semantics are retained, while unrelated
   instrument rival rows/state are untouched.
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
   a complete current-publication API cache is built and validated in staging,
   including canonical songs and per-song/per-instrument top-10 leaderboard
   rows;
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
9. A resume uses the durable phase as its exact branch selector. From
   `notifications_quarantined` it skips path/derived/notification mutation and
   rebuilds only cache staging before validation/finalization. From
   `caches_staged` or `validated` it rechecks semantic cache
   evidence and both staging tables. Cache-build leases and staging writers
   cannot replace that evidence-owned generation. Cache swap, workflow
   completion, and freeze release then commit
   atomically in a source-locked transaction on the live advisory-lock session
   while its durable mutation token remains set; staging share locks and an
   exact immutable-entry comparison run before the swap. The transaction keeps
   `lock_timeout=5s`, uses the configured maintenance `statement_timeout` only
   around that final comparison, and restores `statement_timeout=120s` before
   the bounded swap/checkpoint/verification/unfreeze mutations. Before the
   cache swap the same transaction refreshes the current publication's
   `publication_path_artifacts` rows from the restored or promoted `songs`
   rows and recomputes the `path_artifacts` binding row count and SHA-256.
   Failure to
   validate or restore the bounded timeout rolls back without releasing the
   freeze or durable gate. Disposal releases the
   publication, path-generation, and exclusive mutation advisory locks before
   clearing the token. A normal queued registration holder that acquires the
   shared advisory lock during that short handoff checks the durable owner
   backend identity, releases the shared lock, and retries until the live owner
   clears the token. Bounded try-acquire still rejects immediately, and an
   orphaned token whose owner backend is gone remains fail-closed. Stale direct
   entry or population writers remain durably blocked throughout the handoff.
   Service processes invalidate path/song/response and scraper-admission caches
   and force connected clients to refresh the unchanged publication ID.
   Registration lease acquisition independently refreshes path/instrument
   support before lookup work, closing the interval before the monitor pass.
10. Guarded rollback is a distinct one-shot lifecycle. Dry-run validates the
    exact incomplete promoted-path state, freeze/publication, canonical rollback
    file/database rows, promoted paths, worker/backends/locks, and artifact
    identity without taking the mutation lease. Execution moves the durable run
    into rollback-only phases so apply/resume cannot race it. Path restoration
    and checkpoint commit atomically; affected rankings/stats/rivals,
    notification quarantine, and complete publication caches are then rebuilt
    from rollback maxima plus the unchanged publication source/population.
    The rollback read snapshot validates both the accepted post-promotion
    score-history selector and the exact restored-maximum selector, including
    lower thresholds and a missing pre-apply maximum. Rollback notification
    alignment includes an explicit `rollback` direction
    in its audit digest, preventing reuse of an apply audit when candidate sets
    are identical or empty. Its audit rows, state alignment, rollback audit
    identity/counts, and durable notification checkpoint share one
    transaction. Separate rollback cache-entry evidence preserves
    apply evidence. The registration and path
    advisory locks remain session-owned, but rollback yields the global
    publication lock between transactions. Each atomic unit takes the
    transaction-scoped exclusive publication lock only at commit, so cached
    public reads do not queue behind long derived/cache work. Only exact final
    validation and canonical rollback-file revalidation allow one transaction
    to swap rollback caches, mark `rolled_back`, and release the
    same-publication freeze. Interruption keeps the freeze and resumes from the
    last rollback phase. `rollback_captured` is accepted only when current
    paths prove promotion already committed; otherwise it remains ineligible.
    The max-score freeze rejects normal cache builders, swaps, and non-owner
    staging mutations for the entire rollback, preventing an intermediate
    checkpoint from replacing the prior published cache. This fence does not
    depend on `working_publication_id` remaining null, and scrape allocation
    rejects the active max-score freeze/token before creating a generation.

During this maintenance freeze, `/api/songs` may serve its stable process
cache, immutable current-generation path PNG/JSON files may be served when
present, and outer-cache exact solo leaderboards may be served. When the songs
cache was warmed before path promotion, its prior generation ID is temporary
skew rather than an invalid client path: PNG and JSON requests carrying that
valid stale ID return `503` with `Retry-After: 30` and never read the old
immutable directory. Other cold dependent reads use the same `503` path before
publication read-context or boundary-lease acquisition, so maintenance lock
waits cannot surface as `500`. Ordinary publication transitions still acquire
and hold their existing read leases and retain stale-ID `400`/missing-artifact
`404` behavior. Cacheable ranking/player/band routes otherwise serve the prior
published cache or return `503`; exact solo leaderboards never fall through to
current max-score/leeway reads. Player tracking, selected-profile registration
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
Once rollback begins, apply/resume is rejected. The rollback command validates
the same canonical file/digest on every retry and never deletes promoted
immutable generations, original apply evidence, or notification audit rows.
If the final PostgreSQL commit succeeds but acknowledgement or the immediate
state reload fails, the command reconciles durable `rolled_back` plus the
released freeze, reacquires the exclusive mutation gate if necessary, clears a
proven stale durable owner through normal lease disposal, verifies all owner
fields are null, and only then emits a successful terminal report. A later
terminal retry performs the same cleanup.
Dry-run never performs terminal cleanup; an already-`rolled_back` dry-run is
rejected without acquiring a lease. Apply/resume after rollback begins emits a
non-resumable report using the actual rollback phase and freeze state.
If terminal cleanup remains blocked, report v2 preserves the true
`rolled_back`/unfrozen state with `cleanupPending=true` and requires an execute
retry. The report target itself is reserved before any rollback mutation.
Only an invocation that passed terminal validation and attempted completion or
cleanup may reconcile an ambiguous commit as success; unrelated terminal
preflight failures remain failures.

## Publication-aware API and browser

Publication-bound routes are classified and mapped to required data surfaces by
`PublicationRouteSurfaceContractCatalog`. Operational-live and admin/private
routes have separate behavior.

The browser bootstraps through `/api/publication`. When publication changes it
clears React Query and song caches, reconnects the application WebSocket, and
remounts the application against the new publication. Request pinning remains
effective only when configured and every required surface reports ready.

When `Scraper:UsePublicationPathArtifacts` is enabled, the publication read
middleware also opens a `PathDataStore` publication read scope for the exact
request publication before downstream execution, so every publication-bound
consumer resolves path state and CHOpt maxima from that generation's snapshot.
See
[Publication path artifact snapshots](../database/PublicationPathArtifactSnapshots.md).

Covered service reads first resolve an L1 entry bound to publication and the
current safety revision, then the authoritative current/previous L2 row.
During a required-cache freeze, misses never compute or write. Unfrozen bounded
overview variants may single-flight, compute once, and write through only when
the measured response is successful JSON below the one-second/2 MiB gates.
Same-publication maintenance changes the L2 surface hash/bytes before unfreeze;
API monitors clear L1 on that safety transition.

## Selective execution

The phase flags select the first launch pass. `ScrapePhaseResolver` expands
groups and fills intermediate solo phases; later scheduled cycles revert to the
full pipeline. See [the worker document](../components/worker.md) and
[CLI reference](../reference/cli.md).
