# Opps Feature Design

## Overview

"Opps" (inspired by Rivals in Rock Band Rivals) identifies players who are most consistently competitive with a given user across the global leaderboards. Rather than looking at a single song, Opps surfaces the accounts that repeatedly appear near the user's scores across many songs and instruments — the people you're actually racing against.

---

## Concept

For a registered user:

1. Look at every song/instrument where they have a leaderboard entry.
2. For each entry, grab the surrounding ±50 ranks (the "neighborhood").
3. Across all neighborhoods, count how often each other account appears.
4. The accounts with the highest frequency are the user's **Opps** — their most consistent rivals.

A player who shows up near you on 200 different songs is a much more meaningful rival than someone who edges you out on one song. The frequency-based approach naturally produces that ranking.

---

## Algorithm

### Step 1: Gather the User's Entries

For each instrument DB (`fst-Solo_*.db`), query all of the user's entries:

```sql
SELECT SongId, Rank, Score
FROM LeaderboardEntries
WHERE AccountId = @userId
```

This gives us every (SongId, Instrument, Rank) tuple where the user has a score. Typically a few hundred to a couple thousand entries for an active player across 6 instruments.

### Step 2: Fetch Neighborhoods

For each user entry, query the surrounding ±50 ranks in the same instrument DB:

```sql
SELECT AccountId, Rank, Score
FROM LeaderboardEntries
WHERE SongId = @songId
  AND Rank BETWEEN (@userRank - 50) AND (@userRank + 50)
  AND AccountId != @userId
```

This is cheap — it hits the `IX_Song (SongId, Rank)` index and returns at most 100 rows per query.

### Step 3: Aggregate Across All Songs/Instruments

Build a frequency map: `Dictionary<string accountId, OppCandidate>` where:

```
OppCandidate {
    AccountId       string
    Appearances     int       // how many song/instrument neighborhoods they appeared in
    TotalRankDelta  long      // sum of |theirRank - userRank| across appearances (lower = closer)
    SignedRankDelta long      // sum of (theirRank - userRank) — positive = Opp is behind user, negative = Opp is ahead
    AheadCount      int       // how many entries the Opp is ranked above (ahead of) the user
    BehindCount     int       // how many entries the Opp is ranked below (behind) the user
    Instruments     set       // which instruments they've appeared on
    SongDetails     list      // per-song/instrument detail (for sample selection)
}

OppSongDetail {
    SongId          string
    Instrument      string    // e.g. "Solo_Guitar"
    UserRank        int
    OppRank         int
    RankDelta       int       // oppRank - userRank (signed)
    UserScore       int
    OppScore        int
}
```

During aggregation, every neighborhood hit appends an `OppSongDetail` entry. After selecting the top-N Opps, each Opp's `SongDetails` list is sorted by `|RankDelta|` ascending (closest songs first) and the top **K** (default 10) are persisted as **song samples** — the songs where this Opp is closest to the user.
```

For each neighboring entry:
- Increment `Appearances`
- Add `|neighborRank - userRank|` to `TotalRankDelta`
- Add `(neighborRank - userRank)` to `SignedRankDelta` (positive means they're behind, negative means they're ahead — lower rank number = better)
- If `neighborRank < userRank` → increment `AheadCount`; else increment `BehindCount`
- Track which instrument and song

### Step 4: Rank and Select Opps

Sort candidates by:
1. **Primary**: `Appearances` descending (most frequent neighbors first)
2. **Secondary**: `AvgRankDelta` ascending (`TotalRankDelta / Appearances` — tiebreak by who's closest on average)

Take the top **N** (configurable, default 25) as the user's Opps.

#### Directional Splitting

The signed data enables splitting into two meaningful lists:

- **"Opps to Beat"** — Opps where `AvgSignedDelta < 0` (they're generally ranked ahead). These are the players the user is chasing. Sort by `AvgSignedDelta` ascending (closest ahead first).
- **"Opps on Your Tail"** — Opps where `AvgSignedDelta > 0` (they're generally ranked behind). These are the players chasing the user. Sort by `AvgSignedDelta` ascending (closest behind first).

An Opp with `AvgSignedDelta ≈ 0` is a true dead-heat rival — sometimes ahead, sometimes behind. The `AheadCount` / `BehindCount` fields show the split.

Example API usage: "Show me the top 10 Opps I need to beat and the next 10 closest Opps behind me" is just filtering the single Opps list by sign of `AvgSignedDelta`.

### Step 5: Persist

Store the computed Opps in the meta DB, along with a timestamp. Recompute periodically (after each scrape pass, or on-demand).

---

## Scoring Refinements (Future Iterations)

The basic frequency count works well as a v1. Possible refinements for later:

| Refinement | Description | Complexity |
|---|---|---|
| **Rank-distance weighting** | Neighbors within ±10 ranks count more than those at ±50. e.g., weight = `1.0 / (1 + abs(delta))` summed across appearances. | Low |
| **Instrument diversity bonus** | An Opp who appears on 4 different instruments is a more "complete" rival than one who only plays Guitar. Could multiply score by `sqrt(numInstruments)`. | Low |
| **Recency weighting** | Weight entries on newer songs higher than entries on old songs (using `LastUpdatedAt`). | Medium |
| **Score gap tracking** | Track not just rank proximity but score proximity (how many points apart). Useful for "you need X more points to pass them." | Low |

These can be layered on without schema changes — they only affect the computation, not the storage.

---

## Schema Additions (`fst-meta.db`)

### `UserOpps` Table

Caches the computed Opps for each registered user. Recomputed after each scrape pass.

```sql
CREATE TABLE UserOpps (
    UserId          TEXT    NOT NULL,  -- the registered user's AccountId
    OppAccountId    TEXT    NOT NULL,  -- the rival's AccountId
    Appearances     INTEGER NOT NULL,  -- number of song/instrument neighborhoods shared
    AvgRankDelta    REAL    NOT NULL,  -- average |rank difference| across appearances
    AvgSignedDelta  REAL    NOT NULL,  -- average (theirRank - userRank): negative = ahead, positive = behind
    AheadCount      INTEGER NOT NULL,  -- entries where Opp is ranked above user
    BehindCount     INTEGER NOT NULL,  -- entries where Opp is ranked below user
    InstrumentCount INTEGER NOT NULL,  -- number of distinct instruments they overlap on
    SongCount       INTEGER NOT NULL,  -- number of distinct songs they overlap on
    ComputedAt      TEXT    NOT NULL,  -- ISO 8601 timestamp of last computation
    PRIMARY KEY (UserId, OppAccountId)
);

CREATE INDEX IX_UserOpps_User ON UserOpps (UserId, Appearances DESC);
```

### `OppSongSamples` Table

Stores the closest song/instrument matchups for each Opp. Up to K (default 10) rows per (UserId, OppAccountId) pair.

```sql
CREATE TABLE OppSongSamples (
    UserId          TEXT    NOT NULL,
    OppAccountId    TEXT    NOT NULL,
    SongId          TEXT    NOT NULL,
    Instrument      TEXT    NOT NULL,  -- e.g. "Solo_Guitar"
    UserRank        INTEGER NOT NULL,
    OppRank         INTEGER NOT NULL,
    RankDelta       INTEGER NOT NULL,  -- oppRank - userRank (signed)
    UserScore       INTEGER,
    OppScore        INTEGER,
    PRIMARY KEY (UserId, OppAccountId, SongId, Instrument),
    FOREIGN KEY (UserId, OppAccountId) REFERENCES UserOpps(UserId, OppAccountId)
);

CREATE INDEX IX_OppSamples_User ON OppSongSamples (UserId, OppAccountId, ABS(RankDelta));
```

**Lifecycle:** On recompute, `DELETE FROM OppSongSamples WHERE UserId = @userId`, then bulk INSERT the top-K closest songs per Opp alongside the `UserOpps` rows (same transaction).

**Lifecycle (UserOpps):**
- On recompute: `DELETE FROM UserOpps WHERE UserId = @userId`, then bulk INSERT the new top-N Opps.
- Alternatively, UPSERT and prune stale entries — but full replace is simpler and the table is tiny.

### `OppEvents` Table (Future — after per-account DB work)

Tracks when a user passes one of their Opps or gets passed. Requires comparing leaderboard state between scrape passes.

```sql
CREATE TABLE OppEvents (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId          TEXT    NOT NULL,  -- the registered user
    OppAccountId    TEXT    NOT NULL,  -- the rival
    SongId          TEXT    NOT NULL,
    Instrument      TEXT    NOT NULL,  -- e.g. "Solo_Guitar"
    EventType       TEXT    NOT NULL,  -- 'passed' or 'passed_by'
    UserOldRank     INTEGER,
    UserNewRank     INTEGER,
    OppOldRank      INTEGER,
    OppNewRank      INTEGER,
    UserScore       INTEGER,
    OppScore        INTEGER,
    DetectedAt      TEXT    NOT NULL   -- ISO 8601
);

CREATE INDEX IX_OppEvents_User ON OppEvents (UserId, DetectedAt DESC);
CREATE INDEX IX_OppEvents_Pair ON OppEvents (UserId, OppAccountId, DetectedAt DESC);
```

**Event detection** (deferred — requires previous-state tracking):

After each scrape pass UPSERT commit for a registered user's song/instrument:
1. Load the user's previous rank for this song/instrument (from memory or from the pre-UPSERT state).
2. Load each of the user's Opps previous and new ranks for the same song/instrument.
3. If the user was ranked _below_ an Opp last pass and is now ranked _above_ → `'passed'` event.
4. If the user was ranked _above_ an Opp and is now ranked _below_ → `'passed_by'` event.

This is the same change-detection pattern already used for `ScoreHistory` (registered users only, handful of indexed lookups), just extended to also check Opp accounts.

---

## Data Flow

### Computation Trigger

Opps are recomputed **after each scrape pass completes**, as part of the post-pass work (alongside name resolution and personal DB rebuilds). The order would be:

```
Post-pass sequence:
  1. Resolve new account display names        (existing)
  2. Recompute Opps for registered users      (new)
  3. Rebuild personal DBs for changed users   (existing — now includes Opps data)
```

Alternatively, Opps can be computed **on-demand** via an API call, with the cached result stored in `UserOpps`.

### Computation Cost

For one registered user:

| Step | Queries | Rows read | Cost |
|---|---|---|---|
| Get user's entries across 6 instruments | 6 | ~1,000–3,000 | ~6 indexed queries, fast |
| Fetch neighborhoods (±50 per entry) | ~1,000–3,000 | ~100K–300K | Indexed on (SongId, Rank), ~ms each |
| In-memory aggregation | 0 | — | Dictionary ops, negligible |
| Write top-N Opps + samples | 2 DELETEs + N + N×K INSERTs | ~300 | Tiny write to meta DB |

**Total: ~2–5 seconds per user.** For a handful of registered users, this is negligible as post-pass work.

If the registered user count grows significantly, computation can be parallelized across users (each user's queries hit the same read-only instrument DBs under WAL mode — no contention).

### Interaction with Existing Architecture

- **No changes to instrument DB schema.** Opps computation reads `LeaderboardEntries` via existing indexes.
- **No changes to the scrape loop.** Opps runs as post-pass work, after all UPSERTs are committed.
- **Small addition to meta DB schema.** Two new tables (`UserOpps`, `OppEvents`), same patterns as `ScoreHistory`.
- **Small addition to personal DB.** The personal DB shipped to mobile devices could include the user's Opps list, so the mobile app can display them without additional API calls.

---

## API Endpoints

### New Endpoints

| Method | Path | Classification | Description |
|---|---|---|---|
| `GET` | `/api/player/{accountId}/opps` | Protected | Get the user's computed Opps list (from cache) |
| `POST` | `/api/player/{accountId}/opps/recompute` | Protected | Force recomputation of Opps (on-demand) |
| `GET` | `/api/player/{accountId}/opps/events` | Protected | Get pass/passed-by events (future) |

### Response Shape: `GET /api/player/{accountId}/opps`

```json
{
  "accountId": "abc123",
  "displayName": "PlayerOne",
  "oppsComputedAt": "2026-02-14T12:00:00Z",
  "opps": [
    {
      "accountId": "def456",
      "displayName": "NearbyRival",
      "appearances": 187,
      "avgRankDelta": 12.4,
      "avgSignedDelta": -3.2,
      "aheadCount": 112,
      "behindCount": 75,
      "instrumentCount": 5,
      "songCount": 142,
      "instruments": ["Solo_Guitar", "Solo_Bass", "Solo_Drums", "Solo_Vocals", "Solo_PeripheralGuitar"],
      "closestSongs": [
        { "songId": "sid_bohrap", "instrument": "Solo_Guitar", "userRank": 410, "oppRank": 412, "rankDelta": 2, "userScore": 985200, "oppScore": 985000 },
        { "songId": "sid_freebird", "instrument": "Solo_Guitar", "userRank": 305, "oppRank": 302, "rankDelta": -3, "userScore": 990100, "oppScore": 990400 },
        { "songId": "sid_ttfaf", "instrument": "Solo_Drums", "userRank": 88, "oppRank": 84, "rankDelta": -4, "userScore": 997000, "oppScore": 997500 }
      ]
    },
    {
      "accountId": "ghi789",
      "displayName": "AnotherRival",
      "appearances": 134,
      "avgRankDelta": 18.7,
      "avgSignedDelta": 8.1,
      "aheadCount": 45,
      "behindCount": 89,
      "instrumentCount": 3,
      "songCount": 120,
      "instruments": ["Solo_Guitar", "Solo_Drums", "Solo_Vocals"],
      "closestSongs": [
        { "songId": "sid_paint", "instrument": "Solo_Vocals", "userRank": 150, "oppRank": 151, "rankDelta": 1, "userScore": 970000, "oppScore": 969800 },
        { "songId": "sid_dream", "instrument": "Solo_Drums", "userRank": 220, "oppRank": 218, "rankDelta": -2, "userScore": 960500, "oppScore": 961000 }
      ]
    }
  ]
}
```

### Response Shape: `GET /api/player/{accountId}/opps/events` (Future)

```json
{
  "accountId": "abc123",
  "events": [
    {
      "oppAccountId": "def456",
      "oppDisplayName": "NearbyRival",
      "songId": "sid_song123",
      "instrument": "Solo_Guitar",
      "eventType": "passed",
      "userNewRank": 412,
      "oppNewRank": 413,
      "userScore": 985000,
      "oppScore": 984500,
      "detectedAt": "2026-02-14T08:15:00Z"
    }
  ]
}
```

---

## Personal DB Integration

The personal DB (shipped to mobile devices) currently has Songs + Scores. To include Opps:

### Option A: Embed Opps in the Personal DB

Add an `Opps` table to the personal DB schema:

```sql
CREATE TABLE Opps (
    OppAccountId    TEXT    PRIMARY KEY,
    DisplayName     TEXT,
    Appearances     INTEGER NOT NULL,
    AvgRankDelta    REAL    NOT NULL,
    AvgSignedDelta  REAL    NOT NULL,  -- negative = ahead of user, positive = behind
    AheadCount      INTEGER NOT NULL,
    BehindCount     INTEGER NOT NULL,
    InstrumentCount INTEGER NOT NULL,
    SongCount       INTEGER NOT NULL,
    ComputedAt      TEXT    NOT NULL
);

CREATE TABLE OppSongSamples (
    OppAccountId    TEXT    NOT NULL,
    SongId          TEXT    NOT NULL,
    Instrument      TEXT    NOT NULL,
    UserRank        INTEGER NOT NULL,
    OppRank         INTEGER NOT NULL,
    RankDelta       INTEGER NOT NULL,
    UserScore       INTEGER,
    OppScore        INTEGER,
    PRIMARY KEY (OppAccountId, SongId, Instrument),
    FOREIGN KEY (OppAccountId) REFERENCES Opps(OppAccountId)
);
```

`PersonalDbBuilder.Build()` would populate `Opps` and `OppSongSamples` from `UserOpps` / `OppSongSamples` in the meta DB. The mobile app gets the full Opps list *and* per-rival closest songs for free on sync — no additional API calls.

### Option B: API-Only

Don't embed — the mobile app calls `GET /api/player/{accountId}/opps` when it needs the list. Simpler personal DB, but requires connectivity.

**Recommendation:** Option A. The personal DB is already a self-contained snapshot. Adding a 25-row `Opps` table costs ~2 KB and lets the mobile app show Opps offline.

---

## Mobile App UX Ideas (Brainstorming)

These are not part of the backend design, but useful to think about for API/data requirements:

- **Opps leaderboard**: A compact "your rivals" list showing each Opp's name, how many songs you share, and whether you're generally above or below them.
- **Per-song Opp comparison**: When viewing a song, highlight which Opps also have scores and show the rank gap.
- **Opp feed / timeline**: "You passed NearbyRival on 'Bohemian Rhapsody' (Guitar)!" — driven by `OppEvents`.
- **Opp profile**: Tap an Opp to see a head-to-head breakdown: songs where you're ahead, songs where they're ahead, instruments where you're closest.

---

## Implementation Order (Proposed)

1. **Schema**: Add `UserOpps` table to `MetaDatabase.EnsureSchema()`.
2. **Computation**: `OppsCalculator` class — reads instrument DBs, aggregates neighborhoods, writes to `UserOpps`.
3. **Post-pass hook**: Call `OppsCalculator` after name resolution in `ScraperWorker`.
4. **API endpoints**: `GET /api/player/{accountId}/opps` and `POST /api/player/{accountId}/opps/recompute`.
5. **Personal DB**: Add `Opps` table to `PersonalDbBuilder` schema and populate during build.
6. **OppEvents schema + detection** (future, after per-account state tracking is in place).
7. **Mobile app UI** (future).

---

## Open Questions

- [ ] **Neighborhood size**: ±50 seems safe. ±30 would be faster but might miss relevant rivals on songs where rankings are sparse. Should this be configurable?
- [ ] **Top-N count**: How many Opps to keep per user? 25 seems reasonable for display. Store more (e.g., 50) in the DB so the mobile app can paginate or show "more rivals"?
- [ ] **Recomputation frequency**: Every scrape pass (4 hours) is cheap enough. Or only recompute daily / on-demand to reduce post-pass time?
- [ ] **Instrument weighting**: Should all instruments count equally? A user who plays mostly Guitar might want Guitar Opps weighted higher. Or let the raw frequency handle it naturally?
- [ ] **Minimum appearances threshold**: Should an Opp need to appear in at least N neighborhoods (e.g., 5) to qualify? Prevents one-song coincidences from cluttering the list.
- [ ] **OppEvents scope**: Detecting pass/passed-by requires knowing the previous rank state. Re-use the existing `ScoreHistory` data (which already tracks old/new rank for registered users), or maintain a separate "last known Opp state" cache? The `ScoreHistory` approach is cheaper but only covers the registered user's own rank changes, not the Opp's.
- [ ] **Cross-instrument Opps vs. per-instrument Opps**: The current design produces a single blended Opps list. Should the API also expose per-instrument Opps (e.g., "your Guitar Opps")? Easy to derive from the stored data by filtering on `Instruments`.
