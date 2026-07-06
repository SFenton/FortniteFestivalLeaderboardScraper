# Fortnite Festival Score Tracker — Project Guidelines

## Project overview

**Fortnite Festival Score Tracker (FST)** tracks Fortnite Festival leaderboard scores across seasons, instruments, and songs. Leaderboards reset every season, so the service continuously scrapes Epic APIs and persists a historical record.

| Component | Path | Stack |
|---|---|---|
| FSTService | `FSTService/` | .NET / C# — ASP.NET Core + BackgroundService |
| FortniteFestivalWeb | `FortniteFestivalWeb/` | React + TypeScript + Vite |
| FortniteFestival.Core | `FortniteFestival.Core/` | Shared .NET library |
| Shared TS packages | `packages/` | `@festival/core`, `@festival/theme`, `@festival/ui-utils`, `@festival/auth` |

## Core rules

- Work autonomously through approved tasks and priorities. Do not stop at reports, rejected hypotheses, completed probes, commits, or priority boundaries while safe in-scope work remains.
- Stop only for required operator input, credentials/secrets, privileged access, destructive production maintenance, provider/API terms or budget decisions, ambiguous user-owned changes, or live-safety gates that cannot be cleared non-interactively.
- Keep todos and docs accurate: completed tasks must be marked complete; blocked tasks must name the hard gate; safe follow-up work should become an active task instead of a handoff note.
- Commit and push accepted/project-required changes before starting the next autonomous phase unless the operator says not to.

## Live FST safety

- Production compose ownership is `/home/sfenton/Docker/FestivalServiceTracker`; repo compose files are templates unless the operator explicitly says otherwise.
- During backend/database/storage work, `fstservice` and `festivalweb` must stay live, healthy, and usable by public users unless the exact task explicitly approves restarting or redeploying one of them.
- Do not restart `fstworker`, run full scrapes, prune/delete data, drop tables/indexes, move active Postgres data, or run rewrite/repack maintenance without explicit approval for that action.
- Before broad DB probes, deploys, scrapes, or maintenance, check Docker health, Postgres readiness, public-read freeze state, published scrape, locks/long queries, disk headroom, CPU, and memory.
- All FST database/storage/reclaim work must remain on the 4 TB FST drive. Do not use alternate drives for data, scratch, migration, export, or repack workspace unless SFenton explicitly overrides this rule later.
- Preserve historical leaderboard correctness, Epic/API provenance, publication state, freeze/unfreeze behavior, and replay/parity evidence.

## Build and test

```bash
# Service
dotnet test FSTService.Tests/FSTService.Tests.csproj
dotnet build FSTService/FSTService.csproj -c Release

# Web
cd FortniteFestivalWeb && npm test
cd FortniteFestivalWeb && npx playwright test
```

Use the smallest targeted validation that covers changed behavior. Documentation-only changes do not require build/test unless they alter generated docs or tooling.

## Cross-repo conventions

- API contract changes must keep `FSTService/Api/ApiEndpoints.cs` and `FortniteFestivalWeb/src/api/client.ts` aligned.
- Feature flag changes must keep `FSTService/FeatureOptions.cs` and `FortniteFestivalWeb/src/contexts/FeatureFlagsContext.tsx` aligned.
- Shared instrument/song types live in `FortniteFestival.Core/Config/InstrumentType.cs` and `packages/core/src/`.
- Any third-party package add/remove/change must update generated license manifests and pass `cd FortniteFestivalWeb && npm run licenses:generate && npm run licenses:check`.

## Autonomous execution and reporting

- Use `.github/skills/autonomous-plan-executor/SKILL.md` when the operator requests autonomous execution.
- Send or render phase and final recap reports through `node tools/agent-report-email.mjs`.
- Missing SMTP configuration is a reporting degradation. Render to `.outbox/fst-autonomous-agent/` and continue.
- E-mail reports must include accepted, rejected, blocked, and skipped-with-evidence work; commits; validation; performance; artifacts; and the next autonomous starting point when work remains but is hard-gated.

## Design documents

Detailed designs live in `docs/`. Keep relevant docs current when behavior changes.

| Document | Topic |
|---|---|
| `docs/database/PostgresPersistencePriorityPlan.md` | PostgreSQL persistence, reclaim, and throughput roadmap |
| `docs/design/BandRankHistoryVNextDesign.md` | Band rank-history vNext design |
| `docs/design/PhaseSelectiveScraping.md` | Scrape phase-selective execution |
| `docs/design/ProxyRotationDesign.md` | Proxy rotation design |
