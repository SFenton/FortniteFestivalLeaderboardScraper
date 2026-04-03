# FSTService Architecture Patterns

> Comprehensive reference for FSTService (.NET 9 / ASP.NET Core) conventions, patterns, and decisions.
> Last audited: 2026-04-03

---

## DI Patterns

### Service Lifetimes
- **Almost everything is Singleton** — the service is long-running (BackgroundService + API). Transient is rare.
- `AddSingleton` is the default for all custom services: persistence, scraping, caching, auth, calculators.
- `AddTransient<SongProcessingMachine>` — each caller (post-scrape, backfill) creates a fresh discrete instance.
- Keyed singletons via `AddKeyedSingleton<ResponseCacheService>("PlayerCache", ...)` for per-domain caches with distinct TTLs.

### Registration Conventions
- Services registered in `Program.cs` using top-level statements (minimal API style, no Startup class).
- Registration organized by comment-delimited sections: `// ─── HTTP clients ───`, `// ─── Auth ───`, `// ─── Persistence ───`, etc.
- Interface + concrete dual registration pattern for types consumed both ways:
  ```csharp
  builder.Services.AddSingleton<IMetaDatabase>(sp => new MetaDatabase(...));
  builder.Services.AddSingleton(sp => (MetaDatabase)sp.GetRequiredService<IMetaDatabase>());
  ```
- `IHttpClientFactory` pattern via `AddHttpClient<T>()` with per-service `SocketsHttpHandler` configuration (connection pool limits, idle timeout, HTTP/2 multiplexing).
- Factory lambdas for complex construction (e.g., `SharedDopPool`, `ScrapeTimePrecomputer`, `FestivalService`).

### DI Ordering (critical)
- `StartupInitializer` registered **before** `ScraperWorker` — hosted services start in registration order.
- `AddHostedService(sp => sp.GetRequiredService<StartupInitializer>())` — resolves existing singleton instead of creating new instance.

### Keyed Services (used for caches)
| Key | Type | TTL |
|-----|------|-----|
| `"PlayerCache"` | `ResponseCacheService` | 2 min |
| `"LeaderboardAllCache"` | `ResponseCacheService` | 5 min |
| `"NeighborhoodCache"` | `ResponseCacheService` | 2 min |
| `"RivalsCache"` | `ResponseCacheService` | 5 min |
| `"LeaderboardRivalsCache"` | `ResponseCacheService` | 5 min |

Injected into endpoints via `[FromKeyedServices("KeyName")]`.

---

## Middleware Pipeline

Order in `Program.cs` after `builder.Build()`:

1. **Schema initialization** — `DatabaseInitializer.EnsureSchemaAsync()` (inline await at startup)
2. **PathTraversalGuardMiddleware** — custom, first in pipeline; rejects `..`, `%2e%2e`, etc. with 400
3. **ResponseCompression** — Brotli (optimal) + Gzip, enabled for HTTPS
4. **CORS** — `UseCors()` with configured AllowedOrigins
5. **WebSockets** — `UseWebSockets()`
6. **ForwardedHeaders** — `XForwardedFor | XForwardedProto` (reverse proxy support)
7. **RateLimiter** — `UseRateLimiter()`
8. **Authentication + Authorization** — `UseAuthentication()` → `UseAuthorization()`
9. **Static files** — `UseDefaultFiles()` + `UseStaticFiles()` (only if embedded web app detected)
10. **API endpoints** — `app.MapApiEndpoints()` (minimal APIs)
11. **SPA fallback** — `MapFallbackToFile("index.html")` (only if embedded web app)

### Rate Limiting
- Three named policies: `"public"`, `"auth"`, `"protected"` — all use same FixedWindow (100 req/s per IP).
- Global limiter also applies.
- Testing environment (`"Testing"`) uses `GetNoLimiter`.
- 429 responses include `Retry-After` header.

---

## Configuration

### Options Pattern
Three configuration classes bound via `IOptions<T>`:

| Class | Section | Purpose |
|-------|---------|---------|
| `ScraperOptions` | `"Scraper"` | Scrape interval, DOP, instrument toggles, data paths, CLI mode flags |
| `FeatureOptions` | `"Features"` | Feature flags (Shop, Rivals, Leaderboards, FirstRun, Difficulty) |
| `ApiSettings` | `"Api"` | API key, CORS origins |

- All bound via `builder.Configuration.GetSection(T.Section)`.
- CLI arguments overlay onto `ScraperOptions` via `PostConfigure<ScraperOptions>`: `--setup`, `--once`, `--api-only`, `--resolve-only`, `--backfill-only`, `--test`, `--precompute`.
- `FeatureOptions.Compete` is a derived property: `Rivals && Leaderboards`.
- Environment variables follow `__` convention: `Scraper__DegreeOfParallelism`, `Api__ApiKey`.
- `.env` file loaded manually at startup for local dev secrets (not via library).

### Kestrel
- Configured in `appsettings.json`: `"Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:8080" } } }`.

### Connection Strings
- Single: `"ConnectionStrings:PostgreSQL"` with full Npgsql options (pool size 5-50, idle 300s, timeout 30s).

---

## BackgroundService

### ScraperWorker Pattern
- Extends `BackgroundService` (single instance, registered via `AddHostedService`).
- `ExecuteAsync` wraps `RunAsync` in try/catch:
  - `OperationCanceledException` when `stoppingToken` cancelled → normal shutdown.
  - Any other exception → `LogCritical` + `_lifetime.StopApplication()`.
- `RunAsync` flow:
  1. `await _dbInitializer.WaitForReadyAsync(stoppingToken)` — waits for StartupInitializer
  2. Pre-warm rankings cache for registered users
  3. Load precomputed API responses from disk
  4. Branch based on mode flags: `--api-only`, `--setup`, `--test`, `--resolve-only`, `--backfill-only`, `--precompute`
  5. Normal mode: authenticate → scrape loop (ScrapeOrchestrator → PostScrapeOrchestrator → BackfillOrchestrator)
  6. Sleep for `ScrapeInterval` (default 4h), repeat.

### StartupInitializer Pattern
- Implements `IHostedService` + `IHealthCheck` (dual-purpose).
- `StartAsync` fires-and-forgets `InitializeInBackgroundAsync` → allows Kestrel to start immediately.
- Uses `TaskCompletionSource` for signaling readiness (`_readySignal`).
- On failure: `_readySignal.TrySetException(ex)` → `_lifetime.StopApplication()`.
- Health check wired to `/readyz` endpoint.

### Orchestrator Hierarchy
```
ScraperWorker
├── ScrapeOrchestrator       (phases 2-8: build requests, pipelined scrape, persist)
├── PostScrapeOrchestrator   (enrichment: ranks, firstSeen, nameRes, refresh, rivals, rankings, precompute)
└── BackfillOrchestrator     (queued backfills via SongProcessingMachine)
```

Each orchestrator:
- Takes dependencies via constructor DI (all singletons).
- Has a single `RunAsync(context, ct)` entry point.
- Returns typed results (e.g., `ScrapePassResult`) as explicit output contracts.
- `ScrapePassContext` is the inter-phase data contract.

---

## Error Handling

### General Pattern
- **No global exception handler middleware** — errors handled at the point they occur.
- ScraperWorker: top-level try/catch → `LogCritical` → `StopApplication()`.
- StartupInitializer: catch → `LogCritical` → `StopApplication()`.
- Individual phases: catch non-cancellation exceptions, log, continue where possible.
- `when (ex is not OperationCanceledException)` guard used pervasively in scraping code.

### API Endpoints
- Minimal API handlers return `Results.Ok(...)`, `Results.NotFound(...)`, `Results.BadRequest(...)`.
- Error responses use anonymous objects: `new { error = "message" }`.
- No standardized ProblemDetails — simple JSON error objects.
- Input validation done inline (allowlists for instrument names, path parameters).

### Resilient HTTP
- `ResilientHttpExecutor`: automatic retry with exponential backoff (500ms × 2^attempt).
- CDN 403 blocks: shared cooldown with probe-then-resume pattern.
- 429: honors `Retry-After` header.
- Network errors / timeouts: retry with backoff.
- AIMD feedback to `AdaptiveConcurrencyLimiter` for DOP adjustment.

---

## Logging

### ILogger Conventions
- Every class uses `ILogger<T>` via constructor DI.
- Structured logging with named parameters: `_log.LogInformation("Scraping {SongCount} songs...", count)`.
- Log levels used consistently:
  - `Trace` / `Debug`: detailed internal state (enabled in Development).
  - `Information`: lifecycle events, phase transitions, completion summaries.
  - `Warning`: recoverable issues (expired tokens, fallback logic, skipped items).
  - `Error`: per-request failures, HTTP errors.
  - `Critical`: unrecoverable startup/runtime failures → triggers shutdown.
- `ILoggerFactory` used when multiple loggers needed per class or for creating named loggers: `loggerFactory.CreateLogger("SharedDopPool")`.
- FestivalService (Core library) bridges via event: `service.Log += msg => log.LogInformation("[Core] {Message}", msg)`.

### Logging Configuration
```json
"Logging": {
  "LogLevel": {
    "Default": "Information",
    "Microsoft.AspNetCore": "Warning",
    "System.Net.Http.HttpClient": "Warning",
    "FSTService": "Debug",
    "FSTService.Api.ApiKeyAuthHandler": "Warning"
  }
}
```
- Console formatter: simple, UTC timestamps (`yyyy-MM-dd HH:mm:ss`).
- Development overrides: `FSTService: Trace`.

---

## Code Organization

### Folder Structure
```
FSTService/
├── Program.cs                    # DI registration, middleware pipeline, host configuration
├── ScraperWorker.cs              # BackgroundService — main scrape loop
├── StartupInitializer.cs         # IHostedService + IHealthCheck — DB/catalog init
├── FeatureOptions.cs             # Feature flag options
├── ScraperOptions.cs             # Scraper configuration options
├── ComboIds.cs                   # Bitmask-based instrument combo ID utility
├── Api/                          # HTTP endpoints + API infrastructure
│   ├── ApiEndpoints.cs           # Central routing (partial class hub)
│   ├── {Domain}Endpoints.cs      # Per-domain endpoint files (Songs, Player, Leaderboard, etc.)
│   ├── ApiKeyAuth.cs             # Auth handler + ApiSettings
│   ├── CacheHelper.cs            # ETag/304 cache helper
│   ├── ResponseCacheService.cs   # General-purpose keyed cache with ETag
│   ├── SongsCacheService.cs      # Specialized songs cache
│   ├── ShopCacheService.cs       # Specialized shop cache
│   ├── NotificationService.cs    # WebSocket push notifications
│   └── PathTraversalGuardMiddleware.cs
├── Auth/                         # Epic Games OAuth
│   ├── EpicAuthService.cs        # OAuth flows (device code, refresh, client creds)
│   ├── TokenManager.cs           # Token lifecycle (refresh, persistence, SemaphoreSlim lock)
│   ├── IDeviceAuthStore.cs       # Credential storage abstraction
│   ├── FileDeviceAuthStore.cs    # File-based credential storage
│   └── AuthModels.cs             # Token/credential DTOs
├── Persistence/                  # Data access layer
│   ├── DatabaseInitializer.cs    # DDL + schema creation (idempotent)
│   ├── IMetaDatabase.cs          # Central metadata DB interface
│   ├── MetaDatabase.cs           # PostgreSQL implementation
│   ├── IInstrumentDatabase.cs    # Per-instrument DB interface
│   ├── InstrumentDatabase.cs     # PostgreSQL implementation (partitioned tables)
│   ├── GlobalLeaderboardPersistence.cs  # Coordinator (meta + 6 instrument DBs)
│   ├── FestivalPersistence.cs    # Song catalog persistence
│   └── DataTransferObjects.cs    # All persistence DTOs
├── Scraping/                     # Scrape pipeline + processing
│   ├── ScrapeOrchestrator.cs     # Core scrape (phases 2-8)
│   ├── PostScrapeOrchestrator.cs # Post-scrape enrichment
│   ├── BackfillOrchestrator.cs   # Backfill orchestration
│   ├── GlobalLeaderboardScraper.cs   # V1/V2 API client
│   ├── ILeaderboardQuerier.cs    # Targeted lookup interface
│   ├── SongProcessingMachine.cs  # Song-parallel batch processor
│   ├── SharedDopPool.cs          # Priority-aware concurrency pool (AIMD)
│   ├── ResilientHttpExecutor.cs  # Retry + circuit breaker
│   ├── DeepScrapeCoordinator.cs  # Wave 2 breadth-first deep scrape
│   ├── ScrapeProgressTracker.cs  # Thread-safe progress reporting
│   ├── ScrapePassContext.cs      # Inter-phase data contract
│   ├── ScrapePassResult.cs       # Scrape output contract
│   ├── BatchResultProcessor.cs   # Processes batched results
│   ├── RivalsCalculator.cs       # Per-song rivals
│   ├── RivalsOrchestrator.cs     # Rivals orchestration
│   ├── LeaderboardRivalsCalculator.cs  # Leaderboard-based rivals
│   ├── RankingsCalculator.cs     # Account rankings computation
│   ├── FirstSeenSeasonCalculator.cs    # Season discovery
│   ├── AccountNameResolver.cs    # Epic display name resolution
│   ├── PostScrapeRefresher.cs    # Registered user refresh
│   ├── ScoreBackfiller.cs        # Legacy backfill
│   ├── HistoryReconstructor.cs   # Score history reconstruction
│   ├── ScrapeTimePrecomputer.cs  # API response precomputation
│   ├── PathGenerator.cs          # CHOpt path generation
│   ├── PathDataStore.cs / IPathDataStore.cs  # Path data persistence
│   └── ItemShopService.cs        # Item shop scraper
└── wwwroot/                      # Static files (embedded SPA)
```

### Namespace Conventions
- Root: `FSTService`
- Sub-namespaces: `FSTService.Api`, `FSTService.Auth`, `FSTService.Persistence`, `FSTService.Scraping`
- One namespace per folder, no deeper nesting.

### Endpoint Organization
- `ApiEndpoints` is a `partial class` — domain-specific methods in separate files.
- Each file adds one `Map{Domain}Endpoints(this WebApplication app)` extension method.
- Central `MapApiEndpoints()` calls all domain methods in order.
- Endpoints use `.WithTags("...")` for grouping and `.RequireRateLimiting("policy")`.
- Protected endpoints use `.RequireAuthorization()`.

---

## Key Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `Npgsql` | 9.0.3 | PostgreSQL driver (NpgsqlDataSource for connection pooling) |
| `Microsoft.Extensions.Http` | 9.0.0 | IHttpClientFactory, named/typed HTTP clients |
| `Microsoft.IdentityModel.JsonWebTokens` | 8.3.0 | JWT handling for Epic auth |
| `FortniteFestival.Core` | (project ref) | Shared models, FestivalService, calendar, serialization |

### Framework
- `net9.0`, `Microsoft.NET.Sdk.Web`
- `Nullable: enable`, `ImplicitUsings: enable`
- Global using: `FortniteFestival.Core.Scraping`
- `InternalsVisibleTo: FSTService.Tests`
- No third-party DI container, ORM, or heavy framework — just ASP.NET Core built-ins + Npgsql raw.

---

## Async Patterns

### CancellationToken Propagation
- `CancellationToken` passed through every async method in the scrape pipeline.
- `stoppingToken` from `BackgroundService.ExecuteAsync` is the root token.
- Guard pattern: `when (ex is not OperationCanceledException)` to differentiate normal shutdown from errors.
- `ThrowIfCancellationRequested()` called before heavy work in loops.

### Concurrency Control
- **SharedDopPool**: Priority-aware wrapper around `AdaptiveConcurrencyLimiter` (AIMD).
  - Two lanes: High (post-scrape, full DOP) and Low (backfill, capped percentage).
  - Inner limiter handles congestion-responsive DOP adjustment.
- **SemaphoreSlim**: Used for:
  - `TokenManager._lock` (1,1) — serialized token refresh.
  - `_cdnGate` in `ResilientHttpExecutor` — CDN probe serialization.
  - Song-level gates in `SongProcessingMachine` for bounding concurrent songs.
- **Channel\<T\>**: Per-instrument write channels in `GlobalLeaderboardPersistence.StartWriters()` for pipelined persistence.
- **Parallel.For**: Used sparingly for CPU-bound work (e.g., parallel per-instrument DB reads in leaderboard/all endpoint).
- **Task.WhenAll**: Used to parallelize independent phases (e.g., rivals + leaderboard rivals).

### HTTP Client Patterns
- Typed HTTP clients via `AddHttpClient<T>()` with `SocketsHttpHandler`:
  - `MaxConnectionsPerServer`: 32 (resolvers) to 2048 (scraper)
  - `PooledConnectionIdleTimeout`: 2 min
  - `PooledConnectionLifetime`: 5 min
  - `EnableMultipleHttp2Connections`: true (scraper only)
  - `AutomaticDecompression`: All
- Request messages created fresh each attempt (cannot reuse after sending).
- `ResilientHttpExecutor` wraps all outbound Epic API calls.

---

## Persistence Patterns

### Database Architecture
- **Single PostgreSQL instance** with `NpgsqlDataSource` singleton for connection pooling.
- **Partitioned tables** by instrument: `leaderboard_entries`, `song_stats`, `account_rankings`, `rank_history` — each has 6 partitions (one per instrument).
- **Logical sharding** via `InstrumentDatabase` class: each instance scoped to one instrument key, adds `WHERE instrument = @instrument` to all queries.
- `GlobalLeaderboardPersistence` coordinates 6 `InstrumentDatabase` instances + 1 `MetaDatabase`.

### Query Patterns
- Raw SQL via `NpgsqlCommand` with parameterized queries (`cmd.Parameters.AddWithValue`).
- No ORM — all queries hand-written.
- **Bulk writes**: COPY BINARY into temp table → INSERT...SELECT...ON CONFLICT (for batches >50 entries).
- **Small writes**: Prepared statement loops (for batches ≤50 entries).
- `SET LOCAL synchronous_commit = off` for scrape data (re-scrapeable, trade crash-safety for throughput).
- `ON COMMIT DROP` temp tables for staging.
- Transactions explicit where needed (`BeginTransaction()`), otherwise single-statement auto-commit.

### Schema Management
- `DatabaseInitializer.EnsureSchemaAsync()` — single DDL string with `CREATE TABLE IF NOT EXISTS`.
- All statements idempotent. No migration framework — additive-only schema changes.
- SERIAL sequences reset after COPY migration: `setval(pg_get_serial_sequence(...), MAX(id)+1)`.

### Interfaces
- `IMetaDatabase` — central metadata (scrape log, accounts, score history, backfill, rivals, stats).
- `IInstrumentDatabase` — per-instrument leaderboard entries, rankings, stats.
- `IPathDataStore` — CHOpt max scores and path data.
- Interfaces enable testing with in-memory implementations.

---

## Caching Architecture

### Three-Tier Approach
1. **Precomputed store** (`ScrapeTimePrecomputer`) — full JSON responses computed at scrape time, served instantly.
2. **Keyed ResponseCacheService** — per-domain TTL caches with SHA256-based ETags.
3. **Specialized caches** (`SongsCacheService`, `ShopCacheService`) — domain-specific with custom invalidation.

### ETag Pattern (CacheHelper)
```
1. Check precomputed store → serve if hit
2. Check ResponseCacheService → check If-None-Match → 304 or serve bytes
3. Build response → serialize to byte[] → compute SHA256 ETag → cache → serve
```
Cache-Control headers set per-endpoint: `max-age=120..1800`, `stale-while-revalidate`.

---

## Auth Architecture

### Epic Games OAuth
- **Device Code flow** for initial setup (one-time interactive browser login).
- **Refresh Token** for session persistence across restarts.
- **Client Credentials** for anonymous API calls (device code initiation).
- `TokenManager` — singleton with `SemaphoreSlim(1,1)` lock for thread-safe refresh.
- `ICredentialStore` abstraction → `FileCredentialStore` (JSON file on disk for dev).
- Refresh token expiry: ~8 hours → must re-run `--setup`.

### API Authentication
- Custom `ApiKeyAuthHandler` (ASP.NET Core `AuthenticationHandler<T>`).
- Key via `X-API-Key` header, constant-time comparison.
- Protected endpoints: `RequireAuthorization()` (admin, status, token).
- Public endpoints: `RequireRateLimiting("public")` only.

---

## Testing Architecture
- `InternalsVisibleTo: FSTService.Tests` — tests can access internal members.
- `public partial class Program { }` at bottom of Program.cs — enables `WebApplicationFactory<Program>` for integration tests.
- Testing environment detected: `builder.Environment.IsEnvironment("Testing")` → disables rate limiting.

---

## Real-Time Communication
- **WebSocket** endpoint at `/api/ws` for push notifications.
- `NotificationService` manages connections per account+device.
- Supports both authenticated (mobile) and anonymous (web) clients.
- Global broadcasts for shop rotation events.
- Per-account notifications for backfill completion.
- Circular dependency broken via `Set*` methods called at startup (not DI).
