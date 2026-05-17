# Band Rank History vNext

**Date:** May 10, 2026
**Status:** Phase 6C read-source gate implemented

## Problem

Band rank-history catch-up currently runs as a post-scrape sweep over the published aggregate band ranking tables. The sweep is resumable, but it still performs very large local PostgreSQL work:

1. Read current rankings for one `(band_type, ranking_scope, combo_id)` chunk.
2. Recompute a text-based MD5 fingerprint for each row.
3. Join against `band_team_rank_history_latest`.
4. Write changed rows into wide history, narrow points, latest state, and stats history.

The current chunk key is safe but too coarse. A chunk is currently one scope/combo, so `overall` chunks contain millions of rows.

## Current Findings

### Runtime Shape

Measured scrape 770 recovery showed that `overall` chunks dominate wall time:

| Band type | Scope | Source rows | Observed runtime |
|---|---:|---:|---:|
| Band_Duets | overall | 1.59M | about 9,875s |
| Band_Quad | overall | 3.58M | about 5,732s |
| Band_Trios | overall | 3.12M expected | pending at measurement time |

Combo chunks are highly skewed. Many Quad/Trios combo chunks are tiny, but popular combinations still reach 200k-550k rows.

### Storage Shape

Estimated live table footprint during Phase 6A:

| Table | Estimated rows | Heap | Indexes | Total |
|---|---:|---:|---:|---:|
| `band_team_rank_history` | 242M | 120 GB | 171 GB | 290 GB |
| `band_team_rank_history_points` | 204M | 61 GB | 143 GB | 205 GB |
| `band_team_rank_history_latest` | 20M | 17 GB | 8.7 GB | 25 GB |
| `band_team_ranking_stats_history` | 7k | 1 MB | 3 MB | 4 MB |

The wide and narrow history indexes are larger than their heaps. Any design that keeps dual-writing wide and narrow history at this scale will keep creating heavy WAL, index maintenance, vacuum, and storage pressure.

### Query Shape

Tiny combo `EXPLAIN ANALYZE` was fast: about 29ms execution for a small Quad combo, using index scans on current rankings and latest state.

Popular combo planning remains large even before execution. The Quad `Solo_Guitar+Solo_Bass+Solo_Drums+Solo_Vocals` plan uses a hash left join over large current/latest key ranges, with estimates around 199k current rows and 93k latest rows.

The Quad `overall` plan uses a parallel hash left join with parallel seq scans over both the current ranking table and latest state for that scope. This is the expected whale-chunk behavior and explains why simply adding more chunk workers is not the right first fix.

Large `EXPLAIN ANALYZE` runs for popular and overall chunks should be collected during a maintenance window, not while live Trios recovery/autovacuum is active.

## Current Code Inventory

### Public API Read Path

- `RankingsEndpoints` exposes `GET /api/rankings/bands/{bandType}/{teamKey}/history`.
- The endpoint calls `IMetaDatabase.GetBandRankHistory(...)` and `GetBandRankHistoryStatus(...)`.
- `GetBandRankHistory(...)` already prefers `band_team_rank_history_points` and falls back to `band_team_rank_history`.
- `BandRankHistoryOptions.ApiReadSource` exists, but the read method currently behaves as narrow-with-wide-fallback directly.

### Current Write Path

- `RankingsCalculator` either enqueues a background band history job or calls `SnapshotBandRankHistoryChunked(...)` inline.
- `BandRankHistoryWorker` processes queued jobs when the service is not API-only and no scrape is running.
- `SnapshotBandRankHistoryChunked(...)` seeds latest state, enumerates `(ranking_scope, combo_id)` chunks, and calls `SnapshotBandRankHistoryChunk(...)` sequentially.
- `SnapshotBandRankHistoryChunk(...)` writes wide history, narrow points, latest state, and stats history in one transaction.

### Current Current-Ranking Publish Model

There are two related publish models:

- Song-level current band projections already use `projection_generation` plus `band_current_projection_scope.published_generation`.
- Aggregate band team rankings use build tables and an atomic table rename into `band_team_rankings_current_band_*`. They do not currently carry a durable generation id in the rows.

Band rank-history vNext should add generation metadata to aggregate band team rankings, then use that generation as the source event for history capture.

## Design Goals

1. Keep the public API fast even while scrape/history work is catching up.
2. Stop making wide history the normal write path.
3. Make history capture generation-aware and resumable.
4. Avoid repeated MD5 text hashing over millions of current rows.
5. Split whale chunks by indexed ranges, not by full-table hash rescans.
6. Use metadata tables for freshness/status instead of max-date scans on large point tables.
7. Keep rollback as a flag/read-source change, not a destructive migration.

## Proposed vNext Schema

### `band_team_ranking_generation`

Tracks aggregate band ranking rebuild generations. This makes the current table-swap publish model explicit.

Suggested columns:

| Column | Purpose |
|---|---|
| `generation_id BIGSERIAL PRIMARY KEY` | Durable aggregate ranking generation id. |
| `scrape_id BIGINT` | Source scrape, when available. |
| `band_type TEXT NOT NULL` | `Band_Duets`, `Band_Trios`, or `Band_Quad`. |
| `status TEXT NOT NULL` | `building`, `published`, `failed`, `superseded`. |
| `computed_at TIMESTAMPTZ NOT NULL` | Ranking compute timestamp. |
| `published_at TIMESTAMPTZ` | Time this generation became API-visible. |
| `ranking_table TEXT` | Build/current table name used for diagnostics. |
| `stats_table TEXT` | Stats table name used for diagnostics. |
| `row_count BIGINT NOT NULL DEFAULT 0` | Current rows for this band type. |
| `scope_count INT NOT NULL DEFAULT 0` | Number of stats scopes. |
| `created_at TIMESTAMPTZ NOT NULL DEFAULT now()` | Insert time. |

Indexes:

- `(band_type, status, published_at DESC)`
- `(scrape_id, band_type)`

### Current Ranking Row Additions

Add to aggregate current/build ranking table schema:

| Column | Purpose |
|---|---|
| `ranking_generation BIGINT NOT NULL DEFAULT 0` | Source aggregate generation. |
| `row_fingerprint TEXT NOT NULL` | Precomputed fingerprint for history delta checks. |

The build-table insert path should compute `row_fingerprint` once, while rows are already materialized. History jobs can then compare the stored value directly instead of re-hashing every field later.

### `band_team_rank_history_snapshot_v2`

Small metadata table. This should become the primary source for history freshness/status.

Suggested columns:

| Column | Purpose |
|---|---|
| `snapshot_id BIGSERIAL PRIMARY KEY` | Stable snapshot/scope id. |
| `generation_id BIGINT NOT NULL` | Source aggregate ranking generation. |
| `scrape_id BIGINT` | Source scrape id. |
| `snapshot_date DATE NOT NULL` | User-facing history date. |
| `band_type TEXT NOT NULL` | Band type. |
| `ranking_scope TEXT NOT NULL` | `overall` or `combo`. |
| `combo_id TEXT NOT NULL DEFAULT ''` | Combo key. |
| `total_teams INT NOT NULL` | Scope total. |
| `computed_at TIMESTAMPTZ NOT NULL` | Source ranking timestamp. |
| `source_row_count BIGINT NOT NULL DEFAULT 0` | Rows scanned from source. |
| `changed_row_count BIGINT NOT NULL DEFAULT 0` | Rows written to points. |
| `status TEXT NOT NULL DEFAULT 'queued'` | `queued`, `running`, `complete`, `failed`, `superseded`. |
| `created_at TIMESTAMPTZ NOT NULL DEFAULT now()` | Insert time. |
| `completed_at TIMESTAMPTZ` | Completion time. |
| `last_error TEXT` | Failure detail. |

Indexes and constraints:

- Unique `(generation_id, band_type, ranking_scope, combo_id)`
- Unique `(band_type, ranking_scope, combo_id, snapshot_date)` for daily coalescing, if same-day replacement remains required
- `(band_type, ranking_scope, combo_id, snapshot_date DESC)` for status/API freshness
- `(status, created_at)` for worker pickup

### `band_team_rank_history_points_v2`

Compact API-facing history points. No `team_members` array.

Suggested columns:

| Column | Purpose |
|---|---|
| `band_type TEXT NOT NULL` | Partition/read key. |
| `snapshot_date DATE NOT NULL` | Partition/read key. |
| `ranking_scope TEXT NOT NULL` | `overall` or `combo`. |
| `combo_id TEXT NOT NULL DEFAULT ''` | Combo key. |
| `team_key TEXT NOT NULL` | Team identity. |
| `snapshot_id BIGINT NOT NULL` | Metadata row. |
| `generation_id BIGINT NOT NULL` | Source generation. |
| `snapshot_taken_at TIMESTAMPTZ NOT NULL` | Source computed time. |
| `adjusted_skill_rank INT NOT NULL` | API metric. |
| `weighted_rank INT NOT NULL` | API metric. |
| `fc_rate_rank INT NOT NULL` | API metric. |
| `total_score_rank INT NOT NULL` | API metric. |
| `adjusted_skill_rating DOUBLE PRECISION` | API metric. |
| `weighted_rating DOUBLE PRECISION` | API metric. |
| `fc_rate DOUBLE PRECISION` | API metric. |
| `total_score BIGINT` | API metric. |
| `songs_played INT` | API metric. |
| `coverage DOUBLE PRECISION` | API metric. |
| `full_combo_count INT` | API metric. |
| `total_charted_songs INT` | API metric. |
| `total_ranked_teams INT` | API metric. |
| `raw_weighted_rating DOUBLE PRECISION` | API metric. |
| `raw_skill_rating DOUBLE PRECISION` | API metric. |

Partitioning:

- Parent partitioned by `LIST (band_type)`.
- Band-type children partitioned by `RANGE (snapshot_date)` monthly or quarterly.
- This supports cheap retention by dropping/detaching old date partitions and keeps per-partition indexes smaller.

Indexes:

- Primary key or unique index on `(band_type, snapshot_date, ranking_scope, combo_id, team_key)`.
- API lookup index on `(band_type, ranking_scope, combo_id, team_key, snapshot_date DESC)` including metric columns where PostgreSQL version/storage tradeoffs are acceptable.
- Snapshot parity/indexing on `(snapshot_id, team_key)`.

### `band_team_rank_history_latest_v2`

Compact latest state for delta detection.

Suggested columns:

| Column | Purpose |
|---|---|
| `band_type TEXT NOT NULL` | Partition/key. |
| `ranking_scope TEXT NOT NULL` | Key. |
| `combo_id TEXT NOT NULL DEFAULT ''` | Key. |
| `team_key TEXT NOT NULL` | Key. |
| `generation_id BIGINT NOT NULL` | Last seen generation. |
| `snapshot_id BIGINT NOT NULL` | Last written snapshot. |
| `snapshot_date DATE NOT NULL` | Last written date. |
| `fingerprint TEXT NOT NULL` | Last row fingerprint. |
| `updated_at TIMESTAMPTZ NOT NULL DEFAULT now()` | Latest update time. |

Partitioning:

- Parent partitioned by `LIST (band_type)`.

Indexes:

- Primary key `(band_type, ranking_scope, combo_id, team_key)`.
- `(band_type, snapshot_date DESC)` only if still needed for diagnostics.

This table intentionally does not store every wide ranking column. It only needs enough to determine whether a new current row changed.

### `band_team_rank_history_job_chunks_v2`

Keeps history work resumable and supports whale splitting.

Suggested columns:

| Column | Purpose |
|---|---|
| `chunk_id BIGSERIAL PRIMARY KEY` | Chunk identity. |
| `snapshot_id BIGINT NOT NULL` | Scope snapshot. |
| `band_type TEXT NOT NULL` | Routing/partition key. |
| `ranking_scope TEXT NOT NULL` | Scope. |
| `combo_id TEXT NOT NULL DEFAULT ''` | Combo. |
| `team_key_start TEXT` | Inclusive key-range start. |
| `team_key_end TEXT` | Exclusive key-range end. |
| `estimated_rows BIGINT NOT NULL DEFAULT 0` | Scheduler weight. |
| `status TEXT NOT NULL DEFAULT 'queued'` | `queued`, `running`, `complete`, `failed`. |
| `rows_scanned BIGINT NOT NULL DEFAULT 0` | Counter. |
| `rows_inserted BIGINT NOT NULL DEFAULT 0` | Counter. |
| `started_at TIMESTAMPTZ` | Timing. |
| `completed_at TIMESTAMPTZ` | Timing. |
| `updated_at TIMESTAMPTZ NOT NULL DEFAULT now()` | Scheduler freshness. |
| `last_error TEXT` | Failure detail. |

Indexes:

- `(status, updated_at)` for pickup.
- `(snapshot_id, status)` for progress.
- `(band_type, ranking_scope, combo_id, team_key_start)` for diagnostics.

## Write Path vNext

### Phase 6B: Shadow v2 Tables

Create v2 tables and shadow-write from the current `SnapshotBandRankHistoryChunk(...)` path:

- Keep API reads on existing points/wide tables.
- Write v2 points/latest/snapshot metadata in parallel for selected band types or scopes.
- Do not remove legacy writes yet.
- Add parity harness output for old narrow vs v2 points.

This phase proves the schema and read model without changing user-visible behavior.

Implemented Phase 6B scope:

- `BandRankHistory:WriteMode` supports `Legacy` and `Dual`; default is `Legacy`.
- Aggregate current ranking rows now carry `ranking_generation` and `row_fingerprint`.
- Rebuilds create and publish `band_team_ranking_generation` rows.
- Dual mode shadow-writes `band_team_rank_history_snapshot_v2`, `band_team_rank_history_points_v2`, and `band_team_rank_history_latest_v2` from the existing chunk path.
- Public API history reads remain on legacy narrow/wide sources.
- `BandRankHistoryHarness --write-mode Dual` can process explicit jobs with v2 shadow writes when separately approved.

### Phase 6C: Narrow/v2 Read Source Gate

Wire `BandRankHistoryOptions.ApiReadSource` into `GetBandRankHistory(...)` and add v2 options:

- `LegacyWide`
- `LegacyNarrowWithWideFallback`
- `V2NarrowWithLegacyFallback`
- `V2NarrowOnly`

Switch only after parity checks pass. Keep rollback as a config change.

Implemented Phase 6C scope:

- `BandRankHistory:ApiReadSource` is now wired into `GetBandRankHistory(...)` and `GetBandRankHistoryStatus(...)`.
- Existing values remain supported: `Wide`, `Narrow`, and `NarrowWithWideFallback`.
- Added `V2NarrowWithLegacyFallback` and `V2NarrowOnly`.
- Default appsettings/compose behavior remains `NarrowWithWideFallback`.
- V2 history reads use `band_team_rank_history_points_v2`; v2 freshness uses `band_team_rank_history_snapshot_v2`.
- Production v2 switching remains gated on a future live dual-mode shadow write plus parity run.

### Phase 6D: Weighted Scheduler and Whale Splitting

Replace simple chunk count concurrency with row-weight scheduling:

- Use stats `total_teams` as chunk weight.
- Split chunks over a threshold, for example 250k rows, by indexed `team_key` ranges.
- Use current ranking primary key order `(band_type, ranking_scope, combo_id, team_key)` to avoid repeated full scans.
- Allow small chunks to run concurrently while whale chunks are exclusive or heavily capped.

Suggested controls:

- `BandRankHistory:MaxActiveChunkRows`
- `BandRankHistory:WhaleChunkSize`
- `BandRankHistory:MaxParallelSmallChunks`
- `BandRankHistory:PauseOnApiLatencyMs`
- `BandRankHistory:PauseOnAutovacuumTablePatterns`

Implemented Phase 6D scope:

- Existing `band_rank_history_job_chunks` now supports `chunk_ordinal`, optional `team_key_start`/`team_key_end`, `estimated_rows`, and `source_generation`.
- New jobs range-split scopes by ordered `team_key` using `BandRankHistory:ChunkSize` when `BandRankHistory:RangeChunkingEnabled` is true.
- Existing jobs that already have old `(ranking_scope, combo_id)` chunks keep those chunks and process with `chunk_ordinal = 0`.
- Chunk execution applies optional team-key bounds and progress/retry updates identify chunks by `(job_id, ranking_scope, combo_id, chunk_ordinal)`.
- Pending chunks are ordered by estimated row weight so bounded chunks finish with more truthful progress.
- Parallel small-chunk scheduling remains conservative; `MaxParallelChunks` still defaults to `1` until live DB pressure measurements justify widening.

### Phase 6E: Generation-Publish Capture

Move history capture from post-hoc current-table sweep to generation-aware capture:

1. Create a `band_team_ranking_generation` row before aggregate rebuild.
2. Stamp build rows with `ranking_generation` and `row_fingerprint`.
3. Build current ranking/stats tables as today.
4. Publish via atomic table rename as today, but mark the generation published.
5. Enqueue v2 history snapshots for that generation.
6. Process history from the generation-stamped table/read source using compact latest v2.

This keeps current ranking publish and history catch-up decoupled, but gives history a durable source generation.

Later, selected small scopes can be captured inline during publish if measured safe. Whale scopes should remain queued so API visibility is not blocked by history writes.

Implemented Phase 6E scope:

- New band rank-history jobs record the current aggregate `source_generation` when available.
- Range-split chunks record the source generation observed on current ranking rows.
- V2 snapshot writes prefer chunk/job source-generation metadata before falling back to the source rows' stamped `ranking_generation`.
- Public API read defaults and legacy wide/narrow writes are unchanged.

### Phase 6F: Same-Pass Narrow Parity Proof

Before replacing wide history reads or disabling wide writes, prove parity on the same `snapshot_date` written by a scrape/history pass.

Implemented Phase 6F scope:

- `MetaDatabase.GetBandRankHistoryWideNarrowParity(...)` compares legacy wide history to legacy narrow points for a band type, snapshot date, optional ranking scope, and optional combo id.
- The parity report counts wide rows, narrow rows, key matches, rows missing from narrow, rows missing from wide, and API-visible value mismatches.
- Compared values include ranks, ratings, scores, songs played, coverage, full combos, total charted songs, raw ratings, snapshot timestamp, and total ranked teams via stats history.
- `BandRankHistoryHarness --parity` emits read-only console/JSON parity output and separately labels existing legacy-narrow-vs-v2 parity when v2 rows exist.
- This proof does not flip `ApiReadSource` and does not disable wide compatibility writes.

Live scrape 770 validation on `2026-05-10`:

- The initial broad whole-band query shape timed out on Duets; the parity helper now uses direct snapshot-date-indexed probes and skips mismatch sample scans when the summary counts are clean.
- The optimized harness pass ran with the database session forced read-only and wrote `harness-output/band-rank-history-770-parity-optimized-20260510.json`.
- Legacy wide and legacy narrow matched exactly: Duets `4,202,110 / 4,202,110`, Trios `7,382,330 / 7,382,330`, Quad `8,268,872 / 8,268,872`, with zero missing rows and zero value mismatches for all three band types.
- Live v2 parity was not proven in that run because the live v2 points table was absent.

## Read Path vNext

`GetBandRankHistoryStatus(...)` should read `band_team_rank_history_snapshot_v2` first. It should not need `max(snapshot_date)` over a massive points table.

`GetBandRankHistory(...)` should read `band_team_rank_history_points_v2` by:

```sql
SELECT ...
FROM band_team_rank_history_points_v2
WHERE band_type = @bandType
  AND ranking_scope = @scope
  AND combo_id = @comboId
  AND team_key = @teamKey
  AND snapshot_date >= @cutoff
ORDER BY snapshot_date DESC, snapshot_taken_at DESC;
```

The existing API response shape can remain unchanged.

## Migration Flags

Recommended flags:

| Option | Purpose |
|---|---|
| `BandRankHistory:WriteMode` | Implemented values: `Legacy`, `Dual`. `V2Only` remains a later-phase option after read-source and latest-state gates exist. |
| `BandRankHistory:ApiReadSource` | Implemented values: `Wide`, `Narrow`, `NarrowWithWideFallback`, `V2NarrowWithLegacyFallback`, `V2NarrowOnly`. |
| `BandRankHistory:UseV2LatestState` | Compare against v2 latest state. |
| `BandRankHistory:UseWideHistoryCompatibilityWrite` | Turn off after parity. |
| `BandRankHistory:ChunkSize` | Implemented target rows per key-range subchunk. |
| `BandRankHistory:RangeChunkingEnabled` | Implemented gate for key-range chunk splitting. |
| `BandRankHistory:MaxActiveChunkRows` | Weighted scheduler cap. |
| `BandRankHistory:WhaleChunkSize` | Target rows per key-range subchunk. |
| `BandRankHistory:GenerationCaptureMode` | `PostSwap`, `QueuedGeneration`, `InlineSmallScopes`. |

## Validation Gates

Before any API switch:

1. V2 row counts match legacy narrow points for selected scrape ids, band types, scopes, and dates.
2. V2 latest fingerprints match legacy latest for sampled teams.
3. Existing endpoint responses match v2 endpoint reads for sampled teams and date ranges.
4. Interrupted v2 chunks resume without duplicate points.
5. Weighted scheduler never runs multiple whale chunks over the configured row cap.
6. API `/readyz` and key endpoints remain responsive while v2 shadow writes run.
7. Rollback is proven by flipping read source back to legacy.

## Recommended Next Slice

Phase 6B should be the next implementation proposal, not a giant rewrite.

Recommended Phase 6B scope:

1. Add v2 metadata, points, latest, and chunk tables.
2. Add `row_fingerprint` and `ranking_generation` to aggregate build/current table creation.
3. Add shadow v2 writes for one narrow target, preferably Duets combo scopes first.
4. Add a parity harness/report comparing legacy narrow points to v2 points.
5. Keep API reads on legacy source until parity is proven.

Do not start by dropping wide history, switching API reads, or rewriting all historical data.