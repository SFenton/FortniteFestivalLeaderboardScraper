# Copilot Instructions — Fortnite Festival Score Tracker

This document provides project context for AI coding assistants working in this repository.
For detailed designs, refer to the docs in the `docs/` folder (listed below).

## Project Overview

**Fortnite Festival Score Tracker (FST)** tracks Fortnite Festival leaderboard scores across all seasons, instruments, and songs. The Fortnite Festival leaderboards reset every season, so this system continuously scrapes Epic's APIs to build a persistent historical record.

The repo contains three main components:

| Component | Path | Stack |
|---|---|---|
| **FSTService** | `FSTService/` | .NET 9.0 / C# — ASP.NET Core + BackgroundService |
| **FortniteFestival.Core** | `FortniteFestival.Core/` | .NET shared library (multi-targets net472 + net9.0) |
| **FortniteFestivalRN** | `FortniteFestivalRN/` | React Native mobile app (TypeScript) |

## Architecture

### FSTService

A self-hosted ASP.NET Core application that runs as both an HTTP API server and a background scraper. It uses `ScraperWorker` (a `BackgroundService`) to orchestrate scraping, and exposes API endpoints for data queries, user registration, and backfill triggers.

**Run modes** (set via CLI args, parsed in `Program.cs`):
- Normal: continuous scrape loop with configurable interval
- `--api-only`: no background scraping; API serves requests only (used for local dev/testing)
- `--setup`: interactive device code auth, then exit
- `--test "Song"`: scrape a single song across all instruments, print results
- `--resolve-only`: resolve unresolved account names, then exit
- `--once`: single scrape pass + resolve, then exit

**Scrape pass phases** (executed sequentially in ScraperWorker):
1. Auth token acquisition (Epic device auth)
2. Song catalog sync (via FestivalService)
3. Path generation — runs in parallel with scrape (download MIDI .dat, decrypt, CHOpt → max scores + path images)
4. Global leaderboard scrape (pipelined writes to sharded DBs)
5. FirstSeenSeason calculation
6. Account name resolution (Epic API)
7. Personal DB rebuild (for changed accounts)
8. Post-scrape refresh (registered users' stale entries)
9. Backfill missing scores (LookupAccountAsync for below-60K scores)
10. History reconstruction (seasonal leaderboard walks)
11. Expired session cleanup

### Key Source Layout

```
FSTService/
  Program.cs              — DI setup, middleware pipeline, CLI arg parsing
  ScraperWorker.cs        — BackgroundService orchestrating scrape lifecycle
  ScraperOptions.cs       — Configuration POCO (intervals, DOP, paths, run modes)
  Api/
    ApiEndpoints.cs       — All HTTP route mappings (public + protected)
    ApiKeyAuth.cs         — X-API-Key header auth handler + ApiSettings
    AuthEndpoints.cs      — User auth routes (login, refresh, logout)
    BearerTokenAuthHandler.cs — JWT Bearer validation
    PathTraversalGuardMiddleware.cs — Security middleware
  Auth/
    EpicAuthService.cs    — Epic Games OAuth (device auth, device code, token refresh)
    TokenManager.cs       — Manages Epic access token lifecycle
    JwtTokenService.cs    — Issues/validates JWT tokens for mobile users
    UserAuthService.cs    — User registration, login, session management
    FileDeviceAuthStore.cs — Persists Epic device credentials to disk
  Persistence/
    MetaDatabase.cs       — Central SQLite metadata DB (fst-meta.db)
    InstrumentDatabase.cs — Per-instrument sharded SQLite DBs (fst-{instrument}.db)
    GlobalLeaderboardPersistence.cs — Pipelined writes to instrument DBs + change detection
    PersonalDbBuilder.cs  — Builds per-user/device SQLite DBs for mobile sync
    DataTransferObjects.cs — DTOs (ScoreHistoryEntry, etc.)
  Scraping/
    GlobalLeaderboardScraper.cs    — Fetches leaderboard data from Epic API
    HistoryReconstructor.cs        — Walks seasonal leaderboards for score history
    ScoreBackfiller.cs             — Fills missing scores via LookupAccountAsync
    PostScrapeRefresher.cs         — Refreshes stale entries for registered users
    AccountNameResolver.cs         — Resolves account IDs → display names
    FirstSeenSeasonCalculator.cs   — Determines when songs first appeared
    AdaptiveConcurrencyLimiter.cs  — Dynamic parallelism based on error rates
    BackfillQueue.cs               — Queues users for backfill processing
    ScrapeProgressTracker.cs       — Live progress exposed to API
    ResilientHttpExecutor.cs       — Retry/circuit-breaker HTTP wrapper
    PathGenerator.cs               — Orchestrates MIDI download/decrypt/CHOpt for max scores
    MidiCryptor.cs                 — AES-ECB decrypt/encrypt for .dat ↔ .mid
    MidiTrackRenamer.cs            — Produces instrument-specific MIDI variants for CHOpt
    PathDataStore.cs               — Reads/writes max scores + .dat hashes in Songs DB
```

### FortniteFestival.Core

Shared library consumed by both FSTService and the legacy MAUI app. Multi-targets net472 (for MAUI/WPF) and net9.0 (for FSTService).

- `Services/FestivalService.cs` — Song catalog sync from Epic's calendar API
- `Models/` — Song models, leaderboard models, calendar models
- `Config/InstrumentType.cs` — Instrument enum (Lead, Bass, Drums, Vocals, ProLead, ProBass)
- `Persistence/SqlitePersistence.cs` — Song catalog persistence

### FortniteFestivalRN (React Native Mobile App)

React Native app for iOS/Android/Windows. Uses yarn workspaces monorepo with packages:
- `@festival/core` — shared types, API client
- `@festival/contexts` — React contexts
- `@festival/ui` — shared UI components
- `@festival/local-app` — offline/local mode
- `@festival/server-app` — server-connected mode

## Database Architecture

All databases are SQLite, stored under `FSTService/data/`.

### Meta Database (`fst-meta.db`)

Central metadata store managed by `MetaDatabase.cs`. Contains 11 tables:

| Table | Purpose |
|---|---|
| `ScrapeLog` | Tracks each scrape run (start/end time, counts) |
| `ScoreHistory` | Per-user score change history (song, instrument, old/new score, timestamp, accuracy, FC, stars, SeasonRank, AllTimeRank) |
| `AccountNames` | Maps Epic account IDs → display names |
| `RegisteredUsers` | Users registered for personal tracking |
| `UserSessions` | JWT auth sessions (refresh tokens, device IDs, platform) |
| `BackfillStatus` | Per-user backfill state machine |
| `BackfillProgress` | Detailed backfill progress per song/instrument |
| `HistoryReconStatus` | Per-user history reconstruction state |
| `HistoryReconProgress` | Per-season history reconstruction progress |
| `SeasonWindows` | Cached season start/end dates |
| `SongFirstSeenSeason` | Which season each song first appeared |

**ScoreHistory deduplication**: Uses a unique index on `(AccountId, SongId, Instrument, NewScore, ScoreAchievedAt)` with `ON CONFLICT DO UPDATE` to merge SeasonRank and AllTimeRank from different sources via COALESCE.

**Rank semantics**:
- `SeasonRank` — Populated by HistoryReconstructor (seasonal leaderboard queries). Note: Epic returns final season rank, not point-in-time rank.
- `AllTimeRank` — Populated by ScoreBackfiller, PostScrapeRefresher, and GlobalLeaderboardPersistence (alltime leaderboard queries). Represents rank at lookup time.

### Instrument Databases (`fst-{instrument}.db`)

One SQLite DB per instrument, managed by `InstrumentDatabase.cs`. Contains `LeaderboardEntries` table with columns: `AccountId`, `SongId`, `Score`, `Rank`, `Percentile`, `Accuracy`, `IsFullCombo`, `Stars`, `HighScoreSeason`, `BestRunTotalScore`.

Instruments: `Solo_Guitar`, `Solo_Bass`, `Solo_Drums`, `Solo_Vocals`, `Pro_Guitar`, `Pro_Bass`.

**Concurrency**: `InstrumentDatabase.UpsertEntries` uses a `_writeLock` to prevent nested SQLite transactions during parallel backfill operations.

### Personal Databases (`data/personal/{accountId}/{deviceId}.db`)

Per-user/device SQLite databases built by `PersonalDbBuilder.cs` for mobile app sync. Contains copies of relevant data from the global databases (songs, scores, score history).

### Core Song Database (`fst-service.db`)

Managed by `FortniteFestival.Core/Persistence/SqlitePersistence.cs`. Song catalog from Epic's calendar API.

## Testing

- **Framework**: xUnit + NSubstitute (mocking) + FluentAssertions-style assertions
- **Coverage**: 446 tests (25 unit test files, 3 integration test files)
- **Test project**: `FSTService.Tests/`
- **Run**: `dotnet test FSTService.Tests\FSTService.Tests.csproj`
- **Visibility**: FSTService exposes internals via `InternalsVisibleTo`

When writing tests:
- Use `NSubstitute` for mocking interfaces and abstract classes
- Integration tests use `WebApplicationFactory<Program>` with in-memory databases
- Test files mirror source structure: `MetaDatabaseTests.cs` tests `MetaDatabase.cs`, etc.
- Prefer testing through public API surface; use internal access only when needed

## CI / CD

The GitHub Actions workflow lives at `.github/workflows/publish-image.yml` and runs on pushes/PRs to `master` when FSTService, Core, or test files change.

**Pipeline stages:**
1. **Test** — Restore → Build (Release) → `dotnet test` with `XPlat Code Coverage` (Cobertura format)
2. **Coverage gate** — Parses the Cobertura XML for the `FSTService` package's `line-rate` and fails the build if coverage drops below the threshold (currently **95%**). The threshold is set via the `COVERAGE_THRESHOLD` env var in the workflow.
3. **Build & push Docker image** — Only on `master` pushes (not PRs). Builds `FSTService/Dockerfile` and pushes to `ghcr.io`.

**Coverage notes:**
- Coverage is collected only for `[FSTService]*` (excludes `FortniteFestival.Core` and test assemblies).
- The coverage report is uploaded as a workflow artifact (`test-results`).
- When adding new code, ensure tests maintain coverage above the threshold. If the threshold needs adjusting, update the `COVERAGE_THRESHOLD` env var in the workflow file.

## API

**Base URL**: `http://localhost:8080`

**Authentication schemes**:
- `X-API-Key` header — for admin/protected endpoints (key in `appsettings.json`)
- `Authorization: Bearer {jwt}` — for user-specific endpoints

**Key endpoints** (see `ApiEndpoints.cs` for full list):
- `GET /healthz` — health check (public)
- `GET /api/progress` — live scrape progress (public)
- `GET /api/songs` — song catalog (public)
- `POST /api/register` — register a user for tracking (API key)
- `POST /api/backfill/{accountId}` — trigger score backfill (API key)
- `GET /api/personal/{accountId}/{deviceId}` — download personal DB (Bearer)
- `POST /api/auth/login` — user login (public, rate-limited)
- `POST /api/auth/refresh` — refresh JWT (public, rate-limited)

**Rate limiting**: Fixed-window rate limiters per endpoint category (public: 60/min, auth: 10/min, protected: 30/min, global: 200/min).

## Configuration

All in `appsettings.json` under the `Scraper` section:
- `ScrapeInterval` — time between scrape passes (default: 4 hours)
- `DegreeOfParallelism` — concurrent Epic API requests (default: 512)
- `DataDirectory` — SQLite storage path (default: `data`)
- `QueryLead/Drums/Vocals/Bass/ProLead/ProBass` — instrument toggles

## Development Conventions

- **Language**: C# 12+ with nullable reference types enabled, implicit usings
- **Dependencies**: Minimal — no EF Core, no heavy ORMs. Raw SQL via Microsoft.Data.Sqlite.
- **JSON**: `System.Text.Json` in FSTService, `Newtonsoft.Json` in Core (legacy compat)
- **Async**: Prefer `async/await` throughout; `CancellationToken` propagation in all background work
- **Logging**: `ILogger<T>` via Microsoft.Extensions.Logging
- **Error handling**: `HttpErrorHelper` for Epic API error parsing; resilient HTTP via `ResilientHttpExecutor`
- **SQL**: Use parameterized queries everywhere, never string interpolation in SQL
- **File paths**: Use `Path.Combine` and `Path.GetFullPath`; paths are relative to `DataDirectory`

## Design Documents

Detailed designs live in the `docs/` folder. These are the source of truth for feature architecture:

### Database (`docs/database/`)

| Document | Topic |
|---|---|
| [docs/database/FSTServiceDatabaseDesign.md](../docs/database/FSTServiceDatabaseDesign.md) | Database sharding architecture, table schemas, data flow, Docker deployment, HTTP API layer, security model |

### Feature Designs (`docs/design/`)

| Document | Topic |
|---|---|
| [docs/design/EpicLoginDesign.md](../docs/design/EpicLoginDesign.md) | Epic Games OAuth flow (Authorization Code + PKCE), dual app modes, JWT token design, user sessions |
| [docs/design/UserDeviceRegistrationDesign.md](../docs/design/UserDeviceRegistrationDesign.md) | Simplified username-based registration, JWT auth, device management |
| [docs/design/UserRegistrationBackfillDesign.md](../docs/design/UserRegistrationBackfillDesign.md) | Score backfill pipeline, stale entry detection, history reconstruction, season window scanning |
| [docs/design/OverallRankingsDesign.md](../docs/design/OverallRankingsDesign.md) | Per-instrument aggregate rankings (Skill/Overall/Weighted/Coverage), composite rankings |
| [docs/design/OppsFeatureDesign.md](../docs/design/OppsFeatureDesign.md) | "Opps" (rivals) feature — neighborhood-based matching algorithm |
| [docs/design/ReactNativeCleanupPlan.md](../docs/design/ReactNativeCleanupPlan.md) | Mobile app refactoring plan — deduplication, shared packages, architecture cleanup |

## Registered Test Accounts

For reference during development and testing:

| Username | Account ID | Device ID |
|---|---|---|
| SFentonX | `195e93ef108143b2975ee46662d4d0e1` | `test-device-001` |
| captainparticles | `cb8ebb19b32c40d1a736d7f8efec17ac` | `test-device-002` |
| kahnyri | `4c2a1300df4c49a9b9d2b352d704bdf0` | `test-device-003` |

## Docker

`docker-compose.yml` at repo root and `deploy/docker-compose.yml` for deployment. FSTService has a `Dockerfile` for containerized operation.
