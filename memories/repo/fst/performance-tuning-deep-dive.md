# FSTService Performance Tuning Deep Dive

> Comprehensive reference for the fst-performance agent. Covers all tunable knobs, concurrency mechanics, memory lifecycle, precomputation strategy, diagnostics, and known bottlenecks.

---

## Configuration Knobs

All settings are in `ScraperOptions.cs`, bound from `appsettings.json` under `Scraper` section. Overridable via environment variables (`Scraper__<Name>`).

### Concurrency & Throughput

| Setting | Default | Env Override | Purpose |
|---|---|---|---|
| `DegreeOfParallelism` | 16 | `Scraper__DegreeOfParallelism` | Initial/max DOP for AIMD limiter. Controls concurrent Epic API requests during scrape. |
| `MaxRequestsPerSecond` | 0 (unlimited) | `Scraper__MaxRequestsPerSecond` | Hard RPS cap via token bucket inside `AdaptiveConcurrencyLimiter`. 0 disables. |
| `LowPriorityPercent` | 20 | `Scraper__LowPriorityPercent` | % of maxDop available to low-priority callers (backfill). High-priority gets 100%. |
| `SongMachineDop` | 32 | `Scraper__SongMachineDop` | Max concurrent songs in `SongProcessingMachine`. ×6 instruments = ~192 V2 requests. |
| `PageConcurrency` | 10 | `Scraper__PageConcurrency` | Per-instrument page concurrency in sequential mode. ×6 instruments = ~60 concurrent. |
| `SongConcurrency` | 1 | `Scraper__SongConcurrency` | Songs scraped in parallel in sequential mode. Total = SongConcurrency × 6 × PageConcurrency. |
| `PathGenerationParallelism` | 4 | `Scraper__PathGenerationParallelism` | Max concurrent CHOpt processes for path generation. |
| `LookupBatchSize` | 500 | `Scraper__LookupBatchSize` | Accounts per V2 batch request. ~19KB body limit → max ~518 teams. |

### Scrape Intervals

| Setting | Default | Purpose |
|---|---|---|
| `ScrapeInterval` | 4 hours | Full scrape cycle frequency. |
| `SongSyncInterval` | 5 minutes | Re-sync song catalog from Epic calendar API. |

### Leaderboard Depth

| Setting | Default | Purpose |
|---|---|---|
| `MaxPagesPerLeaderboard` | 100 | Top 10,000 entries per song/instrument (100 entries/page). 0 = unlimited. |
| `OverThresholdMultiplier` | 1.05 | Deep scrape trigger: top score > CHOptMax × 1.05. |
| `OverThresholdExtraPages` | 100 | Deep scrape batch size (10k entries/batch). |
| `ValidEntryTarget` | 10,000 | Target valid entries (≤ CHOpt max) during deep scrape wave 2. 0 = legacy fixed-page. |

### Pipeline Tuning

| Setting | Default | Purpose |
|---|---|---|
| `BoundedChannelCapacity` | 128 | Per-instrument channel buffer for pipelined writer. Back-pressure when full. |
| `WriteBatchSize` | 10 | Items per PostgreSQL transaction in batched writer. |
| `LeaderboardRivalRadius` | 10 | Neighbors above/below for leaderboard rival computation. |

### Scrape Modes

| Setting | Purpose |
|---|---|
| `SequentialScrape` | One song at a time instead of all in parallel. Uses PageConcurrency instead of DOP. |
| `ApiOnly` | Start only HTTP API — no background scraping. |
| `RunOnce` | Single scrape pass then exit. |
| `ResolveOnly` | Only run account name resolution, then exit. |
| `BackfillOnly` | Only backfill enrichment for registered users, then exit. |
| `PrecomputeOnly` | Precompute player/leaderboard responses to disk and exit. |
| `RefreshCurrentSeasonSessions` | When true, post-scrape refresh also queries current season for sub-optimal sessions. Doubles refresh API calls. |

---

## AIMD Concurrency

### Location
- `FortniteFestival.Core/Scraping/AdaptiveConcurrencyLimiter.cs`
- Wrapped by `FSTService/Scraping/SharedDopPool.cs`

### Algorithm: TCP-Style Congestion Control
Uses a sliding window of 500 requests to evaluate error rate:

| Condition | Action |
|---|---|
| Error rate < 1% | **Additive Increase**: DOP += 16 |
| Error rate 1%–5% | **Hold**: no change (logged) |
| Error rate > 5% | **Multiplicative Decrease**: DOP × 0.75 |

DOP clamped between `minDop` (default: max(2, dop/2)) and `maxDop` (default: same as initial DOP).

### Implementation Details
- **Semaphore-based**: `SemaphoreSlim` with max capacity = maxDop
- **Increase**: Release tokens to semaphore (reclaim release debt first via CAS loop)
- **Decrease**: Non-blocking `Wait(0)` drains available tokens; unfetched tokens become **release debt** → absorbed by in-flight tasks on `Release()`
- **Thread safety**: `_evaluationLock` for window evaluation, `Interlocked` for counters, `Volatile.Read` for hot paths
- **Throughput tracking**: Window RPS, overall RPS, and in-flight count reported in log messages

### Token-Bucket Rate Limiter (embedded)
- Active only when `MaxRequestsPerSecond > 0`
- Refill interval: 50ms (20 ticks/sec)
- `tokensPerTick = max(1, maxRPS / 20)`
- Acquisition order: concurrency semaphore first, then rate-bucket semaphore
- Operates as an inner gate within `WaitAsync()`

### SharedDopPool Priority Lanes
- **High priority** (post-scrape refresh, main scrape): Direct access to inner AIMD limiter, up to 100% DOP
- **Low priority** (backfill, registration backfill): Gated by secondary `SemaphoreSlim` with `maxDop × LowPriorityPercent / 100` slots
- Low-priority callers: acquire gate first, then inner limiter. On cancellation, gate is released in catch block.
- Instantiation: `new SharedDopPool(dop, max(2, dop/2), dop, lowPriorityPercent, log, maxRPS)`

### AIMD Feedback Sources
- `ResilientHttpExecutor`: Reports success/failure after each HTTP response
- CDN 403 blocks: Reported as failures (triggers DOP decrease)
- Rate-limited 429: Reported as failures
- Network errors/timeouts: Reported as failures (retried indefinitely)
- Non-retryable status codes (400, 404): NOT reported as failures

---

## Memory Management

### Disposable Resources
| Resource | Disposal Pattern |
|---|---|
| `AdaptiveConcurrencyLimiter` | `IDisposable` — disposes rate bucket timer, rate bucket semaphore, main semaphore |
| `SharedDopPool` | `IDisposable` — disposes low-priority gate + inner limiter (if owned) |
| `SongProcessingMachine.songGate` | Local `SemaphoreSlim`, disposed in finally block |
| `ItemShopService._midnightTimer` | Timer disposed on cleanup |
| `HttpResponseMessage` | Explicitly disposed in `ResilientHttpExecutor` on all non-success paths and retry paths |

### HTTP Response Disposal in ResilientHttpExecutor
- **Success**: Caller owns response (must dispose)
- **CDN 403 (non-JSON)**: Response disposed immediately, enters CDN retry loop
- **JSON 403**: Content disposed and re-wrapped with `StringContent` so caller can read
- **429/5xx retries**: Response disposed before retry
- **Non-retryable**: Returned to caller (caller disposes)
- **Non-probe CDN waiters**: Intermediate responses disposed on continued CDN blocks

### Singleton Lifecycle
All major services registered as singletons in DI container:
- `ScrapeProgressTracker`, `SharedDopPool`, `RivalsCalculator`, `RankingsCalculator`, `ScrapeTimePrecomputer`
- `MetaDatabase`, `GlobalLeaderboardPersistence`, `PathDataStore`
- Cache services: `SongsCacheService`, `ShopCacheService`, 5× `ResponseCacheService` (keyed)
- **SongProcessingMachine**: Registered as `Transient` — new instance per use, single-use lifecycle

### In-Memory Data Stores
| Store | Type | Size Characteristics |
|---|---|---|
| `ScrapeTimePrecomputer._store` | `ConcurrentDictionary<string, PrecomputedResponse>` | All registered player profiles + leaderboard pages + sub-resources. Cleared on each scrape start. |
| `ResponseCacheService._cache` | `ConcurrentDictionary<string, CacheEntry>` | TTL-gated, per-request caching for unregistered players / non-precomputed requests |
| `SongsCacheService` | Single `byte[]?` + ETag | Full /api/songs JSON payload, 5-min TTL |
| `ShopCacheService` | Single `byte[]?` + ETag | Full /api/shop JSON, no TTL (event-driven invalidation) |

### GC Pressure Points
- `ScrapeTimePrecomputer.PrecomputeAllAsync()`: Allocates large JSON byte arrays for all registered players and leaderboard pages. Parallel.ForEach with DOP 8 for tier computation.
- Pipelined writer `List<PersistWorkItem>(batchSize)`: Reuses batch list per writer task (cleared between batches).
- `ResilientHttpExecutor.RetryCdnBlockAsync()`: String allocations for body reads on 403 detection.

---

## Precomputation

### ScrapeTimePrecomputer
Located at `FSTService/Scraping/ScrapeTimePrecomputer.cs`. Runs during post-scrape Phase 8 (Precomputing).

### What's Precomputed
| Phase | Key Pattern | Data |
|---|---|---|
| 1. Population Tiers | Internal (not stored in _store) | Per (songId, instrument) population tiers with Parallel.ForEach DOP=8 |
| 2. Player Profiles | `player:{accountId}:{instrument}:{songFilter}` | Full player profile JSON for all registered users |
| 3. Leaderboard-all Pages | `lb-all:{instrument}:{page}:{sort}` | Page 1 of each instrument × sort combination |
| 4. Player Sub-resources | `stats:{accountId}`, `history:{accountId}`, `syncstatus:{accountId}`, `rivals:{accountId}`, `rivals-all:{accountId}`, `lb-rivals:{accountId}:{instrument}:{sort}` | Stats, history, sync status, rivals overview, all rivals, leaderboard rivals |
| 5. Rankings Pages | `rankings:{instrument}:{metric}:1` | Page 1 per instrument × metric |
| 6. Neighborhoods | `neighborhood:{accountId}:{instrument}` | Leaderboard neighborhood for registered users |
| 7. Static Data | `firstseen` | First-seen season data |

### Serve Path (3-tier hierarchy)
```
Request → ScrapeTimePrecomputer.TryGet(key) [<1ms]
       → ResponseCacheService.Get(key) [<1ms, TTL-gated]
       → Build from DB [10-500ms]
```

### Cache Invalidation
- `ScrapeTimePrecomputer.InvalidateAll()`: Called at scrape start — clears all precomputed data
- Stale player entries evicted after precompute if account becomes unregistered
- `SongsCacheService.Prime()`: Eagerly rebuilt after scrape/catalog sync/path generation
- `ResponseCacheService.InvalidateAll()`: Cleared per-cache after relevant data changes

### On-Demand Single-User Precomputation
`PrecomputeUser(accountId)` — called after `/track` registration or `/api/register`. Builds profile + all sub-resources for one user without waiting for next scrape cycle.

### Disk Persistence
- `--precompute` CLI flag: Runs precomputation and writes to disk, then exits
- On startup: Loads precomputed responses from disk for instant first-request times

---

## Diagnostic Endpoints

### Health & Status
| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /healthz` | None | Simple liveness probe (returns "ok") |
| `GET /readyz` | None | ASP.NET Core health check (200 healthy, 503 unhealthy/degraded) |
| `GET /api/version` | None | Assembly version (cached 24h) |
| `GET /api/progress` | None | Live scrape progress: phase, sub-operation, songs/leaderboards/pages counters, DOP, RPS, ETA |
| `GET /api/status` | Auth (API key) | Last scrape run stats + per-instrument entry counts |

### Diagnostic / Debug
| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /api/diag/events` | None | Queries Epic FNFestival events API (pass-through) |
| `GET /api/diag/leaderboard` | None | Tests arbitrary leaderboard URL patterns (V1 GET or V2 POST) |
| `GET /api/player/{id}/rivals/diagnostics` | None | Rivals calculation diagnostics: status, combos, per-instrument breakdown, rank probes |
| `GET /api/player/{id}/sync-status` | None | Player sync status (precomputed or live) |
| `GET /api/backfill/{id}/status` | Auth | Backfill pipeline status for an account |

### ScrapeProgressTracker Details
- Thread-safe singleton, written by ScraperWorker and all scrape phases
- **Sequence-gated caching**: Response cached until `_changeSequence` increments (avoids rebuilding identical snapshots)
- Reports: adaptive DOP, in-flight requests, window RPS, overall RPS, per-instrument breakdowns
- **Phase tracking**: 14 phases (Idle → Initializing → Scraping → ... → Finalizing)
- **Sub-operation tracking**: Fine-grained within phases (e.g., "fetching_leaderboards" → "persisting_scores")
- **ETA estimation**: Linear extrapolation from completed leaderboards
- **Path generation**: Tracked in parallel with main scrape (separate counters)

### In-Code Diagnostics
- `ScraperWorker` line 192-209: DIAG V2 lookup for #1 player on every scrape pass (checks if percentile is returned)
- All scrape phases log elapsed time via `Stopwatch`
- AIMD limiter logs every DOP adjustment with error rate, window RPS, overall RPS, in-flight count

---

## PostgreSQL Tuning

### Production Configuration (deploy/docker-compose.yml)

| Parameter | Value | Purpose |
|---|---|---|
| `shared_buffers` | 2GB | Shared memory for caching table/index data |
| `work_mem` | 64MB | Per-operation sort/hash memory |
| `maintenance_work_mem` | 512MB | VACUUM, CREATE INDEX memory |
| `effective_cache_size` | 4GB | Planner hint for OS page cache size |
| `max_wal_size` | 4GB | Max WAL between checkpoints |
| `wal_buffers` | 64MB | WAL write buffering |
| `checkpoint_completion_target` | 0.9 | Spread checkpoint writes across 90% of interval |
| `shm_size` | 512mb | Docker shared memory |

### Resource Limits
- Container memory limit: **4GB**
- PostgreSQL image: `postgres:17-alpine`

### Connection Pool (Npgsql)
- Configured via connection string in env:
  - `Minimum Pool Size=5`
  - `Maximum Pool Size=50`
  - `Connection Idle Lifetime=300` (5 min)
  - `Command Timeout=30` (default)

### Extended Command Timeouts (per-query overrides)
| Operation | Timeout | File |
|---|---|---|
| Bulk merge (COPY + INSERT ON CONFLICT) | 120s | `MetaDatabase.cs` |
| Rankings UPSERT | 60s | `InstrumentDatabase.cs` |
| 4-CTE ranking query | 300s | `InstrumentDatabase.cs` |

### Write Optimization: synchronous_commit = off
- Set per-transaction in the pipelined writer: `SET LOCAL synchronous_commit = off`
- Reduces WAL flush latency for batch writes — acceptable for scrape data (can be re-scraped)

### Bulk Write Dual-Path
- **≤50 entries**: Prepared statements (`cmd.Prepare()`)
- **>50 entries**: `COPY binary import → temp table → INSERT...SELECT...ON CONFLICT`
- ~10-50x speedup for large batches

---

## HTTP Client Configuration

| Client | MaxConnections | IdleTimeout | Lifetime | Decompression | HTTP/2 |
|---|---|---|---|---|---|
| `GlobalLeaderboardScraper` | 2048 | 2 min | 5 min | All | Multi-stream |
| `AccountNameResolver` | 32 | 2 min | 5 min | All | Default |
| `HistoryReconstructor` | 32 | 2 min | 5 min | All | Default |
| `PathGenerator` | 8 | 2 min | 5 min | All | Default |
| `ItemShopService` | Default | Default | Default | All | Default |
| `EpicAuthService` | Default | Default | Default | Default | Default |

### ResilientHttpExecutor Retry Strategy
- **Transient errors** (HttpRequestException, timeout): Retry indefinitely with capped exponential backoff (500ms × 2^attempt, max 30s)
- **429 Rate Limit**: Respect `Retry-After` header, counted toward maxRetries (default 3)
- **5xx Server Error**: Exponential backoff, counted toward maxRetries
- **CDN 403 Block**: Shared cooldown with probe serialization. Schedule: 500ms → 1s → 2s → 5s → 10s → 15s → 30s → 45s → 60s → 60s∞
  - One probe request tests CDN recovery; all others wait behind cooldown timestamp
  - Non-probe requests add random jitter (0-500ms) to avoid thundering herd
  - Only CancellationToken exits the CDN retry loop

### API Rate Limiting (inbound)
- Fixed window: **100 requests/second** per IP
- Window: 1 second, QueueLimit = 0 (immediate 429)
- Three named policies (all identical): `public`, `auth`, `protected`
- Global limiter: Same fixed window
- `Retry-After` header in 429 responses (default 1s)

---

## Observed Bottlenecks

### CDN Rate Limiting
- Epic's CDN (Akamai/CloudFront) blocks with non-JSON 403 responses under heavy load
- Primary trigger: high-DOP parallel requests to V1 GET endpoints
- SongProcessingMachine introduced `songGate` (max concurrent songs) to prevent V2 POST batch lookups from triggering CDN blocks
- Sequential scrape mode (`SequentialScrape=true`) available as a fallback: one song at a time with bounded page concurrency

### Precomputation Duration
- Running `PrecomputeAllAsync()` for many registered users involves per-user DB queries × 6 instruments × multiple sub-resources
- Population tier computation parallelized (DOP=8) but each item queries the DB

### Connection Pool Saturation
- Max 50 connections shared across all concurrent operations
- During scrape: 6 pipelined writer tasks + scraper queries + API endpoint queries all compete
- Extended timeouts (120s, 300s) can hold connections for minutes during rankings computation

### Pipelined Writer Back-Pressure
- BoundedChannel capacity 128 per instrument — when channel is full, scraper tasks block on `WriteAsync`
- WriteBatchSize 10 → frequent commits; increasing reduces commit overhead but increases transaction size
- `synchronous_commit = off` mitigates WAL flush latency but doesn't help with lock contention

### In-Memory Cache Growth
- `ScrapeTimePrecomputer` stores all registered player profiles as serialized JSON byte arrays
- No eviction beyond scrape-cycle clear + unregistered-account cleanup
- Each new registered user adds: profile + stats + history + sync-status + rivals + 6× lb-rivals + 6× neighborhoods ≈ many cache entries

### V2 API Batch Size Limits
- V2 POST body limit ~19KB → max ~500 accounts per batch
- Large user bases require many batches per song × instrument → high API call count
- `SongMachineDop` (default 32) throttles song-level parallelism to avoid CDN blocks from concentrated V2 requests
