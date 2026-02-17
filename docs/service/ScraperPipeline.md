# FSTService — Scrape Pipeline

This document details the scraping lifecycle: what happens during each phase of a scrape pass, how data flows through the system, and the mechanisms that make it resilient and efficient.

## Scrape Pass Overview

A single scrape pass is orchestrated by `ScraperWorker.RunScrapePassAsync()` and executes these phases sequentially:

```
Auth Token ──► Song Catalog Sync ──► Global Leaderboard Scrape ──► FirstSeenSeason
     │                                                                    │
     │         ┌──────────────────────────────────────────────────────────┘
     │         ▼
     │    Name Resolution ──► Personal DB Rebuild ──► Post-Scrape Refresh
     │                                                        │
     │         ┌──────────────────────────────────────────────┘
     │         ▼
     │    Score Backfill ──► History Reconstruction ──► Session Cleanup
     │                                                        │
     └────────────────────────────────────────────────────────┘
                                                         Sleep → Loop
```

Each phase reports its status via `ScrapeProgressTracker`, visible through the `GET /api/progress` endpoint.

---

## Phase 1: Auth Token Acquisition

**Component:** `TokenManager`

Before any API call, the service ensures a valid Epic Games access token is available:

1. Check in-memory token — if valid (>5 minutes until expiry), use it
2. Try refreshing via the in-memory refresh token
3. Fall back to the disk-stored refresh token (`data/device-auth.json`)
4. If all fail, trigger interactive device code setup (first run only)

Token refresh is serialized via a `SemaphoreSlim(1,1)` to prevent concurrent refresh attempts.

---

## Phase 2: Song Catalog Sync

**Component:** `FestivalService` (from `FortniteFestival.Core`)

Re-syncs the song catalog from Epic's calendar API. This ensures newly released songs are included in the current scrape pass.

Additionally, a **background song sync loop** runs independently on 15-minute clock-aligned boundaries (`:00`, `:15`, `:30`, `:45`). New songs are persisted to `fst-service.db` but won't affect an already-running scrape pass.

---

## Phase 3: Global Leaderboard Scrape

**Components:** `GlobalLeaderboardScraper`, `GlobalLeaderboardPersistence`, `AdaptiveConcurrencyLimiter`

This is the core data collection phase. It scrapes the full global leaderboard for every song × every enabled instrument combination.

### Epic API Patterns

FSTService uses two Epic leaderboard API versions:

| Version | Method | URL Pattern | Use Case |
|---|---|---|---|
| **V1** | GET | `/api/v1/leaderboards/FNFestival/{eventId}/{windowId}/{accountId}?page={n}` | Full leaderboard scraping (paged) |
| **V2** | POST | `/api/v2/games/FNFestival/leaderboards/{eventId}/{windowId}/scores` | Targeted player lookups |

**V1 Paging Strategy:**
1. Fetch page 0 to discover `totalPages`
2. Fetch pages 1…N concurrently, throttled by the `AdaptiveConcurrencyLimiter`
3. Reassemble entries in page order

**Event/Window ID Formats:**
- Alltime: `eventId = alltime_{songId}_{instrument}`, `windowId = alltime`
- Seasonal: `eventId = {seasonPrefix}_{songId}`, `windowId = {songId}_{instrument}`

### Pipelined Write Architecture

Network I/O and disk I/O are overlapped using per-instrument channels:

```
                         ┌─────────────────────┐
   Song results ────────►│ Channel<WorkItem>    │────► Solo_Guitar writer ────► fst-Solo_Guitar.db
                         │ (bounded, cap=32)    │
                         └─────────────────────┘
                         ┌─────────────────────┐
   Song results ────────►│ Channel<WorkItem>    │────► Solo_Bass writer ────► fst-Solo_Bass.db
                         │ (bounded, cap=32)    │
                         └─────────────────────┘
                                  ...×6
```

- Each instrument gets a dedicated bounded `Channel<PersistWorkItem>` (capacity 32, single reader, multiple writers)
- Each channel has a dedicated writer task that drains and persists to its own SQLite file — **zero cross-instrument lock contention**
- `EnqueueResultAsync` applies back-pressure when persistence can't keep up with scraping
- `DrainWritersAsync` signals completion and waits for all writers to finish

### Change Detection

During persistence, `GlobalLeaderboardPersistence` detects score changes for registered users:

1. **Before UPSERT** — snapshot registered users' current scores for the song/instrument
2. **UPSERT** — write all entries to the instrument DB
3. **After UPSERT** — compare new scores with snapshots
4. Score changes are recorded as `ScoreHistory` entries with `AllTimeRank`
5. Changed account IDs are aggregated in `PipelineAggregates` for downstream phases

### Adaptive Concurrency (AIMD)

The `AdaptiveConcurrencyLimiter` dynamically adjusts the degree of parallelism based on observed error rates, using an **Additive Increase / Multiplicative Decrease** algorithm (similar to TCP congestion control):

| Parameter | Value |
|---|---|
| Evaluation window | 500 requests |
| Additive increase | +16 slots |
| Multiplicative decrease | ×0.75 |
| Error threshold (low, increase) | < 1% |
| Error threshold (high, decrease) | > 5% |
| Between 1%–5% | Hold steady |

The limiter wraps a `SemaphoreSlim`. On increase, extra tokens are released. On decrease, tokens are drained via non-blocking `WaitAsync(0)` — if tokens are in-flight, effective DOP converges naturally as tasks complete.

### Retry Strategy

`FetchPageAsync` (V1) retries up to 3 times with exponential backoff:

| Attempt | Delay |
|---|---|
| 1st retry | 500ms |
| 2nd retry | 1 second |
| 3rd retry | 2 seconds |

**Retryable conditions:** HTTP 429 (respects `Retry-After` header), 5xx, `HttpRequestException`, timeout `TaskCanceledException`.

V2 targeted lookups use `ResilientHttpExecutor` with the same retry logic, plus integration with the `AdaptiveConcurrencyLimiter` — non-retryable status codes (400, 403, 404) are NOT reported as failures since the server handled the request properly.

---

## Phase 4: FirstSeenSeason Calculation

**Component:** `FirstSeenSeasonCalculator`

Determines the first season each song appeared in the leaderboard system. This data is critical for `HistoryReconstructor` — it prevents querying seasonal leaderboards for seasons before a song existed, significantly reducing API calls.

**Algorithm (per song without existing data):**

1. **Phase 1 (local):** Query `MIN(HighScoreSeason)` across all instrument DBs for the song
2. If no entries → store `EstimatedSeason = global max`, `FirstSeenSeason = null`
3. If `MIN = 1` → song existed from the beginning, store `FirstSeenSeason = 1`
4. **Phase 2 (API probe):** If `MIN ≥ 2` → probe season `MIN - 1` via `LookupSeasonalAsync`
   - If probe succeeds (even with "no score found") → song existed in the earlier season
   - If probe fails with `HttpRequestException` → seasonal leaderboard doesn't exist; use observed `MIN`

---

## Phase 5: Account Name Resolution

**Component:** `AccountNameResolver`

Resolves Epic account IDs to display names using Epic's bulk account lookup API.

- **Endpoint:** `GET /account/api/public/account?accountId=X&accountId=Y&...`
- **Batch size:** 100 IDs per request (Epic's maximum)
- **Concurrency:** Limited to 8 concurrent requests (configurable)
- **Failure handling:** Best-effort. Unresolved accounts are retried next pass. IDs that return no display name (deleted/banned accounts) are marked unresolvable to prevent infinite retries.

---

## Phase 6: Personal DB Rebuild

**Component:** `PersonalDbBuilder`

Rebuilds per-user/device SQLite databases only for accounts whose scores changed during the scrape pass (tracked via `PipelineAggregates.ChangedAccountIds`).

**Build process:**
1. Create a temp file
2. Write Songs table (full catalog), Scores table (per-instrument columns from instrument DBs), ScoreHistory table (from meta DB)
3. Atomically replace the target file via `File.Move`
4. Copy the built DB to all other devices registered to the same account

**Storage:** `data/personal/{accountId}/{deviceId}.db`

---

## Phase 7: Post-Scrape Refresh

**Component:** `PostScrapeRefresher`

After the global scrape, registered users may have entries that weren't captured — either because they scored below the top ~60K (not in scraped pages) or because their entry changed between scrape passes.

**Two categories of entries to refresh:**

| Category | Description |
|---|---|
| **Gap entries** | Songs the user has no entry for (scored below top 60K) |
| **Stale entries** | Songs the user HAS an entry for, but it wasn't observed in the current scrape pass |

For each unseen (accountId, songId, instrument) triple:
1. Call `LookupAccountAsync` (V2 targeted lookup)
2. UPSERT into the instrument DB
3. Record score changes in `ScoreHistory`

Uses the "seen set" from `PipelineAggregates.SeenRegisteredEntries` — the set of (AccountId, SongId, Instrument) tuples observed during the scrape pass — to identify what needs refreshing.

---

## Phase 8: Score Backfill

**Component:** `ScoreBackfiller`

A comprehensive per-user operation that fills in leaderboard entries for registered users who scored below the global scrape threshold. Unlike the post-scrape refresh (which only checks songs with existing entries), the backfiller checks **every** charted song × instrument combination.

**State machine:** `BackfillStatus` table tracks progress: `Queued → InProgress → Complete/Failed`

**How it works:**

1. Enumerate all charted songs × 6 instruments to build the total pair count
2. Load already-checked pairs from `BackfillProgress` (enables resumption after crashes)
3. Create an `AdaptiveConcurrencyLimiter` (initial DOP = configured/2, max = configured×2)
4. For each unchecked pair:
   - Check if the instrument DB already has an entry (skip if yes)
   - Otherwise, call `LookupAccountAsync` (V2)
   - Found entries are UPSERTed into the instrument DB and recorded as `ScoreHistory`
5. Progress is flushed to DB every 25 songs (crash-safe)

**Sources of backfill requests:**
- User login (`BackfillQueue.Enqueue` from `UserAuthService`)
- Manual trigger (`POST /api/backfill/{accountId}`)
- Pending/in-progress backfills from previous interrupted passes (resumed automatically)

---

## Phase 9: History Reconstruction

**Component:** `HistoryReconstructor`

A one-time per-user operation that walks seasonal leaderboards backwards to reconstruct the timeline of when each high score was set. Produces `ScoreHistory` entries capturing the complete score progression.

### Season Window Discovery

Before reconstruction, the service must know which seasons exist:

1. **DB cache check** — `SeasonWindows` table
2. **Events API** — `GET /api/v1/events/FNFestival/data/{accountId}?showPastEvents=true` — parses `eventWindows` to extract season numbers and date ranges
3. **Fallback probing** — Uses convention-based naming:
   - Season 1 = `"evergreen"`
   - Season 2–9 = `"season002"` through `"season009"`
   - Season 10+ = `"season010"`, etc.
   - Probes with `LookupSeasonalAsync` using a known song; stops after 2 consecutive failures

### Reconstruction Algorithm

1. Gather all the user's alltime entries across all instrument DBs
2. Filter to entries where `HighScoreSeason > 1` (Season 0–1 means the first play IS the current high score)
3. For each (songId, instrument) pair:
   a. Use `FirstSeenSeason` data to determine the earliest season to query
   b. Query all relevant seasons in parallel using `LookupSeasonalSessionsAsync` to retrieve **all sessions** from each season's `sessionHistory`
   c. Sort all sessions by `endTime` ascending (falls back to season number for ordering)
   d. Walk sorted sessions keeping only those where score **strictly increases** — the score improvement progression
   e. Insert each progression point as a `ScoreHistory` row with `SeasonRank`

### Concurrency Control

When reconstructing multiple users simultaneously, they share a single `AdaptiveConcurrencyLimiter` to prevent overwhelming Epic's API. The shared limiter uses:
- Initial DOP = configured/2
- Max DOP = configured×2

All user reconstruction tasks run in parallel via `Task.WhenAll`, with the shared limiter controlling total API concurrency.

### Resumability

Progress is tracked at the (account, song, instrument) level in `HistoryReconProgress`. If reconstruction is interrupted, it resumes from where it left off on the next pass.

**State machine:** `HistoryReconStatus` table: `InProgress → Complete/Failed`

---

## Phase 10: Session Cleanup

Expired and revoked auth sessions older than 7 days are purged from the `UserSessions` table. This is a lightweight housekeeping step that runs at the end of every pass.

---

## Background Song Sync

Independent of the scrape pass lifecycle, a background task syncs the song catalog from Epic's calendar API every 15 minutes on clock-aligned boundaries (`:00`, `:15`, `:30`, `:45`). New songs are persisted to `fst-service.db` but do not affect an already-running scrape pass.

This ensures the song catalog stays current between scrape passes, which matters for:
- API responses to `GET /api/songs`
- Backfill operations triggered via API during the sleep interval
- The next scrape pass picking up newly released songs immediately

---

## Progress Tracking

The `ScrapeProgressTracker` is a thread-safe singleton that provides real-time visibility into the scrape pass. It exposes:

| Data Point | Description |
|---|---|
| Current phase | Which phase is executing (`ScrapePhase` enum) |
| Songs progress | Completed / total count |
| Leaderboard progress | Completed / total, broken down by instrument |
| Page progress | Fetched / estimated total / discovered total |
| Network stats | Requests, retries, bytes received |
| Concurrency | Current DOP from the adaptive limiter |
| Estimates | Progress percent, estimated remaining seconds |
| Completed operations | History of finished operations this pass |

Progress estimation extrapolates total pages based on the ratio of discovered pages to discovered leaderboards when not all page counts are known yet, falling back to cached totals from previous passes.
