---
status: canonical
owner: service
last_verified: 2026-08-15
last_verified_commit: 02c28ccd
sources:
  - FSTService/Api/ApiEndpoints.cs
  - FSTService/Api/*Endpoints.cs
  - FSTService/Api/HealthEndpoints.cs
  - FSTService/Api/PublicationRouteSurfaceContract.cs
  - FSTService/Scraping/PhaseProgressCatalog.cs
  - FSTService/Scraping/PostScrapeOrchestrator.cs
  - FSTService/Api/PublicReadGateService.cs
  - FSTService/Api/PublicReadGateMiddleware.cs
  - FSTService/Api/SelectedProfileActivityMiddleware.cs
  - FSTService.Tests/Integration/ApiPublicationClassificationTests.cs
  - packages/core/src/api/serverTypes.ts
  - FortniteFestivalWeb/src/api/client.ts
  - FortniteFestivalWeb/src/hooks/data/useServiceInfo.ts
  - FortniteFestivalWeb/src/pages/settings/SettingsServiceProgress.tsx
  - FSTService/Persistence/InstrumentDatabase.cs
  - FSTService/Persistence/MaxScoreMaintenanceModels.cs
  - FSTService/Scraping/RankingsCalculator.cs
  - FortniteFestivalWeb/src/pages/leaderboards/helpers/rankingHelpers.ts
update_triggers:
  - A route, payload, auth rule, rate limit, publication classification, or client method changes.
---

# API contract

## Source set

The API contract is distributed across:

1. `FSTService/Api/ApiEndpoints.cs` - group registration and discoverability;
2. `FSTService/Api/*Endpoints.cs` - actual HTTP route definitions;
3. `PublicationRouteSurfaceContract.cs` - generation-surface requirements;
4. `ApiPublicationClassificationTests.cs` - the intentional route inventory and
   `PublicationBound`/`OperationalLive`/`AdminPrivate` classification;
5. `packages/core/src/api/serverTypes.ts` - shared TypeScript response types;
6. `FortniteFestivalWeb/src/api/client.ts` - browser requests and response use.

Review all applicable files. `ApiEndpoints.cs` alone contains no route
definitions, but it must remain aligned with the domain endpoint groups.

## Current surface

The service maps 80 HTTP routes across 14 route-bearing endpoint files plus
`/api/ws`.

| Group | Main responsibility |
|---|---|
| Health | liveness, readiness, version, progress, publication, service info |
| Features | public web feature payload |
| Account | name refresh and search |
| Songs | songs, shop, path metadata and path data |
| Leaderboard | solo/band song leaderboards and rank offsets |
| Player | profile, tracking, sync, stats, bands, history |
| Export | player and band exports |
| Band sync | band synchronization status |
| Rivals | rival lists, detail, diagnostics, recompute |
| Leaderboard rivals | per-instrument comparisons and recompute |
| Rankings | solo/family/combo/band rankings, history, neighborhoods, band lookup |
| Notifications | player and band improvement notifications |
| Admin | status, Epic token, refresh, path generation, backfill, DB/cache diagnostics |
| Diagnostics | in-flight work, notification diagnostics, client interaction telemetry |
| WebSocket | application publication/score change channel |

Use the integration test's route arrays when an exact pattern list is needed;
do not maintain a second hand-written 80-row table here.

### Path artifacts

`GET /api/paths/{songId}/{instrument}/{difficulty}` serves the current PNG.
Appending `/data` serves the matching structured JSON. Both routes are
publication-bound, accept an optional current `generationId`, and return an
explicit error for invalid instruments, difficulties, generation IDs, or
missing artifacts.

Path JSON schema v2 is represented by `PathDataResponse` in
`packages/core/src/api/serverTypes.ts`. Every activation has an authoritative
instruction and exact trigger score/Overdrive metadata. Legacy schema-v1 JSON
remains readable while catalogue regeneration is in progress.

Supported path instruments are Lead, Bass, Drums, Tap Vocals, Pro Lead,
Pro Bass, Pro Drums, and Pro Drums + Cymbals. `/api/songs` exposes distinct
max-score entries for both plastic-drums modes.

`POST /api/admin/regenerate-paths?songId=<id>&force=<bool>` is an
`AdminPrivate` single-song command. It requires `X-API-Key`, returns `202`, and
starts the normal atomic generation flow. Omitting `songId` is rejected; the
endpoint intentionally does not accept a full catalogue.

### Ranking metric semantics

Per-instrument player ranking payloads expose raw values and ranks with these
production meanings:

- adjusted percentile is the average rank percentile with Bayesian
  score-count credibility;
- popularity-weighted percentile uses log2 leaderboard-population weights and
  the same credibility adjustment;
- FC Rate is `fullComboCount / totalChartedSongs`; unplayed charts remain in
  the denominator and no Bayesian adjustment is applied;
- Total Score is the sum of eligible scores;
- Max Score % uses the CHOpt maximum as its ratio denominator, but validity is
  a separate integer threshold:
  `floor(CHOpt maximum × 21 / 20)`, the exact `1.05` contract. Scores above
  that threshold are excluded. Max-score maintenance admits maxima only
  through `2,045,222,521`, whose cutoff is `int.MaxValue`; the next value is
  rejected before publication because its cutoff cannot fit PostgreSQL
  `INTEGER`. Scores above the denominator but at or below the threshold remain
  valid; their ratios are capped at `1.05` before averaging. The score-count
  credibility adjustment then produces the public value, while
  `rawMaxScorePercent` retains the pre-adjustment average. A valid score on a
  chart without a computed maximum is omitted from that raw average but
  remains in the score count used by the credibility adjustment.

For one successfully rebuilt per-instrument ranking generation,
`totalChartedSongs` is uniform across every account row. The server derives it
from the exact current catalog and validated chart evidence, excludes retained
removed-song scores from current metrics without deleting their source rows,
and fails the ranking build instead of emitting mixed or out-of-range rows.
Browser leaderboard views render each persisted row denominator verbatim; they
do not clamp or infer a replacement total.

Aggregate player scopes intentionally use different formulas:

- combo adjusted and weighted ratings are song-count-weighted averages of the
  represented per-instrument ratings;
- combo FC Rate is Full Combos divided by songs played across the combo
  instruments, and combo Max Score % is the average represented
  per-instrument adjusted value;
- solo-family adjusted and weighted ratings use the full family chart catalog,
  with unplayed charts contributing the worst percentile before credibility;
- solo-family FC Rate uses the full family chart catalog, while Max Score %
  treats unplayed charts as 0% before its credibility adjustment.

## Common behavior

- Protected routes authenticate through `X-API-Key`.
- Public/auth/protected/global fixed-window policies currently use 100 requests
  per second per client outside tests.
- Publication-bound responses participate in read gates, generation context,
  cache behavior, and route-surface readiness.
- During a `max-score-maintenance:v1:<manifest-sha256>` freeze, a
  publication-bound route may serve only an existing published cache hit.
  Otherwise affected song/path/ranking/player/band surfaces return `503` with
  `Retry-After`; path and `/api/songs` are explicitly included even though
  they normally use live endpoint code. `/api/songs` may serve its existing
  stable process cache; exact solo leaderboard routes, especially leeway
  queries, use the outer published cache or return `503`.
- While the exclusive max-score mutation gate or its exact freeze is active,
  `POST /api/player/{accountId}/track`,
  `POST /api/backfill/{accountId}`, and
  `GET /api/bands/{bandType}/{teamKey}/sync-status` return `503` with
  `Retry-After: 30` before their registration or score-history side effects.
  HTTP admission is a pool-capacity-bounded, nonblocking shared advisory
  try-lock on an isolated unpooled session, so the production 100-request/s
  limit cannot fill the normal 15-connection database pool with maintenance
  waiters. The manual backfill endpoint holds the shared session across both
  all-time backfill and optional history reconstruction, including
  cancellation cleanup. Player tracking, band sync, and selected-profile
  activity use the same gate, so an exclusive maintenance holder prevents
  their writes even before freeze-state caches observe the transition.
  Selected-profile headers never touch player activity or register a selected
  band/member set until maintenance releases the gate/freeze.
- Freeze release invalidates path-maxima, song, and response caches and forces
  a WebSocket same-publication refresh.
- Operational-live endpoints expose current process/coordination state.
- Admin/private endpoints must not be reclassified as public data accidentally.

## Service-info durable progress contract

`GET /api/service-info` remains an `OperationalLive` endpoint and retains every
version-1 field. Contract version 2 adds:

- `phasePlan.version` and ordered descriptors (`id`, label, legacy phase,
  ordinal, default units kind, additive `reserved`);
- stable operation, phase, and subphase IDs plus attempt/ordinal/plan version;
- units kind/completed/total and `unitsTotalFinal`;
- exact `phasePercent` only with a final denominator;
- server-owned `overallPercentKind`, optional value/model version;
- optional ETA lower/upper seconds, confidence, and sample count;
- distinct `heartbeatAt` and `lastProgressAt`.

Plan `fst.scrape-plan.v2` remains a stable superset for evidence-package and
historical compatibility. `post.checkpoint` and
`post.deferred_registration_sync` are reserved descriptors and are not current
worker execution policies. They emit `reserved: true`; active descriptors emit
`false`, allowing counts to exclude retired IDs without changing the ordered
list or plan version. Only best-effort phases may use `phaseStatus=skipped`
with a warning reason. Critical skipped rows are invalid and fail closed
regardless of the critical-failure rollout switch.
Absence alone is not evidence that a best-effort phase starved.

Weak overall or ETA evidence is omitted, not serialized as false precision.
Initial overall progress is normally `indeterminate`. Existing `phase`,
`subOperation`, `progressPercent`, labels, branches, and worker-operation fields
remain available for version-1 browser fallback.

The Settings client consumes this additive payload through the existing shared
service-info React Query request. It uses stable IDs for translated labels,
renders exact phase percentage only when `unitsTotalFinal=true`, and shows
server-owned overall/ETA evidence only when present and trustworthy. It does
not derive an overall percentage from browser weights or promote legacy
`progressPercent` into an exact value.

Live web validation of commit `0af25b3f` accepted this browser consumption
contract while publication `1296` stayed idle and unfrozen. Across
320/375/768/1440 px, service-info polling remained one request in flight,
version-1/unknown totals remained indeterminate in automated coverage, and the
idle live payload rendered without fabricated progress or visible `N/A`
diagnostics. The measured evidence is under
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/pr27-settings-live-ab-20260814T062455Z`.

`service_worker_status.current_operation_json` carries the same additive v2
summary. PostgreSQL `scrape_phase_attempts` is authoritative for normalized
attempt/progress timestamps when present; service-info falls back to the
backward-compatible operation JSON for rolling upgrades.

Matched candidate scrape `1300` accepted the reserved-descriptor projection:
the v2 plan remained 28 ordered descriptors, exactly
`post.checkpoint` and `post.deferred_registration_sync` reported
`reserved: true`, the other 26 reported `false`, and neither retired ID
created an attempt or outcome. All 355 candidate monitor samples returned HTTP
200 for readiness, the web shell, and the representative API probe while the
candidate published and unfroze.

Matched scrape `1296` accepted this additive contract with complete
publication parity and `0.0696%` end-to-end wall-clock overhead. Null-valued
compatibility fields may be omitted by JSON serialization. The brief external
service reset/502 in that window is separately attributed and excluded from
latency claims.

## Change checklist

For any API change:

1. update the domain endpoint file and `ApiEndpoints.cs` when a group changes;
2. update publication classification and required surfaces;
3. update integration expectations;
4. update shared TypeScript types;
5. update the web client and its tests;
6. update this document and any affected component/configuration page.
