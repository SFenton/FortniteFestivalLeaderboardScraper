# FSTService Persistence Layer Deep Dive

> Generated 2026-04-03 — Comprehensive reference for the fst-persistence agent.

## Repository Catalog

### DatabaseInitializer (static class)
**File**: `FSTService/Persistence/DatabaseInitializer.cs`
**Responsibility**: Creates the entire PostgreSQL schema idempotently. All DDL uses `IF NOT EXISTS`. Resets SERIAL sequences after COPY migration inserts.

| Method | Return | Description |
|---|---|---|
| `EnsureSchemaAsync(NpgsqlDataSource, CancellationToken)` | Task | Executes full DDL + resets SERIAL sequences for scrape_log, score_history, user_sessions |

### MetaDatabase (implements IMetaDatabase)
**File**: `FSTService/Persistence/MetaDatabase.cs` (~721 lines)
**Responsibility**: Central metadata — scrape logs, score history, account names, registered users, backfill/history-recon tracking, season windows, player stats, first-seen season, leaderboard population, rivals, leaderboard rivals, item shop, composite rankings, combo leaderboards.
**Constructor deps**: `NpgsqlDataSource`, `ILogger<MetaDatabase>`
**Connection pattern**: Every method opens its own connection from the pooled `NpgsqlDataSource` via `_ds.OpenConnection()`.

#### Methods by Domain

**Scrape Log**
| Method | Query Pattern | Return |
|---|---|---|
| `StartScrapeRun()` | INSERT RETURNING id | `long` |
| `CompleteScrapeRun(scrapeId, ...)` | UPDATE by id | void |
| `GetLastCompletedScrapeRun()` | SELECT … ORDER BY id DESC LIMIT 1 | `ScrapeRunInfo?` |

**Score History**
| Method | Query Pattern | Return |
|---|---|---|
| `InsertScoreChange(...)` | INSERT … ON CONFLICT DO UPDATE (dedup on account/song/instrument/score/achieved_at) | void |
| `BackfillScoreHistoryDifficulty(...)` | UPDATE WHERE difficulty IS NULL | void |
| `InsertScoreChanges(changes)` | >20: COPY binary → staging → INSERT SELECT ON CONFLICT; ≤20: prepared-statement loop | `int` |
| `GetScoreHistory(accountId, limit, songId?, instrument?)` | SELECT with dynamic WHERE, ORDER BY id DESC LIMIT | `List<ScoreHistoryEntry>` |
| `GetBestValidScores(accountId, thresholds)` | Temp table _valid_thresholds → JOIN score_history → MAX subquery | `Dictionary<(SongId,Instrument), ValidScoreFallback>` |
| `GetBulkBestValidScores(instrument, entries)` | Temp table _bulk_thresholds → JOIN score_history → MAX subquery | `Dictionary<(AccountId,SongId), ValidScoreFallback>` |
| `GetAllValidScoreTiers(accountId, maxThresholds)` | Temp table _tier_thresholds → JOIN + GROUP BY + ORDER BY score DESC | `Dictionary<(SongId,Instrument), List<ValidScoreFallback>>` |

**Account Names**
| Method | Query Pattern | Return |
|---|---|---|
| `InsertAccountIds(accountIds)` | >50: COPY binary → staging → INSERT ON CONFLICT DO NOTHING; ≤50: prepared loop | `int` |
| `GetUnresolvedAccountIds()` | SELECT WHERE last_resolved IS NULL | `List<string>` |
| `GetUnresolvedAccountCount()` | SELECT COUNT(*) WHERE last_resolved IS NULL | `int` |
| `InsertAccountNames(accounts)` | Prepared loop INSERT ON CONFLICT DO UPDATE | `int` |
| `GetDisplayName(accountId)` | SELECT by PK | `string?` |
| `SearchAccountNames(query, limit)` | ILIKE search, priority ordering (prefix first, shortest first) | `List<(AccountId, DisplayName)>` |
| `GetDisplayNames(accountIds)` | Batched (chunk 500) SELECT … IN (...) | `Dictionary<string, string>` |

**Registered Users**
| Method | Query Pattern | Return |
|---|---|---|
| `GetRegisteredAccountIds()` | SELECT DISTINCT account_id | `HashSet<string>` |
| `RegisterUser(deviceId, accountId)` | INSERT ON CONFLICT DO NOTHING | `bool` |
| `UnregisterUser(deviceId, accountId)` | DELETE + cascade-clean (player_stats, tiers, backfill, recon, rivals) if last reference | `bool` |
| `GetAccountIdForUsername(username)` | SELECT WHERE LOWER(display_name) = LOWER(@username) LIMIT 1 | `string?` |

**Backfill Tracking**
| Method | Query Pattern | Return |
|---|---|---|
| `EnqueueBackfill(accountId, totalSongsToCheck)` | INSERT ON CONFLICT (skip if complete) | void |
| `GetPendingBackfills()` | SELECT WHERE status IN ('pending', 'in_progress') | `List<BackfillStatusInfo>` |
| `GetBackfillStatus(accountId)` | SELECT by PK | `BackfillStatusInfo?` |
| `StartBackfill(accountId)` | UPDATE status + timestamps | void |
| `CompleteBackfill(accountId)` | UPDATE status = 'complete' | void |
| `FailBackfill(accountId, err)` | UPDATE status = 'error' | void |
| `UpdateBackfillProgress(accountId, checked, found)` | UPDATE counters | void |
| `MarkBackfillSongChecked(accountId, songId, instrument, found)` | INSERT ON CONFLICT DO UPDATE | void |
| `GetCheckedBackfillPairs(accountId)` | SELECT song_id, instrument WHERE checked=1 | `HashSet<(SongId, Instrument)>` |

**History Reconstruction** — same pattern as Backfill (EnqueueHistoryRecon, Start, Complete, Fail, UpdateProgress, MarkProcessed, GetProcessedPairs)

**Season Windows**
| Method | Return |
|---|---|
| `UpsertSeasonWindow(seasonNumber, eventId, windowId)` | void |
| `GetSeasonWindows()` | `List<SeasonWindowInfo>` |
| `GetCurrentSeason()` | `int` (MAX season_number) |

**Player Stats**
| Method | Return |
|---|---|
| `UpsertPlayerStats(stats)` | void (INSERT ON CONFLICT DO UPDATE) |
| `GetPlayerStats(accountId)` | `List<PlayerStatsDto>` |
| `UpsertPlayerStatsTiers(accountId, instrument, tiersJson)` | void (JSONB upsert) |
| `UpsertPlayerStatsTiersBatch(rows)` | void (prepared-statement loop in transaction) |
| `GetPlayerStatsTiers(accountId)` | `List<PlayerStatsTiersRow>` |

**First Seen Season**
| Method | Return |
|---|---|
| `GetSongsWithFirstSeenSeason()` | `HashSet<string>` |
| `UpsertFirstSeenSeason(songId, ...)` | void |
| `GetAllFirstSeenSeasons()` | `Dictionary<string, (int?, int)>` |

**Leaderboard Population**
| Method | Return |
|---|---|
| `RaiseLeaderboardPopulationFloor(songId, instrument, floor)` | void (GREATEST to only increase) |
| `UpsertLeaderboardPopulation(items)` | void (prepared-statement loop) |
| `GetLeaderboardPopulation(songId, instrument)` | `long` |
| `GetAllLeaderboardPopulation()` | `Dictionary<(SongId, Instrument), long>` |

**Rivals (per-user song-based)**
| Method | Return |
|---|---|
| `EnsureRivalsStatus(accountId)` | void |
| `StartRivals / CompleteRivals / FailRivals` | void |
| `GetRivalsStatus(accountId)` | `RivalsStatusInfo?` |
| `GetPendingRivalsAccounts()` | `List<string>` |
| `ReplaceRivalsData(userId, rivals, samples)` | void (DELETE all + re-INSERT in transaction) |
| `GetUserRivals(userId, combo?, direction?)` | `List<UserRivalRow>` |
| `GetRivalCombos(userId)` | `List<RivalComboSummary>` |
| `GetRivalSongSamples(userId, rivalId, instrument?)` | `List<RivalSongSampleRow>` |
| `GetAllRivalSongSamplesForUser(userId)` | `Dictionary<string, List<RivalSongSampleRow>>` |

**Leaderboard Rivals (rank-based)**
| Method | Return |
|---|---|
| `ReplaceLeaderboardRivalsData(userId, instrument, rivals, samples)` | void (DELETE by user+instrument + re-INSERT) |
| `GetLeaderboardRivals(userId, instrument?, rankMethod?, direction?)` | `List<LeaderboardRivalRow>` |
| `GetLeaderboardRivalSongSamples(...)` | `List<LeaderboardRivalSongSampleRow>` |

**Item Shop**
| Method | Return |
|---|---|
| `SaveItemShopTracks(songIds, leavingTomorrow, scrapedAt)` | void (TRUNCATE + INSERT) |
| `LoadItemShopTracks()` | `(HashSet<string>, HashSet<string>)` |

**Composite Rankings** — `ReplaceCompositeRankings`, `GetCompositeRankings`, `GetCompositeRanking`, `GetCompositeRankingNeighborhood`, `SnapshotCompositeRankHistory`

**Combo Leaderboard** — `ReplaceComboLeaderboard`, `GetComboLeaderboard`, `GetComboRank`, `GetComboTotalAccounts`

**Maintenance**
| Method | Return |
|---|---|
| `Checkpoint()` | void (WAL checkpoint) |

---

### InstrumentDatabase (implements IInstrumentDatabase)
**File**: `FSTService/Persistence/InstrumentDatabase.cs` (~673 lines)
**Responsibility**: Per-instrument leaderboard data — entries, rankings, pruning, song stats, account rankings, rank history. All queries filter by `WHERE instrument = @instrument` since all instruments share the same partitioned table.
**Constructor deps**: `string instrument`, `NpgsqlDataSource`, `ILogger<InstrumentDatabase>`
**Key constants**: `BulkThreshold = 50` (above this, uses COPY binary; below, uses prepared-statement loop)

#### Methods by Domain

**Leaderboard Entry Writes**
| Method | Return |
|---|---|
| `UpsertEntries(songId, entries)` | `int` — dispatches to Bulk or Loop based on BulkThreshold |
| `UpsertEntries(songId, entries, conn, tx)` | `int` — external connection variant for batched transactions |

**Leaderboard Entry Reads**
| Method | Return |
|---|---|
| `GetEntry(songId, accountId)` | `LeaderboardEntry?` |
| `GetEntriesForAccounts(songId, accountIds)` | `Dictionary<string, LeaderboardEntry>` |
| `GetMinSeason(songId)` | `int?` |
| `GetMaxSeason()` | `int?` |
| `GetTotalEntryCount()` | `long` |
| `GetAnySongId()` | `string?` |

**Leaderboard API Reads**
| Method | Return |
|---|---|
| `GetLeaderboard(songId, top?, offset)` | `List<LeaderboardEntryDto>` (ROW_NUMBER rank) |
| `GetLeaderboardCount(songId)` | `int` |
| `GetAllSongCounts()` | `Dictionary<string, int>` (GROUP BY song_id) |
| `GetLeaderboardWithCount(songId, top?, offset, maxScore?)` | `(List<LeaderboardEntryDto>, int TotalCount)` (COUNT(*) OVER) |
| `GetNeighborhood(songId, centerRank, radius, excludeId)` | `List<(AccountId, Rank, Score)>` |

**Player Queries**
| Method | Return |
|---|---|
| `GetSongIdsForAccount(accountId)` | `HashSet<string>` |
| `GetPlayerScoresForSongs(accountId, songIds)` | `List<PlayerScoreDto>` |
| `GetPlayerScores(accountId, songId?)` | `List<PlayerScoreDto>` |
| `GetPlayerRankings(accountId, songId?)` | `Dictionary<string, int>` (CTE with ROW_NUMBER window) |
| `GetPlayerRankingsFiltered(accountId, maxScores, songId?)` | `Dictionary<string, int>` (temp table + CTE with score filter) |
| `GetRankForScore(songId, score, maxScore?)` | `int` (COUNT+1 above) |
| `GetFilteredEntryCounts(maxScores)` | `Dictionary<string, int>` (temp table + LEFT JOIN filter) |
| `GetPlayerStoredRankings(accountId, songId?)` | `Dictionary<string, (Rank, Total)>` (pre-computed rank column) |

**Rank Computation**
| Method | Return |
|---|---|
| `RecomputeRanksForSong(songId)` | `int` — UPDATE rank via ROW_NUMBER subquery, source='scrape' only |
| `RecomputeAllRanks()` | `int` — iterates all songs, recomputes per song |

**Pruning**
| Method | Return |
|---|---|
| `PruneExcessEntries(songId, maxEntries, preserveIds, overThresholdScore?)` | `int` — temp _preserve table, DELETE with ctid NOT IN |
| `PruneAllSongs(maxPerSong, preserveIds, thresholds?)` | `int` — iterates all songs |

**Threshold Band Queries**
| Method | Return |
|---|---|
| `GetScoresInBand(songId, lowerBound, upperBound)` | `List<int>` — scores between bounds, ascending |
| `GetPopulationAtOrBelow(songId, threshold)` | `int` — COUNT below threshold |

**Song Stats**
| Method | Return |
|---|---|
| `ComputeSongStats(maxScores?, realPopulation?)` | `int` — reads prev counts, fresh counts, merges + writes song_stats |
| `GetOverThresholdEntries()` | `List<(AccountId, SongId)>` — entries >105% CHOpt max |
| `PopulateValidScoreOverrides(overrides)` | void — TRUNCATE partition + INSERT |

**Account Rankings**
| Method | Return |
|---|---|
| `ComputeAccountRankings(totalChartedSongs, credibility, populationMedian)` | `int` — massive 4-CTE query (ValidEntries+UNION+Aggregated+WithBayesian+Ranked) |
| `SnapshotRankHistory(topN, additionalIds?, retentionDays)` | `int` — insert today's snapshot, prune old data |
| `GetAccountRankings(rankBy, page, pageSize)` | `(List<AccountRankingDto>, int TotalCount)` |
| `GetAccountRanking(accountId)` | `AccountRankingDto?` |
| `GetAccountRankingNeighborhood(accountId, radius, rankBy)` | `(Above, Self, Below)` |
| `GetRankHistory(accountId, days)` | `List<RankHistoryDto>` |
| `GetRankedAccountCount()` | `int` |
| `GetAllRankingSummaries()` | `List<(AccountId, Rating, SongsPlayed, Rank)>` |
| `GetAllRankingSummariesFull()` | `List<(AccountId, all rankings fields)>` |

**Cache**
| Method | Return |
|---|---|
| `PreWarmRankingsBatch(accountIds)` | void — calls GetPlayerRankings for each account to prime cache |

**Maintenance**
| Method | Return |
|---|---|
| `Checkpoint()` | void — no-op in PG (WAL managed by GlobalLeaderboardPersistence) |

---

### FestivalPersistence (implements IFestivalPersistence)
**File**: `FSTService/Persistence/FestivalPersistence.cs` (~138 lines)
**Responsibility**: Core library's songs table read/write. Bridges `FortniteFestival.Core` Song models → PostgreSQL `songs` table.
**Constructor deps**: `NpgsqlDataSource`

| Method | Return |
|---|---|
| `LoadSongsAsync()` | `Task<IList<Song>>` — SELECT all 18 columns from songs |
| `SaveSongsAsync(songs)` | Task — INSERT ON CONFLICT DO UPDATE per song in transaction |
| `LoadScoresAsync()` | `Task<IList<LeaderboardData>>` — returns empty (deprecated) |

---

### GlobalLeaderboardPersistence
**File**: `FSTService/Persistence/GlobalLeaderboardPersistence.cs` (~859 lines)
**Responsibility**: Orchestrator that coordinates per-instrument databases + meta DB. Single entry point for ScraperWorker. Manages pipelined channel-based persistence for scrape passes.
**Constructor deps**: `IMetaDatabase`, `ILoggerFactory`, `ILogger<GlobalLeaderboardPersistence>`, `NpgsqlDataSource`

#### Core Orchestration
| Method | Return |
|---|---|
| `Initialize()` | void — creates InstrumentDatabase per known instrument |
| `IsReady()` | `bool` — probe each instrument DB |
| `GetOrCreateInstrumentDb(instrument)` | `IInstrumentDatabase` |
| `PersistResult(result, registeredIds?, pgConnTx?)` | `PersistResult` — upsert entries + detect score changes + insert account IDs |
| `GetEntryCounts()` | `Dictionary<string, long>` |

#### Pipelined Writer System
| Method | Return |
|---|---|
| `StartWriters(channelCapacity=128, writeBatchSize=10, ct)` | `PipelineAggregates` — starts bounded channel + writer task per instrument |
| `EnqueueResultAsync(result, registeredIds, ct)` | `ValueTask` — non-blocking unless channel full |
| `DrainWritersAsync()` | Task — signals completion + awaits all writers |
| `FlushDeferredAccountIds()` | `int` — bulk-inserts accumulated account IDs post-drain |

#### Cross-Instrument Operations
| Method | Return |
|---|---|
| `GetPlayerProfile(accountId, songId?, instruments?)` | `List<PlayerScoreDto>` — parallel across instruments |
| `GetSongCountsForInstruments()` | `Dictionary<(SongId,Instrument), int>` — parallel |
| `GetPlayerRankings(accountId, songId?, instruments?)` | `Dictionary<(SongId,Instrument), int>` — parallel |
| `GetPlayerRankingsFiltered(accountId, maxScores, songId?, instruments?)` | `Dictionary<(SongId,Instrument), int>` — parallel |
| `GetPlayerStoredRankings(accountId, songId?, instruments?)` | `Dictionary<(SongId,Instrument), (Rank,Total)>` — parallel |
| `GetRankForScore(instrument, songId, score, maxScore?)` | `int` |
| `GetFilteredPopulation(maxScoresByInstrument, instruments?)` | `Dictionary<(SongId,Instrument), int>` — parallel |
| `GetLeaderboard(songId, instrument, top?, offset)` | `List<LeaderboardEntryDto>?` |
| `GetLeaderboardWithCount(songId, instrument, top?, offset, maxScore?)` | `(Entries, TotalCount)?` |
| `GetLeaderboardCount(songId, instrument)` | `int?` |
| `RecomputeAllRanks()` | `int` — sequential in PG mode (avoids WAL contention) |
| `RecomputeRanksForSongs(songIds)` | `int` — targeted re-rank |
| `CheckpointAll()` | void — parallel checkpoint across all DBs |
| `PreWarmRankingsCache(accountIds)` | void |
| `PreWarmRankingsCacheAsync(accountIds, timeout, ct)` | Task — with timeout guard |
| `GetTotalSongCount()` | `int` — cached |

---

### DataTransferObjects
**File**: `FSTService/Persistence/DataTransferObjects.cs` (~530+ lines)
**DTOs**: `LeaderboardEntryDto`, `PlayerScoreDto`, `ValidScoreFallback`, `ScoreHistoryEntry`, `BackfillStatusInfo`, `HistoryReconStatusInfo`, `SeasonWindowInfo`, `PlayerStatsDto`, `PlayerStatsTier`, `PlayerStatsTiersRow`, `StatsSongRef`, `ScoreChangeRecord`, `RivalsStatusInfo`, `UserRivalRow`, `RivalSongSampleRow`, `RivalComboSummary`, `SongGapEntry`, `LeaderboardRivalRow`, `LeaderboardRivalSongSampleRow`, `AccountRankingDto`, `CompositeRankingDto`, `RankHistoryDto`, `ComboLeaderboardEntry`, `ScrapeRunInfo`, `SongMaxScores`

---

## Schema

### Tables (28 total)

#### Core Data Tables (partitioned)
| Table | PK | Partitioned By | Partitions |
|---|---|---|---|
| `leaderboard_entries` | (song_id, instrument, account_id) | LIST (instrument) | 6: Solo_Guitar, Solo_Bass, Solo_Drums, Solo_Vocals, Solo_PeripheralGuitar, Solo_PeripheralBass |
| `song_stats` | (song_id, instrument) | LIST (instrument) | 6 |
| `account_rankings` | (account_id, instrument) | LIST (instrument) | 6 |
| `rank_history` | (account_id, instrument, snapshot_date) | LIST (instrument) | 6 |
| `valid_score_overrides` | (song_id, instrument, account_id) | LIST (instrument) | 6 |

#### Metadata Tables (unpartitioned)
| Table | PK | Purpose |
|---|---|---|
| `songs` | song_id | Song catalog + CHOpt max scores + path generation metadata |
| `scrape_log` | id (SERIAL) | Scrape run tracking |
| `score_history` | id (SERIAL) + dedup unique index | Score change audit trail |
| `account_names` | account_id | Display name resolution cache |
| `registered_users` | (device_id, account_id) | Registered user tracking |
| `user_sessions` | id (SERIAL) | JWT session management |
| `backfill_status` | account_id | Backfill task state machine |
| `backfill_progress` | (account_id, song_id, instrument) | Per-song backfill checkpoint |
| `history_recon_status` | account_id | History reconstruction state machine |
| `history_recon_progress` | (account_id, song_id, instrument) | Per-song recon checkpoint |
| `season_windows` | season_number | Epic season event/window mapping |
| `song_first_seen_season` | song_id | Song release season estimation |
| `epic_user_tokens` | account_id | Encrypted Epic OAuth tokens (BYTEA) |
| `leaderboard_population` | (song_id, instrument) | Real leaderboard population tracking |
| `player_stats` | (account_id, instrument) | Precomputed player statistics |
| `player_stats_tiers` | (account_id, instrument) | JSONB leeway breakpoint tiers |
| `data_version` | key | Schema/data version tracking |
| `rivals_status` | account_id | Rivals computation state machine |
| `user_rivals` | (user_id, rival_account_id, instrument_combo) | Precomputed per-user rivals |
| `rival_song_samples` | (user_id, rival_account_id, instrument, song_id) | Per-song rival comparisons |
| `item_shop_tracks` | song_id | Current item shop state |
| `composite_rankings` | account_id | Cross-instrument composite ranking |
| `leaderboard_rivals` | (user_id, rival_account_id, instrument, rank_method) | Rank-based rivals |
| `leaderboard_rival_song_samples` | (user_id, rival_account_id, instrument, rank_method, song_id) | Rank-rival song details |
| `composite_rank_history` | (account_id, snapshot_date) | Daily composite rank snapshots |
| `combo_leaderboard` | (combo_id, account_id) | Multi-instrument combo leaderboards |
| `combo_stats` | combo_id | Combo participant counts |

### Key Indexes

**leaderboard_entries** (5 indexes):
- `ix_le_song_score` — (song_id, instrument, score DESC) — leaderboard display
- `ix_le_account` — (account_id, instrument) — player profile queries
- `ix_le_account_song` — (account_id, song_id, instrument) — single entry lookups
- `ix_le_song_source` — (song_id, instrument, source) — source filtering
- `ix_le_song_rank` — (song_id, instrument, rank) — rank-based neighborhood queries

**account_rankings** (5 unique indexes):
- `ix_ar_skill` — (instrument, adjusted_skill_rank) — skill leaderboard
- `ix_ar_weighted` — (instrument, weighted_rank)
- `ix_ar_fc_rate` — (instrument, fc_rate_rank)
- `ix_ar_total_score` — (instrument, total_score_rank)
- `ix_ar_max_score_pct` — (instrument, max_score_percent_rank)

**score_history**:
- `ix_sh_account` — (account_id) — player history
- `ix_sh_song` — (song_id, instrument) — song history
- `ix_sh_dedup` — UNIQUE (account_id, song_id, instrument, new_score, score_achieved_at) — deduplication

**account_names**:
- `ix_an_unresolved` — (last_resolved) WHERE last_resolved IS NULL — name resolution queue
- `ix_an_name` — (display_name) WHERE display_name IS NOT NULL — name search

---

## Query Catalog

### Expensive Queries (CommandTimeout extended)

1. **ComputeAccountRankings** (timeout: 300s) — 4-CTE Bayesian ranking:
   ```
   ValidEntries (leaderboard_entries JOIN song_stats + UNION valid_score_overrides)
   → Aggregated (GROUP BY account_id with AVG, SUM, COUNT)
   → WithBayesian (Bayesian smoothing with credibility threshold)
   → Ranked (5x ROW_NUMBER for 5 rank methods)
   → INSERT INTO account_rankings
   ```

2. **InsertScoreChanges staging merge** (timeout: 120s) — COPY → staging → INSERT SELECT ON CONFLICT

3. **RecomputeRanksForSong** — UPDATE with ROW_NUMBER subquery per song

### Common Query Patterns

**Rank computation** (used in GetPlayerRankings):
```sql
WITH player_songs AS (SELECT song_id FROM leaderboard_entries WHERE account_id = @accountId AND instrument = @instrument),
     ranked AS (SELECT account_id, song_id, ROW_NUMBER() OVER (PARTITION BY song_id ORDER BY score DESC, ...) AS rank
                FROM leaderboard_entries WHERE instrument = @instrument AND song_id IN (SELECT ...))
SELECT song_id, rank FROM ranked WHERE account_id = @accountId
```

**Filtered ranking** (GetPlayerRankingsFiltered): Same CTE with temp table `_max_thresholds` joined to filter `score <= COALESCE(mt.max_score, le.score + 1)`

**Leaderboard display** (GetLeaderboardWithCount):
```sql
SELECT ..., ROW_NUMBER() OVER (ORDER BY score DESC, COALESCE(end_time, first_seen_at::TEXT) ASC) AS rank,
       COUNT(*) OVER () AS total_count
FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument
ORDER BY score DESC, ... LIMIT ... OFFSET ...
```

---

## Bulk Write Patterns

### COPY Binary (Npgsql BeginBinaryImport)

Used in 3 locations:

1. **InstrumentDatabase.UpsertEntriesBulk** (threshold: >50 entries)
   - Creates temp table `_le_staging` (ON COMMIT DROP or explicit DROP)
   - COPY binary: 15 columns (song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, ts)
   - Merge: `INSERT INTO leaderboard_entries SELECT DISTINCT ON (...) FROM _le_staging ON CONFLICT DO UPDATE SET ...`
   - Complex CASE WHEN merge logic: only update if score changed, preserve source priority (scrape > backfill > neighbor)

2. **MetaDatabase.InsertScoreChanges** (threshold: >20 changes)
   - Creates temp table `_sh_staging` (ON COMMIT DROP)
   - COPY binary: 17 columns
   - Merge: INSERT … ON CONFLICT DO UPDATE with COALESCE to preserve existing data

3. **MetaDatabase.InsertAccountIds** (threshold: >50 IDs)
   - Creates temp table `_acct_staging` (ON COMMIT DROP)
   - COPY binary: 1 column (account_id)
   - Merge: INSERT … ON CONFLICT DO NOTHING

### Prepared-Statement Loops

Used for small batches (below COPY thresholds):
- `cmd.Prepare()` called once, parameters rebound in loop
- Explicit `NpgsqlParameter` variables (not AddWithValue) for prepared statements
- Transactions wrap the entire loop

### Batch Transaction Pattern (Pipelined Writer)

The `RunBatchedWriterAsync` in GlobalLeaderboardPersistence:
1. Opens a single connection + transaction
2. Sets `SET LOCAL synchronous_commit = off`
3. Processes up to `writeBatchSize` (default 10) work items in one transaction
4. Each item may use COPY binary or loop internally (using the shared conn/tx)
5. Single `tx.Commit()` at the end — amortizes commit overhead

---

## Partitioning Strategy

### List Partitioning by Instrument

5 parent tables are partitioned, each with 6 partitions:

| Partition Name Suffix | Instrument Value |
|---|---|
| `_solo_guitar` | `Solo_Guitar` |
| `_solo_bass` | `Solo_Bass` |
| `_solo_drums` | `Solo_Drums` |
| `_solo_vocals` | `Solo_Vocals` |
| `_pro_guitar` | `Solo_PeripheralGuitar` |
| `_pro_bass` | `Solo_PeripheralBass` |

**Why partitioning?**
- Queries always filter by instrument → partition pruning eliminates 5/6 of storage
- TRUNCATE per partition is instant (used in ComputeAccountRankings, PopulateValidScoreOverrides)
- Indexes are local to partitions → smaller, faster

**Partition name resolution** (`GetPartitionName` in InstrumentDatabase):
Used to directly TRUNCATE the correct partition: `TRUNCATE {partitionName}` instead of `DELETE WHERE instrument = ...`

### Partition Usage Patterns
- **ComputeAccountRankings**: `TRUNCATE {GetPartitionName("account_rankings")}` before bulk INSERT
- **PopulateValidScoreOverrides**: `TRUNCATE {GetPartitionName("valid_score_overrides")}` before INSERT
- **All reads**: `WHERE instrument = @instrument` enables automatic partition pruning

---

## Connection Patterns

### NpgsqlDataSource (Connection Pooling)

All database classes receive an `NpgsqlDataSource` via DI constructor injection. This provides transparent connection pooling.

**Standard pattern (synchronous)**:
```csharp
using var conn = _ds.OpenConnection();
using var cmd = conn.CreateCommand();
cmd.CommandText = "...";
cmd.Parameters.AddWithValue("param", value);
// Execute
```

**Async pattern** (only in DatabaseInitializer and FestivalPersistence):
```csharp
await using var conn = await _ds.OpenConnectionAsync(ct);
await using var cmd = conn.CreateCommand();
// ...
await cmd.ExecuteNonQueryAsync(ct);
```

### Transaction Usage

**Explicit transactions** are used for:
1. Multi-statement writes (COPY → staging → merge)
2. Replace patterns (DELETE all → INSERT new)
3. Temp table lifetime (`ON COMMIT DROP`)
4. `SET LOCAL synchronous_commit = off` (scrape writes)
5. Cascade-delete on UnregisterUser

**`SET LOCAL synchronous_commit = off`**:
Used in UpsertEntriesBulk and UpsertEntriesLoop for scrape data — trades crash safety for 5-10x commit throughput. Scrape data is re-scrape-able.

### External Connection Pattern

InstrumentDatabase supports external connection/transaction for batched writes:
```csharp
public int UpsertEntries(string songId, IReadOnlyList<LeaderboardEntry> entries,
                         NpgsqlConnection conn, NpgsqlTransaction tx)
```
Used by `RunBatchedWriterAsync` to batch multiple song upserts into a single PG transaction.

### Per-Method Connection Lifetime

Every public method in MetaDatabase and InstrumentDatabase opens and closes its own connection. No connection is held across method calls. This is the recommended Npgsql pattern for connection-pooled workloads — the pool handles reuse.

### WriteNullableInt Helper

Used in COPY binary paths to write `NpgsqlDbType.Integer` or null:
```csharp
static void WriteNullableInt(NpgsqlBinaryImporter writer, int? value)
{
    if (value.HasValue) writer.Write(value.Value, NpgsqlDbType.Integer);
    else writer.WriteNull();
}
```

### ParseUtc Helper

Parses ISO 8601 timestamps to `DateTime` with `DateTimeStyles.RoundtripKind | AssumeUniversal`:
```csharp
static DateTime ParseUtc(string s) => DateTime.Parse(s, null,
    System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.AssumeUniversal);
```

---

## Key Design Decisions

1. **Synchronous over Async**: MetaDatabase and InstrumentDatabase use synchronous ADO.NET. Only DatabaseInitializer and FestivalPersistence use async. This is intentional — the scrape pipeline runs on dedicated threads, and sync avoids async state machine overhead.

2. **No ORM**: Raw Npgsql throughout. All queries are hand-written parameterized SQL.

3. **Dual write path**: Every bulk operation has both a COPY binary path (large batches) and a prepared-statement loop (small batches), with a threshold constant controlling the switch.

4. **MapRankColumn whitelist**: Prevents SQL injection by mapping user-provided rank method names to a hardcoded set of column names.

5. **Sequential rank recomputation in PG**: `RecomputeAllRanks` runs sequentially (not parallel) because all instruments share the same PG instance — parallel massive UPDATEs contend for WAL writer.
