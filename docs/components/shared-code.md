---
status: canonical
owner: repository
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - FortniteFestival.Core/FortniteFestival.Core.csproj
  - FortniteFestival.Core/Config/InstrumentType.cs
  - packages/core/package.json
  - packages/core/src/index.ts
  - packages/theme/src/index.ts
  - packages/ui-utils/src/index.ts
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

## TypeScript packages

| Package | Responsibility |
|---|---|
| `@festival/core` | Instruments, combos, enums, models, API response types, configuration helpers, suggestion logic, app formatters |
| `@festival/theme` | Colors, spacing/layout, breakpoints, animation constants, typed CSS values and style factories |
| `@festival/ui-utils` | Small platform and stagger utilities shared across UI code |

The web app consumes these packages directly from source through Yarn portal
dependencies. Package exports are the public boundary.

## Cross-language contract

API types in `packages/core/src/api/serverTypes.ts` are manually mirrored from
the service contract; they are not generated from OpenAPI. The HTTP client
lives in `FortniteFestivalWeb/src/api/client.ts`.

When a service DTO, route payload, feature response, or publication response
changes, review all three surfaces rather than type-asserting around a mismatch.

Shared instrument/song types also require review across
`FortniteFestival.Core/Config/InstrumentType.cs` and `packages/core/src/`.
