# Fortnite Festival Score Tracker - Project Guidelines

## Project

FST preserves Fortnite Festival leaderboard history across seasonal resets.

| Component | Path |
|---|---|
| Service/API and worker host | `FSTService/` |
| Service tests | `FSTService.Tests/` |
| Shared .NET code | `FortniteFestival.Core/` |
| React/Vite web app | `FortniteFestivalWeb/` |
| Shared TypeScript packages | `packages/` |

Current architecture and ownership are indexed in `docs/README.md`.

## Operating rules

- Work autonomously through approved repository tasks while safe work remains.
- Stop for required operator input, credentials, privileged access,
  provider/budget decisions, ambiguous user-owned changes, or an uncleared
  live-safety/parity gate.
- Preserve unrelated worktree changes.
- Keep task state and documentation accurate.
- Commit and push accepted/project-required changes unless the operator says
  not to.

## Live safety

- Production Compose ownership is
  `/home/sfenton/Docker/FestivalServiceTracker`; repo Compose files are
  templates.
- Before broad DB probes, deploys, scrapes, or maintenance, check Docker,
  PostgreSQL, freeze/publication state, locks/long queries, disk, CPU, and
  memory.
- Keep all FST database/storage/scratch/export/repack work on the 4 TB FST
  drive unless explicitly overridden.
- Preserve historical correctness, Epic provenance, publication state,
  freeze/unfreeze behavior, and replay/parity evidence.
- Destructive work requires current live-scrape A/B parity, exact objects,
  rollback, and monitoring.

See `docs/operations/live-safety.md`.

## Documentation

Follow `.github/instructions/documentation.instructions.md` and
`docs/governance/documentation.md`.

Whenever documented behavior changes, update the canonical document in the
same change. Create and index a document for a new documentable area. Every
completion must report updated documentation paths or a specific no-impact
reason.

Run:

```bash
node tools/check-docs.mjs
```

## Validation

```bash
dotnet test FSTService.Tests/FSTService.Tests.csproj
dotnet build FSTService/FSTService.csproj -c Release

cd FortniteFestivalWeb
corepack yarn test:unit
corepack yarn build
```

Use the smallest relevant command from `docs/testing/README.md`.

## Contract synchronization

API changes must keep the endpoint aggregator, affected domain endpoint files,
publication contracts/tests, `packages/core/src/api/serverTypes.ts`,
`FortniteFestivalWeb/src/api/client.ts`, and
`docs/reference/api-contract.md` aligned.

Public feature changes must keep `FSTService/FeatureOptions.cs`,
`FSTService/Api/FeatureEndpoints.cs`,
`packages/core/src/api/serverTypes.ts`,
`FortniteFestivalWeb/src/contexts/FeatureFlagsContext.tsx`, and
`docs/reference/feature-flags.md` aligned.

## Dependencies

Any third-party package add/remove/change must update the license manifest
workflow. For web-surfaced dependencies:

```bash
cd FortniteFestivalWeb
corepack yarn licenses:generate
corepack yarn licenses:check
```
