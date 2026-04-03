# Fortnite Festival Score Tracker — Project Guidelines

## Project Overview

**Fortnite Festival Score Tracker (FST)** tracks Fortnite Festival leaderboard scores across all seasons, instruments, and songs. The leaderboards reset every season, so this system continuously scrapes Epic's APIs to build a persistent historical record.

| Component | Path | Stack |
|---|---|---|
| **FSTService** | `FSTService/` | .NET 9.0 / C# — ASP.NET Core + BackgroundService |
| **FortniteFestivalWeb** | `FortniteFestivalWeb/` | React 19 + TypeScript + Vite |
| **FortniteFestival.Core** | `FortniteFestival.Core/` | .NET shared library (net472 + net9.0) |
| **Shared TS packages** | `packages/` | `@festival/core`, `@festival/theme`, `@festival/ui-utils`, `@festival/auth` |

## Architecture Summary

- **FSTService**: Self-hosted ASP.NET Core — HTTP API + background scraper. PostgreSQL for persistence. 9-phase scrape pipeline, rivals calculation, rankings aggregation.
- **FortniteFestivalWeb**: React 19 SPA with React Router, React Query, CSS modules. 9 feature areas (songs, rivals, shop, player, leaderboards, suggestions, compete, settings, shell).
- **FortniteFestival.Core**: Shared .NET library — song models, calendar API client, instrument enums.
- **packages/**: Shared TypeScript — API client, theme, UI utilities, auth.

## Build & Test

```bash
# Service
dotnet test FSTService.Tests\FSTService.Tests.csproj    # 94% coverage gate
dotnet build FSTService\FSTService.csproj -c Release

# Web
cd FortniteFestivalWeb && npm test                       # Vitest
cd FortniteFestivalWeb && npx playwright test             # E2E (4 viewports)
```

## Cross-Repo Conventions

- **API contract**: `FSTService/Api/ApiEndpoints.cs` defines routes; `FortniteFestivalWeb/src/api/client.ts` consumes them. Changes to one MUST be reflected in the other.
- **Feature flags**: `FSTService/FeatureOptions.cs` ↔ `FortniteFestivalWeb/src/contexts/FeatureFlagsContext.tsx`. Both sides must agree on flag names and defaults.
- **Shared types**: Instrument enums, song models — defined in `FortniteFestival.Core/Config/InstrumentType.cs` and `packages/core/src/`.

## Design Documents

Detailed designs in `docs/`. These are source of truth for feature architecture:

| Document | Topic |
|---|---|
| `docs/database/FSTServiceDatabaseDesign.md` | Database architecture, schemas, data flow |
| `docs/design/EpicLoginDesign.md` | Epic OAuth flow |
| `docs/design/UserRegistrationBackfillDesign.md` | Backfill pipeline, history reconstruction |
| `docs/design/OverallRankingsDesign.md` | Rankings calculation |
| `docs/design/OppsFeatureDesign.md` | Rivals/opps feature |
| `docs/refactor/PLAN.md` | Web app refactoring roadmap (18 phases) |

## Agent Coordination Rules

This repository uses a hierarchical agent organization. All agents follow these rules:

### Memory Protocol
- **Read broadly, write narrowly** — Any agent reads any memory file. Write only to your designated area.
- **Update on completion** — After plan mode: write findings + plan. After execute mode: write outcomes + lessons.
- **Check memory FIRST** — Before researching, read relevant memory files. Previous work may already be documented.
- **Structured format** — Use `## {Topic} ({date})` headers, bullets for facts, `> Lesson:` callouts.

### Consistency Enforcement
- New patterns (endpoints, phases, pages, components) MUST be reviewed by the relevant principal agent.
- Principals maintain living consistency registries in `/memories/repo/architecture/` and `/memories/repo/design/`.
- Sub-agents read registries before planning and follow canonical patterns.

### Testing Coordination
- Code changes hand off to testing agents with context written to `/memories/session/task-context.md`.
- Test failures are classified: TEST BUG (test agent fixes), CODE BUG (area owner fixes), ARCHITECTURE ISSUE (principal reviews).

## Registered Test Accounts

| Username | Account ID |
|---|---|
| SFentonX | `195e93ef108143b2975ee46662d4d0e1` |
| captainparticles | `cb8ebb19b32c40d1a736d7f8efec17ac` |
| kahnyri | `4c2a1300df4c49a9b9d2b352d704bdf0` |
