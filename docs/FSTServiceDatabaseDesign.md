# FSTService Database Design

## Overview

FSTService scrapes the full global Fortnite Festival leaderboards — every page, every song, every instrument — and persists the results for analytics, player tracking, and mobile app synchronization.

This document captures the evolving design plan for the persistence layer.

---

## Constraints & Assumptions

### API Limits
- Epic's V1 leaderboard API is capped at **600 pages × 100 entries = 60,000 entries** per song/instrument.
- Leaderboards are **all-time** — scores on older songs change infrequently.

### Instruments
- Currently **6** instruments: Guitar, Bass, Vocals, Drums, ProGuitar, ProBass.
- Expected to grow to **9** in the near future (likely Pro Drums, Pro Vocals, Keys or similar).
- Unlikely to exceed 9.

### Scale Estimates

| Scenario | Songs | Instruments | Avg entries/leaderboard | Total rows |
|---|---|---|---|---|
| Current realistic | 2,000 | 6 | ~5,000 | ~60M |
| Current max (all capped) | 2,000 | 6 | 60,000 | ~720M |
| Future (9 instruments) | 2,500 | 9 | ~5,000 | ~112M |
| Future theoretical max | 2,500 | 9 | 60,000 | ~1.35B |

**Realistic working estimate: 10–20 GB for the main leaderboard data.**

At ~150 bytes per SQLite row, 60M rows ≈ 9 GB, 112M rows ≈ 17 GB.

### Scrape Frequency
- Default: every **4 hours**.
- Each pass UPSERTs the latest state rather than inserting full snapshots (since all-time scores rarely change, full snapshots would waste 10–20 GB/day).

---

## Architecture: Per-Instrument Sharding

### Why shard by instrument?

A single 10–20 GB SQLite file works in theory, but bulk UPSERTs across tens of millions of rows in one file creates write contention. Splitting by instrument gives us:

| | Single DB | Per-instrument DB |
|---|---|---|
| File count | 1 | 6–9 (one per instrument) |
| File size | 10–20 GB | ~1.5–3 GB each |
| Parallel writes during scrape | No (single writer lock) | Yes (no cross-file contention) |
| Adding new instruments | Schema migration | Create a new file |
| Cross-song queries (within instrument) | Simple | Simple (same DB) |
| Cross-instrument queries | Simple | ATTACH (SQLite supports up to 10 attached DBs) |
| Backup/maintenance | One large file | Smaller, manageable files |

### File Layout

```
data/
  fst-meta.db                       ← Small: ScrapeLog, ScoreHistory, AccountNames, RegisteredUsers
  fst-Solo_Guitar.db                ← LeaderboardEntries for Guitar
  fst-Solo_Bass.db                  ← LeaderboardEntries for Bass
  fst-Solo_Drums.db                 ← LeaderboardEntries for Drums
  fst-Solo_Vocals.db                ← LeaderboardEntries for Vocals
  fst-Solo_PeripheralGuitar.db      ← LeaderboardEntries for ProGuitar
  fst-Solo_PeripheralBass.db        ← LeaderboardEntries for ProBass
  fst-service.db                    ← Existing Core persistence (Songs catalog, personal scores)
```

New instruments (7/8/9) simply add new `fst-Solo_*.db` files with the same schema. No migrations needed on existing files.

---

## Schema

### Instrument DBs (`fst-Solo_*.db`)

Each instrument database has an identical schema. The table stores the **latest state only** — UPSERTed on each scrape pass.

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;

CREATE TABLE LeaderboardEntries (
    SongId        TEXT    NOT NULL,
    AccountId     TEXT    NOT NULL,
    Rank          INTEGER NOT NULL,
    Score         INTEGER NOT NULL,
    Accuracy      INTEGER,
    IsFullCombo   INTEGER,       -- 0/1
    Stars         INTEGER,
    Season        INTEGER,
    Percentile    REAL,
    PointsEarned  INTEGER,
    FirstSeenAt   TEXT    NOT NULL,  -- ISO 8601, set on INSERT
    LastUpdatedAt TEXT    NOT NULL,  -- ISO 8601, updated on every UPSERT
    PRIMARY KEY (SongId, AccountId)
);

-- Leaderboard view: all entries for a song, ordered by rank
CREATE INDEX IX_Song ON LeaderboardEntries (SongId, Rank);

-- Player profile: all of a player's entries across songs
CREATE INDEX IX_Account ON LeaderboardEntries (AccountId);
```

**UPSERT pattern:**
```sql
INSERT INTO LeaderboardEntries (SongId, AccountId, Rank, Score, Accuracy, IsFullCombo, Stars, Season, Percentile, PointsEarned, FirstSeenAt, LastUpdatedAt)
VALUES (@songId, @accountId, @rank, @score, @accuracy, @fc, @stars, @season, @pct, @points, @now, @now)
ON CONFLICT(SongId, AccountId) DO UPDATE SET
    Rank = excluded.Rank,
    Score = excluded.Score,
    Accuracy = excluded.Accuracy,
    IsFullCombo = excluded.IsFullCombo,
    Stars = excluded.Stars,
    Season = excluded.Season,
    Percentile = excluded.Percentile,
    PointsEarned = excluded.PointsEarned,
    LastUpdatedAt = excluded.LastUpdatedAt;
```

### Meta DB (`fst-meta.db`)

Central small database for cross-cutting concerns.

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;

-- ─── Scrape run tracking ────────────────────────────

CREATE TABLE ScrapeLog (
    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
    StartedAt     TEXT    NOT NULL,  -- ISO 8601
    CompletedAt   TEXT,              -- NULL while running
    SongsScraped  INTEGER,
    TotalEntries  INTEGER,
    TotalRequests INTEGER,
    TotalBytes    INTEGER
);

-- ─── Score change history (all instruments) ─────────

CREATE TABLE ScoreHistory (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    SongId      TEXT    NOT NULL,
    Instrument  TEXT    NOT NULL,  -- e.g. "Solo_Guitar"
    AccountId   TEXT    NOT NULL,
    OldScore    INTEGER,
    NewScore    INTEGER,
    OldRank     INTEGER,
    NewRank     INTEGER,
    ChangedAt   TEXT    NOT NULL   -- ISO 8601
);

CREATE INDEX IX_ScoreHist_Account ON ScoreHistory (AccountId);
CREATE INDEX IX_ScoreHist_Song    ON ScoreHistory (SongId, Instrument);

-- ─── Display name cache ─────────────────────────────

CREATE TABLE AccountNames (
    AccountId    TEXT PRIMARY KEY,
    DisplayName  TEXT,
    LastResolved TEXT              -- ISO 8601
);

-- ─── Registered mobile app users ────────────────────

CREATE TABLE RegisteredUsers (
    DeviceId     TEXT NOT NULL,
    AccountId    TEXT NOT NULL,
    RegisteredAt TEXT NOT NULL,    -- ISO 8601
    LastSyncAt   TEXT,             -- ISO 8601, NULL until first sync
    PRIMARY KEY (DeviceId, AccountId)
);

CREATE INDEX IX_Reg_Account ON RegisteredUsers (AccountId);
```

### Existing Core DB (`fst-service.db`)

Unchanged. Contains the `Songs` catalog and personal `Scores` table used by `FestivalService`. The scraper continues to use this for song catalog sync and initialization.

---

## Data Flow

### Scrape Pass (every 4 hours)

The production loop uses `GlobalLeaderboardScraper` (not Core's `FestivalService.FetchScoresWithTokenAsync`). `FestivalService` is retained **only** for song catalog sync.

```
1. Authenticate (TokenManager)
2. Sync song catalog (FestivalService → fst-service.db)
3. INSERT INTO ScrapeLog (StartedAt)
4. Build SongScrapeRequests for all songs (filtering to charted instruments)
5. Call GlobalLeaderboardScraper.ScrapeManySongsAsync()
6. As each GlobalLeaderboardResult arrives (one song + one instrument):
     BEGIN transaction on fst-Solo_{instrument}.db
       UPSERT all entries (up to 60K)
     COMMIT
     Collect all AccountIds into a pass-wide Set<string>
     Process ScoreChanges staging table → ScoreHistory + registered user flags
7. Post-pass: resolve new account display names (see Account Name Resolution below)
8. UPDATE ScrapeLog SET CompletedAt, totals
9. Rebuild personal DBs for flagged registered users
```

### Individual Account Lookup (separate flow, design TBD)

A separate flow will use `GlobalLeaderboardScraper.LookupAccountAsync` / `LookupAccountAllInstrumentsAsync` to fetch a single player's scores on demand. Details deferred to later in planning.

### Change Detection: Registered Users Only

After each song/instrument UPSERT transaction commits, the app code:

1. Loads the set of registered AccountIds from `fst-meta.db → RegisteredUsers` (cached in memory; tiny set)
2. For each registered AccountId, queries the instrument DB for the current row: `SELECT * FROM LeaderboardEntries WHERE SongId = @songId AND AccountId = @id`
3. Compares against the previous known state (held in memory from before the UPSERT)
4. If score changed: INSERT into `fst-meta.db → ScoreHistory` and flag for personal DB rebuild

This is extremely cheap — a handful of indexed lookups per song/instrument commit. No triggers, no staging tables, no extra schema in the instrument DBs.

**Expandable later:** If we ever want all-account ScoreHistory, we can add the trigger + staging table approach back. The `ScoreHistory` table in meta DB doesn't care how rows get there.

### Account Name Resolution: Post-Pass Batch

After all UPSERT transactions complete, resolve display names for any accounts not already in `AccountNames`.

**Epic API**: `GET /account/api/public/account?accountId={id1}&accountId={id2}&...` — supports up to **100 account IDs per request**.

**Flow:**
```
1. During scrape: collect all AccountIds into a pass-wide HashSet<string>
2. After all UPSERTs done: query fst-meta.db → AccountNames for existing IDs
3. Subtract known accounts → "new" accounts
4. Batch new accounts into groups of 100
5. Call Epic's bulk account lookup for each batch
6. INSERT resolved names into AccountNames (DisplayName + LastResolved timestamp)
```

**Cost:**
- First scrape: ~500K unique accounts → ~5,000 API requests → ~4 minutes at DOP=4
- Subsequent scrapes: only genuinely new players (hundreds per cycle) → a handful of requests

**Why post-pass, not inline:** A single account may appear on 500+ leaderboards. Post-pass deduplication resolves each account exactly once. Inline would hit the DB 500 times to check "is this account new?" for the same ID.

**Failure handling:**
- Unresolvable accounts (deleted/banned): store `DisplayName = NULL` with `LastResolved` timestamp to avoid re-trying every pass
- If the name API is down, the scrape still succeeds — name resolution is best-effort
- Unresolved accounts are picked up on the next pass

---

## Mobile App Registration & Sync (Future)

### Flow

```
Mobile App                           FSTService
──────────                           ──────────
Epic Login → account_id
               ── POST /register ──→
               {deviceId, accountId}    Save to RegisteredUsers
                                        Query all instrument DBs for accountId
                                        Build personal .db file
               ←── 200 OK ────────────

                                        ... scrape pass runs ...
                                        Detects score change for registered accountId
                                        Rebuild personal DB
               ←── sync signal ──────
               ── GET /sync/{deviceId} →
               ←── updated personal DB ─
```

### Personal DB

A small (~1–2 MB) SQLite file containing only the registered user's scores across all songs/instruments — structurally similar to what the current MAUI app builds via direct API calls. Shipped whole (not deltas) since the file size is trivial.

### API Endpoints (Future)

The service will need an HTTP API layer alongside the `BackgroundService`:
- `POST /api/register` — register device + Epic account
- `GET /api/sync/{deviceId}` — download latest personal DB
- `GET /api/sync/{deviceId}/version` — check if new data available (for polling)
- `GET /api/status` — scrape health/stats

### Sync Strategy

Polling is likely sufficient given the 4-hour scrape interval. The mobile app checks on launch and periodically. Push notifications (FCM/APNs) could be added later if needed.

---

## Open Questions

- [x] **Account name resolution strategy**: ~~Epic's display name API is rate-limited. Resolve during scrape? Batch job? On-demand for registered users only?~~ **DECIDED** — Post-pass batch. Collect all AccountIds during scrape, deduplicate, resolve new ones via Epic bulk lookup (100/request) after all UPSERTs complete. See Decisions section.
- [x] **ScoreHistory retention**: ~~Prune after N days, or keep forever?~~ **DECIDED** — Keep forever. Only tracked for registered users, so volume is negligible. Even worst-case all-account tracking would be ~2–12 GB/year (decaying due to score ceilings). No pruning needed.
- [ ] **Personal DB schema**: Match the existing MAUI `SqlitePersistence` schema exactly (Songs + Scores tables), or design a new optimized format?
- [ ] **New instrument identifiers**: What will the API keys be for instruments 7/8/9? (Need to discover when they appear in the catalog.)
- [x] **UPSERT batching**: ~~Use SQLite transactions wrapping N rows at a time for write performance? What batch size?~~ **DECIDED** — One transaction per song/instrument (up to 60K rows). See Decisions section.
- [x] **ScoreHistory scope**: ~~Track all accounts, registered only, or skip?~~ **DECIDED** — Registered users only, score changes only, keep forever. No triggers/staging tables in instrument DBs. App code queries registered AccountIds after each UPSERT commit. See Decisions section.
- [x] **Startup behavior**: ~~On first run with empty instrument DBs, the initial scrape will take much longer (all INSERTs, no UPSERTs skipping unchanged rows). Plan for this?~~ **DECIDED** — Accept the longer first run. No special "import" mode. The first pass is all INSERTs (actually faster than UPSERTs) plus name resolution (~4 min). Subsequent passes are normal.

---

## Decisions

### Production Loop Uses GlobalLeaderboardScraper

`RunScrapePassAsync` will call `GlobalLeaderboardScraper.ScrapeManySongsAsync()` to fetch full global leaderboards and persist results to the per-instrument DBs. Core's `FestivalService.FetchScoresWithTokenAsync` (which fetches only the authenticated user's personal scores) is no longer used in the production scrape loop.

- `FestivalService` is retained for **song catalog sync only** (`InitializeAsync`, `SyncSongsAsync`)
- Personal scores for any account are derived by querying the instrument DBs: `WHERE AccountId = @id`
- A separate individual-account lookup flow (using `LookupAccountAsync`) will be designed later

### Transaction Granularity: Per Song/Instrument

Each `GlobalLeaderboardResult` (one song + one instrument, up to 60K rows) is written in a **single transaction** via BEGIN/UPSERT-all/COMMIT.

| Granularity | Rows per transaction | Commits per full scrape | Verdict |
|---|---|---|---|
| Per page | 100 | ~600K | Unnecessary overhead |
| **Per song/instrument** | **up to 60K** | **~12K** | **Chosen — right balance** |
| Per instrument (all songs) | up to 120M | 6 | Memory bomb, long-held locks |

Rationale:
- The scraper already returns complete results per song/instrument as `GlobalLeaderboardResult`
- 60K-row transactions in SQLite with WAL mode complete in well under a second
- Each instrument DB is independent, so multiple instrument transactions can run in parallel without lock contention
- If a scrape fails mid-pass, all previously committed song/instruments are safely persisted

### Account Name Resolution: Post-Pass Batch

After all UPSERT transactions complete, a post-pass phase resolves display names for newly-seen account IDs via Epic's bulk account lookup API (100 IDs/request). Names are cached in `fst-meta.db → AccountNames`. Previously resolved accounts are skipped. Unresolvable accounts are marked with `DisplayName = NULL` to avoid re-trying.

- First scrape cost: ~5,000 requests (~4 minutes). One-time.
- Subsequent scrape cost: near-zero (only new players).
- Best-effort: name resolution failure does not block the scrape.

### ScoreHistory: Registered Users Only, Keep Forever

ScoreHistory is tracked **only for registered users** (not all accounts). After each song/instrument UPSERT commit, the app code queries the instrument DB for registered AccountIds, compares against previous state, and writes changes to `fst-meta.db → ScoreHistory`.

- **Scope**: registered users only — extremely cheap (handful of indexed lookups per commit)
- **Filter**: score changes only (not rank shifts)
- **Retention**: keep forever — volume is negligible for a small registered user set
- **No triggers or staging tables** in instrument DBs — simpler schema, less moving parts
- **Expandable**: can add all-account tracking via triggers later if needed

---

## Implementation Order (Proposed)

1. **Persistence layer** — `GlobalLeaderboardPersistence` class that manages instrument DB files and the meta DB
2. **Wire into ScraperWorker** — after `GlobalLeaderboardScraper` returns results, persist them
3. **ScrapeLog** — track pass metadata
4. **ScoreHistory** — detect and record changes during UPSERT
5. **AccountNames** — resolve display names via post-pass batch
6. **Dockerfile + docker-compose** — containerized deployment
7. **HTTP API layer** — ASP.NET Core endpoints alongside background worker
8. **RegisteredUsers + personal DB generation** — mobile app sync
9. **React web app** — frontend consuming the API

---

## Deployment: Self-Hosted Docker

### Architecture

```
┌─────────────────────────────────────────────┐
│  Docker Host (self-hosted machine)          │
│                                             │
│  ┌───────────────────────────────────────┐  │
│  │  fstservice container                 │  │
│  │                                       │  │
│  │  BackgroundService (scraper loop)     │  │
│  │  ASP.NET Core (HTTP API)             │  │
│  │                                       │  │
│  │  /app/data/ ──── volume mount ────────│──│── ./fst-data/ on host
│  │    fst-meta.db                        │  │
│  │    fst-Solo_Guitar.db                 │  │
│  │    fst-Solo_Bass.db                   │  │
│  │    fst-Solo_Drums.db                  │  │
│  │    fst-Solo_Vocals.db                 │  │
│  │    fst-Solo_PeripheralGuitar.db       │  │
│  │    fst-Solo_PeripheralBass.db         │  │
│  │    fst-service.db                     │  │
│  │    device-auth.json                   │  │
│  └───────────────────────────────────────┘  │
│                                             │
│  Port 8080 ← HTTP API (leaderboard data,   │
│               registration, sync, status)   │
│                                             │
│  (Future) React app served via nginx or     │
│           separate container on port 3000   │
└─────────────────────────────────────────────┘
```

### Dockerfile (planned)

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY FortniteFestival.Core/ FortniteFestival.Core/
COPY FSTService/ FSTService/
RUN dotnet publish FSTService/FSTService.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
VOLUME /app/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "FSTService.dll"]
```

Note: base image changes from `runtime` to `aspnet` once the HTTP API layer is added. Until then, `runtime:9.0` is sufficient.

### Usage

```bash
# First time: interactive setup (device code auth)
docker run -it -v ./fst-data:/app/data fstservice --setup

# Run normally (detached, auto-restart)
docker run -d --restart unless-stopped \
  -v ./fst-data:/app/data \
  -p 8080:8080 \
  --name fstservice fstservice

# View logs
docker logs -f fstservice
```

### Persistent Data

All state lives in the host-mounted `./fst-data/` volume:
- Database files (instrument DBs, meta DB, core DB)
- Device auth credentials (`device-auth.json`)

The container is stateless — it can be destroyed and recreated without data loss as long as the volume mount is preserved.

### Backup

Periodic copy of the `fst-data/` directory. Per-instrument sharding makes incremental backups practical (only copy files with a newer modified timestamp).

---

## HTTP API Layer (Future)

When the API layer is added, the service transitions from a pure `BackgroundService` to a combined **ASP.NET Core WebHost + BackgroundService**. This is a standard pattern — the scraper runs as a hosted service alongside Kestrel.

### Planned Endpoints

| Method | Path | Purpose | Consumer |
|---|---|---|---|
| `GET` | `/api/status` | Scrape health, last run, next run, DB stats | React app, monitoring |
| `GET` | `/api/songs` | Song catalog (from fst-service.db) | React app |
| `GET` | `/api/leaderboard/{songId}/{instrument}` | Full leaderboard for a song/instrument | React app |
| `GET` | `/api/leaderboard/{songId}/{instrument}?top=N` | Top N entries | React app |
| `GET` | `/api/player/{accountId}` | Player profile across all songs/instruments | React app |
| `GET` | `/api/player/{accountId}/history` | Score change history (if registered) | React app, mobile |
| `POST` | `/api/register` | Register device + Epic account | Mobile app |
| `GET` | `/api/sync/{deviceId}` | Download personal DB | Mobile app |
| `GET` | `/api/sync/{deviceId}/version` | Check if sync available | Mobile app |
| `GET` | `/healthz` | Liveness probe | Docker health check |

### CORS

When the React app is served from a different origin (e.g., `localhost:3000` in dev, or a separate nginx container), the API will need CORS headers. ASP.NET Core's `AddCors()` middleware handles this.

### Authentication (API)

TBD. Options range from no auth (local network only), API key, to full OAuth. For self-hosted, no auth or a simple API key is probably sufficient. See Security section for endpoint classification.

---

## Security

This section is **mandatory reading for anyone hosting this service**. The container stores sensitive credentials and exposes an HTTP API. If misconfigured, an attacker could steal your Epic Games credentials, impersonate your account, or access private data.

### Threat Model

| Asset | Location | Risk if exposed |
|---|---|---|
| Device auth credentials | `data/device-auth.json` | Attacker can authenticate as your Epic account, access your account data, make purchases |
| Access/refresh tokens | In-memory (TokenManager) | Same as above — full account takeover |
| Epic account ID | `data/fst-meta.db`, instrument DBs | Links your identity to the service instance; enables targeted attacks |
| Registered user data | `data/fst-meta.db → RegisteredUsers` | Exposes device IDs and Epic account IDs of registered users |
| Database files on disk | `data/*.db` | Direct SQLite file access bypasses all API-level controls |

### Security Measures (Required)

#### 1. No Direct File Access from API

The HTTP API must **never** serve raw files from the `data/` directory. All data access goes through typed API endpoints that return only the intended fields.

Implementation:
- No static file middleware pointed at `data/`
- No endpoint that accepts a file path parameter
- API endpoints query SQLite and return serialized JSON — never raw DB files
- The one exception is `GET /api/sync/{deviceId}` which serves a **generated** personal DB (not one of the main DB files)

#### 2. Credential Isolation

Sensitive files must never be readable via any API path:

- `device-auth.json` — read only by `TokenManager` at startup/refresh. Never exposed via any endpoint.
- Access tokens — held in memory only, never logged at INFO level or returned in API responses.
- `fst-meta.db → RegisteredUsers` — only accessible via authenticated admin endpoints (not public).

**No credential management via API.** There is no endpoint to set up, reset, or refresh Epic credentials. To re-authenticate:

1. Stop the container (`docker stop fstservice`)
2. Delete or replace `device-auth.json` in the volume
3. Run the container interactively with `--setup` (`docker run -it -v ./fst-data:/app/data fstservice --setup`)
4. Restart normally (`docker start fstservice`)

This is a deliberate design choice: credential operations are **offline-only**. An attacker with API access cannot trigger re-authentication, replace credentials, or observe the auth flow.

Implementation:
- Middleware that rejects any request containing path traversal patterns (`..`, `%2e%2e`, etc.)
- No endpoint that echoes back internal configuration, file paths, or connection strings
- No endpoint that triggers auth setup, token refresh, or credential writes
- `/api/status` returns only operational metrics (last scrape time, counts) — never file paths, tokens, or credentials

#### 3. Endpoint Classification

Endpoints are split into **public** (anyone can call) and **protected** (require authentication):

| Endpoint | Classification | Rationale |
|---|---|---|
| `GET /healthz` | Public | Liveness probe, returns 200 only |
| `GET /api/songs` | Public | Song catalog is public data |
| `GET /api/leaderboard/{songId}/{instrument}` | Public | Leaderboard data is public |
| `GET /api/player/{accountId}` | Public | Scores are public on Epic's own leaderboards |
| `GET /api/status` | Protected | Reveals operational details about the host |
| `POST /api/register` | Protected | Modifies state, creates registered user records |
| `GET /api/sync/{deviceId}` | Protected | Returns user-specific data |
| `GET /api/player/{accountId}/history` | Protected | Only available for registered users |

Protected endpoints require at minimum an **API key** passed via header (`X-API-Key`). The key is configured via environment variable / appsettings, never hardcoded.

#### 4. Rate Limiting

All endpoints should be rate-limited to prevent abuse and resource exhaustion:

- **Public endpoints**: 60 requests/minute per IP (configurable)
- **Protected endpoints**: 30 requests/minute per API key
- **Global**: 200 requests/minute total

ASP.NET Core's built-in `RateLimiter` middleware handles this. Rate limit responses return `429 Too Many Requests`.

#### 5. Network-Level Protection

Docker and network configuration:

- **Bind to localhost by default**: `-p 127.0.0.1:8080:8080` — the API is only accessible from the host machine. External access requires explicit configuration.
- **Reverse proxy recommended**: For external access, place nginx/Caddy in front with TLS termination. Never expose Kestrel directly to the internet.
- **Docker network isolation**: The fstservice container should be on an internal Docker network. Only the reverse proxy container has external port mappings.

```yaml
# docker-compose.yml with reverse proxy (recommended for external access)
services:
  fstservice:
    build: ./FSTService
    volumes:
      - ./fst-data:/app/data
    networks:
      - internal
    # No port mapping — only accessible via reverse proxy
    restart: unless-stopped

  caddy:
    image: caddy:2
    ports:
      - "443:443"
      - "80:80"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile
    networks:
      - internal
    depends_on:
      - fstservice

networks:
  internal:
    driver: bridge
```

#### 6. Container Hardening

- Run as **non-root user** inside the container:
  ```dockerfile
  RUN adduser --disabled-password --gecos "" appuser
  USER appuser
  ```
- **Read-only filesystem** where possible (`--read-only`), with the `data/` volume as the only writable mount
- No unnecessary packages or tools in the container image
- Use `.dockerignore` to exclude credentials, `.git`, IDE files from the build context

#### 7. Sensitive Data in Logs

- **Never log** access tokens, refresh tokens, device auth secrets, or API keys at any log level
- **Redact** Epic account IDs in logs where possible (show first 4 + last 4 characters only)
- Log files stored in the container should not be accessible via the API

#### 8. CORS Restrictions

- In production, `AllowedOrigins` should be explicitly configured (not `*`)
- Only the React app's origin should be allowed
- Credentials mode should match the auth strategy (API key via header = no credentials needed in CORS)

### Security Checklist (Pre-Deployment)

- [ ] `device-auth.json` is not accessible via any HTTP endpoint
- [ ] No endpoint triggers credential setup, token refresh, or auth flows
- [ ] No endpoint returns raw database files, file paths, or internal config
- [ ] Access tokens and refresh tokens are not present in any log output
- [ ] API key is required for protected endpoints
- [ ] API key is set via environment variable, not in committed config files
- [ ] Docker port binding is `127.0.0.1:8080:8080` (not `0.0.0.0`) unless behind a reverse proxy
- [ ] Container runs as non-root user
- [ ] Rate limiting is configured and tested
- [ ] CORS origins are explicitly listed (not wildcard) in production
- [ ] Path traversal patterns are rejected by middleware

---

## React Web App (Future)

A lightweight React SPA consuming the HTTP API above. Possible features:

- **Leaderboard browser** — search songs, view full leaderboards per instrument
- **Player lookup** — search by display name, view all scores/rankings
- **Stats dashboard** — top players by #1 count, most improved, etc.
- **Scrape status** — last run, next run, errors

Can be served from a separate container (nginx + static build) or bundled into the same container. Separate is cleaner for development.

```yaml
# docker-compose.yml (future)
services:
  fstservice:
    build: ./FSTService
    volumes:
      - ./fst-data:/app/data
    ports:
      - "8080:8080"
    restart: unless-stopped

  web:
    build: ./fst-web
    ports:
      - "3000:80"
    depends_on:
      - fstservice
```
