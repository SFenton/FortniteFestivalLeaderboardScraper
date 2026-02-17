# User & Device Registration Design

## Overview

This document describes the work required to implement simplified user and device registration. Instead of Epic Games OAuth, users sign in by providing their **username** and are identified by a stable **device ID** generated on first launch.

The registration model is:
- A **user** is identified by a username (their Epic Games display name, entered manually).
- A **device** is identified by a UUID generated and persisted on the client.
- A user can have multiple devices. A device belongs to exactly one user at a time.
- The service issues its own access/refresh token pair so the user stays signed in across app launches.

---

## Current State

### FSTService (what exists today)

| Concern | Implementation |
|---------|---------------|
| **User registration** | `POST /api/register` accepts `{ deviceId, accountId }`, inserts into `RegisteredUsers(DeviceId, AccountId)`. Protected by API key. Triggers a personal DB build. |
| **Auth** | API key only (`X-API-Key` header). No per-user tokens, no sessions. |
| **Database** | `fst-meta.db` has `RegisteredUsers` table with `(DeviceId, AccountId, RegisteredAt, LastSyncAt)` composite PK, plus `AccountNames`, `ScoreHistory`, `ScrapeLog` tables. |
| **Sync** | `GET /api/sync/{deviceId}` and `/version` look up the device in `RegisteredUsers`, build/serve a personal SQLite DB for the associated account. |

### React Native App (what exists today)

| Concern | Implementation |
|---------|---------------|
| **Auth context** | `AuthContext.tsx` — state machine: `loading → choosing → local \| authenticated`. Persists mode (`fnfestival:authMode`), session (`fnfestival:authSession`), endpoint (`fnfestival:serviceEndpoint`) in AsyncStorage. |
| **Sign-in screen** | `SignInScreen.tsx` — two options: connect to FST service (endpoint + username) or use locally. |
| **Auth client** | `fstAuthClient.ts` — `FstAuthClient` with `login(username, deviceId, platform)`, `refresh(refreshToken)`, `logout(refreshToken)`. |
| **Auth types** | `authTypes.ts` — `AuthLoginResponse`, `AuthRefreshResponse`, `AuthSession` types. |
| **Device ID** | Generated in `AuthContext.tsx` as UUID v4, stored in AsyncStorage under `fnfestival:deviceId`. |
| **Settings screen** | Has a "Connect" section (if local) or "Use Local" button (if authenticated). |

---

## What Needs to Change

The mobile app already has most of the client-side plumbing (auth context, auth client, sign-in screen, device ID generation). The service is missing the auth endpoints the app expects.

### Gap Analysis

| Component | Gap |
|-----------|-----|
| **FSTService auth endpoints** | `POST /api/auth/login`, `POST /api/auth/refresh`, `POST /api/auth/logout` do not exist. The app's `FstAuthClient` calls these but they return 404 today. |
| **FSTService JWT / token infrastructure** | No token issuance, no session management, no refresh token rotation. |
| **FSTService `UserSessions` table** | Does not exist. Needed to track refresh tokens. |
| **FSTService `RegisteredUsers` schema** | Missing `DisplayName`, `Platform`, `LastLoginAt` columns. Currently uses `AccountId` (Epic account ID) — needs to work with a username as the user identifier. |
| **FSTService auto-registration** | Login must also register the user (upsert `RegisteredUsers`) and build their personal DB. The existing `POST /api/register` uses a raw `AccountId`; the new flow uses a username. |
| **Mobile app** | Functionally complete for the simplified flow. Minor changes needed to handle new response fields and any UX polish. |

---

## FSTService Changes

### 1. Configuration: `JwtSettings`

Add a new configuration section for JWT token issuance.

**`appsettings.json` addition:**
```json
{
  "Jwt": {
    "Secret": "generate-a-256-bit-secret-here",
    "Issuer": "FSTService",
    "AccessTokenLifetimeMinutes": 60,
    "RefreshTokenLifetimeDays": 30
  }
}
```

**New class `Auth/JwtSettings.cs`:**
```csharp
public sealed class JwtSettings
{
    public const string Section = "Jwt";
    public string Secret { get; set; } = "";
    public string Issuer { get; set; } = "FSTService";
    public int AccessTokenLifetimeMinutes { get; set; } = 60;
    public int RefreshTokenLifetimeDays { get; set; } = 30;
}
```

Registered in `Program.cs`:
```csharp
builder.Services.Configure<JwtSettings>(
    builder.Configuration.GetSection(JwtSettings.Section));
```

### 2. Token Service: `JwtTokenService`

New file: `Auth/JwtTokenService.cs`

Responsibilities:
- **Mint access tokens** — JWT (HS256) with claims: `sub` (username), `deviceId`, `iat`, `exp`.
- **Generate refresh tokens** — Opaque `fst_rt_` + 32 random bytes (base64url).
- **Validate access tokens** — Verify signature + expiry.

```
public sealed class JwtTokenService
{
    GenerateAccessToken(username, deviceId) → string (JWT)
    GenerateRefreshToken() → string (opaque, prefixed "fst_rt_")
    ValidateAccessToken(token) → ClaimsPrincipal?
    HashRefreshToken(token) → string (SHA-256 hex)
}
```

### 3. Database Schema: `UserSessions` Table

Add to `MetaDatabase.EnsureSchema()`:

```sql
CREATE TABLE IF NOT EXISTS UserSessions (
    Id               INTEGER PRIMARY KEY AUTOINCREMENT,
    Username         TEXT    NOT NULL,
    DeviceId         TEXT    NOT NULL,
    RefreshTokenHash TEXT    NOT NULL UNIQUE,
    Platform         TEXT,
    IssuedAt         TEXT    NOT NULL,
    ExpiresAt        TEXT    NOT NULL,
    LastRefreshedAt  TEXT,
    RevokedAt        TEXT
);

CREATE INDEX IF NOT EXISTS IX_Sessions_Username ON UserSessions(Username);
CREATE INDEX IF NOT EXISTS IX_Sessions_Token ON UserSessions(RefreshTokenHash) WHERE RevokedAt IS NULL;
```

### 4. Database Schema: `RegisteredUsers` Alterations

Add columns to `RegisteredUsers` (backwards-compatible `ALTER TABLE ADD COLUMN` in migration logic):

```sql
ALTER TABLE RegisteredUsers ADD COLUMN DisplayName TEXT;
ALTER TABLE RegisteredUsers ADD COLUMN Platform TEXT;
ALTER TABLE RegisteredUsers ADD COLUMN LastLoginAt TEXT;
```

The existing `AccountId` column will store the username. Today it stores an Epic account ID, but since the user is providing a username (their Epic display name), this column's semantics shift to represent the user-supplied identifier. No rename is needed — the column stores whichever string identifies the user.

> **Note:** The `AccountId` in `RegisteredUsers` and `AccountNames` continues to be the key used for leaderboard lookups. When the user provides a username, the service must **resolve it to an Epic account ID** (via the `AccountNames` table) before building the personal DB. If no matching account ID is found, the personal DB will be empty until the scraper discovers their scores.

### 5. MetaDatabase New Methods

Add to `MetaDatabase.cs`:

```csharp
// ─── UserSessions ───────────────────────────────────────────

/// Insert a new session. Returns the session ID.
long InsertSession(string username, string deviceId, string refreshTokenHash,
                   string? platform, DateTime expiresAt);

/// Find an active (non-revoked, non-expired) session by refresh token hash.
UserSessionInfo? GetActiveSession(string refreshTokenHash);

/// Revoke a session by its refresh token hash.
void RevokeSession(string refreshTokenHash);

/// Revoke all sessions for a username (e.g., "sign out everywhere").
void RevokeAllSessions(string username);

/// Delete expired and revoked sessions older than a cutoff (cleanup).
int CleanupExpiredSessions(DateTime cutoff);

// ─── RegisteredUsers (enhanced) ─────────────────────────────

/// Register or update a user+device. Returns true if newly inserted.
/// Now also sets DisplayName, Platform, LastLoginAt.
bool RegisterOrUpdateUser(string deviceId, string username,
                          string? displayName, string? platform);

/// Look up a username by account ID from AccountNames.
string? GetAccountIdForUsername(string username);
```

**`UserSessionInfo` DTO:**
```csharp
public sealed class UserSessionInfo
{
    public long Id { get; init; }
    public string Username { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string? Platform { get; init; }
    public DateTime IssuedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
}
```

### 6. User Auth Service: `UserAuthService`

New file: `Auth/UserAuthService.cs`

This orchestrates the login/refresh/logout flows.

```
public sealed class UserAuthService
{
    // ── Login ────────────────────────────────────
    //
    // Input: username, deviceId, platform
    // Steps:
    //   1. Validate inputs (non-empty username, deviceId).
    //   2. Look up the Epic account ID for this username in AccountNames.
    //      (May be null if the scraper hasn't seen them yet — that's OK.)
    //   3. Register/update the user in RegisteredUsers.
    //      - If accountId is known: use it as the AccountId column value.
    //      - If unknown: store the username as the AccountId for now.
    //        (The scraper's name-resolution pass will link them later.)
    //   4. Build the personal DB (if account ID is known and data exists).
    //   5. Generate access token (JWT) + refresh token (opaque).
    //   6. Insert session into UserSessions (store hash of refresh token).
    //   7. Return tokens + profile to the caller.
    //
    // Output: LoginResult { accessToken, refreshToken, expiresIn,
    //                        accountId, displayName, personalDbReady }
    LoginResult Login(string username, string deviceId, string platform);

    // ── Refresh ──────────────────────────────────
    //
    // Input: refreshToken
    // Steps:
    //   1. Hash the token, look up the session in UserSessions.
    //   2. Validate: not revoked, not expired.
    //   3. Revoke the old session row.
    //   4. Generate new access token + new refresh token.
    //   5. Insert new session row (rotation).
    //   6. Return new tokens.
    //
    // Output: RefreshResult { accessToken, refreshToken, expiresIn }
    RefreshResult Refresh(string refreshToken);

    // ── Logout ───────────────────────────────────
    //
    // Input: refreshToken
    // Steps:
    //   1. Hash the token, revoke the session row.
    //   2. (Best-effort — don't fail if token not found.)
    void Logout(string refreshToken);
}
```

### 7. Auth API Endpoints

New file: `Api/AuthEndpoints.cs`

Map these inside `ApiEndpoints.MapApiEndpoints()` (or in a separate extension method for organization):

#### `POST /api/auth/login`

**Request:**
```json
{
  "username": "PlayerOne",
  "deviceId": "a1b2c3d4-...",
  "platform": "ios"
}
```

**Response (200):**
```json
{
  "accessToken": "eyJhbG...",
  "refreshToken": "fst_rt_a1b2c3d4...",
  "expiresIn": 3600,
  "accountId": "abc123",
  "displayName": "PlayerOne",
  "personalDbReady": true
}
```

**Error cases:**
- `400` — missing or empty username/deviceId.

No API key required (this is the authentication step itself). Rate-limited to prevent abuse.

#### `POST /api/auth/refresh`

**Request:**
```json
{
  "refreshToken": "fst_rt_a1b2c3d4..."
}
```

**Response (200):**
```json
{
  "accessToken": "eyJhbG...",
  "refreshToken": "fst_rt_new_token...",
  "expiresIn": 3600
}
```

**Error cases:**
- `401` — refresh token expired, revoked, or not found.

No API key required. Rate-limited.

#### `POST /api/auth/logout`

**Request:**
```json
{
  "refreshToken": "fst_rt_a1b2c3d4..."
}
```

**Response:** `204 No Content`

Best-effort. No API key required.

#### `GET /api/auth/me`

**Headers:** `Authorization: Bearer {fst_access_token}`

**Response (200):**
```json
{
  "username": "PlayerOne",
  "accountId": "abc123",
  "registeredAt": "2026-02-14T...",
  "lastLoginAt": "2026-02-15T..."
}
```

**Error cases:**
- `401` — missing or invalid access token.

### 8. Bearer Token Auth Handler

New authentication scheme alongside the existing `ApiKey` scheme.

**New file: `Api/BearerTokenAuthHandler.cs`**

A custom `AuthenticationHandler<>` that:
1. Reads the `Authorization: Bearer {token}` header.
2. Calls `JwtTokenService.ValidateAccessToken(token)`.
3. Returns the `ClaimsPrincipal` with `sub` (username) and `deviceId` claims.

**`Program.cs` changes:**
```csharp
builder.Services
    .AddAuthentication()
    .AddScheme<ApiKeyAuthOptions, ApiKeyAuthHandler>("ApiKey", ...)
    .AddScheme<BearerAuthOptions, BearerTokenAuthHandler>("Bearer", ...);

// Endpoints can require specific schemes:
//   .RequireAuthorization(policy => policy.AuthenticationSchemes.Add("ApiKey"))
//   .RequireAuthorization(policy => policy.AuthenticationSchemes.Add("Bearer"))
```

Sync endpoints (`/api/sync/{deviceId}`, `/api/sync/{deviceId}/version`) should accept **either** API key or Bearer token for backwards compatibility during rollout.

### 9. Rate Limiting for Auth Endpoints

Add a dedicated rate limit bucket:

```csharp
opts.AddFixedWindowLimiter("auth", window =>
{
    window.PermitLimit = 10;
    window.Window = TimeSpan.FromMinutes(1);
    window.QueueLimit = 0;
});
```

Applied to `/api/auth/login`, `/api/auth/refresh`, `/api/auth/logout`.

### 10. Session Cleanup

Add a periodic cleanup to `ScraperWorker` (or a new `IHostedService`) that runs once per scrape cycle:

```csharp
int cleaned = metaDb.CleanupExpiredSessions(DateTime.UtcNow.AddDays(-7));
log.LogInformation("Cleaned up {Count} expired/revoked sessions.", cleaned);
```

This removes sessions that are both expired and older than 7 days (grace period for debugging).

### 11. Username → Account ID Resolution

When a user registers with a username, the service needs to find their Epic account ID to build a personal DB. The `AccountNames` table already maps `AccountId → DisplayName` (populated by the scraper's name-resolution pass).

New method on `MetaDatabase`:
```csharp
public string? GetAccountIdForUsername(string username)
{
    // SELECT AccountId FROM AccountNames
    // WHERE DisplayName = @username COLLATE NOCASE
    // LIMIT 1;
}
```

**Edge cases:**
- Username not found: The user is registered but their personal DB is empty. It will populate on the next scrape pass after their scores appear on a leaderboard and their name is resolved.
- Multiple accounts with the same display name: Return the first match. This is a known limitation — Epic display names are not guaranteed unique. In practice, collisions for active Festival players are rare.
- Username changes: If a user changes their Epic display name, they need to sign in again with the new name. The old registration remains and will be linked when the scraper updates `AccountNames`.

---

## FSTService New/Modified Files Summary

| File | Action | Description |
|------|--------|-------------|
| `Auth/JwtSettings.cs` | **New** | Configuration POCO for JWT secret, issuer, lifetimes |
| `Auth/JwtTokenService.cs` | **New** | Mint/validate JWTs, generate/hash opaque refresh tokens |
| `Auth/UserAuthService.cs` | **New** | Orchestrates login, refresh, logout flows |
| `Api/AuthEndpoints.cs` | **New** | Maps `POST /api/auth/login`, `/refresh`, `/logout`, `GET /api/auth/me` |
| `Api/BearerTokenAuthHandler.cs` | **New** | Bearer token (JWT) authentication handler |
| `Persistence/MetaDatabase.cs` | **Modified** | Add `UserSessions` table to schema, add session CRUD methods, add `RegisterOrUpdateUser`, add `GetAccountIdForUsername`, add column migrations for `RegisteredUsers` |
| `Persistence/DataTransferObjects.cs` | **Modified** | Add `UserSessionInfo`, `LoginResult`, `RefreshResult` DTOs |
| `Program.cs` | **Modified** | Register `JwtSettings`, `JwtTokenService`, `UserAuthService`; add Bearer auth scheme; add auth rate limiter; map auth endpoints |
| `appsettings.json` | **Modified** | Add `Jwt` configuration section |
| `ScraperWorker.cs` | **Modified** | Add session cleanup call after each scrape pass |

---

## React Native App Changes

The mobile app already has the core infrastructure for this flow. The changes are minor and focused on aligning with the finalized service contract.

### 1. Auth Types (`src/core/auth/authTypes.ts`)

The `AuthLoginResponse` type should match the service's response. Update if needed:

```typescript
export interface AuthLoginResponse {
  accessToken: string;
  refreshToken: string;
  expiresIn: number;
  accountId: string;
  displayName: string;
  personalDbReady: boolean;   // NEW — indicates if a personal DB was built
}
```

The `friends` field was part of the Epic OAuth design and should be removed (no longer applicable without Epic login).

### 2. Auth Client (`src/core/auth/fstAuthClient.ts`)

Already correct — calls `POST /api/auth/login` with `{ username, deviceId, platform }`. No changes needed unless response shape changes.

Verify the API key header situation: the existing client does **not** send an `X-API-Key` header. Since the auth endpoints won't require an API key (they are the authentication step), this is correct.

### 3. Auth Context (`src/app/auth/AuthContext.tsx`)

Already handles:
- Device ID generation and persistence.
- Calling `FstAuthClient.login()` with username, deviceId, platform.
- Persisting session (access token, refresh token, expiry, accountId, displayName).
- Token refresh on app launch when the access token is expired.
- Sign-out flow.

**Minor updates:**
- Handle the `personalDbReady` field in the login response (log or surface to user).
- Consider adding an `accountId` fallback: if the service returns `accountId: null` (username not yet resolved), store the username as the account identifier for now and re-check on next sync.

### 4. Sign-In Screen (`src/screens/SignInScreen.tsx`)

Already implemented with service endpoint + username inputs + "Sign In" button + "Use Local" option. No changes needed.

### 5. Settings Screen (`src/screens/SettingsScreen.tsx`)

Already has a "Connect" section when in local mode and a "Use Local" button when authenticated. No changes needed.

### 6. API Key for Non-Auth Endpoints

If the app makes calls to protected service endpoints (e.g., `/api/sync/{deviceId}`), ensure:
- The `Authorization: Bearer {accessToken}` header is sent.
- Or the `X-API-Key` header is sent (for backwards compatibility until Bearer is fully rolled out).

This will matter when **sync integration** is implemented (Phase 3 from the original design). For now, registration does not require sync — it only establishes the user's identity on the service.

---

## React Native App Changes Summary

| File | Action | Description |
|------|--------|-------------|
| `src/core/auth/authTypes.ts` | **Minor update** | Add `personalDbReady` to `AuthLoginResponse`, remove `friends` field |
| `src/core/auth/fstAuthClient.ts` | **No change** | Already correct |
| `src/app/auth/AuthContext.tsx` | **Minor update** | Handle `personalDbReady` response field |
| `src/screens/SignInScreen.tsx` | **No change** | Already implemented |
| `src/screens/SettingsScreen.tsx` | **No change** | Already has mode-switching UI |

---

## Data Flow

### Registration / Login

```
   App                                     FSTService
    │                                          │
    │  1. User enters username                 │
    │     + device ID (auto-generated)         │
    │                                          │
    │  2. POST /api/auth/login                 │
    │     { username, deviceId, platform }     │
    │  ────────────────────────────────────────▶│
    │                                          │
    │                         3. Look up accountId for username
    │                            in AccountNames table
    │                                          │
    │                         4. Upsert RegisteredUsers
    │                            (deviceId, accountId/username)
    │                                          │
    │                         5. Build personal DB
    │                            (if accountId found + data exists)
    │                                          │
    │                         6. Generate JWT access token
    │                            + opaque refresh token
    │                                          │
    │                         7. Insert UserSessions row
    │                            (hashed refresh token)
    │                                          │
    │  8. Response:                            │
    │     { accessToken, refreshToken,         │
    │       expiresIn, accountId,              │
    │       displayName, personalDbReady }     │
    │◀─────────────────────────────────────────│
    │                                          │
    │  9. Persist session to AsyncStorage      │
    │     Transition to 'authenticated'        │
    │                                          │
```

### Token Refresh

```
   App                                     FSTService
    │                                          │
    │  1. Access token expired                 │
    │                                          │
    │  2. POST /api/auth/refresh               │
    │     { refreshToken }                     │
    │  ────────────────────────────────────────▶│
    │                                          │
    │                         3. Hash token, find active session
    │                         4. Validate not expired/revoked
    │                         5. Revoke old session
    │                         6. Generate new access + refresh tokens
    │                         7. Insert new session (rotation)
    │                                          │
    │  8. Response:                            │
    │     { accessToken, refreshToken,         │
    │       expiresIn }                        │
    │◀─────────────────────────────────────────│
    │                                          │
    │  9. Update persisted session             │
    │                                          │
```

---

## Security Considerations

### Token Storage

| Platform | Storage Mechanism | Notes |
|----------|------------------|-------|
| iOS / Android / Windows | AsyncStorage | Tokens are stored as JSON in AsyncStorage. This is not hardware-encrypted. Acceptable for the current threat model (self-hosted service, single-user). |

> **Future improvement:** Migrate to `react-native-keychain` (iOS Keychain / Android Keystore) for hardware-backed storage. Not required for MVP.

### Refresh Token Rotation

Every `/api/auth/refresh` call:
1. Invalidates the old refresh token.
2. Issues a new refresh token.

If an attacker steals a refresh token and the legitimate user also refreshes, one of them gets a 401. The legitimate user re-authenticates; the attacker's token is dead.

### No API Key on Auth Endpoints

Auth endpoints (`/api/auth/login`, `/refresh`, `/logout`) do **not** require an API key. They are rate-limited instead (10 req/min per IP). This avoids shipping the API key in the mobile app binary.

### Username as Identity

The simplified flow uses an Epic display name as the user identifier. This means:
- No cryptographic proof of identity (unlike OAuth).
- Anyone can register as any username.
- Acceptable for the current use case: self-hosted service with trusted users.

> **Future improvement:** Re-add Epic OAuth or another identity provider for verified identity. The token infrastructure built here (JWT + refresh tokens + sessions) is reusable.

---

## Implementation Plan

### Phase 1: Service Auth Infrastructure (this work)

1. `JwtSettings` config + registration in `Program.cs`.
2. `JwtTokenService` — token minting, validation, refresh token generation.
3. `MetaDatabase` schema changes — `UserSessions` table, `RegisteredUsers` column additions, migration logic.
4. `MetaDatabase` new methods — session CRUD, `RegisterOrUpdateUser`, `GetAccountIdForUsername`.
5. `UserAuthService` — login/refresh/logout orchestration.
6. `AuthEndpoints` — map the 4 auth endpoints.
7. `BearerTokenAuthHandler` — JWT-based auth scheme.
8. Rate limiting bucket for auth endpoints.
9. Session cleanup in `ScraperWorker`.
10. `appsettings.json` — add `Jwt` section.

### Phase 2: Mobile App Alignment (this work)

1. Update `AuthLoginResponse` type (add `personalDbReady`, remove `friends`).
2. Handle `personalDbReady` in `AuthContext`.
3. Verify end-to-end flow: sign-in screen → service login → token persistence → authenticated state.

### Phase 3: Sync Integration (future)

- Authenticated users auto-sync personal DB via `GET /api/sync/{deviceId}`.
- Bearer token sent on sync requests.
- Sync status UI in the app.

### Phase 4: Per-Device Settings (future)

- Service-side storage of per-device settings.
- Settings sync on login / device switch.
- Conflict resolution strategy for settings that differ across devices.

---

## Open Questions

1. **Username uniqueness.** Epic display names are not unique. Two different players can have the same display name. For MVP, the service uses first-match lookup in `AccountNames`. Should we add disambiguation?

2. **Username changes.** If a player changes their Epic display name, the old registration becomes orphaned. Should the service auto-link based on account ID when the scraper discovers the new name?

3. **Multiple devices, one user.** A user can register from multiple devices. Each device gets its own session and personal DB. Is there a max-devices-per-user limit?

4. **Account switching on a device.** If a user signs in with a different username on the same device, what happens to the old registration? Current design: old registration stays (orphaned). The device now points to the new user.

5. **Existing `POST /api/register` endpoint.** Should it be deprecated / removed, or kept for backwards compatibility? The new `/api/auth/login` supersedes it.

6. **API key distribution.** Currently the API key is hardcoded in `appsettings.json`. The mobile app doesn't use it for auth endpoints. Should the sync/protected endpoints migrate fully to Bearer auth and deprecate the API key?
