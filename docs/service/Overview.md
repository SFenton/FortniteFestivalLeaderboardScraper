# FSTService — Architecture Overview

FSTService is a self-hosted ASP.NET Core application that serves as both an HTTP API server and a continuous background scraper for Fortnite Festival leaderboard data. It is built on .NET 9.0 and uses raw SQLite (via `Microsoft.Data.Sqlite`) for persistence — no ORM.

## High-Level Architecture

```
┌──────────────────────────────────────────────────────────┐
│                      FSTService                          │
│                                                          │
│  ┌──────────────┐    ┌──────────────────────────────┐    │
│  │  ASP.NET Core │    │   ScraperWorker               │    │
│  │  HTTP API     │    │   (BackgroundService)         │    │
│  │              │    │                              │    │
│  │  • Public    │    │  1. Auth token acquisition   │    │
│  │  • Protected │    │  2. Song catalog sync        │    │
│  │  • Auth      │    │  3. Global leaderboard scrape│    │
│  │  • Diagnostic│    │  4. FirstSeenSeason calc     │    │
│  │              │    │  5. Account name resolution  │    │
│  └──────┬───────┘    │  6. Personal DB rebuild      │    │
│         │            │  7. Post-scrape refresh      │    │
│         │            │  8. Score backfill           │    │
│         │            │  9. History reconstruction   │    │
│         │            │ 10. Session cleanup          │    │
│         │            └──────────────┬───────────────┘    │
│         │                           │                    │
│         └─────────┬─────────────────┘                    │
│                   ▼                                      │
│  ┌──────────────────────────────────────────────────┐    │
│  │              Persistence Layer                    │    │
│  │                                                  │    │
│  │  fst-meta.db           fst-{instrument}.db (×6)  │    │
│  │  fst-service.db        personal/{acct}/{dev}.db  │    │
│  └──────────────────────────────────────────────────┘    │
│                                                          │
│  ┌──────────────────────────────────────────────────┐    │
│  │              External APIs                        │    │
│  │                                                  │    │
│  │  Epic Games OAuth     Epic Leaderboard APIs      │    │
│  │  Epic Account API     Epic Calendar/Events API   │    │
│  └──────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────┘
```

## Run Modes

FSTService supports several operational modes, controlled via CLI arguments parsed in `Program.cs`. Each mode configures `ScraperOptions` accordingly.

| Mode | CLI Argument | Behavior |
|---|---|---|
| **Normal** | *(none)* | Continuous scrape loop on a configurable interval (default: 4 hours). API is active. |
| **API Only** | `--api-only` | No background scraping. API serves requests from existing data. Useful for local development/testing. |
| **Setup** | `--setup` | Interactive Epic device code authentication flow. Prints a URL + code for the user to authorize in a browser, then saves credentials and exits. |
| **Test** | `--test "Song Name"` | Scrapes a single song (or comma-separated songs) across all instruments, prints results, and exits. Good for verifying API connectivity. |
| **Resolve Only** | `--resolve-only` | Resolves unresolved Epic account IDs to display names using the stored meta DB, then exits. No scraping. |
| **Once** | `--once` | Runs a single full scrape pass + account name resolution, then exits. Useful for cron-based scheduling or one-off data refreshes. |

## Component Map

### Source Layout

```
FSTService/
├── Program.cs                          DI setup, middleware, CLI arg parsing
├── ScraperWorker.cs                    BackgroundService orchestrating scrape lifecycle
├── ScraperOptions.cs                   Configuration POCO
│
├── Api/
│   ├── ApiEndpoints.cs                 All HTTP route mappings (public + protected)
│   ├── AuthEndpoints.cs                User auth routes (login, refresh, logout, me)
│   ├── ApiKeyAuth.cs                   X-API-Key header auth handler + ApiSettings
│   ├── BearerTokenAuthHandler.cs       JWT Bearer token validation
│   └── PathTraversalGuardMiddleware.cs Security middleware
│
├── Auth/
│   ├── EpicAuthService.cs             Epic Games OAuth (device auth, device code, refresh)
│   ├── TokenManager.cs                Manages Epic access token lifecycle
│   ├── JwtTokenService.cs             Issues/validates JWT tokens for mobile users
│   ├── UserAuthService.cs             User registration, login, session management
│   └── FileDeviceAuthStore.cs         Persists Epic device credentials to disk
│
├── Persistence/
│   ├── MetaDatabase.cs                Central SQLite metadata DB (fst-meta.db)
│   ├── InstrumentDatabase.cs          Per-instrument sharded SQLite DBs
│   ├── GlobalLeaderboardPersistence.cs Pipelined writes + change detection
│   ├── PersonalDbBuilder.cs           Builds per-user/device SQLite DBs for mobile sync
│   └── DataTransferObjects.cs         DTOs (ScoreHistoryEntry, etc.)
│
├── Scraping/
│   ├── GlobalLeaderboardScraper.cs    Fetches leaderboard data from Epic API
│   ├── HistoryReconstructor.cs        Walks seasonal leaderboards for score history
│   ├── ScoreBackfiller.cs             Fills missing scores via targeted lookups
│   ├── PostScrapeRefresher.cs         Refreshes stale entries for registered users
│   ├── AccountNameResolver.cs         Resolves account IDs → display names
│   ├── FirstSeenSeasonCalculator.cs   Determines when songs first appeared
│   ├── AdaptiveConcurrencyLimiter.cs  Dynamic parallelism (AIMD algorithm)
│   ├── BackfillQueue.cs               Queues users for backfill processing
│   ├── ScrapeProgressTracker.cs       Live progress exposed to API
│   └── ResilientHttpExecutor.cs       Retry/circuit-breaker HTTP wrapper
│
└── data/                              Runtime data directory
    ├── fst-meta.db                    Central metadata DB
    ├── fst-service.db                 Song catalog DB
    ├── fst-Solo_Guitar.db             Instrument DB (Guitar)
    ├── fst-Solo_Bass.db               Instrument DB (Bass)
    ├── fst-Solo_Drums.db              Instrument DB (Drums)
    ├── fst-Solo_Vocals.db             Instrument DB (Vocals)
    ├── fst-Solo_PeripheralGuitar.db   Instrument DB (Pro Guitar)
    ├── fst-Solo_PeripheralBass.db     Instrument DB (Pro Bass)
    ├── device-auth.json               Epic OAuth credentials
    ├── page-estimate.json             Cached page count for progress estimation
    └── personal/                      Per-user/device databases
        └── {accountId}/
            └── {deviceId}.db
```

### Shared Library: FortniteFestival.Core

A shared .NET library (multi-targets `net472` + `net9.0`) consumed by FSTService:

- **`Services/FestivalService.cs`** — Song catalog sync from Epic's calendar API
- **`Models/`** — Song, leaderboard, and calendar data models
- **`Config/InstrumentType.cs`** — Instrument enum (Lead, Bass, Drums, Vocals, ProLead, ProBass)
- **`Persistence/SqlitePersistence.cs`** — Song catalog persistence to `fst-service.db`

## Startup Pipeline

The application startup in `Program.cs` follows this sequence:

1. **Configure JSON options** — `WhenWritingNull` ignore condition for API responses
2. **Bind configuration** — `ScraperOptions`, `ApiSettings`, `JwtSettings` from `appsettings.json`
3. **Parse CLI args** — Overlay `--api-only`, `--setup`, `--test`, `--once`, `--resolve-only` onto `ScraperOptions`
4. **Register HTTP clients** — Typed clients for `EpicAuthService`, `GlobalLeaderboardScraper`, `AccountNameResolver`, `HistoryReconstructor` with tuned `SocketsHttpHandler` settings
5. **Register singletons** — Auth services, persistence, scraping components, progress tracker
6. **Configure authentication** — Dual scheme: `ApiKey` + `Bearer`
7. **Configure rate limiting** — Fixed-window limiters per endpoint category + global limiter
8. **Configure CORS** — Allowed origins from `ApiSettings`
9. **Register `ScraperWorker`** as a hosted background service
10. **Build the app and initialize** — `GlobalLeaderboardPersistence.Initialize()` ensures all SQLite schemas exist
11. **Configure middleware pipeline** — `PathTraversalGuard` → CORS → Rate Limiter → Authentication → Authorization
12. **Map endpoints** — `MapApiEndpoints()` + `MapAuthEndpoints()`

## Key Design Decisions

- **No ORM** — Raw SQL via `Microsoft.Data.Sqlite` for full control over query performance and schema
- **Sharded databases** — One SQLite file per instrument eliminates write contention during parallel scraping
- **Pipelined writes** — Per-instrument `Channel<T>` writers overlap disk I/O with network I/O
- **Adaptive concurrency** — AIMD algorithm dynamically adjusts parallelism based on Epic API error rates
- **Resumable operations** — Backfill and history reconstruction track per-item progress for crash recovery
- **Dual auth schemes** — API key for server-to-server/admin operations; JWT Bearer for mobile user sessions
