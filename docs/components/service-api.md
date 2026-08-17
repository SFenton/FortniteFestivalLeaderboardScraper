---
status: canonical
owner: service
last_verified: 2026-08-17
last_verified_commit: dffca41c
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
  - FSTService/Api/PublicationApiResponseCachePolicy.cs
  - FSTService/Api/PublicationApiResponseCacheService.cs
  - FSTService/Api/PublicApiCacheTelemetry.cs
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

The freeze-safe public API cache has two tiers:

- L1 is a process-local accelerator keyed by publication, public-read safety
  revision, and normalized request identity.
- L2 is `publication_api_response_cache`, authoritative for covered frozen
  reads and retained for the current and previous publications.

Cache admission first requires exactly one authoritative `PublicationBound`
endpoint classification from the canonical route catalog. Unclassified,
operational, private, or conflicting metadata bypasses both cache tiers even
when its path would pass request-shape checks. The path/query deny-list remains
defense-in-depth, not the trust boundary. Startup route-catalog validation and
middleware tests make future classification drift fail closed.

L2 rows retain deterministic JSON bytes, ETag, and `cached_at`; the service
derives the full SHA-256 and fixed JSON content type on lookup. A service
restart recovers directly from L2. Same-publication maintenance swaps the
complete L2 generation before unfreeze, while catalog/path mutation explicitly
invalidates L1 and durably rewrites the canonical songs row. Catalog refresh
compares the exact provider snapshot hash, not only song count, so metadata
changes and removals invalidate the same-publication row as well as additions.

Freeze-critical coverage is intentionally bounded: `/api/songs`, page-1
per-instrument/composite/generic-band rankings, overview bootstrap sizes,
registered-player default profiles, top-10 song/instrument leaderboards, and
existing leaderboard-all/song-band bootstrap rows. Request aliases resolve
canonical precompute keys and project contained page windows without duplicating
large JSON rows. Selected account/team overlays, arbitrary pages/filters,
search/history/notification variants, paths, shop, operational, private, and
WebSocket routes keep their established owners.

During any required-cache freeze, covered routes perform L1/L2 reads only. A
hit returns `200`/`304`; a miss returns `503` with `Retry-After: 30` and never
builds or writes. Cache hits retain each covered endpoint family's
`Cache-Control`, content type, ETag, publication header, and exact response
bytes. Unfrozen overview sizes `25` and `50` are the only lazy
write-through variants. They use process single-flight, store only successful
JSON responses whose measured build is below one second, and reject slow,
oversized, failed, or transition-raced builds without poisoning L2.
Metric, instrument, band-type, query-order, and numeric spellings normalize to
one semantic lazy/canonical key; request spelling cannot expand the bounded
variant set.

Path PNG/JSON remains immutable-file owned. Missing artifacts, syntactically
valid stale generation IDs retained by a pre-promotion songs cache, and
uncovered cold routes remain fail-closed during max-score maintenance.

`X-FST-Public-Cache` reports `hit`, `miss`, or `build`;
`X-FST-Public-Cache-Tier` distinguishes L1 and L2 hits. Admin telemetry exposes
route patterns, hashed cache-key IDs, publication/revision, outcome, wait/build
duration, payload bytes, cached timestamp, and error type without raw account
or team/profile identifiers, raw cache keys, or exception messages.

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
