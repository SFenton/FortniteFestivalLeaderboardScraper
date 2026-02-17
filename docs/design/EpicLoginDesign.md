# Epic Games Login & User Authentication Design

## Overview

Today, the React Native app has no persistent identity. Users paste a one-time exchange code from a browser to fetch their scores — the token lives in React state and is gone the moment the app closes. Registration with the service (`POST /api/register`) requires the user to know their Epic account ID and manually provide it alongside a device ID. There is no friends list integration.

This document designs a proper **Sign in with Epic Games** flow that:

1. Authenticates the user via Epic's OAuth in an in-app browser.
2. Obtains their `account_id`, `displayName`, and friends list.
3. Registers them with FSTService automatically.
4. Issues our own refresh/access token pair so the user stays signed in across app launches.
5. Uses the cached identity to power score fetching, sync, and social features — without requiring the user to paste exchange codes ever again.

Critically, the app also retains a **Local Mode** — the existing exchange-code-based flow where the app talks directly to Epic's APIs without any service involvement. Users choose between these modes on a sign-in screen shown after the intro carousel.

---

## Current State

### React Native App

| Concern | Current Implementation |
|---------|----------------------|
| **Identity** | None. No user concept. |
| **Score fetching** | User pastes an exchange code (`SyncScreen.tsx`); `FestivalService.fetchScores()` trades it for a short-lived Epic token via `authorization_code` or `exchange_code` grant against the Epic Launcher client (`ec684b8c…`). Token is never stored. |
| **Registration** | No UI. Untouched `POST /api/register` endpoint expects raw `deviceId` + `accountId`. |
| **Secure storage** | None. AsyncStorage (unencrypted) is used for settings and onboarding flag only. |
| **Navigation** | 4 tabs: Songs, Suggestions, Statistics, Settings. No login / profile screen. |

### FSTService

| Concern | Current Implementation |
|---------|----------------------|
| **Service auth** | `EpicAuthService` authenticates the service itself via device-code flow against the Switch client (`98f7e42c…`). `TokenManager` persists the refresh token on disk, auto-refreshes on expiry. |
| **User registration** | `POST /api/register` stores a `(DeviceId, AccountId)` row in `RegisteredUsers` table. Triggers immediate personal DB build. Protected by API key. |
| **User tokens** | The service has no concept of per-user tokens. It has one service-level token for scraping. |
| **Friends** | No friends awareness whatsoever. |

---

## App Modes

The app supports two mutually exclusive modes:

| | **Online Mode** | **Local Mode** |
|---|---|---|
| **Identity** | Epic Games account via OAuth | None (anonymous) |
| **Score source** | Service-mediated sync (personal DB download) | Direct Epic API calls using exchange code |
| **Features** | Full: sync, friends, opps, rankings, score history | Core only: songs, scores, suggestions, statistics |
| **Storage** | Secure Storage (Keychain/Keystore) for FST tokens + SQLite for synced data | AsyncStorage/SQLite for locally-fetched data |
| **Service dependency** | Requires FSTService | Works fully offline (after initial song sync) |

The user picks their mode on a **Sign-In Screen** shown after the intro carousel (first launch) or whenever they are not signed in. They can switch modes later from Settings, but **switching wipes all local data** (scores DB, cached tokens, settings) to avoid cross-contamination between service-synced and locally-fetched data.

---

## Target Architecture

### Online Mode

```
┌──────────────────────────────────────────────────────────┐
│  React Native App                                        │
│                                                          │
│  ┌──────────────┐    ┌────────────────┐                  │
│  │ Epic OAuth    │───▶│ FSTService     │                  │
│  │ (in-app       │    │ /api/auth/*    │                  │
│  │  browser)     │    │                │                  │
│  └──────────────┘    └───────┬────────┘                  │
│                              │                           │
│         ┌────────────────────▼──────────────────┐        │
│         │ Secure Storage (Keychain / Keystore)   │        │
│         │ • FST refresh token                    │        │
│         │ • Account ID + display name            │        │
│         └───────────────────────────────────────┘        │
└──────────────────────────────────────────────────────────┘

                           │
                      HTTPS (API key)
                           │
                           ▼
┌──────────────────────────────────────────────────────────┐
│  FSTService                                              │
│                                                          │
│  POST /api/auth/login   ← authorization code from Epic   │
│  POST /api/auth/refresh ← FST refresh token              │
│  GET  /api/auth/me      ← FST access token               │
│  POST /api/auth/logout  ← FST refresh token              │
│                                                          │
│  ┌─────────────┐  ┌──────────────┐  ┌─────────────────┐ │
│  │ Epic OAuth   │  │ JWT issuer   │  │ Friends cache   │ │
│  │ token swap   │  │ (access +    │  │ (per-user)      │ │
│  │              │  │  refresh)    │  │                 │ │
│  └─────────────┘  └──────────────┘  └─────────────────┘ │
│                                                          │
│  Meta DB: UserSessions table (hashed refresh tokens)     │
└──────────────────────────────────────────────────────────┘
```

### Local Mode

```
┌──────────────────────────────────────────────────────────┐
│  React Native App                                        │
│                                                          │
│  ┌──────────────────────────────────────────┐            │
│  │ FestivalService (existing)               │            │
│  │ • Paste exchange code                    │            │
│  │ • authorization_code grant → Epic token  │            │
│  │ • Fetch leaderboards directly from Epic  │            │
│  │ • Token lives in memory only             │            │
│  └──────────────────────────────────────────┘            │
│                                                          │
│  ┌──────────────────────────────────────────┐            │
│  │ Local Persistence (existing)             │            │
│  │ • SQLite (iOS/Android) or AsyncStorage   │            │
│  │ • Songs + Scores tables                  │            │
│  └──────────────────────────────────────────┘            │
│                                                          │
│  No service dependency. No registration.                 │
│  No friends, opps, rankings, or score history.           │
└──────────────────────────────────────────────────────────┘
```

---

## Epic Games OAuth Flow

### Client Selection

The app currently uses the **Epic Games Launcher** client (`ec684b8c…`) with `authorization_code` grant. This works but has a drawback: Epic's authorization URL (`/id/api/redirect`) returns raw JSON rather than performing a proper OAuth redirect, which means the user has to manually copy a code.

We should instead use a proper **Authorization Code flow** with PKCE, pointing the in-app browser at Epic's standard authorize endpoint:

```
https://www.epicgames.com/id/authorize
  ?client_id={CLIENT_ID}
  &response_type=code
  &redirect_uri={REDIRECT_URI}
  &scope=basic_profile friends_list
```

**Scopes requested:**
- `basic_profile` — account ID, display name (always available).
- `friends_list` — read-only access to the user's friends list.

**Redirect URI:** Epic's Developer Portal requires a valid HTTPS URL, so custom
URI schemes (`festscoretracker://`) cannot be registered directly. Instead the
redirect URI points to FSTService, which 302-redirects back to the app's deep
link:

- iOS / Android: `{FSTService}/api/auth/epiccallback` → 302 → `festscoretracker://auth/callback?code=…`
- Windows: `http://localhost:{port}/auth/callback` (loopback — no proxy needed)

The FSTService callback endpoint (`GET /api/auth/epiccallback`) simply
forwards the `code` (and optional `state`) query parameters to the custom
scheme URL via an HTTP 302 redirect. It does **not** consume or swap the code
itself — that happens later when the app calls `POST /api/auth/login`.

**Client registration:** We need to register an Epic Games application at [dev.epicgames.com](https://dev.epicgames.com) to get a client ID that supports:
- `authorization_code` grant with PKCE
- Custom redirect URIs
- `basic_profile` and `friends_list` scopes

> **Open question:** Epic's developer portal may restrict which scopes are available to third-party apps. If `friends_list` is not grantable, we proceed without it and add it later if/when available. The rest of the design is unaffected.

### Flow Sequence

```
   App                     Epic (browser)              FSTService
    │                           │                           │
    │  1. Open in-app browser   │                           │
    │  ────────────────────────▶│                           │
    │     /id/authorize?...     │                           │
    │                           │                           │
    │  2. User signs in         │                           │
    │     (Epic handles this)   │                           │
    │                           │                           │
    │  3. Redirect to           │                           │
    │     FSTService callback   │                           │
    │     /api/auth/epiccallback│                           │
    │     ?code=XYZ             │                           │
    │                           │                           │
    │                           │   3b. 302 redirect to     │
    │◀─────────────────────────────── festscoretracker://   │
    │                           │      auth/callback?       │
    │                           │      code=XYZ             │
    │                           │                           │
    │  4. POST /api/auth/login  │                           │
    │  ─────────────────────────────────────────────────────▶
    │     { code: "XYZ" }       │                           │
    │                           │                           │
    │                           │   5. Swap code for        │
    │                           │      Epic token           │
    │                           │      POST /oauth/token    │
    │                           │      (server-side)        │
    │                           │                           │
    │                           │   6. Fetch friends list   │
    │                           │      GET /friends/api/    │
    │                           │      v1/{id}/summary      │
    │                           │                           │
    │                           │   7. Auto-register user   │
    │                           │      (RegisteredUsers +   │
    │                           │       personal DB build)  │
    │                           │                           │
    │                           │   8. Issue FST tokens     │
    │                           │      (JWT access +        │
    │                           │       opaque refresh)     │
    │                           │                           │
    │  9. Return tokens + profile                           │
    │◀─────────────────────────────────────────────────────  │
    │     { accessToken,        │                           │
    │       refreshToken,       │                           │
    │       accountId,          │                           │
    │       displayName,        │                           │
    │       friends: [...] }    │                           │
    │                           │                           │
    │  10. Store refresh token  │                           │
    │      in Secure Storage    │                           │
    │                           │                           │
```

### Why the Service Swaps the Code (Not the App)

1. **Client secret stays server-side.** Even with PKCE, having the service perform the token exchange means the OAuth client secret never ships in the app binary.
2. **Friends list access.** The service can call Epic's friends API server-side with the Epic token, cache the results, and never expose it to the client.
3. **Automatic registration.** The service already has the `RegisterUser` + `PersonalDbBuilder` flow. Doing it in the same request is atomic.
4. **Token issuance.** The service issues its own tokens (not Epic tokens) so we control expiry, revocation, and scope.

---

## FSTService Auth Endpoints

### `POST /api/auth/login`

**Request:**
```json
{
  "code": "abc123def456",
  "deviceId": "device-uuid-here",
  "platform": "ios"
}
```

**Server-side steps:**
1. Validate inputs.
2. Call Epic's `/account/api/oauth/token` with `grant_type=authorization_code`, `code={code}`, `redirect_uri={redirect_uri}`.
3. Extract `account_id`, `displayName` from Epic response.
4. (If scope granted) Call `GET https://friends-public-service-prod.ol.epicgames.com/friends/api/v1/{account_id}/summary` with the Epic bearer token. Extract friend account IDs.
5. Store/update friends list in a `UserFriends` table (or cache).
6. Auto-register the user: call existing `MetaDatabase.RegisterUser(deviceId, accountId)`.
7. Generate FST token pair (see Token Design below).
8. Insert session into `UserSessions` table.
9. Kill the Epic token (call `DELETE /account/api/oauth/sessions/{epic_access_token}` — good hygiene, we don't need it after this point).

**Response (200):**
```json
{
  "accessToken": "eyJhbG...",
  "refreshToken": "fst_rt_a1b2c3d4...",
  "expiresIn": 3600,
  "accountId": "abc123",
  "displayName": "PlayerOne",
  "isNewRegistration": true,
  "friends": ["friend_account_id_1", "friend_account_id_2"]
}
```

**Error cases:**
- `400` — missing or malformed code/deviceId.
- `401` — Epic rejected the authorization code (expired, already used).
- `502` — Epic API is down.

### `POST /api/auth/refresh`

**Request:**
```json
{
  "refreshToken": "fst_rt_a1b2c3d4..."
}
```

**Server-side steps:**
1. Look up the hashed refresh token in `UserSessions`.
2. Validate it hasn't expired (30-day lifetime).
3. Rotate: generate a new refresh token, invalidate the old one.
4. Issue a new access token.
5. Optionally re-fetch friends list if stale (>24h since last fetch).

**Response (200):**
```json
{
  "accessToken": "eyJhbG...",
  "refreshToken": "fst_rt_new_token...",
  "expiresIn": 3600
}
```

**Error cases:**
- `401` — refresh token expired, revoked, or not found. Client must re-authenticate with Epic.

### `GET /api/auth/me`

**Headers:** `Authorization: Bearer {fst_access_token}`

Returns the current user's profile and cached friends:
```json
{
  "accountId": "abc123",
  "displayName": "PlayerOne",
  "registeredAt": "2026-02-14T...",
  "friends": ["friend_id_1", "friend_id_2"],
  "friendsUpdatedAt": "2026-02-14T..."
}
```

### `POST /api/auth/logout`

**Request:**
```json
{
  "refreshToken": "fst_rt_a1b2c3d4..."
}
```

Revokes the refresh token. Client clears secure storage.

---

## Token Design

### FST Access Token (JWT)

Short-lived, stateless. Verified by the service without a DB lookup.

```
Header: { "alg": "HS256", "typ": "JWT" }
Payload: {
  "sub": "epic_account_id",
  "name": "PlayerOne",
  "deviceId": "device-uuid",
  "iat": 1739500000,
  "exp": 1739503600    // 1 hour
}
Signed with: HMAC-SHA256 using a server-side secret from config
```

**Lifetime:** 1 hour. Short enough that revocation isn't critical (worst case, a logged-out user has 1h of residual access to their own data).

### FST Refresh Token (Opaque)

Long-lived, stored server-side (hashed), rotated on every use.

- Format: `fst_rt_` + 32 random bytes (base64url).
- Stored in `UserSessions` as a SHA-256 hash.
- **Lifetime:** 30 days. After 30 days of inactivity, the user must re-authenticate with Epic.
- **Rotation:** Every refresh issues a new refresh token and invalidates the old one. This limits the blast radius of a stolen token.

### Why Not Just Use Epic Tokens?

- Epic refresh tokens expire in ~8 hours. Our users may go days between app opens.
- We don't want to hold Epic tokens long-term — they grant broad access to the user's Epic account.
- Our own tokens let us control scope (read-only access to FST data), lifetime, and revocation independently.

---

## Database Schema Changes

### `UserSessions` (new table in `fst-meta.db`)

```sql
CREATE TABLE IF NOT EXISTS UserSessions (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    AccountId       TEXT    NOT NULL,
    DeviceId        TEXT    NOT NULL,
    RefreshTokenHash TEXT   NOT NULL UNIQUE,
    Platform        TEXT,              -- 'ios', 'android', 'windows'
    IssuedAt        TEXT    NOT NULL,  -- ISO 8601
    ExpiresAt       TEXT    NOT NULL,  -- ISO 8601 (30 days from issue)
    LastRefreshedAt TEXT,
    RevokedAt       TEXT,              -- NULL if active

    FOREIGN KEY (AccountId) REFERENCES RegisteredUsers(AccountId)
);
CREATE INDEX IF NOT EXISTS idx_sessions_account ON UserSessions(AccountId);
CREATE INDEX IF NOT EXISTS idx_sessions_token   ON UserSessions(RefreshTokenHash) WHERE RevokedAt IS NULL;
```

### `UserFriends` (new table in `fst-meta.db`)

```sql
CREATE TABLE IF NOT EXISTS UserFriends (
    AccountId       TEXT NOT NULL,
    FriendAccountId TEXT NOT NULL,
    UpdatedAt       TEXT NOT NULL,     -- ISO 8601

    PRIMARY KEY (AccountId, FriendAccountId)
);
CREATE INDEX IF NOT EXISTS idx_friends_account ON UserFriends(AccountId);
```

### Changes to `RegisteredUsers`

Add columns (backwards-compatible `ALTER TABLE ADD COLUMN`):

```sql
ALTER TABLE RegisteredUsers ADD COLUMN DisplayName TEXT;
ALTER TABLE RegisteredUsers ADD COLUMN Platform TEXT;
ALTER TABLE RegisteredUsers ADD COLUMN LastLoginAt TEXT;
```

---

## React Native App Changes

### New Dependencies

| Package | Purpose |
|---------|---------|
| `react-native-app-auth` | OAuth 2.0 Authorization Code + PKCE flow. Wraps AppAuth (iOS/Android) to handle browser open, redirect interception, and code exchange natively in one call. 3.5K+ GitHub stars, actively maintained. |
| `react-native-app-auth-windows` | Windows plugin for `react-native-app-auth`. Required for `react-native-windows` support — provides the same AppAuth flow via a loopback HTTP listener on Windows since UWP/WinUI3 custom URI scheme activation is unreliable. Must be installed alongside the core package. |
| `react-native-keychain` (iOS/Android) | Secure storage for FST refresh token. Uses iOS Keychain / Android Keystore. |
| `expo-secure-store` (alternative) | Alternative secure storage if already using Expo modules. |
| `react-native-uuid` or `uuid` | Generate stable device IDs on first launch. |

> **Why `react-native-app-auth`?** There is no official Epic Games SDK or npm package for React Native. Epic offers the EOS SDK as a native C/C++ library with Unreal/Unity/C# wrappers, but no RN bindings exist. Since the design uses Epic's standard OAuth authorize endpoint, `react-native-app-auth` handles the entire browser → redirect → code extraction flow natively, eliminating the need for manual deep link wiring. Epic's 2FA is handled entirely within their login page during the browser session — no 2FA library is needed on our side.

### New Files

```
src/
├── core/
│   └── auth/
│       ├── authService.ts          // FST auth API client (online mode)
│       ├── authSession.ts          // Session state management
│       ├── secureTokenStorage.ts   // Keychain/Keystore wrapper
│       ├── deviceId.ts             // Stable device ID generation
│       └── appMode.ts              // AppMode type + AsyncStorage helpers
├── app/
│   └── auth/
│       ├── AuthContext.tsx          // React context for auth + mode state
│       └── dataWipe.ts             // wipeAllLocalData() utility
├── screens/
│   ├── SignInScreen.tsx            // Mode selection: Epic login vs. local
│   └── ProfileScreen.tsx           // Replaces/augments SettingsScreen
```

### Auth Service (`src/core/auth/authService.ts`)

Platform-agnostic API client for the new FST auth endpoints:

```typescript
export interface AuthTokens {
  accessToken: string;
  refreshToken: string;
  expiresIn: number;
}

export interface LoginResult extends AuthTokens {
  accountId: string;
  displayName: string;
  isNewRegistration: boolean;
  friends: string[];
}

export interface AuthService {
  login(code: string, deviceId: string, platform: string): Promise<LoginResult>;
  refresh(refreshToken: string): Promise<AuthTokens>;
  getMe(accessToken: string): Promise<UserProfile>;
  logout(refreshToken: string): Promise<void>;
}
```

### Secure Token Storage (`src/core/auth/secureTokenStorage.ts`)

Wraps `react-native-keychain` for iOS/Android and falls back to encrypted AsyncStorage or DPAPI-backed storage on Windows:

```typescript
export interface SecureTokenStorage {
  saveTokens(tokens: { refreshToken: string; accountId: string; displayName: string }): Promise<void>;
  loadTokens(): Promise<{ refreshToken: string; accountId: string; displayName: string } | null>;
  clearTokens(): Promise<void>;
}
```

**Storage keys:** Under a service name like `com.fnfestival.auth`.

### Device ID (`src/core/auth/deviceId.ts`)

Generate a stable UUID on first launch, persist to AsyncStorage:

```typescript
// Key: 'fnfestival:deviceId'
export async function getOrCreateDeviceId(): Promise<string> {
  let id = await AsyncStorage.getItem('fnfestival:deviceId');
  if (!id) {
    id = uuid.v4();
    await AsyncStorage.setItem('fnfestival:deviceId', id);
  }
  return id;
}
```

### Auth Context (`src/app/auth/AuthContext.tsx`)

Top-level context wrapping `FestivalProvider`:

```typescript
/** The two app modes. Persisted to AsyncStorage under 'fnfestival:appMode'. */
export type AppMode = 'online' | 'local';

type AuthState =
  | { status: 'loading' }           // Checking secure storage / mode on mount
  | { status: 'choosing' }          // No mode selected yet (first launch after carousel)
  | { status: 'local' }             // Local mode — no service, exchange code flow
  | { status: 'unauthenticated' }   // Online mode, but no valid session
  | { status: 'authenticated'; accountId: string; displayName: string; accessToken: string };

interface AuthActions {
  /** Enter online mode and open Epic login. */
  signInWithEpic(): Promise<void>;

  /** Enter local mode. Persists mode choice, transitions immediately. */
  useLocally(): Promise<void>;

  /** Sign out (online mode) or exit local mode. Wipes all local data
   *  (scores DB, cached tokens, settings) and returns to the sign-in screen. */
  signOut(): Promise<void>;

  /** Switch from local → online or online → local.
   *  Confirms with the user that all local data will be erased, then
   *  performs a full wipe and transitions to the chosen mode's entry point. */
  switchMode(target: AppMode): Promise<void>;

  /** Returns a valid FST access token, auto-refreshing if needed.
   *  Returns null in local mode (not applicable). */
  getAccessToken(): Promise<string | null>;
}
```

**Startup flow:**

```
App mount
  │
  ├─▶ Read 'fnfestival:appMode' from AsyncStorage
  │
  ├─▶ If mode == 'local':
  │     └─▶ status = 'local' (go straight to main app with local behavior)
  │
  ├─▶ If mode == 'online':
  │     ├─▶ Load tokens from Secure Storage
  │     ├─▶ If refresh token exists:
  │     │     ├─▶ POST /api/auth/refresh
  │     │     ├─▶ Success → status = 'authenticated', store new tokens
  │     │     └─▶ 401 → status = 'unauthenticated' (must re-login with Epic)
  │     └─▶ If no refresh token:
  │           └─▶ status = 'unauthenticated'
  │
  └─▶ If mode not set (first launch / post-carousel):
        └─▶ status = 'choosing'
```

### Sign-In Screen (`src/screens/SignInScreen.tsx`)

Shown when `status === 'choosing'` or `status === 'unauthenticated'`. The screen is context-aware — the second option changes depending on whether the user is making a first-time choice or has an expired online session.

**First launch (`status === 'choosing'`):**

```
┌───────────────────────────────────────────┐
│                                           │
│          Fortnite Festival Tracker        │
│                                           │
│   ┌───────────────────────────────────┐   │
│   │   ⚡ Sign in with Epic Games     │   │
│   │                                   │   │
│   │   Sync your scores automatically, │   │
│   │   see friends, rankings, & more.  │   │
│   └───────────────────────────────────┘   │
│                                           │
│   ┌───────────────────────────────────┐   │
│   │   📂 Use Locally                  │   │
│   │                                   │   │
│   │   Fetch scores directly from Epic │   │
│   │   with an exchange code. No       │   │
│   │   account required.               │   │
│   └───────────────────────────────────┘   │
│                                           │
└───────────────────────────────────────────┘
```

**Expired session (`status === 'unauthenticated'`):**

The user was previously in online mode but their refresh token expired or was revoked. They need to re-authenticate — or switch to local mode.

```
┌───────────────────────────────────────────┐
│                                           │
│        Session Expired                    │
│        Sign in again to sync your         │
│        scores and continue.               │
│                                           │
│   ┌───────────────────────────────────┐   │
│   │   ⚡ Sign in with Epic Games     │   │
│   │                                   │   │
│   └───────────────────────────────────┘   │
│                                           │
│   ┌───────────────────────────────────┐   │
│   │   🔄 Switch to Local Mode         │   │
│   │                                   │   │
│   │   Use exchange codes instead.     │   │
│   │   Synced data will be cleared.    │   │
│   └───────────────────────────────────┘   │
│                                           │
└───────────────────────────────────────────┘
```

**"Sign in with Epic Games" (both contexts):**
1. Opens the in-app browser to Epic's authorize URL.
2. Epic handles login (email/password, 2FA, etc.).
3. On success, Epic redirects to `{FSTService}/api/auth/epiccallback?code=XYZ`.
4. FSTService 302-redirects to `festscoretracker://auth/callback?code=XYZ`.
5. The app intercepts the deep link, extracts the code.
5. Calls `POST /api/auth/login` with the code + device ID.
6. Stores the returned tokens in Secure Storage.
7. Persists `appMode = 'online'` to AsyncStorage.
8. Transitions to `status = 'authenticated'`.

**"Use Locally" (first launch only):**
1. Persists `appMode = 'local'` to AsyncStorage.
2. Transitions to `status = 'local'`.
3. Main app opens — the Sync tab shows the existing exchange code UI.

**"Switch to Local Mode" (expired session only):**
1. Shows a confirmation dialog warning that synced data will be cleared.
2. On confirm: calls `wipeAllLocalData()` (clears scores DB, tokens, settings, image cache).
3. Persists `appMode = 'local'` to AsyncStorage.
4. Transitions to `status = 'local'`.

### Navigation Changes

```
                         ┌──────────────────┐
                         │ AuthContext check │
                         └────────┬─────────┘
                                  │
               ┌──────────────────┼──────────────────┐
               │                  │                  │
          choosing /         'local'           'authenticated'
        unauthenticated          │                  │
               │                  │                  │
       ┌───────▼───────┐  ┌──────▼───────┐  ┌───────▼──────────┐
       │ SignInScreen   │  │ Main App     │  │ Main App (tabs)  │
       │               │  │ (tabs)       │  │ Songs │ Sugg │   │
       │ • Epic Login  │  │ + SyncScreen │  │ Stats │ Settings │
       │ • Use Locally │  │   (exchange  │  │ (no SyncScreen,  │
       │               │  │    code UI)  │  │  auto-sync)      │
       └───────────────┘  └──────────────┘  └──────────────────┘
```

**Key differences by mode in main app:**

| Feature | Online Mode | Local Mode |
|---------|------------|------------|
| **Sync tab** | Auto-sync status card ("Last synced: {time}", manual refresh button) | Existing exchange code UI (paste code, generate code link, fetch scores) |
| **Suggestions** | Full suggestions (uses service-synced data) | Full suggestions (uses locally-fetched data) |
| **Statistics** | Rankings, opps, friends | Basic stats only (local data) |
| **Settings → Account** | Display name, sign out, switch to local | "Using locally", switch to online |

### Settings Screen Additions

**Online mode:**
- **Account section** at the top: display name, account ID (masked), "Sign Out" button.
- Sign out calls `POST /api/auth/logout`, clears Secure Storage, wipes local DB, returns to `SignInScreen`.
- "Switch to Local Mode" option (under Account section, with warning about data wipe).

**Local mode:**
- **Mode section** at the top: "Using Locally" label.
- "Sign in with Epic Games" option to switch to online mode (with warning about data wipe).

---

## Mode Switching & Data Wipe

Switching between modes **clears all local data** to prevent stale or conflicting state. The two modes use fundamentally different data sources (service-synced SQLite DB vs. locally-fetched scores), and mixing them would produce inconsistent state.

### What Gets Wiped

```typescript
async function wipeAllLocalData(): Promise<void> {
  // 1. Clear secure storage (FST tokens)
  await secureTokenStorage.clearTokens();

  // 2. Clear scores database
  //    SQLite (iOS/Android): drop Songs + Scores tables
  //    AsyncStorage (Windows): remove 'fnfestival:songs', 'fnfestival:scores'
  await festivalPersistence.deleteAllScores();
  await festivalPersistence.deleteAllSongs();

  // 3. Clear app mode
  await AsyncStorage.removeItem('fnfestival:appMode');

  // 4. Clear settings (instrument toggles, sort prefs, etc.)
  await AsyncStorage.removeItem('fnfestival:settings');

  // 5. Clear image cache
  await imageCache.clear();

  // 6. Clear FestivalContext in-memory state
  festivalContext.clearEverything();
}
```

### What Gets Preserved

- **Device ID** (`fnfestival:deviceId`) — stable across mode switches. The service uses this to identify the device regardless of mode.
- **Onboarding flag** (`fnfestival:onboardingComplete`) — the intro carousel should not replay on mode switch.

### User Confirmation

Before any mode switch, show a confirmation dialog:

> **Switch to {Online/Local} Mode?**
>
> All locally stored scores, settings, and cached data will be cleared. You'll start fresh in {Online/Local} mode.
>
> [Cancel]  [Switch]

This prevents accidental data loss.

---

## Score Fetching Changes

### Online Mode (Service-Mediated)

```
App → sign in once → FST token → GET /api/sync/{deviceId} → download personal DB
```

- The service already scrapes all leaderboards and builds per-device personal DBs.
- The app just downloads its personal DB (~1–2 MB SQLite file) via the existing sync endpoint.
- Score fetching drops from 12,000 requests over 20 minutes to **one request in seconds**.
- Sync tab shows a status card: last sync time, "Sync Now" button, auto-sync on launch.

**Sync flow:**
1. Authenticated user opens the app.
2. App checks `GET /api/sync/{deviceId}/version` for new data.
3. If newer than local: `GET /api/sync/{deviceId}` → download → replace local SQLite DB.
4. App reads scores from the local SQLite DB (already implemented in `SqliteFestivalPersistence`).

### Local Mode (Direct Epic API — Existing Behavior)

```
App → paste exchange code → Epic OAuth → fetch leaderboards directly from Epic
```

- This is the existing `FestivalService.fetchScores()` flow, **preserved as-is**.
- App holds an Epic bearer token in memory (not persisted).
- App makes ~2000 × 6 = 12,000 HTTP requests directly to Epic's leaderboard API.
- Slower (~20 min at DOP=16), but works without any service dependency.
- Sync tab shows the existing exchange code UI: text field, "Generate Code" link, "Retrieve Scores" button, progress bar, log output.
- No registration, no friends, no rankings, no score history — purely local.

---

## Friends List Integration

### What We Get

Epic's friends summary endpoint returns:

```
GET /friends/api/v1/{accountId}/summary
Authorization: Bearer {epic_token}
```

Response includes `friends[]` — each with `accountId`, `displayName`, and relationship metadata.

### What We Store

Just the friend account IDs, cached in `UserFriends`. Refreshed:
- On every login.
- On every token refresh if >24h stale.
- On-demand via `POST /api/auth/refresh-friends` (future).

### What It Enables

1. **Friends leaderboard:** For any song, show how the user's friends rank. Query the instrument DB for entries matching friend account IDs → return a mini-leaderboard.
2. **Friends in Opps:** The Opps feature can flag when an Opp is also a friend ("Your friend X is 3 ranks ahead on Hotel California").
3. **Friend activity:** If friends are also registered users, show their score changes.

### API Endpoint

```
GET /api/friends/{accountId}/leaderboard/{songId}/{instrument}
Authorization: Bearer {fst_access_token}
```

Returns entries for the user's friends on a specific song/instrument. Only returns data for friends whose entries exist in the instrument DB (scraped or backfilled).

---

## Security Considerations

### Token Storage

| Platform | Storage | Protection |
|----------|---------|------------|
| iOS | Keychain (`react-native-keychain`) | Hardware-backed, encrypted at rest, per-app sandboxed |
| Android | Keystore (`react-native-keychain`) | Hardware-backed on supported devices, encrypted |
| Windows | DPAPI or encrypted file | User-profile-scoped encryption |

**Never** store tokens in AsyncStorage (unencrypted JSON file on disk).

### API Key vs. User Token

The existing API key (`X-API-Key` header) protects service-level endpoints. User auth endpoints add a second layer:

| Endpoint | Auth Required |
|----------|--------------|
| `POST /api/auth/login` | API key only (user is authenticating) |
| `POST /api/auth/refresh` | API key only (user is re-authenticating) |
| `GET /api/auth/me` | API key + FST access token |
| `POST /api/auth/logout` | API key only (passing refresh token in body) |
| `GET /api/sync/{deviceId}` | API key + FST access token (verify token's `deviceId` matches) |
| `GET /api/friends/...` | API key + FST access token (verify token's `accountId` matches) |

### Refresh Token Rotation

Every refresh invalidates the old token and issues a new one. If an attacker intercepts a refresh token and the legitimate user also refreshes, one of them will get a 401 — which is a signal that something is wrong. The legitimate user re-authenticates with Epic; the attacker's stolen token is now dead.

### Epic Token Handling

The Epic token obtained during login is used server-side only, for the duration of the login request. It is:
1. Used to fetch the friends list.
2. Immediately killed via `DELETE /account/api/oauth/sessions/{token}`.
3. Never stored, never sent to the client.

---

## Implementation Plan

### Phase 1: Service Auth Endpoints

1. **JWT infrastructure** — Add a `JwtSettings` config section (`Secret`, `Issuer`, `AccessTokenLifetimeMinutes`). Add a `JwtTokenService` that mints and validates access tokens.
2. **`UserSessions` table** — Add to `MetaDatabase.EnsureSchema()`.
3. **`UserFriends` table** — Add to `MetaDatabase.EnsureSchema()`.
4. **`UserAuthService`** — New service class that:
   - Swaps an Epic authorization code for an Epic token (reuses `EpicAuthService` patterns).
   - Fetches friends list from Epic.
   - Auto-registers the user.
   - Issues FST tokens.
   - Handles refresh with rotation.
   - Handles logout/revocation.
5. **Auth endpoints** — `POST /api/auth/login`, `POST /api/auth/refresh`, `GET /api/auth/me`, `POST /api/auth/logout`.
6. **User-token auth handler** — A second `AuthenticationScheme` ("Bearer") alongside the existing "ApiKey" scheme. Endpoints like `/api/sync/{deviceId}` require both.

### Phase 2: React Native Dual-Mode Login

1. **Secure storage adapter** — `react-native-keychain` integration with platform fallbacks.
2. **Device ID generation** — UUID v4, persisted to AsyncStorage.
3. **App mode persistence** — `fnfestival:appMode` in AsyncStorage (`'online'` | `'local'`).
4. **Data wipe utility** — `wipeAllLocalData()` for mode switching (clears scores DB, tokens, settings, image cache).
5. **Auth service client** — TypeScript client for the new `/api/auth/*` endpoints.
6. **AuthContext** — React context managing auth state + mode, with `signInWithEpic()`, `useLocally()`, `switchMode()`, `signOut()` actions.
7. **SignInScreen** — Two-option screen: "Sign in with Epic Games" and "Use Locally". Shown after intro carousel on first launch, or when not signed in.
8. **Redirect handling** — Handled natively by `react-native-app-auth` (iOS/Android) and `react-native-app-auth-windows` (Windows loopback). No manual deep link registration needed.
9. **Navigation gating** — Three-way: `choosing`/`unauthenticated` → SignInScreen, `local` → main app with SyncScreen, `authenticated` → main app with auto-sync.

### Phase 3: Sync Integration

1. **Online mode sync** — Authenticated users use `GET /api/sync/{deviceId}` instead of direct Epic calls.
2. **Auto-sync on launch** — Check version → download if newer → replace local DB.
3. **Conditional SyncScreen** — Online mode: status card ("Last synced: {time}", refresh button). Local mode: existing exchange code UI preserved.
4. **Profile / mode section in Settings** — Online: display name, sign out, switch to local. Local: "Using locally", switch to online.

### Phase 4: Friends

1. **Friends leaderboard endpoint** — `GET /api/friends/{accountId}/leaderboard/{songId}/{instrument}`.
2. **Friends display in song details** — Show friend scores alongside the user's score.
3. **Friends in Opps** — Tag Opps that are friends.
4. **Periodic friends refresh** — Re-fetch friends list on token refresh if stale.

---

## Post-Pass Sequence Update

With friends and auth in place, the post-scrape pipeline becomes:

```
Scrape pass complete
  │
  ├─▶ Resolve account names
  ├─▶ Compute AccountRankings (per-instrument)
  ├─▶ Compute CompositeRankings
  ├─▶ Compute Opps (with friend tagging)
  ├─▶ Run score backfill for new registrations
  ├─▶ Rebuild personal DBs (now includes rankings + opps + friend flags)
  └─▶ Cleanup expired sessions (DELETE WHERE ExpiresAt < NOW AND RevokedAt IS NULL)
```

---

## Open Questions

1. **Epic developer app registration.** Do we already have a registered Epic Games application with the right redirect URIs and scopes? If not, this is the first prerequisite step.
2. **`friends_list` scope availability.** Third-party apps may not have access to this scope. Need to verify during app registration. The core login flow works without it.
3. **Windows OAuth redirect.** `react-native-app-auth-windows` uses a loopback HTTP listener for the redirect. Verify it works correctly with `react-native-windows` 0.81.x and WinUI3.
4. **Multiple devices, one account.** The current `RegisteredUsers` schema uses `(DeviceId, AccountId)` as a composite key. A user can register multiple devices. Sessions are per-device, so signing out on one device doesn't affect the other.
5. **Account switching.** If a user signs in with a different Epic account on the same device, the old registration should be deactivated and a new one created. The `deviceId` ties to only one `accountId` at a time.
6. **Rate limiting for auth endpoints.** Login and refresh should have their own rate limit bucket (e.g., 10/min per IP) to prevent brute-force attacks on the token-swap endpoint.
7. **Local mode feature gating.** Some UI elements (friends, rankings, opps, score history) are meaningless in local mode. Need per-mode feature flags that hide or grey out inapplicable sections rather than showing empty/error states.
8. **Migration from local to online.** When a local-mode user switches to online, should we attempt to preserve their locally-fetched scores by uploading them? Current design says no (full wipe) for simplicity, but this could be revisited if users find it frustrating.
9. **Exchange code client.** The existing Epic Launcher client (`ec684b8c…`) used for local mode's exchange code flow is a well-known public client. If Epic deprecates or rate-limits it, local mode breaks. This is an accepted risk — online mode is the recommended path.
