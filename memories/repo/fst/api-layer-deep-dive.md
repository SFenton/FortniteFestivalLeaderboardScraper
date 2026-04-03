# FSTService API Layer Deep Dive

> Last updated: 2026-04-03

## Endpoint Handler Catalog

All endpoints are registered as **Minimal API** handlers in partial class `ApiEndpoints` spread across domain-specific files. Every handler uses DI parameter injection directly in the lambda signature.

### Health / Infrastructure

| Route | Method | File | Auth | Rate Limit | Cache-Control | Tags |
|---|---|---|---|---|---|---|
| `/healthz` | GET | HealthEndpoints.cs | None | public | None | Health |
| `/readyz` | GET | HealthEndpoints.cs | None | N/A (MapHealthChecks) | None | Health |
| `/api/version` | GET | HealthEndpoints.cs | None | public | `public, max-age=86400` | Health |
| `/api/progress` | GET | HealthEndpoints.cs | None | public | None | Progress |

**`/healthz`** — Returns `"ok"` string. No DI deps.

**`/readyz`** — ASP.NET HealthChecks with custom status codes (200/503/503).

**`/api/version`** — DI: `HttpContext`. Returns assembly version via `AssemblyInformationalVersionAttribute`.

**`/api/progress`** — DI: `ScrapeProgressTracker`. Returns `tracker.GetProgressResponse()`.

---

### Feature Flags

| Route | Method | File | Auth | Rate Limit | Cache-Control | Tags |
|---|---|---|---|---|---|---|
| `/api/features` | GET | FeatureEndpoints.cs | None | public | None | Features |

**`/api/features`** — DI: `IOptions<FeatureOptions>`. Returns all feature flag values: `shop`, `rivals`, `compete`, `leaderboards`, `firstRun`, `difficulty`.

---

### Account / Search

| Route | Method | File | Auth | Rate Limit | Cache-Control | Tags |
|---|---|---|---|---|---|---|
| `/api/account/check` | GET | AccountEndpoints.cs | None | public | None | Account |
| `/api/account/search` | GET | AccountEndpoints.cs | None | public | `public, max-age=60` | Account |

**`/api/account/check?username={username}`** — DI: `IMetaDatabase`. Looks up account ID by username. Returns `{ exists, accountId, displayName }`.

**`/api/account/search?q={query}&limit={limit}`** — DI: `HttpContext`, `IMetaDatabase`. Autocomplete search. `limit` clamped to max 50. Returns `{ results: [{ accountId, displayName }] }`.

---

### Songs & Shop

| Route | Method | File | Auth | Rate Limit | Cache-Control | Tags |
|---|---|---|---|---|---|---|
| `/api/songs` | GET | SongEndpoints.cs | None | public | `public, max-age=1800, stale-while-revalidate=3600` | Songs |
| `/api/shop` | GET | SongEndpoints.cs | None | public | `public, max-age=300, stale-while-revalidate=600` | Shop |
| `/api/paths/{songId}/{instrument}/{difficulty}` | GET | SongEndpoints.cs | None | public | None | Paths |

**`/api/songs`** — DI: `HttpContext`, `FestivalService`, `IPathDataStore`, `IMetaDatabase`, `SongsCacheService`, `ScrapeTimePrecomputer`. ETag-cached via `SongsCacheService`. Builds enriched song list with maxScores, populationTiers, pathsGeneratedAt. Album art URLs trimmed of CDN prefix.

**`/api/shop`** — DI: `HttpContext`, `ShopCacheService`. Returns pre-cached shop data or empty response. No TTL on cache — only refreshed on shop rotation.

**`/api/paths/{songId}/{instrument}/{difficulty}`** — DI: `IOptions<ScraperOptions>`. Serves PNG path images from disk. **Validates instrument against allowlist** (6 instruments) and **difficulty against allowlist** (easy/medium/hard/expert). **Path traversal protection**: resolved path must start with `dataDir`.

---

### Leaderboards

| Route | Method | File | Auth | Rate Limit | Cache-Control | Tags |
|---|---|---|---|---|---|---|
| `/api/leaderboard/{songId}/{instrument}` | GET | LeaderboardEndpoints.cs | None | public | `public, max-age=300` | Leaderboards |
| `/api/leaderboard/{songId}/all` | GET | LeaderboardEndpoints.cs | None | public | `public, max-age=300, stale-while-revalidate=600` | Leaderboards |

**`/api/leaderboard/{songId}/{instrument}?top={n}&offset={n}&leeway={pct}`** — DI: `HttpContext`, `GlobalLeaderboardPersistence`, `IMetaDatabase`, `IPathDataStore`. Optional `leeway` applies max-score filtering. Enriches entries with display names (bulk lookup). Returns `{ songId, instrument, count, totalEntries, localEntries, entries }`.

**`/api/leaderboard/{songId}/all?top={n}&leeway={pct}`** — DI: adds `ScrapeTimePrecomputer`, `[FromKeyedServices("LeaderboardAllCache")] ResponseCacheService`. Three-tier cache: precomputed → keyed ResponseCacheService → build on demand. **Parallel instrument queries** via `Parallel.For`. Single bulk name resolution across all instruments. Returns `{ songId, instruments: [{ instrument, count, totalEntries, localEntries, entries }] }`.

---

### Player

| Route | Method | File | Auth | Rate Limit | Cache-Control | Tags |
|---|---|---|---|---|---|---|
| `/api/player/{accountId}` | GET | PlayerEndpoints.cs | None | public | `public, max-age=120, stale-while-revalidate=300` | Players |
| `/api/player/{accountId}/track` | POST | PlayerEndpoints.cs | None | public | None | Players |
| `/api/player/{accountId}/sync-status` | GET | PlayerEndpoints.cs | None | public | `public, max-age=5` | Players |
| `/api/player/{accountId}/stats` | GET | PlayerEndpoints.cs | None | public | `public, max-age=300` | Players |
| `/api/player/{accountId}/history` | GET | PlayerEndpoints.cs | None | public | `public, max-age=60` | Players |

**`/api/player/{accountId}?songId={}&instruments={}&leeway={}`** — DI: `HttpContext`, `GlobalLeaderboardPersistence`, `IMetaDatabase`, `IPathDataStore`, `ScrapeTimePrecomputer`, `[FromKeyedServices("PlayerCache")] ResponseCacheService`. Most complex endpoint. Precomputed + keyed cache. Supports instrument filtering (comma-separated). When `leeway` is provided: computes validity per score, finds `ValidScoreFallback` from history, computes filtered ranks/population. Response uses **minified field names** (`si`, `ins`, `sc`, `acc`, `fc`, `st`, etc.) for bandwidth.

**`/api/player/{accountId}/track`** (POST) — DI: `IMetaDatabase`, `FestivalService`, `ScoreBackfiller`, `HistoryReconstructor`, `NotificationService`, `TokenManager`, `SharedDopPool`, `ILoggerFactory`, `BackfillQueue`. Registers web tracking with synthetic device ID `"web-tracker"`. **Fire-and-forget** backfill + history reconstruction. Returns tracking status.

**`/api/player/{accountId}/sync-status`** — DI: `HttpContext`, `IMetaDatabase`, `ScrapeTimePrecomputer`. Returns backfill, historyRecon, and rivals status objects. Short cache (5s).

**`/api/player/{accountId}/stats`** — DI: `HttpContext`, `IMetaDatabase`, `GlobalLeaderboardPersistence`, `ScrapeTimePrecomputer`, `[FromKeyedServices("PlayerCache")] ResponseCacheService`. Returns tiered stats (precomputed `PlayerStatsTiers`) with fallback to legacy `PlayerStats`.

**`/api/player/{accountId}/history?limit={}&songId={}&instrument={}`** — DI: `HttpContext`, `IMetaDatabase`, `ScrapeTimePrecomputer`. Only available for registered users. Default limit 50,000. Precomputed for unfiltered requests.

---

### Rivals (Song-Level)

| Route | Method | File | Auth | Rate Limit | Cache-Control | Tags |
|---|---|---|---|---|---|---|
| `/api/player/{accountId}/rivals` | GET | RivalsEndpoints.cs | None | public | `public, max-age=300, stale-while-revalidate=600` | Rivals |
| `/api/player/{accountId}/rivals/suggestions` | GET | RivalsEndpoints.cs | None | public | `public, max-age=300, stale-while-revalidate=600` | Rivals |
| `/api/player/{accountId}/rivals/all` | GET | RivalsEndpoints.cs | None | public | `public, max-age=300, stale-while-revalidate=600` | Rivals |
| `/api/player/{accountId}/rivals/diagnostics` | GET | RivalsEndpoints.cs | **Yes** | protected | None | Rivals |
| `/api/player/{accountId}/rivals/{combo}` | GET | RivalsEndpoints.cs | None | public | `public, max-age=300, stale-while-revalidate=600` | Rivals |
| `/api/player/{accountId}/rivals/{combo}/{rivalId}` | GET | RivalsEndpoints.cs | None | public | `public, max-age=120, stale-while-revalidate=300` | Rivals |
| `/api/player/{accountId}/rivals/{rivalId}/songs/{instrument}` | GET | RivalsEndpoints.cs | None | public | `public, max-age=120, stale-while-revalidate=300` | Rivals |
| `/api/player/{accountId}/rivals/recompute` | POST | RivalsEndpoints.cs | **Yes** | protected | None | Rivals |

> Route registration order matters: `suggestions`, `all`, `diagnostics` are registered BEFORE `{combo}` to avoid literal values matching as combo parameter.

**`/api/player/{accountId}/rivals`** — Combo overview. DI: `HttpContext`, `IMetaDatabase`, `ScrapeTimePrecomputer`, `[FromKeyedServices("RivalsCache")]`. Returns list of combos with above/below counts.

**`/api/player/{accountId}/rivals/suggestions?combo={}&limit={}`** — DI: `HttpContext`, `IMetaDatabase`, `[FromKeyedServices("RivalsCache")]`. Batch rivals for suggestion generation. Takes top N per direction. Includes song samples. Normalizes combo via `ComboIds.NormalizeAnyComboParam`.

**`/api/player/{accountId}/rivals/all`** — DI: `HttpContext`, `IMetaDatabase`, `ScrapeTimePrecomputer`, `[FromKeyedServices("RivalsCache")]`. All combos in one call — above/below rivals per combo with display names.

**`/api/player/{accountId}/rivals/diagnostics`** — **Admin only** (RequireAuthorization). DI: `IMetaDatabase`, `RivalsCalculator`. Returns detailed diagnostic data including instrument analysis, rank breakdowns, probes.

**`/api/player/{accountId}/rivals/{combo}`** — DI: `HttpContext`, `IMetaDatabase`, `[FromKeyedServices("RivalsCache")]`. Normalizes combo (hex ID or instrument name). Returns above/below rival lists.

**`/api/player/{accountId}/rivals/{combo}/{rivalId}?limit={}&offset={}&sort={}`** — DI: adds `FestivalService`, `RivalsCalculator`. Paginated head-to-head comparison. Sort modes: `closest` (default), `they_lead`, `you_lead`. `limit=0` returns all. Includes `songsToCompete` and `yourExclusiveSongs` from `RivalsCalculator.ComputeSongGaps`.

**`/api/player/{accountId}/rivals/{rivalId}/songs/{instrument}?limit={}&offset={}&sort={}`** — Per-instrument songs without combo context. Same pagination/sorting as combo detail.

**`/api/player/{accountId}/rivals/recompute`** (POST) — **Admin only**. DI: `IMetaDatabase`, `RivalsOrchestrator`. Force recomputation.

---

### Leaderboard Rivals (Ranking-Level)

| Route | Method | File | Auth | Rate Limit | Cache-Control | Tags |
|---|---|---|---|---|---|---|
| `/api/player/{accountId}/leaderboard-rivals/{instrument}` | GET | LeaderboardRivalsEndpoints.cs | None | public | `public, max-age=300, stale-while-revalidate=600` | LeaderboardRivals |
| `/api/player/{accountId}/leaderboard-rivals/{instrument}/{rivalId}` | GET | LeaderboardRivalsEndpoints.cs | None | public | `public, max-age=300, stale-while-revalidate=600` | LeaderboardRivals |

**`/api/player/{accountId}/leaderboard-rivals/{instrument}?rankBy={}`** — DI: `HttpContext`, `IMetaDatabase`, `GlobalLeaderboardPersistence`, `ScrapeTimePrecomputer`, `[FromKeyedServices("LeaderboardRivalsCache")]`. Validates `rankBy` via `InstrumentDatabase.MapRankColumn`. Default `totalscore`. Returns above/below lists with `avgSignedDelta`, ranks.

**`/api/player/{accountId}/leaderboard-rivals/{instrument}/{rivalId}?rankBy={}&sort={}`** — DI: adds `FestivalService`, `RivalsCalculator`. Head-to-head with song gap analysis. Sort: `closest`, `they_lead`, `you_lead`.

---

### Rankings

| Route | Method | File | Auth | Rate Limit | Cache-Control | Tags |
|---|---|---|---|---|---|---|
| `/api/rankings/{instrument}` | GET | RankingsEndpoints.cs | None | public | `public, max-age=1800, stale-while-revalidate=3600` | Rankings |
| `/api/rankings/{instrument}/{accountId}` | GET | RankingsEndpoints.cs | None | public | `public, max-age=300` | Rankings |
| `/api/rankings/{instrument}/{accountId}/history` | GET | RankingsEndpoints.cs | None | public | `public, max-age=300` | Rankings |
| `/api/rankings/{instrument}/{accountId}/neighborhood` | GET | RankingsEndpoints.cs | None | public | `public, max-age=300, stale-while-revalidate=600` | Rankings |
| `/api/rankings/composite` | GET | RankingsEndpoints.cs | None | public | `public, max-age=1800, stale-while-revalidate=3600` | Rankings |
| `/api/rankings/composite/{accountId}` | GET | RankingsEndpoints.cs | None | public | `public, max-age=300` | Rankings |
| `/api/rankings/composite/{accountId}/neighborhood` | GET | RankingsEndpoints.cs | None | public | `public, max-age=300, stale-while-revalidate=600` | Rankings |
| `/api/rankings/combo` | GET | RankingsEndpoints.cs | None | public | `public, max-age=1800, stale-while-revalidate=3600` | Rankings |
| `/api/rankings/combo/{accountId}` | GET | RankingsEndpoints.cs | None | public | `public, max-age=300` | Rankings |
| `/api/rankings/overview` | GET | RankingsEndpoints.cs | None | public | `public, max-age=1800, stale-while-revalidate=3600` | Rankings |

**`/api/rankings/{instrument}?rankBy={metric}&page={}&pageSize={}`** — DI: `HttpContext`, `GlobalLeaderboardPersistence`, `IMetaDatabase`, `ScrapeTimePrecomputer`. Paginated. `pageSize` clamped [1, 200], default 50. Metrics: `adjusted` (default), `weighted`, `fcrate`, `totalscore`, `maxscorepercent`. Precomputed for page 1, size 50.

**`/api/rankings/{instrument}/{accountId}`** — DI: `HttpContext`, `GlobalLeaderboardPersistence`, `IMetaDatabase`. Single account ranking with `totalRankedAccounts`.

**`/api/rankings/{instrument}/{accountId}/history?days={}`** — DI: `HttpContext`, `GlobalLeaderboardPersistence`. Default 30 days. Returns `RankHistoryDto` array.

**`/api/rankings/{instrument}/{accountId}/neighborhood?radius={}`** — DI: `HttpContext`, `GlobalLeaderboardPersistence`, `IMetaDatabase`, `ScrapeTimePrecomputer`, `[FromKeyedServices("NeighborhoodCache")]`. Radius clamped [1, 25], default 5. Returns `{ above, self, below }` with names.

**`/api/rankings/composite?page={}&pageSize={}`** — DI: `HttpContext`, `IMetaDatabase`, `ScrapeTimePrecomputer`. Cross-instrument composite rankings. Returns per-instrument skill/rank breakdowns.

**`/api/rankings/composite/{accountId}`** — Single account composite ranking.

**`/api/rankings/composite/{accountId}/neighborhood?radius={}`** — DI: adds `[FromKeyedServices("NeighborhoodCache")]`. Composite neighborhood.

**`/api/rankings/combo?combo={}&instruments={}&rankBy={}&page={}&pageSize={}`** — DI: `HttpContext`, `IMetaDatabase`. Multi-instrument combo leaderboard. Requires ≥2 instruments. Accepts hex ID or `Solo_Guitar+Solo_Bass` format.

**`/api/rankings/combo/{accountId}?combo={}&instruments={}&rankBy={}`** — Single account combo rank.

**`/api/rankings/overview?rankBy={}&pageSize={}`** — DI: `HttpContext`, `GlobalLeaderboardPersistence`, `IMetaDatabase`, `ScrapeTimePrecomputer`. All instruments in one call. `pageSize` clamped [1, 50], default 10. Single bulk name resolution across all instruments.

---

### Admin / Protected

| Route | Method | File | Auth | Rate Limit | Cache-Control | Tags |
|---|---|---|---|---|---|---|
| `/api/status` | GET | AdminEndpoints.cs | **Yes** | protected | None | Status |
| `/api/admin/epic-token` | GET | AdminEndpoints.cs | **Yes** | protected | None | Admin |
| `/api/admin/shop/refresh` | POST | AdminEndpoints.cs | **Yes** | protected | None | Admin |
| `/api/register` | POST | AdminEndpoints.cs | **Yes** | protected | None | Registration |
| `/api/register` | DELETE | AdminEndpoints.cs | **Yes** | protected | None | Registration |
| `/api/firstseen` | GET | AdminEndpoints.cs | None | public | None | FirstSeenSeason |
| `/api/firstseen/calculate` | POST | AdminEndpoints.cs | **Yes** | protected | None | FirstSeenSeason |
| `/api/admin/regenerate-paths` | POST | AdminEndpoints.cs | **Yes** | protected | None | Paths |
| `/api/backfill/{accountId}/status` | GET | AdminEndpoints.cs | **Yes** | protected | None | Backfill |
| `/api/backfill/{accountId}` | POST | AdminEndpoints.cs | **Yes** | protected | None | Backfill |
| `/api/leaderboard-population` | GET | AdminEndpoints.cs | **Yes** | protected | None | Leaderboard |

**`/api/status`** — DI: `GlobalLeaderboardPersistence`, `IMetaDatabase`. Returns last scrape run info + per-instrument entry counts.

**`/api/admin/epic-token`** — DI: `TokenManager`. Returns access token, account ID, display name, expiry. Diagnostic use.

**`/api/admin/shop/refresh`** (POST) — DI: `ItemShopService`, `ILogger`. Triggers manual shop scrape. Returns success/count/error.

**`/api/register`** (POST) — DI: `RegisterRequest` body, `IMetaDatabase`. Body: `{ DeviceId, Username }`. Looks up account by display name. Returns registration result.

**`/api/register`** (DELETE) — DI: `string deviceId`, `string accountId` (query params), `IMetaDatabase`. Unregisters a user.

**`/api/firstseen`** — DI: `HttpContext`, `IMetaDatabase`, `ScrapeTimePrecomputer`. Public. Returns all songs' first-seen seasons. Precomputed.

**`/api/firstseen/calculate`** (POST) — DI: `FirstSeenSeasonCalculator`, `FestivalService`, `TokenManager`, `IOptions<ScraperOptions>`. Triggers calculation against Epic API.

**`/api/admin/regenerate-paths`** (POST) — DI: `PathGenerator`, `IPathDataStore`, `FestivalService`, `ScrapeProgressTracker`, `IHostApplicationLifetime`, `IOptions<ScraperOptions>`, `ILogger`. Query params: `songId?`, `force?`. **Fire-and-forget** using `ApplicationStopping` token. Returns 202 Accepted.

**`/api/backfill/{accountId}/status`** — DI: `IMetaDatabase`. Returns backfill status.

**`/api/backfill/{accountId}`** (POST) — DI: `ScoreBackfiller`, `HistoryReconstructor`, `FestivalService`, `TokenManager`, `IMetaDatabase`, `SharedDopPool`. Runs backfill + history reconstruction synchronously.

**`/api/leaderboard-population`** — DI: `IMetaDatabase`. Returns all (songId, instrument) → totalEntries.

---

### Diagnostic

| Route | Method | File | Auth | Rate Limit | Cache-Control | Tags |
|---|---|---|---|---|---|---|
| `/api/diag/events` | GET | DiagEndpoints.cs | None | public | None | Diagnostic |
| `/api/diag/leaderboard` | GET | DiagEndpoints.cs | None | public | None | Diagnostic |

**`/api/diag/events?gameId={}`** — DI: `TokenManager`, `IHttpClientFactory`. Proxies to `events-public-service-live.ol.epicgames.com`. Default gameId: `FNFestival`.

**`/api/diag/leaderboard?eventId={}&windowId={}&version={}&...`** — DI: `HttpContext`, `TokenManager`, `IHttpClientFactory`. Supports both V1 (GET) and V2 (POST) Epic leaderboard APIs. Many optional params. Returns raw Epic response wrapped with `_url` and `_status`.

---

### WebSocket

| Route | Method | File | Auth | Rate Limit | Tags |
|---|---|---|---|---|---|
| `/api/ws` | GET (upgrade) | WebSocketEndpoints.cs | Optional | None | N/A |

**`/api/ws?deviceId={}`** — DI: `HttpContext`, `NotificationService`. Accepts WebSocket upgrade. Anonymous clients get random IDs. Authenticated clients use their account info. Sends shop snapshot on connect. Receives: `shop_changed`, `shop_snapshot`, `backfill_complete`, `history_recon_complete`, `rivals_complete`.

---

## DTO Catalog

All DTOs in `FSTService/Persistence/DataTransferObjects.cs`:

### Core Score DTOs
- **`LeaderboardEntryDto`** — AccountId, Score, Rank, Accuracy, IsFullCombo, Stars, Season, Difficulty, Percentile, EndTime, ApiRank, Source
- **`PlayerScoreDto`** — SongId, Instrument + all LeaderboardEntryDto fields (minus Source)
- **`ValidScoreFallback`** — Score, Accuracy?, IsFullCombo?, Stars? (for leeway-filtered invalid scores)

### History DTOs
- **`ScoreHistoryEntry`** — SongId, Instrument, OldScore/NewScore, OldRank/NewRank, Accuracy, IsFullCombo, Stars, Difficulty, Percentile, Season, ScoreAchievedAt, SeasonRank, AllTimeRank, ChangedAt
- **`ScoreChangeRecord`** — Input DTO for bulk insertion (same fields)

### Status Tracking DTOs
- **`BackfillStatusInfo`** — AccountId, Status, SongsChecked, EntriesFound, TotalSongsToCheck, StartedAt, CompletedAt, LastResumedAt, ErrorMessage
- **`HistoryReconStatusInfo`** — AccountId, Status, SongsProcessed, TotalSongsToProcess, SeasonsQueried, HistoryEntriesFound, StartedAt, CompletedAt, ErrorMessage
- **`RivalsStatusInfo`** — AccountId, Status, CombosComputed, TotalCombosToCompute, RivalsFound, StartedAt, CompletedAt, ErrorMessage
- **`SeasonWindowInfo`** — SeasonNumber, EventId, WindowId, DiscoveredAt

### Player Stats DTOs
- **`PlayerStatsDto`** — Legacy flat stats (Instrument, SongsPlayed, FullComboCount, GoldStarCount, AvgAccuracy, BestRank, TotalScore, etc.)
- **`PlayerStatsTier`** — Tiered stats with **minified JSON property names** (`ml`, `sp`, `fcc`, `fcp`, `s6`-`s1`, `aa`, `ba`, etc.). Each tier represents a leeway breakpoint.
- **`PlayerStatsTiersRow`** — DB row: AccountId, Instrument, TiersJson (serialized array of `PlayerStatsTier`)
- **`StatsSongRef`** — SongId, Percentile (for top/bottom song lists)

### Rivals DTOs
- **`UserRivalRow`** — UserId, RivalAccountId, InstrumentCombo, Direction, RivalScore, AvgSignedDelta, SharedSongCount, AheadCount, BehindCount
- **`RivalSongSampleRow`** — UserId, RivalAccountId, Instrument, SongId, UserRank, RivalRank, RankDelta, UserScore?, RivalScore?
- **`RivalComboSummary`** — InstrumentCombo, AboveCount, BelowCount
- **`SongGapEntry`** — SongId, Instrument, Score, Rank

### Leaderboard Rivals DTOs
- **`LeaderboardRivalRow`** — UserId, RivalAccountId, Instrument, RankMethod, Direction, UserRank, RivalRank, SharedSongCount, AheadCount, BehindCount, AvgSignedDelta
- **`LeaderboardRivalSongSampleRow`** — Same as RivalSongSampleRow + RankMethod

### Rankings DTOs
- **`AccountRankingDto`** — Full ranking data: AccountId, Instrument, SongsPlayed, Coverage, RawSkillRating, AdjustedSkillRating/Rank, WeightedRating/Rank, FcRate/Rank, TotalScore/Rank, MaxScorePercent/Rank, AvgAccuracy, FullComboCount, AvgStars, BestRank, AvgRank, ComputedAt
- **`CompositeRankingDto`** — Cross-instrument: InstrumentsPlayed, TotalSongsPlayed, CompositeRating/Rank, per-instrument skill/rank
- **`ComboLeaderboardEntry`** — Rank, AccountId, AdjustedRating, WeightedRating, FcRate, TotalScore, MaxScorePercent, SongsPlayed, FullComboCount
- **`RankHistoryDto`** — SnapshotDate + all rank metrics + their values

### Other DTOs
- **`SongMaxScores`** — Per-instrument max scores (MaxLeadScore, MaxBassScore, etc.) with `GetByInstrument`/`SetByInstrument` helpers. Includes GeneratedAt, CHOptVersion.
- **`ScrapeRunInfo`** — Id, StartedAt, CompletedAt, SongsScraped, TotalEntries, TotalRequests, TotalBytes
- **`PopulationTierData`** — BaseCount + list of `PopulationTier` (Leeway, Total). In `ScrapeTimePrecomputer.cs`.
- **`PopulationTier`** — Sealed record: Leeway (double), Total (int)
- **`RankTier`** — Sealed record: Leeway (double), Rank (int) — for valid score fallback rank curves

### API Request DTOs
- **`RegisterRequest`** — DeviceId, Username (body for `POST /api/register`)

---

## Caching Mechanics

### Three-Tier Cache Architecture

Most read endpoints follow a **three-tier** cache lookup:

1. **ScrapeTimePrecomputer** (`precomputer.TryGet(key)`) — In-memory precomputed responses built after each scrape pass. Fastest tier. Keyed by canonical strings like `"player:{accountId}:::"`, `"rankings:{instrument}:{metric}:{page}:{size}"`.

2. **Keyed ResponseCacheService** (per-domain) — TTL-based `ConcurrentDictionary<string, CacheEntry>` caches. Five named instances:
   - `"PlayerCache"`: TTL 2 minutes
   - `"LeaderboardAllCache"`: TTL 5 minutes
   - `"NeighborhoodCache"`: TTL 2 minutes
   - `"RivalsCache"`: TTL 5 minutes
   - `"LeaderboardRivalsCache"`: TTL 5 minutes

3. **Build on demand** — If neither cache has the response, build it live.

### Specialized Cache Services

- **SongsCacheService** — Single-entry cache for `/api/songs`. Primed eagerly after scrape/path generation/catalog sync. TTL 5 min. Thread-safe via `lock`.
- **ShopCacheService** — Single-entry cache for `/api/shop`. **No TTL** — only refreshed when shop rotates. Primed by `ItemShopService`.

### CacheHelper (ETag Pattern)

`CacheHelper.ServeIfCached(httpContext, entry)` implements the full ETag lifecycle:

1. Takes `(byte[] Json, string ETag)?` — if null, returns null (cache miss)
2. Compares request `If-None-Match` header against cached ETag
3. If match → returns **304 Not Modified** (no body, just ETag header)
4. If no match → returns cached JSON bytes with ETag header
5. Content-Type: `application/json`

### ETag Generation

All cache services compute ETags identically:
```csharp
var hash = SHA256.HashData(json);
var etag = $"\"{Convert.ToBase64String(hash, 0, 16)}\"";
```
SHA-256 of JSON bytes → first 16 bytes → Base64 → wrapped in double quotes.

### HTTP Cache Headers

Endpoints set `Cache-Control` headers at different tiers:
- **Long (30 min)**: `/api/songs`, `/api/rankings/{instrument}`, `/api/rankings/composite`, `/api/rankings/combo`, `/api/rankings/overview` — `max-age=1800, stale-while-revalidate=3600`
- **Medium (5 min)**: `/api/leaderboard`, `/api/shop`, most rivals endpoints — `max-age=300, stale-while-revalidate=600`
- **Short (2 min)**: `/api/player/{accountId}`, rival detail — `max-age=120, stale-while-revalidate=300`
- **Very short (5s)**: `/api/player/{accountId}/sync-status` — `max-age=5`
- **Long (1 day)**: `/api/version` — `max-age=86400`

### Precomputation Strategy

`ScrapeTimePrecomputer` builds responses for:
- Registered player profiles and stats
- Sync status per player
- Score history per player
- Rivals overviews and full rival lists
- Rankings page 1 (default params) per instrument
- Composite rankings page 1
- Rankings overview
- Leaderboard rivals for registered players
- Neighborhood for registered players
- First-seen seasons
- Leaderboard "all instruments" for popular songs
- Population tiers per (songId, instrument)

Responses are stored in a `ConcurrentDictionary<string, PrecomputedResponse>` and served directly as byte arrays.

---

## Auth Mechanics

### API Key Authentication

Defined in `ApiKeyAuth.cs`:

- **Scheme**: Custom `ApiKeyAuthHandler` extending `AuthenticationHandler<ApiKeyAuthOptions>`
- **Header**: `X-API-Key`
- **Validation**: Exact ordinal string comparison against configured key
- **Configuration**: `ApiSettings.ApiKey` loaded from `Api:ApiKey` in appsettings or `Api__ApiKey` env var
- **On success**: Creates `ClaimsPrincipal` with `ClaimTypes.Name = "api-client"`
- **On failure**: Logs warning with method, path, remote IP

### Per-Endpoint Authorization

Two patterns:
1. **Public endpoints** — `.RequireRateLimiting("public")` only. No auth required. All read endpoints.
2. **Protected endpoints** — `.RequireAuthorization().RequireRateLimiting("protected")`. Requires API key. Admin/mutation operations.

### Protected Endpoints (full list)
- `GET /api/status`
- `GET /api/admin/epic-token`
- `POST /api/admin/shop/refresh`
- `POST /api/register`, `DELETE /api/register`
- `POST /api/firstseen/calculate`
- `POST /api/admin/regenerate-paths`
- `GET /api/backfill/{accountId}/status`
- `POST /api/backfill/{accountId}`
- `GET /api/leaderboard-population`
- `GET /api/player/{accountId}/rivals/diagnostics`
- `POST /api/player/{accountId}/rivals/recompute`

### Rate Limiting

Three named policies (all use same `FixedWindowRateLimiterOptions` in production):
- **`public`**: 100 requests/second per client IP, fixed window
- **`auth`**: Same config (unused in current code — may be vestigial)
- **`protected`**: Same config

Plus a **global limiter** with identical settings.

In test mode (`isTesting`), all limiters are `NoLimiter`.

429 responses include `Retry-After` header (defaults to `1` second).

### Security Middleware

**`PathTraversalGuardMiddleware`** — Registered globally. Blocks requests containing `..`, `%2e%2e`, `%2E%2E`, and mixed-case variants in path or query string. Returns 400.

### CORS

Configured via `ApiSettings.AllowedOrigins`. Default: `["http://localhost:3000"]`. Allows any header and method.

---

## Registration Pattern

### File Structure

`ApiEndpoints` is a **partial static class** split across 10 files:
- `ApiEndpoints.cs` — Entry point: `MapApiEndpoints()` extension method + `RegisterRequest` DTO
- `SongEndpoints.cs` — `MapSongEndpoints()` + `AlbumArtPrefix` constant + `TrimAlbumArt` helper
- `LeaderboardEndpoints.cs` — `MapLeaderboardEndpoints()`
- `PlayerEndpoints.cs` — `MapPlayerEndpoints()`
- `RivalsEndpoints.cs` — `MapRivalsEndpoints()` + `MapRivalSummary` helper
- `LeaderboardRivalsEndpoints.cs` — `MapLeaderboardRivalsEndpoints()` + `MapRival` helper
- `RankingsEndpoints.cs` — `MapRankingsEndpoints()`
- `AccountEndpoints.cs` — `MapAccountEndpoints()`
- `AdminEndpoints.cs` — `MapAdminEndpoints()`
- `DiagEndpoints.cs` — `MapDiagEndpoints()`
- `HealthEndpoints.cs` — `MapHealthEndpoints()`
- `FeatureEndpoints.cs` — `MapFeatureEndpoints()`
- `WebSocketEndpoints.cs` — `MapWebSocketEndpoints()`

### Registration Flow

```
Program.cs → app.MapApiEndpoints()
  → ApiEndpoints.MapApiEndpoints(WebApplication)
    → app.MapHealthEndpoints()
    → app.MapFeatureEndpoints()
    → app.MapAccountEndpoints()
    → app.MapSongEndpoints()
    → app.MapLeaderboardEndpoints()
    → app.MapPlayerEndpoints()
    → app.MapRivalsEndpoints()
    → app.MapLeaderboardRivalsEndpoints()
    → app.MapRankingsEndpoints()
    → app.MapAdminEndpoints()
    → app.MapDiagEndpoints()
    → app.MapWebSocketEndpoints()
```

### Canonical Endpoint Pattern

Every handler follows this structure:
```csharp
app.MapGet("/api/route/{param}", (
    HttpContext httpContext,
    /* route params */,
    /* query params */,
    /* DI services */) =>
{
    // 1. Set Cache-Control header
    // 2. Check precomputed cache (ScrapeTimePrecomputer)
    // 3. Check keyed ResponseCacheService
    // 4. Build response manually
    // 5. Serialize with JsonSerializer.SerializeToUtf8Bytes
    // 6. Store in keyed cache + set ETag header
    // 7. Return Results.Bytes(json, "application/json")
})
.WithTags("DomainTag")
.RequireRateLimiting("public" | "protected")
[.RequireAuthorization()];  // if protected
```

### Supporting Infrastructure Classes (in Api/)

| Class | File | Purpose |
|---|---|---|
| `ApiSettings` | ApiKeyAuth.cs | Config: API key, allowed CORS origins |
| `ApiKeyAuthHandler` | ApiKeyAuth.cs | Custom auth handler for X-API-Key |
| `ApiKeyAuthOptions` | ApiKeyAuth.cs | Options for auth scheme |
| `CacheHelper` | CacheHelper.cs | Static ETag/304 helper |
| `ResponseCacheService` | ResponseCacheService.cs | Generic keyed TTL cache (5 named instances) |
| `SongsCacheService` | SongsCacheService.cs | Singleton cache for /api/songs |
| `ShopCacheService` | ShopCacheService.cs | Singleton cache for /api/shop (no TTL) |
| `NotificationService` | NotificationService.cs | WebSocket connection manager + push notifications |
| `PathTraversalGuardMiddleware` | PathTraversalGuardMiddleware.cs | Security middleware |

### DI Service Keys

| Key | TTL | Used By |
|---|---|---|
| `"PlayerCache"` | 2 min | PlayerEndpoints (profile + stats) |
| `"LeaderboardAllCache"` | 5 min | LeaderboardEndpoints (all-instruments) |
| `"NeighborhoodCache"` | 2 min | RankingsEndpoints (neighborhood) |
| `"RivalsCache"` | 5 min | RivalsEndpoints (all rivals routes) |
| `"LeaderboardRivalsCache"` | 5 min | LeaderboardRivalsEndpoints |
