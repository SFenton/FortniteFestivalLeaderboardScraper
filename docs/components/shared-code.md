---
status: canonical
owner: repository
last_verified: 2026-08-14
last_verified_commit: 86379374
sources:
  - FortniteFestival.Core/FortniteFestival.Core.csproj
  - FortniteFestival.Core/Config/InstrumentType.cs
  - packages/core/package.json
  - packages/core/src/index.ts
  - packages/core/src/api/serverTypes.ts
  - packages/core/src/__tests__/serverTypes.test.ts
  - packages/core/src/suggestions/suggestionGenerator.ts
  - packages/theme/package.json
  - packages/theme/src/index.ts
  - packages/theme/src/colorHelpers.ts
  - packages/ui-utils/package.json
  - packages/ui-utils/src/index.ts
  - FSTService/Scraping/Replay/TierOneReplayModels.cs
update_triggers:
  - Shared project targets, package exports, instrument/song types, API types, theme tokens, or utilities change.
---

# Shared code

## .NET core

`FortniteFestival.Core` targets .NET Framework 4.7.2 and .NET 9. The .NET 9
target includes scraping code; the older target excludes it. The project also
retains legacy SQLite/file compatibility dependencies, but FSTService
production persistence is PostgreSQL.

Shared .NET responsibilities include domain models, Epic/catalog integration,
instrument definitions, song/path logic, and compatibility code used outside
the service host.

Tier-0/Tier-1 replay manifests and phase adapters intentionally remain
service-local under `FSTService.Scraping.Replay`. They are same-image evidence
and isolated-execution contracts, not shared public API types or a plugin SDK.
Move them into a shared assembly only after a second independently versioned
consumer exists and compatibility/version-skew requirements are measured.

## TypeScript packages

| Package | Responsibility |
|---|---|
| `@festival/core` | Instruments, combos, enums, models, API response types, configuration helpers, suggestion logic, app formatters |
| `@festival/theme` | Colors, spacing/layout, breakpoints, animation constants, typed CSS values and style factories |
| `@festival/ui-utils` | Small platform and stagger utilities shared across UI code |

The web app consumes these packages directly from source through Yarn portal
dependencies. Package exports are the public boundary. `@festival/theme` and
`@festival/ui-utils` expose only their root barrels and package metadata, and
declare their audited modules side-effect-free. Web TypeScript and Vite
resolution use those portal/package contracts rather than source aliases;
static tests reject deep imports and direct package-source traversal.

`@festival/theme` is also the source for the generated global CSS custom
properties. The explicit generator mapping preserves stable CSS names while
making token drift a build failure. Accuracy interpolation and its shared
gradient derive from the same low/high endpoints for charts and demos.

`@festival/core` owns the stateful suggestion generator. Rival data may arrive
after a mix has initialized or may refresh later; `setRivalData` queues the
generic and per-rival pipelines for each new data revision ahead of remaining
work while preserving emitted-category, song-history, and mix identity state.
Equivalent data references are ignored, empty rival samples are valid, and
explicit endless resets rebuild the full pipeline set from current data.

## Cross-language contract

API types in `packages/core/src/api/serverTypes.ts` are manually mirrored from
the service contract; they are not generated from OpenAPI. The HTTP client
lives in `FortniteFestivalWeb/src/api/client.ts`.

`ServiceInfoResponse` includes the accepted durable-progress v2 phase plan,
attempt, units, exact-phase-percent, conservative overall/ETA, heartbeat, and
last-progress contract. Fields stay optional so an older service response
remains consumable during rolling deployment. Phase descriptors include the
optional mirrored `reserved` boolean; consumers treat only `reserved === true`
as retired so older payloads remain active-compatible.

The mirrored response also includes optional
`ServiceInfoSubphaseProgress` and `phasePlan.subphaseCatalogVersion`.
Subphase schema version 1 distinguishes exact, indeterminate, and
not-applicable progress and carries reset epoch plus monotonic sequence fields.
All additive members remain optional for mixed-version service/web rollout.

The mirrored contract includes path JSON notes, activations, legacy start-note
metadata, and schema-v2 activation fields consumed by the path modal.

When a service DTO, route payload, feature response, or publication response
changes, review all three surfaces rather than type-asserting around a mismatch.

Shared instrument/song types also require review across
`FortniteFestival.Core/Config/InstrumentType.cs` and `packages/core/src/`.
