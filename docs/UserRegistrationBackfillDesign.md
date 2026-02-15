# User Registration & Score Backfill Design

## Overview

When a user registers (providing their Epic Games account ID), the service needs to do more than just start watching for score changes on future scrapes. It needs to:

1. **Immediately** begin building a personal DB — even if a scrape is in progress.
2. **Backfill missing scores** — the user may have scores too low to appear in the top 60,000 per-song all-time leaderboard. Use targeted account lookups to find these.
3. **Reconstruct score history** — walk backwards through seasonal leaderboards to build a timeline of when the user set each high score. One-time operation per user.

All of this must work **asynchronously** and **not block or interfere with** the ongoing scrape loop.

---

## Problem: Scores Not in the Global Leaderboard

The all-time leaderboard is capped at 600 pages × 100 = 60,000 entries per song/instrument. A registered user who scored below rank 60,000 won't appear in our scraped data at all. Worse, we can't distinguish "user never played this song" from "user played but ranked too low."

**Solution:** Use `GlobalLeaderboardScraper.LookupAccountAsync`, which calls the API with `teamAccountIds={accountId}`. This returns the user's specific entry — rank, score, accuracy, etc. — regardless of their position, in a single HTTP request.

---

## Problem: Stale Entries (Fell Out of Top 60K)

A subtler problem: a registered user **was** in the top 60,000 for a song when we scraped it, so we have their entry in the instrument DB. Then:

1. Other players push them out of the top 60K.
2. Optionally, they set a **new high score** — but it's still not enough to re-enter the top 60K.

Our DB entry is now **stale** — it has their old rank (which is now wrong) and potentially their old score (if they improved). A gap check that only queries songs with "no entry at all" would skip this entirely because an entry *does* exist.

**The fix:** The post-scrape check for registered users must cover both:
- Songs where the user has **no entry** (original gap check)
- Songs where the user has an entry but **was not seen in the current scrape pass** (stale entry)

### Detection: Who Was Seen in the Scrape?

`PersistResult` already receives the `registeredAccountIds` set and inspects `result.Entries` for each song. It currently checks if a registered user's entry *changed* — but it doesn't track whether a registered user's entry was simply *present* (with or without a score change).

**Solution:** Extend `PipelineAggregates` to collect a set of `(AccountId, SongId, Instrument)` tuples for every registered user entry seen during the pass:

```csharp
// In PipelineAggregates (new):
private readonly ConcurrentBag<(string AccountId, string SongId, string Instrument)>
    _seenRegisteredEntries = new();

public void AddSeenRegisteredEntries(
    IEnumerable<(string, string, string)> entries)
{
    foreach (var e in entries) _seenRegisteredEntries.Add(e);
}

public IReadOnlyCollection<(string AccountId, string SongId, string Instrument)>
    SeenRegisteredEntries => _seenRegisteredEntries;
```

In `PersistResult`, after the UPSERT, iterate the current `result.Entries` and emit a tuple for every registered user found:

```csharp
// In PersistResult (new, after post-UPSERT change detection):
var seenEntries = result.Entries
    .Where(e => registeredAccountIds.Contains(e.AccountId))
    .Select(e => (e.AccountId, result.SongId, result.Instrument));
// → aggregates.AddSeenRegisteredEntries(seenEntries);
```

After the scrape pass, converting `SeenRegisteredEntries` to a `HashSet<(string, string, string)>` gives O(1) lookup. For 10 registered users × 2000 songs × 6 instruments = up to 120K tuples — trivial in memory.

Then the post-scrape check becomes: for each registered user's instrument DB entries, if `(accountId, songId, instrument)` is NOT in the seen set → **re-query via `LookupAccountAsync`**.

---

## Registration Flow

### Step 1: Immediate (During or Outside Scrape)

When `POST /api/register` is called:

```
1. Store registration in fst-meta.db → RegisteredUsers          (existing)
2. Enqueue a BackfillRequest for the new user                   (new)
3. Build an initial personal DB from whatever data is already    (existing)
   in the instrument DBs for this account
4. Return 200 immediately                                        (existing)
```

The personal DB built in step 3 may be incomplete (the user may have scores we haven't scraped yet), but it gives the mobile app something to show immediately. It will be rebuilt after backfill completes.

### Step 2: Score Backfill (Async, After Scrape Idle)

A **BackfillWorker** (or a dedicated phase in `ScraperWorker`) picks up the backfill request when no scrape is in progress:

```
1. Wait until scrape is idle (not running)
2. Get access token
3. Load the song catalog (all charted songs × instruments)
4. For each song/instrument:
   a. Check if the user already has an entry in the instrument DB
   b. If not → call LookupAccountAsync for this user
   c. If the API returns an entry → UPSERT into the instrument DB
   d. If the API returns null → mark as "confirmed no score" (optional tracking)
5. After all song/instruments checked:
   a. Resolve the user's display name if not already cached
   b. Rebuild the user's personal DB
   c. Mark backfill complete for this user
```

### Step 3: Post-Scrape Refresh (Recurring)

After **every** scrape pass, for each registered user:

```
1. Load the set of all charted song/instrument pairs
2. Build the "seen set" from PipelineAggregates.SeenRegisteredEntries
3. For each charted pair, check TWO conditions:
   a. NO ENTRY:  user has no entry in the instrument DB
      → Call LookupAccountAsync
      → If entry found → UPSERT (they scored too low for top 60K, or just played it)
      → If null → skip (they haven't played it)
   b. STALE ENTRY:  user HAS an entry, but (accountId, songId, instrument)
      is NOT in the seen set (entry was not refreshed by this scrape pass)
      → Call LookupAccountAsync to get their CURRENT rank/score
      → UPSERT the fresh data (score may have improved, rank certainly changed)
      → If API returns null → DELETE the entry (they somehow lost their score?
         Very unlikely but handles edge cases like account bans or data resets.)
4. Rebuild personal DB if any entries were added or updated
```

This catches **three** scenarios the scrape alone misses:
- **Never in top 60K** — user played a song but never ranked high enough to appear in scraped pages.
- **Fell out of top 60K** — user was once in the scraped range but other players pushed them out. Our entry has a stale rank.
- **Fell out + improved** — user fell out of the scraped range AND set a new high score that's still below 60K. Our entry has both a stale rank and a stale score.

### Why Not Just Re-Query Everything?

We could `LookupAccountAsync` for ALL song/instruments for every registered user after every scrape. But that's ~12,000 requests per user per pass. The seen-set approach only re-queries the **delta**: songs where the user wasn't in the scraped pages. For an active player who's in the top 60K on most songs they've played, this delta is small — maybe a few hundred lookups instead of 12,000.

---

## Scrape Concurrency Safety

The backfill **must not run concurrently with a scrape pass**, because both would be making API requests with the same access token and writing to the same instrument DBs. Design:

### Option A: Phase-Based (Recommended)

The backfill runs as a **post-pass phase** in `ScraperWorker`, after all other post-pass work:

```
Post-pass sequence:
  1. Resolve new account display names
  2. Compute per-instrument AccountRankings
  3. Compute cross-instrument CompositeRankings
  4. Recompute Opps for registered users
  5. Backfill missing scores for registered users      ← new
  6. Rebuild personal DBs for changed users
```

**On registration during a scrape:** The backfill request is queued. When the current scrape pass finishes and reaches step 5, it picks up the queued backfill.

**On registration outside a scrape:** The scrape loop is sleeping. We have two sub-options:

- **Interrupt sleep** (preferred): The registration endpoint signals the `ScraperWorker` (via a `SemaphoreSlim` or `CancellationTokenSource`) to wake up from its sleep and run just the backfill phase without a full scrape.
- **Wait for next pass**: Simpler, but the user could wait up to 4 hours. Not ideal for registration UX.

### Option B: Separate BackfillWorker

A second `BackgroundService` that uses a `SemaphoreSlim` to coordinate with `ScraperWorker`. More complex but fully decoupled.

**Recommendation: Option A (phase-based with sleep interrupt).** Keeps all API work in one place, one token manager, one lifecycle. The sleep interrupt ensures prompt backfill on registration.

### Coordination Mechanism

```csharp
// In ScraperWorker or a shared service
private readonly Channel<BackfillRequest> _backfillQueue = Channel.CreateUnbounded<BackfillRequest>();

// Registration endpoint enqueues:
_backfillQueue.Writer.TryWrite(new BackfillRequest(accountId));

// ScraperWorker checks:
// - After each scrape pass, drain the queue and run backfills
// - During sleep, use WaitAsync with the sleep CTS so registration wakes it up
```

---

## Score Backfill: Cost Analysis

For one registered user, worst case (brand new, no scores in leaderboards yet):

| Step | Requests | Time (at DOP=4) |
|---|---|---|
| Check all songs × 6 instruments | ~2,000 × 6 = 12,000 | ~30 minutes |
| Check all songs × 6 instruments | ~2,000 × 6 = 12,000 | ~50 minutes at DOP=2 |

That's the **worst case** (no entries at all in the global leaderboard). In practice:

- Most active players will have entries in the global leaderboard for many songs/instruments. We only query missing ones.
- A typical player might have 1,000 songs × 4 instruments in the leaderboard and need 1,000 × 4 = 4,000 lookups. ~10 minutes at DOP=4.
- After the first backfill, the recurring post-scrape refresh only re-queries the delta: new songs added since last check, plus songs where the user's entry was not in the scraped pages (stale) — typically a small fraction of the total.

**Rate limiting concern:** These are single-page requests (lightweight for the API), but 12K requests is significant. Use a conservative DOP (2–4) with rate-limit backoff. The existing `AdaptiveConcurrencyLimiter` can be reused.

### Optimization: Skip Instruments the User Doesn't Play

If after the first backfill, a user has zero scores on ProBass across all songs, it's reasonable to stop querying ProBass for them. Track per-user instrument coverage:

```
If backfill found 0 entries for a user on an instrument → mark instrument as "likely unused"
On subsequent post-scrape checks, skip "likely unused" instruments
Re-check "likely unused" instruments every N passes (e.g., every 10th = every 40 hours)
```

---

## Score History Reconstruction

### What We Want

For each song/instrument where the user has a score, reconstruct the timeline:
- When did they first play this song?
- When did they improve their score?
- What was their score in each season?

### How the API Works

The all-time leaderboard URL: `alltime_{songId}_{instrument}/alltime/{accountId}`

The seasonal leaderboard URL (based on API structure): `alltime_{songId}_{instrument}/{seasonWindow}/{accountId}`

**Key insight from the `Season` field:** The all-time entry's `trackedStats.SEASON` tells us which season the user last set their high score. If their current high score was set in Season 7, there's no point querying Season 8+ — they didn't improve after S7.

### Seasonal Window Discovery

We need to know what seasonal windows exist. The API's events endpoint (`/api/v1/events/FNFestival/data/{accountId}?showPastEvents=true`) returns event definitions including window IDs. We should:

1. Call the events endpoint once at startup / on first history reconstruction
2. Parse the window IDs to discover all seasonal leaderboard windows
3. Cache the list of season windows (they only grow, never change)

If the window naming is predictable (e.g., `season_1`, `season_2`, ..., `season_N`), we can also enumerate them by convention and stop when we get 404s.

### Reconstruction Algorithm

For one registered user:

```
1. Get the user's all-time entries from instrument DBs (already have these after backfill)
2. For each entry where Season > 0:
   a. Get the season number S from the all-time entry
   b. This is the season where the current high score was set
   c. Query seasons 1, 2, ..., S for this song/instrument/account
      - Use LookupAccountAsync with the seasonal window instead of "alltime"
      - Each season's response gives us the best score AND endTime for that season
   d. The API returns `endTime` on each sessionHistory entry — an ISO 8601
      timestamp of when the session was played (e.g. "2024-04-24T09:55:59.467Z").
      This gives us the exact datetime the high score was set.
   e. Collect all season responses into a list of
      (score, accuracy, fc, stars, season, rank, percentile, endTime).
   f. Sort by endTime ascending.
   g. Walk through the sorted list, keeping only entries where the score
      strictly ascends (each score > previous kept score). This produces
      the progression timeline — only the moments the player improved:
      - First play: season N, score X, rank R, endTime T1
      - Improvement: season M, score Y (Y > X), rank R', endTime T2
      - Improvement: season S, score Z (Z > Y), rank R'', endTime T3
   h. Build ScoreHistory entries for each kept entry.
      Each entry is a **point-in-time snapshot** capturing the full state
      at the moment the score was set — even if rank degrades later:
      - OldScore / NewScore (score delta)
      - OldRank / NewRank (rank at the time — preserved as a historical snapshot)
      - Accuracy (percentage achieved on that play)
      - IsFullCombo (whether FC was achieved on that play)
      - Stars (star/difficulty rating achieved on that play)
      - Percentile (percentile ranking at the time — snapshot, not updated)
      - Season (the season in which the score was set)
      - ScoreAchievedAt (the endTime from the API — exact play timestamp)
3. Insert all ScoreHistory entries into fst-meta.db → ScoreHistory
4. Mark the user's history as "reconstructed" so we don't do it again

**Important:** Ranks and percentiles stored in ScoreHistory are **snapshots** —
they reflect the player's position at the time the score was recorded. They are
never retroactively updated when other players surpass the user. This gives
users an accurate timeline of their achievements ("I was rank 42 when I set
this score") rather than a constantly-shifting view.

**endTime source:** The V1 leaderboard API returns `endTime` on each
`sessionHistory` entry (see [FNLookup docs](https://github.com/FNLookup/data/blob/main/festival/docs/Leaderboards/Public.md)).
This is the authoritative timestamp for when the score was achieved. For
live-scraped score changes, `endTime` is also captured and stored as
`ScoreAchievedAt` in ScoreHistory (with `ChangedAt` being the scrape time).
```

### Smart Querying Strategy

Instead of querying every season for every song, be intelligent:

```
For a song with current high score in Season 7:
  1. Query Season 1 (evergreen / earliest)
     - If null → user didn't play in S1
  2. Binary search: query Season 4 (midpoint of 1–7)
     - If null → first play was S5, S6, or S7. Query S5.
     - If score found → first play was S1–S4. Query S2.
  3. Continue narrowing to find the first season with a score
  4. Then walk forward from the first season to find each improvement
```

This reduces queries from O(S) per song to O(log S + improvements) per song. For a typical user with ~1,000 songs and ~8 seasons, this could save thousands of requests.

**However**, given we don't expect many registered users and this is one-time, a **simple sequential approach** (query S1, S2, ..., Sn for each song) may be perfectly acceptable and much simpler to implement. With ~1,000 songs × 8 seasons × 6 instruments = ~48,000 requests at worst, at DOP=4 that's ~200 minutes (~3.3 hours). Not great, but acceptable as a one-time cost.

**Optimization: Group by season.** Instead of querying per-song, we can iterate by season and batch all songs for that season together. This doesn't reduce total requests but simplifies the logic.

**Optimization: Skip seasons after the known high-score season.** If the all-time entry says `Season = 5`, don't query seasons 6, 7, 8. This is free and significant.

**Optimization: Skip songs where Season = 0 or Season = 1.** If the all-time score was set in the implicitly earliest season, there's no history to find — the first play was the only play.

### Cost Analysis

| Scenario | Songs with scores | Avg seasons to query | Total requests | Time (DOP=4) |
|---|---|---|---|---|
| Casual player | 200 × 2 inst | ~3 seasons | ~1,200 | ~5 min |
| Active player | 1,000 × 4 inst | ~4 seasons | ~16,000 | ~65 min |
| Hardcore player | 2,000 × 6 inst | ~5 seasons | ~60,000 | ~250 min |

The hardcore case is heavy, but:
- It's **one-time per user**
- We don't expect many registered users (single digits to low dozens)
- It runs during idle time (between scrape passes, which have 4-hour intervals)
- It can be interrupted and resumed (persisted progress)

---

## Schema Additions

### `fst-meta.db`: Backfill Tracking

```sql
CREATE TABLE BackfillStatus (
    AccountId           TEXT    PRIMARY KEY,
    Status              TEXT    NOT NULL DEFAULT 'pending',
                                -- 'pending', 'in_progress', 'complete', 'error'
    SongsChecked        INTEGER NOT NULL DEFAULT 0,
    EntriesFound        INTEGER NOT NULL DEFAULT 0,
    TotalSongsToCheck   INTEGER NOT NULL DEFAULT 0,
    StartedAt           TEXT,             -- ISO 8601
    CompletedAt         TEXT,             -- ISO 8601
    LastResumedAt       TEXT,             -- ISO 8601, for resumption after interruption
    ErrorMessage        TEXT              -- if status = 'error'
);
```

### `fst-meta.db`: History Reconstruction Tracking

```sql
CREATE TABLE HistoryReconStatus (
    AccountId           TEXT    PRIMARY KEY,
    Status              TEXT    NOT NULL DEFAULT 'pending',
                                -- 'pending', 'in_progress', 'complete', 'skipped', 'error'
    SongsProcessed      INTEGER NOT NULL DEFAULT 0,
    TotalSongsToProcess INTEGER NOT NULL DEFAULT 0,
    SeasonsQueried      INTEGER NOT NULL DEFAULT 0,
    HistoryEntriesFound INTEGER NOT NULL DEFAULT 0,
    StartedAt           TEXT,
    CompletedAt         TEXT,
    ErrorMessage        TEXT
);
```

### `fst-meta.db`: Seasonal Leaderboard Windows

Cache discovered season windows:

```sql
CREATE TABLE SeasonWindows (
    SeasonNumber    INTEGER PRIMARY KEY,
    EventId         TEXT    NOT NULL,      -- leaderboard event ID segment
    WindowId        TEXT    NOT NULL,      -- window ID segment in URL
    DiscoveredAt    TEXT    NOT NULL       -- ISO 8601
);
```

### Progress Tracking for Resumption

The backfill and history reconstruction can be interrupted (scrape starts, service restarts, etc.). Track which song/instruments have been checked:

```sql
CREATE TABLE BackfillProgress (
    AccountId       TEXT    NOT NULL,
    SongId          TEXT    NOT NULL,
    Instrument      TEXT    NOT NULL,
    Checked         INTEGER NOT NULL DEFAULT 0,  -- 1 = checked, 0 = not yet
    EntryFound      INTEGER NOT NULL DEFAULT 0,  -- 1 = score found, 0 = no score / not checked
    CheckedAt       TEXT,
    PRIMARY KEY (AccountId, SongId, Instrument)
);

CREATE TABLE HistoryReconProgress (
    AccountId       TEXT    NOT NULL,
    SongId          TEXT    NOT NULL,
    Instrument      TEXT    NOT NULL,
    Processed       INTEGER NOT NULL DEFAULT 0,  -- 1 = done, 0 = not yet
    ProcessedAt     TEXT,
    PRIMARY KEY (AccountId, SongId, Instrument)
);
```

These tables enable **resumption without re-querying** — if the backfill is interrupted after checking 500 songs, it picks up at song 501.

---

## Data Flow Diagram

```
User Registration
       │
       ▼
  POST /api/register
       │
       ├──→ Insert RegisteredUsers (meta DB)
       ├──→ Build initial personal DB (from existing data)
       ├──→ Enqueue BackfillRequest(accountId)
       │
       ▼
  ScraperWorker (when idle)
       │
       ├──→ Phase: Score Backfill
       │    │
       │    ├── For each song/instrument with no entry for user:
       │    │     └── LookupAccountAsync → UPSERT if found
       │    │
       │    └── Rebuild personal DB
       │
       ├──→ Phase: History Reconstruction (one-time)
       │    │
       │    ├── Discover season windows (events API)
       │    ├── For each song/instrument with a score:
       │    │     └── Query seasonal leaderboards → ScoreHistory
       │    │
       │    └── Mark reconstruction complete
       │
       ▼
  Normal Scrape Loop
       │
       ├──→ Scrape (collect SeenRegisteredEntries in aggregates)
       │
       └──→ Post-pass: refresh registered user scores
            ├── For entries NOT in seen set → LookupAccountAsync (stale)
            ├── For songs with no entry at all → LookupAccountAsync (gap)
            └── UPSERT any findings, rebuild personal DBs
```

---

## API Changes

### Updated `POST /api/register` Response

```json
{
  "registered": true,
  "deviceId": "device-abc",
  "accountId": "epic-account-123",
  "personalDbReady": true,
  "backfillStatus": "pending",
  "historyReconStatus": "pending"
}
```

### New Endpoint: `GET /api/backfill/{accountId}/status` (Protected)

```json
{
  "accountId": "epic-account-123",
  "backfill": {
    "status": "in_progress",
    "songsChecked": 842,
    "totalSongsToCheck": 2000,
    "entriesFound": 156,
    "startedAt": "2026-02-14T10:00:00Z"
  },
  "historyReconstruction": {
    "status": "pending",
    "songsProcessed": 0,
    "totalSongsToProcess": 0,
    "seasonsQueried": 0,
    "historyEntriesFound": 0
  }
}
```

The mobile app can poll this to show progress: "Finding your scores... 842/2000 songs checked, 156 new scores found".

---

## Interaction with Other Features

### Rankings

Rankings are computed from `LeaderboardEntries`. Backfilled entries go into the same instrument DBs, so they're automatically included in the next rankings computation. However, backfilled entries may have a `Rank` that's beyond 60,000 (the API lookup returns the actual rank). This is fine for `Normalized Rank = Rank / EntryCount` — the entry count from `SongStats` is the count in our DB, which may be less than the actual total entries on that song's leaderboard.

**Note:** For songs where the user's rank is beyond our scraped range, we should use the `totalPages` from the lookup response (if available) to get the true entry count for that song, and use that as the denominator for Normalized Rank for that specific entry. Otherwise the user's Normalized Rank could be > 1.0. Alternatively, clamp to 1.0.

### Opps

Backfilled entries affect neighborhood queries for Opps. Once the user's low-ranked entries are in the instrument DB, Opps computation will find neighbors near those ranks — producing more accurate Opps results.

### ScoreHistory

History reconstruction writes to `ScoreHistory` in the meta DB — the same table used by live change detection. For reconstructed entries, `ScoreAchievedAt` holds the API's `endTime` (the exact timestamp the score was set) while `ChangedAt` is the time the reconstruction ran.

Each `ScoreHistory` row is a **point-in-time snapshot**. In addition to OldScore/NewScore and OldRank/NewRank, it captures:

| Column | Description |
|--------|-------------|
| `Accuracy` | Accuracy percentage achieved on this play |
| `IsFullCombo` | Whether a full combo was achieved |
| `Stars` | Star rating / difficulty level achieved |
| `Percentile` | Percentile ranking at the time the score was recorded |
| `Season` | The season in which the score was set |
| `ScoreAchievedAt` | ISO 8601 timestamp when the session ended (from `endTime` in the API). Exact play time. |
| `ChangedAt` | When the row was written (scrape time for live detection, reconstruction time for backfill) |

These values are **never retroactively updated**. Even if the user's rank drops later as other players surpass them, the recorded rank/percentile reflects the state when the score was set. This gives users an accurate historical timeline ("I was rank 42 in the top 0.3% when I set this score") and supports features like "personal best rank" tracking.

For **reconstructed** entries (from seasonal leaderboard queries), the API returns rank, accuracy, FC, stars, percentile, **and `endTime`** for the best session. The `endTime` is the real datetime the score was achieved — enabling accurate timeline graphs without relying on season boundaries.

---

## Implementation Order (Proposed)

1. **BackfillRequest queue + coordination** — `Channel<BackfillRequest>` in `ScraperWorker`, sleep interrupt mechanism.
2. **BackfillStatus / BackfillProgress schema** — meta DB tables for tracking.
3. **ScoreBackfiller class** — iterates songs/instruments, calls `LookupAccountAsync` for gaps, UPSERTs results.
4. **Post-pass integration** — wire backfill phase into `ScraperWorker` post-pass sequence.
5. **Registration endpoint update** — enqueue backfill on registration, return status.
6. **Backfill status API endpoint** — `GET /api/backfill/{accountId}/status`.
7. **SeasonWindows discovery** — events API call to find seasonal window IDs.
8. **HistoryReconstructor class** — queries seasonal leaderboards, builds ScoreHistory entries.
9. **History reconstruction integration** — runs after backfill for new users, one-time.
10. **Recurring post-scrape gap check** — reuse `ScoreBackfiller` for delta checks.

---

## Open Questions

- [ ] **Seasonal leaderboard URL format**: We need to discover the exact URL structure for seasonal lookups. The all-time URL uses `alltime_{songId}_{instrument}/alltime/{accountId}`. What replaces `alltime` for seasonal? Options: use the diagnostic endpoint to probe, or call the events API to discover window IDs.
- [ ] **DOP for backfill**: 4 is safe for normal scraping. Should backfill use a lower DOP (2) to be gentle, since it's running during "idle" time and we don't want to trigger rate limits before the next scrape?
- [ ] **Backfill interruption**: If a scrape needs to start mid-backfill, should backfill pause gracefully (preferred) or be aborted and restarted? Pause is cleaner — the progress table enables exact resumption.
- [ ] **History reconstruction — is it worth the cost?** For a hardcore player, 60K requests is ~4 hours of one-time work. Is the historical timeline valuable enough? Could be deferred to v2 or made opt-in via an API flag on registration.
- [ ] **Song entry count for backfilled entries**: When a user's entry is beyond rank 60K, should we trust our `SongStats.EntryCount` (which only counts scraped entries) or try to get the true leaderboard size from the lookup response?
- [ ] **"Confirmed no score" tracking**: Should we persist that a user has no score on a song/instrument (to avoid re-querying every pass)? The `BackfillProgress` table with `EntryFound = 0` serves this purpose, but we'd need to invalidate it when new songs are added.
- [ ] **Stale-entry re-query frequency**: The seen-set approach re-queries stale entries after every pass. If a user has 500 stale entries, that's 500 requests every 4 hours — acceptable. If they have 10,000, we might want to rate-limit stale re-queries to every Nth pass. In practice, most active players have the majority of their played songs in the top 60K, so the stale count should be modest.
- [ ] **SeenRegisteredEntries memory**: For 10 registered users × 12,000 song/instrument pairs = 120K tuples. Each tuple is ~3 string refs (~72 bytes). Total ~8.6 MB — trivial. If registered user count grows, consider a `HashSet` with a composite key string instead.
- [ ] **Entry deletion on null lookup**: If a registered user's stale entry returns null from `LookupAccountAsync`, should we delete it from the instrument DB? This would mean the entry was somehow removed from the leaderboard entirely (account ban, data reset). Deleting is probably correct, but flag it in logs.
- [ ] **Sleep interrupt vs. timer-based poll**: Should the backfill queue use a semaphore to wake the scraper loop, or should the scraper just poll the queue every N minutes even when sleeping? Semaphore is more responsive; polling is simpler.
- [ ] **Existing `POST /api/register` flow**: Currently it builds the personal DB synchronously in the request handler. Should we move personal DB building fully async (return immediately, build in background)? The current sync build is fast (~1 second) since it just reads existing data, so keeping it sync for immediate availability seems fine.
