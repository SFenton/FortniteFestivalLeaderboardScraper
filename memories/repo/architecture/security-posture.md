# Security Posture — Fortnite Festival Score Tracker

> Last audited: 2026-04-03 by Security Agent

---

## Authentication

### API Key Auth (`X-API-Key` header)

- **Implementation**: [FSTService/Api/ApiKeyAuth.cs](FSTService/Api/ApiKeyAuth.cs) — custom `AuthenticationHandler<ApiKeyAuthOptions>`
- **Header**: `X-API-Key` — compared with `StringComparison.Ordinal` (constant-time not guaranteed but acceptable for API keys)
- **Config**: `Api:ApiKey` from appsettings.json, overridden via `Api__ApiKey` env var in production
- **Behavior when unconfigured**: Rejects ALL protected requests with a warning log (fail-closed)
- **Failed auth logging**: Logs method, path, and remote IP at WARNING level — no key values logged

### Epic OAuth (Device Code Flow)

- **Implementation**: [FSTService/Auth/EpicAuthService.cs](FSTService/Auth/EpicAuthService.cs)
- **Flow**: Device Code → user approval in browser → access + refresh tokens
- **Client credentials**: Hardcoded defaults for public Fortnite Switch client (`98f7e42c...`/`0a2449a2...`) — low risk, these are public game client creds
- **Override**: Via `EPIC_CLIENT_ID`/`EPIC_CLIENT_SECRET` env vars
- **Token refresh**: Automatic via `TokenManager` with 5-minute expiry buffer
- **Thread safety**: `SemaphoreSlim` lock prevents concurrent refresh races

### Token/Credential Storage

- **Implementation**: [FSTService/Auth/FileDeviceAuthStore.cs](FSTService/Auth/FileDeviceAuthStore.cs)
- **Storage**: Plaintext JSON at `data/device-auth.json` (configurable via `ScraperOptions.DeviceAuthPath`)
- **Risk**: ⚠️ No encryption at rest, no file permission restrictions set in code
- **Mitigation**: File lives inside Docker volume (`/app/data`), container runs as non-root `ubuntu` user
- **Secrets never logged**: Verified — no `Log*` calls include AccessToken, Password, or Secret values

---

## Authorization

### Endpoint Protection Model

| Endpoint Group | Auth Required | Rate Limit Policy | Notes |
|---|---|---|---|
| `/healthz`, `/readyz` | No | Global | Health probes |
| `/api/features` | No | `public` | Feature flags (read-only) |
| `/api/account/*` | No | `public` | Account lookup |
| `/api/songs/*` | No | `public` | Song metadata |
| `/api/leaderboard/*` | No | `public` | Leaderboard data |
| `/api/player/*` | No | `public` | Player stats |
| `/api/rivals/*` (reads) | No | `public` | Rivals data |
| `/api/rivals/*/request`, `/api/rivals/*/accept` | **Yes** (`RequireAuthorization`) | `auth` | Rival mutations |
| `/api/admin/*` | **Yes** (`RequireAuthorization`) | `protected` | Admin operations |
| `/api/register` (POST/DELETE) | **Yes** (`RequireAuthorization`) | `protected` | User registration |
| `/api/status` | **Yes** (`RequireAuthorization`) | `protected` | Service status |

- **No role/permission model**: Single API key grants full access to all protected endpoints. No per-user RBAC.
- **Frontend**: No auth tokens stored in `localStorage` — frontend is read-only SPA consuming public endpoints.

---

## Input Validation

### Pattern

Manual validation using `string.IsNullOrWhiteSpace()` + `.Trim()` with `Results.BadRequest()` responses.

### Coverage by Endpoint

| Endpoint | Validation | Gaps |
|---|---|---|
| `GET /api/account/check?username=` | ✅ Null/whitespace check | No length limit |
| `POST /api/register` | ✅ Null/whitespace for `DeviceId` + `Username` | No format/length validation |
| `DELETE /api/register` | ✅ Null/whitespace for `deviceId` + `accountId` | No format validation |
| `GET /api/player/{accountId}` | ❌ No explicit validation | Relies on ASP.NET route constraints |
| `GET /api/leaderboard/{songId}/{instrument}` | ❌ No explicit validation | Relies on parameterized SQL for safety |
| `GET /api/songs` | ❌ No validation on query params | `leeway` (double), `instruments` (string) unchecked |
| `GET /api/rankings` | ❌ No validation on `rankBy` | Whitelist switch in persistence layer (safe) |

### Assessment

- ⚠️ **No length restrictions** on string inputs (username, deviceId, accountId)
- ⚠️ **No format validation** (e.g., accountId should be hex UUID format)
- ⚠️ **No range checks** on numeric parameters (leeway, top, offset)
- ✅ **No FluentValidation or Data Annotations** — all manual, but functional
- ✅ **SQL safety not dependent on input validation** — parameterized queries provide defense-in-depth

---

## SQL Injection

### Audit Summary

| File | Queries | Status | Issues |
|---|---|---|---|
| `DatabaseInitializer.cs` | 1 (DDL) | ✅ Safe | Static schema, no user input |
| `FestivalPersistence.cs` | 2 | ✅ Safe | Fully parameterized |
| `MetaDatabase.cs` | ~47 | ⚠️ Mostly safe | 2 table-name interpolations (hardcoded arrays, not user input) |
| `InstrumentDatabase.cs` | ~50 | 🔴 **Violations** | See below |
| `GlobalLeaderboardPersistence.cs` | 0 | ✅ N/A | Coordinator only |

### Violations Found

#### 🔴 CRITICAL — `maxScore` interpolation (InstrumentDatabase.cs)

**Lines 423, 503**: Direct interpolation of `maxScore.Value` (nullable int) into SQL:
```csharp
var scoreFilter = maxScore.HasValue ? $"AND score <= {maxScore.Value}" : "";
cmd.CommandText = $"SELECT ... {scoreFilter}";
```

**Risk**: While `maxScore` is typed as `int?` (limiting attack surface since C# enforces type), this violates parameterized query discipline. If the calling chain ever changes to accept string input that's parsed, this becomes exploitable.

**Fix**: Use conditional parameterized query branches.

#### ⚠️ MEDIUM — LIMIT/OFFSET interpolation (InstrumentDatabase.cs)

**Lines 409, 424**: Direct interpolation of `top.Value` and `offset` into SQL:
```csharp
var limit = top.HasValue ? $"LIMIT {top.Value} OFFSET {offset}" : "";
```

**Risk**: Same type-safety argument as above. Values are `int`, but violates defense-in-depth.

**Fix**: Use `@limit` and `@offset` parameters.

#### ℹ️ LOW — Table name interpolation (MetaDatabase.cs)

**Lines 470, 473**: Table names from hardcoded `string[]` arrays interpolated into SQL (Npgsql limitation — table names cannot be parameterized).

### Safe Patterns Used

- ✅ **Parameterized IN clauses** with generated `@id0, @id1, ...` parameter names
- ✅ **Binary COPY import** via `BeginBinaryImport()` (injection-proof)
- ✅ **Whitelist-based column selection** via switch expressions (e.g., `RankByColumn()`)
- ✅ **Whitelist-based partition names** via switch on instrument enum

---

## XSS Prevention

### React Auto-Escaping

- ✅ React 19 auto-escapes all JSX expressions by default
- ✅ No `eval()`, no inline `<script>` tags, no template literal injection into DOM

### `dangerouslySetInnerHTML` Usage

| File | Lines | Context | Risk |
|---|---|---|---|
| [FortniteFestivalWeb/src/components/common/Math.tsx](FortniteFestivalWeb/src/components/common/Math.tsx) | 24, 26 | KaTeX `renderToString()` output | ✅ Safe — KaTeX sanitizes output, no user HTML input |

**Verdict**: Acceptable. Only 2 occurrences, both using a trusted math rendering library with no user-controlled HTML.

### Content Security Policy

- 🔴 **No CSP header configured** in nginx.conf, index.html, or vite.config.ts
- **Recommendation**: Add CSP to nginx.conf:
  ```nginx
  add_header Content-Security-Policy "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; connect-src 'self'; frame-ancestors 'none';" always;
  ```

### Security Headers (nginx.conf)

| Header | Present | Recommendation |
|---|---|---|
| `Content-Security-Policy` | ❌ Missing | Add (see above) |
| `X-Frame-Options` | ❌ Missing | `SAMEORIGIN` |
| `X-Content-Type-Options` | ❌ Missing | `nosniff` |
| `Strict-Transport-Security` | ❌ Missing | `max-age=31536000; includeSubDomains` |
| `Referrer-Policy` | ❌ Missing | `strict-origin-when-cross-origin` |
| `X-XSS-Protection` | ❌ Missing | `1; mode=block` (deprecated but harmless) |

---

## Rate Limiting

### Configuration (Program.cs lines 283–320)

- **Algorithm**: Fixed window
- **Limit**: 100 requests/second per client IP
- **Window**: 1 second
- **Queue**: 0 (immediate rejection)
- **Response**: HTTP 429 with `Retry-After` header
- **IP extraction**: `context.Connection.RemoteIpAddress` (respects ForwardedHeaders middleware)

### Policies

| Policy | Applied To | Limit |
|---|---|---|
| `public` | Song, leaderboard, player endpoints | 100 req/s/IP |
| `auth` | Authentication endpoints | 100 req/s/IP |
| `protected` | Admin endpoints | 100 req/s/IP |
| Global limiter | All requests | 100 req/s/IP |

### Assessment

- ⚠️ **All three policies have identical limits** — no tiered protection for sensitive endpoints
- ⚠️ **100 req/s is generous** — consider lower limits for `auth` and `protected` policies (e.g., 10 req/s)
- ✅ **Test mode**: Rate limiting disabled when `IsEnvironment("Testing")` — correct for integration tests
- ✅ **RetryAfter header**: Properly set on 429 responses

---

## Path Traversal

### Implementation (PathTraversalGuardMiddleware.cs)

- **Position**: First middleware in pipeline (before CORS, auth, rate limiting)
- **Checks both**: Request path AND query string
- **Patterns blocked**: `..`, `%2e%2e`, `%2E%2E`, `%2e.`, `%2E.`, `.%2e`, `.%2E`
- **Matching**: Case-insensitive via `StringComparison.OrdinalIgnoreCase`
- **Response**: HTTP 400 with `"Bad request."` body
- **Logging**: WARNING level with path and query string

### Assessment

- ✅ **Covers URL-encoded variants** — prevents double-encoding bypass
- ✅ **Early in pipeline** — blocks before any processing
- ✅ **Logs attempts** — supports incident detection
- ⚠️ **Does not check request body** — acceptable for this API (no file paths in POST bodies)

---

## CORS

### Configuration (Program.cs lines 324–332)

```csharp
policy.WithOrigins(apiSettings.AllowedOrigins)
      .AllowAnyHeader()
      .AllowAnyMethod();
```

- **Origins**: Configured via `Api:AllowedOrigins` in appsettings.json
- **Default**: `["http://localhost:3000"]` (dev only)
- **Production**: Must be overridden via environment variable or appsettings override
- **Headers**: `AllowAnyHeader()` — permissive but acceptable for API consumption
- **Methods**: `AllowAnyMethod()` — permissive; consider restricting to `GET, POST, DELETE, OPTIONS`

### Assessment

- ✅ **Not using `AllowAnyOrigin()`** — origins are explicitly configured
- ✅ **Not using `AllowCredentials()` with `AllowAnyOrigin()`** — avoids the dangerous combo
- ⚠️ **`AllowAnyMethod()`** is more permissive than necessary

---

## Secrets Management

### Source Control

| Secret | In Git? | Production Override |
|---|---|---|
| API Key (`dfc3d5b232a24c458db366650a1961de`) | ⚠️ **Yes** (appsettings.json) | `Api__ApiKey` env var |
| DB Password (`fst_dev`) | ⚠️ **Yes** (appsettings.json connection string) | `ConnectionStrings__PostgreSQL` env var |
| DB Username (`fst`) | ⚠️ **Yes** (appsettings.json) | Overridden in connection string |
| Epic Client ID/Secret | ⚠️ **Yes** (hardcoded defaults) | `EPIC_CLIENT_ID`/`EPIC_CLIENT_SECRET` env vars |
| MIDI Encryption Key | ✅ Not in git | `Scraper__MidiEncryptionKey` env var only |

### Assessment

- 🔴 **appsettings.json contains development secrets committed to git** — API key and DB password are visible in repo
- ✅ **Production overrides via environment variables** — deploy/docker-compose.yml uses `${PG_PASSWORD:?...}` and `${API_KEY:?...}` (fail if unset)
- ✅ **`.env` files gitignored** — `.env`, `.env.local`, `.env*.local` all in .gitignore
- ✅ **Device auth credentials** in Docker volume, not in image
- ✅ **No secrets logged** — verified no `Log*` calls include token/password/secret values

### Recommendations

- Move development secrets to .NET User Secrets (`dotnet user-secrets`) so they're not in git
- Or use placeholder values in appsettings.json with comments indicating env var override required

---

## Docker Security

### FSTService Dockerfile

| Check | Status | Details |
|---|---|---|
| Multi-stage build | ✅ | SDK build stage → aspnet runtime |
| Non-root user | ✅ | `USER ubuntu` (UID 1000) |
| Data directory permissions | ✅ | `chown -R ubuntu:ubuntu /app/data` |
| No secrets in image | ✅ | All secrets via env vars at runtime |
| Volume for persistent data | ✅ | `/app/data` as Docker volume |
| Minimal runtime packages | ✅ | Only CHOpt dependencies + curl for healthcheck |

### FortniteFestivalWeb Dockerfile

| Check | Status | Details |
|---|---|---|
| Multi-stage build | ✅ | node:20-slim build → nginx:stable-alpine runtime |
| Non-root user | ⚠️ | Uses default nginx user (acceptable for alpine) |
| No secrets in image | ✅ | `API_BACKEND_URL` injected at runtime via envsubst |
| Minimal attack surface | ✅ | Alpine-based nginx |

### Docker Compose (deploy/)

| Check | Status | Details |
|---|---|---|
| Required secrets enforced | ✅ | `${PG_PASSWORD:?Set PG_PASSWORD in .env}` — fails if missing |
| Ports bound to localhost | ✅ | `127.0.0.1:3000:80` and `127.0.0.1:8080:8080` |
| Healthchecks | ✅ | All three services have healthchecks |
| Resource limits | ✅ | PostgreSQL limited to 4GB memory |
| Network isolation | ✅ | Services on internal Docker network |
| PostgreSQL hardened | ✅ | Tuned `shared_buffers`, `work_mem`, etc. |

---

## OWASP Top 10 (2021) Checklist

| # | Category | Status | Notes |
|---|---|---|---|
| A01 | **Broken Access Control** | ⚠️ Partial | API key auth on admin endpoints. No per-user RBAC. No CSRF tokens. |
| A02 | **Cryptographic Failures** | ⚠️ Partial | Dev secrets in git (overridden in prod). Credential file unencrypted. |
| A03 | **Injection** | ⚠️ Partial | 98% parameterized. 3 SQL interpolation violations in InstrumentDatabase.cs (int types, low exploitability). |
| A04 | **Insecure Design** | ✅ Good | Clear separation of concerns, defense-in-depth middleware pipeline. |
| A05 | **Security Misconfiguration** | ⚠️ Partial | Missing security headers in nginx. No CSP. Rate limit tiers identical. |
| A06 | **Vulnerable Components** | ✅ Good | All dependencies current (React 19, .NET 9, nginx stable-alpine). |
| A07 | **Authentication Failures** | ✅ Good | API key auth fail-closed. Token refresh with SemaphoreSlim. No brute-force opportunity (100 req/s). |
| A08 | **Data Integrity Failures** | ✅ Good | Multi-stage Docker builds. No deserialization of untrusted objects. |
| A09 | **Logging & Monitoring** | ✅ Good | Path traversal attempts logged. Failed auth logged with IP. No secrets in logs. |
| A10 | **SSRF** | ✅ Good | No user-controlled URLs in server-side HTTP requests. Epic API URLs hardcoded. |

---

## Vulnerabilities Found

### 🔴 HIGH — Development Secrets Committed to Git

- **File**: [FSTService/appsettings.json](FSTService/appsettings.json)
- **Details**: API key `dfc3d5b232a24c458db366650a1961de` and DB password `fst_dev` in source control
- **Impact**: Anyone with repo access can see development credentials
- **Mitigation**: These are dev-only values; production uses env var overrides
- **Recommendation**: Move to .NET User Secrets or use placeholder values

### ⚠️ MEDIUM — SQL String Interpolation (3 instances)

- **File**: [FSTService/Persistence/InstrumentDatabase.cs](FSTService/Persistence/InstrumentDatabase.cs) lines 409, 423-424, 503
- **Details**: `maxScore.Value`, `top.Value`, `offset` interpolated into SQL strings
- **Impact**: Low exploitability (C# enforces `int` type), but violates defense-in-depth
- **Recommendation**: Parameterize all values, even typed integers

### ⚠️ MEDIUM — Missing Security Headers in nginx

- **File**: [FortniteFestivalWeb/nginx.conf](FortniteFestivalWeb/nginx.conf)
- **Details**: No `X-Frame-Options`, `X-Content-Type-Options`, `Strict-Transport-Security`, `Content-Security-Policy`
- **Impact**: Clickjacking, MIME-sniffing, no HSTS, no XSS defense-in-depth
- **Recommendation**: Add standard security headers (see XSS Prevention section)

### ⚠️ MEDIUM — Identical Rate Limit Tiers

- **Details**: `public`, `auth`, and `protected` policies all allow 100 req/s/IP
- **Impact**: No additional protection for sensitive admin/auth endpoints
- **Recommendation**: Lower `auth` to 10 req/s, `protected` to 20 req/s

### ℹ️ LOW — No CSRF Protection

- **Details**: No CSRF tokens on state-changing endpoints. Relies on API key auth + CORS.
- **Impact**: Low — admin endpoints require API key header (browsers won't send custom headers cross-origin without CORS preflight)
- **Recommendation**: Acceptable given current architecture (API key + CORS provides equivalent protection)

### ℹ️ LOW — Credential File Unencrypted

- **File**: `data/device-auth.json`
- **Details**: Refresh tokens stored as plaintext JSON
- **Impact**: Low — file in Docker volume, container runs as non-root
- **Recommendation**: Consider OS-level encryption or DPAPI on Windows

### ℹ️ LOW — Permissive Input Validation

- **Details**: No length limits, format validation, or range checks on most API inputs
- **Impact**: Low — parameterized SQL provides safety net; worst case is wasteful queries
- **Recommendation**: Add basic length limits (e.g., username ≤ 64 chars, accountId = 32 hex chars)
