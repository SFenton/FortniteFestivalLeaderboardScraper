---
status: canonical
owner: service
last_verified: 2026-08-12
last_verified_commit: 41c3bdb4
sources:
  - FSTService/Api/ApiEndpoints.cs
  - FSTService/Api/*Endpoints.cs
  - FSTService/Api/PublicationRouteSurfaceContract.cs
  - FSTService.Tests/Integration/ApiPublicationClassificationTests.cs
  - packages/core/src/api/serverTypes.ts
  - FortniteFestivalWeb/src/api/client.ts
  - FSTService/Persistence/InstrumentDatabase.cs
  - FSTService/Scraping/RankingsCalculator.cs
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
- Max Score % first excludes scores above the configured 105% CHOpt validity
  cutoff, averages eligible per-song ratios capped at 105%, and applies the
  score-count credibility adjustment. `rawMaxScorePercent` retains the
  pre-adjustment average. A valid score on a chart without a computed maximum
  is omitted from that raw average but remains in the score count used by the
  credibility adjustment.

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
- Operational-live endpoints expose current process/coordination state.
- Admin/private endpoints must not be reclassified as public data accidentally.

## Change checklist

For any API change:

1. update the domain endpoint file and `ApiEndpoints.cs` when a group changes;
2. update publication classification and required surfaces;
3. update integration expectations;
4. update shared TypeScript types;
5. update the web client and its tests;
6. update this document and any affected component/configuration page.
