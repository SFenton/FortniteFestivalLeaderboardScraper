# FSTService Authentication & Authorization Deep Dive

## Epic OAuth Flow

### Overview
FSTService authenticates with Epic Games to obtain access tokens for calling Epic's leaderboard, events, and account APIs. Two distinct OAuth flows exist:

### 1. Device Code Flow (Service/Scraper Authentication)
Used by the background scraper to authenticate as a specific Epic account. This is one-time interactive setup.

**Client**: `fortniteNewSwitchGameClient` (ID: `98f7e42c2e3a4f86a74eb43fbb41ed39`, overridable via `EPIC_CLIENT_ID` / `EPIC_CLIENT_SECRET` env vars)

**Steps**:
1. `EpicAuthService.GetClientCredentialsTokenAsync()` — obtains anonymous client_credentials token
2. `EpicAuthService.StartDeviceCodeFlowAsync()` — POSTs to `/account/api/oauth/deviceAuthorization` with bearer CC token → returns `DeviceAuthorizationResponse` with `user_code`, `device_code`, `verification_uri_complete`
3. User opens `verification_uri_complete` in browser and approves login
4. `EpicAuthService.PollDeviceCodeAsync()` — polls `/account/api/oauth/token` with `grant_type=device_code` every 5+ seconds until user approves or timeout
5. On success: returns `EpicTokenResponse` with `access_token`, `refresh_token`, `account_id`, `display_name`
6. Refresh token persisted to disk via `ICredentialStore` → `FileCredentialStore`

**Entry point**: `ScraperWorker` calls `TokenManager.PerformDeviceCodeSetupAsync()` when `--setup` CLI flag is passed

### 2. Authorization Code Exchange (User-Facing OAuth)
Used for user-facing login (mobile app / web). Exchange an authorization code for tokens using EOS application credentials (separate from the Switch client).

**Method**: `EpicAuthService.ExchangeAuthorizationCodeAsync(code, clientId, clientSecret, redirectUri)`

### 3. User Token Refresh
`EpicAuthService.RefreshUserTokenAsync(refreshToken, clientId, clientSecret)` — refreshes a user's token with arbitrary client credentials (not the Switch client). Referenced by `TokenVault` for transparent stored user token refresh.

### Epic API Base URL
All OAuth calls target: `https://account-public-service-prod.ol.epicgames.com`

### Token Verification
`EpicAuthService.VerifyTokenAsync(accessToken)` — GETs `/account/api/oauth/verify` with bearer token, returns bool.

---

## API Key Authentication

### Mechanism
FSTService uses a custom ASP.NET Core authentication scheme called `"ApiKey"` to protect admin/privileged endpoints.

### Key Classes
- **`ApiSettings`** (`FSTService/Api/ApiKeyAuth.cs`): Configuration POCO bound to `Api` section of appsettings
  - `ApiKey`: The server-side API key string
  - `AllowedOrigins`: CORS origin whitelist
- **`ApiKeyAuthOptions`** (`FSTService/Api/ApiKeyAuth.cs`): Extends `AuthenticationSchemeOptions`, holds `ApiKey`
- **`ApiKeyAuthHandler`** (`FSTService/Api/ApiKeyAuth.cs`): Custom `AuthenticationHandler<ApiKeyAuthOptions>`

### Validation Logic (`HandleAuthenticateAsync`)
1. If `Options.ApiKey` is empty/null → reject with "API key not configured on server"
2. If `X-API-Key` header is missing → `AuthenticateResult.NoResult()` (allows anonymous endpoints to pass through)
3. If header value doesn't match → `AuthenticateResult.Fail("Invalid API key")` + warning log with IP
4. If match → creates `ClaimsPrincipal` with `ClaimTypes.Name = "api-client"` → `AuthenticateResult.Success`

### Configuration Sources
- **appsettings.json**: `Api.ApiKey` = `"dfc3d5b232a24c458db366650a1961de"` (development default)
- **Environment variable**: `Api__ApiKey` (standard .NET config override)
- **appsettings.Development.json**: Suppresses auth handler logging to Warning level

### Registration (Program.cs)
```csharp
builder.Services.AddAuthentication("ApiKey")
    .AddScheme<ApiKeyAuthOptions, ApiKeyAuthHandler>("ApiKey", opts =>
    {
        opts.ApiKey = apiSettings.ApiKey;
    });
builder.Services.AddAuthorization();
```

---

## Token Management

### TokenManager (`FSTService/Auth/TokenManager.cs`)
Singleton service managing the Epic access token lifecycle for the scraper account.

**Token Acquisition Strategy** (`GetAccessTokenAsync`):
1. Return cached `_currentToken` if valid (>5 min until expiry)
2. Try refresh via in-memory refresh token
3. Try loading persisted credentials from `ICredentialStore` and refreshing
4. If all fail → return `null` (caller must handle re-auth)

**Thread Safety**: Uses `SemaphoreSlim(1,1)` to serialize token refresh operations

**Refresh Token Rolling**: Each refresh yields a new refresh token (~8h lifetime). After every successful refresh, the new refresh token is persisted to disk immediately.

**Failure Mode**: If the service is offline >8 hours, the refresh token expires. User must re-run with `--setup` to perform device code login again.

**Events**: `DeviceCodeLoginRequired` event fires with the verification URL when interactive login is needed.

### ICredentialStore / FileCredentialStore
- **Interface** (`FSTService/Auth/IDeviceAuthStore.cs`): `LoadAsync()` / `SaveAsync()` for `StoredCredentials`
- **Implementation** (`FSTService/Auth/FileDeviceAuthStore.cs`): JSON file at `ScraperOptions.DeviceAuthPath` (default: `data/device-auth.json`)
- Gracefully handles missing/corrupt files (returns null, logs warning)
- Creates parent directories on save if they don't exist

### StoredCredentials (`FSTService/Auth/AuthModels.cs`)
```
AccountId, RefreshToken, DisplayName, SavedAt
```

### Token Consumers
TokenManager is injected as a singleton into:
- **ScraperWorker**: Obtains tokens for all scrape operations (3+ calls per scrape cycle)
- **AccountNameResolver**: Resolves Epic account IDs to display names via Epic API
- **PostScrapeOrchestrator**: FirstSeenSeason calculation, post-scrape refresh
- **BackfillOrchestrator**: Account backfill operations
- **AdminEndpoints**: `/api/admin/epic-token`, `/api/backfill/{accountId}`, `/api/firstseen/calculate`
- **DiagEndpoints**: `/api/diag/events`, `/api/diag/leaderboard`
- **PlayerEndpoints**: On-demand score lookups

---

## Auth Middleware

### Pipeline Order (Program.cs)
```
app.UseCors()
app.UseWebSockets()
app.UseForwardedHeaders(...)
app.UseRateLimiter()
app.UseAuthentication()     ← ApiKeyAuthHandler runs here
app.UseAuthorization()      ← .RequireAuthorization() enforced here
```

### PathTraversalGuardMiddleware (`FSTService/Api/PathTraversalGuardMiddleware.cs`)
Security middleware (registered earlier in pipeline at line 384) that blocks directory traversal attacks. Checks request path and query string for `..`, `%2e%2e`, `%2E%2E`, etc. Returns 400 if detected.

### CORS
```csharp
policy.WithOrigins(apiSettings.AllowedOrigins)  // default: ["http://localhost:3000"]
      .AllowAnyHeader()
      .AllowAnyMethod();
```

### Rate Limiting
Three named policies, all using the same FixedWindow config per client IP:
- `"public"`: 100 req/s — used by all public read endpoints
- `"auth"`: 100 req/s — (defined but not widely used)
- `"protected"`: 100 req/s — used by all `.RequireAuthorization()` endpoints

Plus a global limiter with the same 100 req/s per IP.

In `Testing` environment, all rate limits are disabled (`GetNoLimiter`).

---

## Endpoint Protection

### Protected Endpoints (require `X-API-Key` header)
All endpoints calling `.RequireAuthorization()`:

| File | Route | Method | Purpose |
|---|---|---|---|
| AdminEndpoints | `/api/status` | GET | Scrape status + entry counts |
| AdminEndpoints | `/api/admin/epic-token` | GET | Current token info |
| AdminEndpoints | `/api/admin/shop/refresh` | POST | Trigger shop scrape |
| AdminEndpoints | `/api/register` | POST | Register user device |
| AdminEndpoints | `/api/register` | DELETE | Unregister user device |
| AdminEndpoints | `/api/firstseen/calculate` | POST | Calculate first-seen seasons |
| AdminEndpoints | `/api/admin/regenerate-paths` | POST | Regenerate CHOpt paths |
| AdminEndpoints | `/api/backfill/{accountId}/status` | GET | Backfill status |
| AdminEndpoints | `/api/backfill/{accountId}` | POST | Trigger account backfill |
| AdminEndpoints | `/api/leaderboard-population` | GET | Leaderboard population data |
| RivalsEndpoints | `/api/player/{accountId}/rivals/inspect` | GET | Rivals inspection (debug) |
| RivalsEndpoints | `/api/player/{accountId}/rivals/recompute` | POST | Force rivals recomputation |

### Public Endpoints (no auth required)
All endpoints with only `.RequireRateLimiting("public")`:

| File | Routes |
|---|---|
| HealthEndpoints | `/health`, `/ready`, `/api/version` |
| FeatureEndpoints | `/api/features` |
| AccountEndpoints | `/api/account/check`, `/api/account/search` |
| SongEndpoints | `/api/songs`, `/api/songs/{songId}`, `/api/songs/max-scores` |
| LeaderboardEndpoints | `/api/leaderboard/{songId}/{instrument}`, `/api/leaderboard/{songId}` |
| PlayerEndpoints | `/api/player/{accountId}`, `/api/player/{accountId}/track`, `/api/player/{accountId}/sync-status`, `/api/player/{accountId}/stats`, `/api/player/{accountId}/history` |
| RivalsEndpoints | Most rivals GET endpoints (summaries, details, combos) |
| LeaderboardRivalsEndpoints | `/api/leaderboard/{songId}/{instrument}/rivals`, all GET endpoints |
| RankingsEndpoints | All rankings GET endpoints |
| DiagEndpoints | `/api/diag/events`, `/api/diag/leaderboard` (no auth but use service token internally) |
| WebSocketEndpoints | WebSocket connections (no auth/rate limit) |

### Notable: DiagEndpoints
These are public (no `.RequireAuthorization()`) but internally use `TokenManager.GetAccessTokenAsync()` to proxy calls to Epic's API. They act as authenticated proxies without requiring client-side API keys.

---

## Key Classes

| Class | File | Role |
|---|---|---|
| `EpicAuthService` | `FSTService/Auth/EpicAuthService.cs` | All Epic OAuth HTTP interactions: device code flow, token refresh, auth code exchange, token verification |
| `TokenManager` | `FSTService/Auth/TokenManager.cs` | Singleton managing access token lifecycle — refresh, persist, auto-renew with 5-min buffer |
| `ICredentialStore` | `FSTService/Auth/IDeviceAuthStore.cs` | Interface for persisting refresh tokens across restarts |
| `FileCredentialStore` | `FSTService/Auth/FileDeviceAuthStore.cs` | JSON file implementation of `ICredentialStore` (default: `data/device-auth.json`) |
| `StoredCredentials` | `FSTService/Auth/AuthModels.cs` | DTO for persisted credentials (AccountId, RefreshToken, DisplayName, SavedAt) |
| `DeviceAuthorizationResponse` | `FSTService/Auth/AuthModels.cs` | DTO for device code flow initiation response |
| `EpicTokenResponse` | `FSTService/Auth/AuthModels.cs` | DTO for OAuth token response (access token, refresh token, expiry, account info) |
| `ApiSettings` | `FSTService/Api/ApiKeyAuth.cs` | Config POCO for API key + CORS origins |
| `ApiKeyAuthHandler` | `FSTService/Api/ApiKeyAuth.cs` | Custom `AuthenticationHandler` validating `X-API-Key` header |
| `ApiKeyAuthOptions` | `FSTService/Api/ApiKeyAuth.cs` | Options class extending `AuthenticationSchemeOptions` |
| `PathTraversalGuardMiddleware` | `FSTService/Api/PathTraversalGuardMiddleware.cs` | Security middleware blocking directory traversal patterns |
| `ExchangeCodeToken` | `FortniteFestival.Core/Auth/ExchangeCodeModels.cs` | Legacy shared model for exchange code token response (Core library) |
| `ExchangeCodeResponse` | `FortniteFestival.Core/Auth/ExchangeCodeModels.cs` | Legacy shared model for exchange code response (Core library) |

### DI Registration Summary (Program.cs)
```csharp
// Credential store — file-based, path from ScraperOptions.DeviceAuthPath
builder.Services.AddSingleton<ICredentialStore>(sp => new FileCredentialStore(path, log));

// Epic auth HTTP service (injected with typed HttpClient)
builder.Services.AddHttpClient<EpicAuthService>();
builder.Services.AddSingleton<EpicAuthService>();

// Token lifecycle manager
builder.Services.AddSingleton<TokenManager>();

// API key auth scheme
builder.Services.AddAuthentication("ApiKey")
    .AddScheme<ApiKeyAuthOptions, ApiKeyAuthHandler>("ApiKey", opts => { opts.ApiKey = ...; });
builder.Services.AddAuthorization();
```

---

## Security Notes

1. **API key is static** — single shared key, no per-user or per-client differentiation. Adequate for admin-only endpoints in a self-hosted context.
2. **No user-facing auth on public endpoints** — by design; the web app consumes public data freely.
3. **Epic tokens are server-side only** — never exposed to web clients (except via protected `/api/admin/epic-token` endpoint).
4. **Refresh token rolling** — each refresh invalidates the previous token, reducing replay risk.
5. **Path traversal guard** — blocks `..` and encoded variants in paths and query strings.
6. **Rate limiting** — 100 req/s per IP globally and per policy, disabled in test environment.
7. **CORS** — restricted to configured origins (default `localhost:3000`), not wildcard.
