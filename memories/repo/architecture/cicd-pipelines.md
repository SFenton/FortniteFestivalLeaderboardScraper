# CI/CD Pipelines & Infrastructure

> Last updated: 2026-04-03

## GitHub Actions

### Workflow: `publish-image.yml` (single workflow, 4 jobs)

**Triggers:**
- `push` to `master` — filtered by path (FSTService/**, FortniteFestival.Core/**, FSTService.Tests/**, FortniteFestivalWeb/**, FortniteFestivalRN/**, packages/**)
- `pull_request` to `master` — same path filters
- `workflow_dispatch` — manual trigger

**Jobs:**

| Job | Runs On | Condition | Purpose |
|---|---|---|---|
| `version-bump` | ubuntu-latest | push to master only, skip if commit message contains `[version-bump]` | Auto-increment patch version for changed components |
| `test` | ubuntu-latest | always (unless failure/cancelled) | Build + test FSTService with coverage gate |
| `build-and-push-service` | ubuntu-latest | push/dispatch only, after version-bump | Docker build + push FSTService to GHCR |
| `build-and-push-web` | ubuntu-latest | push/dispatch only, after version-bump | Docker build + push FestivalWeb to GHCR |

**Version Bump Logic:**
- Detects changed components via `git diff --name-only HEAD~1 HEAD`
- Components tracked: FSTService, FortniteFestivalWeb, FortniteFestivalRN, @festival/core, @festival/theme
- Each component gets independent patch version bump (MAJOR.MINOR.PATCH → MAJOR.MINOR.PATCH+1)
- FSTService version lives in `FSTService/FSTService.csproj` `<Version>` element (currently 1.0.117)
- Web/RN/packages versions live in respective `package.json` files
- Syncs `APP_VERSION`, `CORE_VERSION`, `THEME_VERSION` constants in `packages/core/src/index.ts`
- Commits as `github-actions[bot]` with message `chore: auto version bump [version-bump]`
- The `[version-bump]` tag prevents infinite loops

**Test Job:**
- .NET 9.0 SDK
- Restores, builds Release, runs tests with XPlat Code Coverage (Cobertura format)
- Coverage filtered to `[FSTService]*` assembly only
- Enforces 94% minimum line coverage (parsed from Cobertura XML)
- Uploads TRX + coverage XML as artifacts

**Web Test Job:** Currently commented out (TODO: re-enable). Would run `yarn vitest run --coverage`.

**Docker Push:**
- Registry: `ghcr.io`
- Images: `ghcr.io/sfenton/fstservice`, `ghcr.io/sfenton/fstfestivalweb`
- Tags: `sha-<commit>` + `latest` (on default branch only)
- Uses `docker/metadata-action@v5` for tag generation
- Uses `docker/build-push-action@v6` for build+push
- Only runs on push to master or workflow_dispatch (NOT on PRs)

## Docker

### FSTService Dockerfile (multi-stage)

**Stage 1: Build** (`mcr.microsoft.com/dotnet/sdk:9.0`)
- Copies .csproj files first (layer caching for `dotnet restore`)
- Restores FSTService.csproj (pulls in FortniteFestival.Core transitively)
- Copies full source, publishes Release to `/app`

**Stage 2: Runtime** (`mcr.microsoft.com/dotnet/aspnet:9.0-noble`)
- Ubuntu 24.04 Noble (glibc 2.39+ for CHOpt Qt6 binary)
- Installs runtime deps: curl, libpng, Qt6 OpenGL libs, fontconfig, glib, dbus
- Copies CHOpt CLI binary from `tools/chopt-cli-linux/`
- Runs as non-root `ubuntu` user (UID 1000)
- Volume mount: `/app/data` (DB files, device-auth.json)
- Exposes port 8080
- Entrypoint: `dotnet FSTService.dll`

### FortniteFestivalWeb Dockerfile (multi-stage)

**Stage 1: Build** (`node:20-slim`)
- Copies shared packages (core, theme, ui-utils) + web package.json/yarn.lock
- Forces `nodeLinker: node-modules` (Docker override of local PnP)
- `yarn install` then full COPY then `yarn vite build --outDir /webapp-dist`

**Stage 2: Runtime** (`nginx:stable-alpine`)
- Copies built SPA to `/usr/share/nginx/html/`
- Copies `nginx.conf` as template to `/etc/nginx/templates/`
- Custom `docker-entrypoint.sh` runs `envsubst` for `${API_BACKEND_URL}` substitution
- Exposes port 80

### Nginx Configuration
- Gzip compression for text/JS/CSS/JSON/SVG
- Static assets (js/css/images/fonts): 1-year cache, `immutable`
- `/api/` → reverse proxy to `${API_BACKEND_URL}` (WebSocket-ready)
- `/healthz`, `/readyz` → proxied to backend
- SPA fallback: `try_files $uri $uri/ /index.html`

## Docker Compose

### Root `docker-compose.yml` (local dev / self-hosted)
- **postgres**: PostgreSQL 17 Alpine, volume `pg-data`, healthcheck via `pg_isready`
- **fstservice**: Builds from local Dockerfile, depends on healthy postgres, port `127.0.0.1:8080:8080`
- **festivalweb**: Builds from local Dockerfile, depends on healthy fstservice, port `127.0.0.1:3000:80`
- All ports bound to localhost only (no external exposure)
- Environment: `DOTNET_ENVIRONMENT=Production`, PostgreSQL connection string, API key (required), feature flags (all OFF by default)

### `deploy/docker-compose.yml` (production remote host)
- Same 3 services but `fstservice` and `festivalweb` use **pre-built images** from GHCR (`ghcr.io/sfenton/fstservice:latest`, `ghcr.io/sfenton/fstfestivalweb:latest`)
- **Postgres tuned**: shared_buffers=2GB, work_mem=64MB, effective_cache_size=4GB, max_wal_size=4GB, shm_size=512mb, 4GB memory limit
- Additional scraper config: `MaxPagesPerLeaderboard`, `SequentialScrape`, `PageConcurrency`, `SongConcurrency`, `MaxRequestsPerSecond`
- Epic OAuth client overrides: `EPIC_CLIENT_ID`, `EPIC_CLIENT_SECRET`
- Setup requires: `docker login ghcr.io` with PAT (read:packages scope)

## Test Infrastructure

### FSTService Tests (xUnit + .NET 9)
- Project: `FSTService.Tests/FSTService.Tests.csproj`
- Framework: xUnit 2.9.2, NSubstitute 5.3.0 for mocking
- Integration: `Microsoft.AspNetCore.Mvc.Testing` (WebApplicationFactory), `Testcontainers.PostgreSql` 4.3.0
- Coverage: `coverlet.collector` 6.0.2 (Cobertura format)
- `InternalsVisibleTo` from FSTService → FSTService.Tests

### FortniteFestivalWeb Tests (Vitest + Playwright)
- **Unit tests**: Vitest 4.0.18, jsdom environment, `@testing-library/react` 16.3.2
  - Setup file: `__test__/setup.ts`
  - Excludes: `e2e/**`
  - Coverage: `@vitest/coverage-v8` with per-file thresholds (95% lines/branches/statements/functions)
- **E2E tests**: Playwright 1.58.2
  - 4 viewport projects: desktop (1280×800), desktop-narrow (800×800), mobile (375×812), mobile-narrow (320×568)
  - Dev server auto-starts: `npx vite --mode e2e --port 5173`
  - Headless by default

## Coverage Gates

| Component | Tool | Threshold | Enforcement |
|---|---|---|---|
| FSTService | coverlet (Cobertura) | **94% line coverage** | GitHub Actions `test` job — bash script parses XML, fails build if below |
| FortniteFestivalWeb | @vitest/coverage-v8 | **95% per-file** (lines, branches, statements, functions) | Vitest config `thresholds` — fails locally but CI job currently disabled |

**Coverage collection command (CI):**
```bash
dotnet test FSTService.Tests/FSTService.Tests.csproj -c Release --no-build \
  --collect:"XPlat Code Coverage" \
  -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura \
  DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[FSTService]*"
```

## Build Process

### FSTService
1. `dotnet restore FSTService.Tests/FSTService.Tests.csproj` (restores FSTService + Core transitively)
2. `dotnet build -c Release --no-restore`
3. `dotnet test -c Release --no-build --collect:"XPlat Code Coverage"`
4. `dotnet publish FSTService/FSTService.csproj -c Release --no-restore -o /app` (Docker)

### FortniteFestivalWeb
1. `yarn install` (Yarn 4.12.0 with PnP locally, node-modules in Docker)
2. `tsc -b` (TypeScript check)
3. `yarn vite build` (outputs to `FSTService/wwwroot/` locally, `/webapp-dist` in Docker)
4. `npx vitest run` (unit tests)
5. `npx playwright test` (E2E)

### Local dev build output
- `vite build` outputs to `FSTService/wwwroot/` — the .NET service serves the SPA in development
- In production Docker, nginx serves the SPA separately

## Deployment Model

**Architecture:** 3-container stack (postgres + fstservice + festivalweb) behind localhost binding.

**Deployment flow:**
1. Push to `master` → GitHub Actions workflow triggers
2. `version-bump` job auto-increments affected component versions
3. `test` job runs .NET tests with 94% coverage gate
4. `build-and-push-service` + `build-and-push-web` build Docker images and push to GHCR
5. On remote host: `docker compose pull && docker compose up -d` (manual)

**No CD automation to remote host** — deployment is pull-based (operator runs `docker compose pull` on the target machine using `deploy/docker-compose.yml`).

**Health checks:**
- postgres: `pg_isready -U fst -d fstservice` (10s interval)
- fstservice: `curl -sf http://localhost:8080/readyz` (30s interval)
- festivalweb: `wget -qO- http://localhost/` (30s interval)

## Environment Configuration

| Setting | Development | Production (docker-compose) |
|---|---|---|
| `DOTNET_ENVIRONMENT` | Development (implicit) | Production |
| Feature flags | All ON (appsettings.Development.json) | All OFF by default (env var overrides) |
| Logging | Trace for FSTService | Info/Debug standard |
| API key | Hardcoded dev key in appsettings.json | Required via `API_KEY` env var |
| PostgreSQL | localhost:5432, password `fst_dev` | Container `postgres`, password from `.env` |
| Web dev server | Vite port 3000, proxy `/api` → `VITE_API_BASE` | Nginx port 80, proxy to `http://fstservice:8080` |

## Scripts & Tools

### `tools/scripts/` (PowerShell PostToolUse hooks)
| Script | Trigger | Purpose |
|---|---|---|
| `coverage-check-hook.ps1` | After `runTests` or `dotnet test` in terminal | Reminds agent to check 94% coverage threshold |
| `test-after-edit-hook.ps1` | After editing FSTService source files | Reminds agent to run tests after code changes |
| `agent-ownership-map.ps1` | Called by evolution hook | Maps source paths → owning agents (40+ mappings) |
| `agent-evolution-hook.ps1` | After editing source files | Detects cross-territory edits, routes to owning agent |

### Build tools
| Tool | Location | Purpose |
|---|---|---|
| CHOpt CLI (Linux) | `tools/chopt-cli-linux/` | Path generation for max scores (bundled in Docker image) |
| CHOpt CLI (Windows) | `tools/chopt-cli/` | Local Windows development |
| payload_analysis.py | `tools/payload_analysis.py` | API payload analysis utility |
| V2BatchTest | `tools/V2BatchTest/` | Epic API v2 batch testing |
| v1lookup | `tools/v1lookup/` | Epic API v1 lookup utility |
| PathValidation | `tools/PathValidation/` | CHOpt path validation |

### VS Code Tasks (`.vscode/tasks.json`)
- `restore` — `dotnet restore` solution
- `build core` — build FortniteFestival.Core (depends on restore)
- `build maui windows` — build MAUI app for Windows
- `run maui windows` — build+run MAUI app
- `clean` — `dotnet clean` solution
