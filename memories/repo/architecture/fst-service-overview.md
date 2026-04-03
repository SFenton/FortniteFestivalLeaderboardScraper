# FSTService — Complete Domain Overview

## Service Overview

**FSTService** is a self-hosted ASP.NET Core 9.0 application that continuously scrapes Fortnite Festival leaderboard scores from Epic Games APIs and serves them via a REST API + WebSocket layer.

- **Stack**: .NET 9.0, ASP.NET Core minimal APIs, PostgreSQL (NpgsqlDataSource), BackgroundService
- **Hosting**: Kestrel on port 8080, Docker-deployable, can embed the React SPA in wwwroot/
- **Entry point**: `Program.cs` (top-level statements) + `ScraperWorker.cs` (BackgroundService)
- **Initialization**: `StartupInitializer` (IHostedService) runs before ScraperWorker — creates DB schema, loads song catalog, initializes Item Shop

---

## Scrape Pipeline

The scrape pipeline runs in a continuous loop (default 4-hour interval) orchestrated by `ScraperWorker.RunScrapePassAsync()`. It delegates to three orchestrators in sequence:

### Phase Overview

| # | Phase | Orchestrator | Description |
|---|-------|-------------|-------------|
| 1 | **Auth** | `ScraperWorker` | Ensure valid Epic access token via `TokenManager` |
| 2 | **Global Scrape** | `ScrapeOrchestrator` | V1 alltime paged scrape of all songs × 6 instruments |
| 3 | **Persist (pipelined)** | `ScrapeOrchestrator` | Per-instrument channel writers → PG bulk UPSERT |
| 4 | **Enrichment (parallel)** | `PostScrapeOrchestrator.RunEnrichmentAsync` | Rank recomputation → then pruning, FirstSeenSeason, name resolution in parallel |
| 5 | **Registered User Refresh** | `PostScrapeOrchestrator.RefreshRegisteredUsersAsync` | `SongProcessingMachine` — V2 batch lookups for registered users + backfill + history recon |
| 6 | **Rankings** | `PostScrapeOrchestrator.ComputeRankingsAsync` | `RankingsCalculator` — per-instrument + composite + combo rankings |
| 7 | **Rivals (parallel)** | `PostScrapeOrchestrator` | `RivalsOrchestrator` (per-song) + `LeaderboardRivalsCalculator` (per-ranking) in parallel |
| 8 | **Precompute** | `PostScrapeOrchestrator` | `ScrapeTimePrecomputer` — precompute JSON responses for registered players, popular pages |
| 9 | **Finalize** | `PostScrapeOrchestrator` | WAL checkpoint, rankings cache pre-warm, persist precomputed to disk |

### Detailed Phase Descriptions

**Phase 2 — Global Scrape (`ScrapeOrchestrator.RunAsync`)**
- Builds scrape requests: one per song × enabled instruments
- `GlobalLeaderboardScraper.ScrapeManySongsAsync` — pages through V1 alltime leaderboards
- Uses `SharedDopPool` with `AdaptiveConcurrencyLimiter` (AIMD) for congestion control
- Deep scrape wave 2 via `DeepScrapeCoordinator` for songs with scores exceeding CHOpt max
- Default: DOP=512, max 100 pages/leaderboard (10K entries), deep scrape target 10K valid entries

**Phase 3 — Pipelined Persistence**
- 6 per-instrument `Channel<T>` writers — zero cross-instrument contention
- Bulk UPSERT via COPY binary + merge for batches >50 entries
- Score change detection for registered users → `ScoreHistory` table
- Deferred account ID flush, WAL checkpoint after heavy writes

**Phase 4 — Enrichment**
- Rank recomputation: `InstrumentDatabase.RecomputeAllRanks()` or incremental per changed songs
- Pruning: excess entries beyond threshold removed, registered users preserved
- `FirstSeenSeasonCalculator`: determines which season each song first appeared
- `AccountNameResolver`: bulk Epic API lookup (100/request) for display names

**Phase 5 — SongProcessingMachine**
- Song-parallel batch processor: fires all songs concurrently
- Combines three user types: post-scrape refresh, pending backfill, history recon
- V2 POST batch lookups (500 accounts/request) for alltime + seasonal sessions
- `BatchResultProcessor` handles change detection, UPSERT, ScoreHistory insertion

**Phase 6 — Rankings (`RankingsCalculator`)**
- Per-instrument → composite → combo rankings
- Metrics: Adjusted Skill (Bayesian), Weighted (log₂), FC Rate, Total Score, Max Score %
- Bayesian credibility: `adjusted = (songs × raw + 50 × 0.5) / (songs + 50)`
- Daily history snapshots (top 10K + registered users)
- Combo rankings across multi-instrument combos

**Phase 7 — Rivals**
- `RivalsOrchestrator` → `RivalsCalculator`: ±50 rank neighborhoods per song, frequency+proximity weighted
- `LeaderboardRivalsCalculator`: ±N ranked neighbors in account_rankings, head-to-head song comparison
- Both run in parallel (no shared write targets)

**Phase 8 — Precompute (`ScrapeTimePrecomputer`)**
- Population tiers, player profiles, leaderboard-all pages, player sub-resources
- Stored in `ConcurrentDictionary<string, PrecomputedResponse>` (in-memory)
- Persisted to disk (compressed) for instant responses on service restart

---

## API Surface

All endpoints defined in `Api/ApiEndpoints.cs` via partial class extension methods.

### Endpoint Groups

| Group | File | Routes | Description |
|-------|------|--------|-------------|
| **Health** | `HealthEndpoints.cs` | `GET /healthz`, `GET /readyz`, `GET /api/version`, `GET /api/progress` | Liveness, readiness, version, live scrape progress |
| **Features** | `FeatureEndpoints.cs` | `GET /api/features` | Feature flags (shop, rivals, leaderboards, etc.) |
| **Account** | `AccountEndpoints.cs` | `GET /api/account/check`, `GET /api/account/search` | Username lookup, display name autocomplete |
| **Songs** | `SongEndpoints.cs` | `GET /api/songs`, `GET /api/shop`, `GET /api/paths/{songId}/{instrument}/{difficulty}` | Song catalog, item shop, path images |
| **Leaderboards** | `LeaderboardEndpoints.cs` | `GET /api/leaderboard/{songId}/{instrument}`, `GET /api/leaderboard/{songId}/all` | Per-song leaderboard with pagination, all-instruments view |
| **Player** | `PlayerEndpoints.cs` | `GET /api/player/{accountId}` | Player profile with scores, rankings, leeway filtering |
| **Rankings** | `RankingsEndpoints.cs` | `GET /api/rankings/{instrument}`, `GET /api/rankings/{instrument}/{accountId}` | Per-instrument rankings (paginated), single account ranking |
| **Rivals** | `RivalsEndpoints.cs` | `GET /api/player/{accountId}/rivals`, `GET /api/player/{accountId}/rivals/suggestions`, `GET /api/player/{accountId}/rivals/{combo}`, `GET /api/player/{accountId}/rivals/{combo}/{rivalId}` | Per-song rival overview, suggestions, combo detail, head-to-head |
| **Leaderboard Rivals** | `LeaderboardRivalsEndpoints.cs` | `GET /api/player/{accountId}/leaderboard-rivals/{instrument}`, `GET /api/player/{accountId}/leaderboard-rivals/{instrument}/{rivalId}` | Rankings-based rivals list and detail |
| **Admin** | `AdminEndpoints.cs` | `GET /api/status` (auth), `GET /api/admin/epic-token` (auth), `POST /api/admin/shop/refresh` (auth), `POST /api/register` | Status, token info, shop refresh, user registration |
| **Diagnostic** | `DiagEndpoints.cs` | `GET /api/diag/events`, `GET /api/diag/leaderboard` | Raw Epic API proxy for debugging |
| **WebSocket** | `WebSocketEndpoints.cs` | `GET /api/ws` | Real-time notifications |

### Auth & Security
- **Public endpoints**: Rate limited (100 req/s per IP, fixed window)
- **Protected endpoints** (`/api/status`, `/api/admin/*`): Require `X-API-Key` header via `ApiKeyAuthHandler`
- **Path traversal guard**: `PathTraversalGuardMiddleware` blocks `..` patterns early in pipeline
- **CORS**: Configurable allowed origins
- **Response compression**: Brotli + Gzip

---

## Persistence Layer

PostgreSQL-backed, using `NpgsqlDataSource` (connection pooling). Schema managed by `DatabaseInitializer`.

### Repositories

| Class | Interface | Responsibility |
|-------|-----------|----------------|
| **`MetaDatabase`** | `IMetaDatabase` | Central metadata: scrape_log, score_history, account_names, registered_users, backfill_status, history_recon_progress, season_windows, player_stats, first_seen_season, leaderboard_population, rivals_status, rivals, rival_song_samples, item_shop_tracks, user_sessions, leaderboard_rivals, leaderboard_rival_song_samples, combo_rankings, player_stats_tiers |
| **`InstrumentDatabase`** | `IInstrumentDatabase` | Per-instrument leaderboard entries (partitioned table), song_stats, account_rankings, rank_history. One logical instance per instrument but all share the same PG table with `WHERE instrument = @instrument`. Bulk UPSERT via COPY binary + merge. |
| **`GlobalLeaderboardPersistence`** | — | Coordinator: owns MetaDatabase + per-instrument InstrumentDatabase instances. Pipelined writers via `Channel<T>`. Entry point for ScraperWorker persistence calls. |
| **`FestivalPersistence`** | — | Song catalog persistence (in FortniteFestival.Core). Songs table UPSERT. |
| **`PathDataStore`** | `IPathDataStore` | Max scores, CHOpt data, path generation state. Stored in songs table columns. |
| **`DatabaseInitializer`** | — | Static class: creates all tables, indexes, partitions via idempotent DDL. |
| **`DataTransferObjects`** | — | All DTOs: `LeaderboardEntry`, `LeaderboardEntryDto`, `PlayerScoreDto`, `AccountRankingDto`, `RankHistoryDto`, `ScrapeRunInfo`, `ScoreHistoryEntry`, etc. |

### Key Tables

| Table | Partitioned? | Description |
|-------|-------------|-------------|
| `songs` | No | Song catalog + CHOpt max scores + path generation state |
| `leaderboard_entries` | BY LIST (instrument) → 6 partitions | Core per-song scores: PK (song_id, instrument, account_id) |
| `song_stats` | No | Per-song per-instrument aggregates (avg, median, p10, entry count) |
| `account_rankings` | No | Per-instrument player rankings (adjusted, weighted, FC rate, total score, max score %) |
| `rank_history` | No | Daily ranking snapshots |
| `scrape_log` | No | Scrape run metadata |
| `score_history` | No | Score change timeline for registered users |
| `account_names` | No | Epic display name cache |
| `registered_users` | No | User registrations (device_id → account_id) |
| `rivals` / `rival_song_samples` | No | Per-song rival computation results |
| `leaderboard_rivals` / `leaderboard_rival_song_samples` | No | Rankings-based rival results |
| `combo_rankings` | No | Multi-instrument combo rankings |
| `item_shop_tracks` | No | Currently in-shop song IDs |
| `season_windows` | No | Discovered season event/window IDs |
| `player_stats` | No | Precomputed per-instrument player statistics |
| `player_stats_tiers` | No | Leeway-tiered player stats JSON |

---

## Auth System

### Epic Games OAuth (`Auth/`)

| Class | Responsibility |
|-------|----------------|
| **`EpicAuthService`** | Handles Epic OAuth: device code flow, refresh token, client credentials. Uses `fortniteNewSwitchGameClient` (98f7e42c). |
| **`TokenManager`** | Token lifecycle: holds current token, auto-refreshes, persists refresh tokens. Exposes `GetAccessTokenAsync()` and `PerformDeviceCodeSetupAsync()`. Fires `DeviceCodeLoginRequired` event for interactive setup. |
| **`FileCredentialStore`** | `ICredentialStore` impl: persists refresh tokens to `data/device-auth.json`. |

### API Key Auth (`Api/ApiKeyAuth.cs`)

| Class | Responsibility |
|-------|----------------|
| **`ApiKeyAuthHandler`** | ASP.NET Core `AuthenticationHandler`: validates `X-API-Key` header against configured key |
| **`ApiSettings`** | Configuration: API key + allowed CORS origins |

### Auth Flow
1. First run: `--setup` → device code flow → user visits URL → access + refresh token
2. Subsequent runs: load refresh token from disk → refresh → new access/refresh tokens
3. Runtime: `TokenManager.GetAccessTokenAsync()` returns valid token, auto-refreshing ≥5 min before expiry
4. API auth: Protected endpoints check `X-API-Key` header (no user-level auth on public API)

---

## Background Processing

### ScraperWorker Lifecycle

`ScraperWorker` extends `BackgroundService`. The `RunAsync` flow:

1. **Wait for StartupInitializer** — DBs + song catalog ready
2. **Pre-warm rankings cache** — CTE queries for registered users
3. **Load precomputed responses** — from disk if available
4. **Mode dispatch**:
   - `--api-only`: Sleep forever, API only
   - `--setup`: Interactive device code auth, then exit
   - `--test "song"`: Fetch one song, then exit
   - `--resolve-only`: Resolve account names, then exit
   - `--backfill-only`: Run backfill enrichment, then exit
   - `--precompute`: Precompute API responses to disk, then exit
   - `--once`: Single scrape pass, then exit
   - Default: Continuous loop
5. **Background song sync** — Every 5 min (clock-aligned) via `FestivalService.SyncSongsAsync()`
6. **Main scrape loop** — `RunScrapePassAsync()` → sleep → repeat

### Run Modes (CLI flags)

| Flag | Effect |
|------|--------|
| `--api-only` | HTTP API only, no background scraping |
| `--setup` | Interactive Epic device code authentication |
| `--once` | Single scrape pass then exit |
| `--resolve-only` | Resolve unresolved account names then exit |
| `--backfill-only` | Backfill registered users then exit |
| `--test "query"` | Fetch leaderboard for one matching song then exit |
| `--precompute` | Precompute API responses to disk then exit |

---

## Caching Architecture

### Three-tier Cache Strategy

```
Request → Precomputed (in-memory) → Keyed Cache (TTL) → Build from DB
```

| Cache | Class | Scope | TTL | Description |
|-------|-------|-------|-----|-------------|
| **Precomputed Store** | `ScrapeTimePrecomputer` | All registered players + popular pages | Until next scrape | JSON responses prebuilt during post-scrape, <1ms serve time |
| **Songs Cache** | `SongsCacheService` | Single entry (full /api/songs) | 5 min | ETag-based, eagerly primed after scrape/catalog sync |
| **Shop Cache** | `ShopCacheService` | Single entry (full /api/shop) | No TTL (event-driven) | Updated only when shop content changes |
| **Player Cache** | `ResponseCacheService` (keyed "PlayerCache") | Per-account player profiles | 2 min | Keyed by `player:{accountId}:{songId}:{instruments}:{leeway}` |
| **Leaderboard All Cache** | `ResponseCacheService` (keyed "LeaderboardAllCache") | Per-song all-instruments | 5 min | Keyed by `lb:{songId}:{top}:{leeway}` |
| **Neighborhood Cache** | `ResponseCacheService` (keyed "NeighborhoodCache") | Per-account neighborhoods | 2 min | Rankings neighborhood responses |
| **Rivals Cache** | `ResponseCacheService` (keyed "RivalsCache") | Per-account rivals | 5 min | Overview, suggestions, detail, head-to-head |
| **Leaderboard Rivals Cache** | `ResponseCacheService` (keyed "LeaderboardRivalsCache") | Per-account leaderboard rivals | 5 min | Rankings-based rival responses |

### Cache Behavior
- All caches support **ETag**: SHA256-based content hash for conditional requests
- `CacheHelper.ServeIfCached()` handles If-None-Match → 304 responses
- Precomputed responses are persisted to disk and loaded on startup for instant first-request times
- After each scrape: `PlayerCache.InvalidateAll()`, `LeaderboardAllCache.InvalidateAll()`, `SongsCache.Prime()`

---

## WebSocket System

### NotificationService (`Api/NotificationService.cs`)

- Endpoint: `GET /api/ws` — accepts WebSocket upgrade
- Connection tracking: `ConcurrentDictionary<accountId, ConcurrentDictionary<deviceId, WebSocket>>`
- Anonymous web clients get generated IDs, authenticated mobile clients use their account/device IDs
- Supports per-account and global broadcast notifications

### Notification Types
- `shop_snapshot`: Full shop state sent on connect
- `shop_changed`: Incremental shop update (added/removed songs)
- `backfill_complete`: Backfill finished for an account
- `backfill_progress`: Progress updates during backfill
- `history_recon_complete`: History reconstruction finished

### Shop Real-time Updates
- `ItemShopService` scrapes fortnite-api.com, detects content changes via SHA256 hash
- Midnight timer triggers re-scrape at UTC day boundary (with retry logic)
- Changes broadcast to all connected WebSocket clients via `NotificationService.BroadcastAsync()`

---

## Configuration

### ScraperOptions (`Scraper:*` section)

| Key | Default | Purpose |
|-----|---------|---------|
| `ScrapeInterval` | 4 hours | Time between scrape loops |
| `DegreeOfParallelism` | 512 | Max concurrent leaderboard requests |
| `MaxRequestsPerSecond` | 0 (unlimited) | Hard RPS cap via token bucket |
| `MaxPagesPerLeaderboard` | 100 | Pages per leaderboard (100 entries/page) |
| `SongMachineDop` | 32 | Max concurrent songs in SongProcessingMachine |
| `LookupBatchSize` | 500 | Accounts per V2 batch request |
| `ValidEntryTarget` | 10,000 | Target valid entries for deep scrape |
| `OverThresholdMultiplier` | 1.05 | Deep scrape trigger: score > CHOpt × 1.05 |
| `EnablePathGeneration` | true | Auto-generate optimal paths via CHOpt |
| `DataDirectory` | "data" | Root for all data files |
| `Query{Instrument}` | all true | Which instruments to scrape |

### FeatureOptions (`Features:*` section)

| Flag | Default | Controls |
|------|---------|----------|
| `Shop` | false | Item shop UI feature |
| `Rivals` | false | Rivals pages/navigation |
| `Leaderboards` | false | Leaderboards + full rankings |
| `FirstRun` | false | First-run experience carousels |
| `Difficulty` | false | Difficulty pill on UI elements |
| `Compete` | (derived) | Rivals AND Leaderboards |

### ApiSettings (`Api:*` section)

| Key | Purpose |
|-----|---------|
| `ApiKey` | Required for protected endpoints (X-API-Key header) |
| `AllowedOrigins` | CORS allowed origins |

---

## Key Types

### Scraping Layer

| Class | Role |
|-------|------|
| `GlobalLeaderboardScraper` | V1 paged scraping + V2 batch lookups. 6 instruments, page-level parallelism. |
| `ScrapeOrchestrator` | Orchestrates core scrape: request building, pipelined persistence, progress tracking. Returns `ScrapePassResult`. |
| `PostScrapeOrchestrator` | Orchestrates post-scrape: enrichment, refresh, rankings, rivals, precompute, finalize. |
| `BackfillOrchestrator` | Orchestrates backfill: queued accounts + pending backfills via SongProcessingMachine. |
| `SongProcessingMachine` | Song-parallel batch processor. Single-use, discrete. Handles post-scrape + backfill + history recon. |
| `DeepScrapeCoordinator` | Breadth-first wave 2 deep scrape for over-threshold leaderboards. |
| `SharedDopPool` | Priority-aware DOP: high (post-scrape, full DOP) + low (backfill, gated %). Wraps `AdaptiveConcurrencyLimiter`. |
| `AdaptiveConcurrencyLimiter` | AIMD congestion control + optional RPS token bucket. |
| `ResilientHttpExecutor` | Retry with exponential backoff, CDN 403 cooldown, Retry-After, AIMD reporting. |
| `ScrapePassContext` | Data bag passed between orchestrators: token, registered IDs, aggregates. |
| `ScrapePassResult` | Output contract from core scrape: context + stats. |
| `ScrapeProgressTracker` | Thread-safe singleton: live progress for `/api/progress`. Phase enum, per-instrument counters. |
| `BatchResultProcessor` | Processes V2 batch results: change detection, UPSERT, ScoreHistory, population floor. |
| `RankingsCalculator` | Per-instrument + composite rankings with Bayesian credibility. 5 metrics. |
| `RivalsCalculator` | Per-song ±50 rank neighborhoods, frequency+proximity scoring. |
| `LeaderboardRivalsCalculator` | Rankings-based rivals across 5 rank methods. |
| `RivalsOrchestrator` | Drives rivals computation for all registered users. |
| `AccountNameResolver` | Bulk Epic display name resolution (100/request). |
| `FirstSeenSeasonCalculator` | Determines which season each song first appeared. |
| `HistoryReconstructor` | Walks seasonal leaderboards to reconstruct full score timeline. |
| `PostScrapeRefresher` | (Largely superseded by SongProcessingMachine) Refreshes stale entries for registered users. |
| `ScoreBackfiller` | (Largely superseded by SongProcessingMachine) Per-account backfill of missing entries. |
| `ItemShopService` | Scrapes fortnite-api.com for current shop, midnight timer, WebSocket broadcast. |
| `ScrapeTimePrecomputer` | Precomputes JSON responses during post-scrape. Disk persistence for instant startup. |
| `PathGenerator` | Downloads encrypted MIDI, decrypts, runs CHOpt, produces max scores + path images. |
| `PathDataStore` | Stores/retrieves CHOpt max scores and path generation state from PG. |
| `ComboIds` | Deterministic bitmask-based combo IDs for instrument combinations (6-bit, hex encoded). |
| `PlayerStatsCalculator` | Computes leeway-tiered player stats for precomputation. |

### Persistence Layer

| Class | Role |
|-------|------|
| `GlobalLeaderboardPersistence` | Coordinator: owns MetaDatabase + InstrumentDatabase instances. Pipelined Channel writers. |
| `MetaDatabase` | Central metadata PG repository (~20 tables). |
| `InstrumentDatabase` | Per-instrument leaderboard PG repository. Partitioned table. COPY binary bulk UPSERT. |
| `DatabaseInitializer` | Idempotent DDL for all tables, indexes, partitions. |
| `FestivalPersistence` | Song catalog PG repository (Core library). |

### Auth Layer

| Class | Role |
|-------|------|
| `EpicAuthService` | Epic OAuth: device code, refresh, client credentials. |
| `TokenManager` | Token lifecycle: auto-refresh, credential persistence. |
| `FileCredentialStore` | Disk-based refresh token storage. |
| `ApiKeyAuthHandler` | ASP.NET X-API-Key authentication handler. |

---

## Data Flow Diagram

```
  ┌──────────────────────────────────────────────────────────────────┐
  │                        ScraperWorker                             │
  │              (BackgroundService — main loop)                     │
  └──────┬───────────────┬──────────────────────┬───────────────────┘
         │               │                      │
    ┌────▼────┐   ┌──────▼───────┐    ┌────────▼─────────┐
    │  Auth   │   │ Song Catalog │    │ Path Generation  │
    │TokenMgr │   │ FestivalSvc  │    │  PathGenerator   │
    │EpicAuth │   │  (sync API)  │    │  MidiDecrypt     │
    └────┬────┘   └──────┬───────┘    │  CHOpt           │
         │               │            └────────┬─────────┘
         │               │                     │
  ┌──────▼───────────────▼─────────────────────▼──────────────────┐
  │                    ScrapeOrchestrator                          │
  │  GlobalLeaderboardScraper → V1 alltime pages → per-instrument │
  │  SharedDopPool (AIMD) → ResilientHttpExecutor → Epic API      │
  │  DeepScrapeCoordinator (wave 2 for over-threshold)            │
  └──────────────────────────┬────────────────────────────────────┘
                             │ Channel<T> per instrument
  ┌──────────────────────────▼────────────────────────────────────┐
  │              GlobalLeaderboardPersistence                      │
  │  ┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐     │
  │  │Guitar│ │ Bass │ │Drums │ │Vocals│ │ProGtr│ │ProBss│     │
  │  └──┬───┘ └──┬───┘ └──┬───┘ └──┬───┘ └──┬───┘ └──┬───┘     │
  │     └────────┴────────┴────────┴────────┴────────┘           │
  │            PostgreSQL (partitioned leaderboard_entries)        │
  │  + MetaDatabase (scrape_log, score_history, account_names,    │
  │    registered_users, rivals, rankings, stats, shop, etc.)     │
  └──────────────────────────┬────────────────────────────────────┘
                             │
  ┌──────────────────────────▼────────────────────────────────────┐
  │                  PostScrapeOrchestrator                        │
  │  ┌─────────────┐  ┌──────────────────┐  ┌─────────────────┐  │
  │  │ Enrichment  │  │ SongProcessingM. │  │    Rankings     │  │
  │  │ Ranks       │  │ V2 batch lookups │  │  Adjusted/Wtd   │  │
  │  │ FirstSeen   │  │ Backfill+History │  │  FC/Total/Max%  │  │
  │  │ NameResolve │  │ Score changes    │  │  Bayesian cred. │  │
  │  │ Pruning     │  │                  │  │  Combo rankings │  │
  │  └─────────────┘  └──────────────────┘  └─────────────────┘  │
  │  ┌────────────────┐  ┌──────────────────┐                    │
  │  │ Rivals (both)  │  │   Precompute     │                    │
  │  │ PerSong ±50    │  │ Player JSON      │                    │
  │  │ Leaderboard ±N │  │ Leaderboard JSON │                    │
  │  │                │  │ Population tiers  │                    │
  │  └────────────────┘  └──────────────────┘                    │
  └───────────────────────────────────────────────────────────────┘
                             │
  ┌──────────────────────────▼────────────────────────────────────┐
  │                      API Layer                                 │
  │  ┌───────────┐ ┌───────────┐ ┌────────────┐ ┌──────────────┐ │
  │  │/api/songs │ │/api/player│ │/api/leader- │ │/api/rankings │ │
  │  │/api/shop  │ │/{accountId}│ │board/{song}│ │/{instrument} │ │
  │  └───────────┘ └───────────┘ └────────────┘ └──────────────┘ │
  │  ┌───────────┐ ┌───────────┐ ┌────────────┐ ┌──────────────┐ │
  │  │/api/rivals│ │/api/ws    │ │/api/account│ │/api/progress │ │
  │  │           │ │ WebSocket │ │ /search    │ │ /status      │ │
  │  └───────────┘ └───────────┘ └────────────┘ └──────────────┘ │
  │                                                                │
  │  Cache: Precomputed → Keyed TTL → Build from DB               │
  │  Security: RateLimit → PathTraversal → CORS → ApiKeyAuth      │
  │  Compression: Brotli/Gzip                                     │
  └────────────────────────────────────────────────────────────────┘
```

---

*Last updated: 2026-04-03*
