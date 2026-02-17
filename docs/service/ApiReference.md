# FSTService — API Reference

**Base URL:** `http://localhost:8080`

## Authentication Schemes

FSTService supports two authentication schemes, configured as a dual-scheme setup:

| Scheme | Header | Purpose |
|---|---|---|
| **API Key** | `X-API-Key: {key}` | Admin/server-to-server access to protected endpoints. Key configured in `appsettings.json` under `Api.ApiKey`. |
| **Bearer Token** | `Authorization: Bearer {jwt}` | User-specific endpoints for mobile app sessions. JWT issued by `POST /api/auth/login`. |

Endpoints marked `RequireAuthorization()` (no explicit scheme) accept **either** scheme. Endpoints specifying `"Bearer"` require a JWT Bearer token specifically.

## Rate Limiting

All endpoints are rate-limited using fixed-window rate limiters:

| Policy | Limit | Window | Applied To |
|---|---|---|---|
| `public` | 60 requests | 1 minute | Public endpoints (songs, leaderboards, health) |
| `auth` | 10 requests | 1 minute | Auth endpoints (login, refresh, logout) |
| `protected` | 30 requests | 1 minute | Admin/API-key endpoints |
| `global` | 200 requests | 1 minute | All requests (per-server) |

Exceeding any limit returns **HTTP 429 Too Many Requests**.

---

## Public Endpoints

### `GET /healthz`
Health check endpoint.

**Response:** `200 OK`
```json
"ok"
```

---

### `GET /api/progress`
Live scrape progress for the current or most recent scrape pass.

**Response:** `200 OK`
```json
{
  "current": {
    "phase": "Scraping",
    "songs": { "completed": 120, "total": 400 },
    "leaderboards": {
      "completed": 450,
      "total": 2400,
      "byInstrument": {
        "Solo_Guitar": { "completed": 80, "total": 400 },
        "Solo_Bass": { "completed": 75, "total": 400 }
      }
    },
    "pages": {
      "fetched": 12000,
      "estimatedTotal": 50000,
      "discoveredTotal": 48000,
      "discoveryComplete": false
    },
    "requests": 13500,
    "retries": 12,
    "bytesReceived": 524288000,
    "currentDop": 256,
    "progressPercent": 24.0,
    "estimatedRemainingSeconds": 180.5
  },
  "completedOperations": [ ... ],
  "passElapsedSeconds": 62.3
}
```

**Phase values:** `Idle`, `Initializing`, `Scraping`, `CalculatingFirstSeen`, `ResolvingNames`, `RebuildingPersonalDbs`, `RefreshingRegisteredUsers`, `BackfillingScores`, `ReconstructingHistory`

**Progress estimation:** If not all leaderboards' page counts are known yet, the tracker extrapolates total pages based on the ratio of discovered pages to discovered leaderboards. It falls back to cached totals from previous passes stored in `data/page-estimate.json`.

---

### `GET /api/songs`
Returns the full song catalog.

**Response:** `200 OK`
```json
{
  "count": 400,
  "songs": [
    {
      "songId": "abc123",
      "title": "Song Title",
      "artist": "Artist Name",
      "album": "Album Name",
      "year": 2024,
      "tempo": 120.0,
      "genres": ["Rock"],
      "difficulty": {
        "guitar": 4,
        "bass": 3,
        "vocals": 2,
        "drums": 5,
        "proGuitar": 4,
        "proBass": 3
      }
    }
  ]
}
```

---

### `GET /api/leaderboard/{songId}/{instrument}`
Returns the global leaderboard for a specific song and instrument.

**Path Parameters:**
- `songId` — The song's unique identifier
- `instrument` — One of: `Solo_Guitar`, `Solo_Bass`, `Solo_Drums`, `Solo_Vocals`, `Solo_PeripheralGuitar`, `Solo_PeripheralBass`

**Query Parameters:**
- `top` *(optional, int)* — Limit results to top N entries

**Response:** `200 OK`
```json
{
  "songId": "abc123",
  "instrument": "Solo_Guitar",
  "count": 1500,
  "entries": [
    {
      "accountId": "195e93ef108143b2975ee46662d4d0e1",
      "score": 999999,
      "rank": 1,
      "percentile": 100.0,
      "accuracy": 100.0,
      "isFullCombo": true,
      "stars": 6,
      "highScoreSeason": 5,
      "bestRunTotalScore": 1200000
    }
  ]
}
```

**Error:** `404 Not Found` if the instrument name is invalid.

---

### `GET /api/player/{accountId}`
Returns a player's scores across all songs and instruments.

**Response:** `200 OK`
```json
{
  "accountId": "195e93ef108143b2975ee46662d4d0e1",
  "displayName": "SFentonX",
  "totalScores": 2400,
  "scores": [ ... ]
}
```

---

### `GET /api/firstseen`
Returns the first-seen season metadata for all songs.

**Response:** `200 OK`
```json
{
  "count": 400,
  "songs": [
    {
      "songId": "abc123",
      "firstSeenSeason": 3,
      "estimatedSeason": false
    }
  ]
}
```

---

### `GET /api/diag/events`
Diagnostic endpoint that queries Epic's events API directly and proxies the raw response.

**Query Parameters:**
- `gameId` *(optional, string)* — Default: `FNFestival`

**Response:** Raw JSON from Epic's `GET /api/v1/events/{gameId}/data/{accountId}?showPastEvents=true`

---

### `GET /api/diag/leaderboard`
Diagnostic endpoint for testing arbitrary leaderboard queries against Epic's API.

**Query Parameters:**
- `eventId` *(required)* — Event ID (e.g., `alltime_songId_instrument`)
- `windowId` *(required)* — Window ID (e.g., `alltime`)
- `version` *(optional, int)* — `1` for V1 GET (default), `2` for V2 POST
- `gameId` *(optional)* — Default: `FNFestival`
- `acct` *(optional)* — Set to `"false"` to omit accountId from V2 request
- `fromIndex` *(optional, int)* — V2 starting index (default: 0)
- `findTeams` *(optional)* — V2 findTeams parameter
- `page` *(optional, int)* — V1 page number
- `rank` *(optional, int)* — V1 rank parameter
- `teamAccountIds` *(optional)* — Comma-separated account IDs

**Response:** `200 OK` — Wrapped Epic response:
```json
{
  "_url": "https://events-public-service-live...",
  "_status": 200,
  "body": { ... }
}
```

---

## Protected Endpoints (API Key Required)

All endpoints below require the `X-API-Key` header.

### `GET /api/status`
Returns service status including last scrape run details and entry counts per instrument.

**Response:** `200 OK`
```json
{
  "lastScrape": {
    "id": 42,
    "startedAt": "2026-02-17T00:00:00Z",
    "completedAt": "2026-02-17T00:05:00Z",
    "songsScraped": 400,
    "totalEntries": 1200000,
    "totalRequests": 30000,
    "totalBytes": 2147483648
  },
  "instruments": {
    "Solo_Guitar": 200000,
    "Solo_Bass": 180000,
    "Solo_Drums": 190000,
    "Solo_Vocals": 175000,
    "Solo_PeripheralGuitar": 150000,
    "Solo_PeripheralBass": 140000
  },
  "totalEntries": 1035000
}
```

---

### `POST /api/register`
Registers a user for personal score tracking.

**Request Body:**
```json
{
  "deviceId": "test-device-001",
  "username": "SFentonX"
}
```

**Response:** `200 OK`
```json
{
  "registered": true,
  "deviceId": "test-device-001",
  "accountId": "195e93ef108143b2975ee46662d4d0e1",
  "displayName": "SFentonX",
  "personalDbReady": true
}
```

If the username isn't found in the account names database:
```json
{
  "registered": false,
  "error": "no_account_found",
  "description": "No Epic Games account was found for that name. Please check spelling and try again."
}
```

---

### `DELETE /api/register`
Unregisters a user and cleans up their personal database.

**Query Parameters:**
- `deviceId` *(required)* — Device identifier
- `accountId` *(required)* — Epic account ID

**Response:** `200 OK`
```json
{
  "unregistered": true,
  "deviceId": "test-device-001",
  "accountId": "195e93ef108143b2975ee46662d4d0e1"
}
```

---

### `GET /api/player/{accountId}/history`
Returns score change history for a registered user.

**Query Parameters:**
- `limit` *(optional, int)* — Max entries to return (default: 100)

**Response:** `200 OK`
```json
{
  "accountId": "195e93ef108143b2975ee46662d4d0e1",
  "count": 50,
  "history": [
    {
      "songId": "abc123",
      "instrument": "Solo_Guitar",
      "oldScore": 900000,
      "newScore": 950000,
      "accuracy": 98.5,
      "isFullCombo": false,
      "stars": 5,
      "seasonRank": 15,
      "allTimeRank": 42,
      "scoreAchievedAt": "2026-01-15T12:00:00Z"
    }
  ]
}
```

**Error:** `404 Not Found` if the account is not registered.

---

### `POST /api/firstseen/calculate`
Triggers FirstSeenSeason calculation for songs that don't have one yet.

**Response:** `200 OK`
```json
{
  "songsCalculated": 15,
  "message": "Calculated FirstSeenSeason for 15 song(s)."
}
```

---

### `GET /api/backfill/{accountId}/status`
Returns the current backfill status for an account.

**Response:** `200 OK`
```json
{
  "accountId": "195e93ef108143b2975ee46662d4d0e1",
  "status": "complete",
  "songsChecked": 400,
  "totalSongsToCheck": 400,
  "entriesFound": 1200,
  "startedAt": "2026-02-17T00:10:00Z",
  "completedAt": "2026-02-17T00:12:00Z",
  "errorMessage": null
}
```

**Status values:** `queued`, `in_progress`, `complete`, `failed`

---

### `POST /api/backfill/{accountId}`
Triggers a full backfill + history reconstruction + personal DB rebuild for a registered account.

**Execution Steps:**
1. **Backfill** — Query Epic API for all songs/instruments where the user has no entry
2. **History Reconstruction** — Walk seasonal leaderboards to build score timeline (if not already done)
3. **Personal DB Rebuild** — Regenerate the user's mobile sync database

**Response:** `200 OK`
```json
{
  "accountId": "195e93ef108143b2975ee46662d4d0e1",
  "newEntriesFound": 150,
  "status": "complete",
  "songsChecked": 400,
  "totalSongsToCheck": 400,
  "entriesFound": 1200,
  "historyEntriesCreated": 800,
  "personalDbsRebuilt": 1
}
```

**Error:** `404 Not Found` if the account is not registered.

---

### `GET /api/sync/{deviceId}/version`
Check the version (last-modified timestamp) and size of a device's personal database.

**Response:** `200 OK`
```json
{
  "deviceId": "test-device-001",
  "available": true,
  "version": "2026-02-17T00:15:00Z",
  "sizeBytes": 1048576
}
```

---

### `GET /api/sync/{deviceId}`
Download the personal SQLite database for a device. Builds on demand if not yet generated. Updates the device's last sync timestamp.

**Response:** `200 OK` — Binary SQLite database file (`application/x-sqlite3`)

**Error:** `404 Not Found` if the device is not registered. `503 Service Unavailable` if the database could not be built.

---

## Auth Endpoints

These endpoints handle user authentication for the mobile app. Rate-limited to 10 requests/minute.

### `POST /api/auth/login`
Authenticates a user and returns JWT tokens.

**Request Body:**
```json
{
  "username": "SFentonX",
  "deviceId": "test-device-001",
  "platform": "iOS"
}
```

**Response:** `200 OK`
```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIs...",
  "refreshToken": "fst_rt_base64urlstring...",
  "expiresIn": 3600,
  "accountId": "195e93ef108143b2975ee46662d4d0e1",
  "displayName": "SFentonX",
  "personalDbReady": true
}
```

**Side Effects:**
- Registers/updates the user and device pair
- Builds personal database if the account is known
- Enqueues the account for background score backfill
- Creates a session with a hashed refresh token

---

### `POST /api/auth/refresh`
Refreshes an expired access token.

**Request Body:**
```json
{
  "refreshToken": "fst_rt_base64urlstring..."
}
```

**Response:** `200 OK`
```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIs...",
  "refreshToken": "fst_rt_newbase64urlstring...",
  "expiresIn": 3600
}
```

**Security:** Uses refresh token rotation — the old refresh token is revoked and a new one is issued. Attempting to reuse a revoked token fails with `401 Unauthorized`.

---

### `POST /api/auth/logout`
Revokes the refresh token session.

**Request Body:**
```json
{
  "refreshToken": "fst_rt_base64urlstring..."
}
```

**Response:** `204 No Content`

---

### `GET /api/auth/me` *(Bearer token required)*
Returns information about the currently authenticated user.

**Response:** `200 OK`
```json
{
  "username": "SFentonX",
  "accountId": "195e93ef108143b2975ee46662d4d0e1",
  "displayName": "SFentonX",
  "registeredAt": "2026-01-01T00:00:00Z",
  "lastLoginAt": "2026-02-17T12:00:00Z"
}
```

**Error:** `401 Unauthorized` if the Bearer token is invalid or expired. `404 Not Found` if the user is not found.
