---
status: canonical
owner: service
last_verified: 2026-08-14
last_verified_commit: 86379374
sources:
  - FSTService/FeatureOptions.cs
  - FSTService/Scraping/PostScrapeOrchestrator.cs
  - FSTService/Api/FeatureEndpoints.cs
  - deploy/config/fstservice-role.env
  - deploy/config/fstworker-role.env
  - packages/core/src/api/serverTypes.ts
  - FortniteFestivalWeb/src/contexts/FeatureFlagsContext.tsx
update_triggers:
  - A FeatureOptions property, role override, public feature payload, shared type, or web feature consumer changes.
---

# Feature flags

`FeatureOptions` currently defines 13 flags. Only `AppManual` crosses the
public API boundary; the other 12 control service/worker persistence and
publication behavior.

| Flag | Code default | Audience | Purpose |
|---|---:|---|---|
| `AppManual` | `false` | Public web | Show the App Manual route/navigation |
| `WriteLegacyLiveLeaderboardDuringScrape` | `true` | Worker | Continue legacy mutable scrape writes |
| `WriteLegacyLiveLeaderboardSupplementalRows` | `true` | Worker | Continue legacy supplemental/backfill writes |
| `UseSnapshotOverlayWorkerReaders` | `false` | Worker | Read current state through snapshots plus overlays |
| `UseLeaderboardScopeFingerprints` | `true` | Worker | Record observe-only scope content/coverage fingerprints |
| `SkipUnchangedPhysicalLeaderboardSnapshots` | `false` | Worker | Reuse a validated published physical scope |
| `WritePublishedScopeSources` | `false` | Worker | Build/promote per-scope published source mappings |
| `EnforceScopeCompletenessManifests` | `false` | Worker | Require complete expected page manifests |
| `RequireSuccessfulScrapeWriters` | `false` | Worker | Reject candidate after writer failure |
| `EnforcePublicationCriticalPhases` | `false` | Worker | Reject candidate after critical post-phase failure |
| `EnablePublicationReadContext` | `false` | Service/worker | Enable generation pinning only when all surfaces are ready |
| `UsePublishedScopeSources` | `false` | Service | Resolve public reads/exports through published source maps |
| `UseStoredSoloProjectionRanksForFilteredReads` | `false` | Service | Preserve stored published projection order for filtered reads |

`WriteLegacyLiveLeaderboardDuringScrape` is the one tracked
`appsettings.json` override that differs from its code initializer:
`appsettings.json` sets it to `false`. The service role does not override that
value, and the worker role explicitly sets it to `false`, so both shipped roles
currently disable primary legacy mutable scrape writes.

## Effective role overrides

Role files override code/appsettings defaults:

| Service role override | Value |
|---|---:|
| `WritePublishedScopeSources` | `false` |
| `UsePublishedScopeSources` | `true` |
| `UseStoredSoloProjectionRanksForFilteredReads` | `false` |
| `SkipUnchangedPhysicalLeaderboardSnapshots` | `false` |
| `EnablePublicationReadContext` | `false` |

| Worker role override | Value |
|---|---:|
| `EnforceScopeCompletenessManifests` | `true` |
| `RequireSuccessfulScrapeWriters` | `true` |
| `EnforcePublicationCriticalPhases` | `true` |
| `WritePublishedScopeSources` | `true` |
| `UsePublishedScopeSources` | `false` |
| `SkipUnchangedPhysicalLeaderboardSnapshots` | `false` |
| `EnablePublicationReadContext` | `false` |
| `WriteLegacyLiveLeaderboardDuringScrape` | `false` |

The worker therefore does not maintain legacy mutable leaderboard rows during
the primary scrape. Supplemental legacy writes remain a separate flag and
must not be inferred from the primary-write setting. While the primary flag is
false, `RankRecompute` is retained as a rollback contract and completes without
scheduling the legacy update. A publication-critical phase cannot record
`skipped`. Turning the flag back on restores the existing recompute path; it
does not weaken the publication-critical phase policy.

## Public feature contract

`GET /api/features` returns:

```json
{
  "appManual": false
}
```

Any public feature change must keep these aligned:

- `FSTService/FeatureOptions.cs`
- `FSTService/Api/FeatureEndpoints.cs`
- `packages/core/src/api/serverTypes.ts`
- `FortniteFestivalWeb/src/contexts/FeatureFlagsContext.tsx`

Backend-only flags must be documented here and assigned to the correct role;
they do not need to be exposed to the browser.
