# FSTService PostgreSQL Database Design

**Authoritative runtime:** PostgreSQL 17 in `fst-postgres`  
**Production compose owner:** `/home/sfenton/Docker/FestivalServiceTracker`  
**Production data root:** `/mnt/docker-storage/Docker/FestivalServiceTracker/pg-data`  
**Last live inventory:** 2026-07-13 14:00 UTC

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

## Live shape snapshot

The 2026-07-10 inventory found 269 public tables/partitions, 735 public indexes,
and 273 public constraints. Values below are operational evidence, not fixed
limits.

| Surface | Live size |
|---|---:|
| Database | 3,564.32 GB |
| Solo physical snapshot partitions | 1,788.63 GB |
| Band rank-history v2 point partitions | 857.72 GB |
| Solo rank-history partitions | 174.47 GB |
| Current band leaderboard partitions | 139.04 GB |
| Composite rank history | 90.78 GB |
| Solo logical version partitions | 65.94 GB |
| `band_member_stats` | 59.79 GB |
| Solo published/current projection partitions | 45.18 GB |
| `band_members` | 44.55 GB |
| Legacy mutable solo leaderboard partitions | 40.82 GB |
| Logical current partitions | 27.30 GB |
| Band source-entry partitions | 25.37 GB |
| Band rank-history v2 latest partitions | 18.80 GB |
| `player_score_observations` | 11.69 GB |

The PG-1 decision sample had `247.2GB` free on `/mnt/docker-storage`, published
scrape `1230`, public reads unfrozen, `6,129` complete published-source rows,
and no ungranted locks. The per-scope map occupied `4.63MB`.

After scrape `1236`, PG-3 retired the redundant non-constraint
`ix_crh_latest` index from `composite_rank_history`. Database size fell from
`3,784,729,548,467` to `3,763,839,399,603` bytes, and filesystem free space
rose from `78,549,483,520` to `99,439,702,016` bytes. The exact index DDL is
retained as rollback evidence; no table rows, constraints, publication state,
or history data changed.

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
| `scrape_log` | Durable source | Worker via `MetaDatabase` | Service status, maintenance, evidence tooling | One row per scrape; incomplete rows are not publishable |
| `scrape_publication_state` | Publication ledger | Worker publish/freeze transaction | Public read resolvers, service status, notifications | Single row; preserve through every restore |
| `leaderboard_published_scope_source` | Durable per-scope publication ledger | Worker coverage/build/publish path | Service current-state readers and solo exports behind rollback flag | One validated snapshot or explicit empty source per published `(song_id, instrument, scope_kind)` |
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
and clears the public-read freeze in one transaction.

| Rollout switch | Container | Default | Effect | Rollback |
|---|---|---:|---|---|
| `Features__WritePublishedScopeSources` | `fstworker` | `false` | Backfills the current clean publication when needed, records scope coverage, builds the next mapping, and requires it in publication | Set `false`; incomplete candidates never move the global pointer |
| `Features__UsePublishedScopeSources` | `fstservice` | `false` | Resolves solo current reads, projection readiness, totals, member filters, and published solo exports from the current mapping | Set `false`; active-state/legacy resolver remains available |

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
| `leaderboard_entries` | Legacy mutable durable rollback source | Optional scrape dual-write | Legacy fallback only; not the preferred published model |
| `leaderboard_entries_snapshot` partitions | Durable physical source | Worker snapshot writer | Worker candidate reads use active state; service/exports use the mapped published snapshot after PG-1 cutover |
| `leaderboard_snapshot_state` | Source-selection metadata | Worker finalization | Active source, not automatically a published source |
| `leaderboard_scope_fingerprints` | Correctness/audit metadata | Worker observe/coverage dual-write | Content, reported entries/pages, completeness, source scrape, and published scrape must validate before publication |
| `leaderboard_published_scope_source` | Durable published source selection | Worker candidate build and publication transaction | Service and export resolver when `Features:UsePublishedScopeSources=true`; supports physical snapshot and explicit empty sources |
| `leaderboard_population`, `song_stats` | Durable derived metadata | Worker/post-process | Ranking totals/statistics; generation must match source |
| `leaderboard_entries_overlay` | Durable corrective overlay | Controlled writes | Merged with selected base source; precedence is explicit |
| `leaderboard_current_entries` | Experimental logical current source | Worker logical dual-write | Shadow until PG-4 parity promotes a read owner |
| `leaderboard_entry_versions` | Experimental logical history | Worker logical dual-write | Historical versions; semantic rank churn ownership unresolved |
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

PG-1 does not enable physical snapshot write skipping or change max-page,
deep-scrape, retry, or Epic request policy. Scope completeness means the
expected result was observed, page zero did not fail, every page in the
configured captured range completed, reported totals/pages were recorded, and
the selected physical row count exactly matches the fingerprint. Fingerprints
use the same highest-score-per-account deduplication as the snapshot primary
key, so duplicate API rows cannot make a valid physical source appear
incomplete. WORKER-0 continues the stricter request/failure-propagation work.

Mapped service reads use `current_leaderboard_entries` only for scopes whose
projection ledger matches the mapped physical/empty source. Mismatched or
failed scopes fall back individually to the mapped snapshot plus overlay, so a
single stale projection cannot force an all-instrument slow path or leak the
active candidate.

### Score, player, and solo ranking history

| Tables | Class | Owner/callers | Retention |
|---|---|---|---|
| `score_history` | Durable user-visible history | `MetaDatabase`, player/ranking services | Preserve score/rank/season/timestamp semantics; nullable-time uniqueness repair is PG-3/PG-7 |
| `player_score_observations` | Durable candidate/duplicate observation owner | Band and metadata writers; no confirmed production reader in the audit window | Ownership decision required before archive/drop |
| `player_stats`, `player_stats_tiers` | Derived projection | Player stats calculator/API | Rebuildable for a published generation |
| `account_rankings`, `account_ranking_stats` | Derived ranking projection | Rankings pipeline | Rebuildable; generation/source must remain auditable |
| `rank_history` partitions, `rank_history_latest`, `rank_history_snapshot_stats`, `rank_history_tracked_accounts` | Durable user-visible history plus latest projection | Ranking/history pipeline and API | Append only on meaningful change after PG-5 redesign |
| `ranking_deltas`, `ranking_delta_tiers`, `rank_history_deltas` partitions | Derived experimental projections | Rankings pipeline | Feature-flagged; rebuildable |
| `composite_rankings`, `composite_rank_history`, `composite_rank_history_latest`, `composite_ranking_deltas` | Derived current plus durable history | `MetaDatabase`, rankings API | Current/latest rebuildable; history retained by explicit policy |
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
| `band_current_projection_scope`, `band_current_projection_state`, `band_current_projection_source_state` | Publication/readiness metadata | Band projection builder | Published generation/readiness; source ownership must align with PG-1 |
| `band_search_team_projection`, `band_search_member_projection`, `band_search_projection_state` | Derived search projection | `BandSearchProjectionBuilder` | Service search/profile reads; rebuildable |
| `band_extraction_source_state` | Durable work/source metadata | Band extraction pipeline | Prevents ambiguous source generation |

### Band rankings and rank history

| Tables | Class | Owner/callers | Publication/retention |
|---|---|---|---|
| `band_team_rankings_current_band_*` | Derived current ranking projection | Ranking rebuild/table swap | Candidate current state |
| `band_team_rankings_published_band_*` | Derived published ranking projection | Publication transaction | Public ranking source and rollback target |
| `band_team_ranking_stats_current_band_*`, `band_team_ranking_stats_published_band_*` | Derived stats projection | Ranking rebuild/publication | Must promote with ranking rows |
| `band_team_ranking_generation` | Publication/audit metadata | Ranking pipeline | Tracks durable generation and source scrape |
| `band_song_team_rankings`, `band_song_team_rankings_current_band_*`, `band_song_team_ranking_state` | Derived song/team ranking projection | Ranking pipeline | Optional rebuild currently defaults off; stale fallback is explicit |
| `band_team_rank_history`, `band_team_rank_history_points`, `band_team_rank_history_latest`, `band_team_ranking_stats_history` | Legacy durable history/latest | `MetaDatabase`, history API | Retain until v2/read-source parity and restore prove removal |
| `band_team_rank_history_points_v2` partitions | Durable compact public history | History worker through `MetaDatabase` | About 799 GiB; archive/prune only by exact range manifest |
| `band_team_rank_history_latest_v2` partitions | Derived latest delta state | History worker | Rebuildable from retained history/current generation if proven |
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
| `player_improvement_state`, `player_rank_improvement_state`, `band_improvement_state`, `band_rank_improvement_state`, `band_improvement_subjects` | Durable detection state | Improvement detector | Idempotency/delta state |
| `player_improvement_events`, `band_improvement_events`, `improvement_detection_runs` | Durable event/audit | Improvement detector/service | Bounded retention with replay identity |
| `service_notifications` | Durable notification outbox/read model | `ImprovementNotificationService` | Expiry cleanup is bounded; future process split must preserve replay |
| `api_response_cache`, `api_response_cache_staging` | Cache | Precompute/publication path | Staging swaps atomically; safe to clear and regenerate from published source |

### Dirty, shadow, and audit-only surfaces

`scrape_dirty_account`, `scrape_dirty_song_instrument`,
`scrape_dirty_band_scope`, `scrape_dirty_band_team`,
`post_scrape_shadow_run`, `post_scrape_shadow_metric`,
`invalid_leaderboard_shadow_observation`, and
`notification_cleanup_audit_20260509` are work/audit surfaces. They must have a
named owner and bounded retention before cleanup. Zero current rows or zero
`pg_stat` scans is not sufficient evidence for deletion.

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
7. `composite_rank_history` intentionally has no `ix_crh_latest` secondary
   index. The full latest-state snapshot job uses a parallel sequential
   scan/sort plan at production scale, while the primary key preserves
   identity and account-bounded date access. `ix_crh_retention_cutoff_account`
   remains the owned date-first path for bounded retention.

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

`/api/service-info` reads latest scrape, published scrape, publication/freeze,
and worker operation state in one PostgreSQL statement. It exposes active and
published scrape IDs, freeze reason/ID, network/post-process/publication
phases, failed or stalled states, and only a future scheduler timestamp.
Worker heartbeats refresh the active operation timestamp and elapsed time
during long phases.

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
- `tools/postgres-capacity-guard.sh` is required before broad scrape,
  post-process, optional build, or maintenance work. It records free space,
  DB/WAL size, scratch, publication state, locks, and active maintenance.
- Use its `reclaim` action only for a proven space-releasing operation with
  zero transient/scratch bytes. It still requires one full-scrape emergency
  buffer and no active vacuum/index/rewrite/ungranted-lock conflict.
- `VACUUM FULL`, broad table rewrites, large non-concurrent index builds, and
  unbounded `pg_repack` are prohibited at current headroom.
- Archive/prune operations require exact object/range manifests, checksums,
  rehydration, live-scrape parity, rollback, and post-action route validation.

## Backup, restore, and rollback

### Current promotion gate

A full 3.56 TB duplicate restore is not safe with roughly 315 GB free. Full
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
  redundant `ix_crh_latest` index was retired with exact plan/route/export
  parity. Member facts, observation-table dual writes, and nullable
  score-history uniqueness remain explicit owner decisions.
- **PG-4 / WORKER-4:** semantic-change writes, unchanged physical source reuse,
  diff projections/rankings, and one atomic band publication.
- **PG-5:** latest-state history design, explicit retention, and same-drive
  Parquet/DuckDB artifact pilots.
- **PG-6 / SERVICE-4:** versioned migrations, one migration owner, autovacuum,
  work-memory, WAL/checkpoint, and recovery governance.
- **PG-7:** parity-gated object-by-object archive and reclaim.

These are tracked changes to this design, not permission to bypass its current
source, publication, same-drive, restore, or parity rules.
