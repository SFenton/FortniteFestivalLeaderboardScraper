---
status: canonical
owner: data
last_verified: 2026-08-23
last_verified_commit: 4c36926a
sources:
  - FSTService/Persistence/PublicationPathArtifactSchema.cs
  - FSTService/Persistence/MetaDatabase.PathPromotion.cs
  - FSTService/Persistence/PublicationPathPromotion.cs
  - FSTService/Scraping/ScrapePassPathIngestion.cs
  - FSTService/Scraping/ScrapeOrchestrator.cs
  - FSTService/Scraping/PathGenerationCoordinator.cs
  - FSTService/Api/AdminPathRegenerationGate.cs
  - FSTService/Api/AdminEndpoints.cs
  - FSTService/Persistence/PublicationPathArtifactReleaseGate.cs
  - FSTService/StartupInitializer.cs
  - deploy/config/fstservice-role.env
  - deploy/config/fstworker-role.env
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/MetaDatabase.cs
  - FSTService/Persistence/MetaDatabase.Publication.cs
  - FSTService/Persistence/PublicationGeneration.cs
  - FSTService/Scraping/PathDataStore.cs
  - FSTService/Scraping/PathDataStorePublicationScope.cs
  - FSTService/Scraping/IPathDataStore.cs
  - FSTService/Scraping/PathArtifactResolver.cs
  - FSTService/Api/PublicationReadContext.cs
  - FSTService/Api/PublicationReadiness.cs
  - FSTService/Api/SongEndpoints.cs
  - FSTService/Api/SongsCacheService.cs
  - FSTService/Api/PublicationApiResponseCacheService.cs
  - FSTService/Api/PublicApiResponseCacheMiddleware.cs
  - FSTService/Api/PublicationApiResponseCachePolicy.cs
  - FSTService/StartupInitializer.cs
  - FSTService/SongCatalogRefreshWorker.cs
  - FSTService/Program.cs
  - FSTService/ScraperOptions.cs
update_triggers:
  - Publication path artifact schema, capture, binding, retention, read
    scoping, or source-flag behavior changes.
  - Scrape-pass staging, staged-promotion metadata, or publication-commit
    live promotion behavior changes.
---

# Publication path artifact snapshots

`publication_path_artifacts` is a durable data family that binds path
generation state and CHOpt maxima to a specific publication generation, so
published reads never depend on the mutable live `songs` table.

The family has two layers:

- **Phase A** — publication-bound capture, binding, retention, and effective
  reads behind `Scraper:UsePublicationPathArtifacts`.
- **Phase B** — publication-safe scrape-pass path staging behind
  `Scraper:EnableScrapePassPathGeneration`. Staging only writes the working
  publication snapshot; live `songs` rows change exclusively inside the
  publication commit transaction that advances the pointer.

Legacy API-owned automatic generation
(`Scraper:EnableAutomaticPathGeneration`) remains rejected at startup in both
layers because it promotes mutable live rows outside the publication
pipeline.

## Ownership

| Concern | Owner |
|---|---|
| Table and canonical hash function | `PublicationPathArtifactSchema` |
| Startup migration and bootstrap backfill | `DatabaseInitializer` (`publication-path-artifacts` step) |
| Candidate capture and retention | `MetaDatabase.StartScrapeRun` |
| Binding re-emission at preparation | `MetaDatabase.PreparePublicationSurfaceBindings` |
| Snapshot refresh after max-score maintenance | `MetaDatabase.CompleteMaxScoreMaintenanceCore` |
| Effective publication reads | `PathDataStore` + `PathDataStorePublicationScope` |
| Scrape-pass staging | `ScrapePassPathIngestion` (worker scrape pass) |
| Candidate staged promotion | `MetaDatabase.ApplyWorkingPublicationPathPromotion` |
| Commit-time live promotion | `MetaDatabase.PromoteStagedPathArtifacts` |
| Admin regeneration race gate | `AdminPathRegenerationGate` |
| Automatic-staging deferral state | `songs.path_generation_*` deferral columns + `PathDataStore` |
| Readiness and source evidence | `PublicationReadinessEvaluator`, `MetaDatabase.GetPublicationSurfaceSourceEvidence` |

## Schema

One canonical row per publication catalog song:

```text
publication_path_artifacts (
    publication_id  BIGINT  -- FK publication_generations ON DELETE CASCADE
    song_id         TEXT
    path_generation_revision, path_artifact_generation_id,
    dat_file_hash, song_last_modified, catalog_last_modified,
    paths_generated_at, chopt_version, chopt_binary_sha256,
    path_generation_profile, path_expected_instruments,
    path_generation_pending,
    max_lead_score, max_bass_score, max_drums_score, max_vocals_score,
    max_pro_lead_score, max_pro_bass_score, max_pro_cymbals_score,
    max_pro_drums_score,
    captured_at,
    -- Phase B staged-promotion metadata (additive, idempotent)
    promotion_pending, promotion_attempt_id, promotion_generation_id,
    promotion_source, promotion_staged_at,
    expected_live_revision, expected_live_generation_id,
    PRIMARY KEY (publication_id, song_id)
)
```

The Phase B columns are added with `ADD COLUMN IF NOT EXISTS`, so the migration
is additive and repeatable. Rows with no staged promotion keep
`promotion_pending = FALSE` and null promotion metadata, which is exactly Phase
A semantics. A fresh candidate capture never inherits stale promotion metadata
because capture inserts new rows with the column defaults.

The primary key is the only access path required by current reads
(`publication_id` equality and `(publication_id, song_id)` lookup), so no
additional index is created.

Rows are stored raw. The invalid plastic-drums profile suppression rule is
applied on read in `PathDataStore`, exactly as it is for live rows, so the
snapshot preserves the recorded generation truth.

### Authoritative null rows

Every song in the bound publication catalog gets a row, including songs with no
generated paths. Those rows carry a null `path_artifact_generation_id`, null
maxima, and revision `0`. A missing row is a defect, never "no paths".

### Manifest version

`PublicationPathArtifactSchema.ManifestVersion` (currently `2`) versions the
canonical manifest projection and its SHA-256 function. It is deliberately
separate from `ContractVersion` (`1`), which remains the global route surface
contract and does not move when only the path manifest hash changes.

Version 2 adds Phase B staged-promotion metadata to the hashed identity, so a
version-1 `content_hash` can never match a version-2 function result. Every
binding carries `manifestVersion`, and `PathDataStore` requires both
`contractVersion` and `manifestVersion` to match before serving a snapshot, so
a live upgrade can never serve or accept a stale hash.

### Canonical manifest hash

`publication_path_artifact_manifest_sha256(publication_id)` returns the
deterministic SHA-256 of the snapshot: every column joined with `chr(31)` per
row, rows joined with `chr(30)` ordered by `song_id`, `NULL` normalized to the
empty string, and timestamps rendered as UTC ISO-8601 microseconds. Staged
promotion metadata is part of the hashed candidate identity, so a staged
promotion changes the binding hash. Public `PathGenerationState` and
`SongMaxScores` reconstruction still uses only the target published columns;
promotion metadata is never projected into a read model. The same
function computes the binding hash and the readiness source evidence hash, so
the two can never drift.

## Binding contract

The `path_artifacts` publication surface binding is emitted from the snapshot:

| Snapshot state | Binding kind | Status |
|---|---|---|
| Row count equals the bound exact catalog song count | `generation_path_artifact_manifest` | `ready` |
| Any other state (missing rows, missing/inexact catalog) | `legacy_live_unversioned` | `building` |

A ready binding JSON identifies `table`, `publicationId`, `scrapeId`,
`source`, `authoritative: true`, `contractVersion`, `manifestVersion`, and
`expectedRowCount`.
Recognized `source` values are `legacy_live_backfill`,
`schema_manifest_upgrade`, `generation_candidate_snapshot`,
`scrape_pass_path_staging`, `generation_prepared_snapshot`, and
`max_score_maintenance_apply`.

An incomplete snapshot fails closed: it never reports `ready`, and it never
carries a content hash.

## Lifecycle

1. **Startup migration.** The `publication-path-artifacts` step runs as one
   short transaction under bounded lock/statement timeouts. It creates or
   alters the table, replaces the canonical hash function, backfills the
   **current publication only** from its exact `publication_song_catalog`
   joined to live `songs`, emits the `legacy_live_backfill` binding, retires
   superseded snapshots, and rebinds every retained active pointer publication
   (current, previous, and working when non-null) whose binding predates the
   current `manifestVersion`, using the `schema_manifest_upgrade` source. The
   backfill is skipped when the publication already has rows, so repeated
   startups do not rewrite snapshot rows.

   Because the function replacement and the rebinds share one transaction,
   there is no interval in which the new hash function is visible while an
   active binding still carries a hash computed by the old one. The upgrade
   only rewrites bindings; snapshot rows and `captured_at` are untouched, and
   rerunning it is a no-op.
2. **Scrape allocation.** `StartScrapeRun` captures the complete candidate
   snapshot for the new working publication in the same transaction that
   captures the catalog, emits the `generation_candidate_snapshot` binding, and
   fails allocation if the snapshot is incomplete.
3. **Scrape-pass staging (Phase B).** On a full resolved pipeline only,
   immediately after allocation and before the scrape opens its publication
   read scope, `ScrapePassPathIngestion` stages generations for pending catalog
   songs and applies each validated generation to the candidate snapshot,
   rebinding the ready manifest each time. Phase-selective passes skip staging
   so maxima cannot change without rebuilding rankings, statistics, and the
   canonical songs cache together. The snapshot stays complete even when songs
   are excluded, blocked, or failed.
4. **Publication preparation.** Preparation re-emits the binding as
   `generation_prepared_snapshot`. It no longer overwrites the surface with the
   legacy live binding.
5. **Publication commit.** The commit transaction locks the
   `promotion_pending` rows in song order, locks the matching `songs` rows, and
   compare-and-swaps the staged state into live rows immediately before the
   publication pointer advances. Fresh and deferred commits first revalidate
   manifest version, exact row count, canonical hash, and that a candidate with
   staged promotions owns a rebuilt rather than inherited canonical songs
   cache.
6. **Max-score maintenance.** The final same-publication apply/rollback
   transaction refreshes the current publication rows from the restored or
   promoted `songs` rows and recomputes the binding count and hash before the
   cache swap and unfreeze.
7. **Retention.** Only current, previous, and working publication snapshots are
   retained. Superseded bindings are retired to
   `retired_generation_path_artifacts`; failed candidates are cleaned up and
   marked `failed_generation_path_artifacts`.

## Reads

`Scraper:UsePublicationPathArtifacts` (backend-only, default `false`) selects
the read source:

- **Off** — every read is a live `songs` read. Behavior is byte-compatible with
  the pre-Phase-A service.
- **On** — the unqualified `IPathDataStore` read members
  (`GetAllMaxScores`, `GetPathGenerationStates`, `GetPathGenerationState`) serve
  the publication snapshot.

Mutation, generation, and maintenance code paths call the explicit
`GetLiveAllMaxScores`, `GetLivePathGenerationStates`, and
`GetLivePathGenerationState` members and always observe live rows. That
includes `PathGenerationCoordinator` and `MaxScoreMaintenanceService`.

Scrape allocation opens the new working publication's read scope before
network threshold/support selection. The worker reopens the same scope for
post-processing, rankings, statistics, and publication cleanup, while
`ScrapeTimePrecomputer` scopes its canonical songs payload to the exact cache
target publication. An out-of-band live generation after allocation therefore
belongs to a later publication and cannot change the current candidate's
thresholds, derived rows, or advertised generation ID.

### Read scoping

`IPathDataStore.BeginPublicationRead(publicationId)` opens an `AsyncLocal`
scope. `PublicationReadContextMiddleware` and
`PublicationBoundaryReadLeaseMiddleware` open the scope with the exact request
`PublicationReadContext` publication ID before downstream execution, so every
publication-bound consumer, not only `/api/songs` and `/api/paths`, reads the
same generation. Concurrent requests and background tasks stay isolated because
the scope flows with the asynchronous control flow and is restored on dispose.

Resolution order when the flag is on:

1. explicit scope, which **fails closed** with
   `PublicationPathArtifactsUnavailableException` when that publication has no
   snapshot rows;
2. otherwise the current publication pointer snapshot;
3. otherwise live rows (no current publication, for example before the first
   publication or during max-score-maintenance no-context fallbacks).

Publication-scoped results are cached separately per publication and are never
written into the shared live max-score cache.

### `/api/songs`

With the flag on, `/api/songs` is built strictly from the bound publication
catalog plus the scoped snapshot. Mutable live catalog fields are not overlaid.
With the flag off the existing build path, including the frozen published
fallback, is unchanged.

### Songs cache ownership

With the flag on, the durable current-publication `public-api:songs:v1` row is
owned by the publication pipeline, which builds it during scrape-time
precompute with population tiers and promotes it at publication commit.

Every other process is a reader. `SongsCacheService` is constructed with
`publicationBoundReads` from `Scraper:UsePublicationPathArtifacts`, and in that
mode:

- `Prime` (startup priming, catalog-refresh priming, and the
  `SetDurableRefresh` callback) hydrates the in-process cache from the durable
  row instead of rebuilding it, and verifies byte/ETag identity before
  installing it;
- `TrySetIfBuildTokenUnchanged` refuses to persist any process-local build, so
  no code path can overwrite the canonical row;
- a same-publication content change clears the in-process cache but does not
  mark the durable key stale, because that row is bound to the published
  catalog snapshot rather than this process's live catalog;
- when no usable durable row exists, a local build may populate the in-process
  cache only, but it is built from the exact bound publication catalog and path
  snapshot rather than mutable live catalog state. It is never persisted, so a
  degraded payload cannot poison the publication cache or expose an unpublished
  song.

The public response cache middleware follows the same ownership rule. For
`/api/songs` in publication-bound mode the request plan is rewritten so the
canonical key is the only lookup candidate and write-through is disabled, which
means a degraded endpoint build can neither create nor be served from a
`public-route:/api/songs...` row. Startup additionally purges any pre-existing
route-key rows for that path so the canonical key wins immediately after
rollout; the purge is best effort because the plan rewrite already makes those
rows unreadable in this mode.

Hydration is also race-tolerant: when the publication pointer moves between
capturing the build token and reading the durable row, hydration retries
against the new pointer, bounded by
`SongsCacheService.MaxHydrationAttempts`, instead of falling back to a
process-local build.

This is the fix for a live canary regression: restarting the service role with
the flag on rebuilt `public-api:songs:v1` from process-local state whose
precomputer had no population tiers, and persisted a 725 KB payload over the
canonical 4.49 MB one. With the flag off, cache behavior is unchanged.

## Scrape-pass staging (Phase B)

`Scraper:EnableScrapePassPathGeneration` (worker-only, default `false`) turns
on publication-safe staging. `ScrapeOrchestrator` invokes it once per pass,
after `StartScrapeRun` captured the candidate snapshot and before
`BeginPublicationRead`, so the scrape's thresholds, instrument support,
rankings, and precompute all consume the updated candidate.

`Scraper:EnableScrapePassPathGeneration` requires
`Scraper:UsePublicationPathArtifacts` and `Scraper:EnablePathGeneration`;
startup rejects any other combination, because a staged generation is only
readable through the publication-bound snapshot.

Staging is best-effort infrastructure for the scrape pass, never a gate on it.
Selection, admission, provider, generation, validation, and repository failures
are contained: they are logged, reported in the pass result (`Aborted` with a
failure reason), and the scrape continues against the candidate snapshot as it
stands. Only caller cancellation propagates, and the worker operation is always
closed or failed.

Batch-wide prerequisites such as the MIDI key and CHOpt runtime fail the
staging batch before any song attempt. They leave selected songs' retry/review
state untouched so a corrected deployment can retry immediately; only an
actual per-song attempt or timeout consumes that song's bounded backoff.

Selection and execution:

- Phase-selective passes do not run automatic staging. Only
  `ScrapePhase.All` can publish staged maxima because it rebuilds every
  maximum-dependent ranking/statistics surface and `public-api:songs:v1`.
- Pending song IDs and per-song state are read from live `songs` rows
  explicitly (`GetPendingPathGenerationSongIds`,
  `GetAutomaticPathGenerationCandidates`, `GetLivePathGenerationStates`).
- Songs deferred for review or backoff are excluded from automatic selection
  (see [Automatic staging deferral state](#automatic-staging-deferral-state)).
- A `SongPathRequest` is built for exact catalog songs that have an encrypted
  chart. Empty provider instrument metadata is admitted because the coordinator
  decrypts the MIDI and discovers non-empty supported tracks; a genuinely empty
  chart fails per-song and receives normal retry state.
- Songs are processed in `song_id` order, capped by
  `Scraper:ScrapePassPathGenerationMaxSongs`, under the whole-batch budget
  `Scraper:ScrapePassPathGenerationTimeout`.
- Staging reuses `PathGenerationCoordinator.StagePathsSerialAsync` with
  `stopOnFailure: false` and a completion collector, so songs that finished
  before the batch budget expired are still applied to the candidate. Max-score
  maintenance keeps the strict stop-on-first-failure default and its
  all-or-nothing return contract.
- When the budget expires, the song that was generating at that moment is
  backed off; songs that were never attempted keep their place at the front of
  the next pass.
- Every staged generation is revalidated with
  `PathArtifactResolver.ValidateImmutableGeneration` and an identity check
  against the staged promotion before any candidate write.

Classification of a validated generation against the live state it was staged
against:

| Case | Result |
|---|---|
| No existing generation: revision `0`, null generation ID, all eight maxima null | Bootstrap, applied |
| Existing generation whose eight staged maxima are all identical | Identical refresh, applied |
| Existing generation with any changed maximum | Blocked by default: a `max_score_change_requires_review` row is recorded in `path_generation_errors`, the song is durably marked review-required, and the candidate, the live row, and `path_generation_pending` are unchanged |

`Scraper:ScrapePassPathGenerationAllowChangedMaxima` opts into applying changed
maxima. It is off by default because a changed published maximum is a reviewed
max-score maintenance decision, not a scrape-pass decision.

Failures are per-song warnings: a failed download, generation, validation, or
candidate conflict leaves that song pending with its candidate and live rows
untouched, and the batch continues. Temporary `.path-work` directories are
reclaimed by the existing coordinator behavior. A generation that was moved
to immutable storage but then rejected before candidate attachment is deleted
only after a database reference check proves that neither live state nor any
retained publication snapshot/promotion row owns it. Verification or deletion
failure retains the directory and records `orphan_cleanup` evidence.

Each pass logs and reports pending, selected, staged, applied-to-candidate,
bootstrap, identical-refresh, changed-blocked, failed, conflicted, and
remaining counts, and publishes a `scrape.path_staging` worker operation.

### Automatic staging deferral state

`songs` carries additive deferral columns that decide only whether an
*automatic* attempt may run now. They never clear `path_generation_pending`:
a deferred song stays pending, so the owed work and its reason remain
auditable.

```text
songs (
    path_generation_review_required   BOOLEAN NOT NULL DEFAULT FALSE
    path_generation_review_reason     TEXT
    path_generation_review_at         TIMESTAMPTZ
    path_generation_next_attempt_at   TIMESTAMPTZ
    path_generation_attempt_count     INTEGER NOT NULL DEFAULT 0
    path_generation_deferral_identity TEXT
)
```

- **Review required.** A blocked max-score change sets
  `path_generation_review_required` with a reason. The song is excluded from
  automatic selection until it is re-armed, so one blocked song cannot consume
  the per-pass cap or re-record the same error and regenerate the same
  artifacts every scrape.
- **Retry after.** A deterministic generation failure, an invalid staged
  generation, a candidate promotion conflict (explicit `Conflict`,
  `SongMissing`, or `PublicationNotStaging`, or a thrown repository error), or
  a budget-consuming timeout schedules
  `path_generation_next_attempt_at` with bounded exponential backoff of
  `1, 2, 4, …` hours capped at 24 hours, driven by
  `path_generation_attempt_count`. Scheduling a retry also clears any
  review-required flag and review timestamp and records the retry reason: the
  song was re-armed and attempted again, so the ordinary retry outcome replaces
  the obsolete review decision. Without that transition a song re-armed by a
  catalog change whose next attempt failed would stay excluded forever.
- **Catalog re-arm.** Both deferrals record the provider catalog identity they
  were taken against in `path_generation_deferral_identity`. When
  `songs.last_modified` no longer matches it, the song is eligible again
  immediately: a new chart is a new question.
- **Success clears state.** A successful live promotion clears review, backoff,
  and attempt count. That covers the publication-commit compare-and-swap,
  explicit admin regeneration, and max-score maintenance batch promotion.
- **Operator reset.** `POST /api/admin/path-generation/rearm?songId=<id>`
  clears review and backoff state for one song and reports the resulting
  state. It never generates paths and never touches published data.

### Candidate promotion compare-and-swap

`ApplyWorkingPublicationPathPromotion` applies exactly one validated generation
in one transaction and proves, in the statement itself, that:

- the publication is still the `building` working publication of that scrape;
- the snapshot row exists and its revision, generation ID, and catalog
  timestamp still match the state the generation was staged against;
- the row does not already carry a staged promotion.

It then writes the target path fields, maxima, expected instruments, runtime
identity, and generation ID, sets the target revision to `expected + 1`, clears
the target pending flag, persists `expected_live_revision` /
`expected_live_generation_id` with `promotion_pending = TRUE`, and rebinds the
ready `path_artifacts` manifest with the new row count and SHA-256. Zero
matched rows are classified explicitly as `Conflict`, `SongMissing`, or
`PublicationNotStaging`.

### Live promotion at publication commit

Inside `CommitPreparedScrapePublication`, before the pointer advances:

1. `promotion_pending` rows are selected `FOR UPDATE` in `song_id` order.
2. The matching `songs` rows are locked in the same order.
3. Live rows are updated only where
   `songs.path_generation_revision = expected_live_revision` **and**
   `songs.path_artifact_generation_id IS NOT DISTINCT FROM
   expected_live_generation_id`.

The compare-and-swap deliberately does **not** include the provider
`songs.last_modified` value: an ordinary catalog refresh may legitimately change
it mid-scrape. Instead, `path_generation_pending` is cleared only when
`NULLIF(songs.last_modified, '')` still matches the staged
`catalog_last_modified`; otherwise the song stays pending and the next scrape
pass regenerates it.

If the affected row count is not exactly the staged count, the commit throws
`PublicationPathPromotionConflictException`. That is nonretryable by
construction: the transaction rolls back, the pointer does not move, the live
rows keep their out-of-band generation, and the outer worker path treats it as
a `PublicationCommitExecutionException` that fails and isolates the candidate.
It is never converted into a busy or deferred outcome, so an unexpected
conflict cannot become a ready-deferred wedge.

Promotion inputs are read exclusively from `publication_path_artifacts`, so a
deferred or restarted commit promotes correctly from durable state with no
in-memory dependency. Staged rows are left intact after commit because they are
part of the committed candidate identity and its binding hash. Cache
invalidation after the pointer change is the normal publication monitor path.

Preparation refuses to inherit the previous publication cache when any
`promotion_pending` row exists. The final commit transaction independently
requires a manifest-v2 ready binding with exact catalog/snapshot counts and the
canonical hash, and requires the API-cache binding's
`inheritedFromPublicationId` to be JSON null for staged promotions. A
mixed-version or deferred candidate therefore fails before pointer movement
instead of publishing stale path/cache state.

### Release readiness gate

A role that sets `Scraper:ApiOnly=true`,
`Scraper:SkipStartupSchemaInitialization=true`, or
`Scraper:RolloutReadOnlyStartup=true` never runs DDL, so it can otherwise start
against a database whose path artifact release has not been applied. When such
a role also sets `Scraper:UsePublicationPathArtifacts=true`,
`StartupInitializer` runs `PublicationPathArtifactReleaseGate` before
signalling ready, including before the rollout read-only early return, so no
publication-bound role starts against an unreleased database.

`ScraperOptions.SkipsStartupSchemaInitialization` and
`ScraperOptions.RequiresPublicationPathArtifactReleaseGate` are the single
source of truth for both decisions, so the schema-initialization branch and the
readiness gate cannot drift apart.

The gate is a single read-only query. It requires the current publication to
have a `generation_path_artifact_manifest` binding that is `ready`, at
`contractVersion` 1 and the current `manifestVersion`, whose row count equals
both the snapshot row count and the exact catalog song count, and whose
`content_hash` equals the canonical manifest hash recomputed from the same
function. A database with no current publication passes; a missing table or
hash function fails with the schema error text.

Failure throws `PublicationPathArtifactReleaseException` with the specific
mismatch and the operator instruction: start the API/schema-initializing role
first, then this role. The gate never executes DDL.

### Rollout ordering

1. Apply the release with the schema-initializing role. In the repository
   templates that is `fstservice` (`SkipStartupSchemaInitialization=false`),
   whose startup runs the `publication-path-artifacts` migration and rebinds
   active pointer snapshots to the current manifest version.
2. Confirm the service role is healthy and serving publication-bound reads.
3. Start `fstworker`, which sets `SkipStartupSchemaInitialization=true` and
   fails fast through the release readiness gate if step 1 has not been
   applied.

Reversing this order is safe but not useful: the worker refuses to start and
logs the exact remediation.

### Admin regeneration race gate

Immediate generation promotes mutable live rows directly, so it must never race
a staged promotion that owns a live compare-and-swap.
`POST /api/admin/regenerate-paths` therefore returns `409 Conflict` when:

1. `Scraper:UsePublicationPathArtifacts` is enabled, unconditionally; or
2. `Scraper:EnableScrapePassPathGeneration` is enabled; or
3. any working publication exists.

Rule 1 is the primary policy. In publication-bound mode the supported ways to
change path state are worker scrape-pass staging, guarded max-score
maintenance, and `POST /api/admin/path-generation/rearm` - never immediate live
promotion. The API role cannot observe whether a worker is staging right now,
and its own copy of `Scraper:EnableScrapePassPathGeneration` is not
authoritative for the worker role, so neither signal may be trusted to allow
the operation. Rule 1 also needs no publication pointer read.

Rules 2 and 3 remain as defense in depth for a disabled or misconfigured source
flag. The route, auth, and payload shape are unchanged.

## Limitations and rollback

- Only the **current** publication is bootstrapped. Prior publication history
  cannot be reconstructed from mutable live rows and is intentionally not
  fabricated.
- A candidate snapshot is captured at scrape allocation. Path generations
  performed after that allocation, other than scrape-pass staging applied
  before the read scope opens, are not reflected in that publication's snapshot
  until the next capture or a max-score maintenance refresh.
- Legacy API-owned `Scraper:EnableAutomaticPathGeneration` remains rejected at
  startup. The song catalog refresher no longer generates paths at all, so a
  configuration mistake cannot reintroduce out-of-band live promotion.
- Changed existing maxima are excluded by default and stay pending, marked
  review-required, for max-score maintenance review. Clearing that state is an
  explicit operator or successful-promotion action.
- Candidate cleanup already cascades from the failed publication generation.
  Generations attached to a candidate that later fails remain on disk for
  separate retention work; only never-attached rejected generations are
  reference-checked and removed by this phase.
- Supported production configuration enables both flags:
  `deploy/config/fstservice-role.env` sets
  `Scraper__UsePublicationPathArtifacts=true`, and
  `deploy/config/fstworker-role.env` sets that plus
  `Scraper__EnableScrapePassPathGeneration=true`, so the catalog is never left
  without a generator. The shipped option defaults stay `false` for generic
  safety.
- Rollback is a configuration change: set
  `Scraper__EnableScrapePassPathGeneration=false` to stop staging, and
  `Scraper__UsePublicationPathArtifacts=false` to return reads to live rows.
  Both take effect on restart. Reverting the source flag does not revert the
  manifest version: bindings stay at version 2 and remain valid, because the
  hash function is not downgraded. The table, bindings, and migration remain in
  place and stay harmless because nothing else reads them.
- A candidate that already carries staged promotions is failed and isolated
  as a whole when its live compare-and-swap does not match; the current
  publication and live rows keep their existing state.

## Related

- [Data publication flow](../architecture/data-publication-flow.md)
- [Data storage](../architecture/data-storage.md)
- [Path generation](../components/path-generation.md)
- [Configuration](../reference/configuration.md)
