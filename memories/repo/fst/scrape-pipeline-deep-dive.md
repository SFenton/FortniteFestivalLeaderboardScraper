# Scrape Pipeline Deep Dive

> Last updated: 2026-04-03

## Pipeline Overview

The scrape pipeline runs as a `BackgroundService` (`ScraperWorker`) in an infinite loop with a configurable `ScrapeInterval` (default 4 hours). Each pass flows through these high-level stages:

```
ScraperWorker.RunScrapePassAsync()
  ├── 1. Song Catalog Sync         (re-sync from Epic API)
  ├── 2. Path Generation            (fire-and-forget, parallel with scrape)
  ├── 3. ScrapeOrchestrator.RunAsync()
  │     ├── Build scrape requests   (1 per song × enabled instruments)
  │     ├── V1 Global Scrape        (paged leaderboard fetching)
  │     ├── Deep Scrape Wave 2      (deferred over-threshold combos)
  │     ├── Pipelined Persistence   (bounded channel writers per instrument)
  │     ├── WAL Checkpoint          (SQLite WAL flush)
  │     └── Population Update       (from Epic's reported totalPages)
  ├── 4. PostScrapeOrchestrator.RunAsync()
  │     ├── Enrichment Phase (parallel)
  │     │     ├── Rank Recomputation
  │     │     ├── FirstSeenSeason Calculation
  │     │     ├── Account Name Resolution
  │     │     └── Entry Pruning (after ranks complete)
  │     ├── Song Processing Machine  (registered user refresh + backfill + history recon)
  │     ├── Rankings Computation     (per-instrument → composite → combo → history)
  │     ├── Rivals + Leaderboard Rivals (parallel)
  │     ├── Precomputation + Player Stats Tiers (parallel)
  │     └── Finalization (WAL checkpoint + cache pre-warming, parallel)
  ├── 5. Save precomputed responses to disk
  ├── 6. Prime songs/player/leaderboard caches
  └── 7. EndPass + Sleep
```

### Input/Output Contract

- **Input**: `FestivalService.Songs` (song catalog), Epic access token, `ScraperOptions`
- **Core Output**: `ScrapePassResult` containing `ScrapePassContext` (token, registered IDs, aggregates, scrape requests, DOP)
- **Side Effects**: Instrument databases populated, meta DB updated, caches primed, WebSocket notifications sent

## Phase Details

### Phase 1: Song Catalog Sync

**File**: `ScraperWorker.cs` (inline in `RunScrapePassAsync`)

- Calls `FestivalService.SyncSongsAsync()` to refresh the song catalog from Epic's calendar/content APIs
- Background song sync also runs independently every 5 minutes (clock-aligned via `BackgroundSongSyncLoopAsync`)
- New songs are persisted but don't affect an in-progress scrape pass

### Phase 2: Path Generation (fire-and-forget)

**File**: `ScraperWorker.TryGeneratePathsAsync()` + `PathGenerator.cs`

- Downloads encrypted MIDI `.dat` files from Epic CDN
- Decrypts with AES key (`MidiEncryptionKey` option)
- Runs CHOpt CLI to compute optimal paths and max attainable scores per instrument
- Stores results in `IPathDataStore` for use in over-threshold detection and score validation
- Runs in parallel with the main scrape — errors don't block scraping

### Phase 3: Core Global Scrape (ScrapeOrchestrator)

**File**: `ScrapeOrchestrator.cs`

**Purpose**: Fetch all V1 alltime global leaderboard entries for every song × instrument combination.

**Key Steps**:
1. **Build requests**: One `SongScrapeRequest` per song with all enabled instruments (up to 6: Guitar, Bass, Vocals, Drums, ProLead, ProBass)
2. **Initialize progress**: Total leaderboards = songs × instruments; loads cached page estimate from previous run
3. **Start pipelined writers**: `GlobalLeaderboardPersistence.StartWriters()` creates per-instrument bounded channel writers
4. **Scrape**: `GlobalLeaderboardScraper.ScrapeManySongsAsync()` — all songs fire in parallel, bounded by `AdaptiveConcurrencyLimiter`
5. **Deep Scrape Wave 2**: If `deferDeepScrape=true` and `validEntryTarget>0`, collects deferred over-threshold metadata and runs breadth-first coordinated deep scrape via `DeepScrapeCoordinator`
6. **Drain writers**: Wait for all per-instrument channel writers to flush
7. **Flush deferred account IDs**: Persist accumulated account ID → name mappings
8. **WAL Checkpoint**: Force SQLite WAL checkpoint to keep files small
9. **Update population**: Upsert `LeaderboardPopulation` from Epic's `reportedTotalPages`

**Output**: `ScrapePassResult` with scrape ID, request/byte counts, duration, and `ScrapePassContext`

### Phase 4: Post-Scrape Enrichment (PostScrapeOrchestrator.RunEnrichmentAsync)

**File**: `PostScrapeOrchestrator.cs`

Four operations with partial parallelism:

| Operation | Parallel With | Description |
|---|---|---|
| **Rank Recomputation** | FirstSeen, NameRes | Updates rank column in instrument DBs. Incremental (only changed songs) when possible |
| **FirstSeenSeason** | Ranks, NameRes | Probes Epic API to determine the earliest season each song appeared. Skips already-calculated songs |
| **Account Name Resolution** | Ranks, FirstSeen | Resolves Epic account IDs → display names via bulk lookup API (100 per request). Batched with semaphore-bounded concurrency |
| **Entry Pruning** | FirstSeen, NameRes (after Ranks) | Removes excess entries beyond `MaxPagesPerLeaderboard × 100`, preserving registered users and entries above CHOpt max |

**Execution order**: Ranks, FirstSeen, and NameRes start in parallel. Pruning starts after Ranks completes, overlapping with FirstSeen and NameRes.

### Phase 5: Song Processing Machine (PostScrapeOrchestrator.RefreshRegisteredUsersAsync)

**File**: `SongProcessingMachine.cs`, `PostScrapeOrchestrator.cs`

**Purpose**: Unified machine for registered user refresh, backfill, and history reconstruction. Processes ALL songs in parallel for ALL users.

**Architecture**:
- Songs fire in parallel, bounded by `SongMachineDop` (default 32) via `SemaphoreSlim`
- Each song fans out into 6 instrument tasks in parallel
- Each instrument performs alltime batch lookup AND seasonal session lookup in parallel
- All API calls go through `SharedDopPool` for priority-aware slot allocation
- Uses V2 POST batch API (`/api/v2/.../scores` with `teams` body) for efficient multi-account lookups

**User Types** (combined in a single machine run):
| Type | Purpose | Work |
|---|---|---|
| `PostScrape` | Refresh registered users' scores below top-60K cutoff | Alltime + current season sessions |
| `Backfill` | Fill missing scores for newly registered users | Alltime for all songs |
| `HistoryRecon` | Reconstruct complete score timeline | All seasons' sessions |

**Key classes**:
- `UserWorkItem`: Per-user work spec with `WorkPurpose` flags, `SeasonsNeeded`, `AlreadyChecked` for resumption
- `BatchResultProcessor`: Processes V2 results — detects changes, upserts instrument DBs, inserts ScoreHistory, raises population floors

### Phase 6: Rankings Computation

**File**: `RankingsCalculator.cs`

**Metrics computed** (all with Bayesian credibility adjustment where noted):
- **Adjusted Skill**: AVG(rank/entries) per song, Bayesian (m=50, C=0.5)
- **Weighted**: Log₂-weighted AVG(rank/entries), Bayesian
- **FC Rate**: Full combo percentage, Bayesian
- **Total Score**: Sum of all scores (no Bayesian)
- **Max Score %**: AVG(score/CHOpt max), Bayesian

**Flow**: Per-instrument SongStats + AccountRankings (parallel) → Composite rankings → History snapshots → Combo rankings

### Phase 7: Rivals + Leaderboard Rivals (parallel)

**Files**: `RivalsOrchestrator.cs`, `RivalsCalculator.cs`, `LeaderboardRivalsCalculator.cs`

- **Per-song rivals**: Finds opponents with similar scores across instruments. Runs in parallel across users. Skipped for users whose scores didn't change.
- **Leaderboard rivals**: Per instrument per rank method, finds neighbors and compares shared songs. Only runs if rankings succeeded.
- Both run in parallel via `Task.WhenAll`

### Phase 8: Precomputation + Player Stats Tiers (parallel)

**Files**: `ScrapeTimePrecomputer.cs`, `PlayerStatsCalculator.cs`

- **Precomputer**: Generates JSON responses for registered players and popular leaderboard pages. Stored in `ConcurrentDictionary` for <1ms API response times. Also persisted to disk for instant startup.
- **Player Stats Tiers**: Computes leeway-tiered statistics for accounts whose scores changed + all registered users. Enables frontend slider for CHOpt leeway filtering.
- Both run in parallel (no shared write targets)

### Phase 9: Finalization

- **WAL Checkpoint**: Force-flush all SQLite WAL files
- **Rankings Cache Pre-Warming**: Run CTE queries to populate cache for registered users
- Both run in parallel via `Task.WhenAll`
- **EndPass**: Marks progress tracker as idle

## Concurrency Model

### AdaptiveConcurrencyLimiter (AIMD)

**File**: `FortniteFestival.Core/Scraping/AdaptiveConcurrencyLimiter.cs`

TCP-inspired congestion control for HTTP request concurrency:

| Parameter | Value | Description |
|---|---|---|
| Evaluation Window | 500 requests | Size of sliding window before DOP adjustment |
| Additive Increase | +16 | DOP increase when error rate < 1% |
| Multiplicative Decrease | ×0.75 | DOP decrease when error rate > 5% |
| Error Threshold Low | 1% | Below this → increase |
| Error Threshold High | 5% | Above this → decrease |
| Hold Zone | 1–5% | No change |

**Token-bucket rate limiter** (optional): When `MaxRequestsPerSecond > 0`, a `SemaphoreSlim` is refilled every 50ms with `maxRPS/20` tokens. Requests must acquire both a concurrency slot AND a rate token.

**Release debt mechanism**: When DOP decreases but tokens are held by in-flight tasks, a debt counter is incremented. Releasing tasks absorb debt tokens instead of returning them to the semaphore.

### SharedDopPool

**File**: `SharedDopPool.cs`

Wraps `AdaptiveConcurrencyLimiter` with two priority lanes:

| Priority | Access | Use Case |
|---|---|---|
| **High** | Direct access to full DOP | Main scrape, post-scrape refresh |
| **Low** | Gated by secondary `SemaphoreSlim` (default 20% of maxDop) | API-triggered backfill, registration backfill |

Low-priority callers must acquire the gate AND the inner limiter. This prevents backfill work from starving the main scrape.

### Pipelined Persistence (Bounded Channels)

The core scrape uses per-instrument `System.Threading.Channels.Channel<T>` for producer-consumer pipelining:

1. Scraper produces `GlobalLeaderboardResult` objects
2. `GlobalLeaderboardPersistence.EnqueueResultAsync()` dispatches to the correct instrument channel
3. Per-instrument writer tasks drain channels in batches (`WriteBatchSize`, default 10) within single PostgreSQL transactions
4. `BoundedChannelCapacity` (default 128) provides back-pressure when writers fall behind

### Song-Level Parallelism

The `SongProcessingMachine` uses a `SemaphoreSlim` gate (`SongMachineDop`, default 32) to bound song-level parallelism. Each song fans out into 6 instrument tasks (unbounded), and each instrument acquires a `SharedDopPool` slot for its API call. This gives approximately `32 × 6 = 192` concurrent V2 requests.

## V2 Scraping — SongProcessingMachine

**File**: `SongProcessingMachine.cs`

The V2 API (`/api/v2/games/FNFestival/leaderboards/.../scores`) supports batched lookups with a `teams` body containing multiple account IDs. The machine exploits this for efficient multi-user processing:

### Request Flow per Song/Instrument

```
ProcessSongInstrumentAsync()
  ├── RunAlltimeLookups()          (alltime V2 POST batch)
  │     ├── For each chunk of users (batch size 500):
  │     │     ├── Acquire DOP slot (high or low priority)
  │     │     ├── POST /api/v2/.../alltime/scores with teams body
  │     │     ├── Release DOP slot immediately after HTTP response
  │     │     └── Process results outside DOP slot (DB writes)
  │     └── Mark backfill progress per user/song/instrument
  └── RunSeasonalLookups()         (per-season V2 POST batch)
        └── For each season needed by any user:
              └── Same acquire-call-release-process pattern
```

Both alltime and seasonal lookups run in parallel via `Task.WhenAll` — they target independent API endpoints and write to separate tables.

### V2 vs V1 API

| Aspect | V1 (Global Scrape) | V2 (SongProcessingMachine) |
|---|---|---|
| HTTP Method | GET | POST |
| Pagination | page=0…N (100 entries/page) | fromIndex (25 entries/page) |
| Account Targeting | None (global) | `teams` body with specific account IDs |
| Use Case | Scrape top-N entries for all users | Targeted lookup for registered users |
| Concurrency | Shared `AdaptiveConcurrencyLimiter` | `SharedDopPool` with priority lanes |

### Deep Scrape (Wave 2)

**File**: `DeepScrapeCoordinator.cs`

When CHOpt max scores are available and a leaderboard's top score exceeds `CHOptMax × OverThresholdMultiplier` (default 1.05), the scraper defers a deep scrape job:

1. Wave 1 scrapes the standard `MaxPagesPerLeaderboard` (default 100 = 10,000 entries)
2. Deferred metadata is collected for all over-threshold combos
3. `DeepScrapeCoordinator` runs breadth-first: all page 101s across all combos before any page 102
4. Each job tracks valid entries (≤ raw CHOpt max) and stops when `ValidEntryTarget` (default 10,000) is reached
5. 3 consecutive 403 responses on a combo → that combo is marked done (CDN boundary hit)

## HTTP Resilience — ResilientHttpExecutor

**File**: `ResilientHttpExecutor.cs`

### Standard Retry

| Error Type | Strategy | Budget |
|---|---|---|
| 429 Rate Limit | Honour `Retry-After` header, then exponential backoff | `maxRetries` (default 3) |
| 5xx Server Error | Exponential backoff: 500ms × 2^(attempt-1), capped at 30s | `maxRetries` (default 3) |
| `HttpRequestException` | Exponential backoff, capped at 30s | **Infinite** (only CancellationToken exits) |
| `TaskCanceledException` (timeout) | Exponential backoff, capped at 30s | **Infinite** (only CancellationToken exits) |

### CDN Block Handling (403 non-JSON)

Epic's CDN returns 403 with HTML body when rate-limited at the infrastructure level. The executor uses a **shared cooldown** model:

1. First request detecting a CDN block becomes the **probe**
2. Probe walks an escalating backoff schedule: 500ms, 1s, 2s, 5s, 10s, 15s, 30s, 45s, 60s, then 60s indefinitely
3. All other concurrent requests **wait** for the cooldown timestamp instead of hammering the CDN
4. Probe tests the CDN after each delay; on success, clears cooldown and resets retry index
5. Non-probe requests add random jitter (up to 500ms) to avoid thundering herd

### AIMD Integration

Success/failure outcomes are reported to the `AdaptiveConcurrencyLimiter` via `ReportSuccess()`/`ReportFailure()`. The executor does NOT manage slot acquisition — callers are responsible for acquiring/releasing concurrency slots.

## Scheduling

### Main Loop (`ScraperWorker`)

```
while (!stoppingToken.IsCancellationRequested)
{
    await RunScrapePassAsync(...)
    if (opts.RunOnce) break;
    await Task.Delay(opts.ScrapeInterval, stoppingToken);  // default 4 hours
}
```

### Background Song Sync

Clock-aligned to 5-minute boundaries (:00, :05, :10, ...). Sleeps until the next boundary, then syncs. Runs independently of scraping.

### Operational Modes

| Mode | Trigger | Behavior |
|---|---|---|
| Normal | Default | Infinite scrape loop with `ScrapeInterval` sleep |
| `--once` | `RunOnce=true` | Single scrape + post-scrape, then exit |
| `--api-only` | `ApiOnly=true` | No scraping, API-only mode |
| `--setup` | `SetupOnly=true` | Device code auth setup only |
| `--test "song"` | `TestSongQuery` set | Scrape single song(s) across all instruments, then exit |
| `--resolve-only` | `ResolveOnly=true` | Name resolution only, then exit |
| `--backfill-only` | `BackfillOnly=true` | Backfill enrichment only, then exit |
| `--precompute` | `PrecomputeOnly=true` | Precompute API responses to disk, then exit |

### Key Timing Options

| Option | Default | Description |
|---|---|---|
| `ScrapeInterval` | 4 hours | Time between scrape passes |
| `SongSyncInterval` | 5 minutes | Background song catalog refresh |
| `DegreeOfParallelism` | 16 | Initial/max DOP for global scrape |
| `MaxPagesPerLeaderboard` | 100 | Pages per leaderboard (100 entries/page) |
| `SongMachineDop` | 32 | Max concurrent songs in SongProcessingMachine |
| `PageConcurrency` | 10 | Pages per instrument in sequential mode |
| `SongConcurrency` | 1 | Songs in parallel in sequential mode |
| `LookupBatchSize` | 500 | Accounts per V2 batch request |
| `MaxRequestsPerSecond` | 0 (unlimited) | Token-bucket RPS cap |

## Progress Tracking

**File**: `ScrapeProgressTracker.cs`

Thread-safe singleton with lock-free counters (`Interlocked` operations). Exposed via `GET /api/progress` endpoint.

### Phase Enum

```csharp
Idle → Initializing → Scraping → PostScrapeEnrichment → SongMachine → 
ComputingRankings → ComputingRivals → Precomputing → Finalizing → Idle
```

(Also: `CalculatingFirstSeen`, `ResolvingNames`, `RefreshingRegisteredUsers`, `BackfillingScores`, `ReconstructingHistory`)

### Progress Response Structure

```json
{
  "current": {              // Active operation snapshot
    "operation": "Scraping",
    "subOperation": "fetching_leaderboards",
    "progressPercent": 45.2,
    "estimatedRemainingSeconds": 120,
    "songs": { "completed": 50, "total": 200 },
    "leaderboards": { "completed": 300, "total": 1200 },
    "leaderboardsByInstrument": { ... },
    "pages": { "fetched": 5000, "estimatedTotal": 12000, "discoveryComplete": false },
    "requests": 5100,
    "retries": 12,
    "bytesReceived": 150000000,
    "currentDop": 256,
    "inFlight": 200,
    "requestsPerSecond": 85.3
  },
  "running": [...],         // Currently active operations (scrape + path gen)
  "completedOperations": [...],  // Phase history for this pass
  "passElapsedSeconds": 300.5,
  "pathGeneration": { ... }     // Parallel path gen progress (if running)
}
```

### Sequence-Number Caching

The tracker uses a monotonically increasing `_changeSequence` counter. The `GetProgressResponse()` method caches the response and only rebuilds when the sequence changes, avoiding redundant serialization for polling clients.

### WebSocket Notifications

**File**: `NotificationService.cs`

Per-account WebSocket connections (`GET /api/ws?token={jwt}`) for pushing real-time events:

| Event | Trigger |
|---|---|
| `backfill_complete` | After backfill finishes for a user |
| `history_recon_complete` | After history reconstruction finishes |
| Shop updates | When item shop changes |

Connections are keyed by `(accountId, deviceId)`. Dead connections are cleaned up on send failure.

### Sub-Operation Tracking

Each phase can set a `subOperation` string for granular progress:

| Phase | Sub-Operations |
|---|---|
| Scraping | `fetching_leaderboards`, `persisting_scores`, `deep_scraping`, `persisting_to_database`, `checkpointing`, `updating_population` |
| PostScrapeEnrichment | `enriching_parallel`, `pruning_excess_entries` |
| SongMachine | `discovering_season_windows`, `processing_songs`, `completing_user_actions` |
| Finalization | `final_checkpoint`, `pre_warming_cache` |

### Path Generation (Parallel Tracker)

Path generation runs in parallel with the main scrape and has its own tracking:
- `_pathGenTotal`, `_pathGenCompleted`, `_pathGenSkipped`, `_pathGenFailed`
- `_pathGenCurrentSong` — which song is currently being processed
- Included in the `/api/progress` response as a separate `pathGeneration` object
