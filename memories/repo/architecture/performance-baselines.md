# Performance Baselines & Patterns

> Comprehensive inventory of performance-related configurations, patterns, and bottlenecks across FSTService and FortniteFestivalWeb. Last updated: 2026-04-03.

---

## Scrape Performance

### AIMD Concurrency Model (`AdaptiveConcurrencyLimiter`)
- **Algorithm**: TCP-style Additive Increase / Multiplicative Decrease
- **Evaluation window**: 500 requests
- **Additive increase**: +16 DOP (when error rate < 1%)
- **Multiplicative decrease**: ×0.75 DOP (when error rate > 5%)
- **Hold zone**: 1%–5% error rate → no change
- **Token-bucket RPS**: Refills every 50ms (20 ticks/sec), configurable via `MaxRequestsPerSecond` (0 = unlimited)
- **Release debt**: CAS loop absorbs tokens from in-flight tasks when DOP decreases mid-flight

### SharedDopPool (Priority Lanes)
- **High priority** (main scrape, post-scrape refresh): Full DOP access
- **Low priority** (backfill, registration): Gated by secondary semaphore at `LowPriorityPercent` of maxDop
- Default `LowPriorityPercent`: **20%**
- Instantiated in Program.cs: `initialDop = DegreeOfParallelism`, `minDop = max(2, dop/2)`, `maxDop = dop`

### Key Scraper Options (defaults)
| Setting | Default | Notes |
|---|---|---|
| `DegreeOfParallelism` | 16 | Max concurrent leaderboard requests |
| `MaxRequestsPerSecond` | 0 (unlimited) | Hard RPS cap via token bucket |
| `ScrapeInterval` | 4 hours | Full scrape cycle |
| `SongSyncInterval` | 5 minutes | Catalog re-sync from Epic |
| `MaxPagesPerLeaderboard` | 100 | Top 10,000 entries per song/instrument |
| `OverThresholdExtraPages` | 100 | Deep scrape batch size (10k entries/batch) |
| `ValidEntryTarget` | 10,000 | Target valid entries for deep scrape |
| `SongMachineDop` | 32 | Max concurrent songs in SongProcessingMachine (×6 instruments = ~192 V2 requests) |
| `LookupBatchSize` | 500 | Accounts per V2 batch request (~19KB body limit) |
| `PageConcurrency` | 10 | Per-instrument page concurrency in sequential mode |
| `SongConcurrency` | 1 | Songs scraped in parallel in sequential mode |
| `PathGenerationParallelism` | 4 | Max concurrent CHOpt processes |

### ResilientHttpExecutor
- Automatic retry with **exponential backoff** (500ms × 2^attempt)
- CDN 403 cooldown with probe serialization (`_cdnGate`)
- `Retry-After` header respect
- Reports success/failure to AIMD limiter after each request

### HTTP Client Configuration
| Client | MaxConnectionsPerServer | PooledConnectionIdleTimeout | PooledConnectionLifetime |
|---|---|---|---|
| `GlobalLeaderboardScraper` | 2048 | 2 min | 5 min |
| `AccountNameResolver` | 32 | 2 min | 5 min |
- Both: `EnableMultipleHttp2Connections = true`, `AutomaticDecompression = All`

---

## Database Performance

### Connection Pool (PostgreSQL via Npgsql)
- **Min Pool Size**: 5
- **Max Pool Size**: 50
- **Connection Idle Lifetime**: 300s
- **Default Command Timeout**: 30s

### Extended Command Timeouts
| Operation | Timeout | Location |
|---|---|---|
| Bulk merge (COPY + INSERT ON CONFLICT) | 120s | `MetaDatabase.cs:177, 386` |
| Rankings UPSERT | 60s | `InstrumentDatabase.cs:532` |
| 4-CTE ranking query (`ComputeAccountRankings`) | 300s | `InstrumentDatabase.cs:650` |

### Bulk Write Dual-Path Pattern
- **≤50 entries**: Prepared statements with `cmd.Prepare()`
- **>50 entries**: COPY binary import → temp table → `INSERT...SELECT...ON CONFLICT`
- ~10-50x faster than loop path for large batches

### Pipelined Writer Architecture
- Per-instrument `BoundedChannel<PersistWorkItem>` (capacity: `BoundedChannelCapacity` = **128**)
- `FullMode = BoundedChannelFullMode.Wait` → back-pressure when channel full
- `WriteBatchSize` = **10** items per PostgreSQL transaction
- `RunBatchedWriterAsync`: drains channel reader, batches into single transactions

---

## API Response Performance

### 3-Tier Cache Hierarchy
```
Request → Precomputed (in-memory) → Keyed Cache (TTL) → Build from DB
```

### Tier 1: Precomputed Store (`ScrapeTimePrecomputer`)
- Full JSON responses prebuilt during post-scrape Phase 8
- Stored in `ConcurrentDictionary<string, PrecomputedResponse>` (in-memory)
- Persisted to disk → loaded on startup for instant first-request times
- **Serve time**: <1ms
- Covers: All registered players, popular pages, `/api/firstseen`

### Tier 2: Keyed Response Caches
| Cache Key | Service | TTL | ETag |
|---|---|---|---|
| `"PlayerCache"` | `ResponseCacheService` | 2 min | SHA256 |
| `"LeaderboardAllCache"` | `ResponseCacheService` | 5 min | SHA256 |
| `"NeighborhoodCache"` | `ResponseCacheService` | 2 min | SHA256 |
| `"RivalsCache"` | `ResponseCacheService` | 5 min | SHA256 |
| `"LeaderboardRivalsCache"` | `ResponseCacheService` | 5 min | SHA256 |

### Tier 3: Specialized Caches
| Cache | TTL | Notes |
|---|---|---|
| `SongsCacheService` | 5 min | Single entry (full /api/songs), eagerly primed after scrape/catalog sync |
| `ShopCacheService` | Custom | Shop-specific invalidation |

### ETag / Conditional Response
- All caches compute SHA256-based ETags
- Endpoints check `If-None-Match` header → return **304 Not Modified** if match
- Bytes served directly from cache (pre-serialized JSON)

### API Rate Limiting
- **Fixed window**: 100 requests/second per IP
- **Window**: 1 second, `QueueLimit = 0` (no queuing — immediate 429)
- **Policies**: `public`, `auth`, `protected` (all use same fixed window)
- **Global limiter**: Same fixed window policy
- `Retry-After` header: Included in 429 responses (default 1s)

### Response Compression
- **Brotli** (Optimal level) + **Gzip** enabled
- `EnableForHttps = true`

---

## Web Bundle Performance

### Code Splitting (Route-Level Lazy Loading)
14 lazy-loaded page routes via `React.lazy()` + `Suspense`:
- `SongDetailPage`, `LeaderboardPage`, `PlayerHistoryPage`, `PlayerPage`
- `SuggestionsPage`, `SettingsPage`, `ShopPage`
- `RivalsPage`, `RivalDetailPage`, `RivalCategoryPage`, `AllRivalsPage`
- `LeaderboardsOverviewPage`, `FullRankingsPage`, `CompetePage`
- Wrapped in `<Suspense fallback={<SuspenseFallback />}>`

### Vite Build Configuration
- Output: `FSTService/wwwroot` (served directly by ASP.NET Core)
- No explicit `rollupOptions.manualChunks` — relies on Vite's default chunk splitting
- No explicit `build.chunkSizeWarningLimit` override
- Aliases resolve `@festival/core`, `@festival/theme`, `@festival/ui-utils` from workspace packages (tree-shakeable source imports)

### Missing Optimizations
- No `rollupOptions.output.manualChunks` for vendor splitting (react, recharts, tanstack, etc.)
- No explicit `build.target` — defaults to Vite's ESM baseline
- No `cssCodeSplit: true` (default in Vite, likely active)

---

## Render Performance

### Memoization Patterns
- **`useMemo`**: Heavily used in contexts (`FabSearchContext`, `ShopContext`, `SettingsContext`) and data pages
- **`useCallback`**: All context action registrations use `useCallback` with stable deps
- **Context values**: All context providers wrap their value objects in `useMemo` (prevents unnecessary re-renders)
- **No `React.memo` wrappers found** on page components — context changes propagate to all consumers

### Virtualization
- **Songs page**: `@tanstack/react-virtual` (`useVirtualizer`) for the main song list
- **Suggestions page**: `react-infinite-scroll-component` for progressive loading
- Other list pages (leaderboards, player, rivals): No virtualization detected

### Image Handling
- Album art images: `loading="lazy"` attribute used on `<img>` tags
- Instrument icons: `loading="lazy"` attribute
- No WebP/AVIF conversion pipeline
- No responsive `srcSet` usage detected

---

## Network Performance

### Nginx (Production Docker)
- **Gzip**: Enabled on text/css/js/json/xml/svg (min 256 bytes)
- **Static asset cache**: `expires 1y; Cache-Control: public, immutable` for hashed assets (js/css/images/fonts)
- **SPA fallback**: `try_files $uri $uri/ /index.html`
- **API proxy**: Reverse proxy to backend with WebSocket upgrade support

### ASP.NET Core Compression
- Brotli (Optimal) + Gzip for API responses
- `EnableForHttps = true`

### No CDN Layer
- Direct nginx → ASP.NET Core — no edge caching

---

## Known Bottlenecks

### Database
1. **`ComputeAccountRankings`**: 4-CTE ranking query needs 300s timeout — most expensive single query
2. **Bulk merge** at 120s timeout: Large song catalogs can produce heavy temp table operations
3. **Max Pool Size 50**: May become contention point during concurrent scrape + API serving

### Scraping
4. **CDN 403 blocks**: When full DOP is used for V2 POST batch lookups, CDN blocks trigger → `SongMachineDop` capped at 32 to mitigate
5. **Sequential deep scrape**: Wave 2 extension fetches run in batches, adding latency to scrape completion

### Web Rendering
6. **No virtualization on leaderboard/player/rivals lists**: Large lists rendered entirely in DOM
7. **Context propagation**: `useMemo` on context values but no `React.memo` on consumers — filter/sort changes re-render full page trees
8. **Single-bundle vendor code**: React, Recharts, react-router all in one chunk — large initial download

---

## Optimization Opportunities

### Low-Hanging Fruit
1. **Vite manual chunks**: Split `react`/`react-dom`, `recharts`, `@tanstack/*` into separate vendor chunks → better cache hit rate
2. **Add `React.memo`** to song row / leaderboard row components used in long lists
3. **Virtualize leaderboard and player lists** with `@tanstack/react-virtual` (already a dependency)
4. **WebP album art**: Convert album art URLs to WebP where Epic CDN supports it
5. **Add `build.target: 'es2022'`** in Vite config for smaller output (native async/await, etc.)

### Medium Effort
6. **Partial precomputation warmup**: Precompute responses can be warmed incrementally instead of all-at-once during post-scrape
7. **Connection pool tuning**: Consider raising Max Pool Size to 100 during scrape peak, or use separate data sources for API vs scrape
8. **Query plan review**: `ComputeAccountRankings` 4-CTE query may benefit from materialized views or incremental maintenance
9. **Stale-while-revalidate cache pattern**: Serve stale cache entries while refreshing in background (eliminates cold-start latency for expired entries)

### Architecture Level
10. **CDN/Edge caching**: Add CloudFlare or similar for static assets and cacheable API responses (songs, leaderboard snapshots)
11. **Read replica**: Offload API read queries to a PG read replica during scrape to avoid lock contention
12. **Streaming SSR / RSC**: For SEO-critical pages (song detail, leaderboards) — currently pure SPA
