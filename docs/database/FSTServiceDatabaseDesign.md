# FSTService PostgreSQL Database Design

**Authoritative runtime:** PostgreSQL 17 in `fst-postgres`  
**Production compose owner:** `/home/sfenton/Docker/FestivalServiceTracker`  
**Production data root:** `/mnt/docker-storage/Docker/FestivalServiceTracker/pg-data`  
**Last targeted live storage validation:** 2026-07-29 10:10 UTC

This document defines FST PostgreSQL ownership, source-of-truth boundaries,
publication behavior, retention, index posture, and restore paths. PostgreSQL
is the durable source of truth. DuckDB, Parquet, caches, projections, and
exports are rebuildable companions unless a later promoted design explicitly
changes that boundary.

## Non-negotiable invariants

1. Historical leaderboard rows remain attributable to Epic/source evidence,
   scrape ID, instrument or band scope, and publication state.
2. Public reads must resolve only data belonging to the published generation.
   An in-progress or failed scrape must not become visible through a cold cache
   miss, export, projection, ranking, or fallback query.
3. `scrape_publication_state` is the global publication ledger.
   `leaderboard_published_scope_source` records the validated physical source
   for every published solo scope; the global pointer and per-scope rows are
   consumed as one publication contract.
4. Staging, build, dirty, queue, and cache tables are never authoritative
   substitutes for a completed and published source.
5. All database data, backup, restore, archive, export, migration, repack, and
   scratch work stays on the 4 TB FST filesystem unless SFenton explicitly
   overrides that rule.
6. Destructive cleanup, index/table drop, rewrite, repack, archive pruning, or
   irreversible publication changes require backup/restore evidence and
   live-scrape old-versus-new data parity.
7. SQL is raw parameterized Npgsql. There is no ORM.

## Runtime ownership

| Responsibility | Current owner | Primary code |
|---|---|---|
| PostgreSQL connection pool | Shared service/worker process | `FSTService/Program.cs` |
| Startup schema creation | Explicit one-off initializer before a schema-changing deploy | `Persistence/DatabaseInitializer.cs`, `Persistence/StartupInitializer.cs` |
| Legacy/localized schema checks | Shared persistence classes; migration debt | `MetaDatabase`, `InstrumentDatabase`, projection/history builders |
| Scrape ledger and publication | Worker through `MetaDatabase` | `ScraperWorker`, `MetaDatabase` |
| Solo network/staging/snapshot writes | Worker | `ScrapeOrchestrator`, `LeaderboardSpoolWriterFactory`, `GlobalLeaderboardPersistence` |
| Band source/current writes | Worker | `BandSpoolWriterFactory`, `BandLeaderboardPersistence` |
| Solo and band current projections | Worker/post-process | `SoloCurrentProjectionBuilder`, `BandCurrentProjectionBuilder` |
| Rankings and history | Worker/post-process/background jobs | `RankingsCalculator`, `MetaDatabase`, `BandRankHistoryWorker` |
| Public API reads and exports | `fstservice` | API endpoint groups, `InstrumentDatabase`, `MetaDatabase`, `PlayerDataExportService` |
| Song catalog, shop, and path metadata | Service owns live catalog refresh; scrape allocation owns immutable catalog publication cuts | `FestivalPersistence`, `MetaDatabase`, `PathDataStore` |
| Durable notifications/improvement state | Shared persistence; service delivers publicly | `ImprovementNotificationService` |
| Retention planning and bounded cleanup | Service maintenance runner | `DatabaseMaintenanceDryRunReporter`, `DatabaseRetentionMaintenanceService` |

Production API-only and worker containers both skip routine startup schema
initialization. Schema-changing releases run an explicit one-off initializer
while the worker is held, then deploy the role containers. PG-6/SERVICE-4
replace this monolithic path and localized `Ensure*Schema` calls with a
versioned migration ledger, advisory lock, and bounded lock/statement
timeouts.

The current one-off command is:

```bash
docker compose run --rm --no-deps fstservice \
  --initialize-schema-only
```

It applies idempotent schema only and exits without precompute, scrape,
rankings, notifications, or path generation.

WORKER-0A live windows use
`tools/postgres-worker-correctness-monitor.sh --scrape-id <id> --output
<same-drive-path>` for 60-second public-health/resource/status samples and
`tools/postgres-scrape-evidence.sh` for pre/post manifests, mappings, phase
outcomes, writer failures, routes, WAL/temp, locks, and relation sizes.

## Live shape snapshot

The 2026-07-10 inventory found 269 public tables/partitions, 735 public indexes,
and 273 public constraints. Values below are operational evidence, not fixed
limits.

| Surface | Live size |
|---|---:|
| Database | 3,389,362,312,883 bytes after compact Trios promotion |
| Solo physical snapshot partitions | 1,788.63 GB |
| Band rank-history v2 point partitions | 857.72 GB |
| Solo rank-history partitions | 174.47 GB |
| Current band leaderboard partitions | 139.04 GB |
| Composite rank history | 90.78 GB |
| Logical version partitions | 144 KB after LOGICAL-RETIRE |
| `band_member_stats` | 59.79 GB |
| Solo published/current projection partitions | 45.18 GB |
| `band_members` | 44.55 GB |
| Legacy mutable solo leaderboard partitions | 40,825,225,216 bytes |
| Logical current partitions | 144 KB after LOGICAL-RETIRE |
| Band source-entry partitions | 25.37 GB |
| Band rank-history v2 latest partitions | 48 KB after ORPHAN-RECLAIM |
| `rank_history_latest` | 16 KB after ORPHAN-RECLAIM |
| `player_score_observations` | 24 KB after OBSERVATION-RETIRE |
| `scrape_dirty_*` | 64 KB after ORPHAN-RECLAIM |

The PG-1 decision sample had `247.2GB` free on `/mnt/docker-storage`, published
scrape `1230`, public reads unfrozen, `6,129` complete published-source rows,
and no ungranted locks. The per-scope map occupied `4.63MB`.

After scrape `1236`, PG-3 retired the redundant non-constraint
`ix_crh_latest` index from `composite_rank_history`. Database size fell from
`3,784,729,548,467` to `3,763,839,399,603` bytes, and filesystem free space
rose from `78,549,483,520` to `99,439,702,016` bytes. The exact index DDL is
retained as rollback evidence; no table rows, constraints, publication state,
or history data changed.

During the 2026-07-15 disk-pressure incident, scrape `1261` was stopped before
rankings/publication and failed closed while published scrape `1236` remained
authoritative. PG-3 then retired the non-constraint partitioned
`ix_btrhlv2_snapshot` latest-state snapshot lookup family. Database size fell
from `3,851,429,820,083` to `3,848,151,824,051` bytes and filesystem free space
rose from `26,942,255,104` to `30,220,271,616` bytes. Latest-state rows,
primary-key conflict semantics, publication state, and public route/export
responses remained unchanged.

A second one-family decision retired the non-constraint partitioned
`ix_btrhpv2_snapshot` points snapshot lookup family. Database size fell from
`3,848,151,824,051` to `3,839,287,383,731` bytes and filesystem free space
rose from `30,962,761,728` to `39,827,243,008` bytes. Public history retained
the team/date indexes, and no history rows, primary keys, publication state,
or route/export output changed.

The residual capacity phase replaced the 23,526,973,440-byte composite
retention btree with the 688,128-byte
`ix_crh_retention_cutoff_brin`. Database size fell to
`3,815,761,122,995` bytes and measured filesystem free space reached
`63,339,065,344` bytes. The exact scrape-completion guard now has
`18,190,839,808` bytes of margin. No history rows, constraints, publication
state, or public response changed.

After rejected scrape `1262`, PG-3 retired the non-constraint partitioned
`ix_rh_latest` family from solo rank history. `SnapshotRankHistory` now finds
each account's latest date through the retained primary key, reducing the
no-index full-plan cost from `8,314,062.24` to `1,212,279.07`. Database size
fell from `3,842,540,050,099` to `3,796,992,710,323` bytes and measured
filesystem free space reached `76,804,927,488` bytes, leaving
`31,656,701,952` bytes above the scrape-completion boundary. Exact recreate
DDL builds nine child indexes concurrently and attaches them to a partitioned
parent. No history row, constraint, publication state, route, export, or
retention result changed.

After scrape `1263` failed on capacity, PG-3 retired `33` non-constraint
indexes across six owner-proven families: duplicate/non-owning band ranking
indexes, obsolete dirty-work lookups, unowned band appearance sorts, orphan
latest-snapshot lookups, observation-table read indexes with no production
reader, and duplicate/deprecated composite ranking indexes. Database size
fell from `3,846,380,738,227` to `3,829,206,537,907` bytes and measured free
space reached `48,546,029,568` bytes, `3,397,804,032` bytes above the measured
scrape boundary. Exact recreate SQL is retained. Primary/unique indexes,
published `1236`, failed-candidate route isolation, rows, and constraints were
unchanged.

The 2026-07-25 LOGICAL-RETIRE inventory measured the disabled experimental
logical shadow exactly. After its secondary-index retirement, the final
pre-truncate state was `39,820,273` current rows / `26,674,814,976` bytes and
`194,171,215` version rows / `96,499,073,024` bytes. Published scrape `1267`
cleared the disabled-writer parity gate. On 2026-07-28 LOGICAL-RETIRE
transactionally truncated the two partitioned parents without `CASCADE`.
Their 18 leaves now contain zero rows and occupy `294,912` bytes combined;
all 20 primary-key constraints and indexes remain valid. The retained
`leaderboard_logical_write_metrics` table still has `108` rows and occupies
`106,496` bytes.

On 2026-07-27 POST-1265-LOW-SCRATCH retired only the logical shadow's four
non-constraint secondary index trees: `ix_lce_scope_rank`,
`ix_lce_last_changed`, `ix_lev_open_versions`, and `ix_lev_from_scrape`.
Their 36 child indexes reclaimed `18,289,049,600` database bytes. The
`39,820,273` current rows, `194,171,215` version rows, 20 primary-key
constraints, table heaps, metrics, and publication state were unchanged.
`DatabaseInitializer` no longer recreates these dormant indexes; exact
concurrent recreate/attach SQL is retained in the phase evidence.

Scrape `1266` exposed a separate live concurrency defect: composite rank
snapshotting and Band Duets ranking rebuild both entered
`EnsureBandRankHistoryTables` concurrently and deadlocked on the same
`ALTER TABLE ... ADD COLUMN IF NOT EXISTS`. The ensure path now takes a
transaction-scoped Postgres advisory lock before any idempotent band-rank
history DDL, and the ranking caller retries one `40P01` deadlock. This preserves
the existing schema and write modes while serializing only the localized schema
ensure. The same-run Duets repair rebuilt `4,477,133` current ranking rows
before the failed candidate was abandoned without publication.

ORPHAN-RECLAIM then removed only exact owner-proven obsolete or derived state.
Nine Tier 1 schemas were truncated and the dated
`notification_cleanup_audit_20260509` table was dropped without `CASCADE`,
reclaiming `10,027,671,552` database bytes. A separate Tier 2 transaction
truncated `band_team_rank_history_latest_v2` and `rank_history_latest`,
reclaiming `18,553,454,592` database bytes. All retained constraints remained
valid, `13/13` normalized public fingerprints matched, and final filesystem
free space reached `64,001,667,072` bytes. Exact evidence and rebuild limits
are in `docs/database/OrphanReclaimRunbook.md`.

Scrape `1267` supplied the missing disabled-logical-writer publication proof.
It completed `8,232/8,232` manifests and all 10 publication-critical phases,
then atomically published/unfroze `1267`. The publication owns `6,174`
complete source mappings and `39,937,029` physical rows. Two independent
post-publish captures returned HTTP `200` and matched `13/13` normalized
leaderboard, export, player, ranking, history, composite, band, and band-song
fingerprints.

Full logical current/version fingerprints remained byte-exact for
`39,820,273` and `194,171,215` rows. Scrape `1267` touched zero logical rows,
emitted zero logical metrics, and produced no positive logical read-counter
delta. The logical-shadow destructive parity gate therefore cleared.
LOGICAL-RETIRE then truncated the two logical parents and all 18 leaves on
2026-07-28, reclaiming `123,173,593,088` database bytes while retaining empty
schemas, 20 primary keys, and the metrics table.

The 2026-07-25 SOLO-DYNAMIC-AB inventory measured the active solo current
projection at `39,601,283` rows / `46,633,459,712` bytes:
`17,821,523,968` heap and `28,806,701,056` indexes. The accepted replacement
research candidate is not a dynamic read cutover. It retains a complete,
partitioned, generation-guarded row set and the account/rank/score indexes,
removes the structural-only primary key and unowned per-row `computed_at`,
and adds a bounded publication-hot tier. Conservative projected steady size is
no more than `20,215,010,912` bytes. No production schema changed.

The 2026-07-28 BAND-HISTORY-COMPACT phase refreshed the exact frozen v2 point
inventory to `917,793,219` rows / `848,759,203,840` bytes:
`154,235,944,960` Duets, `305,843,961,856` Trios, and
`388,775,297,024` Quad. Production history writes remain disabled and the API
still reads v2 narrow points. A bounded `4,651,508`-row Duets pilot proved
zero bidirectional row differences with integer team/scope/combo IDs and
`BYTEA(16)` fingerprints. Its compact heap plus primary key used
`251.98` bytes/row versus `716.93` current Duets bytes/row, and the compact
pilot key served the public lookup without the duplicated full-width
secondary tree.

The lower-scratch continuation proved that `902,775,955,523` was the generic
seven-day policy reserve plus candidate size rather than a measured physical
peak. Duets v3 was built in six committed date chunks with deferred local
indexes and explicit checkpoints. Its four monthly leaves and dictionaries
occupy `52,134,436,864` bytes. Exact counts/ranges/groups, monthly checksums,
a deterministic exact team sample, and repeated `9/9` HTTP payload parity
passed.

Production now enables the independent compact v3 flags for Duets and Trios;
only Quad remains on v2 narrow points. The retired Duets leaf was detached
with a proven 11.827 ms reattach path and then dropped without `CASCADE`. The
Duets source drop released `154,235,944,960` bytes and its net database
reduction is `102,101,475,328` bytes. Date
deletion and Parquet-as-live-source remain rejected because the API/export
still serve all history and no runtime rehydration tier exists. Details are in
`docs/database/BandHistoryCompactionRunbook.md`.

The first full Trios v3 build attempt was not promoted. It stopped at
`335,757,940 / 343,275,419` rows and `49 / 51` dates, had no point indexes,
and retained a `building` state row with no validation or promotion timestamp.
No code, deployed binary, API route, database dependency, or runtime writer
referenced it; Trios reads continued through v2. The exact
`73,478,529,024`-byte candidate was therefore dropped without `CASCADE` after
a rollback rehearsal, `13/13` public parity, and explicit scrape-boundary
coordination. Duets compact v3 and the Trios/Quad v2 sources were unchanged.

A clean 2026-07-29 rebuild has all `343,275,419` Trios rows, all 51 dates,
four exact monthly multiset-hash matches, and a valid partitioned unique index
family. Production enables the independently reversible
`CompactV3TriosReadEnabled` flag. The service A/B, readiness promotion,
detach/reattach rollback, and source drop passed; Trios v2 no longer exists.

A clean 2026-07-30 Quad continuation has all `359,383,226` rows and all 52
dates, exact monthly full-row multiset hashes, and a valid partitioned unique
index family. Production enables the independently reversible
`CompactV3QuadReadEnabled` flag. The service A/B, readiness promotion,
detach/reattach rollback, and source drop passed; Quad v2 no longer exists.
The `388,779,032,576`-byte source was replaced by
`87,994,753,024` compact bytes, a `300,784,279,552`-byte net database
reduction.

## Data ownership and restore class

| Class | Meaning | Restore rule |
|---|---|---|
| Durable source | Required to reconstruct historical or current truth | Restore from verified backup or source manifest before service promotion |
| Publication ledger | Determines which durable/projection rows are public | Restore atomically with published generation; never infer from newest active data |
| Derived projection | Rebuildable from durable source for a named generation | Prefer deterministic rebuild; restore backup only when rebuild evidence is unavailable |
| Cache | Regenerable response acceleration | Clear and rebuild after source/publication restore |
| Work state | Queue, staging, dirty, or resumability state | Replay or discard only according to owning operation semantics |
| Audit/artifact | Diagnostic, parity, or operational evidence | Retain per documented policy; never use as public source implicitly |

## Table-family design

### Scrape, publication, and operational ledger

| Tables | Class | Writer | Readers | Publication/retention |
|---|---|---|---|---|
| `scrape_log` | Durable source | Worker via `MetaDatabase` | Service status, maintenance, evidence tooling | One row per scrape with durable `running`/`completed`/`failed` state; failed rows are never publishable |
| `scrape_publication_state` | Publication ledger | Worker publish/freeze transaction | Public read resolvers, service status, notifications | Single row; preserve through every restore. Publication atomically queues the published scrape and its exact bounded notification projection scope plan. Interrupted attempts reuse that plan, the worker holds before another scrape, and the publication transaction refuses to replace an incomplete marker. |
| `leaderboard_published_scope_source` | Durable per-scope publication ledger | Worker coverage/build/publish path | Service current-state readers and solo exports behind rollback flag | One validated snapshot or explicit empty source per published `(song_id, instrument, scope_kind)` |
| `leaderboard_scope_manifests` | Durable candidate-integrity ledger | Solo and band page fetchers through worker persistence | Publication gate, replay/evidence tooling | One row per scrape and expected scope with exact page outcomes, boundaries, deep range, and fingerprints |
| `scrape_writer_failures` | Durable failure/replay ledger | Solo, band, and online writer drains | Worker status and replay/evidence tooling | Exact failed scopes/pages/rows plus same-drive artifact path; retained with failed scrape |
| `scrape_phase_outcomes` | Durable publication-decision ledger | Post-scrape phase runner | Publication guard, service status, evidence tooling | Explicit `publication_critical` or `best_effort` result per selected phase |
| `scrape_phase_timings` | Audit/artifact | Worker | Evidence and maintenance tooling | Bounded metadata retention |
| `service_worker_status` | Durable operational state | Worker heartbeat/operation publisher | Service status/readiness | Keep current/last operation; stale state must be visible |
| `instrument_scrape_state` | Durable work state | Worker | Resume/status logic | Retain latest per instrument |
| `data_version` | Schema metadata | Startup initializer | Startup/schema logic | Replaced by versioned migration ledger in PG-6 |
| `api_request_telemetry` | Audit/artifact | API instrumentation where enabled | Diagnostics | Bounded retention; no secrets or unbounded cardinality |

When `Features:WritePublishedScopeSources` is enabled, the worker records
reported coverage for every expected solo scope, validates exact physical row
counts, and builds the complete per-scope candidate before marking the scrape
complete. `PublishScrapeRun` then validates the expected mapping count, marks
matching fingerprints published, publishes current band ranking tables, swaps
staged API cache rows, advances
`scrape_publication_state.published_scrape_id`, queues the same scrape in the
improvement-notification marker when enabled, stores the exact bounded
projection workset, and clears the public-read freeze in one transaction.
Long band ranking copies and index builds run before the cache swap so the
`api_response_cache` `ACCESS EXCLUSIVE` lock is retained only for the final
truncate/insert and commit work. Before publication, the transaction locks the
existing publication row and rejects a newer publication unless the current
published scrape's notification marker is `completed` or `disabled` (or no
marker exists on a legacy row).

| Rollout switch | Container | Default | Effect | Rollback |
|---|---|---:|---|---|
| `Features__WritePublishedScopeSources` | `fstworker` | `false` | Backfills the current clean publication when needed, records scope coverage, builds the next mapping, and requires it in publication | Set `false`; incomplete candidates never move the global pointer |
| `Features__SkipUnchangedPhysicalLeaderboardSnapshots` | `fstworker` | `false` | With strict manifests/fingerprints and published-source writes enabled, reuses only exact validated published physical sources for unchanged scopes | Set `false`; all non-empty scopes write current-scrape physical rows |
| `Features__UsePublishedScopeSources` | `fstservice` | `false` | Resolves solo current reads, projection readiness, totals, member filters, and published solo exports from the current mapping | Set `false`; active-state/legacy resolver remains available |
| `Features__UseStoredSoloProjectionRanksForFilteredReads` | service or worker reader | `false` | Uses stored projection rank plus exact removed-above counts for filtered leaderboard/player ranks instead of full window re-sorts | Set `false`; prior score/tie window SQL remains |
| `Features__EnforceScopeCompletenessManifests` | `fstworker` | `false` | Requires every expected solo and band scope to have a complete page manifest before publication | Set `false`; manifests remain available as observe-only evidence |
| `Features__RequireSuccessfulScrapeWriters` | `fstworker` | `false` | Rejects a candidate when any disk-spool or bounded-online writer reports failed pages/rows | Set `false`; durable failure rows and replay artifacts remain |
| `Features__EnforcePublicationCriticalPhases` | `fstworker` | `false` | Rejects a candidate after any explicitly publication-critical post-scrape phase failure | Set `false`; phase outcomes remain visible while legacy swallow behavior is restored |
| `Features__EnablePublicationReadContext` | `fstservice` | `false` | Enables publication bootstrap, pinned HTTP/WebSocket requests, shared read leases, and `409 publication_changed` enforcement after every public surface is generation-addressable | Keep `false` until all required surface bindings are ready; additive ledger/pointer rows remain |
| `Features__WriteLogicalLeaderboardVersions` | all roles | `false` and startup-rejected when true | Retired shadow writer; no service/API reader exists | Future enablement requires a versioned migration, rebuild/restore validation, and a new live-scrape promotion |

### Song, account, registration, and authentication metadata

| Tables | Class | Owner/callers | Retention and safety |
|---|---|---|---|
| `songs`, `item_shop_tracks`, `season_windows`, `song_first_seen_season` | Durable source/metadata | `FestivalPersistence`, `MetaDatabase`, path/ranking readers | Keep provider IDs/timestamps and source provenance. `season_windows.source_kind` ranks `event_api` above legacy/probe/synthetic rows so conventional IDs cannot replace authoritative event windows |
| `songs.provider_json`, `live_song_catalog` | Exact provider restart rows and canonical live singleton | `FestivalPersistence.SaveSongsVersionedAsync` | Provider-known and extension fields, schema/version, SHA-256, count, source kind, exactness, and capture time; excludes `imagePath`, `isSelected`, and `isInLocalData` |
| `publication_song_catalog` | Immutable generation snapshot | `MetaDatabase.StartScrapeRun`, publication retention | One row per publication; ready only when its exact version/hash token matches the catalog selected by the scrape; retained only for current, previous, and working pointers |
| `account_names` | Durable source/cache of Epic identity | Worker resolver; API/search readers | Refreshable, but historical account IDs remain stable |
| `registered_users`, `registered_bands` | Durable source | API activity/registration and worker consumers | Activity-based retention must preserve idempotent claims |
| `registered_user_refresh_scope_progress` | Durable recurring-refresh work state | `PostScrapeOrchestrator`, `CyclicalSongMachine`, `MetaDatabase` | One latest successful checkpoint per `(song_id, instrument)` with `status`, `checked_at`, nullable positive `scrape_id`, and explicit `scrape`/`phase_only` provenance; the small partial `checked_at` index supports fairness/coverage reads |
| `registered_band_processing_status`, `registered_band_processing_progress`, `registered_player_band_discovery_progress` | Durable work state | Registration/backfill workers | Resume/idempotency state includes exact seasonal `window_id`; a changed/noncanonical ID invalidates the prior season checkpoint without changing the compact primary key |
| `backfill_status`, `backfill_progress`, `history_recon_status`, `history_recon_progress`, `deep_scrape_queue` | Durable work state | Worker queues/orchestrators | Preserve failed/incomplete work for replay. History status/progress is bound to a reconstruction version and exact season-window fingerprint |
| `user_sessions`, `epic_user_tokens` | Security-sensitive durable state | Authentication subsystem | Never include values in logs, reports, fixtures, or exports; restore with access controls |

#### Atomic CHOpt path generations

`PathGenerationCoordinator` is the only runtime owner for automatic, admin,
and worker/startup path generation. It derives the expected instrument set from
raw chart-property presence (`gr`, `ba`, `ds`, `vl`, `pg`, `pb`); a present
property with difficulty `0` is charted, while an absent property is never
invoked. `Scraper:EnablePathGeneration` permits explicit bounded generation,
while `Scraper:EnableAutomaticPathGeneration` controls background generation.
Automatic work includes only new songs and changed songs with an authoritative
non-null provider modification timestamp that already own an atomic generation.
New-song eligibility is the durable `songs.path_generation_pending` flag set
in the same PostgreSQL transaction as exact catalog persistence, not missing
path metadata, so startup cannot reinterpret an incomplete legacy database as
a full-catalog migration. Existing legacy rows default false. New rows are
pending; changed rows become pending only when they already own an atomic
generation and the incoming provider modification timestamp is non-empty.
Promotion clears the flag in the same CAS update, so missing MIDI,
cancellation, configuration failure, CHOpt failure, CAS conflict, and service
restart remain retryable. Rows with legacy path metadata are never
migrated implicitly. The admin
endpoint requires one exact `songId`; full-catalog
legacy regeneration is deliberately unavailable through that route.

CHOpt writes only beneath the configured
`DataDirectory/.path-work/<attempt-id>/` staging directory. A candidate is
complete only when every expected instrument has successful easy, medium,
hard, and expert invocations. PNG validation checks chunk bounds, chunk CRCs,
bounded non-interlaced IHDR dimensions/format, non-empty consecutive IDAT,
zlib decompression, exact scanline sizes/filter bytes, terminal IEND, and exact
end-of-file.
JSON validation requires the complete web path-data root contract, typed
notes/activations, and a positive expert `totalScore`. The coordinator detects
the bounded runtime `CHOpt --version` value and binary SHA-256 before
generation. `Scraper:PathGenerationProfile` versions the argument and artifact
contract.

Validated artifacts move on the same filesystem to the immutable layout:

```text
DataDirectory/paths/<song-id>/generations/<generation-id>/
  generation.json
  <instrument>/<difficulty>.png
  <instrument>/<difficulty>.json
```

Only after that move does one short row-locked/CAS transaction update all of
the following together:

| `songs` field | Meaning |
|---|---|
| `path_artifact_generation_id` | Reachable immutable artifact generation |
| `path_expected_instruments` | Canonical raw-property-derived expected set |
| `path_generation_revision` | Per-song CAS revision |
| `path_generation_pending` | Durable automatic new/changed-song queue; cleared only by successful promotion |
| six `max_*_score` fields | Positive expert maxima for the expected set; unsupported instruments remain null |
| `dat_file_hash`, `song_last_modified` | Exact candidate inputs |
| `paths_generated_at` | Successful promotion time |
| `chopt_version`, `chopt_binary_sha256` | Actual runtime identity |
| `path_generation_profile` | CHOpt argument/artifact contract identity |

Promotion compares both `path_generation_revision` and the exact normalized
provider `songs.last_modified` value captured by the request. A catalog update
that commits before the row lock therefore rejects the older CHOpt result and
leaves `path_generation_pending=true`; an update waiting behind promotion
requeues the changed atomic row afterward. A stale result cannot clear newer
catalog work.

The schema also installs
`trg_reject_incoherent_legacy_path_write`. Once a row owns an atomic revision
or generation pointer, an old/mixed-version writer cannot change any path
maxima or identity field without advancing the revision. This converts an
otherwise silent mixed-generation overwrite into a visible SQLSTATE `55000`
failure. Rollback therefore disables path generation first; if a generation
was promoted, restore the affected song row from its pre-deploy snapshot
before deploying a binary that only understands the legacy artifact layout.

`path_generation_errors` is append-only and deliberately has no secondary
index. It records bounded detail plus attempt, song, known DAT/runtime
identity, expected set, stage, instrument, difficulty, and timestamp. Failed
validation, cancellation, move, database write, or CAS leaves the old pointer,
maxima, hashes, timestamps, and runtime identity unchanged. A database failure
after the immutable move may leave an orphan directory, but it is unreachable
and safe for a later separately approved retention pass.

Path image, path JSON, and `/api/songs` metadata resolve the same database
generation pointer. The web client supplies `pathArtifactGenerationId` to both
artifact requests, so URL caching is generation-specific and a pointer change
is rejected instead of mixing image and JSON generations. The songs cache uses
an invalidation plus public-read safety token: a response build that began
before path promotion, freeze/unfreeze, failed-candidate isolation, or
publication change cannot install or serve its stale generation metadata.
The coordinator opens the songs-cache content-mutation epoch before entering
the database promotion transaction and closes it only after the transaction
returns, eliminating the commit-to-invalidation race.
`PathDataStore` applies the same revision fence to its five-minute max-score
cache, so a pre-promotion PostgreSQL read cannot reinstall old maxima or a stale
generation ID after promotion.
Blocked installs return through the stable-cache/fail-closed gate instead of
serving candidate bytes; a cold miss during any freeze returns bounded
no-store HTTP `503` rather than rebuilding in a loop. An open text
path modal also treats a generation ID change as a new target and returns to
its loading phase. Rows with a null pointer retain the legacy
`paths/<song-id>/<instrument>/<difficulty>.*` read layout; rows with a non-null
pointer never fall back to legacy or stale files. Promotion does not alter
scrape IDs, publication pointers, public-read freeze state, rankings, history,
or notification delivery. The additive columns and error table are
idempotent; deployment still follows the explicit schema-initializer hold
described above, with normal lock/long-query checks before the initializer.
Deploy first with automatic generation disabled, prove legacy reads and the
single-song admin guard, then enable automatic new/changed atomic-song work.

The exact-four Pro Lead repair is an explicit one-shot extension of this path,
not another generator implementation. All repair, automatic, admin, and worker
path work shares PostgreSQL advisory lease
`5067481511116519000`, below both the fixed publication lock and the unbounded
per-publication cache-build key range; a repair promotion or ranking rebuild
also holds the publication advisory lock so scrape allocation/publication
cannot overlap. Lease acquisition uses `pg_try_advisory_lock` and fails closed
rather than waiting behind another owner.

`--path-repair-stage-exact-four` requires automatic generation disabled and an
explicit new `.json` output below `DataDirectory`. Existing paths, symbolic
links, and path escapes are rejected. The command loads exactly the four
compile-time-approved IDs in ordinal order, captures their current revision,
exact catalog `last_modified`, all-six-maxima-null identity, and
pending/pointer state, then invokes the coordinator serially for Pro Lead only.
Provider and database catalog timestamps are canonicalized to the same UTC
instant before identity comparison, so harmless ISO-8601 fractional precision
differences do not block repair while malformed or different timestamps remain
fail-closed.
The normal decrypt/CHOpt/runtime and all-difficulty validation path moves each
successful generation from `.path-work` into immutable same-filesystem
storage. The selective generation pointer serves Pro Lead while other
instruments retain legacy artifact fallback. Stage-only never calls the path
CAS and never changes maxima, hashes, timestamps, pointers, revision, or
pending state. It re-reads all four source identities before atomically
creating the strict notification maintenance manifest; any CHOpt or identity
failure leaves no manifest and appends the normal visible path error evidence.

`--path-repair-promote-exact-four` binds that strict manifest to an explicitly
expected current published scrape. It requires no working publication and
preflights all four current database rows, published exact catalog timestamps,
strict `generation.json` identities, non-symbolic-link PNG/JSON files, runtime
identity, every expected instrument/difficulty, and reconstructed expert
maxima before the first write. A new rollback snapshot below `DataDirectory`
captures all six maxima, revision, pointer, DAT/catalog identities, generation
timestamp/runtime/profile, expected instruments, and pending state before
promotion. It then establishes the purpose-owned public-read freeze before the
first row-locked CAS, which is called exactly once per song in ordinal order.
This is deliberately serial rather than falsely all-four atomic: a later
failure stops immediately, keeps public reads failed closed, and reports the
exact promoted, failed, and not-attempted subset while preserving the rollback
snapshot.

`--path-repair-rebuild-rankings` validates the same manifest in its
post-promotion state, requires the same idle current publication and existing
purpose-owned freeze, and recomputes only Pro Lead plus the dependent
composite, solo-family, and combo rankings from that publication's immutable
catalog. It does not rebuild unrelated solo instruments or bands, allocate or
publish a scrape, run notification detection, write scrape phase timings, or
append rank-history snapshots. Failure or cancellation retains the freeze.
Only a fully validated success releases it; the API then discards pre-freeze
process-cache entries and broadcasts a same-publication refresh so connected
web clients clear React Query and songs caches before reading the repaired
state.

Failed-candidate `/api/songs` recovery is publication-owned rather than a live
candidate bypass. When the process cache is empty, failed-candidate isolation
may build a no-store response from `publication_song_catalog` for the current
published scrape only when automatic path generation is disabled. The bound
catalog's publication ID, row count, and content hash must match the current
binding. A schema-v2 provider-exact ready binding is preferred; the current
legacy-reconstructed building binding is accepted only for this fallback.
Richer live provider metadata such as album art is merged only when the
published and live catalogs contain the same unique song IDs and every
normalized provider `lastModified` timestamp matches. A mismatch preserves the
sparse published catalog instead of crossing generations.
Other publication-bound routes remain under their existing
stable-cache/published resolver or HTTP `503` behavior.

The publication-critical registered-user refresh contains only recurring
all-time/current-season `PostScrape` work. Registration backfill and history
reconstruction remain on the resumable registration/deferred workers and keep
their profiles in no-store `202` state until a later ranked publication. The
solo refresh has no absolute wall-clock timeout; the progress-aware worker
watchdog owns true hangs without cancelling a phase that is still advancing.
Continuous workers drain pending/deferred backfills before history-only work.
A successful run-once scrape performs the same durable drain only after the
new publication and notification gate completes; a failed run-once scrape
leaves the queues untouched for the next worker.

Recurring refresh fairness is scope-based because every registered account is
batched together for a song/instrument. Before each pass,
`MetaDatabase.GetRegisteredUserRefreshSongOrder` keeps the complete current
charted-song set but orders songs with missing scope checkpoints first, then
least-recently checked coverage. `CyclicalSongMachine` preserves that preferred
order while tracking completion by song ID so a concurrent attachment or
loop-back cycle cannot corrupt progress when ordering changes.

`registered_user_refresh_scope_progress` is updated incrementally through the
PostScrape attachment callback, not from the final machine result. A
song/instrument checkpoint is eligible only after every required all-time and
current-season batch succeeds. Successful empty and recognized Epic
`event_not_found` responses are complete; transport failures, unexpected API
statuses, invalid payloads, missing required season windows, cancellation, and
other incomplete lookups do not advance that scope. Already completed scopes
remain durable if a later scope, timeout, process exit, or cancellation ends
the attachment. The newest `checked_at` wins; a tie prefers positive scrape
provenance. Full scrapes store their positive scrape ID. Supported phase-only
`SoloRefreshUsers` runs store `scrape_id = NULL` with
`provenance = 'phase_only'` instead of inventing a scrape identity.

PostScrape chooses one authoritative current season as the maximum of the
discovered/persisted season windows and the instrument-observation fallback,
then passes that exact value in its attachment options. The cyclical machine
merges supplied windows and uses the declared value for core clamping.
Each nonblank discovered `SeasonWindowInfo.WindowId` is the authoritative
lookup ID; only synthetic blank-window rows fall back to the conventional
`seasonNNN` prefix. A rollover window `N` therefore cannot be replaced by an
instrument maximum of `N-1` or a reconstructed prefix; the exact discovered
season `N` lookup must succeed before the scope callback can checkpoint.

The enrichment branch resolves/persists authoritative windows before
FirstSeenSeason probes and passes those rows directly to
`FirstSeenSeasonCalculator`. Calculation version `4` invalidates questionable
version `3` rows. It advances only after a fresh `event_api` discovery and
conclusive probes: HTTP 400 is a confirmed missing leaderboard, while
transport/auth/5xx failures leave the older version retryable. Registered-band
targeted processing and registered-player band discovery carry the same exact
ID through lookup intents and durable progress; legacy blank progress resolves
to its conventional ID, while a newly discovered noncanonical ID forces a
recheck.

Each cyclical pass snapshots its attachment/window set and binds the active
core pass to `(current season, exact lookup ID)`. A late PostScrape attachment
whose requested fingerprint differs is not admitted to remaining songs in
that pass; it retains missing song IDs and runs in the next matching cycle.
This prevents an all-time-only result from checkpointing a scope that requested
a different current-season window.

The legacy `HistoryReconstructor.ReconstructAccountAsync` path also resolves
blank synthetic windows through `GetSeasonLookupId`. Missing windows or failed
required seasonal calls leave the song/instrument unprocessed and the account
history status in error for retry; partial required coverage is never marked
complete. The batched `SongProcessingMachine` path uses strict lookup failure
propagation for history users and marks a pair only after every required
seasonal lookup succeeds. Version `2` plus the SHA-256 exact-window fingerprint
invalidates version `1` completed status/progress and prevents a changed window
map from reusing stale pair completion.

Multi-season history users do not mark progress in the fast core pass. Their
full season set, including the current season, runs coherently in the history
pass; a failed required lookup leaves the song missing so the attachment
retries instead of completing. Historical admission checks the same current
window and full-window fingerprint as the cycle snapshot.

Backfill and history resume sets are independent on each work item:
`BackfillAlreadyChecked` suppresses only all-time backfill calls, while
`HistoryAlreadyProcessed` suppresses only version/fingerprint-matched seasonal
history. Versioned status, counter, failure, pair-upsert, and completion writes
all require the active identity, with the progress upsert locking that status
row. Late work from fingerprint F1 therefore cannot overwrite an activated F2.
Coverage gates enumerate the exact current catalog song/instrument pairs:
obsolete removed-song rows are ignored, while any missing current pair blocks
backfill or history completion. The history-only drain applies that same
coverage test before its completed-status fast path; adding or reintroducing a
charted song therefore reopens an otherwise version/window-matched account.

History/backfill durability adds a monotonic admission revision to that
identity. Staged seasonal score-history rows and history pair progress promote
in one PostgreSQL transaction only while `(version, fingerprint, revision)` is
still active; cancellation/discard removes both buffers. Backfill completion
separately requires the exact current charted-song × nine-instrument all-time
pair set, so historical success cannot hide a failed core lookup.

Authoritative FirstSeen floors suppress pre-release seasonal calls in the
batched machine. Legacy reconstruction no longer treats season 0/1 entries as
already complete: it queries from authoritative FirstSeen through the highest
current window, records later lower-score sessions, and leaves pairs without
authoritative FirstSeen metadata pending.

`song_first_seen_season` rows bind calculation version to the authoritative
window fingerprint and maximum season. A null/not-found result is reopened
whenever either changes, preventing an older terminal miss from surviving a
new season window. Null/not-found rows also remain retryable within the same
binding so a newly released catalog song does not wait for another rollover.

The worker logs bounded before/after coverage over only the current charted
songs and nine solo instruments: expected scopes, checked scopes, missing
scopes, oldest checked timestamp/age, and rows completed by the current scrape.
There is currently no pass cap; fairness changes ordering only. Registration
backfill/history progress and solo-projection dirty-scope persistence remain
separate contracts. Rollback retains the additive table, index, provenance
rows, and the fixed nullable-provenance writer; orchestration ordering can be
disabled without deleting checkpoint evidence. Do not restore the predecessor
writer that rejects phase-only execution while that supported mode remains.

SERVICE-1/WORKER-5 consolidate competing registration consumers, catalog
ownership, and token refresh ownership.

### Solo leaderboard source, snapshot, and current state

All instrument-partitioned families use these nine keys:
`Solo_Guitar`, `Solo_Bass`, `Solo_Drums`, `Solo_Vocals`,
`Solo_PeripheralGuitar`, `Solo_PeripheralBass`,
`Solo_PeripheralVocals`, `Solo_PeripheralCymbals`, and
`Solo_PeripheralDrums`.

| Tables | Class | Write path | Read path / semantics |
|---|---|---|---|
| `leaderboard_staging`, `leaderboard_staging_meta`, `leaderboard_staging_v2` | Work state | Bounded/COPY writer | Never public; truncate/replay only after operation proof |
| `leaderboard_entries` | Legacy mutable rollback/fallback source | Main scrape dual-write is disabled; backfill/refresh/neighbor writes still dual-write with overlays. The refreshed owner card has `36,769,051` rows after `970` new backfill rows | Public mapped reads bypass it, but publication-critical `PostScrapeBandExtractor`, conditional projection fallback, direct legacy helpers, diagnostics, and restore tooling still own it |
| `leaderboard_entries_snapshot` partitions | Durable physical source | Worker snapshot writer | Worker candidate reads use active state; service/exports use the mapped published snapshot after PG-1 cutover |
| `leaderboard_snapshot_state` | Source-selection metadata | Worker finalization | Active source, not automatically a published source |
| `leaderboard_scope_fingerprints` | Correctness/audit metadata | Worker observe/coverage dual-write | Content, reported entries/pages, completeness, source scrape, and published scrape must validate before publication |
| `leaderboard_published_scope_source` | Durable published source selection | Worker candidate build and publication transaction | Service and export resolver when `Features:UsePublishedScopeSources=true`; supports physical snapshot and explicit empty sources |
| `leaderboard_population`, `song_stats` | Durable derived metadata | Worker/post-process | Ranking totals/statistics; generation must match source |
| `leaderboard_entries_overlay` | Durable corrective overlay | Controlled writes | Merged with selected base source; precedence is explicit |
| `leaderboard_current_entries` | Empty retired logical current schema | Disabled worker dual-write | Never authoritative; rows truncated 2026-07-28, primary-key family retained, dormant rank/change secondary trees retired; rebuild semantic current from the published physical map only after an explicit future migration/promotion |
| `leaderboard_entry_versions` | Empty retired logical chronology schema | Disabled worker dual-write | Non-authoritative scrape `1223`-`1237` chronology intentionally discarded 2026-07-28; primary-key family retained and dormant open/from-scrape secondary trees retired |
| `leaderboard_logical_write_metrics` | Audit/artifact | Worker | Per-scrape changed/new/unchanged evidence |
| `current_leaderboard_entries`, `solo_current_projection_scope`, `solo_current_projection_state` | Derived published/current projection | `SoloCurrentProjectionBuilder` | Preferred bounded current reads when scope state is ready |
| `valid_score_overrides` | Durable operator/source metadata | Controlled writes | Threshold exception source; retain provenance |

`leaderboard_snapshot_state.active_snapshot_id` may advance before publication
and remains the worker's candidate source. Service containers enable
`Features:UsePublishedScopeSources` only after the current published scrape has
a complete mapping. Worker containers keep that read flag disabled while
enabling `Features:WritePublishedScopeSources`, so post-process calculations
use the active candidate and public reads stay on the mapped published source.
Rollback disables the two flags; the additive table and fingerprint fields may
remain for diagnosis.

`PublishedSoloScopeSql` is the shared current-publication selector used by
service-side solo readers. When the service flag is enabled, generated reader
SQL contains no active-snapshot branch. Projection fast paths additionally
require the mapped source ID, ready scope state, and matching projection
generation in the same query that reads projection rows; a mismatch falls back
to the mapped physical snapshot plus overlay rather than active state.
`PlayerDataExportService` delegates published solo scores to this same
resolver. Published mapping rows also snapshot the clean-boundary
`leaderboard_population` floor so route totals do not consume a later active
scrape update. Existing complete mappings repair this metadata only when
unfrozen with no newer scrape or active snapshot. The old active-snapshot
export/query path remains only behind the disabled rollback flag.

Published full-player exports prefilter published band-projection rows through
the indexed durable `band_members` membership set before applying the
published-generation join. This preserves solo and band export contents while
avoiding an unbounded cold scan of `current_band_leaderboard_entries`.

Band best/worst-song public reads use the optional
`band_song_team_rankings_current_band_*` projection only while it is fresh.
When that projection is stale or disabled, extrema are derived from
`current_band_leaderboard_entries` rows joined to each scope's
`published_generation`, using the same ordering as the `/song-rows` response.
The stale projection data was retired on 2026-07-26; its empty tables,
indexes, TOAST relations, and `band_song_team_ranking_state` audit rows remain
available for an exact archive restore or a future clean-generation rebuild.
`scrape_publication_state.band_projection_generation` is stamped in the same
transaction as the global published scrape. Both public band-song endpoints
return `503` while that generation differs from
`band_current_projection_state.current_generation`, preventing an internally
published projection from escaping before global publication. Both endpoints
also return `503` when their published scope is unavailable instead of reading
candidate `band_entries`; live current-state extrema require the explicit
`BandSongPerformanceReadMode.CurrentState` selector.

PG-1 does not enable physical snapshot write skipping or change max-page,
deep-scrape, retry, or Epic request policy. WORKER-0A extends scope
completeness with a per-scrape manifest containing the expected and received
pages, every final page status, legitimate Epic empty/forbidden terminal
boundary, parse status, retry exhaustion, reported totals/pages, coordinated
deep range, and content/coverage fingerprints. Unexplained gaps, malformed
pages, exhausted non-terminal retries, missing expected scopes, writer
failures, or publication-critical phase failures reject the candidate. The
selected physical row count and physical content fingerprint must still match
the published-source mapping. Duplicate API rows remain deduplicated by
highest score per account before physical-source validation.

When `Features:SkipUnchangedPhysicalLeaderboardSnapshots=true`, the flag is
effective only with published-source writes, scope fingerprints, and strict
completeness manifests enabled. Both disk-spool and bounded-online writers
receive the completed scope manifest before persistence. A non-empty scope may
skip current-scrape physical rows only when:

- the current manifest is complete;
- current deduplicated content and row count match the current published
  mapping;
- coverage fingerprints match, except for the one-way upgrade from published
  `1236`'s legacy 32-character coverage fingerprint to a complete
  64-character manifest fingerprint;
- the mapped physical source exists with its exact row count.

Finalization then pins active snapshot state to that mapped published source.
It never selects a newer failed/active source merely because its content
matches. New, changed, incomplete, coverage-changed, missing-source, or
ambiguous scopes write a new snapshot. Empty scopes remain explicit-empty
mapping rows. The publication transaction still performs the final all-scope
count/content/coverage validation.

Disk-spool failures retain the original binary spool plus
`writer-failures.json`; bounded-online failures retain typed JSON batches.
Production passes the configured FST data-directory spool root, so all replay
artifacts remain on the required 4 TB filesystem. A failed candidate keeps its
durable status and diagnostics while the mapped published generation remains
unchanged.

`CleanupAbandonedStaging` may remove obsolete staging/deep-scrape rows and
legacy `running` scrape-log rows, but it must retain `status='failed'` rows so
their manifests, writer failures, phase outcomes, and service status remain
auditable until bounded metadata retention applies.

Mapped service reads use `current_leaderboard_entries` only for scopes whose
projection ledger matches the mapped physical/empty source. Mismatched or
failed scopes fall back individually to the mapped snapshot plus overlay, so a
single stale projection cannot force an all-instrument slow path or leak the
active candidate.

SOLO-DYNAMIC-AB confirmed that exports, member-score filtering, and
unfiltered totals do not force projection retention, but deep pages,
player/account rows, score-band metadata, rivals, ranking/precompute, and
registered-player notifications do. The promotion candidate therefore keeps
all current payload fields except `computed_at`, all nine instrument
partitions, and the account/rank/score indexes. It intentionally omits a
primary key because scope build SQL already resolves one row per account and
the source/generation ledger must validate exact row count and fingerprint.
Promotion requires a logged shadow, complete live scrape/publication parity,
and retained old-table rollback; bounded unlogged samples are evaluation-only.

### Score, player, and solo ranking history

| Tables | Class | Owner/callers | Retention |
|---|---|---|---|
| `score_history` | Durable user-visible history | `MetaDatabase`, player/ranking services | Preserve score/rank/season/timestamp semantics. The explicit audited PG-3/PG-7 maintenance command promotes `ix_sh_dedup` to five-column `UNIQUE ... NULLS NOT DISTINCT`; no row cleanup runs at startup. |
| `score_history_dedup_maintenance_runs`, `score_history_dedup_original_rows` | Immutable maintenance audit/restore source | Explicit `ScoreHistoryDedupMaintenanceService` CLI only | Retention-independent. Stores non-null CLI/database/digest/index provenance and every affected original row before merge/delete; triggers reject update, delete, truncate, and post-seal original-row append. |
| `player_score_observations` | Empty retained rollback schema for the retired non-authoritative observation surface | Solo-history and band-member writers remain independently default-off in deployed code/config; no production reader | OBSERVATION-RETIRE truncated `10,167,937` rows after scrape `1267` parity, reclaiming `12,682,330,112` database bytes while preserving the table, union view, indexes, primary key, and sequence |
| `player_stats`, `player_stats_tiers` | Derived projection | Player stats calculator/API | Rebuildable for a published generation |
| `account_rankings`, `account_ranking_stats` | Derived ranking projection | Rankings pipeline | Rebuildable; generation/source must remain auditable |
| `rank_history` partitions, `rank_history_snapshot_stats`, `rank_history_tracked_accounts` | Durable user-visible history and snapshot metadata | Ranking/history pipeline and API | Append only on meaningful change after PG-5 redesign |
| `rank_history_latest` | Empty obsolete latest projection schema | No current exact caller | ORPHAN-RECLAIM truncated stale rows; deterministic rebuild from retained `rank_history` |
| `ranking_deltas`, `ranking_delta_tiers`, `rank_history_deltas` partitions | Derived experimental projections | Rankings pipeline | Feature-flagged; rebuildable |
| `composite_rankings`, `composite_rank_history`, `composite_ranking_deltas` | Derived current plus durable history | `MetaDatabase`, rankings API | Current/deltas rebuildable; history retained by explicit policy |
| `composite_rank_history_latest` | Empty obsolete latest projection schema | No current exact caller | ORPHAN-RECLAIM truncated stale rows; deterministic rebuild from retained `composite_rank_history` |
| `solo_family_rankings`, `combo_leaderboard`, `combo_stats`, `combo_ranking_deltas` | Derived ranking projections | Rankings pipeline/API | Rebuildable from published solo current state |

### Band source, identity, membership, and projections

Band-partitioned source/current families use `Band_Duets`, `Band_Trios`, and
`Band_Quad`.

| Tables | Class | Writer | Readers / semantics |
|---|---|---|---|
| `band_entries` partitions | Durable source | `BandSpoolWriterFactory`, `BandLeaderboardPersistence` | Band projections, exports, repair, ranking inputs |
| `band_identity` | Durable identity source | Band persistence | Stable band/team identity |
| `band_members`, `band_member_stats` | Durable member facts/statistics | Band persistence | Exports, projection, search, API; overlapping ownership is PG-3.3 |
| `band_team_configurations`, `band_team_membership`, `band_team_membership_state` | Durable configuration/membership source | Band extraction/ranking | Canonical membership migration is PG-2/PG-3 |
| `current_band_leaderboard_entries` partitions | Derived current projection | `BandCurrentProjectionBuilder` | Public/export current band rows |
| `band_current_projection_scope`, `band_current_projection_state` | Publication/readiness metadata | Band projection builder | Published generation/readiness; source ownership must align with PG-1 |
| `band_current_projection_source_state` | Empty retired source-state experiment schema | No current exact caller | ORPHAN-RECLAIM truncated stale rows; any reuse needs a versioned owner |
| `band_search_team_projection`, `band_search_member_projection`, `band_search_projection_state` | Derived search projection | `BandSearchProjectionBuilder` | Service search/profile reads; rebuildable |
| `band_extraction_source_state` | Durable work/source metadata | Band extraction pipeline | Prevents ambiguous source generation |

Registered-player activity resolves known teams through the indexed
`account_id` paths on `band_team_membership`, `band_members`, and
`band_search_member_projection`. The worker batches all requested accounts in
one query. It must not probe
`account_id = ANY(band_search_team_projection.member_account_ids)`: that
unnestable array predicate has no supporting index and previously caused one
full 12 GB team-projection scan per registered account at every scrape
boundary.

### Band rankings and rank history

| Tables | Class | Owner/callers | Publication/retention |
|---|---|---|---|
| `band_team_rankings_current_band_*` | Derived current ranking projection | Ranking rebuild/table swap | Candidate current state |
| `band_team_rankings_published_band_*` | Derived published ranking projection | Publication transaction | Public ranking source and rollback target |
| `band_team_ranking_stats_current_band_*`, `band_team_ranking_stats_published_band_*` | Derived stats projection | Ranking rebuild/publication | Must promote with ranking rows |
| `band_team_ranking_generation` | Publication/audit metadata | Ranking pipeline | Tracks durable generation and source scrape |
| `band_song_team_rankings`, `band_song_team_rankings_current_band_*`, `band_song_team_ranking_state` | Retired optional song/team ranking projection schema and audit state | Ranking pipeline only when explicitly re-enabled | Data tables are empty; rebuild defaults off; public reads use published current-band rows or fail closed |
| `band_team_rank_history`, `band_team_rank_history_points`, `band_team_rank_history_latest`, `band_team_ranking_stats_history` | Legacy durable history/latest | `MetaDatabase`, history API | Retain until v2/read-source parity and restore prove removal |
| `band_team_rank_history_points_v2` partitions | Durable public history for Quad | Disabled history writer; API/export for non-promoted band types | Duets and Trios leaves retired; Quad remains `359,383,226` rows / `388,775,297,024` bytes |
| `band_team_rank_history_points_v3_duets` monthly partitions and dictionaries | Durable compact Duets public history | `MetaDatabase` when the default-off compact flag and ready state are enabled | `215,134,574` rows / `52,134,436,864` bytes; rebuilds v2 through checked-in SQL |
| `band_team_rank_history_points_v3_trios` monthly partitions and dictionaries | Durable compact Trios public history | `MetaDatabase` when the independently default-off compact flag and ready state are enabled | `343,275,419` rows / `83,664,461,824` bytes; rebuilds v2 through checked-in SQL |
| `band_team_rank_history_latest_v2` partitions | Empty derived latest delta schema | History worker only when mode is enabled | ORPHAN-RECLAIM truncated `21,403,363` rows while production mode was `Disabled`; rebuildable from retained v2 points |
| `band_team_rank_history_snapshot_v2` | Durable history generation metadata | History worker/API status | Primary freshness/coverage ledger |
| `band_rank_history_jobs`, `band_rank_history_job_chunks` | Durable resumability state | Background history worker | Keep incomplete/failed jobs for bounded retry/replay |

Band ranking rows, stats, cache generation, scrape publication pointer, and
future per-scope source mapping must promote atomically. A failed band type must
retain its prior published generation rather than produce a success-shaped
partial result.

### Rivals, improvements, notifications, and caches

| Tables | Class | Owner/callers | Retention/publication |
|---|---|---|---|
| `user_rivals`, `rival_song_samples`, `rival_song_fingerprints`, `rival_instrument_state`, `rivals_status`, `rivals_dirty_songs` | Derived durable user projection/work state | Rivals calculator and API | Rebuild from published source; dirty/status rows are resumable work |
| `leaderboard_rivals`, `leaderboard_rival_song_samples` | Derived public projection | Rankings/rivals pipeline | Generation must match published leaderboard source |
| `player_improvement_state`, `player_rank_improvement_state`, `band_improvement_state`, `band_rank_improvement_state`, `band_improvement_subjects` | Durable detection state | Improvement detector | Idempotency/delta state. Subjects registered after the prior completed detection run are baselined once before events are emitted, preventing back-catalog first-play/first-score spam while preserving later improvements. |
| `player_improvement_events`, `band_improvement_events`, `improvement_detection_runs` | Durable event/audit | Improvement detector/service | Bounded retention with replay identity. `notification_purpose`, `notification_cause`, and `delivery_state` default existing/routine rows to visible. Public reads, source cursors, expiry, and supersession operate only on `delivery_state='visible'`. Detection runs record `published_scrape_id` and selective new-subject baseline counts so publication completion and catch-up are auditable. |
| `service_notifications` | Durable notification outbox/read model | `ImprovementNotificationService` | Existing and item-shop rows default to visible routine metadata. Public reads and expiry cleanup require `delivery_state='visible'`; future process split must preserve replay. |
| `improvement_notification_maintenance_runs`, `improvement_notification_maintenance_candidates` | Non-public maintenance audit/quarantine | `ImprovementNotificationMaintenanceService` | Purpose `maintenance_pro_lead_max_score_repair_v1` has a compile-time and database-enforced visible delivery cap of exactly zero. The run stores the exact manifest, total-charted count, and canonical projected classification. Its `published_scrape_id` is a non-null immutable integer with no retention-coupled `scrape_log` FK. Only maintenance-attributed candidates enter quarantine; those rows have no expiry column and never participate in public reads, routine supersession, source cursors, or WebSocket invalidation. |
| `api_response_cache`, `api_response_cache_staging` | Cache | Precompute/publication path | Staging swaps atomically after long band snapshot work; keep its exclusive lock at transaction end; safe to clear and regenerate from published source |

Notification recovery and registered-phase budget operations are documented in
`docs/database/ImprovementNotificationRecoveryRunbook.md`. The protected
`/api/diag/improvement-notifications` endpoint and API-side staleness monitor
surface pending/failed publication markers, scrape lag, and time lag without
changing public response contracts. Recovery reads
`improvement_notifications_projection_scopes` from the publication ledger and
fails closed when the plan is absent or not ready; it never substitutes an
all-current-scope rebuild implicitly.

The Pro Lead max-score repair uses a separate purpose-specific notification
gate. Its strict manifest contains exactly four ordinal-sorted unique song IDs,
their expected current path revisions/catalog timestamps/old Pro Lead maxima,
positive proposed maxima, staged generation IDs and DAT hashes, and complete
runtime identity. The read-only dry run requires the expected
published scrape to be completed, unfrozen, notification-complete, and backed
by completed visible routine player and band song/rank runs. Current song and
`song_stats` identities plus the published exact catalog timestamps must match
the manifest, and current Pro Lead ranking total-charted values must agree with
that published charted-song catalog.

Proposed ranks are not read from live `account_rankings`. The gate projects the
full Pro Lead population from `current_leaderboard_entries`, `song_stats`, and
`score_history` using the normal 1.05 current-score cutoff, best-valid-history
fallback, Bayesian maximum-score-percent adjustment (`m=50`, `C=0.5`), and
rank tie breakers. The canonical SHA-256 binds the published scrape ID,
normalized manifest, total-charted count, and sorted projected candidates,
while excluding timestamps, run IDs, UUIDs, and generated GUIDs. Only
`Solo_PeripheralGuitar` `max_score_percent_rank` movement is classified as
denominator-derived maintenance. Direct player/band score observations remain
ordinary work outside quarantine/baselining. Missing state, another instrument
without ordinary-score evidence, ambiguous attribution, or any other
unclassified aggregate/rank movement blocks execute.

After separately promoting exactly those staged generations and rebuilding
rankings, execute requires the same published scrape, manifest, and digest.
Every path row must have advanced exactly one revision and match the proposed
maximum, generation ID, DAT hash, catalog identity, and supplied runtime
identity; `song_stats` must expose the proposed maximum. Execute recomputes the
projection and requires the actual `account_rankings` candidate set to match it
exactly before any audit/quarantine/baseline write. A passing execute stores
only the quarantine/audit evidence and selectively advances
`player_rank_improvement_state.max_score_percent_rank` for the allowed Pro
Lead subjects, preventing a later routine pass from reinterpreting the same
maintenance movement. It does not refresh projections, touch player-song or
band state, create visible events, expire/supersede visible events, or request
notification-feed WebSocket invalidation.

The nullable `score_history` repair is a separate explicit one-shot safety
gate. `--score-history-dedup-maintenance` defaults to a canonical
`REPEATABLE READ`, `READ ONLY` dry run. Its digest binds sorted original rows,
per-group selected survivor/rank/time values, the merge contract, and the
structured current index state while excluding transaction/report clocks,
planner estimates, and relation sizes. Reports include total/null/duplicate
rows, groups/excess, affected account/song IDs, per-group maxima and semantic
variance, table/index sizes, and exact merge semantics.

Execute additionally requires `--score-history-dedup-execute` and
`--expected-score-history-dedup-digest`. Under a transaction advisory lock and
`SHARE ROW EXCLUSIVE` lock on `score_history`, it re-reads and verifies the
digest before reserving an audit ID. Any duplicate with `new_score != 0`, or
variation outside `id`, `new_rank`, `all_time_rank`, and `changed_at`, blocks
before writes. A passing transaction stores every original row, preserves the
lowest ID, earliest `changed_at`, and minimum positive/non-null ranks, deletes
only non-survivors, then builds the PostgreSQL 17 `NULLS NOT DISTINCT`
replacement under a temporary name. Reads remain available during the index
scan/build; the final old-index drop/new-index rename creates a brief
`ACCESS EXCLUSIVE` pause immediately before commit. Existing five-column
`ON CONFLICT` paths therefore become null-safe without query-specific
predicates.

The immutable run stores executable rollback SQL. Rollback verifies the target
index and unchanged merged survivors, drops the nulls-not-distinct index,
restores exact audited originals, recreates the legacy ordinary unique index,
and advances the sequence without rewinding it. Full commands and lock/runtime
planning are in
`docs/database/ScoreHistoryDedupMaintenanceRunbook.md`.

### Dirty, shadow, and audit-only surfaces

`scrape_dirty_account`, `scrape_dirty_song_instrument`,
`scrape_dirty_band_scope`, `scrape_dirty_band_team`,
`post_scrape_shadow_run`, `post_scrape_shadow_metric`,
`invalid_leaderboard_shadow_observation`, and the former
`notification_cleanup_audit_20260509` are work/audit surfaces. They require a
named owner and bounded retention before cleanup. Zero current rows or zero
`pg_stat` scans alone is not sufficient evidence for deletion.

The 2026-07-26 storage-owner manifest proved the four `scrape_dirty_*` tables
contained `19,836,661` rows only from scrapes `926`-`1146`, occupied
`8,706,752,512` bytes, had no current repository caller or database
dependency, and recorded no writer since the 2026-07-07
`pg_stat_statements` reset. ORPHAN-RECLAIM truncated the complete family after
confirming 27 later successful scrapes culminating in published `1236`; the
empty schemas and primary keys remain. The checked-in package now accepts only
the original exact manifest or the fully empty retired state.

## Index and partition policy

1. Partition pruning keys are mandatory on the large instrument and band-type
   families. Queries should include the partition key explicitly.
2. Primary and unique indexes enforce upsert, swap, identity, and historical
   correctness even when `idx_scan=0`; never drop them from scan counts alone.
3. Secondary indexes require an owner card: creating migration, constraint
   ownership, caller/query, statistics age, size, recreate SQL, and write cost.
4. Large index creation uses `CREATE INDEX CONCURRENTLY` when the table and
   PostgreSQL command permit it, with explicit lock and statement timeouts.
5. Build/swap tables must not leave stale build-name indexes without ownership
   proof.
6. Date/range partitioning is preferred for future history retention only when
   the public history contract, range manifest, and rehydration path are proven.
7. `composite_rank_history` intentionally has neither `ix_crh_latest` nor the
   former `ix_crh_retention_cutoff_account` btree. The full latest-state
   snapshot job uses a parallel sequential scan/sort plan at production scale.
   The primary key preserves identity and account-bounded date access, while
   `ix_crh_retention_cutoff_brin` rejects empty cutoff ranges for bounded
   retention.
8. `band_team_rank_history_latest_v2` is currently empty and intentionally has
   no `ix_btrhlv2_snapshot` secondary family. Production history mode is
   disabled and public reads use retained point/wide history, not this latest
   state. Any future delta join or `ON CONFLICT` writer uses the retained
   partition primary keys; the retired `snapshot_id` path had no production
   owner.
9. `band_team_rank_history_points_v2` intentionally has no
   `ix_btrhpv2_snapshot` secondary family. Public history/parity reads use the
   retained team/date indexes, while primary keys retain point identity and
   conflict behavior. Its exact rollback follows the same concurrent-child,
   metadata-parent, attach sequence.
   BAND-HISTORY-COMPACT proved the next replacement should not recreate both
   wide trees: a typed dictionary-backed v3 unique index family ordered by
   team/scope/combo/date serves uniqueness and the public read. Build it only
   one band type at a time after the same-drive rewrite guard passes. Duets is
   promoted and its source retired; apply the same retain/validate/detach/drop
   sequence to Trios and then Quad.
10. The retired logical shadow intentionally has no
    `ix_lce_scope_rank`, `ix_lce_last_changed`, `ix_lev_open_versions`, or
    `ix_lev_from_scrape` tree. The writer is startup-rejected and there is no
    runtime reader. All primary-key constraints remain; exact child-concurrent,
    metadata-parent, attach rollback SQL must run before any future migration
    restores ownership.

## Publication and freeze sequence

### Atomic-publication Phase 0 proof harness (2026-07-30)

The approved atomic-publication roadmap begins with additive proof and
measurement rather than changing read semantics:

- all `83` `/api` routes have exactly one explicit
  `PublicationBound`, `OperationalLive`, or `AdminPrivate` classification;
- startup validates the completed endpoint data source, rejects missing or
  duplicate metadata, rejects catalog mismatches, canonicalizes equivalent
  route spellings, and rejects dynamic first-segment routes that could capture
  `/api` traffic;
- publication-bound WebSocket content is not exempt;
- `PublicApiCacheTelemetry` records bounded per-route-template frozen hits,
  continued misses, blocked misses, and publication-bound bypasses;
- protected diagnostics are exposed at
  `/api/admin/public-cache-telemetry`;
- no cache/read behavior changes in Phase 0.

The route matrix is the driver for later old-generation/new-generation
integration and browser tests. The cache telemetry is deployed but its
representative frozen-window baseline remains a time-accrual gate for the next
approved scrape.

### Atomic-publication Phase 1 safe-writer boundary (2026-07-30)

Phase 1 removes direct early publication from registration, profile/history,
rivals, response-cache, notification, and background-history paths while the
generation ledger is still being built:

- single-user precompute no longer mutates the live or shared staging cache;
- registered users without a published full-profile/history payload receive a
  no-store `202 syncing/notYetPublished` response;
- filtered history is derived only from the published full-history payload,
  and filtered registered profiles fail closed rather than reading live rows;
- the worker quiesces background backfill/history operations before the scrape,
  waits for admitted cyclical-song work to drain, and discards cancelled
  in-memory staging before candidate work begins;
- ranking input time and projection scope keys are sealed before the
  publication cache cut. Registration work completed after that boundary stays
  `rankings_pending`, queues rivals for the next ranked publication, and cannot
  refresh the current publication projection or cache;
- projection refresh and cache precompute are unconditionally
  publication-critical. Empty cache staging, partial precompute, cancellation,
  or failed durable isolation leaves the previous publication active and keeps
  reads frozen if the isolation marker itself cannot be recorded;
- backfill pending state is monotonic and can be cleared only after a successful
  ranking publication whose input cutoff included that completion;
- background band rank-history jobs are claimable only for the current
  published scrape, read published ranking tables, and supersede failed or
  older unpublished candidates;
- notification recovery reuses the projection plan persisted with publication
  and does not mutate the published projection after commit.

This is a fail-closed transition layer, not the final generation-addressable
architecture. Phase 2 still owns durable `publication_generations`,
current/previous/working pointers, typed surface manifests,
`PublicationReadContext`, and publication-keyed URLs/query keys/caches.

### Atomic-publication Phase 2 generation/context foundation

Phase 2 adds the durable identity and request-pinning foundation without
claiming that legacy mutable surfaces are already generation-addressable:

- `publication_generations` allocates one durable identity per scrape and
  tracks `building`, `ready`, `current`, `retained`, `failed`, and `retired`
  lifecycle state;
- `scrape_publication_state` owns current, previous, and working publication
  pointers while preserving `published_scrape_id` compatibility;
- `publication_surface_bindings` records typed bindings, row counts, hashes,
  readiness, and explicit `legacy_live_unversioned`/inherited status rather
  than presenting unbound surfaces as complete;
- publication takes a cross-process advisory exclusive lock, requires the
  matching working generation, rotates the previous pointer, and records
  surface bindings in the same transaction;
- publication-bound request pinning uses a separate 64-connection lock pool,
  a short `READ COMMITTED` shared advisory lease, the
  `X-FST-Publication-Id` response header, and `publicationId` URL parameter;
- `/api/publication` bootstraps the browser before query fan-out. The web client
  clears query/catalog caches on rotation, appends the ID to HTTP/path URLs,
  pins WebSocket handshakes, and refreshes before every pinned reconnect;
- API instances poll the durable pointer and close stale pinned sockets across
  the worker/service process boundary;
- predecessor references use `ON DELETE SET NULL`, while scrape-generation
  ownership uses `ON DELETE CASCADE`, preserving normal abandoned-scrape and
  metadata-retention behavior.

`Features:EnablePublicationReadContext` remains default-off. It must not be
enabled until every `PublicationBound` route resolves through a ready,
generation-addressable surface binding. Catalog and API response cache now
have ready generation bindings; shop, path, and the remaining inherited
surfaces stay deliberately incomplete.

The first completed source cut is the API response cache:

- `publication_api_response_cache` and its staging sibling use
  `(publication_id, cache_key)` primary keys;
- precompute captures one explicit target generation and holds a global shared
  publication lock plus an exclusive per-generation build lock for the entire
  build;
- standalone current-publication rebuilds are rejected while a working
  generation or failed-candidate isolation exists;
- candidate staging, flush, and swap validate the same target and cannot
  retarget after a concurrent pointer change;
- publication retains exact current and previous cache generations only;
  failed/retired staging is deleted immediately and older cache bindings are
  marked retired;
- legacy rollback writers remain supported: startup compares row count plus a
  cache-key/ETag fingerprint and authoritatively reconciles changed or removed
  rows into the current generation;
- watchdog recovery marks the generation failed, clears the working pointer,
  and deletes its keyed staging.

Failed-candidate isolation distinguishes two cache layers. The outer
route-shaped cache may serve an exact persisted hit, but it has no producer
for endpoint-internal keys such as `player:*`, `history:v2:*`, and
`rankings:*`. An exact route catalog therefore marks only handlers that own
their failed-candidate behavior: they must serve the current publication's
internal cache, return conservative sync state such as no-store HTTP `202`, or
call `ServeUnavailableIfFrozen` before any live/unversioned read. Both public
read middlewares honor the same marker. Export, leaderboard-population, and
rank-derived notification routes remain outer-blocked until their sources are
generation-addressable. Safety-state lookup failure never delegates to the
endpoint. Legacy in-process response caches are cleared and bypassed during
failed-candidate isolation so late in-flight writes cannot repopulate
candidate data. Process and songs-cache entries carry the current publication
ID and are discarded after a pointer change; neither cache accepts writes
while public reads are frozen. Every publication-bound read holds the shared
publication advisory lease during a frozen transition even while full client
pinning remains disabled. `FailScrapeRun` takes the matching exclusive lock
before activating isolation, so an in-flight response cannot cross the
failure boundary. The dedicated read-lock pool disables
`idle_in_transaction_session_timeout` for these request-lifetime leases so the
production 60-second general timeout cannot silently drop the barrier. The
service also clears process and songs caches when the durable publication
pointer first appears or changes.
Rank-offset and first-seen cache misses now fail closed, and `/api/songs`
checks the strict gate before attempting a cold live rebuild.

The legacy `api_response_cache` tables remain as rollback/current-service
compatibility mirrors until request pinning is promoted.

CATALOG-1 adds the first immutable non-cache source cut without changing any
endpoint reader:

- `SongCatalogSnapshotBuilder` canonicalizes object keys recursively while
  preserving provider array order and raw/extension fields. Known and unknown
  Epic fields survive sync and restart; mutable local UI fields `imagePath`,
  `isSelected`, and `isInLocalData` are excluded;
- `FestivalPersistence.SaveSongsVersionedAsync` writes exact per-song
  `provider_json`, legacy compatibility columns, and the `live_song_catalog`
  singleton under the publication advisory lock. The singleton records a
  monotonic catalog version, schema version, SHA-256, count, source kind,
  exactness, and capture time;
- worker sync replaces every provider-owned field while retaining only local
  UI state. `SyncSongsWithResultAsync` reports request success, parse drops,
  duplicate IDs, zero-catalog responses, blocked eviction/safety merges, and
  the persistence token. Only a successful fully parsed response with no
  safety merge is persisted as `provider_exact`;
- the worker requires that exact result and token before freezing reads or
  allocating a scrape. Failed, partial, zero-song, or safety-merged refreshes
  abort the pass with no `scrape_log`, generation, ready binding, or live
  exactness promotion;
- a per-service semaphore serializes the complete provider fetch, merge,
  canonical snapshot, and persistence operation used by startup, shop refresh,
  the catalog worker, and the scrape worker. A queued inexact refresh cannot
  mutate objects participating in an exact capture before its token returns;
- `SyncImagesAsync` persists only `SongLocalState` through
  `ILocalSongStatePersistence`. PostgreSQL updates `songs.image_path` without
  touching `provider_json`, `live_song_catalog`, catalog versions, hashes, or
  exactness;
- resume-only recovery resolves the exact ready
  `publication_song_catalog` for the resumed scrape, validates its token, and
  creates an isolated `FestivalService` snapshot. All resume post-process song
  lists, scrape requests, expected scope pairs, rankings, rivals, precompute,
  cleanup, and cache priming use that snapshot; no provider refresh or new
  publication allocation occurs;
- the 10% eviction guard applies only when the loaded in-memory baseline came
  from a trusted exact live catalog. A reconstructed/inexact baseline is
  replaced wholesale by the first complete, fully parsed provider response,
  allowing legacy stale rows to converge to an exact source. A rejected bulk
  eviction does not downgrade baseline trust or replace the persisted token;
  it also does not apply partial provider mutations to the in-memory exact
  baseline, so repeated identical partial responses remain rejected;
- `ScrapeOrchestrator` snapshots the same songs used to build scrape requests,
  verifies their hash/count against the persistence token, and
  `StartScrapeRun` rejects any service/worker race before inserting a scrape
  row. Allocation copies only the matching exact live version into
  `publication_song_catalog` and creates the ready binding under the same
  advisory lock;
- publication validates the snapshot/binding pair but does not rewrite it as
  `legacy_live_unversioned`, so later catalog refreshes cannot alter an
  in-progress or published generation;
- startup may reconstruct diagnostic payloads from legacy `songs` columns,
  but labels both singleton and publication rows reconstructed/inexact and
  keeps the surface binding `building`. Existing unproven ready bindings are
  downgraded. A fresh exact provider capture is required before any new working
  publication receives a ready binding; current historical generations are
  never rewritten to pretend source-cut accuracy;
- catalog payload retention follows current, previous, and working publication
  pointers. Failed and older payloads are removed while their binding metadata
  is marked failed or retired;
- additive rollback keeps old insert/update SQL valid through defaults. A
  compatibility trigger detects any legacy content change that did not advance
  the catalog version and marks the singleton reconstructed/inexact, forcing a
  fresh provider capture before new-code allocation.

This is additive storage only. `/api/songs`, `/api/shop`, and path generation
still read the live `FestivalService`/legacy path sources, and
`Features:EnablePublicationReadContext` remains `false`. Rollback is a prior
binary with the additive tables retained and ignored; no destructive migration
is required.

The read-only band-search reuse probe measured
`46,662,828,032` bytes across `band_search_team_projection` and
`band_search_member_projection`. Scrape `1269`/`1271` refresh evidence bounds
reusable team bindings at `92.79-94.09%`. Changed immutable payload plus a
full publication map is modeled at `2.16-2.85 GB` per publication, replacing
the prior conservative full-copy assumption. Exact content-fingerprint proof
is still required before implementation.

1. Worker starts `scrape_log` and freezes public reads.
2. Network, staging, physical/logical writes, fingerprints, and scope coverage
   accrue without changing the public generation.
3. The freeze reason transitions from `scrape` to `post-process`; required
   projections, rankings, caches, and cleanup complete while the frozen scrape
   ID remains pinned to the current published scrape and the original freeze
   timestamp remains stable.
4. The freeze reason transitions to `publish`. The publication transaction
   validates and promotes the complete per-scope
   mapping and fingerprint publication IDs together with all required public
   pointers and cache generation, records the published scrape, and unfreezes
   public reads.
5. Any failed network, writer, manifest, required post-process, or publication
   step leaves the prior published generation active and explicitly unfreezes
   it after the failed pass is finalized.

If a watchdog must stop a worker after unversioned derived tables have
changed, finalize the scrape with an explicit isolation marker:
`capacity_watchdog_abandoned` for disk-capacity stops or
`post_process_no_progress_abandoned` for a stale post-process operation. The
publication ledger may then be unfrozen on the prior published scrape only
after proving that the worker process, worker-owned query/transaction,
candidate-publication DML, ungranted/advisory lock, maintenance task, and
candidate published-source rows are all absent. The service treats either
durable marker as a separate published-read isolation state. Persistent
published cache hits and explicitly mapped solo leaderboard reads remain
available; unversioned derived cache misses and exports return `503` until a
later successful publication advances past the abandoned scrape. This prevents
ranking, history, export, band-song, or fallback reads from exposing the failed
candidate while keeping
`scrape_publication_state.public_reads_frozen=false` and service status
truthful.

For autonomous live windows, run:

```bash
node tools/fst-worker-no-progress-watchdog.mjs \
  --monitor \
  --evidence-dir /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/<session>/watchdog \
  --idle-seconds 2700 \
  --poll-seconds 60 \
  --send-report
```

The default idle gate is 45 minutes without an explicit operation/phase
advance. A worker-owned active PostgreSQL query defers the timeout, and an
optional `--max-phase-seconds` adds a measured hard bound. Recovery stops the
worker, writes exact rollback SQL, performs the guarded fail/unfreeze/offline
transaction, and sends or renders the incident report. Exit code `42` means
the recovery committed and autonomous repair must continue.

`/api/service-info` reads latest scrape, published scrape, publication/freeze,
and worker operation state in one PostgreSQL statement. It exposes active and
published scrape IDs, freeze reason/ID, network/post-process/publication
phases, failed or stalled states, and only a future scheduler timestamp.
Worker liveness updates only `last_heartbeat_at`. The operation
`UpdatedAtUtc` field advances only for explicit operation, phase,
sub-operation, item, or progress changes, so a liveness loop cannot hide a
stalled post-process phase. `/api/service-info` derives live elapsed time from
the operation start even when the progress timestamp is intentionally stable.
The best-effort deferred-registration phase has a separate 30-minute default
timeout; it records a visible best-effort failure and leaves queued users for a
later pass instead of delaying publication indefinitely.

The PG-1 schema is additive and creates no startup secondary-index build. The
primary key is ordered for current-publication instrument reads. Initial
backfill runs only at a clean unfrozen boundary with no newer scrape or active
snapshot, validates all physical row counts, and fails closed on a partial or
ambiguous mapping. Runtime freeze/publish calls first use a read-only schema
probe; missing legacy columns are repaired in a separate short transaction
with a five-second lock timeout, so schema DDL locks are not retained through
cache and band-ranking publication work.

The notification-maintenance schema is also additive. Constant defaults make
pre-existing notification rows visible without a data backfill or table
rewrite. `DatabaseInitializer` creates only the two notification prerequisites,
then runs and commits the complete notification schema in its own command and
transaction before the unbounded main/publication schema batch. That
transaction uses local two-second lock and fifteen-second statement timeouts,
so notification ALTER locks cannot survive into publication reconciliation.
No secondary index is added to the existing event tables.

The score-history dedup audit schema is likewise additive and runs in its own
short schema transaction with the same two-second lock and fifteen-second
statement timeouts. It creates only the immutable run/original-row audit
tables, trigger function, triggers, and small digest lookup index. It does not
scan, merge, delete, or index-rebuild `score_history`; those actions exist only
behind the explicit maintenance execute digest gate.

## Retention and maintenance

- `DatabaseRetentionMaintenanceService` skips cleanup under measured database
  pressure and uses an advisory lock.
- Metadata TTL cleanup is bounded by batch count, row count, and command
  timeout. Snapshot rewrite remains disabled by default.
- Composite rank-history retention batches are intentionally unordered. The
  BRIN handles cutoff-range rejection, the primary key handles account/date
  existence probes, and `LIMIT` bounds each delete without a global sort.
- `tools/postgres-capacity-guard.sh` is required before broad scrape,
  post-process, optional build, or maintenance work. It records free space,
  DB/WAL size, scratch, publication state, locks, and active maintenance.
- Use its `reclaim` action only for a proven space-releasing operation with
  zero transient/scratch bytes. It still requires one full-scrape emergency
  buffer by default and no active vacuum/index/rewrite/ungranted-lock
  conflict. Below the buffer, `--expected-reclaim-bytes` is an explicit
  fail-closed exception only when the conservative estimate restores the full
  emergency window; rerun the guard without an estimate after the action.
- `VACUUM FULL`, broad table rewrites, large non-concurrent index builds, and
  unbounded `pg_repack` are prohibited at current headroom.
- Archive/prune operations require exact object/range manifests, checksums,
  rehydration, live-scrape parity, rollback, and post-action route validation.
- Score-history dedup audit rows have no scrape-log foreign key, expiry, or
  retention path. Preserve them after execute and after any rollback.

## Backup, restore, and rollback

### Current promotion gate

A full multi-terabyte duplicate restore is not safe with approximately
`164,328,067,072` bytes free after LOGICAL-RETIRE. Full
restore promotion remains blocked until same-drive reclaim creates the exact
source database plus target data/WAL/index/scratch headroom.

### Bounded restore path

PG-0 uses this non-destructive interim path:

1. Run the capacity guard and record the production commit/image, published and
   active scrape IDs, freeze state, relation sizes, locks, and free bytes.
2. Create schema-only backup plus a bounded representative manifest containing
   catalog/publication metadata and selected solo, band, score-history, and
   ranking rows from named scrape/scope keys.
3. Store backup, manifest, checksums, and restore workspace under
   `/mnt/docker-storage/Docker/FestivalServiceTracker/`; never use another
   filesystem.
4. Restore into an isolated PostgreSQL database/container that cannot publish
   or contact Epic.
5. Validate schema/constraints, row counts, min/max keys and timestamps,
   fingerprints/checksums, and representative API fixtures.
6. Drop the isolated target after evidence is persisted.

The bounded `score_history` dedup restore path is independent of the general
database restore drill: retrieve `rollback_sql` from the immutable maintenance
run and execute it only under the runbook's maintenance gate. It restores all
audited IDs/values and the legacy index in one fail-closed transaction; the
audit evidence remains.

Run the implemented drill with:

```bash
tools/postgres-bounded-restore-drill.sh \
  --scrape-id <published-id> \
  --out-dir /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/<session>/bounded-restore-<id>
```

The command retains the isolated target PGDATA as evidence and removes the
container. Delete that exact retained target only after its manifest/report is
accepted and another restore is not using it.

The 2026-07-10 scrape-1228 drill restored 32 datasets with exact CSV parity and
matched selected fields from both solo and band public API fixtures. The
backup was 21,620,913 bytes, the restored database was 65,812,147 bytes, and
restore time was 9.970 seconds after 76.918 seconds of bounded export work.
The retained isolated PGDATA was 173,083,028 bytes.

The same live measurements proved a full duplicate restore cannot fit:

- streaming restore additional requirement: 3,934,382,812,204 bytes;
- current free bytes: 314,856,988,672;
- streaming shortfall: 3,619,525,823,532 bytes;
- durable full backup plus duplicate restore requirement:
  7,502,187,283,167 additional bytes.

Full restore remains blocked until source size and same-drive headroom satisfy
the measured formula. The bounded drill is accepted evidence, not a waiver.

### Restore behavior by class

| Class | Restore/rollback |
|---|---|
| Durable source/history | Restore verified rows/tables/partitions, then compare manifests before allowing reads |
| Publication ledger | Restore last known published mapping/generation atomically; keep reads frozen until validation |
| Derived projections/rankings | Rebuild from restored published source or restore the matching generation |
| Cache | Clear and regenerate after source/projection validation |
| Work queues/staging | Replay retained spool/outbox/claims or discard only the exact failed operation |
| Index experiment | Recreate exact captured DDL and verify owning query/route |
| Feature-flag candidate | Revert config/image/commit while retaining diagnostic dual-write data when safe |

The P6/P8/P9 owner cards, manifests, rebuild limitations, and exact
truncate/drop sequence are in
`docs/database/StorageOwnershipReadinessRunbook.md`. Read-only recapture uses
`tools/postgres-storage-ownership-readiness.sh`; the generated packages have no
apply mode and all destructive SQL fails closed on an explicit session GUC.
The executed orphan/latest-state object list, exact byte deltas, and future
rebuild statements are in `docs/database/OrphanReclaimRunbook.md`.

### Retired logical leaderboard shadow

- Executed truncate:
  `TRUNCATE TABLE public.leaderboard_current_entries,
  public.leaderboard_entry_versions;` without `CASCADE`. Truncating either
  partitioned parent includes its nine leaf partitions and their indexes; it
  does not include `leaderboard_logical_write_metrics`. LOGICAL-RETIRE ran this
  transaction on 2026-07-28 after a rollback-only rehearsal and a successful
  pre-commit public parity check.
- Current-state rebuild uses the current
  `scrape_publication_state` row, complete
  `leaderboard_published_scope_source` snapshot mappings, and
  `leaderboard_entries_snapshot`. It recomputes the writer-compatible row
  fingerprint and resets logical first/change/seen metadata to the published
  baseline. It does not recreate discarded chronology.
- Version chronology is experimental and non-authoritative. No full same-drive
  duplicate is permitted. Preserve exact counts/ranges/canonical
  fingerprints, schema DDL, and a bounded deterministic sample. A future
  promotion may seed one new open baseline version per rebuilt current row,
  but must not describe that as restoration of the discarded history.
- Scrape `1267` completed with the writer disabled, globally published, and
  passed mapped route, export, ranking, history, publication, health, and
  resource parity. Independent recapture found zero database dependencies,
  unchanged full logical fingerprints, and no production read path.
- The committed truncate reclaimed `123,173,593,088` database bytes. All
  target rows are zero, metrics remain 108 rows, public fingerprints stayed
  `13/13` exact through the 60-second monitor, and the final scrape capacity
  guard passes with more than 103.9 GB of margin.
- Execution evidence and exact rebuild SQL:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/logical-retire-executed-20260728T092804Z`.

## Operational verification

Before broad work:

```bash
tools/postgres-capacity-guard.sh \
  --action-class observation \
  --output /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/<session>/capacity-preflight.json
```

For a proven zero-scratch index/table release, rerun the same guard with
`--action-class reclaim`. A successful observation does not override a blocked
reclaim, optional-build, maintenance, or rewrite class.

Then verify:

- production `docker compose ps`;
- Postgres `pg_isready`;
- `fstservice` `/readyz`;
- festivalweb health and static shell;
- `/api/service-info` through festivalweb;
- `scrape_publication_state`, latest `scrape_log`, and worker heartbeat;
- ungranted locks, long queries, active vacuum/index/rewrite;
- disk, CPU, memory, WAL, and temp counters.

After any worker/service/web restart, all expected containers and the full
public path must be healthy before unrelated work continues.

For a durable per-scrape evidence pack:

```bash
tools/postgres-scrape-evidence.sh \
  --scrape-id <id> \
  --label baseline \
  --out-dir /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/<session>/scrape-<id>-baseline
```

The pack contains publication state, complete scope fingerprint metadata,
logical write metrics, relation/index sizes, WAL/temp/checkpoint counters,
phase timings, locks/active queries, projection summaries, Docker resources,
representative public route bodies and hashes, and a checksummed manifest.
Pass `--compare-to <baseline-dir>` on a later capture to persist route,
scope-total, and relation-size deltas for an A/B decision.

## Known design work

- **PG-1 / SERVICE-0 / WORKER-0:** complete live-scrape promotion evidence for
  the per-scope source resolver, then continue worker failure propagation and
  durable service-status semantics.
- **PG-2 / SERVICE-2:** bounded published reads, set-based API queries, and
  asynchronous cancellation-aware repositories.
- **PG-3:** retain public band-history and composite-retention indexes; the
  redundant `ix_crh_latest`, latest-v2 `ix_btrhlv2_snapshot`, and points-v2
  `ix_btrhpv2_snapshot` indexes were retired with exact plan/route/export
  parity. The solo `ix_rh_latest` family was also retired after its ranking
  latest-row owner moved to a primary-key group/max plan. Post-`1263`,
  duplicate band ranking, dirty-work, appearance-sort, orphan latest,
  observation-read, and composite-ranking secondary indexes were retired
  while their primary/unique owners stayed intact. ORPHAN-RECLAIM also emptied
  the stale dirty/shadow state plus unowned composite/solo/band latest caches.
  Member facts, observation-table publication parity, and nullable
  score-history uniqueness remain explicit owner decisions.
- **PG-4 / WORKER-4:** semantic-change writes, unchanged physical source reuse,
  diff projections/rankings, and one atomic band publication. The retired
  logical shadow is not a PG-4 reader or default writer.
- **PG-5:** latest-state history design, explicit retention, and same-drive
  Parquet/DuckDB artifact pilots.
- **PG-6 / SERVICE-4:** versioned migrations, one migration owner, autovacuum,
  work-memory, WAL/checkpoint, and recovery governance.
- **PG-7:** parity-gated object-by-object archive and reclaim.

These are tracked changes to this design, not permission to bypass its current
source, publication, same-drive, restore, or parity rules.
