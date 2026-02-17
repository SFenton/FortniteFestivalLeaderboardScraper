# FSTService — Deployment & Configuration

This document covers configuration, Docker deployment, and operational concerns for running FSTService.

## Configuration

All configuration is in `appsettings.json`, organized into sections. Every setting can be overridden via environment variables using the `__` (double underscore) separator.

### Scraper Settings (`Scraper` section)

| Key | Type | Default | Env Override | Description |
|---|---|---|---|---|
| `ScrapeInterval` | TimeSpan | `04:00:00` | `Scraper__ScrapeInterval` | Time between scrape passes |
| `SongSyncInterval` | TimeSpan | `00:15:00` | `Scraper__SongSyncInterval` | Background song catalog sync interval (clock-aligned) |
| `DegreeOfParallelism` | int | `512` | `Scraper__DegreeOfParallelism` | Max concurrent Epic API requests per scrape pass |
| `QueryLead` | bool | `true` | `Scraper__QueryLead` | Scrape Guitar leaderboards |
| `QueryDrums` | bool | `true` | `Scraper__QueryDrums` | Scrape Drums leaderboards |
| `QueryVocals` | bool | `true` | `Scraper__QueryVocals` | Scrape Vocals leaderboards |
| `QueryBass` | bool | `true` | `Scraper__QueryBass` | Scrape Bass leaderboards |
| `QueryProLead` | bool | `true` | `Scraper__QueryProLead` | Scrape Pro Guitar leaderboards |
| `QueryProBass` | bool | `true` | `Scraper__QueryProBass` | Scrape Pro Bass leaderboards |
| `DataDirectory` | string | `data` | `Scraper__DataDirectory` | Root directory for all data files |
| `DatabasePath` | string | `data/fst-service.db` | `Scraper__DatabasePath` | Song catalog SQLite database path |
| `DeviceAuthPath` | string | `data/device-auth.json` | `Scraper__DeviceAuthPath` | Epic OAuth credentials file path |

### API Settings (`Api` section)

| Key | Type | Default | Env Override | Description |
|---|---|---|---|---|
| `ApiKey` | string | *(per environment)* | `Api__ApiKey` | API key for protected endpoints |
| `AllowedOrigins` | string[] | `["http://localhost:3000"]` | `Api__AllowedOrigins__0` | CORS allowed origins |

### JWT Settings (`Jwt` section)

| Key | Type | Default | Env Override | Description |
|---|---|---|---|---|
| `Secret` | string | `CHANGE-ME-...` | `Jwt__Secret` | HS256 signing key (≥32 chars) |
| `Issuer` | string | `FSTService` | `Jwt__Issuer` | JWT issuer claim |
| `AccessTokenLifetimeMinutes` | int | `60` | `Jwt__AccessTokenLifetimeMinutes` | Access token validity (minutes) |
| `RefreshTokenLifetimeDays` | int | `30` | `Jwt__RefreshTokenLifetimeDays` | Refresh token validity (days) |

### Kestrel Settings (`Kestrel` section)

| Key | Type | Default | Description |
|---|---|---|---|
| `Endpoints.Http.Url` | string | `http://0.0.0.0:8080` | Listen address and port |

### Logging (`Logging` section)

Default configuration provides structured console logging with UTC timestamps:

| Logger | Level | Notes |
|---|---|---|
| `Default` | Information | General application logging |
| `Microsoft.Hosting.Lifetime` | Information | Host start/stop messages |
| `Microsoft.AspNetCore` | Warning | ASP.NET Core internals (reduced noise) |
| `System.Net.Http.HttpClient` | Warning | HTTP client factory internals |
| `FSTService` | Debug | Service-specific detailed logging |

---

## CLI Arguments

CLI arguments are parsed in `Program.cs` and overlaid onto `ScraperOptions`:

| Argument | Effect |
|---|---|
| `--api-only` | Sets `ApiOnly = true`. No background scraping; API serves existing data. |
| `--setup` | Sets `SetupOnly = true`. Interactive Epic device code auth, then exit. |
| `--once` | Sets `RunOnce = true`. Single scrape pass + resolve, then exit. |
| `--resolve-only` | Sets `ResolveOnly = true`. Resolve unresolved account names, then exit. |
| `--test "Song Name"` | Sets `TestSongQuery`. Scrape one or more comma-separated songs, then exit. |

**Examples:**
```bash
# Normal operation (continuous scraping + API)
dotnet run --project FSTService/FSTService.csproj

# API-only mode for development
dotnet run --project FSTService/FSTService.csproj -- --api-only

# First-time authentication setup
dotnet run --project FSTService/FSTService.csproj -- --setup

# Test a specific song scrape
dotnet run --project FSTService/FSTService.csproj -- --test "Bohemian Rhapsody"

# Test multiple songs
dotnet run --project FSTService/FSTService.csproj -- --test "Song A,Song B"

# One-shot scrape (useful for cron)
dotnet run --project FSTService/FSTService.csproj -- --once
```

---

## Docker Deployment

### Building the Image

The Dockerfile uses a multi-stage build:

**Stage 1 — Build** (`mcr.microsoft.com/dotnet/sdk:9.0`):
- Copies `.csproj` files first for Docker layer caching on `dotnet restore`
- Restores `FSTService.csproj` (pulls in `FortniteFestival.Core` transitively)
- Copies full source and publishes Release configuration to `/app`

**Stage 2 — Runtime** (`mcr.microsoft.com/dotnet/aspnet:9.0`):
- Creates non-root `appuser` for container hardening
- Creates `/app/data` directory with proper ownership
- Runs as `appuser` (non-root)
- Exposes port 8080
- Declares `/app/data` as a `VOLUME`

```bash
# Build locally
docker build -t fstservice -f FSTService/Dockerfile .

# Build via docker-compose (root docker-compose.yml)
docker compose build
```

### Running with Docker Compose

**Local development** (root `docker-compose.yml`):
```bash
docker compose up -d
```

**Production deployment** (`deploy/docker-compose.yml`):
```bash
# On the remote host:
# 1. Authenticate with GitHub Container Registry
echo YOUR_PAT | docker login ghcr.io -u SFenton --password-stdin

# 2. Pull and start
docker compose -f deploy/docker-compose.yml pull
docker compose -f deploy/docker-compose.yml up -d
```

### Docker Compose Configuration

Both compose files share the same structure:

```yaml
services:
  fstservice:
    image: ghcr.io/sfenton/fstservice:latest  # deploy/ uses pre-built image
    # build:                                    # root uses local build
    #   context: .
    #   dockerfile: FSTService/Dockerfile
    container_name: fstservice
    restart: unless-stopped
    volumes:
      - ./fst-data:/app/data          # Persistent data directory
    ports:
      - "127.0.0.1:8080:8080"         # Localhost only — use reverse proxy for external
    environment:
      - DOTNET_ENVIRONMENT=Production
      # Override settings via env vars:
      # - Scraper__ScrapeInterval=04:00:00
      # - Scraper__DegreeOfParallelism=16
      # - Scraper__ApiOnly=true
      # - Api__ApiKey=your-secure-key
      # - Jwt__Secret=your-256-bit-secret
```

**Key notes:**
- Port binding is `127.0.0.1:8080:8080` — only accessible from localhost. Use a reverse proxy (nginx, Caddy) for external access with TLS.
- The `./fst-data` volume persists all databases, credentials, and personal DB files across container restarts.
- `restart: unless-stopped` ensures the service comes back up after reboots.

---

## Data Directory Structure

All persistent data lives under the configured `DataDirectory` (default: `data/`, or `/app/data` in Docker):

```
data/
├── fst-meta.db                        Central metadata (scrape logs, history, users, sessions)
├── fst-service.db                     Song catalog from Epic's calendar API
├── fst-Solo_Guitar.db                 Guitar leaderboard entries
├── fst-Solo_Bass.db                   Bass leaderboard entries
├── fst-Solo_Drums.db                  Drums leaderboard entries
├── fst-Solo_Vocals.db                 Vocals leaderboard entries
├── fst-Solo_PeripheralGuitar.db       Pro Guitar leaderboard entries
├── fst-Solo_PeripheralBass.db         Pro Bass leaderboard entries
├── device-auth.json                   Epic OAuth device credentials (SENSITIVE)
├── page-estimate.json                 Cached page count for progress estimation
└── personal/                          Per-user/device mobile sync databases
    └── {accountId}/
        └── {deviceId}.db
```

### Backup Considerations

- **Critical to back up:** `device-auth.json` (re-authentication requires `--setup` with browser access), `fst-meta.db` (score history, user registrations, sessions)
- **Rebuildable:** Instrument DBs can be repopulated from a fresh scrape. Personal DBs are rebuilt from instrument + meta DBs.
- **Disposable:** `page-estimate.json` is only used for progress estimation UX.

---

## First-Time Setup

1. **Build** the application:
   ```bash
   dotnet build FSTService/FSTService.csproj
   ```

2. **Run authentication setup:**
   ```bash
   dotnet run --project FSTService/FSTService.csproj -- --setup
   ```
   Follow the console prompts to authenticate with your Epic Games account in a browser.

3. **Start the service:**
   ```bash
   dotnet run --project FSTService/FSTService.csproj
   ```
   The service will begin scraping immediately and repeat on the configured interval.

4. **Verify it's running:**
   ```bash
   curl http://localhost:8080/healthz
   # → "ok"

   curl http://localhost:8080/api/progress
   # → Live scrape progress or idle status
   ```

### Docker First-Time Setup

For Docker deployments, run `--setup` interactively before starting the daemon:

```bash
# 1. Run setup interactively (requires terminal access for device code)
docker run -it --rm -v ./fst-data:/app/data fstservice -- --setup

# 2. Start the service as a daemon
docker compose up -d
```

---

## Monitoring

### Health Check

`GET /healthz` returns `"ok"` when the service is accepting HTTP requests. This can be used for:
- Docker `HEALTHCHECK`
- Load balancer health probes
- Uptime monitoring

### Scrape Progress

`GET /api/progress` provides real-time visibility into the current scrape pass including:
- Current phase (Scraping, ResolvingNames, BackfillingScores, etc.)
- Per-instrument leaderboard progress
- Network stats (requests, bytes, retries)
- Estimated remaining time
- History of completed operations

### Logging

FSTService uses `ILogger<T>` via `Microsoft.Extensions.Logging`. Key log events:

| Level | Examples |
|---|---|
| **Critical** | `ScraperWorker` unhandled exception |
| **Error** | Auth failure, backfill failure for specific account |
| **Warning** | Name resolution failure, Epic API transient errors |
| **Information** | Scrape pass start/complete, entry counts, phase transitions |
| **Debug** | Background song sync (no changes), individual API retry details |

---

## Performance Tuning

### DegreeOfParallelism

The `DegreeOfParallelism` setting controls the initial concurrency for API requests. The `AdaptiveConcurrencyLimiter` dynamically adjusts from this starting point:

| Environment | Recommended DOP | Notes |
|---|---|---|
| Local development | 16–64 | Lower to avoid rate limiting during testing |
| Production (small VPS) | 128–256 | Balance throughput vs. resource usage |
| Production (dedicated) | 512+ | Higher throughput, faster scrape passes |

The AIMD algorithm will automatically decrease concurrency if Epic starts returning errors (>5% error rate) and increase when things are healthy (<1% error rate).

### HTTP Client Configuration

`SocketsHttpHandler` settings are tuned for high-throughput scraping:

| Client | MaxConnectionsPerServer | Notes |
|---|---|---|
| `GlobalLeaderboardScraper` | 2048 | High concurrency for bulk scraping |
| `AccountNameResolver` | 32 | Lower — name resolution is less latency-sensitive |
| `HistoryReconstructor` | 32 | Lower — targeted per-user queries |

All clients use:
- `PooledConnectionIdleTimeout: 2 minutes`
- `PooledConnectionLifetime: 5 minutes`
- `AutomaticDecompression: All`
