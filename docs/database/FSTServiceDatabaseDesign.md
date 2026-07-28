# FSTService PostgreSQL Database Design

**Authoritative runtime:** PostgreSQL 17 in `fst-postgres`  
**Production compose owner:** `/home/sfenton/Docker/FestivalServiceTracker`  
**Production data root:** `/mnt/docker-storage/Docker/FestivalServiceTracker/pg-data`  
**Last targeted live storage validation:** 2026-07-28 11:25 UTC

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
| Song catalog, shop, and path metadata | Currently shared; SERVICE-1 makes service sole owner | `FestivalPersistence`, `MetaDatabase`, `PathDataStore` |
| Durable notifications/improvement state | Shared persistence; service delivers publicly | `ImprovementNotificationService` |
| Retention planning and bounded cleanup | Service maintenance runner | `DatabaseMaintenanceDryRunReporter`, `DatabaseRetentionMaintenanceService` |

Production API-only and worker containers both skip routine startup schema
initialization. Schema-changing releases run an explicit one-off initializer
while the worker is held, then deploy the role containers. PG-6/SERVICE-4
replace this monolithic path and localized `Ensure*Schema` calls with a
versioned migration ledger, advisory lock, and bounded lock/statement
timeouts.

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
| Database | 3,805.58 GB |
| Solo physical snapshot partitions | 1,788.63 GB |
| Band rank-history v2 point partitions | 857.72 GB |
| Solo rank-history partitions | 174.47 GB |
| Current band leaderboard partitions | 139.04 GB |
| Composite rank history | 90.78 GB |
| Logical version partitions | 144 KB after LOGICAL-RETIRE |
| `band_member_stats` | 59.79 GB |
| Solo published/current projection partitions | 45.18 GB |
| `band_members` | 44.55 GB |
| Legacy mutable solo leaderboard partitions | 40.824 GB |
| Logical current partitions | 144 KB after LOGICAL-RETIRE |
| Band source-entry partitions | 25.37 GB |
| Band rank-history v2 latest partitions | 48 KB after ORPHAN-RECLAIM |
| `rank_history_latest` | 16 KB after ORPHAN-RECLAIM |
| `player_score_observations` | 12.682 GB |
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

Production now enables `BandRankHistory:CompactV3DuetsReadEnabled`; only
Duets reads v3. Trios and Quad remain on v2 narrow points. The retired Duets
leaf was detached with a proven 11.827 ms reattach path and then dropped
without `CASCADE`. The source drop released `154,235,944,960` bytes and the
net database reduction from phase start is `102,101,475,328` bytes. Date
deletion and Parquet-as-live-source remain rejected because the API/export
still serve all history and no runtime rehydration tier exists. Details are in
`docs/database/BandHistoryCompactionRunbook.md`.

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
| `scrape_publication_state` | Publication ledger | Worker publish/freeze transaction | Public read resolvers, service status, notifications | Single row; preserve through every restore. Publication atomically queues the published scrape for improvement detection, and interrupted attempts remain pending for startup/operator recovery. |
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
matching fingerprints published, swaps staged API cache rows, publishes current
band ranking tables, advances `scrape_publication_state.published_scrape_id`,
queues the same scrape in the improvement-notification marker when enabled,
and clears the public-read freeze in one transaction.

| Rollout switch | Container | Default | Effect | Rollback |
|---|---|---:|---|---|
| `Features__WritePublishedScopeSources` | `fstworker` | `false` | Backfills the current clean publication when needed, records scope coverage, builds the next mapping, and requires it in publication | Set `false`; incomplete candidates never move the global pointer |
| `Features__SkipUnchangedPhysicalLeaderboardSnapshots` | `fstworker` | `false` | With strict manifests/fingerprints and published-source writes enabled, reuses only exact validated published physical sources for unchanged scopes | Set `false`; all non-empty scopes write current-scrape physical rows |
| `Features__UsePublishedScopeSources` | `fstservice` | `false` | Resolves solo current reads, projection readiness, totals, member filters, and published solo exports from the current mapping | Set `false`; active-state/legacy resolver remains available |
| `Features__UseStoredSoloProjectionRanksForFilteredReads` | service or worker reader | `false` | Uses stored projection rank plus exact removed-above counts for filtered leaderboard/player ranks instead of full window re-sorts | Set `false`; prior score/tie window SQL remains |
| `Features__EnforceScopeCompletenessManifests` | `fstworker` | `false` | Requires every expected solo and band scope to have a complete page manifest before publication | Set `false`; manifests remain available as observe-only evidence |
| `Features__RequireSuccessfulScrapeWriters` | `fstworker` | `false` | Rejects a candidate when any disk-spool or bounded-online writer reports failed pages/rows | Set `false`; durable failure rows and replay artifacts remain |
| `Features__EnforcePublicationCriticalPhases` | `fstworker` | `false` | Rejects a candidate after any explicitly publication-critical post-scrape phase failure | Set `false`; phase outcomes remain visible while legacy swallow behavior is restored |
| `Features__WriteLogicalLeaderboardVersions` | all roles | `false` and startup-rejected when true | Retired shadow writer; no service/API reader exists | Future enablement requires a versioned migration, rebuild/restore validation, and a new live-scrape promotion |

### Song, account, registration, and authentication metadata

| Tables | Class | Owner/callers | Retention and safety |
|---|---|---|---|
| `songs`, `item_shop_tracks`, `season_windows`, `song_first_seen_season` | Durable source/metadata | `FestivalPersistence`, `MetaDatabase`, path/ranking readers | Keep provider IDs/timestamps and source provenance |
| `account_names` | Durable source/cache of Epic identity | Worker resolver; API/search readers | Refreshable, but historical account IDs remain stable |
| `registered_users`, `registered_bands` | Durable source | API activity/registration and worker consumers | Activity-based retention must preserve idempotent claims |
| `registered_band_processing_status`, `registered_band_processing_progress`, `registered_player_band_discovery_progress` | Durable work state | Registration/backfill workers | Resume/idempotency state; prune only completed stale work |
| `backfill_status`, `backfill_progress`, `history_recon_status`, `history_recon_progress`, `deep_scrape_queue` | Durable work state | Worker queues/orchestrators | Preserve failed/incomplete work for replay |
| `user_sessions`, `epic_user_tokens` | Security-sensitive durable state | Authentication subsystem | Never include values in logs, reports, fixtures, or exports; restore with access controls |

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
| `leaderboard_entries` | Legacy mutable rollback/fallback source | Main scrape dual-write is disabled; backfill/refresh/neighbor writes still dual-write with overlays | Public mapped reads bypass it, but publication-critical `PostScrapeBandExtractor` and direct legacy helpers still own it |
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
| `score_history` | Durable user-visible history | `MetaDatabase`, player/ranking services | Preserve score/rank/season/timestamp semantics; nullable-time uniqueness repair is PG-3/PG-7 |
| `player_score_observations` | Non-authoritative duplicate/audit observation surface | Solo-history and band-member writers are independently default-off in deployed code/config; no production reader | `10,167,937` rows remain; truncate only after a complete writer-off scrape publishes and API/export/ranking/history parity passes |
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

### Band rankings and rank history

| Tables | Class | Owner/callers | Publication/retention |
|---|---|---|---|
| `band_team_rankings_current_band_*` | Derived current ranking projection | Ranking rebuild/table swap | Candidate current state |
| `band_team_rankings_published_band_*` | Derived published ranking projection | Publication transaction | Public ranking source and rollback target |
| `band_team_ranking_stats_current_band_*`, `band_team_ranking_stats_published_band_*` | Derived stats projection | Ranking rebuild/publication | Must promote with ranking rows |
| `band_team_ranking_generation` | Publication/audit metadata | Ranking pipeline | Tracks durable generation and source scrape |
| `band_song_team_rankings`, `band_song_team_rankings_current_band_*`, `band_song_team_ranking_state` | Retired optional song/team ranking projection schema and audit state | Ranking pipeline only when explicitly re-enabled | Data tables are empty; rebuild defaults off; public reads use published current-band rows or fail closed |
| `band_team_rank_history`, `band_team_rank_history_points`, `band_team_rank_history_latest`, `band_team_ranking_stats_history` | Legacy durable history/latest | `MetaDatabase`, history API | Retain until v2/read-source parity and restore prove removal |
| `band_team_rank_history_points_v2` partitions | Durable public history for Trios/Quad | Disabled history writer; API/export for non-promoted band types | Duets leaf retired; Trios/Quad remain `702,658,645` rows / `694,619,258,880` bytes |
| `band_team_rank_history_points_v3_duets` monthly partitions and dictionaries | Durable compact Duets public history | `MetaDatabase` when the default-off compact flag and ready state are enabled | `215,134,574` rows / `52,134,436,864` bytes; rebuilds v2 through checked-in SQL |
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
| `player_improvement_events`, `band_improvement_events`, `improvement_detection_runs` | Durable event/audit | Improvement detector/service | Bounded retention with replay identity. Detection runs record `published_scrape_id` and selective new-subject baseline counts so publication completion and catch-up are auditable. |
| `service_notifications` | Durable notification outbox/read model | `ImprovementNotificationService` | Expiry cleanup is bounded; future process split must preserve replay |
| `api_response_cache`, `api_response_cache_staging` | Cache | Precompute/publication path | Staging swaps atomically; safe to clear and regenerate from published source |

Notification recovery and registered-phase budget operations are documented in
`docs/database/ImprovementNotificationRecoveryRunbook.md`. The protected
`/api/diag/improvement-notifications` endpoint and API-side staleness monitor
surface pending/failed publication markers, scrape lag, and time lag without
changing public response contracts.

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
