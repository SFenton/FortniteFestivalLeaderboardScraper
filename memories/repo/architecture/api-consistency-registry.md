# API Consistency Registry

> **Maintained by**: FST Principal API Designer  
> **Last updated**: 2026-04-03  
> **Source of truth for**: Route naming, response shapes, caching tiers, error handling, rate limiting

---

## Endpoint Registry

### Health & Infrastructure

| Method | Path | Handler | Auth | Cache | Rate Limit | Description |
|--------|------|---------|------|-------|------------|-------------|
| GET | `/healthz` | HealthEndpoints | None | None | public | Liveness probe → `"ok"` |
| GET | `/readyz` | HealthEndpoints (HealthChecks) | None | None | — | Readiness probe (503 if unhealthy) |
| GET | `/api/version` | HealthEndpoints | None | `max-age=86400` | public | Assembly version |
| GET | `/api/progress` | HealthEndpoints | None | None | public | Scrape progress tracker |
| GET | `/api/features` | FeatureEndpoints | None | None | public | Feature flags (shop, rivals, compete, leaderboards, firstRun, difficulty) |

### Account

| Method | Path | Handler | Auth | Cache | Rate Limit | Description |
|--------|------|---------|------|-------|------------|-------------|
| GET | `/api/account/check?username=` | AccountEndpoints | None | None | public | Check if account exists by username |
| GET | `/api/account/search?q=&limit=` | AccountEndpoints | None | `max-age=60` | public | Autocomplete display name search (limit clamped ≤50) |

### Songs & Shop

| Method | Path | Handler | Auth | Cache | Rate Limit | Description |
|--------|------|---------|------|-------|------------|-------------|
| GET | `/api/songs` | SongEndpoints | None | `max-age=1800, swv=3600` + ETag | public | Full song catalog with maxScores, populationTiers |
| GET | `/api/shop` | SongEndpoints | None | `max-age=300, swv=600` + ETag | public | Current item shop songs |
| GET | `/api/paths/{songId}/{instrument}/{difficulty}` | SongEndpoints | None | None | public | Path image (PNG file) |

### Leaderboards

| Method | Path | Handler | Auth | Cache | Rate Limit | Description |
|--------|------|---------|------|-------|------------|-------------|
| GET | `/api/leaderboard/{songId}/{instrument}?top=&offset=&leeway=` | LeaderboardEndpoints | None | `max-age=300` | public | Single instrument leaderboard (paginated) |
| GET | `/api/leaderboard/{songId}/all?top=&leeway=` | LeaderboardEndpoints | None | `max-age=300, swv=600` + ETag | public | All instruments for a song |

### Players

| Method | Path | Handler | Auth | Cache | Rate Limit | Description |
|--------|------|---------|------|-------|------------|-------------|
| GET | `/api/player/{accountId}?songId=&instruments=&leeway=` | PlayerEndpoints | None | `max-age=120, swv=300` + ETag | public | Player profile with all scores |
| POST | `/api/player/{accountId}/track` | PlayerEndpoints | None | None | public | Start tracking + trigger backfill |
| GET | `/api/player/{accountId}/sync-status` | PlayerEndpoints | None | `max-age=5` | public | Backfill/history-recon/rivals status |
| GET | `/api/player/{accountId}/stats` | PlayerEndpoints | None | `max-age=300` + ETag | public | Player stats tiers per instrument |
| GET | `/api/player/{accountId}/history?limit=&songId=&instrument=` | PlayerEndpoints | None | `max-age=60` | public | Score history (registered users only) |

### Rivals (Neighborhood-Based)

| Method | Path | Handler | Auth | Cache | Rate Limit | Description |
|--------|------|---------|------|-------|------------|-------------|
| GET | `/api/player/{accountId}/rivals` | RivalsEndpoints | None | `max-age=300, swv=600` + ETag | public | Combo overview (above/below counts) |
| GET | `/api/player/{accountId}/rivals/suggestions?combo=&limit=` | RivalsEndpoints | None | `max-age=300, swv=600` + ETag | public | Batch rivals for suggestion generation |
| GET | `/api/player/{accountId}/rivals/all` | RivalsEndpoints | None | `max-age=300, swv=600` + ETag | public | All combos in one call |
| GET | `/api/player/{accountId}/rivals/diagnostics` | RivalsEndpoints | API Key | None | protected | Rivals computation diagnostics |
| GET | `/api/player/{accountId}/rivals/{combo}` | RivalsEndpoints | None | `max-age=300, swv=600` + ETag | public | Rival list for a specific combo |
| GET | `/api/player/{accountId}/rivals/{combo}/{rivalId}?limit=&offset=&sort=` | RivalsEndpoints | None | `max-age=120, swv=300` + ETag | public | Detailed song-by-song comparison (paginated) |
| GET | `/api/player/{accountId}/rivals/{rivalId}/songs/{instrument}?limit=&offset=&sort=` | RivalsEndpoints | None | `max-age=120, swv=300` + ETag | public | Per-instrument songs for a rival |
| POST | `/api/player/{accountId}/rivals/recompute` | RivalsEndpoints | API Key | None | protected | Force rivals recomputation |

### Leaderboard Rivals

| Method | Path | Handler | Auth | Cache | Rate Limit | Description |
|--------|------|---------|------|-------|------------|-------------|
| GET | `/api/player/{accountId}/leaderboard-rivals/{instrument}?rankBy=` | LeaderboardRivalsEndpoints | None | `max-age=300, swv=600` + ETag | public | Leaderboard rivals list per instrument |
| GET | `/api/player/{accountId}/leaderboard-rivals/{instrument}/{rivalId}?rankBy=&sort=` | LeaderboardRivalsEndpoints | None | `max-age=300, swv=600` + ETag | public | Leaderboard rival head-to-head detail |

### Rankings

| Method | Path | Handler | Auth | Cache | Rate Limit | Description |
|--------|------|---------|------|-------|------------|-------------|
| GET | `/api/rankings/overview?rankBy=&pageSize=` | RankingsEndpoints | None | `max-age=1800, swv=3600` | public | Batch: all instruments top N |
| GET | `/api/rankings/composite?page=&pageSize=` | RankingsEndpoints | None | `max-age=1800, swv=3600` | public | Composite rankings (paginated) |
| GET | `/api/rankings/composite/{accountId}` | RankingsEndpoints | None | `max-age=300` | public | Single account composite ranking |
| GET | `/api/rankings/composite/{accountId}/neighborhood?radius=` | RankingsEndpoints | None | `max-age=300, swv=600` + ETag | public | Composite ranking neighborhood |
| GET | `/api/rankings/combo?combo=&instruments=&rankBy=&page=&pageSize=` | RankingsEndpoints | None | `max-age=1800, swv=3600` | public | Combo leaderboard (paginated) |
| GET | `/api/rankings/combo/{accountId}?combo=&instruments=&rankBy=` | RankingsEndpoints | None | `max-age=300` | public | Single account combo rank |
| GET | `/api/rankings/{instrument}?rankBy=&page=&pageSize=` | RankingsEndpoints | None | `max-age=1800, swv=3600` | public | Per-instrument rankings (paginated) |
| GET | `/api/rankings/{instrument}/{accountId}` | RankingsEndpoints | None | `max-age=300` | public | Single account per-instrument ranking |
| GET | `/api/rankings/{instrument}/{accountId}/history?days=` | RankingsEndpoints | None | `max-age=300` | public | Rank history (default 30 days) |
| GET | `/api/rankings/{instrument}/{accountId}/neighborhood?radius=` | RankingsEndpoints | None | `max-age=300, swv=600` + ETag | public | Ranking neighborhood (above/self/below) |

### Admin & Registration (Protected)

| Method | Path | Handler | Auth | Cache | Rate Limit | Description |
|--------|------|---------|------|-------|------------|-------------|
| GET | `/api/status` | AdminEndpoints | API Key | None | protected | Last scrape run + instrument entry counts |
| GET | `/api/admin/epic-token` | AdminEndpoints | API Key | None | protected | Get current Epic access token |
| POST | `/api/admin/shop/refresh` | AdminEndpoints | API Key | None | protected | Trigger shop scrape |
| POST | `/api/register` | AdminEndpoints | API Key | None | protected | Register device/user (body: deviceId, username) |
| DELETE | `/api/register?deviceId=&accountId=` | AdminEndpoints | API Key | None | protected | Unregister device/user |
| GET | `/api/firstseen` | AdminEndpoints | None | None | public | All first-seen seasons |
| POST | `/api/firstseen/calculate` | AdminEndpoints | API Key | None | protected | Calculate first-seen seasons |
| POST | `/api/admin/regenerate-paths?songId=&force=` | AdminEndpoints | API Key | None | protected | Regenerate path images → 202 Accepted |
| GET | `/api/backfill/{accountId}/status` | AdminEndpoints | API Key | None | protected | Backfill status for account |
| POST | `/api/backfill/{accountId}` | AdminEndpoints | API Key | None | protected | Trigger full backfill + history recon |
| GET | `/api/leaderboard-population` | AdminEndpoints | API Key | None | protected | All leaderboard population counts |

### Diagnostic (Proxy)

| Method | Path | Handler | Auth | Cache | Rate Limit | Description |
|--------|------|---------|------|-------|------------|-------------|
| GET | `/api/diag/events?gameId=` | DiagEndpoints | None | None | public | Proxy Epic Festival events API |
| GET | `/api/diag/leaderboard?eventId=&windowId=&version=&...` | DiagEndpoints | None | None | public | Proxy Epic leaderboard API (v1/v2) |

### WebSocket

| Method | Path | Handler | Auth | Cache | Rate Limit | Description |
|--------|------|---------|------|-------|------------|-------------|
| GET | `/api/ws` | WebSocketEndpoints | Optional | None | None | Real-time notifications (shop, backfill, etc.) |

---

## Response Format Decision Tree

```
Is this a cached dataset that changes infrequently?
├── YES → Serialize to byte[] → Results.Bytes() + ETag + Cache-Control
│         (songs, leaderboards, rivals, rankings, player profile, neighborhoods)
├── Is this a small/fresh response?
│   ├── YES → Results.Ok(new { ... })
│   │         (features, account/check, status, progress, sync-status, register, backfill/status)
├── Is this a file?
│   ├── YES → Results.File(path, mimeType)
│   │         (path images)
├── Is this a long-running operation?
│   ├── YES → Results.Accepted(value: { ... })
│   │         (regenerate-paths)
├── Is this a proxied external API?
│   ├── YES → Results.Content(body, "application/json")
│   │         (diag/events, diag/leaderboard)
└── Error → See Error Response Contract below
```

**Rule**: Within an endpoint group, do NOT mix `Results.Bytes()` and `Results.Ok()` for successful responses of the same shape. Choose one based on data size and cacheability.

---

## Caching Strategy

### Cache-Control Tiers

| Tier | max-age | stale-while-revalidate | Applies to |
|------|---------|------------------------|------------|
| **volatile** | 5–60s | — | sync-status (5s), account/search (60s), history (60s) |
| **standard** | 120s | 300s | player profile, rival detail, rival songs |
| **stable** | 300s | 600s | single-instrument leaderboards, single rankings, rival overview, rivals/{combo}, leaderboard-rivals, shop |
| **static** | 1800s | 3600s | songs catalog, rankings (page 1), composite, combo, overview |
| **permanent** | 86400s | — | version |

### Application-Level Caches (In-Memory)

| Cache Service | Registration Key | TTL | ETag | Strategy |
|---------------|-----------------|-----|------|----------|
| `SongsCacheService` | Singleton | 5 min | SHA256 | Event-driven prime + on-demand fallback |
| `ShopCacheService` | Singleton | ∞ (no TTL) | SHA256 | Event-driven only (shop rotation) |
| `ResponseCacheService` | `"PlayerCache"` | 2 min | SHA256 | Key per `player:{accountId}:{params}` |
| `ResponseCacheService` | `"LeaderboardAllCache"` | 5 min | SHA256 | Key per `lb:{songId}:{top}:{leeway}` |
| `ResponseCacheService` | `"NeighborhoodCache"` | 2 min | SHA256 | Key per `neighborhood:{instrument}:{accountId}:{radius}` |
| `ResponseCacheService` | `"RivalsCache"` | 5 min | SHA256 | Multiple key patterns per endpoint |
| `ResponseCacheService` | `"LeaderboardRivalsCache"` | 5 min | SHA256 | Key per `lb-rivals:{accountId}:{instrument}:{rankBy}` |
| `ScrapeTimePrecomputer` | Singleton | Startup precompute | SHA256 | Precomputed on boot, served from memory |

### ETag / Conditional Request Pattern

All `Results.Bytes()` endpoints follow this pattern via `CacheHelper.ServeIfCached()`:

1. Check precomputed store (`ScrapeTimePrecomputer.TryGet()`)
2. Check keyed `ResponseCacheService` (or dedicated `SongsCacheService`/`ShopCacheService`)
3. If cache hit: compare `If-None-Match` → 304 or serve cached bytes + ETag
4. If cache miss: build payload → `JsonSerializer.SerializeToUtf8Bytes()` → store → set ETag → `Results.Bytes()`

ETag format: `"<base64(SHA256(json)[0:16])>"` (quoted, 24-char base64 prefix of SHA256)

### Cache Key Naming Convention

```
{domain}:{primaryId}:{secondaryId}:{params...}
```

Examples:
- `player:{accountId}:{songId}:{instruments}:{leeway}`
- `lb:{songId}:{top}:{leeway}`
- `rivals-overview:{accountId}`
- `rivals-all:{accountId}`
- `overview:{accountId}`, `all:{accountId}`, `list:{accountId}:{combo}`
- `detail:{accountId}:{combo}:{rivalId}:{limit}:{offset}:{sort}`
- `neighborhood:{instrument}:{accountId}:{radius}`
- `playerstats:{accountId}`
- `syncstatus:{accountId}`
- `history:{accountId}`
- `lb-rivals:{accountId}:{instrument}:{rankBy}`

---

## Rate Limiting

### Configuration

All rate limit policies use **identical** Fixed Window configuration:

| Parameter | Value |
|-----------|-------|
| Window | 1 second |
| PermitLimit | 100 requests |
| QueueLimit | 0 (no queuing) |
| Partition key | Client IP address |

### Policy Names

| Policy | Used by | Notes |
|--------|---------|-------|
| `public` | All public GET endpoints | 100/sec per IP |
| `auth` | Defined but currently unused | 100/sec per IP (same config) |
| `protected` | Admin/protected endpoints | 100/sec per IP (same config) |
| **GlobalLimiter** | All requests | 100/sec per IP (same config) |

> **Note**: All three policies are currently identical (100 req/sec per IP). The policy names serve as semantic markers for future differentiation.

### Rejection Behavior

- Status: `429 Too Many Requests`
- Header: `Retry-After: {seconds}` (from metadata, default 1s)
- Testing environment: All rate limits disabled (`GetNoLimiter`)

---

## Error Response Contract

### Standard Error Shapes

| Status | Shape | Usage |
|--------|-------|-------|
| 400 | `{ "error": "Human-readable validation message" }` | Invalid parameters, missing required fields |
| 404 | `{ "error": "Resource description not found" }` | Account/instrument/song not found |
| 500 | `Results.Problem("description")` → ProblemDetails | Server-side failures (missing tokens, empty catalogs) |
| 429 | (empty body) + `Retry-After` header | Rate limit exceeded |
| 502 | `{ "success": false, "error": "message", "scrapedAt": "..." }` | Upstream Epic API failure (shop/refresh only) |

### Error Examples

```json
// 400 — Missing param
{ "error": "username query parameter is required." }

// 400 — Invalid param
{ "error": "Invalid instrument name." }
{ "error": "At least two instruments required. Use 'combo' (hex ID) or 'instruments' (e.g. Solo_Guitar+Solo_Bass)." }

// 404 — Not found
{ "error": "Unknown account." }
{ "error": "No rivals found for this combo." }
{ "error": "Account not found in rankings for this instrument." }
{ "error": "Score history is only available for registered users." }

// 404 — Soft failure (register)
{ "registered": false, "error": "no_account_found", "description": "No Epic Games account was found..." }
```

> **Exception**: `POST /api/register` returns 200 with `{ registered: false, error: "no_account_found" }` rather than 404. This is a legacy exception.

---

## Auth Patterns

### Mechanism

- **Scheme**: Custom `ApiKeyAuthHandler` registered as `"ApiKey"`
- **Header**: `X-API-Key: {secret}`
- **Configuration**: `Api__ApiKey` env var or `Api:ApiKey` in appsettings.json

### Endpoint Protection Levels

| Level | Fluent API | Endpoints |
|-------|-----------|-----------|
| **Public** | `.RequireRateLimiting("public")` | All read-only data endpoints |
| **Protected** | `.RequireAuthorization().RequireRateLimiting("protected")` | Admin, registration, backfill trigger, rivals recompute, diagnostics requiring auth |
| **WebSocket** | Neither | `/api/ws` — optional auth via claims, anonymous allowed |

### CORS

- Origins: Configured via `Api:AllowedOrigins` array (default: `http://localhost:3000`)
- Methods: Any
- Headers: Any

---

## Query Parameter Conventions

### Naming

| Convention | Examples |
|------------|---------|
| camelCase | `songId`, `accountId`, `rankBy`, `pageSize` |
| Abbreviations allowed for well-known params | `q` (search query), `top` (limit alias) |

### Pagination

| Parameter | Default | Max | Used by |
|-----------|---------|-----|---------|
| `page` | 1 | — | Rankings (1-indexed) |
| `pageSize` | 50 | 200 | Rankings (`Math.Clamp`) |
| `top` | 10 | — | Leaderboards |
| `offset` | 0 | — | Leaderboards, rival detail |
| `limit` | Varies (5, 50, 50000) | — | Rivals suggestions, rival detail, history |

> **Note**: `limit=0` means "all" in rival detail endpoints. History defaults to 50000.

### Filtering

| Parameter | Type | Used by |
|-----------|------|---------|
| `songId` | string | Player profile filter, history filter |
| `instruments` | comma-separated | Player profile filter (e.g. `Solo_Guitar,Solo_Bass`) |
| `instrument` | string | History filter, single instrument |
| `leeway` | double (percentage) | Score validity threshold (e.g. `15.0` = 15% over max) |
| `combo` | hex ID or `+`-joined instruments | Combo rankings, rivals |
| `rankBy` | string enum | Rankings sort metric: `adjusted`, `weighted`, `fcrate`, `totalscore`, `maxscorepercent` |
| `sort` | string enum | Rival songs sort: `closest`, `they_lead`, `you_lead` |
| `days` | int | Rank history window (default 30) |
| `radius` | int | Neighborhood radius (default 5, clamped 1–25) |

### Instrument Identifiers

Instruments are referenced in two forms:
- **Full name**: `Solo_Guitar`, `Solo_Bass`, `Solo_Drums`, `Solo_Vocals`, `Solo_PeripheralGuitar`, `Solo_PeripheralBass`
- **Hex combo ID**: `01` (guitar), `02` (bass), etc. via `ComboIds` utility

`ComboIds.NormalizeAnyComboParam()` accepts either form and normalizes to hex.

---

## Response Envelope

### No standard envelope. Responses are endpoint-specific.

Common patterns observed:

**Collection responses** (Results.Ok):
```json
{
  "count": 42,
  "songs": [...]
}
```

**Paginated responses** (Results.Ok):
```json
{
  "instrument": "Solo_Guitar",
  "rankBy": "adjusted",
  "page": 1,
  "pageSize": 50,
  "totalAccounts": 1234,
  "entries": [...]
}
```

**Player profile** (Results.Bytes):
```json
{
  "accountId": "...",
  "displayName": "...",
  "totalScores": 500,
  "scores": [...]
}
```

**Neighborhood** (Results.Bytes):
```json
{
  "instrument": "Solo_Guitar",
  "accountId": "...",
  "rank": 42,
  "above": [...],
  "self": { ... },
  "below": [...]
}
```

**Status/tracking** (Results.Ok):
```json
{
  "accountId": "...",
  "trackingStarted": true,
  "backfillStatus": "pending",
  "backfillKicked": true
}
```

### Player Score DTO (Compact)

Player profile scores use shortened keys for bandwidth:
```json
{
  "si": "songId",
  "ins": "01",
  "sc": 999999,
  "acc": 100,
  "fc": true,
  "st": 6,
  "dif": "Expert",
  "sn": 6,
  "pct": 99.5,
  "rk": 1,
  "et": "2024-01-01T00:00:00Z",
  "te": 5000
}
```

> **Note**: This compact format is unique to the player profile endpoint. All other endpoints use full property names.

---

## Middleware Pipeline

Order of execution (top to bottom):

1. **PathTraversalGuardMiddleware** — Rejects `..`, `%2e%2e` etc. in path and query
2. **ResponseCompression** — Brotli (Optimal) + Gzip for HTTPS
3. **CORS** — Configured origins, any method/header
4. **WebSockets** — Required before WS endpoint mapping
5. **ForwardedHeaders** — X-Forwarded-For + X-Forwarded-Proto (reverse proxy support)
6. **RateLimiter** — Per-IP fixed window
7. **Authentication** — ApiKey scheme
8. **Authorization** — Enforces `RequireAuthorization()`
9. **DefaultFiles + StaticFiles** — Only if embedded web app exists
10. **API Endpoints** — `MapApiEndpoints()`
11. **SPA Fallback** — `MapFallbackToFile("index.html")`

---

## Serialization

- **Global JSON options**: `JsonIgnoreCondition.WhenWritingNull` — null properties omitted
- **All Results.Bytes() endpoints**: Use `JsonSerializer.SerializeToUtf8Bytes()` with shared options
- **ETag computation**: `SHA256.HashData(json)` → base64 first 16 bytes → quoted string

---

## Conventions Summary

### Route Naming
- Base: `/api/{resource}` or `/api/{resource}/{id}`
- Sub-resources: `/api/player/{accountId}/rivals/{combo}/{rivalId}`
- Actions: POST to resource path (e.g. `/api/player/{accountId}/track`, `/api/player/{accountId}/rivals/recompute`)
- Admin: `/api/admin/{action}` or `/api/{resource}` with RequireAuthorization()
- Health: `/healthz`, `/readyz` (no `/api` prefix)

### Known Inconsistencies
1. **Route registration ambiguity**: Rivals endpoints register `/api/player/{accountId}/rivals/suggestions`, `/api/player/{accountId}/rivals/all`, `/api/player/{accountId}/rivals/diagnostics` BEFORE `/{combo}` to prevent string params matching literal routes
2. **Register endpoint**: Uses `POST /api/register` and `DELETE /api/register` (not under `/api/admin/`)
3. **Leaderboard population**: `GET /api/leaderboard-population` uses kebab-case hyphen (only endpoint with this style)
4. **Player scores DTO**: Uses compact 2-3 char keys (`si`, `ins`, `sc`) unlike all other DTOs
5. **Soft 404 on register**: Returns 200 with `registered: false` instead of 404
6. **firstseen**: `GET /api/firstseen` is public but `POST /api/firstseen/calculate` is protected — asymmetric auth
7. **Diag endpoints**: No auth required despite being developer-focused proxy endpoints
8. **Rate limit differentiation**: All three policies (public, auth, protected) have identical 100/sec limits
