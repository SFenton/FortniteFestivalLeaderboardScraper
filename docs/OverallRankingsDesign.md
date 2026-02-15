# Overall Rankings Feature Design

## Overview

After each scrape pass, compute **aggregate per-instrument rankings** and a **cross-instrument weighted rank** for every account on the leaderboard. This answers "how good is this player overall at Guitar?" and "how good is this player across all instruments?" — not just on one song, but across the entire catalog.

---

## The Problem

Per-song leaderboards tell you how a player did on one song. But players (and their Opps) want to know:
- "What's my overall rank on Guitar?"
- "Am I a top 5% player overall, or just on a few songs?"
- "How does my skill compare when accounting for breadth?"

This requires aggregating ~2,000 per-song positions into a single composite metric — and doing it fairly when different players have played different subsets of songs.

---

## Available Data Per Entry

Each row in `LeaderboardEntries` (per instrument DB) gives us:

| Field | Description | Normalization |
|---|---|---|
| `Rank` | Ordinal position (1 = best) | Not directly comparable across songs (different leaderboard sizes) |
| `Score` | Raw score | Not comparable across songs (different max scores) |
| `Percentile` | Fraction from API | Unreliable — often 0 or missing. **Do not use.** |
| `PointsEarned` | API-provided points | Unclear formula, but consistent within a song |
| `Accuracy` | Percent hit × 10,000 | Comparable across songs |
| `IsFullCombo` | Boolean | Comparable |
| `Stars` | 1–6 star rating | Comparable |

**Rank is the foundation.** It's always present, always an integer, always reliable. To make it comparable across songs with different leaderboard sizes, we derive a **Normalized Rank** per song:

```
NormalizedRank = Rank / EntryCount    (for that song/instrument)
```

Where `EntryCount = COUNT(*)` from the instrument DB for that song. This gives a value between ~0 and 1:
- Rank 1 of 50,000 → 0.00002 (elite)
- Rank 100 of 50,000 → 0.002 (excellent)
- Rank 100 of 500 → 0.20 (mediocre)
- Not on the leaderboard → treated as 1.0 (dead last)

This is effectively a locally-computed percentile derived entirely from our own data — no dependency on the API's `Percentile` field.

---

## Metrics

### 1. Skill Rating (Per-Instrument)

**Average Normalized Rank across played songs only.**

```
SkillRating = AVG(Rank / EntryCount) across all songs where the account has an entry
```

- Pure skill metric — measures how well you do on songs you've played.
- Not affected by how many songs you've played.
- Lower is better (0.01 = you're consistently in the top 1%).
- Derived entirely from `Rank` and `EntryCount` — no dependency on the API `Percentile` field.

**Handles the "other users haven't played songs I have" problem naturally.** If you played a niche song that only 500 people played, your Normalized Rank on that song accounts for the small field. If your Opp didn't play it, it simply doesn't factor into *their* average — which is fair, because there's no data to judge them on.

**Weakness:** A player who cherry-picks 10 easy songs and aces them looks better than someone who plays 2,000 songs and is consistently excellent. Pure skill, no breadth signal.

### 2. Overall Rating (Per-Instrument)

**Average Normalized Rank with unplayed songs penalized.**

```
OverallRating = (SUM(Rank / EntryCount for played songs) + (UnplayedCount × 1.0)) / TotalChartedSongs
```

Where:
- `TotalChartedSongs` = number of songs charted for this instrument (known from the song catalog)
- `UnplayedCount` = `TotalChartedSongs - SongsPlayed`
- `AssumedPercentile` = penalty for unplayed songs (in Normalized Rank terms)

**What should `AssumedPercentile` be?**

| Value | Meaning | Effect |
|---|---|---|
| `1.0` | Assume dead last (Rank = EntryCount) | Heavy breadth incentive — you must play everything |
| `0.5` | Assume median | Moderate penalty — doesn't assume you'd be terrible |
| `0.75` | Assume bottom quartile | Middle ground |
| Population median for that song | Realistic baseline | Fairest, but more complex to compute |

**Recommendation: `1.0` (dead last) for v1.** It's simple, deterministic, and provides a clear incentive: "play more songs to improve your Overall Rating." Players who avoid hard songs are penalized. This matches Rock Band Rivals' approach where unplayed songs counted as zero.

If `1.0` feels too harsh in practice, we can soften it later without schema changes — it's just a parameter in the computation.

### 3. Weighted Rating (Per-Instrument)

**Weighted average Normalized Rank, where more competitive songs (larger leaderboards) carry more weight and songs with fewer entries are dampened.**

The problem: a song with only 200 entries gives coarse Normalized Ranks (rank 1 = 0.005, rank 2 = 0.01 — big jumps). If a player does poorly on a niche song, it can disproportionately crater their average. Weighting by leaderboard size fixes this — a bad score on a 200-entry song barely registers, while a bad score on a 40,000-entry song matters.

```
WeightedRating = SUM((Rank / EntryCount) × Weight) / SUM(Weight)
```

Where `Weight` for each song could be:

| Weight Function | Rationale | Notes |
|---|---|---|
| `log₂(TotalEntries)` | Logarithmic — a 50K-entry song counts ~3× more than a 500-entry song, not 100×. A 200-entry niche song barely moves the needle. | Recommended — dampens low-entry outliers without ignoring them |
| `TotalEntries` | Linear — raw entry count | Too extreme; one viral song overwhelms everything |
| `1` (uniform) | Same as Skill Rating | No weighting |
| `sqrt(TotalEntries)` | Square root — moderate scaling | Good alternative to log |

**Recommendation: `log₂(TotalEntries)` for played songs, with unplayed songs included at `AssumedPercentile × log₂(MedianEntries)`.**

This means:
- Your score on a highly competitive song matters more than on a niche song
- Unplayed songs still penalize you, but at a moderate weight (you're not penalized at the weight of the most popular song)

**Weight source:** We need `TotalEntries` per song/instrument. This is derivable from the existing data: `SELECT COUNT(*) FROM LeaderboardEntries WHERE SongId = @songId`. Alternatively, `TotalPages` from `GlobalLeaderboardResult` gives `TotalPages × 100`. We can precompute these into a lightweight lookup table.

### 4. Coverage (Per-Instrument)

Not a ranking metric, but useful context displayed alongside rankings:

```
Coverage = SongsPlayed / TotalChartedSongs
```

A player with Skill Rating = 0.02 and Coverage = 95% is more impressive than one with Skill Rating = 0.01 and Coverage = 5%.

### 5. Cross-Instrument Composite (Weighted Rank)

A single number combining all instruments. Options:

| Approach | Formula | Notes |
|---|---|---|
| **Average of per-instrument SkillRatings** | `AVG(SkillRating across instruments)` | Simple but weights all instruments equally regardless of how much the player plays each |
| **Weighted by songs played per instrument** | `SUM(InstrumentSkillRating × InstrumentSongsPlayed) / SUM(SongsPlayed)` | Heavier instruments (where player has more entries) count more |
| **Best N instruments** | Average of top 3 or 4 instruments | Avoids penalizing players who don't play all 6 instruments |

**Recommendation: Weighted by songs played.** This naturally reflects where the player spends their time. A Guitar main's composite is dominated by their Guitar rating. An all-rounder's composite is balanced.

Instruments with zero songs played are excluded (not penalized), since many players legitimately don't own pro instruments.

---

## Should We Compute for Every Account?

**Yes.** Here's why:

| Approach | Pros | Cons |
|---|---|---|
| **Registered users only** | Tiny computation | Can't produce ordinal rank ("you are #1,234") without knowing everyone else's score. At best you get percentile brackets. |
| **Every account** | True ordinal rankings. Powers player lookup API. Enables Opps comparisons. | Larger computation and storage. |

### Cost Analysis

| Step | Per instrument | Total (6 instruments) |
|---|---|---|
| GROUP BY query (full scan) | ~10–30 seconds on 10M rows | ~1–3 minutes |
| Result rows (unique accounts) | ~500K | ~500K per instrument DB |
| Storage (~100 bytes/row) | ~50 MB | ~300 MB across 6 instrument DBs |
| ROW_NUMBER() assignment | Negligible (sort in SQLite) | — |

**~3 minutes of compute + ~300 MB of storage, once every 4 hours.** This is well within acceptable limits for post-pass work. The instrument DBs are already 1.5–3 GB each, so a 50 MB summary table adds ~3%.

### Where to Store?

**Per-instrument summary table lives in each instrument DB.** This keeps the data close to its source, avoids cross-DB writes, and supports per-instrument API queries directly.

**Cross-instrument composite lives in `fst-meta.db`.** Computed by reading each instrument's summary table and aggregating.

---

## Schema

### Per-Instrument DB: `AccountRankings` Table

Added to each `fst-Solo_*.db` alongside `LeaderboardEntries`.

```sql
CREATE TABLE AccountRankings (
    AccountId         TEXT    PRIMARY KEY,
    SongsPlayed       INTEGER NOT NULL,
    TotalChartedSongs INTEGER NOT NULL,   -- denominator for coverage/overall
    Coverage          REAL    NOT NULL,   -- SongsPlayed / TotalChartedSongs

    -- Skill Rating (played songs only)
    SkillRating       REAL    NOT NULL,   -- AVG(Rank/EntryCount) across played songs (lower = better)
    SkillRank         INTEGER NOT NULL UNIQUE,  -- ordinal rank by SkillRating (1 = best, no ties)

    -- Overall Rating (with unplayed penalty)
    OverallRating     REAL    NOT NULL,   -- avg Normalized Rank including penalty for unplayed songs
    OverallRank       INTEGER NOT NULL UNIQUE,  -- ordinal rank by OverallRating (no ties)

    -- Weighted Rating (log-weighted by leaderboard size)
    WeightedRating    REAL    NOT NULL,   -- weighted avg Normalized Rank (lower = better)
    WeightedRank      INTEGER NOT NULL UNIQUE,  -- ordinal rank by WeightedRating (no ties)

    -- Supporting stats
    TotalScore        INTEGER NOT NULL,   -- sum of Score across all played songs
    TotalPoints       INTEGER NOT NULL,   -- sum of PointsEarned
    AvgAccuracy       REAL    NOT NULL,   -- average Accuracy across played songs
    FullComboCount    INTEGER NOT NULL,   -- number of full combos
    AvgStars          REAL    NOT NULL,   -- average star rating
    BestRank          INTEGER NOT NULL,   -- best single-song rank (lowest Rank value)
    AvgRank           REAL    NOT NULL,   -- average raw rank across played songs

    ComputedAt        TEXT    NOT NULL    -- ISO 8601
);

CREATE INDEX IX_AccountRankings_Skill    ON AccountRankings (SkillRank);
CREATE INDEX IX_AccountRankings_Overall  ON AccountRankings (OverallRank);
CREATE INDEX IX_AccountRankings_Weighted ON AccountRankings (WeightedRank);
```

**Lifecycle:** After each scrape pass, recompute and replace:
```sql
BEGIN;
DELETE FROM AccountRankings;
INSERT INTO AccountRankings (...) SELECT ... FROM LeaderboardEntries GROUP BY AccountId;
COMMIT;
```

### Song Stats Helper: `SongStats` Table

Precomputed per-song entry counts, used as weights for the Weighted Rating.

```sql
CREATE TABLE SongStats (
    SongId       TEXT    PRIMARY KEY,
    EntryCount   INTEGER NOT NULL,   -- COUNT(*) for this song on this instrument
    LogWeight    REAL    NOT NULL,   -- log2(EntryCount), precomputed
    ComputedAt   TEXT    NOT NULL
);
```

Refreshed alongside `AccountRankings`. The computation reads this first to get weights, then aggregates.

### Meta DB: `CompositeRankings` Table

Cross-instrument composite, stored in `fst-meta.db`.

```sql
CREATE TABLE CompositeRankings (
    AccountId            TEXT    PRIMARY KEY,
    InstrumentsPlayed    INTEGER NOT NULL,   -- how many instruments have entries (0–6+)
    TotalSongsPlayed     INTEGER NOT NULL,   -- sum across all instruments

    -- Weighted composite (weighted by songs played per instrument)
    CompositeRating      REAL    NOT NULL,   -- weighted avg of per-instrument SkillRatings
    CompositeRank        INTEGER NOT NULL UNIQUE,  -- ordinal rank (no ties)

    -- Supporting detail (for display without re-querying instrument DBs)
    GuitarSkillRating    REAL,    -- NULL if no entries on this instrument
    BassSkillRating      REAL,
    DrumsSkillRating     REAL,
    VocalsSkillRating    REAL,
    ProGuitarSkillRating REAL,
    ProBassSkillRating   REAL,

    GuitarSkillRank      INTEGER,
    BassSkillRank        INTEGER,
    DrumsSkillRank       INTEGER,
    VocalsSkillRank      INTEGER,
    ProGuitarSkillRank   INTEGER,
    ProBassSkillRank     INTEGER,

    ComputedAt           TEXT    NOT NULL
);

CREATE INDEX IX_CompositeRankings_Rank ON CompositeRankings (CompositeRank);
```

**Lifecycle:** Computed after all per-instrument `AccountRankings` are refreshed. Reads each instrument DB's `AccountRankings` table, aggregates per account, assigns composite rank.

---

## Computation Flow

### Post-Pass Sequence (Updated)

```
Post-pass sequence:
  1. Resolve new account display names              (existing)
  2. Compute per-instrument AccountRankings         (new — per instrument DB)
  3. Compute cross-instrument CompositeRankings     (new — meta DB)
  4. Recompute Opps for registered users            (existing)
  5. Rebuild personal DBs for changed users         (existing — now includes rankings)
```

Rankings are computed before Opps because Opps could optionally reference overall rank comparisons.

### Per-Instrument Computation Detail

For each instrument DB:

```
1. Compute SongStats:
   DELETE FROM SongStats;
   INSERT INTO SongStats
     SELECT SongId, COUNT(*), LOG2(COUNT(*))
     FROM LeaderboardEntries
     GROUP BY SongId;

2. Compute per-account aggregates:
   For each account (GROUP BY AccountId on LeaderboardEntries):
     - SongsPlayed      = COUNT(*)
     - SkillRating      = AVG(Rank / EntryCount)  — via JOIN with SongStats
     - TotalScore       = SUM(Score)
     - TotalPoints      = SUM(PointsEarned)
     - AvgAccuracy      = AVG(Accuracy)
     - FullComboCount   = SUM(IsFullCombo)
     - AvgStars         = AVG(Stars)
     - BestRank         = MIN(Rank)               — best single-song rank
     - AvgRank          = AVG(Rank)               — average raw rank (for display)

   JOIN with SongStats to compute:
     - WeightedRating   = SUM((Rank / EntryCount) × LogWeight) / SUM(LogWeight)

   OverallRating requires TotalChartedSongs (from song catalog):
     - TotalChartedSongs = count of songs with difficulty > 0 for this instrument
     - UnplayedCount     = TotalChartedSongs - SongsPlayed
     - OverallRating     = (SUM(Rank / EntryCount) + UnplayedCount × 1.0) / TotalChartedSongs

3. Assign ordinal ranks (no ties — every account gets a unique rank):
   SkillRank    = ROW_NUMBER() ordered by SkillRating ASC, then tiebreakers
   OverallRank  = ROW_NUMBER() ordered by OverallRating ASC, then tiebreakers
   WeightedRank = ROW_NUMBER() ordered by WeightedRating ASC, then tiebreakers

   Tiebreaker order (when ratings are equal):
     1. SongsPlayed DESC       — more songs played wins
     2. TotalScore DESC        — higher total score wins
     3. FullComboCount DESC    — more FCs wins
     4. AccountId ASC          — deterministic final fallback (alphabetical)

4. Write to AccountRankings (full replace in one transaction)
```

**SQL sketch (single query approach):**

```sql
WITH Aggregated AS (
    SELECT
        le.AccountId,
        COUNT(*)                                          AS SongsPlayed,
        @totalCharted                                     AS TotalChartedSongs,
        CAST(COUNT(*) AS REAL) / @totalCharted            AS Coverage,
        AVG(CAST(le.Rank AS REAL) / ss.EntryCount)        AS SkillRating,
        (SUM(CAST(le.Rank AS REAL) / ss.EntryCount) + (@totalCharted - COUNT(*)) * 1.0) / @totalCharted
                                                          AS OverallRating,
        SUM((CAST(le.Rank AS REAL) / ss.EntryCount) * ss.LogWeight) / SUM(ss.LogWeight)
                                                          AS WeightedRating,
        SUM(le.Score)                                     AS TotalScore,
        SUM(le.PointsEarned)                              AS TotalPoints,
        AVG(le.Accuracy)                                  AS AvgAccuracy,
        SUM(le.IsFullCombo)                               AS FullComboCount,
        AVG(le.Stars)                                     AS AvgStars,
        MIN(le.Rank)                                      AS BestRank,
        AVG(le.Rank)                                      AS AvgRank
    FROM LeaderboardEntries le
    JOIN SongStats ss ON ss.SongId = le.SongId
    GROUP BY le.AccountId
),
Ranked AS (
    SELECT *,
        ROW_NUMBER() OVER (ORDER BY SkillRating    ASC, SongsPlayed DESC, TotalScore DESC, FullComboCount DESC, AccountId ASC) AS SkillRank,
        ROW_NUMBER() OVER (ORDER BY OverallRating  ASC, SongsPlayed DESC, TotalScore DESC, FullComboCount DESC, AccountId ASC) AS OverallRank,
        ROW_NUMBER() OVER (ORDER BY WeightedRating ASC, SongsPlayed DESC, TotalScore DESC, FullComboCount DESC, AccountId ASC) AS WeightedRank
    FROM Aggregated
)
INSERT INTO AccountRankings
SELECT AccountId, SongsPlayed, TotalChartedSongs, Coverage,
       SkillRating, SkillRank, OverallRating, OverallRank,
       WeightedRating, WeightedRank,
       TotalScore, TotalPoints, AvgAccuracy, FullComboCount, AvgStars,
       BestRank, AvgRank,
       @now
FROM Ranked;
```

### Cross-Instrument Computation

After all instrument DBs are refreshed:

```
1. For each instrument DB, read all AccountRankings rows
2. Group by AccountId across instruments
3. CompositeRating = SUM(SkillRating × SongsPlayed) / SUM(SongsPlayed)
   (weighted by how much the player plays each instrument)
4. Assign CompositeRank = ROW_NUMBER() ordered by CompositeRating ASC,
     TotalSongsPlayed DESC, InstrumentsPlayed DESC, AccountId ASC  (no ties)
5. Write to fst-meta.db → CompositeRankings
```

This can use ATTACH to read instrument DBs from the meta DB connection, or be done in application code.

---

## Handling Edge Cases

### Unplayed Songs — "Other users haven't played songs I have"

This is handled naturally by the metrics:

- **Skill Rating**: Only averages what each player has played. If you played a song your Opp didn't, it doesn't affect their rating and doesn't directly affect yours beyond adding to your average.
- **Overall Rating**: Unplayed songs hurt everyone equally (penalty of 1.0 = dead last Normalized Rank). If you played a song and got a good Normalized Rank, you're better off than someone who didn't (they got the 1.0 penalty for it).
- **Weighted Rating**: Same principle as Overall, but weighted. Playing a popular song well really helps; not playing it hurts at that song's weight.

**Example:**
- Song X has 40,000 entries. Player A played it (rank 2,000 → Normalized Rank 0.05). Player B didn't.
- For Overall Rating: Player A gets 0.05 in the average. Player B gets 1.0 (the penalty).
- For Weighted Rating: Player A gets 0.05 × log₂(40000) ≈ 0.05 × 15.3 = 0.77. Player B gets 1.0 × log₂(40000) = 15.3. Player A is way better off.

### Very Low Song Counts

A player with 1 song played who got Normalized Rank 0.001 would have a misleadingly elite Skill Rating. Mitigation options:

| Approach | Description |
|---|---|
| **Minimum threshold** | Don't include in rankings below N songs (e.g., 10). Show "Unranked" instead. |
| **Bayesian prior** | Pull toward population average with strength inversely proportional to songs played. Like IMDB's weighted rating formula. |
| **Display only** | Compute the rating but flag low-coverage accounts in the API response (CoverageWarning). Let the client decide. |

**Recommendation:** Minimum threshold of 10 songs for Skill Rating rankings. Below that, the account gets a `SkillRank` of NULL. Overall and Weighted ratings are inherently self-correcting (1 out of 2000 songs = Coverage of 0.05% → terrible Overall Rating).

### Songs With Very Few Entries

A song with only 50 entries gives coarse Normalized Ranks. Rank 1 of 50 = 0.02, but rank 1 of 50,000 = 0.00002. The Weighted Rating addresses this — low-entry songs get lower weight via `log₂(EntryCount)`.

### Rank Beyond EntryCount

Normally `Rank ≤ EntryCount` for any song, so `NormalizedRank ≤ 1.0`. If stale data causes `Rank > EntryCount` (e.g., EntryCount changed between scrapes), clamp to `MIN(Rank / EntryCount, 1.0)`.

---

## API Endpoints

### New Endpoints

| Method | Path | Classification | Description |
|---|---|---|---|
| `GET` | `/api/rankings/{instrument}` | Public | Overall rankings for one instrument (paginated) |
| `GET` | `/api/rankings/{instrument}/{accountId}` | Public | One account's ranking on one instrument |
| `GET` | `/api/rankings/composite` | Public | Cross-instrument composite rankings (paginated) |
| `GET` | `/api/rankings/composite/{accountId}` | Public | One account's composite ranking |

### Response Shape: `GET /api/rankings/{instrument}/{accountId}`

```json
{
  "accountId": "abc123",
  "displayName": "PlayerOne",
  "instrument": "Solo_Guitar",
  "skillRating": 0.032,
  "skillRank": 1234,
  "overallRating": 0.187,
  "overallRank": 892,
  "weightedRating": 0.041,
  "weightedRank": 1102,
  "songsPlayed": 1850,
  "totalChartedSongs": 2000,
  "coverage": 0.925,
  "totalScore": 18500000,
  "totalPoints": 245000,
  "avgAccuracy": 9823.5,
  "fullComboCount": 412,
  "avgStars": 5.7,
  "bestRank": 3,
  "avgRank": 245.8,
  "computedAt": "2026-02-14T12:00:00Z",
  "totalRankedAccounts": 487000
}
```

### Response Shape: `GET /api/rankings/composite/{accountId}`

```json
{
  "accountId": "abc123",
  "displayName": "PlayerOne",
  "compositeRating": 0.038,
  "compositeRank": 1456,
  "instrumentsPlayed": 5,
  "totalSongsPlayed": 8400,
  "instruments": {
    "Solo_Guitar":          { "skillRating": 0.032, "skillRank": 1234, "songsPlayed": 1850 },
    "Solo_Bass":            { "skillRating": 0.028, "skillRank": 987,  "songsPlayed": 1800 },
    "Solo_Drums":           { "skillRating": 0.045, "skillRank": 2100, "songsPlayed": 1700 },
    "Solo_Vocals":          { "skillRating": 0.051, "skillRank": 3200, "songsPlayed": 1600 },
    "Solo_PeripheralGuitar":{ "skillRating": 0.022, "skillRank": 450,  "songsPlayed": 1450 },
    "Solo_PeripheralBass":  null
  },
  "computedAt": "2026-02-14T12:00:00Z",
  "totalRankedAccounts": 520000
}
```

### Paginated Leaderboard: `GET /api/rankings/{instrument}?page=1&pageSize=50`

```json
{
  "instrument": "Solo_Guitar",
  "rankBy": "skill",
  "page": 1,
  "pageSize": 50,
  "totalAccounts": 487000,
  "entries": [
    { "rank": 1,  "accountId": "xyz", "displayName": "TopPlayer", "skillRating": 0.0003, "songsPlayed": 1990, "coverage": 0.995 },
    { "rank": 2,  "accountId": "abc", "displayName": "Runner Up", "skillRating": 0.0004, "songsPlayed": 1985, "coverage": 0.993 }
  ]
}
```

Query params:
- `rankBy` = `skill` | `overall` | `weighted` (default: `overall`) — which ranking to sort by
- `page`, `pageSize` — standard pagination

---

## Personal DB Integration

Add per-instrument rankings and composite rank to the personal DB:

```sql
CREATE TABLE MyRankings (
    Instrument        TEXT    PRIMARY KEY,  -- "Solo_Guitar", etc. + "Composite" for the cross-instrument rank
    SkillRating       REAL,
    SkillRank         INTEGER,
    OverallRating     REAL,
    OverallRank       INTEGER,
    WeightedRating    REAL,
    WeightedRank      INTEGER,
    SongsPlayed       INTEGER,
    TotalChartedSongs INTEGER,
    Coverage          REAL,
    TotalScore        INTEGER,
    TotalPoints       INTEGER,
    AvgAccuracy       REAL,
    FullComboCount    INTEGER,
    AvgStars          REAL,
    BestRank          INTEGER,
    AvgRank           REAL,
    TotalRankedAccounts INTEGER,
    ComputedAt        TEXT
);
```

One row per instrument + one "Composite" row. ~7 rows, negligible size.

---

## Performance Considerations

### Full Table Scan Mitigation

The GROUP BY over `LeaderboardEntries` is a full scan (~10M rows per instrument). Strategies to keep it fast:

1. **Run after scrape completes** (not during) — no write contention.
2. **WAL mode** — reads don't block the next scrape pass.
3. **Covering index** (optional, if the scan is too slow):
   ```sql
   CREATE INDEX IX_Rankings_Cover ON LeaderboardEntries (AccountId, Rank, Score, PointsEarned, Accuracy, IsFullCombo, Stars, SongId);
   ```
   This is a large index (~3 GB across all instruments) and may not be worth it if the scan is already fast enough. Benchmark first.
4. **Incremental computation** (future optimization): Track which songs changed in this scrape pass, recompute only affected accounts. Complex but O(changed) instead of O(all).

### Memory

The GROUP BY result set (~500K rows × ~100 bytes = ~50 MB) fits in memory. No streaming needed.

---

## Implementation Order (Proposed)

1. **`SongStats` table + computation** — per-instrument entry counts as weights.
2. **`AccountRankings` table + computation** — the big GROUP BY + RANK() query, per instrument.
3. **`CompositeRankings` table + computation** — cross-instrument aggregation in meta DB.
4. **Post-pass hook** — wire into `ScraperWorker` after name resolution, before Opps.
5. **API endpoints** — rankings lookup and paginated leaderboard.
6. **Personal DB** — `MyRankings` table in `PersonalDbBuilder`.
7. **Benchmarking** — measure actual computation time on production data, add covering index if needed.

---

## Open Questions

- [ ] **Assumed Normalized Rank for unplayed songs**: 1.0 (dead last) is proposed. Too harsh? Should this be configurable per deployment or hardcoded?
- [ ] **Minimum songs threshold for Skill Rank**: 10 proposed. Should other metrics have minimums too?
- [ ] **Weight function**: `log₂(EntryCount)` proposed. Should we benchmark `sqrt` as an alternative?
- [ ] **EntryCount staleness**: `SongStats.EntryCount` is refreshed each pass — should this be the current pass's count, or a rolling maximum (to avoid rank inflation when leaderboards shrink due to API quirks)?
- [ ] **Composite rank — instrument exclusion**: Should instruments with < N songs played be excluded from the composite? Currently they're excluded only if 0 songs played.
- [x] **Rank ties**: ~~Use `RANK()` (gaps after ties: 1,2,2,4) or `DENSE_RANK()` (no gaps: 1,2,2,3)?~~ **DECIDED** — Use `ROW_NUMBER()` — every account gets a unique rank with no ties. Tiebreakers: SongsPlayed DESC → TotalScore DESC → FullComboCount DESC → AccountId ASC (deterministic final fallback).
- [ ] **Historical tracking**: Should we keep a history of rankings over time (e.g., "your Overall Rank improved from #2,000 to #1,500 this week")? Could be a simple append table. Low priority but cool for the mobile app.
- [ ] **Display name resolution for top-N**: The paginated rankings endpoint returns display names. Should these come from `AccountNames` (best-effort, may have NULLs) or trigger on-demand resolution?
- [ ] **Re-computation scope**: Recompute all accounts every pass, or only accounts that had score changes? Full recompute is simpler and guarantees rank correctness (since other players' changes affect your rank too). Incremental is faster but complex.
