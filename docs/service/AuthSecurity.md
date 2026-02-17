# FSTService — Authentication & Security

This document covers all authentication flows, security mechanisms, and token management in FSTService.

## Authentication Architecture

FSTService has two distinct authentication domains:

| Domain | Purpose | Credentials |
|---|---|---|
| **Epic Games OAuth** | Server-side access to Epic's leaderboard and account APIs | Device auth credentials (persisted to disk) |
| **User Authentication** | Mobile app user sessions | JWT access tokens + opaque refresh tokens |

Additionally, the HTTP API uses **API key authentication** for admin/protected endpoints.

```
┌──────────────────────────────────────────────────────────────────────────┐
│                        Authentication Flows                              │
│                                                                          │
│  ┌────────────────────┐     ┌────────────────────┐                       │
│  │  Epic Games OAuth   │     │  User Auth (JWT)   │                       │
│  │                    │     │                    │                       │
│  │  EpicAuthService   │     │  UserAuthService   │                       │
│  │  TokenManager      │     │  JwtTokenService   │                       │
│  │  FileCredentialStore│     │                    │                       │
│  └────────────────────┘     └────────────────────┘                       │
│                                                                          │
│  ┌────────────────────┐     ┌────────────────────┐                       │
│  │  API Key Auth       │     │  Bearer Token Auth │                       │
│  │                    │     │                    │                       │
│  │  ApiKeyAuthHandler │     │  BearerTokenAuth-  │                       │
│  │  X-API-Key header  │     │  Handler           │                       │
│  └────────────────────┘     └────────────────────┘                       │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## Epic Games OAuth

### Overview

FSTService authenticates with Epic Games to access their leaderboard and account APIs. All API calls to Epic require a valid access token obtained through their OAuth 2.0 system.

**Client:** `fortniteNewSwitchGameClient` (Client ID: `98f7e42c2e674...`), which supports `device_code`, `refresh_token`, and `client_credentials` grant types.

### First-Time Setup (Device Code Flow)

Run with `--setup` to perform initial authentication:

1. `EpicAuthService.StartDeviceCodeFlowAsync()` requests a device authorization code from Epic
2. The service displays a verification URL and user code in the console
3. The user navigates to the URL and enters the code in their browser
4. `EpicAuthService.PollDeviceCodeAsync()` polls until the user completes authorization
5. On success, the access token, refresh token, and account ID are received
6. Credentials are persisted to `data/device-auth.json` via `FileCredentialStore`

### Token Lifecycle (TokenManager)

`TokenManager` manages the access token lifecycle with these strategies:

```
GetAccessTokenAsync()
        │
        ▼
  In-memory token valid     ──► Return token
  (>5 min until expiry)?
        │ No
        ▼
  In-memory refresh token   ──► RefreshTokenAsync() ──► Persist new refresh token
  available?                                              Return new access token
        │ No
        ▼
  Disk-stored refresh token ──► Load & RefreshTokenAsync() ──► Return new access token
  available?
        │ No
        ▼
  Return null (need --setup)
```

**Concurrency:** Refresh attempts are serialized via `SemaphoreSlim(1,1)`. Multiple concurrent callers will wait for a single refresh operation rather than triggering multiple refreshes.

**Token expiry:** Epic access tokens expire after approximately 8 hours. `TokenManager` proactively refreshes when within 5 minutes of expiry.

### Credential Storage

`FileCredentialStore` persists Epic device credentials to `data/device-auth.json`:
- Refresh token (used to obtain new access tokens)
- Account ID (the authenticated Epic account)

This file must be kept secure — anyone with the refresh token can obtain access tokens for the account.

---

## User Authentication (Mobile App)

### JWT Token Design

FSTService issues JWT tokens for mobile app user sessions:

| Token Type | Format | Lifetime | Storage |
|---|---|---|---|
| Access Token | HS256 JWT | 60 minutes (configurable) | Client-side (memory) |
| Refresh Token | Opaque: `fst_rt_` + 32 random bytes (base64url) | 30 days (configurable) | Client-side (secure storage) |

**Access token claims:**
- `sub` — Username
- `deviceId` — Device identifier
- `jti` — Unique token ID
- Standard JWT fields: `iss`, `exp`, `iat`

**Refresh token storage:** The raw refresh token is **never stored on the server**. Only a SHA-256 hash is persisted in the `UserSessions` table. This means a database breach does not expose usable tokens.

### Login Flow

```
POST /api/auth/login { username, deviceId, platform }
        │
        ▼
  Look up Epic account ID ──► AccountNames table (case-insensitive)
        │
        ▼
  Register/update user ──► RegisteredUsers table
  and device pair
        │
        ▼
  Account known? ──► Build personal DB + enqueue backfill
        │
        ▼
  Generate JWT access token ──► JwtTokenService
  Generate opaque refresh token
        │
        ▼
  Store session ──► UserSessions table (hashed refresh token)
        │
        ▼
  Return { accessToken, refreshToken, expiresIn, accountId, displayName, personalDbReady }
```

### Token Refresh (Rotation)

Refresh tokens use **rotation** for security. Each refresh operation:

1. Validates the incoming refresh token (hashes and looks up in `UserSessions`)
2. **Revokes** the old session/token
3. Generates a **new** refresh token
4. Creates a new session with the new hashed token
5. Returns new access + refresh tokens

If an attacker steals and uses a refresh token that the legitimate user has already refreshed, the stolen token will be rejected (already revoked). This limits the window of compromise.

### Session Management

Sessions are tracked in the `UserSessions` table:
- Device ID and platform
- Hashed refresh token
- Creation and expiry timestamps
- Session status (active/revoked)

**Cleanup:** Expired/revoked sessions older than 7 days are purged at the end of each scrape pass.

---

## HTTP API Authentication

### API Key Scheme (`ApiKeyAuthHandler`)

Protected endpoints require the `X-API-Key` header. The handler:

1. Reads the `X-API-Key` header from the request
2. Compares it against the configured key (`Api.ApiKey` in `appsettings.json`)
3. On match, creates a `ClaimsPrincipal` with `Name = "api-client"`
4. On failure, returns `AuthenticateResult.Fail` (results in HTTP 401)

**Configuration:** `Api.ApiKey` in `appsettings.json`. If no key is configured, all protected endpoints are rejected.

### Bearer Token Scheme (`BearerTokenAuthHandler`)

User-specific endpoints accept the `Authorization: Bearer {jwt}` header. The handler:

1. Extracts the token from the `Authorization` header
2. Validates via `JwtTokenService.ValidateAccessTokenAsync()` (signature, expiry, issuer)
3. Allows 30-second clock skew for token validation
4. Returns the `ClaimsPrincipal` with the JWT claims on success

### Dual-Scheme Authorization

Most protected endpoints use `RequireAuthorization()` without specifying a scheme, which means **either** API Key or Bearer token is accepted. The `GET /api/auth/me` endpoint specifically requires the `"Bearer"` scheme.

---

## Security Middleware

### Path Traversal Guard

`PathTraversalGuardMiddleware` runs first in the middleware pipeline and rejects requests containing directory traversal patterns in the URL path or query string:

**Blocked patterns:** `..`, `%2e%2e`, `%2E%2E`, `%2e.`, `%2E.`, `.%2e`, `.%2E`

Matching is case-insensitive. Detected attempts return **HTTP 400 Bad Request** and are logged as warnings.

### Rate Limiting

Fixed-window rate limiters prevent abuse:

| Policy | Limit | Window | Endpoints |
|---|---|---|---|
| `public` | 60 req | 1 min | `/healthz`, `/api/songs`, `/api/leaderboard/*`, `/api/player/*`, `/api/progress`, `/api/firstseen`, `/api/diag/*` |
| `auth` | 10 req | 1 min | `/api/auth/login`, `/api/auth/refresh`, `/api/auth/logout` |
| `protected` | 30 req | 1 min | `/api/status`, `/api/register`, `/api/backfill/*`, `/api/sync/*`, `/api/player/*/history`, `/api/firstseen/calculate` |
| `global` | 200 req | 1 min | All endpoints (server-wide) |

Exceeding any limit returns HTTP 429 Too Many Requests.

### CORS

CORS is configured via `Api.AllowedOrigins` in `appsettings.json`. The default policy allows:
- Specified origins (default: `http://localhost:3000`)
- Any header
- Any method

---

## Configuration Reference

### JWT Settings (`Jwt` section in `appsettings.json`)

| Key | Type | Default | Description |
|---|---|---|---|
| `Secret` | string | `CHANGE-ME-...` | HS256 signing key. Must be ≥32 characters. **Change in production.** |
| `Issuer` | string | `FSTService` | JWT issuer claim |
| `AccessTokenLifetimeMinutes` | int | `60` | Access token validity period |
| `RefreshTokenLifetimeDays` | int | `30` | Refresh token validity period |

### API Settings (`Api` section in `appsettings.json`)

| Key | Type | Default | Description |
|---|---|---|---|
| `ApiKey` | string | *(set per environment)* | API key for protected endpoints. Can be set via `Api__ApiKey` env var. |
| `AllowedOrigins` | string[] | `["http://localhost:3000"]` | CORS allowed origins |

---

## Security Checklist for Production

- [ ] Change the JWT `Secret` to a strong, unique 256-bit key
- [ ] Change the `Api.ApiKey` to a unique, unguessable value
- [ ] Set specific CORS origins (never use `*` in production)
- [ ] Protect `data/device-auth.json` — contains Epic refresh token
- [ ] Run the Docker container as the non-root `appuser` (default in Dockerfile)
- [ ] Mount `/app/data` as a Docker volume with restricted permissions
- [ ] Use HTTPS in production (via reverse proxy like nginx or Caddy)
- [ ] Consider IP-based rate limiting in the reverse proxy
