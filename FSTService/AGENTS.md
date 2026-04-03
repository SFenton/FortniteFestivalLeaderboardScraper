# FSTService — Development Guidelines

## Stack

.NET 9.0 / C# 12+ — ASP.NET Core + BackgroundService. PostgreSQL via Npgsql. No ORM — raw SQL with parameterized queries.

## Language Conventions

- Nullable reference types enabled, implicit usings
- `async/await` throughout; `CancellationToken` propagation in all background work
- `ILogger<T>` via Microsoft.Extensions.Logging
- `System.Text.Json` for serialization
- Parameterized queries ALWAYS — never string interpolation in SQL
- File paths via `Path.Combine` and `Path.GetFullPath`

## Architecture

### Scrape Pipeline (sequential phases in ScraperWorker)

1. Auth token acquisition (EpicAuthService)
2. Song catalog sync (FestivalService)
3. Path generation (MIDI decrypt/CHOpt — parallel with scrape)
4. Global leaderboard scrape (pipelined writes to sharded DBs)
5. FirstSeenSeason calculation
6. Account name resolution
7. Post-scrape refresh (registered users)
8. Score backfill (below-60K missing scores)
9. History reconstruction (seasonal leaderboard walks)
10. Rivals calculation
11. Rankings aggregation

### Key Classes

- `ScrapeOrchestrator` / `PostScrapeOrchestrator` / `BackfillOrchestrator` — phase orchestration
- `GlobalLeaderboardScraper` — Epic API leaderboard fetching
- `MetaDatabase` — central metadata (10 tables)
- `InstrumentDatabase` — per-instrument sharded DBs (6 shards)
- `GlobalLeaderboardPersistence` — pipelined writes + change detection
- `SharedDopPool` — shared concurrency pool across phases
- `ResilientHttpExecutor` — retry/circuit-breaker HTTP

### API Layer

- Endpoint groups: Account, Admin, Leaderboard, LeaderboardRivals, Rivals, Player, Rankings, Song, Feature, Health, Diag, WebSocket
- Route convention: `/api/{resource}/{id}/{subresource}` — kebab-case
- Auth: `X-API-Key` header via ApiKeyAuth middleware
- Rate limiting: public (60/min), protected (30/min), global (200/min)
- Caching: `ResponseCacheService` with ETag + Cache-Control tiers

### Database

PostgreSQL with NpgsqlDataSource connection pooling.

- `using var conn = _ds.OpenConnection()` — always
- Transactions: `using var tx = conn.BeginTransaction()` → `tx.Commit()`
- Bulk writes: dual-path (≤50 prepared statements, >50 COPY binary import → temp table → INSERT ON CONFLICT)
- Null params: `(object?)nullable ?? DBNull.Value`
- Schema: `IF NOT EXISTS` for idempotency, async init via `EnsureSchemaAsync()`

## Testing

- **Framework**: xUnit + NSubstitute + FluentAssertions-style
- **Run**: `dotnet test FSTService.Tests\FSTService.Tests.csproj`
- **Coverage gate**: 94% line coverage (CI enforced)
- **Helpers**: `InMemoryMetaDatabase`, `TempInstrumentDatabase`, `MockHttpMessageHandler`
- **Integration**: `WebApplicationFactory<Program>` with in-memory DBs
- **Internals**: FSTService exposes via `InternalsVisibleTo`

## Configuration

`appsettings.json` under `Scraper` section:
- `ScrapeInterval` (default: 4h), `DegreeOfParallelism` (default: 512)
- `DataDirectory` (default: `data`), instrument toggles
- Feature flags in `FeatureOptions`

## CI/CD

`.github/workflows/publish-image.yml` — test → coverage gate (94%) → Docker build → push to ghcr.io. Version bumping automated per component.
