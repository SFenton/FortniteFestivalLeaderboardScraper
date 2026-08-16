---
status: canonical
owner: service
last_verified: 2026-08-16
last_verified_commit: bf770d49
sources:
  - FSTService/Program.cs
  - FSTService/HostedWorkerMode.cs
  - FSTService/Api/ApiEndpoints.cs
  - FSTService/Api/*Endpoints.cs
  - FSTService/Api/HealthEndpoints.cs
  - FSTService/Api/PublicationRouteSurfaceContract.cs
  - FSTService/Scraping/PhaseProgressCatalog.cs
  - FSTService/Api/PublicReadGateService.cs
  - FSTService/Api/PublicReadGateMiddleware.cs
  - FSTService/Api/PublicationReadContext.cs
  - FSTService/Api/PublicApiResponseCacheMiddleware.cs
  - FSTService/Api/SongEndpoints.cs
  - FSTService/Scraping/PathArtifactResolver.cs
  - FSTService/Api/SelectedProfileActivityMiddleware.cs
  - FSTService/Api/PublicationChangeMonitorService.cs
  - FSTService/Api/AdminEndpoints.cs
  - FSTService/Scraping/RegistrationMutationCoordinator.cs
  - FSTService.Tests/Integration/ApiPublicationClassificationTests.cs
update_triggers:
  - Hosting modes, middleware, endpoints, auth, rate limits, cache behavior, or publication contracts change.
---

# Service and API

FSTService is an ASP.NET Core .NET 9 application. The same binary can host the
public API, the full worker, a registration-sync worker, read-only rollout
serving, one-shot tools, or an embedded SPA.

## API role

Production `fstservice` normally runs with scraper mutation disabled. It still
performs startup loading, serves HTTP/WebSocket traffic, monitors publication
changes, and refreshes the song catalog/path state assigned to the frontend
role.

The application can serve static `wwwroot` assets when an embedded web bundle
exists. The normal split deployment uses the standalone Nginx web container.

## Middleware order

After CORS, WebSockets, and forwarded headers, the service applies:

1. rate limiting;
2. API-key authentication and authorization;
3. public API response caching;
4. publication read context;
5. publication read leases;
6. the public-read gate;
7. selected-profile activity tracking.

This order is part of the read-safety contract.

The digest-owned max-score freeze has one narrow short circuit within that
order. After the outer public-response cache gets the first chance to serve,
max-score-dependent song/path/exact-solo requests defer publication
read-context and boundary-lease acquisition to the public-read/endpoint gate.
This permits a stable cache or immutable path hit and makes every cold result
an explicit `503` with `Retry-After`, even while maintenance holds the
publication advisory lock. Other freeze reasons and ordinary publication
commit/read-lease behavior keep the listed order unchanged.

## Endpoint organization

`ApiEndpoints.cs` is the authoritative group aggregator, not the route
inventory. Domain routes live in `FSTService/Api/*Endpoints.cs`.

The mapped groups are health, feature flags, account, songs/shop/paths,
leaderboards, players, exports, band sync, rivals, leaderboard rivals,
rankings/bands, improvement notifications, admin, diagnostics, and WebSocket.

Path PNG and JSON routes are publication-bound and resolve one current
immutable artifact generation. The protected single-song regeneration route
uses the same atomic promotion path; it is not a full-catalogue bulk endpoint.
See [Path generation](path-generation.md).

The path route validates the eight generated solo instruments, including the
two plastic-drums scoring modes backed by Epic's shared `pd` chart.

The current source contains 80 HTTP mappings across 14 route-bearing endpoint
files, plus `/api/ws`. Integration tests classify each intentional route as:

- `PublicationBound`
- `OperationalLive`
- `AdminPrivate`

See [API contract](../reference/api-contract.md).

## Authentication and rate limiting

Protected endpoints use the `X-API-Key` authentication scheme. Public,
authenticated, protected, and global fixed-window limiters currently share the
same 100-request, one-second, per-client policy outside the test environment.
Do not copy older minute-based limits from deleted historical guidance.

## Caching and publication

Response caches expose ETag/Cache-Control behavior and are coordinated with the
scrape lifecycle. Publication-bound routes declare the generation surfaces they
require. Read pinning is permitted only when configuration is enabled and all
required surfaces are ready; stale or unavailable generations fail explicitly
instead of silently reading candidate state.

A digest-owned max-score maintenance freeze requires published cache hits or
`503` for affected publication-bound reads. `/api/songs` and both `/api/paths`
forms are included even though they are normally live endpoint code. A warm
`SongsCacheService` response may serve the prior publication; an existing
immutable current-generation path PNG/JSON may also be served. Missing path
artifacts, syntactically valid stale generation IDs retained by a warm
pre-promotion songs cache, and cold exact solo leaderboard reads, including
leeway requests, return `503`/`Retry-After: 30`. Path endpoints never serve
the requested old immutable generation. Outer-cache exact leaderboard hits
remain available.

While the exclusive maintenance gate or its freeze is active, the public-read
gate rejects player tracking, manual `POST /api/backfill/{accountId}`, and the
registration-changing band sync-status route. The manual endpoint holds the
shared advisory gate through all-time backfill and optional history
reconstruction; player tracking, band sync, and selected-profile activity use
the same gate around their registration writes. HTTP admission uses a
pool-capacity-bounded, nonblocking shared try-lock on an isolated unpooled
session, so requests return consistent `503` with `Retry-After: 30` rather
than consuming or queueing behind the normal PostgreSQL pool. Background
workers retain cancellable waiting admission on isolated sessions.

Each holder verifies its live backend/session token before guarded writes, and
database triggers fence registration plus leaderboard/score-history writes
against the durable exclusive owner. Selected-profile activity
tracking performs no player touch or band/member registration, including when
the outer response cache handles the request. When the same publication is
released, every API process invalidates response caches, the path-maxima cache,
and `SongsCacheService`, then broadcasts a forced publication refresh so
browsers do not retain the pre-maintenance maxima. Before any later
registration lookup, gate acquisition also refreshes only path/instrument
support synchronously; it does not broadly invalidate API caches.

## Operational progress

`GET /api/service-info` remains operational-live and exposes additive contract
version 2 phase-plan, normalized attempt, units, progress, ETA-confidence, and
separate heartbeat/last-progress fields. It preserves the version-1 labels and
summary fields for rolling worker and browser compatibility. The normalized
PostgreSQL ledger is authoritative when a running attempt exists; the worker
operation JSON remains the fallback summary.

The phase plan also identifies subphase catalog version
`fst.subphase-plan.v1`. `currentUpdate.subphaseProgress` is optional and
separate from phase progress; it carries schema version 1, ID, epoch, sequence,
exact/indeterminate/not-applicable kind, and exact counters only when the
denominator is final. Parallel attempts resolve deterministically to the
lowest phase ordinal. Worker heartbeat/activity fallback writes are
instance-fenced so restart overlap cannot restore an older operation.

Each phase-plan descriptor includes additive `reserved`. The accepted ordered
v2 list/version remains unchanged; `true` identifies retired IDs retained only
for historical and Tier-0 lineage. Consumers exclude those descriptors from
active phase counts.

The production API role runs with `--api-only`, which deliberately skips global
schema initialization. A first deployment to a database without
`scrape_phase_attempts` must run the existing `--initialize-schema-only`
command before starting the v2 API. Existing deployments remain idempotent.

Live acceptance observed contract v2 during network collection and verified
the normalized ledger throughout every later phase. An unrelated service
deployment caused one explicit 502 and replaced the candidate API for the rest
of scrape `1296`; it is excluded from service-latency attribution, not hidden
as candidate behavior.
