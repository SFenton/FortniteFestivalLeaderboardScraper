---
status: canonical
owner: data
last_verified: 2026-08-30
last_verified_commit: 21d7193c
sources:
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/MetaDatabase.cs
  - FSTService/Persistence/MetaDatabase.Publication.cs
  - FSTService/Persistence/PublicationGeneration.cs
  - FSTService/Persistence/SongCatalogSnapshot.cs
  - FSTService/Persistence/InstrumentDatabase.cs
  - FSTService/Persistence/PublishedSoloScopeSql.cs
  - FSTService/Persistence/GlobalLeaderboardPersistence.cs
  - FSTService/Persistence/BandCurrentProjectionBuilder.cs
  - FSTService/Persistence/PublicationPathArtifactSchema.cs
  - FSTService/Persistence/MetaDatabase.PathPromotion.cs
  - FSTService/Scraping/PathDataStore.cs
  - FSTService/Persistence/MaxScoreMaintenanceSchema.cs
  - FSTService/Persistence/MaxScoreMaintenanceModels.cs
  - FSTService/Persistence/MaxScoreMaintenanceService.cs
  - FSTService/Persistence/MaxScoreMaintenanceScoreHistoryEvidence.cs
  - FSTService/Persistence/MaxScoreMaintenanceAccountIdPolicy.cs
  - FSTService/Persistence/MaxScoreMaintenanceCacheEntryEvidenceStore.cs
  - FSTService/Persistence/MaxScoreMaintenanceArtifactValidator.cs
  - FSTService/Persistence/MaxScoreMaintenanceNotificationService.cs
  - FSTService/Persistence/RegistrationMutationGuard.cs
  - FSTService/Persistence/MetaDatabase.PhaseProgress.cs
  - FSTService/Persistence/Maintenance/DatabaseMaintenanceDryRunReporter.cs
  - FSTService/Persistence/Maintenance/DatabaseRetentionMaintenanceService.cs
  - FSTService/Persistence/Maintenance/ServiceMaintenanceLock.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionSchema.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.Reads.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionOracle.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationQuarantineSchema.cs
  - FSTService/SnapshotGenerationRetentionSafePointQueue.cs
  - tools/postgres-snapshot-generation-archive.py
  - tools/postgres-snapshot-generation-archive-drill.py
  - FSTService/Persistence/Maintenance/SnapshotGenerationDropSchema.cs
  - tools/FstSnapshotGenerationDrop/
  - tools/postgres-snapshot-generation-restore.py
  - docs/database/SnapshotGenerationDropRunbook.md
  - tools/FstSnapshotGenerationQuarantine/
  - FSTService/Api/PublicationApiResponseCacheService.cs
  - FSTService/Api/PublicationApiResponseCachePolicy.cs
  - FSTService/Api/PublicationReadiness.cs
  - FSTService/SongCatalogRefreshWorker.cs
  - tools/postgres-retire-ix-le-song-rank.py
  - FSTService/Scraping/BackfillOrchestrator.cs
  - FSTService/Scraping/RegistrationMutationCoordinator.cs
  - FSTService/Scraping/RegisteredBandProcessing.cs
  - FSTService/Scraping/RegisteredPlayerBandDiscovery.cs
  - FSTService/Scraping/RankingsCalculator.cs
  - FSTService/Scraping/PlayerStatsTierRebuilder.cs
  - FSTService/Scraping/LeaderboardRivalsCalculator.cs
  - FSTService/Scraping/ScrapeTimePrecomputer.cs
  - FSTService/Scraping/Replay/
  - FSTService/FeatureOptions.cs
  - deploy/postgres.Dockerfile
  - tools/postgres-pro-bass-snapshot-rewrite.py
  - docs/database/ProBassSnapshotRewritePilot.md
  - docs/database/SnapshotGenerationPartitionMigration.md
update_triggers:
  - Schema, persistence ownership, publication storage, retention, restore, or source-of-truth behavior changes.
---

# Data storage

## Authority

PostgreSQL 17, accessed through Npgsql and parameterized SQL, is the service
source of truth. The service does not use an ORM.

`MetaDatabase`, `InstrumentDatabase`, and other repository-style classes are
logical ownership boundaries over the same PostgreSQL database. In particular,
an `InstrumentDatabase` applies an instrument predicate to shared relations; it
is not a separate per-instrument SQLite database.

`FortniteFestival.Core` still contains legacy file/SQLite compatibility code
because it targets both .NET Framework 4.7.2 and .NET 9. That compatibility
surface is not the production service persistence model.

## Data families

| Family | Purpose |
|---|---|
| Catalog/provider capture | Exact Epic song payloads, catalog versions, images, item shop, path-generation inputs |
| Scrape/candidate state | Scrape runs, page coverage, writer outcomes, manifests, replay/failure evidence |
| Solo leaderboard state | Physical snapshots, overlays, current projections, ranks, history, population, first-seen data |
| Band state | Membership/context, rankings, histories, extraction state, tracked bands |
| Account state | Display names, registrations, selected profiles, refresh/backfill progress |
| Derived products | Rankings, rivals, statistics, precomputed responses, improvement notifications |
| Publication state | Published scrape/generation, source bindings, read freeze, commit intent, leases, cache generations, publication-bound path artifact snapshots |
| Operations/audit | Worker heartbeat, terminal scrape-phase outcomes, detailed subphase timings, max-score checkpoints/rollback evidence, immutable snapshot-generation observations/deferrals/holds/hash chains, immutable quarantine/reattach/attestation evidence, maintenance notification quarantine, dedup/recovery audit state |
| Replay evidence artifacts | Immutable Tier-0 filesystem packages that describe producer/source/build/schema/config/phase lineage and checksummed artifact metadata; never publication authority |

### Freeze-safe publication API cache

`publication_api_response_cache` is the authoritative L2 for covered JSON
responses. Its primary key `(publication_id, cache_key)` already owns the
required read and uniqueness path; no additional index or schema rewrite is
needed. Each row stores deterministic JSON bytes, ETag, and `cached_at`.
Service lookup derives the full SHA-256 and fixed JSON content type and combines
those with publication ID and the public-read safety revision for L1 identity.
The warm current-publication L1 path reads current publication, durable freeze,
and failed-candidate isolation through one combined PostgreSQL snapshot query.
No schema/index is added. Publication ID remains in the L1 key, and any
publication/freeze/failure change increments the process safety revision and
clears current L1 entries.

Publication cleanup retains only current and previous generations. Full
precompute uses staging and atomic swap. Same-publication max-score maintenance
rebuilds and swaps the same surface before unfreeze. Guarded same-publication
path/max-score changes write the canonical songs row only after a
content-mutation token remains current; service L1 is invalidated before
mutation and after the durable update. Periodic API catalog refresh instead
updates `live_song_catalog` and live song metadata without rewriting the
publication-owned canonical row. It compares canonical provider hashes, so
same-count metadata changes still advance live catalog identity and lag
telemetry.

Publication preparation validates the staged canonical songs count and exact
ID set against `publication_song_catalog`; deferred commit validates the
prepared generation again. Cache inheritance requires equal exact catalog
hashes. Service-info reads only scalar versions, hashes, counts, and path
pending totals on each poll. A full per-song catalog comparison runs only when
the live/published hashes differ and is cached in-process by both exact
version/hash identities.

Bounded lazy writes use the shared publication advisory lock, require the
expected current publication, no working publication, and unfrozen reads, then
upsert both generation and compatibility rows in one short transaction.
The surface binding row count/hash/timestamp is updated in the same commit.
Frozen or transition-raced writes return no row and cannot poison L1.
An exact current byte/ETag match is returned without an upsert, so service
restart recovery does not rewrite an already-current songs payload.

The current production surface has 9,255 rows and about 245 MB of JSON for one
publication. The candidate adds at most 6,319 eager rows (one songs row plus
6,318 published song/instrument top-10 rows). A read-only stratified sample of
180 standalone instrument payloads across 20 evenly spaced catalog songs
measured a `1.09696` standalone-to-leaderboard-all byte ratio, yielding
19.02 MB logical payload per publication. Keep 20.74 MB as the conservative
upper bound until a full candidate precompute measures every row. The only lazy space is ten finite overview
metric/size variants, at most 1.59 MB from measured payloads. Current and
previous generation rows plus the required current `api_response_cache`
compatibility mirror therefore add about 57.06 MB logical payload centrally,
with 62.22 MB retained as the conservative upper bound. If all ten lazy variants exist in both retained generations, their
current compatibility mirror raises the lazy upper bound to about 4.76 MB;
table/index overhead is additional. Full precompute writes both compatibility
and generation staging tables, so incremental eager staging is about 38.04 MB
centrally and 41.48 MB conservatively. Existing live generation/compatibility
relations compress logical JSON materially; the current ratios imply roughly
24.83-27.08 MB steady physical growth, but this is indicative only.
Incremental WAL, table overhead, and
promotion-copy cost remain full-scrape A/B measurements rather than inferred
acceptance evidence.

The rejected service-only A/B on publication 1302 measured the full current
cache build directly. Head `5a227954` staged 15,574 rows and 267,948,123
logical JSON bytes, then atomically swapped current while retaining publication
77. Relative to the 9,255-row baseline, current generation and compatibility
mirror each added 6,319 rows and 22,805,709 logical bytes. Physical database
growth was 24,453,120 bytes and filesystem growth 24,596,480 bytes. Peak
staging/free-space excursion was 409,980,928 bytes, WAL was 466,458,000 bytes,
and PostgreSQL temp-file/byte deltas were zero. The core precompute took
167.94 seconds. These measurements bind to the rejected Unicode-escaping head;
the repaired encoder must be remeasured before service promotion.

The accepted repeat A/B on head `cf044631` staged the repaired current cache
without changing publication/freeze state. After two bounded lazy overview
rows, current held 15,576 rows and 264,511,124 logical bytes: `+6,321` rows and
`+19,368,712` logical bytes over baseline. WAL was 469,401,072 bytes,
PostgreSQL temp delta remained zero, physical database growth was 13,484,032
bytes, and peak free-space excursion was 296,656,896 bytes. Current and
previous generations remained intact and staging was empty after swap.

The accepted cache remains current on publication 80 with 15,576 rows and
264,511,124 logical JSON bytes; publication 77 remains retained and staging is
empty. Official deployment did not move publication/freeze pointers.

### Solo ranking denominator ownership

Normal per-instrument ranking denominators are catalog-bound. For each exact
current-catalog song, support is the union of provider admission, a matching
promoted path instrument, and positive mutable `leaderboard_population` for
that same song/instrument. The population table is supplementary evidence in
this path: stale rows whose song is absent from the current catalog are never
counted. Positive population for a current-catalog scope may keep that scope in
the denominator even when a present provider sentinel prevents a new scrape,
because retained ranking inputs can still contain it.

Current ranking materialization filters resolved score rows and valid-score
overrides to current-catalog song IDs. Removed-song leaderboard entries and
`song_stats` remain stored for history and recovery but do not contribute to
current songs played, Full Combos, total score, coverage, or FC rate.

Every successfully rebuilt account-ranking partition must carry its selected
uniform denominator. The post-ranking summary pass rejects rows whose
denominator differs, whose songs/full combos exceed it, or whose coverage/FC
rate is non-finite or outside `[0,1]`. Instruments skipped because their
selected denominator is zero are not newly validated or rewritten. This keeps
downstream family, combo, history, and cached responses from normalizing or
publishing inconsistent per-account values.

The `songs` path-generation state stores distinct theoretical maxima for all
eight path instruments. Plastic drums use separate
`max_pro_cymbals_score` and `max_pro_drums_score` columns because cymbal-mode
gems can score differently from the no-cymbal mode even though both originate
from Epic's single `pd` chart. Schema initialization adds these nullable
columns idempotently and includes them in the atomic path-metadata write guard.

The exact relation inventory is intentionally source-driven because it changes
frequently. `DatabaseInitializer` and its tests are the schema inventory;
canonical documentation describes ownership and invariants instead of copying
volatile table counts.

### Legacy solo rank-index retirement

`ix_le_song_rank` is not bootstrap schema and is not a scrape-time droppable or
recreated index. Its parent plus nine leaves were removed from production on
2026-08-17; no current physical family remains.

Only `tools/postgres-retire-ix-le-song-rank.sh` may validate absence or perform
an explicitly reviewed restore/retirement cycle. The tool binds the production
Compose project, PostgreSQL system identifier, publication, exact parent plus
nine leaf OIDs/definitions and attachments, dependency/constraint inventory,
bytes, and dated zero-use observation. It generates the exact rollback before
any execute command.

The completed removal changed no rows, rankings, publication pointers, API
contract, unrelated index/constraint, or representative score-index plan.
Legacy rank predicates remain logically correct without it. Public API roles
continue to read published/current projection sources. See the
[stale solo rank index retirement runbook](../database/StaleSoloRankIndexRetirementRunbook.md).

### Max-score maintenance evidence

`max_score_maintenance_runs` owns the digest-bound workflow checkpoint:
manifest/plan identities, exact publication/catalog, score-source,
notification-state, rank-history, publication-population evidence, and bounded
complete consumed `score_history` evidence, freeze owner, last durable phase,
rollback file digest, notification audit link, counters, staged-cache evidence,
and bounded failure detail. Population evidence stores scope count, effective
range, and hash. History evidence stores row count, ID/time ranges, and hash.
Cache evidence stores the whole-stage hash, the exact publication-scope cache
key inventory hash, and target-scope, affected-account, and
overlay-only-account hashes.
`max_score_maintenance_cache_entries` stores one immutable row per staged
cache key with its ETag and JSON SHA-256. The evidence is captured atomically
with the `caches_staged` checkpoint only after the legacy and
publication-addressed staging tables match exactly; updates/deletes and later
inserts are rejected. From the committed `caches_staged` checkpoint through
every later pre-complete state, generation-aware lease checks and statement
triggers reject ordinary cache-build leases plus staging
insert/update/delete/truncate operations. Only the live maintenance session
whose lease token matches the exact frozen run/publication may mutate those
tables for validation or final publication. A
post-freeze failure changes status to `failed` only while
the lock-owning backend can commit that checkpoint; backend loss leaves the
last durable status/phase unchanged and never clears the freeze.

Guarded rollback extends the same run with separate rollback timestamps,
before/after path fingerprints, restored/rebuilt/quarantined/cache counts,
rollback cache evidence, and rollback-specific failure detail. Apply failure
fields and apply cache evidence remain unchanged for forensic history.
Rollback phases progress from `rollback_validating` through
`rollback_validated`; only phase/status `rolled_back` is terminal.
`max_score_maintenance_rollback_cache_entries` stores the immutable rollback
staging key/ETag/JSON hashes separately from apply evidence. Schema migration
adds these columns, phases/status, constraints, table, and mutation guards
idempotently; the CLI one-shot still requires a prior release schema
initialization.
Either committed apply cache evidence or rollback cache evidence blocks normal
cache-build leases and non-owner staging mutations for the frozen publication;
application guards and database triggers enforce the same rule.
The stronger admission rule starts with the active digest-owned max-score
freeze, before either evidence field exists, so intermediate apply/rollback
state cannot be published by an ordinary cache builder.
It remains fail-closed if a working publication pointer appears unexpectedly;
active maintenance detection is bound to the current publication/freeze/run,
not to the absence of that pointer. Scrape allocation rejects the freeze or
durable mutation token before inserting a new working generation.
The publication-addressed staging guard is row-scoped for DML: non-owner
startup retention may delete only rows outside the current, previous, and
working publication IDs. All current-generation DML and every staging truncate
remain rejected, allowing FSTService schema initialization to restart safely
during a freeze without exposing maintenance state.

Notification maintenance candidate JSON includes an `alignmentDirection`
(`apply` or `rollback`) before hashing. The unique audit key therefore cannot
reuse a completed apply alignment for rollback merely because both candidate
sets are identical or empty. Rollback audit insertion/reuse, baseline
alignment, rollback audit ID/count fields, and the
`rollback_notifications_quarantined` phase checkpoint commit in the same
lease transaction.

`max_score_maintenance_rollback_songs` stores every pre-promotion path field,
all eight maxima, the immutable generation file count, and the exact artifact
tree SHA-256 for every manifest song. It complements the canonical same-drive
rollback JSON. Plan and apply revalidate both current rollback and staged
generation trees, including every JSON/PNG hash; missing, extra, symlinked, or
changed artifacts fail closed. Database triggers reject workflow-identity
changes and rollback-row updates/deletes; neither surface deletes historical
generations. Rollback JSON v3 binds the immutable run `created_at`, exact
publication/catalog, and database rollback-song identity. Every post-capture
resume and final completion reloads canonical bytes and requires the
checkpointed SHA-256; missing, corrupt, or swapped evidence keeps the freeze
resumable.

Rollback path restoration and its durable checkpoint share one serializable
source-locked transaction. It targets only the exact rollback song set and
requires every locked current path to match the promoted manifest identity
before replacing all stored path/maxima fields with rollback values. It never
deletes promoted immutable generations or audit rows. Later derived,
notification, and cache work is resumable; final validated cache swap,
`rolled_back` checkpoint, and unfreeze are one serializable lease-owned
transaction.

Each rollback invocation captures two score-history views in the same
repeatable-read/source-locked snapshot: the original accepted
post-promotion selector and the selector implied by the restored maxima. The
latter covers rows that become fallback-eligible only when a maximum decreases;
a missing restored maximum has an explicit empty fallback evidence set. Final
validation recomputes both views. The digest-owned freeze and database mutation
guards prevent source drift between retries.

Rollback retains the session-owned registration mutation and path-generation
advisory locks but not the global publication lock. Every lease-owned
transaction requests the transaction-scoped exclusive publication lock after
its uncommitted work and immediately before durable commit. This commit fence
lets cached public reads continue during long computation while ensuring
readers observe only complete committed units.

Forward resume uses the same commit-fence lease shape under application name
`fst-max-score-resume`. Initial apply still retains the publication lock while
creating the freeze; a resume can yield it because the exact digest-owned
freeze, durable mutation token, worker-offline gate, and source locks already
fence the current publication.

Affected-account cache evidence orders the public fingerprint tuple by song ID
and projected combo ID. Raw instrument names are converted before sorting so
the expected ordering matches the serialized player cache ordering for
same-song multi-instrument scores.
Max-score maintenance report/log diagnostics use a `sha256:<16-hex>` evidence
identifier for registered accounts. The durable cache keys and payloads retain
their required exact account IDs, but operator evidence and error text do not
emit them.

Terminal success additionally requires all
`max_score_mutation_gate_*` ownership fields to be null. If the commit succeeds
but the original backend disappears before lease disposal, the same rollback
command acquires a fresh exclusive cleanup lease, replaces the stale owner,
releases its locks, clears its token, and verifies the row before reporting
success. An already-`rolled_back` retry performs this reconciliation too.
When cleanup cannot finish, report v2 represents the committed terminal state
with `cleanupPending=true` rather than rewriting it as a preflight failure.

Maintenance observed-score bounds, affected-account selection, player-stat
validation, and ranking/player-stat inputs share the published solo source
resolver: the current publication's selected snapshot or empty source plus
supplemental overlay, with overlay precedence per account. They do not trust
`current_leaderboard_entries`, which can lag overlay-only writes.
For each changed maximum, maintenance records the CHOpt denominator and the
exact ranking validity cutoff,
`RankingsCalculator.ComputeMaxScoreThreshold(newMaximum)` or
`floor(newMaximum × 21 / 20)`. The shared
`MaximumScoreWithRepresentableRankingCutoff` is `2,045,222,521`, derived as
`(((int.MaxValue + 1) × 20) - 1) / 21`; the next maximum would require cutoff
`2,147,483,648`. Target complete maxima, partial constraints, actual
current/staged paths, manifest paths, and report checks reject that value
before mutation. General ranking threshold computation remains compatible
with unrelated frozen-catalog maxima by using exact `long` arithmetic and
saturating the result to `int.MaxValue`; no PostgreSQL `INTEGER` score can
exceed the unsaturated cutoff in that case. Score-history selectors therefore
receive only representable `INTEGER` maximum/cutoff arrays without turning an
unrelated catalog value into target admission. For every changed pair, plan
report/digest contract v6 records the raw highest resolved score, the highest
ranking-eligible score at or below the cutoff, and the count above the cutoff.
A mapped empty source records two null maxima and a zero outlier count. Raw
rows above the cutoff remain visible ranking-invalid evidence but do not block
compatibility; ranking and score-history fallback evidence owns their
exclusion and replacement. Apply and every resumable continuation reload the
exact maxima/count evidence and reconstruct the approved digest before
mutation. A missing mapping, invalid maximum/cutoff, eligible maximum above
the cutoff, or any raw, eligible, or count drift fails closed.
Maintenance population is resolved from the same complete source map,
combining each source's reported population with its resolved overlay row
count. It is snapshotted once under the exclusive fence and never falls back
to mutable `leaderboard_population`. The strict read context remains active
through cache generation and final validation, so active snapshots, the worker
projection, and legacy rows cannot enter the staged cache.
The frozen catalog plus this population map also owns maintenance song and
instrument scopes, total-song/completion denominators, maximum-score filtering,
and the expected base/leeway/rank-offset cache keys. Maintenance never uses
legacy `GetAllSongCounts()`, active `song_stats`/`leaderboard_entries`, or the
process-cached total-song count for those decisions.
For each affected instrument, one fenced transaction deletes `song_stats`
outside the immutable published scope set and upserts every published scope,
including empty scopes with zero counts. The instrument ranking partition is
then replaced from that exact source, so active-only accounts and old
denominators cannot survive. Final validation compares the complete expected
and actual `song_stats` inventories and rejects ranking rows whose account or
`total_charted_songs` is not owned by the frozen source.

Affected accounts' `player_stats_tiers` rows are deleted and rebuilt atomically
per fenced chunk rather than only upserted. This removes stale active-only
instrument tiers while leaving unrelated accounts untouched. Maintenance cache
serialization admits `Overall` plus only instruments present in the frozen
publication scope; unrelated accounts may retain other durable tier rows, but
those rows cannot leak into the maintenance cache generation.

Blank or whitespace-only affected account IDs are invalid selector output.
They are removed consistently from score-history affected sets, tier chunks,
registered cache account sets, cache evidence, and final player-tier
validation. When a changed published source contains such a row, maintenance
first proves that no matching blank identity exists in `score_history`,
`registered_users`, or account-specific API cache keys. This guard keeps the
plan-digest v6 contract unchanged for a source-only blank row with no consumed
history, allowing a previously frozen run to resume with its existing digest;
an identity or evidence conflict remains fail-closed.

Leaderboard-rivals maintenance is scoped by the manifest's changed
instruments. Each affected instrument loads account rankings once, derives all
registered-user neighborhoods in memory, deduplicates users and neighbors, and
performs one authoritative published-snapshot-plus-overlay profile query.
Existing C# calculations still own all five rank methods, above/below
directions, shared-song counts, signed deltas, and the top-200 samples. Each
user/instrument replacement remains transactional. Rival rows, samples, and
state for instruments outside the manifest receive no delete, update, or
insert.

The score-history aggregate covers all registered-account history consumed by
player/history caches, fallback tiers for affected player-stat accounts, and
fallback candidates across every song in each rebuilt instrument. Before
selection, a transaction-local publication row must match the manifest
publication/scrape, have no working publication, name a `current` generation,
and carry a ready `solo_scope_sources` `scrape_id` binding whose row count and
JSON identities match complete nonempty all-time source rows.

There is no publication-wide current-candidate temporary table. Exact changed
scopes enumerate every current account independent of score, preserving
overlay-only classification. Ranking and affected-player fallback keys are
inserted directly with per-scope parameterized snapshot probes grouped by
instrument. Snapshot predicates use
`(snapshot_id, song_id, instrument, score DESC)` with a strict integer
`score > cutoff/maximum`; runtime pruning removes other instrument partitions.
An account's snapshot row is excluded whenever a supplemental overlay exists
for the same scope, regardless of the overlay score, and only a qualifying
overlay is inserted. Unique `ON CONFLICT DO NOTHING` fallback keys preserve
player/ranking differential semantics.

Registered history joins the captured registration rows with the same
multiplicity as the established contract, while nonregistered history uses
unique account/song/instrument fallback keys and the existing score-history
lookup index. Each branch preserves the prior
`hashtextextended(jsonb_build_array(...)::TEXT, seed)` row identity exactly,
including field order, JSON nulls, signed values, and epoch-microsecond
timestamps. The branches aggregate independently to count, ID/time ranges, and
hash sum/xor state, then combine associatively into the unchanged report
fingerprint envelope. JSON text is hashed per row but never retained; no
history row is copied to a temporary table or ordered payload. Selector tables
contain only publication/source/maxima/account/fallback keys, are
`ON COMMIT DROP`, and are also removed explicitly; a savepoint restores the
caller's maintenance transaction after cancellation or timeout without
releasing its pre-existing publication/source fences. No new index is
required.

`improvement_notification_maintenance_runs` and
`improvement_notification_maintenance_candidates` retain historical
`maintenance_pro_lead_max_score_repair_v1` rows and accept new
`maintenance_max_score_correction_v1` audit rows. Both purposes remain
quarantine-only with a compile/schema-enforced visible delivery count of zero.
Maintenance candidate parity uses routine visible cardinality rather than raw
audit-row cardinality: player ranks coalesce by player/instrument, band-song
metrics coalesce by play, band rank metrics group by subject/scope, progress
metrics remain individual, and `band_rank_state_missing` is audit/alignment
only. Max-score-percent rank changes likewise remain in quarantine and state
alignment. Missing current band subjects and their song/rank state are created
inside the same repeatable-read quarantine transaction before candidate
collection.

Registration, backfill, registered-user refresh, score-history reconstruction,
tracked-band discovery/processing, selected-profile activity, and stale
registration pruning share one PostgreSQL session advisory mutation gate.
Each complete external async mutation lifecycle holds the shared form on a
dedicated unpooled, non-multiplexed session before its first write; gate
holders and waiters therefore do not consume the normal 15-connection service
pool. The lease owns no transaction or publication-row lock, so
`idle_in_transaction_session_timeout` cannot expire it while Epic/history work
uses ordinary pooled connections. Background/manual workers may wait with
cancellation on the isolated session. HTTP tracking, manual backfill, and band
sync use a pool-capacity-bounded `pg_try_advisory_lock_shared` admission and
return `503`/`Retry-After: 30` instead of queueing behind maintenance.
Cancellation or disposal explicitly unlocks and physically closes the
isolated session.

Max-score plan/apply takes the exclusive form of the same gate first. It waits
for every admitted shared lifecycle to finish and prevents later registration
work from starting. The lock-owning unpooled session immediately records an
opaque random owner token, publication ID, backend PID/start, and acquisition
time in `scrape_publication_state`; this durable fence also blocks HTTP before
a public-read freeze exists. Apply then takes the path-generation and
publication advisory locks, establishes or revalidates its digest-owned
freeze. Every dependent mutation or phase checkpoint then opens its own
bounded transaction on that same unpooled lock-owning session and takes
`SHARE` locks in fixed order on `leaderboard_entries_overlay`,
`leaderboard_entries`, `score_history`, `band_member_stats`, and
`leaderboard_population` before doing work. Checkpoints therefore remain
durable between resumable phases without moving writes onto unrelated pooled
sessions. The durable owner and table triggers bridge the short gaps between
transactions.

Row triggers remain a fail-closed second line on registered users/bands,
registered-user refresh progress, registered-band status/progress,
registered-player band-discovery progress, backfill status/progress, and
history-reconstruction status/progress. Statement triggers apply the same
fence to `leaderboard_entries`, `leaderboard_entries_overlay`,
`leaderboard_population`, and `score_history`, plus `band_entries`,
`band_member_stats`, `band_members`,
`band_team_membership`, `band_team_membership_state`, and
`band_team_configurations`. This prevents a backfill whose shared lock backend
died after its entry commit from raising the population floor after exclusive
maintenance has claimed the durable gate. Every band persistence entry point
also validates and share-locks the durable gate row at the start of its
complete transaction, even when `MemberStats` is empty. Each trigger rejects
either the active durable mutation-gate token or a
`max-score-maintenance:v1:<digest>` freeze. This row lock orders a surviving
write from a lost shared session before a newly claimed exclusive gate,
without taking a second advisory lock on a pooled connection.

Both lease types set an independent random session token and capture the
backend PID. Every max-score write explicitly executes through the lease API;
inside that same transaction it verifies the token, backend, advisory locks,
durable owner, and all five source locks both before work and immediately
before ordinary commits. Final completion validates its exact owner/freeze,
publishes the cache, marks the workflow complete, and unfreezes in one
source-locked transaction while retaining the durable owner token. Lease
disposal then releases the publication, path-generation, and exclusive
mutation advisory locks before conditionally clearing that token. A queued
shared holder therefore cannot pass the advisory gate early, and a stale
direct writer remains blocked by the durable token throughout the handoff. No
`AsyncLocal` or preflight-only check is accepted as write authority.
Shared-gate acquisition synchronously invalidates cached path
maxima and refreshes scraper instrument support before lookup-bearing
backfill/history/band work;
metadata-only HTTP activity and pruning take the same gate without that extra
cache churn. The maintenance owner has one transaction-local owner-token
bypass used only to remove stale negative backfill checks and matching successful
`history_recon_progress` rows for newly promoted path-backed pairs. Matching
history status rows move to `pending`, clear the completion timestamp,
recompute `songs_processed` from preserved rows, clear aggregate season/entry
counters, advance their admission revision, and fence preserved unrelated
progress to that revision.
Only affected accounts are requeued; positive backfill checks and unrelated
history pairs are preserved. Ordinary scrape/publication freezes retain their
existing registration behavior.

Final cache swap, completed checkpoint, and unfreeze run inside one
source-locked transaction on the live lock-owning session. It share-locks both
staging tables and compares every key, ETag, and JSON SHA-256 with
`max_score_maintenance_cache_entries` before any swap. A `caches_staged` or
later pre-complete freeze rejects ordinary cache-build leases and staging
writers. The durable gate is cleared
only after advisory-lock release. Backend loss before the final commit keeps
the old cache, freeze, and durable owner. Backend loss after that commit but
before durable-gate clear keeps the completed cache/publication coherent
and leaves guarded mutations fail-closed. A new validated lease may replace
the stale owner token and either resume the incomplete workflow or complete
the post-commit release.

## Publication ownership

Candidate writes do not become public merely because they were committed to a
table. Publication validates the candidate, prepares generation-bound state,
drains readers, and atomically advances the published pointer.

`publication_path_artifacts` binds one canonical path/max-score row per
publication catalog song, including authoritative null-generation rows, so
published path reads do not depend on the mutable `songs` table. Its schema,
canonical manifest hash, binding contract, retention, read scoping, staged
promotion metadata, the `Scraper:UsePublicationPathArtifacts` and
`Scraper:EnableScrapePassPathGeneration` flags, and the commit-time
compare-and-swap into live `songs` rows are owned by
[Publication path artifact snapshots](../database/PublicationPathArtifactSnapshots.md).

Feature flags support staged migration among legacy mutable rows, snapshot and
overlay readers, per-scope published sources, and generation-aware reads. Role
files intentionally use different read/write settings for `fstservice` and
`fstworker`.

## Phase timing evidence

`scrape_phase_outcomes` is the terminal correctness ledger for named
post-scrape phases and their publication criticality.

`scrape_phase_timings` is append-only operational evidence for finer subphases.
Its bootstrap shape intentionally matches the surviving production relation:

- `BIGSERIAL` primary key;
- scrape, phase, optional subphase/item, timestamps, duration, optional
  row/scope counts, success, and optional error;
- no foreign key in this compatibility repair;
- indexes on `(scrape_id, phase, subphase, item_key)` and
  `started_at DESC`.

Timing persistence is best effort and cannot change phase failure,
cancellation, or publication behavior. Retention remains owned by the existing
service-level metadata cleanup.

For BandMaintenance timing rows, `success=false` means the subphase did not
complete successfully, including cancellation. Optional row/scope metrics are
null on failure because partial work may have occurred. Successful no-work
subphases record zero. For `current_projection_refresh`, `rows_read` stores the
already-known impacted scope count considered and `scope_count` stores scopes
selected for refresh, so `0`/`0` and `N`/`0` remain distinct without another
query or timing row.

PR #47 merges the current-projection query implementation default-off without
changing schema or data ownership. It replaces seven same-key correlated
aggregates over `band_member_stats` with one lateral aggregate that returns the
same ordered member arrays. The table primary key is
`(song_id, band_type, team_key, instrument_combo, member_index)`, so duplicate
or tied `member_index` values are structurally impossible inside the exact key
used by either projection query. No secondary array-order key is needed.
Nullable stat columns retain the existing `-1` sentinel and missing member
rows retain shorter or empty arrays in both paths.

PostgreSQL remains authoritative; scope selection, candidate-generation
deletion, per-scope transaction boundaries, scope/global state, generation
publication, cleanup, and row ordering are unchanged. Production enablement
still requires a capacity-safe matched full-scrape A/B.

Live scrape `1293` validated the compatibility shape and bounded write cost:
the two prior comparable scrapes contained `69` timing rows each, while `1293`
contained `72`, exactly the three new BandMaintenance rows. Their stored tuple
size was about `411` bytes in total. Whole-phase reconciliation left only
`257 ms` (`0.00324%`) outside the timed subphases, a conservative upper bound
for timing-persistence overhead and well below the `1%` acceptance gate.

## Durable phase-attempt ledger

`scrape_phase_attempts` complements rather than replaces
`scrape_phase_outcomes`, `scrape_phase_timings`, and
`service_worker_status.current_operation_json`.

Its primary key is `(scrape_id, phase_id, attempt)`. Typed columns retain the
stable operation/phase IDs, ordinal and plan version, worker instance,
subphase, terminal/running status, units and denominator-final flag, exact
phase percent, conservative overall/ETA fields, start/progress/heartbeat/end
timestamps, safe build/config hashes, and warning/error text. It intentionally
has no foreign key so startup is additive and rollback does not couple scrape
history deletion to telemetry. An FK and explicit row-retention lifecycle are
an L3 follow-up requiring measured growth, scrape-log retention, delete-lock,
and rollback evidence; they are not part of PR #15.

The same row now stores subphase telemetry independently from parent phase
progress: `current_subphase_epoch`, `subphase_sequence`,
`subphase_progress_kind`, `subphase_units_kind`,
`subphase_units_completed`, `subphase_units_total`,
`subphase_units_total_final`, `subphase_percent`,
`subphase_started_at`, and `subphase_last_progress_at`. Exact fields are
populated only for a final valid denominator; indeterminate and
not-applicable rows keep numeric fields null. Worker-instance and increasing
sequence predicates fence updates.

Indexes follow the actual paths:

- active service-info/watchdog lookup by scrape, `last_progress_at`, ordinal,
  and attempt;
- orphan interruption by running worker instance;
- successful same-plan/config history for ETA sampling.

The current row is updated rather than appended for every progress tick.
Expected writes are one start and terminal update per phase, subphase
transitions, a maximum one meaningful progress update per five seconds, and
one heartbeat-only update per worker heartbeat interval. Progress updates use
the greater of the stored and observed progress timestamps, so a backwards
clock step cannot regress `last_progress_at` or violate its start-time check.
When multiple attempts run in parallel, the operational service-info read
selects the lowest phase ordinal and then newest attempt deterministically.

`service_worker_status` remains the rolling-upgrade fallback. Heartbeat claims
are ordered by worker start time, and activity writes from a known instance
must match the stored instance and not regress `updated_at`. An older process
therefore cannot overwrite a newer worker's status or operation JSON.

Accepted scrape `1296` produced 24 attempt rows across 22 phase IDs, 2,068
updates, and a 212,992-byte relation (106,496-byte heap and 65,536-byte
indexes). It ended with zero running, interrupted, cancelled, orphaned, or
null-completion rows. The matched wall-clock upper bound for all PR-2 overhead
was `0.0696%`; summed terminal phase outcomes differed by `0.736%`.

## Generation-child report-only retention and archive evidence

The additive snapshot-generation control plane is default-off and structurally
report-only. It stores immutable cycles, exact physical-child observations,
safe-point deferrals, explicit holds, and append-only hash-chain evidence. It
has no job relation, operation kind, lease, executor state, or service method
that can archive, detach, rename, drop, truncate, or delete a child.

A separate operator tool can copy one cryptographically authenticated planner
candidate into a custom-format PostgreSQL archive and prove that package in an
isolated PostgreSQL 17 container. It rebuilds the newest cycle's full
observation sets, stable/cycle hashes, and evidence chain rather than trusting
stored candidate flags. It is not a service executor and cannot change source
catalog or row state. Accepted packages contain the exact top parent,
instrument parent, and numeric child plus source identity, catalog,
before/after fences, row count/fingerprint, TOC, manifest, and checksums. No
duplicate plaintext row export is created.

The evidence verifier reproduces `TierZeroCanonicalJson` byte-for-byte,
including default `Utf8JsonWriter` string escaping and explicit persisted
planner order. Production record-shaped validation arrays remain unchanged in
the summary hash; their exact computed `comparisonKey` values alone drive
planner/oracle agreement. A C# fixture references the actual FSTService record
types and serializer, including numeric-child keys with embedded JSON quotes.

Packages use one dedicated FST evidence root. Source PGDATA, tablespaces,
container mounts, and Docker root are resolved before package creation and
cannot overlap it by equality, ancestry, bind alias, mount FS-root identity, or
nested mount boundary. Source commands are pinned to the discovered immutable
container ID and revalidate full provenance before and after streaming. A
shared reservation lock serializes archive/proof capacity; current physical
size and free space are rechecked at admission.

The reservation lock is pre-provisioned outside the archive root and opened
read-only without creation. Public commands pin archive-root/protected mount
identity before lock acquisition and revalidate after locking and before the
first output write.

The proof container has network mode `none`, no published ports, bounded
resources, and a read-only package mount. Restore validation covers the exact
logical hierarchy, columns, defaults, nullability, constraints, indexes,
options, access methods, tablespaces, expected restore owner, row count, and
SHA-256 row fingerprint. The exact PGDATA bind and `data_directory` must agree,
and unexpected data volumes fail. Label-based cleanup proves container absence
before removing PGDATA and writes final cleanup/rejection evidence. Structured
Docker bind mounts avoid colon ambiguity; anonymous image-declared volumes are
captured and must disappear through `docker rm -f -v`. The archive and
checksummed proof manifest remain durable evidence.

Archive authentication directly binds catalog bytes twice: `SHA256SUMS`
authenticates the actual `catalog.json`, and the validator requires
`manifest.catalog.sha256` to equal that same digest. The immutable quarantine
operation separately binds the authenticated manifest bytes. A recomputed
package/proof checksum set with a false manifest catalog digest therefore
still fails closed.

The semantic reader accepts the two authenticated catalog encodings already
produced in production: relation/index/parent OIDs and relfilenodes may be JSON
numbers or canonical unsigned decimal strings, and cycle-16
`opclassOids`/`collationOids` string arrays normalize to the same values as
numeric arrays. The parser accepts ASCII digits only, rejects signs,
whitespace, leading zeroes, values outside PostgreSQL's OID range, and zero
for positive identifiers. Zero remains valid only for storage-less relation
nodes and noncollatable collation entries. Counts, key attnums, and index
options remain strict JSON numbers. Older cycle-14 catalogs without optional
index metadata and newer cycle-16 catalogs with that metadata therefore
produce the same semantic evidence when their physical structure is equal.

Prospective proof storage is validated before any write beneath `proofs` or
scratch. Existing/prospective parent mount identity, nested boundaries, and
protected-source aliases are checked first and revalidated immediately before
the proof directory is atomically reserved.

The first live package/proof canary used the newest cycle-`9` smallest
candidate, Pro Cymbals snapshot `1314`. The attached source remained
`4,628,480` bytes and `8,627` rows with unchanged OID/relfilenode. Its
`359,470`-byte custom archive has SHA-256
`0187f8894222846c9040c60461001643c9cd908cd830b1c0fad5c190dba8e5de`.
The network-none PostgreSQL 17 restore reproduced the exact row SHA-256
`89bb111ca53eb905c344f113a3668102b8ad9a0fc5581cb585d6fb5004a81c29`
and logical catalog SHA-256
`dce534bec2cd70afe873ccd5cc0c327d636bc93137839b07f20ee57631908501`,
then proved complete container, anonymous-volume, PGDATA, and scratch cleanup.
No source relation or row changed.

The next repository tier adds a separate no-Docker-socket
quarantine/reattach CLI. Its durable control plane is additive:

- `snapshot_generation_quarantine_operations` stores one immutable sealed-plan
  execution and exact child/default-partition identities;
- `snapshot_generation_quarantine_reattachments` stores the one immutable
  rollback completion;
- `snapshot_generation_quarantine_attestations` stores immutable
  `quarantined`, `soak`, and `reattached` parity observations;
- `snapshot_generation_quarantine_index_renames` stores immutable
  role-classified old/new leaf-index and PK-constraint names, exact
  OID/relfilenode, name-insensitive semantics, phase, and transaction
  identity;
- `fst_snapshot_quarantine` owns detached children during a bounded canary.

Quarantine and its `retention_in_flight` hold commit atomically. Before detach,
the empty instrument DEFAULT child receives a validated
`CHECK (snapshot_id <> G)` so later writes cannot silently route into it.
The detached child receives `CHECK (snapshot_id = G)` plus a mutation-rejection
trigger. After detach and before the schema move, quarantine structurally
proves exactly one supported PK and score btree index and renames their
existing OIDs to `sgqi_<full-operation-id>_{pk|score}`; renaming the PK index
renames its constraint. Reattach is the only implemented source transition: it
requires
successful original-publication quarantine evidence, successful soak evidence
for the current publication, zero current liveness roots, exact physical
identity, and both required index attachment chains. It then restores the same
child and removes both temporary checks in one transaction. Pre-change
operations are repaired through the same exact role-based normalization before
`SET SCHEMA`; unrelated public objects are never renamed, dropped, or rebuilt.

The CLI has no service scheduler, Docker access, arbitrary relation/SQL input,
drop, truncate, delete, or automatic-retirement path. Its first live canary
accepted Pro Cymbals snapshot `1314`: the child remained quarantined for 452
seconds, passed 11 public-health samples and three exact 55-route
attestations, then reattached with unchanged OID, relfilenode, 8,627 rows,
4,628,480 bytes, row SHA-256, and both required index links. That result
authorized implementation, but not live execution, of the separately gated
non-cascading drop tier.

The subsequent DROP tier remains outside that executable. Its additive schema
stores immutable drop, route-attestation, logical-restore, finalization, and
hash-chain evidence. An exact Q1 publication-rotation rehearsal and a distinct
active Q2 quarantine are mandatory. The Docker-free drop binary derives the
private relation only from Q2 database evidence, retains the
`retention_in_flight` hold, retains the already validated Q2 DEFAULT exclusion
under its existing deterministic name, and executes one non-cascading table
DROP. At the final destructive boundary, while holding only the private child
in `ACCESS EXCLUSIVE`, it rebuilds the detached structural index inventory and
requires exact role, index OID, relfilenode, and operation-scoped name matches
to both immutable quarantine rename rows. Any renamed, rebuilt, reindexed,
role-swapped, extra, or missing private index rejects the transaction without
DROP residue. Avoiding a constraint rename keeps the live-tree lock at explicit
`SHARE` on only that DEFAULT child. It has no service, worker, API, batch, or
automatic caller. Its database functions are `SECURITY INVOKER`, remain
revoked from `PUBLIC`, and receive no repository-defined role grants.

These additive evidence tables have explicit schema evolution. PostgreSQL
does not reconcile an existing relation with a newer
`CREATE TABLE IF NOT EXISTS` declaration. The first live DROP attempt reached
the database function with an empty initial-revision operation table and
failed before DDL with `42703` because nine semantic columns were absent; no
operation row or child DROP occurred. Initialization now gates on row count:
an empty pre-semantic drop table receives all nine exact `NOT NULL` columns
and rebuilt current hash/identity constraints, while any nonempty
pre-semantic table raises `55000` without fabricating evidence. The restore
operation table applies the same rule for its four later semantic/index
columns and three authorization identity columns. Audit confirmed no
post-initial column evolution in drop/restore
attestations, restore finalizations, or the drop evidence chain.

After that empty-table upgrade, DROP operation
`333ba4b9fb69dbc098d127f0008ec709` committed with plan digest
`fa45ca20c2c975e543b7d539d3b27cb05c5d80ff16345665205f2355eb67d5dc`.
The source child is therefore physically absent and recovery ownership is the
logical-restore tier. Its first restore-plan attempt stopped before output or
mutation when Python object reserialization produced a non-authoritative plan
digest. Restore validation now removes only the two top-level identity member
spans from the original ordinal-sorted C# canonical UTF-8 object and hashes
the untouched remaining bytes. The immutable database DROP row remains the
later state anchor; it does not replace file-integrity verification.

The corrective recovery path adds immutable
`snapshot_generation_restore_tool_authorizations`. It is FK-bound to one DROP
and records the original pin/bundle, reviewed validator base, final executing
tool, byte-identical helper, authorizer, repair-package, repository
commit/tree/diffs/source, tests, and dual approval. The still-empty restore
operation table receives `pinned_tool_sha256`, `executing_tool_sha256`, and a
nullable authorization ID through an explicit empty-only upgrade. A composite
FK and unique consumption constraint prevent cross-DROP or repeated use.
Pinned restores require no authorization; replacement restores require the
exact row at plan, load, and attach boundaries. No authorization check changes
row, route, TOC, catalog, capacity, index, or topology validation.

The first authorized H3 plan lookup failed read-only because its generated SQL
used PostgreSQL keyword `authorization` as a table alias. It created no
restore plan, list, or database row. That authorization remains immutable and
unused. H4 uses the explicit `auth_row` alias through the one shared resolver
used by plan, restore, confirm, attest, and finalize. A PostgreSQL-backed test
executes that exact generated query, and the existing schema permits a second
authorization for the same DROP when the executing-tool hash differs and no
restore has consumed either authorization. This correction needs no schema
change.

The authorized H4 plan next stopped read-only at fixed-index validation:
cycle-16 `catalog.json` stores PostgreSQL opclass and collation OIDs as
canonical decimal strings, while H4 compared them directly with integer
arrays. H5 accepts only JSON integers or canonical unsigned decimal strings,
rejects booleans, signs, whitespace, leading zeroes, fractions, exponents, and
values above `uint32`, and permits zero only for collation OIDs. It normalizes
those arrays for PostgreSQL-17 fixed-shape and child/root/top equality without
coercing counts, options, or key attnums. Exact live Q2 catalog/TOC fixtures
cover the authorized plan path. H3 and H4 authorizations remain immutable and
unused; a third H5 authorization can coexist without a schema change.

H5 subsequently committed restore operation
`da07c4ce4d07b9692a45a1498313f8b3` with new child OID/relfilenode
`321906645`, exact rows/fingerprint/bound/semantic hashes, and two attached
index chains. Its route attestation stopped before database write because the
Python restore tool compared raw ZIP export hashes. The raw captures remain
authenticated; their differences are limited to generated XLSX names, Office
core-property relationships, and random core-property part names. The shared
C# canonical ZIP validator proves identical band/player export semantics.

Post-restore acceptance is therefore a separate storage/control-plane tier.
`snapshot_generation_restore_continuation_authorizations` immutably binds one
restore row and predecessor restore-tool authorization to one
continuation-only C# tool, evidence assembly, package, route pair, repository
diff, and dual approval. Empty restore-attestation/finalization tables gain
the continuation tool/authorization FKs; attestation also stores semantic
binary parity, the fixed canonical-ZIP algorithm ID, and route semantic
evidence hash. No column or update touches the committed restore row.

`FstSnapshotGenerationRestoreContinuation` exposes only confirm, attest, and
finalize over direct Npgsql. It references the shared
`FstSnapshotGenerationEvidence` assembly and calls the existing
`QuarantineEvidenceValidator`; no Python ZIP port exists. The package
references immutable H5 and route evidence by hashes and contains no archive,
route body, export workbook, Docker helper, or physical restore executable.
Finalization requires the exact same continuation authorization recorded by
attestation before atomically removing the mutation trigger and releasing the
hold.

Rolling deployment explicitly drops only the known 13-argument live and
16-argument intermediate restore-function overloads before defining the
21-argument authorization-aware signature. The authorization row stores both
the client canonical evidence
SHA-256 and a PostgreSQL-computed SHA-256 of `canonical_evidence::text`; its
ID covers both plus every substantive provenance hash. This preserves
independent database verifiability without claiming C# canonical bytes equal
JSONB textual encoding.

The generation-creation function now checks active `retention_in_flight` and
`restore_in_flight` holds before returning or creating a generation and again
after its partition-DDL lock, plus committed DROP evidence when that additive
schema exists. A restored child remains write-blocked until attestation and
finalization, and an absent protected or physically retired generation cannot
be silently replaced even if its hold were released incorrectly.
After finalization, the tombstone admits only the recorded restored OID.
Relfilenode remains immutable historical evidence but is not a write-path
identity requirement because `VACUUM FULL`, `CLUSTER`, or a supported repack
can rewrite it without replacing the relation.

Logical recovery is a separate Python tool that reuses the archive tool's
package, mount, PostgreSQL 17, and Docker helpers without changing the
archive-only CLI. It selects only the archived child table, data, primary-key
constraint, and secondary index TOC entries for authentication, but executes
only table and table data. Archived index DDL is provenance, never executable.
After strict fixed-shape validation, a short guarded transaction creates
`sgri_<full-restore-operation-id>_{pk|score}` from repository-owned SQL,
promotes the PK with `USING INDEX`, and attaches the relation. Restored OID and
relfilenode are expected to change; schema/name, parent/bound, rows, ordered
row SHA-256, name-insensitive semantic catalog, and both index chains must
match. Existing unrelated objects with archived names remain untouched. The
hold is released only
after an immutable restored-route/data/catalog attestation and explicit
finalization; the restore mutation trigger is removed atomically with that
release.

Leaf index and index-backed constraint names are not semantic child identity.
Each archive's raw archive/catalog/config hashes remain independently
authenticated provenance, but raw custom-format archive bytes can differ for
equivalent packages. Q1/Q2 equality therefore uses stable child identity,
exact rows/table identity, a versioned name-insensitive catalog/index
projection, and exact leaf/root/top index OID topology.

Official confirmation scrape `1333` is accepted: 710 songs, 41,154,968
entries, 608,691 requests, 92,821,715,390 bytes, 8,520/8,520 complete
manifests across 12 instruments, zero critical or best-effort failures, zero
writer failures, and no retry-exhausted or failure-reason outcome.
Publication `157` became current with 6,390
published solo source bindings, first-attempt completed notifications,
unfrozen reads, and no working publication, commit intent, or max-score gate.
Cycle `13` is immutable report-only evidence for scrape `1333`/publication
`157`: exact planner/oracle agreement, 111 candidates, 174 protected, zero
blocked/global blockers, and 194,754,322,432 candidate bytes. Pro Cymbals
`1314` was its true smallest candidate at 4,628,480 bytes; Solo Bass
`1308` remains protected by `unreplayed_writer_failure` with stable identity
`4e3310328261704da558e6d83f99cbc77bc01cef10abbac0840df471d33809cc`.
Production continued automatically into scrape `1334`.

Q1 operation `1b44941dc5d5ea806dabc2187c3cffed` passed the successful
scrape-1335/publication-159-to-162 rotation, exact cycle `15`, and
publication-162 55-route soak. Its initial reattach failed closed with
`42P07` when a new Solo Guitar child reused the target's former secondary
index name. The transaction committed no residue; the Pro Cymbals child
remained private at OID/relfilenode `319748510` with its hold and fences at
that incident boundary. Later live progression reached a separately approved
DROP function call, so the earlier pending-recovery statement is no longer
current; the exact intervening acceptance evidence remains operator-owned.
The DROP itself still did not occur because the schema upgrade failed closed.

This capability remains unaccepted for promotion or automation. Any retry is
blocked on the reviewed empty-table schema upgrade, rebuilt artifacts, current
live-state/evidence revalidation, and explicit operator authorization. See
[Snapshot generation DROP and logical restore](../database/SnapshotGenerationDropRunbook.md).

Physical identity and liveness are per `(instrument, snapshot_id)` and exact
OID/relfilenode/bound/name/schema configuration. Stable child/config hashes
exclude row/byte estimates; a separate observation hash includes those
metrics. One bounded `REPEATABLE READ`, `READ ONLY` transaction captures all
topology and liveness rows. A separately implemented SQL oracle independently
derives exact child/live/unreferenced sets; any mismatch persists both sides
and forces zero candidates.

Topology validation covers the complete partitioned-index tree. The top
snapshot parent must retain its required primary and score indexes; every
instrument root must own exactly one valid/ready partitioned index attached to
each top index; and its default and numeric children must each own exactly one
valid/ready regular index attached to every root index with matching
primary/unique/access-method attributes. The primary
`pg_inherits` inventory and independent oracle `pg_partition_tree` inventory
persist and compare canonical validation facts for every numeric child,
including missing, duplicate, detached, invalid, unready, and attribute-
mismatched indexes, plus childless roots.

Roots include active rows, projection sources, named current/previous/working
publication sources, running/configured-resume scrapes, same-instrument
unreplayed writer failures, and active holds. Named publication roots are
accepted only when their ready binding, preparation identity/count, and
canonical SHA-256 source-key set exactly match every source row; the independent
SQL oracle validates the same facts separately. Instrument topology failures
remain cycle-global without numeric children, and trigger/named/physical-child
scrape provenance is independently terminal-validated even for otherwise
protected children. Unpointed building/ready/current generations are blockers.
Planner version 3 inventories every failed publication's exact scrape status,
pointer/recovery references, surface bindings, caches and staging, catalog,
path and scrape-staging rows, prepared/retained band relations, source rows,
and unreplayed writer failures. A named, resumable, nonterminal/malformed, or
live-artifact-backed failed publication remains a global blocker. An exact
terminal unnamed failure with no live recovery artifact is instead immutable
`unpointed_terminal_failed_publication` anomaly evidence; orphaned source-map
rows remain counted provenance, and writer failures retain only their separate
instrument/generation root. Unnamed retained legacy generations likewise
remain nonblocking `unpointed_retained_publication` anomalies introduced by
planner version 2. Canonical anomaly/blocker evidence participates in the
observation hash without changing candidate identity. Version 1 and 2 cycles
remain immutable and are never reclassified. Freeze, commit intent, max-score
mutation, notification, broadcast, registration-drain, and
background-quiescence state also fail closed. The temporary max-score evidence
table is never queried.

Registration drain inventory separates runnable `pending`/`in_progress`/
`deferred` backfills and safely re-admittable missing/error history work from
non-runnable missing/error/unknown backfill state. The worker reads these
counts through one command-timeout-bounded aggregate query. Runnable work wakes
the registration claimant and receives a bounded adaptive drain window before
background pause; safe-point polling never repeatedly cancels and discards a
long staged batch. If the window expires, the queue remains durable and the
next scrape may proceed without recording a cycle. Non-runnable state bypasses
that wait and persists a cycle-global blocker with counts rather than account
identifiers, allowing the FIFO to advance without making any child a
candidate. Malformed terminal notification state follows the same blocker
rule; only exact `pending`/`running`/`failed` notification state is retryable.

Publication rotation retires servable generation state separately from
scrape/evidence retention. Once a generation leaves
current/previous/working, bounded post-commit cleanup verifies its exact ready
scope-source binding, removes only its publication-owned source rows and
servable cache/catalog/path/band artifacts, marks every binding retired, stores
`retired_at` plus `retired_scrape_id`, and clears `scrape_id`. Startup cleanup
and metadata TTL can retry that exact transition after interruption.

Immutable report cycles/deferrals/observations, active holds, unreplayed writer
failures, and newer publication rows that cite the retired scrape remain
unchanged. Their restrictive references continue to block scrape-log aging and
protect physical children, but no longer force a normal third publication to
remain `unpointed_retained`. Missing/legacy/partial binding evidence still
leaves the generation retained, but that stale-bookkeeping row is reported as
a nonblocking anomaly rather than laundering or suppressing candidate
classification. The observer never deletes or rewrites those legacy
generation, binding, or source rows merely to clear the warning. Metadata TTL
shares the centralized service-maintenance lock with the observer, and its
scrape-delete predicate independently retains all of those provenance
references.

Existing databases receive `retired_at` and `retired_scrape_id` only through a
short transactional migration with bounded lock/statement/command timeouts.
Their partial lookup index is built separately with
`CREATE INDEX CONCURRENTLY` on a bounded, advisory-lock-serialized session.
Exact catalog validation makes a valid index a no-op; an invalid artifact from
a timed-out attempt is concurrently dropped and rebuilt on retry. The
unbounded main-publication bootstrap performs neither retirement `ALTER` nor
retirement-index work.

Publication-to-scrape protection is rolling compatible. The legacy
`publication_generations_scrape_id_fkey` may remain `ON DELETE CASCADE` because
an old `c35b7f47` binary can recreate that exact constraint. A separately named
validated `publication_generations_scrape_id_restrict_fkey_v2` plus a
`BEFORE DELETE` guard on `scrape_log` are installed additively in the bounded
foreign-key migration; the old initializer does not know either invariant.
The legacy FK is never dropped during cutover, and a timeout rolls back the
additive transaction, so no migration step exposes an unreferenced window.
The previous-publication foreign keys retain their bounded exact upgrades.

Scrape-boundary staging cleanup follows the same provenance rule. It may remove
obsolete staging and deep-scrape rows, but an older incomplete `running`
scrape and its allocated generation become explicit
`abandoned_staging_cleanup` failures rather than being deleted.

See
[Snapshot generation retention safety](../database/SnapshotGenerationRetentionSafety.md).

## Legacy whole-instrument snapshot retention planning evidence

`DatabaseMaintenanceDryRunReporter` estimates partition-local keep-only
rewrites from bounded PostgreSQL catalogs and `pg_stats`; report-only planning
does not scan snapshot partitions. Its rewrite option remains disabled and it
is not the generation-child deletion oracle.

The estimator is fail-closed:

- retained rows include both policy `Keep` and `Blocked` snapshot IDs;
- active, current-projection-source, publication-physical-source, and
  rollback-protected IDs are present in every partition plan even when absent
  from `most_common_vals`;
- publication physical sources are resolved only from current, previous, and
  working publication IDs through `publication_generations.scrape_id` and
  `leaderboard_published_scope_source.published_scrape_id`; source maps for
  older unnamed generations do not pin hot snapshots forever;
- positive `n_distinct`, negative fraction-of-row `n_distinct`, zero/unknown
  values, MCV/frequency length, frequency remainder, and the drift between
  `n_live_tup` and `reltuples` all contribute to statistics safety;
- MCV row/byte estimates plus an explicit unknown remainder reconcile to one
  conservative row total and the relation's total bytes. Floor-rounding
  residual is retained, never purged;
- complete statistics allow at most `max(1, MCV count)` residual rows and
  `max(4096 bytes, MCV count)` residual bytes from floor rounding, and require
  nonzero `n_live_tup` versus `reltuples` drift to stay within `10%`;
- if protected estimates are missing or statistics are partial, stale, or
  inconsistent, executable purge rows/bytes are zero, retained/workspace
  estimates become the full partition, and `CanExecute=false`;
- informational candidate-purge estimates remain separate from executable
  estimates.

A truly empty partition may report zero retained rows, but it is not a rewrite
candidate. Exact row scans remain confined to the separately guarded execution
preflight and are never introduced into report-only planning.

The live read-only candidate on publication `1293` completed in `94 ms`
without locks or public degradation. All nine partitions contained protected
IDs `1293` and `1291` that were absent from MCV statistics, so every plan
failed closed. Estimated rows and bytes reconciled, but conservative retained
workspace became the full `2,607,232,278,528` bytes; executable purge
rows/bytes were zero. The separately labeled informational candidate was about
`2.52` billion rows / `1.46 TB`, with about `392 GB` outside MCV coverage.
Those values are not reclaim proof.

### Accepted pro-bass archive/rewrite

The exact pro-bass transition completed live on 2026-08-18. The generic
report-only planner remains unchanged and the global `500 GiB`
rewrite policy is not reduced. The exact
`leaderboard_entries_snapshot_pro_bass` pilot uses a separate one-target
orchestrator with no arbitrary relation input.

Its `plan` stage scans only the exact partition and derives retained IDs from
authoritative active/projection/publication/rollback ownership. Modern purge
IDs require terminal `scrape_log` ownership. Legacy missing/failed ownership is
accepted only when the exact verified archive contains the unchanged ID/content
and no named current/previous/working source map references it. Production
enumeration uses leading-index `MIN(snapshot_id)` probes and metadata joins,
not a historical-row aggregate. Exact total rows/ranges come from the
checksummed verified archive restore; PostgreSQL calculates exact
counts/ranges/fingerprints only for protected IDs. Source/reference parity and
the original column/constraint/index/owner/tablespace catalog remain required.

The original relation is streamed as a PostgreSQL custom archive directly to
an explicitly authorized temporary directory on `/dev/nvme2n1p2`. The archive
contains the partitioned parent, pro-bass child, data, attach records,
constraints, primary/score indexes, and ownership. It is checksum-verified and
restored in isolated network-none PostgreSQL 17 before any build is eligible.

The replacement may be built in one run-owned temporary 8 TB tablespace when
the dual-filesystem capacity gate passes. A short-lock transaction detaches and
retains the original, then renames/attaches the replacement. Validation occurs
while rename-back rollback is available. Final old-relation drop is separate;
before it, `repatriate` copies/swaps the retained relation to `pg_default`
while the original still provides rollback and retains the scratch candidate
as a second rollback relation. Only after repatriation fingerprint/reference/
catalog/API parity passes does final drop remove both rollback relations,
normalize names, and remove the temporary tablespace. The restore-drilled
archive remains on 8 TB through
acceptance and a later explicit retention decision.

The final 180,000-row drill measured a 75,415,552-byte original,
19,636,224-byte replacement, 20,423,824 scratch-build WAL bytes, 8,429,568
temp bytes, 19,689,472 peak scratch growth, and 95,043,584 immediate bytes
returned when the original plus scratch rollback relation were dropped.
Rename-back, verified-archive adoption, repatriation to `pg_default`, durable
copy/swap evidence, torn-evidence recovery, scratch tablespace removal, and
final-drop paths all passed.

The read-only live archive is `11,942,257,904` bytes with checksum
`3decc75ffe33e24dad72e379fb874c7b0c7b4a421121de6a227acd0fe344760f`.
Its verified input checksum is
`483cf15e12df3f0fcda370f6fc5ee969b450b8c4f1eeb2c291f7ec2201326c15`;
it binds 308,536,699 restored rows, exact per-snapshot counts/content hashes,
and the full canonical restored catalog.
Its isolated restore proved `308,536,699` exact rows across 125 snapshot IDs
(`769-1302`), the exact partition/primary/score-index catalog, and a packed
`129,666,588,672`-byte restored relation. The first restore shape was rejected
for a duplicate child primary key; the successful retry built child indexes
while detached and attached afterward. The `130,771,858,177`-byte restore
PGDATA was deleted after validation while the archive remained.

Using the exact archive row count, `6,691,993` protected rows, live heap/index
bytes, and measured ratios projects a `2,685,343,018`-byte replacement and a
`69,713,820,289`-byte free-space requirement. At current free space
`68,545,114,112`, the exact-row direct-build projection is short by
`1,168,706,177` bytes. The older approximately `3.4 GB`
retained-size sensitivity remains a conservative `72.19-73.06 GB` requirement.
The temporary-tablespace candidate instead requires `63,889,690,620` free on
4 TB and `17,260,886,072` on scratch, so its capacity math passes narrowly at
the accepted free-space assumptions. Pre-drop repatriation requires
`66,575,033,638`; current projected margin is `1,970,080,474`. The live run retained IDs `1301-1302`, removed `301,844,706` rows from hot
storage, returned `152,985,165,824` filesystem bytes, and left a
`2,811,404,288`-byte `pg_default` partition. The scratch tablespace and mount
are absent; the verified archive remains. See the
[pilot runbook](../database/ProBassSnapshotRewritePilot.md) for evidence and
recovery.

Validation scrape `1303` then reused 1,717 scopes / 6,112,541 rows globally.
Pro bass reused 350 scopes / 1,436,731 rows from `1302`, wrote 1,910,331 rows
for `1303`, and grew by `1,000,898,560` bytes. The worker-role default now
enables fingerprints and unchanged-snapshot reuse. The subsequent generation
migration removed obsolete `1301` and converted the retained physical IDs
into independently droppable children.

### Snapshot-generation subpartition layout

Fresh schemas partition `leaderboard_entries_snapshot` by instrument and then
partition each instrument by `snapshot_id`. A retained generation child owns
its heap plus primary/score leaf indexes; an empty default child preserves
diagnostic/test compatibility.

Before snapshot insertion, the worker calls the fixed
`ensure_leaderboard_snapshot_generation_partition(instrument, snapshot_id)`
helper. It accepts only the nine supported instruments, serializes concurrent
creation with an advisory transaction lock, validates the child bound, and
returns without mutation while a live instrument still uses the legacy regular
partition. This permits code deployment before the guarded per-instrument
migration.

The one-time per-instrument migration retains an independent read-only copy of
the restore-proved archive and its plan/archive/restore/validation evidence on
the authorized temporary scratch device. Kernel read leases protect both
source and recovery files through destructive commit and durable reporting.
An anchored, read-only package plus a pre-commit recovery manifest allows a
torn committed drop to be reported without trusting the original archive; the
independent package remains authoritative until the separate archive-deletion
decision.

`pro-bass` completed this conversion on 2026-08-18. The accepted migration
removed `3,345,859` obsolete `1301` rows and returned `3,812,192,256`
filesystem bytes while exact publication/reference/API parity remained
unchanged. Validation scrape `1304` added `1,395,539` rows in a
`726,654,976`-byte dedicated child. Its live tree now contains snapshot
children `1302-1304` plus an empty default and occupies `2,940,837,888`
bytes.

`pro-guitar` completed the same conversion later on 2026-08-18. The exact
archive contained `1,015,961,791` rows across 245 generations; only
`9,239,429` rows from `1302-1303` remained protected. The accepted run removed
`1,006,722,362` hot rows, returned `588,232,740,864` filesystem bytes, and
left a `4,074,053,632`-byte `pg_default` tree with an empty default child and
no migration artifacts. Validation scrape `1304` added `3,674,245` rows in a
`2,013,806,592`-byte dedicated child. Its live tree now contains snapshot
children `1302-1304` plus an empty default and occupies `6,087,860,224`
bytes.

`solo-guitar` completed on 2026-08-20. The exact archive contained
`902,057,650` rows across 172 generations; `17,888,406` rows from
`1302-1304` remained protected. The accepted run removed `884,169,244` hot
rows, returned `445,956,923,392` filesystem bytes, and left a
`7,126,245,376`-byte `pg_default` tree with an empty default child. Validation
scrape `1305` added `5,632,637` rows in a dedicated `1305` child and completed
through publication, notifications, registration drain, and normal worker
exit.

`solo-vocals` completed on 2026-08-21. The restore proved
`912,731,557` exact rows across 174 generations; `23,925,998` rows from
`1302-1305` remained protected. The accepted run removed `888,805,559` hot
rows, returned `445,096,439,808` filesystem bytes, and left a
`9,389,801,472`-byte `pg_default` tree with an empty default child and no
migration artifacts.

`solo-drums` completed later on 2026-08-21. The isolated restore proved
`904,310,454` exact rows across 176 generations; `28,959,242` rows from
`1302-1306` remained protected. The accepted run removed `875,351,212` hot
rows, returned `428,561,866,752` filesystem bytes, and left an
`11,075,510,272`-byte `pg_default` tree with an empty default child and no
migration artifacts. Four instrument partitions remain on the legacy
regular-table layout.

Scrape `1304` proved the mixed-layout writer in production. All `8,448` scope
manifests and `603,015` persisted page statuses completed successfully.
Published source rows mapped exactly to the two new generation-child row
counts, all `6,336` published solo scopes were complete, publication `92`
advanced and unfroze, notification recovery and post-publication registration
drain completed, and the run-once worker exited normally. The worker is held
before the next single-instrument migration. Scrape `1305` subsequently proved
the Pro Bass, Pro Guitar, and Solo Guitar `1305` children through the same
terminal gates.

Scrape `1306` then proved all four migrated instruments through publication,
notifications, post-publication drain, and exit. Published-source sums match
the physical `1306` children exactly: Pro Bass `1,738,972`, Pro Guitar
`3,484,122`, Solo Guitar `5,227,744`, and Solo Vocals `5,380,894`; every
default child remained empty. The worker was held before the Solo Drums
migration.

Scrape `1307` then proved all five migrated instruments through publication,
notification recovery, post-publication registration drain, and exit.
Published-source sums match the physical `1307` children exactly: Pro Bass
`1,652,632`, Pro Guitar `3,463,985`, Solo Guitar `4,761,707`, Solo Vocals
`4,910,955`, and Solo Drums `4,842,176`; every default child remains empty.
Their complete live trees occupy `5,628,272,640`, `12,227,526,656`,
`14,962,409,472`, `14,549,868,544`, and `13,431,971,840` bytes respectively.
All `6,363` publication sources are complete, publication `98` is current and
unfrozen, public routes are healthy, and the run-once worker exited `0` and
remains held offline.

Solo Bass was subsequently converted under that worker hold. Its exact
archive/restore contained `895,497,806` rows, retained `28,995,467` rows from
snapshots `1302-1307`, and removed `866,502,339` obsolete rows. The final
`11,351,261,184`-byte tree and all indexes are in `pg_default`, the default
child is empty, no migration artifact remains, and `429,125,984,256`
filesystem bytes were returned. Scrape `1309` subsequently satisfied the
post-Solo Bass validation gate.

Pro Vocals was then converted during the authorized final migration interval.
Its exact archive/restore contained `633,981,317` rows, retained `34,514,935`
rows from snapshots `1302-1307` and `1309`, and removed `599,466,382`
obsolete rows. The final `15,253,815,296`-byte tree and all indexes are in
`pg_default`, the default child is empty, no migration artifact remains, and
`350,852,210,688` filesystem bytes were returned. Exact retained fingerprints,
publication/reference state, and public API bodies matched before final drop.

Pro Cymbals followed in the same worker hold. Its exact archive/restore
contained `8,661,068` rows, retained `400,455` rows from snapshots `1302-1307`
and `1309`, and removed `8,260,613` obsolete rows. The final live tree occupies
`185,352,192` bytes in `pg_default`, the default child is empty, no migration
artifact remains, and `4,757,069,824` filesystem bytes were returned. Exact
retained fingerprints, publication/reference state, and public API bodies
matched before final drop.

Pro Drums completed the final migration interval. Its exact archive/restore
contained `5,473,658` rows, retained `190,168` rows from snapshots `1302-1307`
and `1309`, and removed `5,283,490` obsolete rows. The final live tree occupies
`86,032,384` bytes in `pg_default`, the default child is empty, no migration
artifact remains, and `2,942,509,056` filesystem bytes were returned. All nine
instrument roots are now snapshot-generation partitioned. Validation scrape
`1310` subsequently accepted all nine generation writer paths.

The first validation attempt, scrape `1308`, completed network collection and
all `2,121` band manifests but failed closed on one 13-row Solo Bass writer
batch. Concurrent first-batch generation DDL for different instruments raced
while PostgreSQL selected truncated inherited-index names, raising SQLSTATE
`23505`. The failed page is retained for replay, publication `98` remains
current and unfrozen, and no post-scrape phase ran. The writer now acquires one
global generation-DDL advisory lock in a separate statement before partition
creation so the post-wait catalog snapshot is current and cross-instrument
index naming is serialized.

Scrape `1309` accepted that retry through publication `101`, notifications,
registration drain, and normal worker exit. All `8,484` manifests and
`604,907` page statuses completed successfully, with zero writer failures.
Published-source sums match the physical `1309` children exactly: Pro Bass
`1,642,317`, Pro Guitar `3,995,405`, Solo Guitar `5,460,593`, Solo Bass
`4,489,655`, Solo Vocals `5,351,184`, and Solo Drums `5,360,563`; all six
default children are empty. Their complete live trees occupy `7,319,396,352`,
`16,611,909,632`, `20,473,880,576`, `15,551,946,752`, `19,875,627,008`, and
`18,626,674,688` bytes respectively.

Publication `101` is current and unfrozen. The run-once worker exited `0`,
PostgreSQL returned to the production `16/20 GiB` envelope, and the final
three-instrument migration interval is accepted. Pro Vocals, Pro Cymbals, and
Pro Drums completed every independent gate; the required all-nine validation
scrape was the next gate.

Scrape `1310` accepted the complete all-nine layout through publication `103`,
notifications, registration drain, and normal worker exit. All `8,484`
manifests and `605,239` persisted page statuses completed successfully, with
zero writer failures. Published-source sums match the physical `1310` children
exactly:

| Instrument | Rows |
|---|---:|
| Pro Bass | `1,577,901` |
| Pro Guitar | `3,818,765` |
| Pro Vocals | `4,988,523` |
| Pro Cymbals | `45,323` |
| Pro Drums | `19,725` |
| Solo Guitar | `5,190,179` |
| Solo Bass | `4,478,546` |
| Solo Vocals | `5,254,600` |
| Solo Drums | `5,369,892` |

Every default child remains empty. The complete live trees now occupy
`8,151,441,408`, `18,735,874,048`, `18,053,455,872`, `210,624,512`,
`96,575,488`, `23,122,640,896`, `17,711,562,752`, `22,520,455,168`, and
`21,252,939,776` bytes respectively. Publication `103` is current and
unfrozen; player notification run `230` emitted `101` events, band run `231`
emitted `47`, the registration drain completed with no queued account, and
the worker exited `0`. PostgreSQL returned to the production `16/20 GiB`
envelope with unchanged OOM/OOM-kill counters `9/2`.

After migration, a separately gated retention owner can archive and drop
obsolete generation children as whole relations. That recurring owner is not
implemented by the layout migration itself. It must preserve
current/previous/working publication sources, active snapshot state, and
projection sources; archive/restore-prove nonempty obsolete children; and keep
the default child empty. Readers continue to query the unchanged parent
relation. Normal scheduling remains held until this recurring lifecycle is
accepted. Normally one guarded run-once scrape is required after each
instrument migration, followed by another worker hold before the next
migration. The only current exception is the operator-authorized final
interval: after a clean post-Solo Bass retry proves all six migrated writer
paths, Pro Vocals, Pro Cymbals, and Pro Drums may be migrated sequentially
under one hold, with every target independently completing the full migration
state machine. All three migrations are accepted. One complete scrape across
all nine instruments was required immediately afterward and is accepted as
scrape `1310`.

The first physical-reference inventory after publication `103` found six
children with no active/projection/named-publication source: the failed-scrape
`1308` leaves for Pro Bass, Pro Guitar, Solo Guitar, Solo Bass, Solo Vocals,
and Solo Drums. That observation is not accepted retention eligibility:
same-instrument unreplayed writer-failure evidence remains a mandatory root
until explicitly replayed/proven. The six children occupy
`12,908,355,584` bytes, while the nine new `1310` children occupy
`15,870,648,320` bytes. Older successful generations remain sparsely pinned by
snapshot reuse; retained publication `101` still uses `621`, `220`, `153`,
`384`, `406`, and `525` scopes from generations `1302-1307`. Whole-child
retirement is therefore the first safe owner, but sparse-child compaction is a
separately gated second phase before storage can be described as bounded.

The existing generic retention service remains the compatibility owner for
unmigrated regular instrument partitions only. It deliberately produces no
legacy rewrite candidate for a generation-subpartitioned root; child retirement
belongs to the future archive-before-child-drop owner.

## Tier-0 replay evidence packages

Tier-0 evidence packages are filesystem artifacts, not PostgreSQL relations and
not a replacement source of truth. Their manifest records source
scrape/publication/catalog identity, producer build and OCI identity,
producer-supplied database/schema facts, allowlisted configuration hash,
stable phase-plan descriptors, summary references, artifact metadata,
checksums, and parent lineage.

Packages are mutable only before sealing. Artifact commit uses a pending
journal record around the same-directory temporary-file rename so an
interrupted writer can validate and finish the transaction. Sealing commits
the exact canonical state-journal bytes, produces an artifact checksum
manifest, and then writes `manifest.json` as the final marker. The root hash
covers canonical manifest metadata while omitting only its own field.
Verification is read-only and detects corruption, missing/extra files or
directories, path/symlink escape, and expected parent/config/schema/phase
mismatches.

No live capture, production database export/import, accepted production replay,
publication binding, or retention automation uses this contract. Future
production-derived packages and replay workspace must stay on the 4 TB FST
drive and receive explicit capacity/retention ownership. See
[Replay evidence artifacts](replay-artifacts.md).

The accepted PR-5 repository capability adds only a synthetic/bounded Tier-1
import into a fresh marker-owned isolated PostgreSQL database. The importer
owns three typed input datasets and the BandMaintenance current-projection
output tables; it rejects all pre-existing public objects, source/production
cluster identity, arbitrary SQL, publication tables, and unlisted datasets.
Isolated replay databases are ephemeral experimental workspaces, never
source-of-truth or restore targets.

## Storage and maintenance rules

- Production data, scratch, exports, repacks, and migration artifacts stay on
  the 4 TB FST drive unless the operator explicitly overrides the rule.
- The operator-authorized generation migrations use `/dev/nvme2n1p2` only for
  temporary archive, isolated restore, and immutable recovery/report files.
  No accepted PostgreSQL relation or permanent FST artifact may use that
  device. Each recovery package remains temporary but retained until a
  separate deletion/retention decision.
- FST free space after the accepted Solo Vocals validation scrape was
  `1,590,535,684,096` bytes; after the accepted Solo Drums final drop it is
  `2,020,845,260,800` bytes; after accepted validation scrape `1307` it is
  `1,994,932,432,896` bytes; after the accepted Solo Bass final drop it is
  `2,426,681,905,152` bytes; after accepted validation scrape `1309` it is
  `2,382,491,947,008` bytes; after the accepted Pro Vocals final drop it is
  `2,721,327,648,768` bytes; after the accepted Pro Cymbals final drop it is
  `2,725,916,037,120` bytes; after the accepted Pro Drums final drop it is
  `2,728,956,956,672` bytes; after accepted validation scrape `1310` it is
  `2,701,792,727,040` bytes. These measured capacities do not reduce any
  migration-specific emergency floor, rollback, archive, or parity gate.
- Destructive maintenance requires exact affected objects, parity evidence,
  rollback, live preflight, and a bounded maintenance window.
- Current-publication max-score correction requires the canonical manifest and
  plan digests, the path-generation/publication lock order, a durable
  maintenance freeze, complete rollback coverage, and atomic cache
  swap/unfreeze. Use the
  [max-score correction runbook](../database/MaxScoreCorrectionMaintenanceRunbook.md).
- Schema initialization is idempotent but is not a substitute for a bounded
  maintenance command.
- Preserve Epic/provider provenance, historical leaderboard correctness,
  publication state, and replay evidence.

## Current procedures

Use the [living runbook index](../operations/runbooks/README.md). Completed
physical cleanup, retirement, compaction, and rejected rollout documents were
removed from the current tree after their current conclusions were captured.
They must not be reintroduced as pending procedures.
