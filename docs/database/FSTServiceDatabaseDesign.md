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

The repository now removes the retired logical writer/config/rollback surface
and stops creating the logical current, version, and metrics schemas during
startup. Tracked role and runtime configuration no longer expose the retired
flag. No live objects are dropped by this code change: the empty current/
version families and 108-row metrics table await cleanup-image full-scrape
parity before a separate physical-schema cleanup.

The repository also removes both retired `player_score_observations` writers,
their tracked configuration keys, and startup creation of the table, unique
source index, union view, primary key, and sequence. Fresh schemas therefore
contain no observation objects while the durable `score_history` and band
fact/statistic owners remain unchanged. This code change performs no live DDL:
the already-empty production table,
`player_score_observation_union`, indexes, primary key, and sequence await a
cleanup image and successful full-scrape publication/public-fingerprint
parity before the checked-in drop SQL may remove them. The exact rehydrate,
truncate, drop, and execution evidence remain retained.

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

Band-history API status follows the active read path rather than the retired
writer schema. A ready, enabled compact-v3 source obtains
`historyComputedThrough` from
`band_rank_history_compact_v3_state.max_snapshot_date`; otherwise the
configured v2/legacy source supplies the date. Because snapshots use UTC
calendar dates, enabled history is `current` only when that date equals the
UTC date of the current ranking `computed_at`; older or mismatched history is
`stale`. Background `queued`/`running`/`paused` work remains `catching_up`,
and `failed` remains `failed`. When `BandRankHistory:Mode=Disabled`, the API
reports `disabled` while retaining the current-ranking timestamp,
history-through date, latest job timestamp, and a read-only-history message.

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

Every solo rank producer and equivalent filtered-read baseline uses the same
total order: `score DESC`, `COALESCE(end_time, first_seen_at::TEXT) ASC`,
then `account_id ASC`. The stored-rank switch remains default-off until
PostgreSQL parity covers exact score/timestamp peers, threshold `-1`/exact/`+1`
boundaries, rank/page boundaries, current and reused published sources,
source-mismatch fallback, explicit empty scopes, and player, member, and
leaderboard API reads.

The rollout proof is implemented by `tools/FstStoredRankRollout` and
`tools/postgres-stored-rank-rollout.sh`; operator procedure and acceptance are
in `docs/database/StoredRankFilteredReadsRolloutRunbook.md`. The C# harness
calls the real `InstrumentDatabase` readers rather than maintaining copied
baseline/candidate SQL. Manifest selection uses the current complete published
source CTE and records projection-generation/source guards, active overlays,
selected accounts, ties, thresholds from the same `PathDataStore` reader used
by APIs, and all nine instruments. The guard is rechecked before every measured
block; both page offsets must return non-empty ranks 100/101, cold and warm
resource regressions are gated independently, and PostgreSQL counter resets
reject the run. Bounded service-health curl deadlines preserve automatic false
rollback; the marker clears only after verified false service/worker state.
Only container samples overlapping request windows are accepted, and API
parity requires explicit successful statuses rather than equality of matching
failures. Qualification uses the actual sample observation time, not Docker
process lifetime. Overlay evidence must traverse a source-matched current/
reused candidate row, threshold evidence must prove real boundary transitions,
and all post-mutation Docker operations are deadline-bounded/cancellable.
Zero-baseline resource increases are represented as nullable finite JSON and
reject automatically. One reviewed tag+digest/image ID is embedded in the
manifest and enforced for every service recreate and rollback. Full-run outputs
are rejected outside the configured 4 TB evidence tree. The manifest also
binds the exact mounted source/filesystem, a global lock spans mutation through
rollback, per-block identity/state checks detect external redeploys, health
requires exact HTTP 200 plus valid publication/worker JSON, and acceptance is
finalized only after verified false rollback.

Stored-rank rollout recreates use `Scraper:RolloutReadOnlyStartup`. That mode
opens existing instrument readers and loads persisted catalog/item-shop state
without schema DDL, startup cleanup, provider/image sync, item-shop refresh or
timers, or mutation-capable hosted services. It is service-only and defaults
off, so normal API and worker startup behavior is unchanged.
The rollout always follows its read-only false rollback with a second
digest-pinned recovery recreate that restores
`RolloutReadOnlyStartup=false`; final acceptance requires that normal-mode
state and health.
Recovery captures exactly one post-recreate service container ID before any
normal-state check. All image/env/direct-port/hostname/nonce/health checks and
the final recheck remain pinned to that ID; health probing cannot adopt a
concurrent replacement. Marker clearing occurs only after the complete pinned
verification succeeds. Recovery/final quiescence and role-evidence functions
also require that same ID before, during, and after persistence. A replacement
during evidence collection keeps the marker armed and returns to recovery.
Runtime recovery is unconditional even if evidence storage fails. The
mutation marker is cleared only from verified normal state, while missing
rollback/recovery evidence still rejects the run and cannot cause the EXIT
trap to reinstall read-only mode.
Recovery evidence records freshly observed container IDs, image, flags, and
health rather than expected constants. The same runtime state is rechecked
immediately before acceptance, so same-image external recreate drift cannot
pass.
The direct benchmark/API endpoint is derived from the inspected service
container's sole loopback `8080/tcp` host binding. Service-info exposes a
per-host-instance nonce, container hostname, process ID, and start time. Every
recreate binds that nonce/hostname to the captured container ID; the web proxy
is checked separately and cannot substitute for direct endpoint identity.
The final DB-quiescence report is hashed before a fresh final runtime capture;
acceptance embeds both and is published with an atomic rename.
`fstworker` must be stopped and is separately manifest-pinned by container ID,
image, full Docker state timestamps/restart count, and false role flags. The
durable worker ledger must be offline/stale with no active connections or
jobs; any worker start/restart or role drift invalidates the service-only
experiment while normal service recovery remains mandatory.
Hashed DB-quiescence checkpoints cover every block, both recovery phases, and
pre-acceptance, rejecting worker application sessions and granted advisory
mutation leases such as path-generation admission.
The evidence connection remains non-superuser/select+temp-only but must be a
effective `USAGE` holder of `pg_read_all_stats` or `pg_monitor`; `MEMBER`
through a `NOINHERIT` chain is insufficient. Validation opens a controlled
session as the production service role and directly proves cross-role
`pg_stat_activity` user/application/query visibility before evidence queries.
The manifest additionally binds the evidence database name, PostgreSQL
`system_identifier`, server address/port and socket directories to the
sanitized effective service target and the production Compose Postgres
container ID, image, one shared network ID, addresses, and the service host
alias exclusively owned by that container. Every active endpoint on that
network is inspected across Docker `Aliases`, `DNSNames`, and normalized
container names, so a name-resolvable stale clone fails closed. The identity is
reattested immediately before every request block and in every hashed
quiescence checkpoint. Alternate databases, clones, `POSTGRES_CONTAINER`
mismatches, service-target drift, or container/network drift fail closed.
`/api/service-info` exposes only host, port, database, username, and the
read-only-option boolean; passwords and raw connection strings are never
reported.
`StartupInitializer` reads the actual `default_transaction_read_only` setting
before all startup modes. Rollout mode requires `on`; normal startup and final
recovery require `off`, and service-info reports the observed value.
Rollout service Npgsql sessions also force `default_transaction_read_only=on`.
Request middleware rejects mutating and
mutation-on-GET paths, suppresses selected-profile activity persistence, and
surfaces any read-only SQL violation through unhealthy readiness.
Mutation-route matching canonicalizes trailing slashes and uses consistent
case-insensitive comparisons.
Filtered player/member rank and population readers no longer create temporary
threshold tables. They bind deterministic parallel song/score arrays through
materialized `unnest` CTEs; the player valid-score and last-played helpers use
the same typed song/instrument/score staging. This preserves score filtering
and total ordering while remaining valid under
`default_transaction_read_only=on`. Nested or aggregate PostgreSQL `25006`
errors are unwrapped by the request guard and returned as no-store HTTP 503.

The candidate compose override sets only `fstservice` true and explicitly sets
`fstworker` false. The baseline/rollback override sets both false and recreates
only `fstservice`. Publication mapping, freeze/unfreeze behavior, score/leeway
filtering, and worker post-process reads are unchanged.

Tracked Compose templates load non-secret role defaults from
`deploy/config/fstservice-role.env` and
`deploy/config/fstworker-role.env`. The API role reads published solo sources
but never writes them. The worker writes and validates candidate source maps
but never resolves post-process reads through the prior publication. Automatic
path generation and snapshot reuse stay false in both roles. Worker schema
initialization is skipped only after the API role has initialized the schema.

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
`paths/<song-id>/<instrument>/<difficulty>.*` read layout. For a non-null
pointer, instruments claimed by the immutable generation never fall back to
legacy or stale files; instruments outside that generation's expected set may
continue to use their legacy artifacts. Promotion does not alter
scrape IDs, publication pointers, public-read freeze state, rankings, history,
or notification delivery. The additive columns and error table are
idempotent; deployment still follows the explicit schema-initializer hold
described above, with normal lock/long-query checks before the initializer.
Deploy first with automatic generation disabled, prove legacy reads and the
single-song admin guard, then enable automatic new/changed atomic-song work.

The one-time exact-four Pro Lead repair has completed and its executable
extension is retired. Publication `1276` is current; four immutable generations
were promoted, the dependent rankings were rebuilt, and the sole notification
maintenance run recorded `26` quarantined candidates with `0` visible
deliveries. The compiled song allowlist, repair manifest/runtime services,
repair-specific lease, selective ranking adapter, executable command parser,
and DI registrations no longer exist. A startup denylist recognizes every
retired command/argument form and aborts before hosted-worker mode selection,
including double-dash, single-dash, slash-prefixed, and bare `key=value`
forms, so stale operator automation cannot fall through to a normal scrape.

Recurring path generation continues through the worker and the protected
single-song admin endpoint. It retains provider timestamp normalization,
full decrypt/CHOpt/runtime/artifact validation, immutable generation moves,
per-song in-process serialization, row-locked revision/catalog comparison, and
the database CAS. The four promoted partial generations remain valid: their
generation pointers serve the generated Pro Lead artifacts while instruments
not owned by those generations continue to resolve their legacy artifacts.

Every recurring path-generation batch first enters a provider-local semaphore
before opening a dedicated `Pooling=false` PostgreSQL session, then acquires
the generic session-scoped advisory lock `5067481511116519000`. Admission
therefore consumes no shared-pool slot; the local gate limits each
service/worker process to one dedicated session while the PostgreSQL lock
serializes across processes. Both are held for the full batch before runtime
identity detection or CHOpt work. Explicit unlock is checked, but physical
session close is the fail-safe for cancellation races, broken sessions, or an
ambiguous unlock result. Cancellation or acquisition failure remains visible
through normal `path_generation_errors`. Runtime-identity and state-read
failures release admission before recording batch errors, and the complete
batch shares one five-second best-effort error-write budget so a large failed
request cannot retain or replace the global lease with unbounded diagnostic
work. After an explicit CAS
conflict or missing-song result, the rejected immutable generation directory
is deleted only after a fresh database read proves that generation is not the
current pointer. An ambiguous state read or a pointer to that generation keeps
the directory, preferring safe retention over deleting a winner.

The historical rollback snapshot and command reports remain evidence rather
than executable input. A future reversal requires a separately reviewed
public-read-frozen transaction restoring all captured path fields, followed by
a supported full ranking recompute and identity validation. The API retains
same-publication cache/client refresh compatibility for the historical ranking
maintenance freeze reasons, so an interrupted old-image freeze can still be
released without serving pre-freeze process or song-cache state.

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
| `leaderboard_current_entries` | Empty retired logical current schema | None; writer and startup creation removed | Never authoritative; rows truncated 2026-07-28, primary-key family retained, dormant rank/change secondary trees retired; rebuild semantic current from the published physical map only after an explicit future migration/promotion |
| `leaderboard_entry_versions` | Empty retired logical chronology schema | None; writer and startup creation removed | Non-authoritative scrape `1223`-`1237` chronology intentionally discarded 2026-07-28; primary-key family retained and dormant open/from-scrape secondary trees retired |
| `leaderboard_logical_write_metrics` | Retained audit artifact | None; metrics writer and startup creation removed | Historical 108-row evidence remains until cleanup-image full-scrape parity permits physical cleanup |
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

Band best/worst-song and `/song-rows` public reads derive from
`current_band_leaderboard_entries` rows joined to each scope's
`published_generation`. The disabled optional band-song ranking writer, its
legacy read helper, tracked rebuild configuration, maintenance ownership, and
startup schema/index creation are removed. Fresh schemas therefore exclude
`band_song_team_rankings`, `band_song_team_ranking_state`,
`band_song_team_rankings_current_band_duets`,
`band_song_team_rankings_current_band_trios`, and
`band_song_team_rankings_current_band_quad` while retaining the live
`band_current_projection_scope` path. Existing physical copies of those exact
retired relations, their indexes, and TOAST objects remain empty until a
cleanup image completes one full scrape with publication and public-fingerprint
parity; this repository change performs no live DDL.
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
| `score_history` | Durable user-visible history | `MetaDatabase`, player/ranking services | Preserve score/rank/season/timestamp semantics. The explicit audited PG-3/PG-7 maintenance command promotes `ix_sh_dedup` to five-column `UNIQUE ... NULLS NOT DISTINCT` and permits only contract-v2 null-to-one-known `difficulty`/`season` enrichment; no row cleanup runs at startup. |
| `score_history_dedup_maintenance_runs`, `score_history_dedup_original_rows` | Immutable maintenance audit/restore source | Explicit `ScoreHistoryDedupMaintenanceService` CLI only | Retention-independent. Stores non-null CLI/database/digest/index provenance and every affected original row before merge/delete; triggers reject update, delete, truncate, and post-seal original-row append. |
| `player_score_observations` | Empty production rollback schema; absent from fresh schemas after writer/schema-creation retirement | None; solo-history and band-member writers, tracked config, and startup creation are removed, with no production reader | OBSERVATION-RETIRE truncated `10,167,937` rows after scrape `1267` parity, reclaiming `12,682,330,112` database bytes. Existing table/view/index/primary-key/sequence objects await cleanup-image full-scrape parity; exact rehydrate/drop SQL remains retained |
| `player_stats`, `player_stats_tiers` | Derived projection | Player stats calculator/API | Rebuildable for a published generation |
| `account_rankings`, `account_ranking_stats` | Derived ranking projection | Rankings pipeline | Rebuildable; generation/source must remain auditable |
| `rank_history` partitions, `rank_history_snapshot_stats`, `rank_history_tracked_accounts` | Durable user-visible history and snapshot metadata | Ranking/history pipeline and API | Append only on meaningful change after PG-5 redesign |
| `rank_history_latest` | Empty obsolete latest projection schema | No current exact caller | ORPHAN-RECLAIM truncated stale rows; deterministic rebuild from retained `rank_history` |
| `ranking_deltas`, `ranking_delta_tiers`, `rank_history_deltas` partitions | Empty retired aggregate-leeway projection schemas; absent from fresh schemas | None; compute/read/persistence code, flags, DTOs, and startup DDL are removed | Existing physical relations remain rollback-only until cleanup-image full-scrape parity |
| `composite_rankings`, `composite_rank_history` | Derived current plus durable history | `MetaDatabase`, rankings API | Current rankings rebuildable; history retained by explicit policy |
| `composite_ranking_deltas` | Empty retired aggregate-leeway projection schema; absent from fresh schemas | None; compute/persistence code and startup DDL are removed | Existing physical relation remains rollback-only until cleanup-image full-scrape parity |
| `composite_rank_history_latest` | Empty obsolete latest projection schema | No current exact caller | ORPHAN-RECLAIM truncated stale rows; deterministic rebuild from retained `composite_rank_history` |
| `solo_family_rankings`, `combo_leaderboard`, `combo_stats` | Derived ranking projections | Rankings pipeline/API | Rebuildable from published solo current state |
| `combo_ranking_deltas` | Empty retired aggregate-leeway projection schema; absent from fresh schemas | None; compute/persistence code and startup DDL are removed | Existing physical relation remains rollback-only until cleanup-image full-scrape parity |

The 2026-08-04 aggregate ranking-delta retirement removes all runtime and
startup ownership for the five empty relation families above. Operator-supplied
catalog evidence records zero rows across all 32 physical relations and keeps
the same-drive catalog baseline, rollback DDL, and checksums under
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/branch-cleanup-20260803/ranking-deltas/`.
This repository change executes no live DDL: the physical relations stay in
place until a cleanup image completes one full scrape with publication and
public-fingerprint parity. Their later cleanup is a separate explicit
object-by-object `DROP` without `CASCADE`.

That later action is now packaged, but not executed, in
`tools/postgres-retired-schema-cleanup.sh`. The 61-row allowlist also includes
the independently retired logical shadow, observation, and optional band-song
families. Check mode hashes exact relkinds, owners, bounded exact-zero probes,
the complete attached partition set, canonical payloads for the retained 108
logical-metric and 3 band-state rows, complete column/catalog signatures,
owned objects, external dependencies, verified repository and sanitized
production-compose/bind source roots, bounded rollback/psql processes,
cleanup-image identity, publication state, and the exact 13-fingerprint
leaderboard/player/ranking/band public suite. Account and team samples are
resolved from the captured ranking lists and then bound into the manifest.
Execute uses one all-family transaction, cooperative DDL/sequence advisory
guards, signature checks after all locks and immediately before the first drop,
the exact ordered compose-label file set for every render/run, a
compose/runtime-attested production PostgreSQL container and cluster target,
and PID/application-name-controlled timeout-state reconciliation. It still
forces local-socket libpq access, rejects all unexpected incoming inheritance
parents and non-roundtrippable catalog states, and requires a same-cluster
pre-destructive scratch restore/signature proof. Target RLS/forced-RLS must be
off, probes force `row_security=off`, and the pinned role must be superuser or
`BYPASSRLS`. Fsynced launch/connect/post-connect barriers record local,
container, and backend identity before the destructive client receives SQL.
Container
discovery requires executable `psql` plus an exact application-name argv token,
so scanner/control processes cannot self-match. It still requires accepted
scrape-`1278` publication/unfreeze parity and an identical manifest. See
`docs/database/RetiredPhysicalSchemaCleanupRunbook.md`.
After scratch restore proof, the connected destructive client remains blocked
while a second complete HTTP/capacity/container/publication/lock/catalog/
retained-data/target gate is recaptured and matched to that manifest.
Container validation includes secret-sanitized actual command, relevant
environment hashes/presence, mounts, networks/IP aliases, and Compose labels.
The service's password-free database target must resolve through a shared
network alias to the same Postgres container/system identifier used by
maintenance.
All running endpoints on each shared network are enumerated; the configured
database alias must be owned exclusively by that attested container.
The operator-approved manifest is SHA-verified and sealed after the last gate;
its captured drop hash controls a separately sealed destructive-SQL memfd.
Neither manifest nor SQL pathname is reopened before streaming.
Post-drop `--initialize-schema-only` uses an immutable manifest image ID, not
the mutable Compose tag, and inspects the temporary container's actual image
before accepting the startup check.
Raw rollback dumps remain immutable evidence; executable rollback copies retain
their random restriction keys while replacing only pg_dump's zero timeout
preamble with bounded statement/lock/idle/transaction values.
Its capacity gate clears inherited policy overrides, passes the accepted
zero-reclaim/zero-scratch emergency-window constants explicitly, and
manifest-binds the effective policy plus guard-script SHA-256.

Canonical `account_rankings`, base `rank_history`, `composite_rankings`,
`composite_rank_history`, solo-family rankings, combo rankings, and all band
ranking/history change detection remain active. Score `minLeeway` filtering,
valid-score fallbacks and local filter tiers, population tiers/rank offsets,
`player_stats_tiers`, stored-rank filtered reads, rival `rankDelta`, and API
score filtering are separate mechanisms and are not retired.

Solo-family ranking denominators are selected identically by runtime and the
one-shot backfill. For each instrument, the effective denominator is the
greater of the supplied exact catalog chart count and the maximum canonical
`account_rankings.total_charted_songs`; a family denominator is the sum of its
effective instrument values. This preserves retained valid canonical scores
when the current catalog has fewer chart properties, without capping songs,
full combos, or total score.

Every produced family row is rejected before replacement if songs played or
full combos exceed the family denominator, or if coverage/FC rate exceeds
`1 + 1e-9` or is non-finite. Runtime logs deterministic catalog/canonical/
effective overrides. The standalone `--solo-family-ranking-backfill` command
returns the same evidence as JSON and uses the existing transactional
`TRUNCATE`/binary-`COPY` replacement only with explicit
`--solo-family-ranking-backfill-execute`.

The command takes the global publication advisory key by try-lock, holds the
canonical `account_rankings` source under a bounded share lock, holds the
publication-state row against freeze/pointer changes, requires no active scrape
or worker operation, no working publication, unfrozen reads, a stable current
generation/completed published scrape, and an exact ready published catalog.
It initializes no schema and starts no hosted workers. Because
`solo_family_rankings` is unversioned, live execute still requires worker and
service quiescence (or separately proven bounded table-lock behavior). Scrape
`1277` failed publication on the former denominator mismatch and is not
republished by maintenance; a later full scrape must prove publication. See
`docs/database/SoloFamilyRankingBackfillRunbook.md`.

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
| `band_song_team_rankings`, `band_song_team_ranking_state`, `band_song_team_rankings_current_band_duets`, `band_song_team_rankings_current_band_trios`, `band_song_team_rankings_current_band_quad` | Retired optional song/team ranking projection objects; ranking rows are empty, while the state ledger retains 3 rebuild rows; absent from fresh schemas | None; writer, legacy reader, config, maintenance ownership, and startup creation are removed | Existing physical objects await cleanup-image full-scrape parity; cleanup must hash and preserve the 3 state rows; public reads use published current-band rows or fail closed |
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
| `improvement_notification_maintenance_runs`, `improvement_notification_maintenance_candidates` | Immutable historical audit/quarantine compatibility | No executable writer; schema retained by `ImprovementNotificationSchema` | The completed purpose `maintenance_pro_lead_max_score_repair_v1` run stores its exact manifest, total-charted count, canonical classification, and `26` quarantined candidates with `0` visible deliveries. `published_scrape_id` is a non-null immutable integer with no retention-coupled `scrape_log` FK. Rows have no expiry column and never participate in public reads, routine supersession, source cursors, or WebSocket invalidation. |
| `api_response_cache`, `api_response_cache_staging` | Cache | Precompute/publication path | Staging swaps atomically after long band snapshot work; keep its exclusive lock at transaction end; safe to clear and regenerate from published source |

Notification recovery and registered-phase budget operations are documented in
`docs/database/ImprovementNotificationRecoveryRunbook.md`. The protected
`/api/diag/improvement-notifications` endpoint and API-side staleness monitor
surface pending/failed publication markers, scrape lag, and time lag without
changing public response contracts. Recovery reads
`improvement_notifications_projection_scopes` from the publication ledger and
fails closed when the plan is absent or not ready; it never substitutes an
all-current-scope rebuild implicitly.

The one-time Pro Lead notification maintenance writer is retired after the
publication `1276` execution persisted `26` quarantined candidates and no
visible event. The hardcoded manifest and dry-run/execute services are absent,
and routine recovery cannot reopen a completed marker for rebaselining.

The safety boundary remains durable. Existing and fresh schemas retain the two
immutable maintenance audit tables and their purpose/cause/quarantine/zero
visible-delivery constraints. Public event reads, source cursors, expiry, and
supersession continue to require `delivery_state='visible'`, so the retained
historical rows cannot enter a feed or alter routine event lifecycle. This code
retirement performs no live DDL and does not delete or rewrite audit rows.

The nullable `score_history` repair is a separate explicit one-shot safety
gate. `--score-history-dedup-maintenance` defaults to a canonical
`REPEATABLE READ`, `READ ONLY` dry run. Contract version `2` keeps the existing
operation purpose but changes the digest contract. Its digest binds sorted
original rows, per-group selected survivor/rank/time/difficulty/season values,
the deterministic merge contract, the exact allowed-null-enrichment list, and
the structured current index state while excluding transaction/report clocks,
planner estimates, and relation sizes. Reports include total/null/duplicate
rows, groups/excess, affected account/song IDs, per-group maxima, selected
metadata, actual enrichment/conflict fields, classification counts,
table/index sizes, and exact merge semantics.

Execute additionally requires `--score-history-dedup-execute` and
`--expected-score-history-dedup-digest`. It uses `SET LOCAL` for the
three-second lock and 180-second statement timeouts, acquires the
`SHARE ROW EXCLUSIVE` lock on `score_history` before any snapshot-establishing
`SELECT`, then takes the transaction advisory lock. Consequently, a writer
that commits after transaction start but before table-lock acquisition is part
of the locked repeatable-read snapshot and either changes the digest or is
included in the analyzed candidate state.

Before candidate reads, the command also verifies the exact release-owned
audit catalog: tables/columns/defaults, validated constraints, immutable
function bodies and enabled triggers, digest index shape, and run-ID sequence.
It fails closed when any object is absent or inexact and never initializes or
repairs schema. `--initialize-schema-only` remains the normal release owner of
the entire schema and sequence advancement; its short audit-schema step widens
the contract constraint to preserve version-1 runs while accepting version 2.
It is not a maintenance prerequisite.

After the locked re-read verifies the digest, any duplicate with
`new_score != 0`, non-null `score_achieved_at`, variation in `old_score`,
`old_rank`, `accuracy`, `is_full_combo`, `stars`, `percentile`, or
`season_rank`, or more than one distinct non-null `difficulty`/`season` value
blocks before writes. A passing transaction stores every original row,
preserves the lowest ID, earliest `changed_at`, and minimum positive/non-null
ranks, selects the single known difficulty/season value (or null when all are
null), updates the survivor, deletes only audited non-survivors, then builds
the PostgreSQL 17 `NULLS NOT DISTINCT` replacement under a temporary name.
Reads remain available during the index scan/build; the final old-index
drop/new-index rename creates a brief `ACCESS EXCLUSIVE` pause immediately
before commit. Existing five-column `ON CONFLICT` paths therefore become
null-safe without query-specific predicates.

The supplied 2026-08-04 contract-v1 dry run recorded `763,908` total rows,
`1,632` null timestamps, and `324` duplicate groups / `1,398` rows / `1,074`
excess. Contract v1 accepted `122` rank-only groups and blocked `202` solely
for null-to-one-known difficulty/season variance (`151` difficulty, `46`
season, `5` both). Every group is zero-score/null-timestamp, both fields have
at most one distinct non-null value, and no other invariant varies. This is
read-only classification evidence, not execute authorization; two matching
accepted contract-v2 dry runs and the runbook maintenance gate remain required.

The immutable run stores executable rollback SQL. Rollback verifies the target
index and unchanged merged survivors, including selected difficulty/season,
proves every audited non-survivor ID is still absent, then drops the
nulls-not-distinct index, restores exact audited originals, recreates the
legacy ordinary unique index, and advances the sequence without rewinding it.
A reused explicit ID or later survivor metadata write fails before any delete;
unrelated later rows remain untouched. Re-execution after a rollback creates a
new immutable audit run. Contract-v1 digests/runs cannot satisfy version-2
execution. Full commands, bounded catalog preflight, and lock/runtime planning
are in
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
    `ix_lev_from_scrape` tree. Its writer, rollback path, runtime/config flag,
    and startup schema creation are removed, and there is no runtime reader.
    Existing physical primary-key constraints remain until cleanup-image
    full-scrape parity clears their separate drop; exact child-concurrent,
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

PUB-CONTRACT/PUB-READINESS adds a fail-closed decision layer without changing
schema, readers, publication promotion, or production configuration:

- contract version `1` maps all 55 current `PublicationBound` route
  definitions to immutable route-family descriptors and the named surfaces
  `account_names`, `account_overlays`, `band_rankings`, `history`,
  `improvement_notifications`, `item_shop`, `path_artifacts`,
  `solo_scope_sources`, and `song_catalog`; `api_response_cache` is a required
  pinning-infrastructure surface;
- startup/tests reject duplicate contracts, unmapped publication-bound routes,
  stale contracts, unknown families, unknown surfaces, duplicate surface
  requirements, and family drift. Operational and private routes remain
  explicitly outside this contract; no publication-bound route has an
  independent/live exemption;
- readiness requires the current generation to exist, match the published
  scrape, remain `current`, and carry source-cut/ready/published timestamps.
  Every required binding must be unique, `ready`, use an allowed kind, carry
  contract version `1`, match its publication/scrape/source identity, and
  provide the row count or content hash promised by that binding type;
- source existence/count/hash is rechecked for generation cache rows, exact
  song-catalog rows, retained published solo scope mappings, and the current
  published band projection. Candidate-mutated latest fingerprint rows are
  not used to invalidate the still-current generation. Missing or retired
  sources fail closed;
- existing binding producers intentionally do not receive synthetic contract
  readiness in this phase. In particular, `item_shop` and `path_artifacts`
  remain `legacy_live_unversioned`/`building`; existing catalog, cache,
  scope-map, band, and notification bindings also lack the full versioned
  route-reader contract required to activate pinning;
- `/api/publication` now returns additive `contractVersion`,
  `readyForPinning`, effective `pinningEnabled`, and deterministically sorted
  `unreadySurfaces`/reason arrays. The web client must add these fields to
  `FortniteFestivalWeb/src/api/client.ts` in a non-conflicting follow-up;
- when configuration remains false, request behavior is unchanged. If it is
  set true while the current generation is unready, publication-bound pinned
  requests return the existing problem-details `503`; a stale requested ID is
  still evaluated first and returns `409`. `/api/publication` and operational
  health routes remain available to explain the block.

This is continuous-safe readiness code only. No production deployment,
restart, live probe, database mutation, schema migration, reader cutover, or
flag enablement is part of the phase. Shop, path, overlay, history, names, and
remaining reader source cuts are still required before
`Features:EnablePublicationReadContext` can be enabled.

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
- controlled precompute stores canonical `public-route:` aliases for
  per-instrument ranking page-one payloads. Query parameters are sorted before
  keying, so equivalent request orderings share one exact generation entry.
  These aliases make commit-intent hits bypass both publication lock
  middlewares, while uncached pages still receive bounded `503` responses;
- publication retains exact current and previous cache generations only;
  failed/retired staging is deleted immediately and older cache bindings are
  marked retired;
- a prepared generation cache is authoritative across the final pointer
  commit even if the process stops before the legacy compatibility mirror is
  refreshed. Startup may reconcile an authoritative generation from the
  legacy table only when the legacy rows are newer than the generation
  binding, preserving explicit rollback-writer compatibility without
  replacing a newly committed generation with the prior publication;
- watchdog recovery marks the generation failed, clears the working pointer,
  and deletes its keyed staging.

### Bounded publication prepare/commit protocol

`PUB-COMMIT-SPLIT` replaces the transaction-wide exclusive publication
section that caused the scrape `1278` REST outage:

1. `PrepareScrapePublication` takes only a nonqueueing shared advisory lock.
   It validates the completed scrape, catalog and scope-source cut; copies and
   indexes deterministic publication-ID band candidate tables; populates the
   immutable generation cache; calculates cache count/hash bindings; and
   marks the generation `ready`. The current pointer, canonical published band
   aliases, unfreeze state, and published fingerprint ownership are unchanged.
2. The worker records durable freeze reason `publication-commit`. Cacheable
   requests execute an exact current-generation cache lookup before either
   publication read-lock middleware. Exact hits remain HTTP `200` without a
   shared advisory lease with publication pinning either disabled or enabled;
   pinned hits still validate the requested publication ID and complete
   readiness contract. Uncached publication-bound requests return
   `503 Retry-After: 1` during this drain instead of entering PostgreSQL's
   advisory-lock wait queue. Health, shell, operational routes, and existing
   WebSockets remain outside that boundary.
3. Commit intent is an exception-safe owned lease. Its dedicated
   `publication_commit_intent_started_at`,
   `publication_commit_intent_heartbeat_at`, and
   `publication_commit_intent_owner` fields are stamped on every transition;
   they never inherit the hours-old scrape-freeze timestamp. The active owner
   refreshes its heartbeat before each nonqueueing lock attempt, and a second
   fresh owner cannot overwrite it. Every noncommitted return,
   advisory drain timeout, relation-lock `55P03`, cancellation, and arbitrary
   finalization exception conditionally restores the exact pre-intent freeze
   state. An already-published retry clears a matching obsolete intent.
   Startup and the read gate reconcile intents only when the dedicated
   heartbeat is genuinely older than the configured threshold and after a
   nonqueueing exclusive-lock proof; retry gaps retain a fresh heartbeat and
   an active commit is never cleared. A stale working candidate is marked
   failed, its pointer is cleared, and normal failed-candidate isolation takes
   ownership.
4. `CommitPreparedScrapePublication` opens a fresh short transaction for each
   attempt and uses `pg_try_advisory_xact_lock`. A rejected attempt rolls back
   immediately and retries outside the transaction. Once acquired, finite
   relation and statement timeouts plus PostgreSQL 17
   `transaction_timeout` enforce one cumulative remaining cutover budget
   across all statements and retry attempts. Only `55P03` lock rejection and
   `40P01` deadlock are retryable; statement cancellation `57014` fails the
   candidate immediately. The transaction revalidates the
   pointer/generation/source cut, renames prebuilt band tables, stamps
   published fingerprints, rotates current/previous/working pointers, queues
   the persisted notification plan, and unfreezes atomically.
5. The old canonical band tables are renamed to deterministic retained names
   owned by the previous publication. Exact previous generation cache rows
   and catalog payloads remain retained. Post-commit cleanup refreshes the
   legacy cache mirror and retires only objects older than current plus
   previous, outside the exclusive lock.
6. Cache inheritance never substitutes a nonempty legacy compatibility table
   for an empty current generation. That inconsistent state blocks
   preparation and requires explicit reconciliation, preventing post-commit
   cleanup from truncating the only remaining cache rows.
7. Preparation or final-transaction failure leaves the old publication
   authoritative. `FailScrapeRun` first durably marks the scrape and
   generation failed without waiting on advisory readers. It then prefers the
   nonqueueing exclusive lock, but after the normal short drain budget may
   take a compatible shared recovery lock to clear the failed working pointer.
   A hung reader therefore cannot wedge future publication, while the normal
   successful commit path never queues an exclusive waiter. If pointer cleanup
   is interrupted, startup/next-prepare sweeping clears any working pointer
   whose generation is already failed. If even durable failure recording
   throws, the worker persists `publication-isolation-pending` when possible,
   keeps process caches and public reads frozen, and explicitly suppresses the
   normal `ScrapeFailed` unfreeze. The read gate treats that state as
   cached-read-only and reconciliation completes failure isolation before
   clearing it.
8. Prepared and retained band artifacts use only the validated
   `btr[s]_(pubprep|retained)_<publicationId>_<bandType>` naming contract. An
   idempotent sweeper holds a shared publication lock, derives exact
   current/previous/active-working publication IDs, preserves those tables,
   and drops only unreferenced exact matches. It runs after publication and
   failure cleanup, stale-intent recovery, startup, and before each new
   preparation. Lock-timeout deferral is safe because the next invocation
   retries; active working and rollback tables are never dropped.
9. Post-commit cache compatibility cleanup also takes the exact
   per-publication cache-build advisory key transactionally. If a current
   generation rebuild owns its session build lease, cleanup defers without
   truncating legacy staging or deleting generation staging. The rebuild can
   complete its live swap, and a later cleanup retry mirrors the resulting
   generation safely.
10. A prepared `building`/`ready` working generation with no commit intent,
    live scrape, recent scrape-worker publication heartbeat, or durable
    deferred marker is treated as an abandoned crash artifact. Startup
    recovery marks it failed, clears the exact working pointer, removes its
    prepared artifacts, and permits the next scrape.
11. Pending failure isolation is correlated to
    `public_reads_frozen_scrape_id`, not whichever generation happens to own
    the working pointer later. Recovery marks and confirms that exact scrape
    failed before clearing the fail-closed freeze; a mismatched newer working
    generation remains untouched. Failed updates leave the pending freeze in
    place.
12. Advisory contention and cutover deadline exhaustion are not data
    failures. The worker retries the same prepared generation under a bounded
    policy. Exhaustion records `publication-commit-deferred`, preserves the
    ready working generation, blocks allocation of a replacement scrape, and
    keeps reads cached/fail-closed until the same generation is retried.
13. Publication read leases have server-enforced transaction lifetimes:
    ordinary publication-bound routes use 30 seconds and export routes use
    180 seconds by default. Both
    `idle_in_transaction_session_timeout` and PostgreSQL 17
    `transaction_timeout` prevent abandoned shared leases from blocking
    publication indefinitely.
14. HTTP requests never execute reconciliation, DDL, or orphan cleanup while
    holding the public-read gate monitor. Stale/pending detection triggers a
    TTL-limited single-flight background recovery coordinator; startup runs
    the same coordinator synchronously before readiness.
15. Candidate preparation sets bounded server `lock_timeout`,
    `statement_timeout`, and `transaction_timeout` values before acquiring
    the shared publication lock, preventing an unbounded preparation
    transaction while retaining the measured multi-minute build budget.
16. Recovery freeze reasons (`publication-commit`,
     `publication-commit-deferred`, and
     `publication-isolation-pending`) cannot be overwritten by generic
     scrape-start, publish, failure, or unfreeze transitions. Only owned commit
     leases, confirmed failure reconciliation, or successful pointer commit
     may replace them.
17. Preparation persists its complete final-commit contract in generation
     metadata. Before authentication, catalog refresh, `ScrapeStarting`, or new
     scrape allocation, every worker pass loads a deferred ready generation and
     retries that exact preparation under the bounded contention policy.
     Success performs normal cutover/unfreeze; continued contention keeps the
     deferred fail-closed state; invalid metadata/candidate state is failed and
     cleaned safely. New scrape allocation is rejected while a deferred ready
     generation remains.
18. Deferred recovery executes before improvement-notification recovery,
     Epic authentication, API-only waiting, and the normal scrape loop.
     Notification recovery explicitly yields while a deferred publication owns
     the fail-closed state, then runs after successful cutover. Contention
     exhaustion is converted into a deferred pass outcome rather than escaping
     `ExecuteAsync`; bounded retries use a five-second outer backoff and never
     stop the host. API-only workers follow the same recovery path without Epic
     credentials.
19. Deferred metadata handling distinguishes proven corruption from transient
     storage failures. Only `DeferredPublicationMetadataException` causes
     candidate isolation. Npgsql, pool, schema-query, and other transient
     lookup failures retain the ready generation and retry without clearing its
     working pointer or deferred freeze.
20. Worker-shutdown cancellation during contention retry is classified as
     deferral, not publication failure. Normal and deferred callers preserve
     the ready generation/artifacts, restore or reassert fail-closed state, and
     exit cleanly without failed-candidate isolation.
21. `PublicationCommitDeferred` is nonthrowing. If its durable freeze write
     fails transiently, the service installs an in-process fail-closed gate
     override, triggers background recovery, and does not claim durable
     deferral. The override is retained until a durable commit/deferred/pending
     or failed-candidate state is observed.
22. One owned commit-intent lease spans the entire contention retry policy.
     The owner heartbeat is refreshed before each try-lock, and retry delays
     never restore the permissive `publish` freeze. Exhaustion or shutdown
     atomically transitions that same owner to deferred; terminal success
     clears it in the pointer transaction.
23. If owner-aware deferred transition fails, lease disposal does not restore
     the prior freeze. The durable cross-process `publication-commit` latch
     remains visible to `fstservice`, an additive worker-local fail-closed
     override is installed, and a heartbeat-backed background task retries the
     deferred transition.
24. Notification-gate freeze probes and pending-isolation reconciliation are
     inside the gate's retry/backoff try/catch. Transient Npgsql, pool, or
     schema failures hold the next scrape and retry; only host cancellation
     propagates.
25. Pending isolation has a terminal-success branch. If the frozen target
     scrape is already the current published generation, or is a safely
     retained predecessor after a later atomic publication, reconciliation
     clears only the stale commit/isolation latch. It never marks that
     published scrape or generation failed.
26. Deferred-resume exception handling ends at the atomic commit boundary.
     Cleanup, lifecycle/status updates, notification detection, and
     scores-changed broadcast failures after commit are logged/retried
     separately and cannot call `FailScrapeRun` for the already-current scrape.
27. Nonterminal commit failures transfer the still-owned commit-intent lease
     into failure isolation. If failure recording and owner-aware pending
     transition both fail, lease disposal is suppressed: the durable
     cross-process `publication-commit` latch remains heartbeated and visible to
     `fstservice`, while worker-local fail-closed state is additive.
28. Deferred-resume lookup error handling contains only metadata lookup and
     parsing. `PublicationCommitExecutionException` is handled exclusively at
     the commit boundary, where its owned lease is passed into `FailScrapeRun`;
     confirmed isolation clears the owner safely, while dual-write failure
     preserves/heartbeats the latch and local gate until reconciliation.

Structured logs and worker suboperations report preparation, reader-drain,
exclusive-lock, lock-rejection, relation-lock-retry, and post-commit cleanup
durations. Defaults are configured under `PublicationCommit`; the exclusive
hold budget is 5 seconds and is enforced by the database transaction, not
merely logged.

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
while public reads are frozen. Frozen exact generation-cache hits are served
before the shared advisory boundary; cache misses and noncacheable
publication-bound reads still require the shared lease outside the bounded
commit-intent interval. `FailScrapeRun` takes the matching nonqueueing
exclusive lock before activating isolation, so an in-flight response cannot
cross the failure boundary. The dedicated read-lock pool disables
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
- catalog refresh never queues for that lock. It first takes a non-queueing
  shared try lease and performs a command-timeout-bounded singleton lookup.
  An exact match returns the existing token without taking the exclusive lock
  or rewriting either `songs` or `live_song_catalog`. The no-mutation shortcut
  requires a positive catalog version plus matching schema version, SHA-256,
  song count, `provider_exact` source, exactness, and JSONB catalog. A mismatch
  releases the read transaction before the writer attempts the non-queueing
  exclusive lease. If that lease is busy, a second shared exact-match check
  safely recognizes a concurrent identical writer before reporting
  contention;
- any contended mismatch, invalid singleton, or inability to take the shared
  try lease throws `SongCatalogPersistenceBusyException`. Periodic catalog
  refresh logs the retryable deferral and retries at its next interval;
  scrape capture and startup surface the same exception rather than reporting
  stale success. Exact provider changes are staged and applied to the in-memory
  catalog only after persistence succeeds, so a busy result preserves the
  previous catalog/token and the next interval still observes the change.
  Once the exclusive try lock succeeds, the existing in-lock row recheck,
  song upserts, monotonic version allocation, singleton update, and
  transaction commit remain unchanged;
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

The notification schema is additive. Constant defaults make pre-existing
notification rows visible without a data backfill or table rewrite.
`DatabaseInitializer` retains the two immutable historical maintenance audit
tables for compatibility even though their executable writer is retired, then
runs and commits the complete notification schema in its own command and
transaction before the unbounded main/publication schema batch. That
transaction uses local two-second lock and fifteen-second statement timeouts,
so notification ALTER locks cannot survive into publication reconciliation.
No secondary index is added to the existing event tables.

The score-history dedup audit schema is likewise additive and runs in its own
short schema transaction with the same two-second lock and fifteen-second
statement timeouts. It creates only the immutable run/original-row audit tables, trigger
function, triggers, and small digest lookup index, plus a one-time constraint
migration that preserves contract-v1 audit rows while accepting contract v2.
It does not scan, merge, delete, or index-rebuild `score_history`; those
actions exist only behind the explicit maintenance execute digest gate.

## Retention and maintenance

- `DatabaseRetentionMaintenanceService` skips cleanup under measured database
  pressure and uses an advisory lock.
- Metadata TTL cleanup is bounded by batch count, row count, and command
  timeout. Snapshot rewrite remains disabled by default.
- Production must resolve
  `DatabaseMaintenance__SnapshotRetentionRewriteEnabled` from one authoritative
  Compose environment source. Do not duplicate a contradictory value in a
  service `env_file`: Compose `environment` entries take precedence. Keep the
  resolved value `false` until an explicit dual-lane live-scrape parity window,
  capacity guard, exact partition plan, and rollback package promote a rewrite.
- Composite rank-history retention batches are intentionally unordered. The
  BRIN handles cutoff-range rejection, the primary key handles account/date
  existence probes, and `LIMIT` bounds each delete without a global sort.
- `tools/postgres-capacity-guard.sh` is required before broad scrape,
  post-process, optional build, or maintenance work. It records free space,
  DB/WAL size, scratch, publication state, locks, and active maintenance.
- Scrape-time API precompute staging writes below
  `Scraper:DataDirectory/precompute-staging`. Production resolves that path
  under `/app/data` on the 4 TB FST drive; it must never fall back to container
  `/tmp` or the Docker overlay filesystem.
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

- Repository code no longer exposes or validates a logical writer flag, calls
  logical write/rollback paths, or creates the logical current, version, or
  metrics schemas. Tracked appsettings, Compose, and role defaults no longer
  include the retired key.
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
- No live DDL is part of the code retirement. The empty current/version
  objects, retained primary-key families, and 108-row metrics table await a
  cleanup image and successful full-scrape publication/public-fingerprint
  parity before exact physical removal.

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
  logical shadow has no PG-4 reader, writer, config surface, or startup schema
  owner.
- **PG-5:** latest-state history design, explicit retention, and same-drive
  Parquet/DuckDB artifact pilots.
- **PG-6 / SERVICE-4:** versioned migrations, one migration owner, autovacuum,
  work-memory, WAL/checkpoint, and recovery governance.
- **PG-7:** parity-gated object-by-object archive and reclaim.

These are tracked changes to this design, not permission to bypass its current
source, publication, same-drive, restore, or parity rules.
