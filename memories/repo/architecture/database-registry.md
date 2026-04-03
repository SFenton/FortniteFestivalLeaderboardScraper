# FSTService Database Consistency Registry

> **Last updated:** 2026-04-03
> **Maintained by:** fst-principal-db

## Overview

FSTService uses a single **PostgreSQL** database (`fstservice`) accessed via **NpgsqlDataSource** (built-in connection pooling). All schema is managed by `DatabaseInitializer.EnsureSchemaAsync()` — a single DDL string with `IF NOT EXISTS` for idempotency.

**Connection string (dev):**
```
Host=localhost;Port=5432;Database=fstservice;Username=fst;Password=fst_dev;
Minimum Pool Size=5;Maximum Pool Size=50;Connection Idle Lifetime=300;Command Timeout=30
```

**Key files:**
- `FSTService/Persistence/DatabaseInitializer.cs` — DDL schema (all tables, indexes, partitions)
- `FSTService/Persistence/MetaDatabase.cs` — Central metadata database (IMetaDatabase)
- `FSTService/Persistence/InstrumentDatabase.cs` — Per-instrument leaderboard database (IInstrumentDatabase)
- `FSTService/Persistence/GlobalLeaderboardPersistence.cs` — Coordination layer + pipelined writers
- `FSTService/Persistence/FestivalPersistence.cs` — Songs table CRUD (IFestivalPersistence)
- `FSTService/Persistence/DataTransferObjects.cs` — All DTOs
- `FSTService/Scraping/PathDataStore.cs` — Path generation data (max scores, hashes)

---

## Tables

### Core Data Tables

#### `songs`
| Column | Type | Constraints | Purpose |
|---|---|---|---|
| song_id | TEXT | PRIMARY KEY | Unique song identifier |
| title | TEXT | | Song title |
| artist | TEXT | | Song artist |
| active_date | TEXT | | When the song became active |
| last_modified | TEXT | | Last modification timestamp |
| image_path | TEXT | | Album art URL path |
| lead_diff | INTEGER | | Guitar difficulty |
| bass_diff | INTEGER | | Bass difficulty |
| vocals_diff | INTEGER | | Vocals difficulty |
| drums_diff | INTEGER | | Drums difficulty |
| pro_lead_diff | INTEGER | | Pro guitar difficulty |
| pro_bass_diff | INTEGER | | Pro bass difficulty |
| release_year | INTEGER | | Song release year |
| tempo | INTEGER | | BPM |
| plastic_guitar_diff | INTEGER | | Plastic guitar difficulty |
| plastic_bass_diff | INTEGER | | Plastic bass difficulty |
| plastic_drums_diff | INTEGER | | Plastic drums difficulty |
| pro_vocals_diff | INTEGER | | Pro vocals difficulty |
| max_lead_score | INTEGER | | CHOpt max score for lead |
| max_bass_score | INTEGER | | CHOpt max score for bass |
| max_drums_score | INTEGER | | CHOpt max score for drums |
| max_vocals_score | INTEGER | | CHOpt max score for vocals |
| max_pro_lead_score | INTEGER | | CHOpt max score for pro lead |
| max_pro_bass_score | INTEGER | | CHOpt max score for pro bass |
| dat_file_hash | TEXT | | Hash of .dat file for path gen |
| song_last_modified | TEXT | | Song data last modified |
| paths_generated_at | TIMESTAMPTZ | | When paths were generated |
| chopt_version | TEXT | | CHOpt version used |

**Written by:** FestivalPersistence.SaveSongsAsync(), PathDataStore
**Read by:** FestivalPersistence.LoadSongsAsync(), API endpoints

#### `leaderboard_entries` (PARTITIONED BY LIST instrument)
| Column | Type | Constraints | Purpose |
|---|---|---|---|
| song_id | TEXT | NOT NULL | FK to songs |
| instrument | TEXT | NOT NULL | Instrument key |
| account_id | TEXT | NOT NULL | Epic account ID |
| score | INTEGER | NOT NULL | Player's score |
| accuracy | INTEGER | | Accuracy percentage |
| is_full_combo | BOOLEAN | | Full combo flag |
| stars | INTEGER | | Star rating |
| season | INTEGER | | Season number |
| percentile | REAL | | Percentile ranking |
| rank | INTEGER | DEFAULT 0 | Computed rank |
| source | TEXT | NOT NULL DEFAULT 'scrape' | Origin: 'scrape', 'backfill', 'neighbor' |
| difficulty | INTEGER | DEFAULT -1 | 0=Easy, 1=Med, 2=Hard, 3=Expert |
| api_rank | INTEGER | | Real rank from Epic API |
| end_time | TEXT | | Session end timestamp |
| first_seen_at | TIMESTAMPTZ | NOT NULL | When first scraped |
| last_updated_at | TIMESTAMPTZ | NOT NULL | Last update timestamp |

**Primary Key:** `(song_id, instrument, account_id)`
**Partitions:** 6 partitions by instrument value:
- `leaderboard_entries_solo_guitar` — Solo_Guitar
- `leaderboard_entries_solo_bass` — Solo_Bass
- `leaderboard_entries_solo_drums` — Solo_Drums
- `leaderboard_entries_solo_vocals` — Solo_Vocals
- `leaderboard_entries_pro_guitar` — Solo_PeripheralGuitar
- `leaderboard_entries_pro_bass` — Solo_PeripheralBass

**Written by:** InstrumentDatabase.UpsertEntries() (bulk + loop paths)
**Read by:** InstrumentDatabase.GetLeaderboard(), GetPlayerScores(), GetPlayerRankings(), etc.

#### `score_history`
| Column | Type | Constraints | Purpose |
|---|---|---|---|
| id | SERIAL | PRIMARY KEY | Auto-increment ID |
| song_id | TEXT | NOT NULL | Song identifier |
| instrument | TEXT | NOT NULL | Instrument key |
| account_id | TEXT | NOT NULL | Epic account ID |
| old_score | INTEGER | | Previous score (NULL for first) |
| new_score | INTEGER | | New score |
| old_rank | INTEGER | | Previous rank |
| new_rank | INTEGER | | New rank |
| accuracy | INTEGER | | Accuracy at time of score |
| is_full_combo | BOOLEAN | | FC flag at time of score |
| stars | INTEGER | | Stars at time of score |
| percentile | REAL | | Percentile at time of score |
| season | INTEGER | | Season number |
| score_achieved_at | TIMESTAMPTZ | | When score was achieved |
| season_rank | INTEGER | | Seasonal leaderboard rank |
| all_time_rank | INTEGER | | All-time leaderboard rank |
| difficulty | INTEGER | | Difficulty level |
| changed_at | TIMESTAMPTZ | NOT NULL | When change was detected |

**Written by:** MetaDatabase.InsertScoreChange(), InsertScoreChanges()
**Read by:** MetaDatabase.GetScoreHistory(), GetBestValidScores(), GetAllValidScoreTiers()

### Rankings Tables

#### `account_rankings` (PARTITIONED BY LIST instrument)
| Column | Type | Constraints | Purpose |
|---|---|---|---|
| account_id | TEXT | NOT NULL | Epic account ID |
| instrument | TEXT | NOT NULL | Instrument key |
| songs_played | INTEGER | NOT NULL | # songs with scores |
| total_charted_songs | INTEGER | NOT NULL | Total songs in catalog |
| coverage | REAL | NOT NULL | songs_played / total |
| raw_skill_rating | REAL | NOT NULL | Avg(rank/population) |
| adjusted_skill_rating | REAL | NOT NULL | Bayesian-adjusted skill |
| adjusted_skill_rank | INTEGER | NOT NULL | Rank by adjusted skill |
| weighted_rating | REAL | NOT NULL | Log-weight-adjusted rating |
| weighted_rank | INTEGER | NOT NULL | Rank by weighted rating |
| fc_rate | REAL | NOT NULL | Full combo rate |
| fc_rate_rank | INTEGER | NOT NULL | Rank by FC rate |
| total_score | INTEGER | NOT NULL | Sum of all scores |
| total_score_rank | INTEGER | NOT NULL | Rank by total score |
| max_score_percent | REAL | NOT NULL | Avg score/max_score |
| max_score_percent_rank | INTEGER | NOT NULL | Rank by max score % |
| avg_accuracy | REAL | NOT NULL | Average accuracy |
| full_combo_count | INTEGER | NOT NULL | # full combos |
| avg_stars | REAL | NOT NULL | Average star rating |
| best_rank | INTEGER | NOT NULL | Best rank on any song |
| avg_rank | REAL | NOT NULL | Average rank |
| computed_at | TIMESTAMPTZ | NOT NULL | When rankings were computed |

**Primary Key:** `(account_id, instrument)`
**Partitions:** Same 6 instruments as leaderboard_entries
**Written by:** InstrumentDatabase.ComputeAccountRankings() — 4-CTE query with Bayesian adjustment
**Read by:** InstrumentDatabase.GetAccountRankings(), GetAccountRanking(), GetAccountRankingNeighborhood()

#### `rank_history` (PARTITIONED BY LIST instrument)
Daily snapshots of rank positions for tracking rank movement over time.

**Primary Key:** `(account_id, instrument, snapshot_date)`
**Partitions:** Same 6 instruments
**Written by:** InstrumentDatabase.SnapshotRankHistory()
**Read by:** InstrumentDatabase.GetRankHistory()

#### `composite_rankings`
Cross-instrument composite rankings.

**Primary Key:** `account_id`
**Written by:** MetaDatabase.ReplaceCompositeRankings() — DELETE + INSERT pattern
**Read by:** MetaDatabase.GetCompositeRankings(), GetCompositeRankingNeighborhood()

#### `composite_rank_history`
Daily snapshots of composite rank positions.

**Primary Key:** `(account_id, snapshot_date)`
**Written by:** MetaDatabase.SnapshotCompositeRankHistory()

#### `combo_leaderboard`
Rankings for specific instrument combinations (e.g., guitar+bass).

**Primary Key:** `(combo_id, account_id)`
**Written by:** MetaDatabase.ReplaceComboLeaderboard()
**Read by:** MetaDatabase.GetComboLeaderboard(), GetComboRank()

#### `combo_stats`
| Column | Type | Constraints | Purpose |
|---|---|---|---|
| combo_id | TEXT | PRIMARY KEY | Combo identifier |
| total_accounts | INTEGER | NOT NULL | Total ranked accounts |
| computed_at | TIMESTAMPTZ | NOT NULL | Computation timestamp |

### Song Stats Tables

#### `song_stats` (PARTITIONED BY LIST instrument)
| Column | Type | Constraints | Purpose |
|---|---|---|---|
| song_id | TEXT | NOT NULL | Song identifier |
| instrument | TEXT | NOT NULL | Instrument key |
| entry_count | INTEGER | NOT NULL | Entries on leaderboard |
| previous_entry_count | INTEGER | NOT NULL DEFAULT 0 | Previous entry count |
| log_weight | REAL | NOT NULL | log2(entry_count) for weighted rankings |
| max_score | INTEGER | | CHOpt max score |
| computed_at | TIMESTAMPTZ | NOT NULL | Computation timestamp |

**Primary Key:** `(song_id, instrument)`
**Partitions:** Same 6 instruments
**Written by:** InstrumentDatabase.ComputeSongStats()

#### `valid_score_overrides` (PARTITIONED BY LIST instrument)
Stores replacement scores for entries exceeding the max-score threshold (cheated scores).

**Primary Key:** `(song_id, instrument, account_id)`
**Partitions:** Same 6 instruments
**Written by:** InstrumentDatabase.PopulateValidScoreOverrides()
**Read by:** ComputeAccountRankings() CTEs

### Player Data Tables

#### `player_stats`
Pre-computed player statistics per instrument.

**Primary Key:** `(account_id, instrument)`
**Written by:** MetaDatabase.UpsertPlayerStats()
**Read by:** MetaDatabase.GetPlayerStats()

#### `player_stats_tiers`
Leeway breakpoint tiers — JSONB-encoded array of stats at different leeway thresholds.

| Column | Type | Constraints | Purpose |
|---|---|---|---|
| account_id | TEXT | NOT NULL | Epic account ID |
| instrument | TEXT | NOT NULL | Instrument key |
| tiers_json | JSONB | NOT NULL DEFAULT '[]' | JSON array of PlayerStatsTier |
| updated_at | TIMESTAMPTZ | NOT NULL | Last update |

**Primary Key:** `(account_id, instrument)`
**Written by:** MetaDatabase.UpsertPlayerStatsTiers(), UpsertPlayerStatsTiersBatch()
**Read by:** MetaDatabase.GetPlayerStatsTiers()

### Account/Auth Tables

#### `account_names`
| Column | Type | Constraints | Purpose |
|---|---|---|---|
| account_id | TEXT | PRIMARY KEY | Epic account ID |
| display_name | TEXT | | Resolved display name |
| last_resolved | TIMESTAMPTZ | | When name was last resolved |

**Written by:** MetaDatabase.InsertAccountIds(), InsertAccountNames()
**Read by:** MetaDatabase.GetDisplayName(), SearchAccountNames(), GetDisplayNames()

#### `registered_users`
| Column | Type | Constraints | Purpose |
|---|---|---|---|
| device_id | TEXT | NOT NULL | Device identifier |
| account_id | TEXT | NOT NULL | Epic account ID |
| display_name | TEXT | | Display name |
| platform | TEXT | | Platform identifier |
| last_login_at | TIMESTAMPTZ | | Last login |
| registered_at | TIMESTAMPTZ | NOT NULL | Registration time |
| last_sync_at | TIMESTAMPTZ | | Last sync time |

**Primary Key:** `(device_id, account_id)`

#### `user_sessions`
| Column | Type | Constraints | Purpose |
|---|---|---|---|
| id | SERIAL | PRIMARY KEY | Auto-increment |
| username | TEXT | NOT NULL | Username |
| device_id | TEXT | NOT NULL | Device ID |
| refresh_token_hash | TEXT | NOT NULL UNIQUE | Hashed refresh token |
| platform | TEXT | | Platform |
| issued_at | TIMESTAMPTZ | NOT NULL | Token issue time |
| expires_at | TIMESTAMPTZ | NOT NULL | Token expiry |
| last_refreshed_at | TIMESTAMPTZ | | Last token refresh |
| revoked_at | TIMESTAMPTZ | | Token revocation time |

#### `epic_user_tokens`
Encrypted Epic OAuth tokens stored in BYTEA columns with per-row nonces.

**Primary Key:** `account_id`

### Rivals Tables

#### `rivals_status`
Computation status tracking for rivals feature.

**Primary Key:** `account_id`

#### `user_rivals`
| Column | Type | Constraints | Purpose |
|---|---|---|---|
| user_id | TEXT | NOT NULL | Requesting user |
| rival_account_id | TEXT | NOT NULL | Rival player |
| instrument_combo | TEXT | NOT NULL | Instrument combo key |
| direction | TEXT | NOT NULL | 'above' or 'below' |
| rival_score | REAL | NOT NULL | Rivalry strength score |
| avg_signed_delta | REAL | NOT NULL | Average rank delta |
| shared_song_count | INTEGER | NOT NULL | Shared songs |
| ahead_count | INTEGER | NOT NULL | Songs user is ahead |
| behind_count | INTEGER | NOT NULL | Songs user is behind |
| computed_at | TIMESTAMPTZ | NOT NULL | Computation time |

**Primary Key:** `(user_id, rival_account_id, instrument_combo)`
**Written by:** MetaDatabase.ReplaceRivalsData() — DELETE + INSERT in tx

#### `rival_song_samples`
Per-song comparison data between user and rival.

**Primary Key:** `(user_id, rival_account_id, instrument, song_id)`

#### `leaderboard_rivals`
Precomputed global-ranking-based rivals.

**Primary Key:** `(user_id, rival_account_id, instrument, rank_method)`
**Written by:** MetaDatabase.ReplaceLeaderboardRivalsData() — DELETE + INSERT in tx

#### `leaderboard_rival_song_samples`
Per-song data for leaderboard rivals.

**Primary Key:** `(user_id, rival_account_id, instrument, rank_method, song_id)`

### Backfill/Reconstruction Tables

#### `backfill_status`
Tracks per-account backfill job status.

**Primary Key:** `account_id`

#### `backfill_progress`
Per-song/instrument backfill progress tracking.

**Primary Key:** `(account_id, song_id, instrument)`

#### `history_recon_status`
History reconstruction job status.

**Primary Key:** `account_id`

#### `history_recon_progress`
Per-song/instrument history reconstruction progress.

**Primary Key:** `(account_id, song_id, instrument)`

### Reference/Config Tables

#### `season_windows`
Discovered Epic season event/window IDs.

**Primary Key:** `season_number`

#### `song_first_seen_season`
First seen season for each song (for history reconstruction).

**Primary Key:** `song_id`

#### `leaderboard_population`
Real (Epic API) leaderboard population counts.

**Primary Key:** `(song_id, instrument)`

#### `item_shop_tracks`
Currently in-shop song IDs.

**Primary Key:** `song_id`

#### `data_version`
Key-value version tracking for data format changes.

**Primary Key:** `key`

#### `scrape_log`
Scrape run metadata.

| Column | Type | Constraints | Purpose |
|---|---|---|---|
| id | SERIAL | PRIMARY KEY | Run ID |
| started_at | TIMESTAMPTZ | NOT NULL | Start time |
| completed_at | TIMESTAMPTZ | | End time |
| songs_scraped | INTEGER | | Songs processed |
| total_entries | INTEGER | | Entries found |
| total_requests | INTEGER | | HTTP requests made |
| total_bytes | BIGINT | | Bytes downloaded |

---

## Indexes

### leaderboard_entries indexes
| Index | Columns | Purpose |
|---|---|---|
| `ix_le_song_score` | `(song_id, instrument, score DESC)` | Leaderboard display, rank computation |
| `ix_le_account` | `(account_id, instrument)` | Player profile queries |
| `ix_le_account_song` | `(account_id, song_id, instrument)` | Single entry lookup |
| `ix_le_song_source` | `(song_id, instrument, source)` | Source-filtered queries |
| `ix_le_song_rank` | `(song_id, instrument, rank)` | Rank-based lookups |

### account_rankings indexes (all UNIQUE)
| Index | Columns | Purpose |
|---|---|---|
| `ix_ar_skill` | `(instrument, adjusted_skill_rank)` | Skill ranking pages |
| `ix_ar_weighted` | `(instrument, weighted_rank)` | Weighted ranking pages |
| `ix_ar_fc_rate` | `(instrument, fc_rate_rank)` | FC rate ranking pages |
| `ix_ar_total_score` | `(instrument, total_score_rank)` | Total score ranking pages |
| `ix_ar_max_score_pct` | `(instrument, max_score_percent_rank)` | Max score % ranking pages |

### score_history indexes
| Index | Columns | Purpose |
|---|---|---|
| `ix_sh_account` | `(account_id)` | Player history queries |
| `ix_sh_song` | `(song_id, instrument)` | Song-specific history |
| `ix_sh_dedup` | `(account_id, song_id, instrument, new_score, score_achieved_at)` UNIQUE | ON CONFLICT dedup |

### account_names indexes
| Index | Columns | Purpose |
|---|---|---|
| `ix_an_unresolved` | `(last_resolved) WHERE last_resolved IS NULL` | Name resolution queue |
| `ix_an_name` | `(display_name) WHERE display_name IS NOT NULL` | Name search |

### Other indexes
| Index | Table | Columns | Purpose |
|---|---|---|---|
| `ix_scrapelog_completed` | scrape_log | `(id DESC) WHERE completed_at IS NOT NULL` | Last completed run |
| `ix_reg_account` | registered_users | `(account_id)` | Account lookup |
| `ix_sessions_username` | user_sessions | `(username)` | Session by user |
| `ix_sessions_token` | user_sessions | `(refresh_token_hash) WHERE revoked_at IS NULL` | Active session lookup |
| `ix_backfill_status` | backfill_status | `(status)` | Pending backfill queue |
| `ix_bfp_account` | backfill_progress | `(account_id)` | Per-account progress |
| `ix_hr_status` | history_recon_status | `(status)` | Pending recon queue |
| `ix_hrp_account` | history_recon_progress | `(account_id)` | Per-account progress |
| `ix_ur_combo` | user_rivals | `(user_id, instrument_combo, direction, rival_score DESC)` | Rival listing |
| `ix_rs_rival` | rival_song_samples | `(user_id, rival_account_id, instrument)` | Rival song lookup |
| `ix_lbr_user_inst` | leaderboard_rivals | `(user_id, instrument, rank_method, direction)` | LB rival listing |
| `ix_lbrss_user_rival` | leaderboard_rival_song_samples | `(user_id, rival_account_id, instrument, rank_method)` | LB rival songs |
| `ix_cr_rank` | composite_rankings | `(composite_rank)` | Composite rank pages |
| `ix_pst_account` | player_stats_tiers | `(account_id)` | Account tier lookup |
| `ix_combo_adjusted` | combo_leaderboard | `(combo_id, adjusted_rating ASC)` | Combo ranking by adjusted |
| `ix_combo_weighted` | combo_leaderboard | `(combo_id, weighted_rating ASC)` | Combo ranking by weighted |
| `ix_combo_fc_rate` | combo_leaderboard | `(combo_id, fc_rate DESC)` | Combo ranking by FC |
| `ix_combo_total_score` | combo_leaderboard | `(combo_id, total_score DESC)` | Combo ranking by score |
| `ix_combo_max_score` | combo_leaderboard | `(combo_id, max_score_percent DESC)` | Combo ranking by max % |

---

## Query Patterns

### Canonical Connection Pattern
```csharp
using var conn = _ds.OpenConnection();      // Synchronous (scrape pipeline)
await using var conn = await _ds.OpenConnectionAsync(ct);  // Async (startup, API)
```
Connections come from NpgsqlDataSource's built-in pool. No manual pool management.

### Simple Read (Single Row)
```csharp
using var conn = _ds.OpenConnection();
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT ... FROM table WHERE id = @id";
cmd.Parameters.AddWithValue("id", value);
var result = cmd.ExecuteScalar();
```

### Simple Read (Multiple Rows)
```csharp
using var conn = _ds.OpenConnection();
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT ... FROM table WHERE ...";
cmd.Parameters.AddWithValue("param", value);
var list = new List<T>();
using var r = cmd.ExecuteReader();
while (r.Read()) list.Add(MapRow(r));
```

### Parameterized Insert (AddWithValue)
Used for single inserts and simple operations:
```csharp
cmd.Parameters.AddWithValue("songId", songId);
cmd.Parameters.AddWithValue("nullableValue", (object?)nullable ?? DBNull.Value);
```

### Prepared Statement Loop (≤ threshold)
Used for batches up to BulkThreshold (50 for leaderboard_entries, 20 for score_history):
```csharp
using var cmd = conn.CreateCommand();
cmd.Transaction = tx;
cmd.CommandText = "INSERT INTO ... VALUES (@p1, @p2, ...) ON CONFLICT ...";
var p1 = cmd.Parameters.Add("p1", NpgsqlDbType.Text);
var p2 = cmd.Parameters.Add("p2", NpgsqlDbType.Integer);
cmd.Prepare();
foreach (var item in items) {
    p1.Value = item.Field1;
    p2.Value = item.Field2;
    cmd.ExecuteNonQuery();
}
```

### Batch Parameterized IN Queries
For variable-length IN clauses, parameter names are dynamically generated:
```csharp
var pNames = new string[idList.Count];
for (int i = 0; i < batch.Length; i++) {
    pNames[i] = $"@id{i}";
    cmd.Parameters.AddWithValue($"id{i}", batch[i]);
}
cmd.CommandText = $"SELECT ... WHERE id IN ({string.Join(',', pNames)})";
```
Batched in chunks of 500 for large sets (GetDisplayNames).

### Rank Column Whitelist
SQL injection prevention for dynamic ORDER BY columns:
```csharp
internal static string MapRankColumn(string rankBy) => rankBy.ToLowerInvariant() switch
{
    "totalscore" => "TotalScoreRank",
    "adjusted" => "AdjustedSkillRank",
    // ... whitelist only
    _ => "TotalScoreRank",
};
```

---

## Bulk Operations

### COPY Binary Import → Temp Table → INSERT ON CONFLICT (Primary Pattern)
Used when batch size > threshold. ~10-50x faster than loop path.

```
1. CREATE TEMP TABLE _staging (...) ON COMMIT DROP
2. COPY _staging FROM STDIN (FORMAT BINARY)  -- NpgsqlBinaryImport
3. INSERT INTO real_table SELECT ... FROM _staging ON CONFLICT ... DO UPDATE SET ...
4. tx.Commit()  -- temp table auto-dropped
```

**Used by:**
- `InstrumentDatabase.UpsertEntriesBulk()` — leaderboard_entries (threshold: 50)
- `MetaDatabase.InsertScoreChanges()` — score_history (threshold: 20)
- `MetaDatabase.InsertAccountIds()` — account_names (threshold: 50)

### Temp Table Join Pattern
Used for multi-key lookups (avoiding huge IN clauses):
```
1. CREATE TEMP TABLE _thresholds (...) ON COMMIT DROP
2. INSERT INTO _thresholds via prepared loop
3. SELECT ... JOIN _thresholds ON ... WHERE ...
4. tx.Commit()
```

**Used by:**
- `MetaDatabase.GetBestValidScores()` — _valid_thresholds
- `MetaDatabase.GetBulkBestValidScores()` — _bulk_thresholds
- `MetaDatabase.GetAllValidScoreTiers()` — _tier_thresholds
- `InstrumentDatabase.GetPlayerRankingsFiltered()` — _max_thresholds
- `InstrumentDatabase.GetFilteredEntryCounts()` — _max_thresholds2

### DELETE + INSERT Replace Pattern
For full table replacement (rankings):
```csharp
using var tx = conn.BeginTransaction();
DELETE FROM table [WHERE scope_key = @key];
INSERT INTO table ... (prepared loop);
tx.Commit();
```

**Used by:**
- `MetaDatabase.ReplaceRivalsData()` — user_rivals + rival_song_samples
- `MetaDatabase.ReplaceLeaderboardRivalsData()` — leaderboard_rivals + samples
- `MetaDatabase.ReplaceCompositeRankings()` — composite_rankings
- `MetaDatabase.ReplaceComboLeaderboard()` — combo_leaderboard
- `MetaDatabase.SaveItemShopTracks()` — item_shop_tracks

### TRUNCATE Partition Pattern
Used for partition-level replacement (faster than DELETE):
```csharp
cmd.CommandText = $"TRUNCATE {GetPartitionName("account_rankings")}";
// Then INSERT fresh data
```

**Used by:**
- `InstrumentDatabase.ComputeAccountRankings()` — account_rankings partition
- `InstrumentDatabase.PopulateValidScoreOverrides()` — valid_score_overrides partition

---

## Connection Management

### NpgsqlDataSource (Singleton)
```csharp
var pgDataSource = NpgsqlDataSource.Create(pgConnStr);
builder.Services.AddSingleton(pgDataSource);
```
- Pool Size: 5 min, 50 max
- Connection Idle Lifetime: 300s
- Command Timeout: 30s (overridden to 120s or 300s for expensive operations)

### DI Registration
- `NpgsqlDataSource` — Singleton, injected everywhere
- `IMetaDatabase` → `MetaDatabase` — Singleton
- `IInstrumentDatabase` → `InstrumentDatabase` — Created by `GlobalLeaderboardPersistence` per instrument (6 instances)
- `IFestivalPersistence` → `FestivalPersistence` — Singleton (via Core library)
- `GlobalLeaderboardPersistence` — Singleton, owns all instrument DBs

### Connection Lifetime
- Connections acquired per-operation: `using var conn = _ds.OpenConnection()`
- Returned to pool on dispose
- No connection caching or manual pooling
- MVCC handles concurrent reads/writes natively (no locking)

---

## Transaction Patterns

### Standard Write Transaction
```csharp
using var conn = _ds.OpenConnection();
using var tx = conn.BeginTransaction();
// ... operations with cmd.Transaction = tx ...
tx.Commit();
```

### WAL Optimization for Scrape Data
Scrape data is re-scrapeable, so WAL flush is disabled for throughput:
```csharp
using (var sc = conn.CreateCommand()) {
    sc.Transaction = tx;
    sc.CommandText = "SET LOCAL synchronous_commit = off";
    sc.ExecuteNonQuery();
}
```
Used in: UpsertEntriesBulk, UpsertEntriesLoop, RunBatchedWriterAsync

### Command Timeout Overrides
- Default: 30s (from connection string)
- Bulk merge: 120s (`c.CommandTimeout = 120`)
- ComputeAccountRankings: 300s (`cmd.CommandTimeout = 300`)

### CASCADE Delete on Unregister
When a user is fully unregistered (no remaining device registrations):
```csharp
foreach (var t in new[] { "player_stats", "player_stats_tiers", "backfill_status", ... })
{ cmd.CommandText = $"DELETE FROM {t} WHERE account_id = @id"; ... }
```
> Note: Table names are hardcoded string literals, not user input — safe from injection.

---

## Data Flow

### Scrape Pipeline → Persistence
```
ScrapeOrchestrator
  → SongProcessingMachine (parallel, DOP=32-512)
    → Epic API HTTP calls
    → GlobalLeaderboardResult per (song, instrument)
    → Channel<PersistWorkItem> per instrument (bounded, capacity 128)
      → RunBatchedWriterAsync (batch size: 10 items per PG transaction)
        → InstrumentDatabase.UpsertEntries(conn, tx)  // shared connection
        → Score change detection (pre/post UPSERT comparison)
        → MetaDatabase.InsertScoreChanges()
        → Account ID accumulation (deferred bulk flush)
  → Drain all channels
  → Bulk flush deferred account IDs
```

### Post-Scrape Processing
```
PostScrapeOrchestrator (9 phases):
  1. Song stats computation → song_stats
  2. Rank recomputation → leaderboard_entries.rank
  3. Account rankings → account_rankings (TRUNCATE + recompute)
  4. Rank history snapshot → rank_history
  5. Composite rankings → composite_rankings
  6. Leaderboard rivals → leaderboard_rivals
  7. Player stats → player_stats, player_stats_tiers
  8. Name resolution → account_names
  9. Pruning → deletes excess entries
```

### API → Persistence
```
ApiEndpoints → IInstrumentDatabase/IMetaDatabase
  → NpgsqlDataSource.OpenConnection()
  → Parameterized queries
  → DTO mapping
  → JSON response
```

---

## Migration Strategy

### Idempotent DDL
All schema is defined in `DatabaseInitializer.Schema` as a single const string:
```sql
CREATE TABLE IF NOT EXISTS table_name (...);
CREATE INDEX IF NOT EXISTS ix_name ON table (...);
```

### Startup Schema Application
```csharp
await DatabaseInitializer.EnsureSchemaAsync(pgDs);
```
Called once at app startup. Safe to re-run — all DDL is idempotent.

### Serial Sequence Reset
After migration (COPY with explicit IDs), sequences are reset:
```sql
SELECT setval(pg_get_serial_sequence('scrape_log', 'id'),
    COALESCE((SELECT MAX(id) FROM scrape_log), 0) + 1, false);
```
Applied to: scrape_log, score_history, user_sessions

### Schema Evolution Approach
- New columns: Add to the Schema const + migration runs on next startup
- New tables: Add CREATE TABLE IF NOT EXISTS + partitions if needed
- No formal migration versioning system — relies on IF NOT EXISTS idempotency
- Rollback: Manual SQL intervention (no automated rollback)

---

## Partitioning Strategy

Three tables use LIST partitioning by instrument:
- `leaderboard_entries` — 6 partitions (largest table)
- `song_stats` — 6 partitions
- `account_rankings` — 6 partitions
- `rank_history` — 6 partitions
- `valid_score_overrides` — 6 partitions

Partition values:
| Partition Suffix | Instrument Value |
|---|---|
| `_solo_guitar` | Solo_Guitar |
| `_solo_bass` | Solo_Bass |
| `_solo_drums` | Solo_Drums |
| `_solo_vocals` | Solo_Vocals |
| `_pro_guitar` | Solo_PeripheralGuitar |
| `_pro_bass` | Solo_PeripheralBass |

Benefits:
- TRUNCATE per-partition is instant (ComputeAccountRankings, PopulateValidScoreOverrides)
- Indexes are per-partition → smaller B-trees
- Partition pruning on WHERE instrument = @instrument

---

## Known Issues & Inconsistencies

### SQL Injection Risk (Low Severity — int type)
`InstrumentDatabase.GetLeaderboardWithCount()` and `GetRankForScore()` interpolate `maxScore.Value` directly:
```csharp
var scoreFilter = maxScore.HasValue ? $"AND score <= {maxScore.Value}" : "";
```
> **Risk:** Low — `maxScore` is `int?`, not string. But violates the parameterization convention.
> **Fix:** Use `cmd.Parameters.AddWithValue("maxScore", maxScore.Value)` and `@maxScore` in SQL.

### Mixed Parameter Patterns
Some code uses `AddWithValue()` for simple cases and `Parameters.Add(name, NpgsqlDbType)` for prepared loops. Both are correct but the mixing within the same class can be confusing.

### Temp Table Cleanup Inconsistency
- `ON COMMIT DROP` used for single-transaction temp tables (correct)
- `DROP TABLE IF EXISTS _le_staging` + manual `CREATE` used for batched writer (where tx spans multiple songs — correct, but different pattern)
- Both approaches are valid for their use cases

### Cascade Delete Uses Table Name Interpolation
```csharp
foreach (var t in new[] { "player_stats", "player_stats_tiers", ... })
    c.CommandText = $"DELETE FROM {t} WHERE account_id = @id";
```
> Safe: table names are string literals in a hardcoded array, never user input.

### ComputeAccountRankings 4-CTE Complexity
The ranking computation query is a massive 4-CTE query (ValidEntries → Aggregated → WithBayesian → Ranked). Command timeout is set to 300s. Should consider EXPLAIN ANALYZE if performance degrades.

### No Data Retention Policy for score_history
score_history grows indefinitely. Consider adding a retention/archival policy.

---

## Naming Conventions

### Index Naming
Pattern: `ix_{table_alias}_{columns}`
| Alias | Table |
|---|---|
| `le` | leaderboard_entries |
| `ar` | account_rankings |
| `sh` | score_history |
| `an` | account_names |
| `ur` | user_rivals |
| `rs` | rival_song_samples |
| `lbr` | leaderboard_rivals |
| `lbrss` | leaderboard_rival_song_samples |
| `cr` | composite_rankings |
| `pst` | player_stats_tiers |

### Partition Naming
Pattern: `{parent_table}_{instrument_short}`
Example: `leaderboard_entries_solo_guitar`, `account_rankings_pro_bass`

### Temp Table Naming
Pattern: `_{purpose}_staging` or `_{purpose}_thresholds`
Examples: `_le_staging`, `_sh_staging`, `_acct_staging`, `_valid_thresholds`, `_max_thresholds`
