---
status: canonical
owner: data
last_verified: 2026-08-23
last_verified_commit: 4c36926a
sources:
  - FSTService/Persistence/PublicationPathArtifactSchema.cs
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
  - FSTService/ScraperOptions.cs
update_triggers:
  - Publication path artifact schema, capture, binding, retention, read
    scoping, or source-flag behavior changes.
---

# Publication path artifact snapshots

`publication_path_artifacts` is a durable data family that binds path
generation state and CHOpt maxima to a specific publication generation, so
published reads never depend on the mutable live `songs` table.

This page describes Phase A only. Phase A is additive and feature-flagged:
there is **no** worker automatic path generation, **no** commit-time staged
promotion, and **no** automatic snapshot regeneration during a scrape.

## Ownership

| Concern | Owner |
|---|---|
| Table and canonical hash function | `PublicationPathArtifactSchema` |
| Startup migration and bootstrap backfill | `DatabaseInitializer` (`publication-path-artifacts` step) |
| Candidate capture and retention | `MetaDatabase.StartScrapeRun` |
| Binding re-emission at preparation | `MetaDatabase.PreparePublicationSurfaceBindings` |
| Snapshot refresh after max-score maintenance | `MetaDatabase.CompleteMaxScoreMaintenanceCore` |
| Effective publication reads | `PathDataStore` + `PathDataStorePublicationScope` |
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
    PRIMARY KEY (publication_id, song_id)
)
```

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

### Canonical manifest hash

`publication_path_artifact_manifest_sha256(publication_id)` returns the
deterministic SHA-256 of the snapshot: every column joined with `chr(31)` per
row, rows joined with `chr(30)` ordered by `song_id`, `NULL` normalized to the
empty string, and timestamps rendered as UTC ISO-8601 microseconds. The same
function computes the binding hash and the readiness source evidence hash, so
the two can never drift.

## Binding contract

The `path_artifacts` publication surface binding is emitted from the snapshot:

| Snapshot state | Binding kind | Status |
|---|---|---|
| Row count equals the bound exact catalog song count | `generation_path_artifact_manifest` | `ready` |
| Any other state (missing rows, missing/inexact catalog) | `legacy_live_unversioned` | `building` |

A ready binding JSON identifies `table`, `publicationId`, `scrapeId`,
`source`, `authoritative: true`, `contractVersion`, and `expectedRowCount`.
Recognized `source` values are `legacy_live_backfill`,
`generation_candidate_snapshot`, `generation_prepared_snapshot`, and
`max_score_maintenance_apply`.

An incomplete snapshot fails closed: it never reports `ready`, and it never
carries a content hash.

## Lifecycle

1. **Startup migration.** The `publication-path-artifacts` step creates the
   table and hash function under short lock/statement timeouts, backfills the
   **current publication only** from its exact `publication_song_catalog`
   joined to live `songs`, emits the `legacy_live_backfill` binding, and
   retires superseded snapshots. The backfill is skipped when the publication
   already has rows, so repeated startups do not rewrite it.
2. **Scrape allocation.** `StartScrapeRun` captures the complete candidate
   snapshot for the new working publication in the same transaction that
   captures the catalog, emits the `generation_candidate_snapshot` binding, and
   fails allocation if the snapshot is incomplete.
3. **Publication preparation.** Preparation re-emits the binding as
   `generation_prepared_snapshot`. It no longer overwrites the surface with the
   legacy live binding.
4. **Max-score maintenance.** The final same-publication apply/rollback
   transaction refreshes the current publication rows from the restored or
   promoted `songs` rows and recomputes the binding count and hash before the
   cache swap and unfreeze.
5. **Retention.** Only current, previous, and working publication snapshots are
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

## Phase A limitations and rollback

- Only the **current** publication is bootstrapped. Prior publication history
  cannot be reconstructed from mutable live rows and is intentionally not
  fabricated.
- A candidate snapshot is captured at scrape allocation. Path generations
  performed after that allocation are not reflected in that publication's
  snapshot until the next capture or a max-score maintenance refresh.
- Worker automatic path staging and generation do not exist yet.
  The legacy API-owned `Scraper:EnableAutomaticPathGeneration` mode promotes
  mutable live rows outside the publication pipeline, so Phase A rejects it at
  startup even when the source flag is enabled.
- Rollback is a configuration change: set
  `Scraper__UsePublicationPathArtifacts=false` and restart. Reads return to
  live rows immediately. The table, bindings, and migration remain in place and
  stay harmless because nothing else reads them.

## Related

- [Data publication flow](../architecture/data-publication-flow.md)
- [Data storage](../architecture/data-storage.md)
- [Path generation](../components/path-generation.md)
- [Configuration](../reference/configuration.md)
