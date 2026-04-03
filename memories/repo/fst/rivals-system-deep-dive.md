# Rivals System Deep Dive

> Last updated: 2026-04-03

## Rivals Overview

The rivals system finds players with similar skill levels to a registered user and tracks per-song head-to-head comparisons. There are **two independent rival systems**:

1. **Per-Song Rivals** — Scans rank neighborhoods across every song on each instrument to find players who consistently appear near the user. Produces weighted rivalry scores and per-instrument/combo rival lists.
2. **Leaderboard Rivals** — Takes the ±N neighbors on the global leaderboard for each instrument × rank method and compares their shared songs. Produces per-instrument per-rankmethod rival data.

Both produce "above" and "below" rivals (players ranked higher and lower than the user). Both include per-song sample rows showing rank and score comparisons on individual songs.

The frontend uses the term "opps" (opponents) as branding; the backend consistently uses "rivals."

### Key Classes

| Class | File | Purpose |
|---|---|---|
| `RivalsCalculator` | `FSTService/Scraping/RivalsCalculator.cs` | Per-song rival computation algorithm |
| `RivalsOrchestrator` | `FSTService/Scraping/RivalsOrchestrator.cs` | Orchestrates per-song rivals for all registered users |
| `LeaderboardRivalsCalculator` | `FSTService/Scraping/LeaderboardRivalsCalculator.cs` | Leaderboard-based rival computation |
| `ApiEndpoints` (partial) | `FSTService/Api/RivalsEndpoints.cs` | Per-song rival REST endpoints |
| `ApiEndpoints` (partial) | `FSTService/Api/LeaderboardRivalsEndpoints.cs` | Leaderboard rival REST endpoints |
| `MetaDatabase` | `FSTService/Persistence/MetaDatabase.cs` | Rivals persistence (lines 605–630) |
| `ComboIds` | `FSTService/ComboIds.cs` | Bitmask-based instrument combo IDs |

---

## Matching Algorithm (Per-Song Rivals)

### Constants

| Constant | Value | Description |
|---|---|---|
| `MinUserSongsPerInstrument` | 10 | User must have ≥10 songs on an instrument to include it |
| `MinSharedSongsPerInstrument` | 5 | Rival must share ≥5 songs with user on a single instrument |
| `MinSharedSongsPerInstrumentInCombo` | 3 | Lower threshold for multi-instrument combo qualification |
| `NeighborhoodRadius` | 50 | Scans ±50 ranks around user's rank on each song |
| `RivalsPerDirection` | 10 | Top 10 above + top 10 below per combo |
| `MaxSamplesPerRivalPerInstrument` | 200 | Max per-song comparison rows stored per rival per instrument |

### Algorithm Steps

**Step 1 — Gather user entries per instrument:**
- For each instrument key, load the user's scores from `InstrumentDatabase.GetPlayerScores(userId)`
- Skip instruments where the user has fewer than `MinUserSongsPerInstrument` songs

**Step 2 — Scan neighborhoods:**
- For each of the user's ranked songs, query `InstrumentDatabase.GetNeighborhood(songId, effectiveRank, radius=50, excludeUserId)`
- Prefers dense `Rank` (set by `RecomputeAllRanks`) over `ApiRank` (Epic's global rank)
- Skips songs where the user is unranked or only one player has a score

**Step 3 — Accumulate candidates:**
- Each neighbor found becomes a `RivalCandidate`
- Per-encounter stats accumulated:
  - `Appearances` — number of shared songs
  - `WeightedScore += log2(songEntryCount) / (1 + |rankDelta|)` — popular songs with close ranks score higher
  - `SignedDeltaSum += neighborRank - userRank` — positive = behind, negative = ahead  
  - `AheadCount` / `BehindCount` — directional counters
  - `SongDetails` — full per-song comparison data preserved for sample selection

**Step 4 — Per-instrument rival selection:**
- Filter candidates where `Appearances >= MinSharedSongsPerInstrument`
- Classify by `AvgSignedDelta`: negative = "above" (rival is ahead), positive/zero = "below"
- Take top `RivalsPerDirection` (10) per direction, ordered by `WeightedScore` descending
- Combo key for single instruments: `ComboIds.FromInstruments([instrument])` (hex bitmask)

**Step 5 — Combination rival computation:**
- Generate all non-empty subsets (power set) of valid instruments using bitmask enumeration
- For multi-instrument combos (≥2), intersect candidates across all instruments — a candidate must appear in **all** instruments in the combo
- Combined score = sum of per-instrument `WeightedScore`
- Combined `AvgSignedDelta` = weighted average (weighted by per-instrument score)
- Lower qualification threshold: `MinSharedSongsPerInstrumentInCombo` (3)

**Step 6 — Sample selection:**
- For each selected rival (across all combos), collect `SongDetail` entries per instrument
- Sort by `|RankDelta|` ascending (closest matches first)
- Take up to `MaxSamplesPerRivalPerInstrument` (200) per instrument

### Scoring Formula

```
WeightedScore = Σ log2(songEntryCount) / (1 + |rankDelta|)
```

- `songEntryCount`: total players who have a score on that song (more popular songs weigh more)
- `rankDelta`: rank distance between user and neighbor on that song (closer = higher weight)
- The `log2` dampens extremely popular songs from dominating

---

## Leaderboard Rivals

### Algorithm (LeaderboardRivalsCalculator)

**Inputs:**
- `ScraperOptions.LeaderboardRivalRadius` (default 10): ±N neighbors on the global leaderboard
- `RankMethods`: `["totalscore", "adjusted", "weighted", "fcrate", "maxscore"]`

**Per instrument × rank method:**
1. Query `InstrumentDatabase.GetAccountRankingNeighborhood(userId, radius, rankMethod)` → above neighbors, self, below neighbors
2. For each neighbor, load their scores on the user's songs via `GetPlayerScoresForSongs()`
3. Compare every shared song: compute `rankDelta = rivalSongRank - userSongRank`
4. Aggregate: `sharedSongCount`, `aheadCount`, `behindCount`, `avgSignedDelta`
5. Store top 200 closest song samples per rival

**Key differences from per-song rivals:**
- No weighted scoring formula — simply uses leaderboard proximity
- Direction is "above"/"below" based on leaderboard rank, not per-song delta
- Keyed by `(instrument, rankMethod)` instead of instrument combo
- Persists `userRank` and `rivalRank` on the leaderboard (not just song ranks)
- Neighbor scores are cached per instrument to avoid re-fetching across rank methods
- Only replaces data when user is found in `AccountRankings` — preserves previous data if rankings haven't been computed yet

---

## Data Model

### Tables

#### `rivals_status`
Tracks computation status per user.

| Column | Type | Description |
|---|---|---|
| `account_id` | TEXT PK | User's Epic account ID |
| `status` | TEXT | `pending`, `in_progress`, `complete`, `error` |
| `combos_computed` | INTEGER | Number of instrument combos processed |
| `total_combos_to_compute` | INTEGER | Expected combos (for progress tracking) |
| `rivals_found` | INTEGER | Total rival rows produced |
| `started_at` | TIMESTAMPTZ | When computation began |
| `completed_at` | TIMESTAMPTZ | When computation finished |
| `error_message` | TEXT | Error details if status is `error` |

#### `user_rivals`
One row per rival per instrument combo.

| Column | Type | Description |
|---|---|---|
| `user_id` | TEXT | The registered user |
| `rival_account_id` | TEXT | The rival player |
| `instrument_combo` | TEXT | Hex combo ID (e.g., "01", "03", "0f") |
| `direction` | TEXT | `above` or `below` |
| `rival_score` | REAL | Weighted rivalry score |
| `avg_signed_delta` | REAL | Average rank delta (negative = rival ahead) |
| `shared_song_count` | INTEGER | Songs appearing in both players' data |
| `ahead_count` | INTEGER | Songs where rival is ahead |
| `behind_count` | INTEGER | Songs where rival is behind |
| `computed_at` | TIMESTAMPTZ | Timestamp of computation |

**PK:** `(user_id, rival_account_id, instrument_combo)`
**Index:** `ix_ur_combo` on `(user_id, instrument_combo, direction, rival_score DESC)`

#### `rival_song_samples`
Per-song comparison data between user and rival.

| Column | Type | Description |
|---|---|---|
| `user_id` | TEXT | The registered user |
| `rival_account_id` | TEXT | The rival player |
| `instrument` | TEXT | Single instrument key |
| `song_id` | TEXT | Song identifier |
| `user_rank` | INTEGER | User's rank on this song |
| `rival_rank` | INTEGER | Rival's rank on this song |
| `rank_delta` | INTEGER | `rival_rank - user_rank` |
| `user_score` | INTEGER? | User's score |
| `rival_score` | INTEGER? | Rival's score |

**PK:** `(user_id, rival_account_id, instrument, song_id)`
**Index:** `ix_rs_rival` on `(user_id, rival_account_id, instrument)`

#### `leaderboard_rivals`
Global leaderboard-based rival relationships.

| Column | Type | Description |
|---|---|---|
| `user_id` | TEXT | Registered user |
| `rival_account_id` | TEXT | Rival player |
| `instrument` | TEXT | Instrument key |
| `rank_method` | TEXT | `totalscore`, `adjusted`, `weighted`, `fcrate`, `maxscore` |
| `direction` | TEXT | `above` or `below` |
| `user_rank` | INTEGER | User's leaderboard rank |
| `rival_rank` | INTEGER | Rival's leaderboard rank |
| `shared_song_count` | INTEGER | Shared songs count |
| `ahead_count` | INTEGER | Songs where user is ahead |
| `behind_count` | INTEGER | Songs where rival is ahead |
| `avg_signed_delta` | REAL | Average signed rank delta |
| `computed_at` | TIMESTAMPTZ | Computation timestamp |

**PK:** `(user_id, rival_account_id, instrument, rank_method)`
**Index:** `ix_lbr_user_inst` on `(user_id, instrument, rank_method, direction)`

#### `leaderboard_rival_song_samples`
Per-song comparison data for leaderboard rivals.

| Column | Type | Description |
|---|---|---|
| `user_id`, `rival_account_id`, `instrument`, `rank_method`, `song_id` | TEXT | Composite key |
| `user_rank`, `rival_rank`, `rank_delta` | INTEGER | Song-level rank comparison |
| `user_score`, `rival_score` | INTEGER? | Song scores |

**PK:** `(user_id, rival_account_id, instrument, rank_method, song_id)`
**Index:** `ix_lbrss_user_rival` on `(user_id, rival_account_id, instrument, rank_method)`

### DTOs

| DTO | File | Used by |
|---|---|---|
| `UserRivalRow` | `DataTransferObjects.cs:272` | Per-song rivals |
| `RivalSongSampleRow` | `DataTransferObjects.cs:~295` | Per-song song samples |
| `RivalComboSummary` | `DataTransferObjects.cs:~320` | Combo overview counts |
| `SongGapEntry` | `DataTransferObjects.cs:~330` | Song gap analysis |
| `LeaderboardRivalRow` | `DataTransferObjects.cs:~345` | Leaderboard rivals |
| `LeaderboardRivalSongSampleRow` | `DataTransferObjects.cs:~365` | Leaderboard rival song samples |
| `RivalsStatusInfo` | `DataTransferObjects.cs:~260` | Computation status tracking |
| `RivalsResult` | `RivalsCalculator.cs:675` | Return type from `ComputeRivals()` |
| `SongGapsResult` | `RivalsCalculator.cs:687` | Return type from `ComputeSongGaps()` |
| `LeaderboardRivalsResult` | `LeaderboardRivalsCalculator.cs:~170` | Return type from leaderboard computation |

### Persistence Operations (MetaDatabase)

| Method | Description |
|---|---|
| `EnsureRivalsStatus(accountId)` | INSERT pending status if not exists |
| `StartRivals(accountId, totalCombos)` | Mark `in_progress`, record expected combos |
| `CompleteRivals(accountId, combos, rivalsFound)` | Mark `complete` with counts |
| `FailRivals(accountId, errorMessage)` | Mark `error` with message |
| `GetRivalsStatus(accountId)` | Read status row → `RivalsStatusInfo?` |
| `GetPendingRivalsAccounts()` | List account IDs with `pending` or `in_progress` status |
| `ReplaceRivalsData(userId, rivals, samples)` | Delete + re-insert all rival data in a transaction |
| `GetUserRivals(userId, combo?, direction?)` | Query `user_rivals` with optional filters |
| `GetRivalCombos(userId)` | Aggregate above/below counts per combo |
| `GetRivalSongSamples(userId, rivalId, instrument?)` | Query song samples |
| `GetAllRivalSongSamplesForUser(userId)` | All samples grouped by rival ID |
| `ReplaceLeaderboardRivalsData(userId, instrument, rivals, samples)` | Replace LB rivals per instrument |
| `GetLeaderboardRivals(userId, instrument, rankMethod)` | Query LB rivals |
| `GetLeaderboardRivalSongSamples(userId, rivalId, instrument, rankMethod)` | Query LB song samples |

### Data Replacement Strategy

Both per-song and leaderboard rivals use a **full-replace** strategy within a transaction:
1. DELETE all existing song samples for the user (per-song) or user+instrument (leaderboard)
2. DELETE all existing rival rows for the user/instrument
3. INSERT all new rival rows (prepared statement, batch loop)
4. INSERT all new song sample rows (prepared statement, batch loop)

Account removal cascades: when an account is deregistered, `user_rivals` and `rival_song_samples` rows for that `user_id` are cleaned up.

---

## API Endpoints

### Per-Song Rivals

| Route | Method | Auth | Rate Limit | Cache | Description |
|---|---|---|---|---|---|
| `/api/player/{accountId}/rivals` | GET | None | public | 300s + precomputed | Combo overview — lists combos with above/below counts |
| `/api/player/{accountId}/rivals/all` | GET | None | public | 300s + precomputed | Batch: all combos with full rival lists in one call |
| `/api/player/{accountId}/rivals/suggestions` | GET | None | public | 300s | Batch rival data for suggestion generation |
| `/api/player/{accountId}/rivals/diagnostics` | GET | Auth | protected | None | Diagnostic info revealing the computation funnel |
| `/api/player/{accountId}/rivals/{combo}` | GET | None | public | 300s | Rival list for a specific combo (above + below) |
| `/api/player/{accountId}/rivals/{combo}/{rivalId}` | GET | None | public | 120s | Detailed head-to-head with song data, song gaps |
| `/api/player/{accountId}/rivals/{rivalId}/songs/{instrument}` | GET | None | public | 120s | Per-instrument songs for a specific rival |
| `/api/player/{accountId}/rivals/recompute` | POST | Auth | protected | None | Force recompute rivals for a user |

**Query parameters for paginated endpoints:**
- `limit` (default 50, 0 = all), `offset` (default 0), `sort` (`closest` | `they_lead` | `you_lead`)

**Query parameters for suggestions endpoint:**
- `combo` (optional, filter by combo), `limit` (default 5, rivals per direction)

### Leaderboard Rivals

| Route | Method | Auth | Rate Limit | Cache | Description |
|---|---|---|---|---|---|
| `/api/player/{accountId}/leaderboard-rivals/{instrument}` | GET | None | public | 300s + precomputed | List rivals by instrument + rank method |
| `/api/player/{accountId}/leaderboard-rivals/{instrument}/{rivalId}` | GET | None | public | 300s | Head-to-head detail with song samples + song gaps |

**Query parameters:**
- `rankBy` (default `totalscore`): `totalscore`, `adjusted`, `weighted`, `fcrate`, `maxscore`
- `sort` (default `closest`): `closest`, `they_lead`, `you_lead`

### Response Shapes

**Combo overview** (`/rivals`):
```json
{
  "accountId": "...",
  "computedAt": "2026-04-03T12:00:00Z",
  "combos": [
    { "combo": "01", "aboveCount": 10, "belowCount": 10 }
  ]
}
```

**Rival list** (`/rivals/{combo}`):
```json
{
  "combo": "01",
  "above": [
    { "accountId": "...", "displayName": "...", "rivalScore": 15.2, "sharedSongCount": 42, "aheadCount": 28, "behindCount": 14, "avgSignedDelta": -3.5 }
  ],
  "below": [ ... ]
}
```

**Rival detail** (`/rivals/{combo}/{rivalId}`):
```json
{
  "rival": { "accountId": "...", "displayName": "..." },
  "combo": "01",
  "totalSongs": 42,
  "offset": 0, "limit": 50, "sort": "closest",
  "songs": [
    { "songId": "...", "title": "...", "artist": "...", "instrument": "Solo_Guitar", "userRank": 5, "rivalRank": 8, "rankDelta": 3, "userScore": 999000, "rivalScore": 998500 }
  ],
  "songsToCompete": [ { "songId": "...", "title": "...", "instrument": "...", "score": 999000, "rank": 3 } ],
  "yourExclusiveSongs": [ ... ]
}
```

**Leaderboard rival list** (`/leaderboard-rivals/{instrument}`):
```json
{
  "instrument": "Solo_Guitar",
  "rankBy": "totalscore",
  "userRank": 42,
  "above": [
    { "accountId": "...", "displayName": "...", "sharedSongCount": 100, "aheadCount": 55, "behindCount": 45, "avgSignedDelta": -2.1, "leaderboardRank": 41, "userLeaderboardRank": 42 }
  ],
  "below": [ ... ]
}
```

### Caching

- Two dedicated `ResponseCacheService` instances: `"RivalsCache"` and `"LeaderboardRivalsCache"` (DI keyed singletons)
- ETag-based HTTP caching with `Cache-Control: public, max-age=300, stale-while-revalidate=600` (overview/list) or `max-age=120, stale-while-revalidate=300` (detail)
- `ScrapeTimePrecomputer` precomputes common responses at scrape completion
- Per-song rivals cache invalidated on `RivalsOrchestrator.ComputeForUser()` completion
- Song gaps in `RivalsCalculator` cached in-memory with 5-minute TTL (`SongGapsCacheTtl`), invalidated per user after rivals recompute

---

## Calculation Timing

### Pipeline Position (PostScrapeOrchestrator.RunAsync)

```
1. RunEnrichmentAsync           — ranks, firstSeen, nameRes, pruning
2. RefreshRegisteredUsersAsync  — ensure low scores present in DBs
3. ComputeRankingsAsync         — global rankings (AccountRankings, CompositeRankings)
4. [PARALLEL]
   ├─ ComputeRivalsAsync        — per-song rivals (RivalsOrchestrator.ComputeAllAsync)
   └─ ComputeLeaderboardRivalsAsync — leaderboard rivals (skipped if rankings failed)
5. PrecomputeAllAsync + PlayerStatsTiers — precompute API responses
6. Checkpoint + cache warm
```

### Triggering

**Per-song rivals:**
- Every scrape pass, for all registered users with `pending` or `in_progress` status
- Also triggered for users whose scores changed (`ChangedAccountIds` from scrape aggregates)
-  `dirtyInstruments` map: currently marks all instruments dirty for any changed user (no per-instrument tracking yet)
- Manual trigger: `POST /api/player/{accountId}/rivals/recompute`

**Leaderboard rivals:**
- Every scrape pass, after rankings have been successfully computed
- Skipped entirely if `ComputeRankingsAsync` failed (would wipe data for stale rankings)
- Runs in parallel with per-song rivals

### Parallelism

- **Per-song rivals**: `RivalsOrchestrator.ComputeAllAsync` runs users in parallel via `Task.Run` per user. Each user reads instrument DBs under WAL isolation.
- **Leaderboard rivals**: `ComputeLeaderboardRivalsAsync` runs users in parallel via `Task.Run` per user.
- Both per-song and leaderboard rivals run in parallel with each other (no shared write targets).

### Notifications

- `NotificationService.NotifyRivalsCompleteAsync(accountId)` is called (best-effort) after per-song rivals complete for a user
- Used to notify the frontend via WebSocket that fresh rival data is available

### After Backfill

- `RivalsOrchestrator.ComputeForUser()` is called directly after backfill completion (from `PostScrapeOrchestrator` via the backfill pipeline)

---

## Song Gaps Feature

`RivalsCalculator.ComputeSongGaps(userId, rivalId, instruments)` is computed on-the-fly (not stored) when a rival detail endpoint is called.

- **Songs to compete**: Songs the rival has scored on that the user hasn't — opportunities to beat the rival's score
- **Your exclusives**: Songs the user has scored on that the rival hasn't — competitive advantage
- Sorted by rank ascending (best-ranked songs first)
- Capped at `MaxSongGapsPerDirection` (100) entries per direction
- Results cached in-memory for 5 minutes per (user, rival, instruments) tuple

---

## Combo IDs

Instrument combinations are encoded as 2-digit hex bitmasks via `ComboIds`:

| Instrument | Bit | Solo Hex |
|---|---|---|
| Solo_Guitar | 0 | `01` |
| Solo_Bass | 1 | `02` |
| Solo_Drums | 2 | `04` |
| Solo_Vocals | 3 | `08` |
| Solo_PeripheralGuitar | 4 | `10` |
| Solo_PeripheralBass | 5 | `20` |

Examples: Guitar+Bass = `03`, All Pad (G+B+D+V) = `0f`, All 6 = `3f`

API endpoints accept both hex IDs and legacy `Instrument+Instrument` format, normalized via `ComboIds.NormalizeAnyComboParam()`.

---

## DI Registration (Program.cs)

```csharp
builder.Services.AddKeyedSingleton<ResponseCacheService>("RivalsCache", ...);
builder.Services.AddSingleton<RivalsCalculator>();
builder.Services.AddSingleton<RivalsOrchestrator>();
builder.Services.AddSingleton<LeaderboardRivalsCalculator>();
builder.Services.AddKeyedSingleton<ResponseCacheService>("LeaderboardRivalsCache", ...);
```

All are singletons — rivals calculation reads from instrument DBs (WAL-safe) and writes to MetaDatabase within transactions.
