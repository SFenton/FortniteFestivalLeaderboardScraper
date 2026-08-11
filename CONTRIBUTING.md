# Contributing

## Repository layout

- `FSTService/`: ASP.NET Core API and hosted workers
- `FSTService.Tests/`: service tests
- `FortniteFestival.Core/`: shared .NET domain/Epic code
- `FortniteFestivalWeb/`: React/Vite application
- `packages/`: shared TypeScript packages
- `deploy/` and `docker-compose.yml`: deployment templates
- `docs/`: canonical documentation, runbooks, roadmap, decisions, and archive

Read the nearest `AGENTS.md` and
[`docs/README.md`](docs/README.md) before changing a component.

## Setup and validation

Service:

```bash
dotnet test FSTService.Tests/FSTService.Tests.csproj
dotnet build FSTService/FSTService.csproj -c Release
```

Web:

```bash
cd FortniteFestivalWeb
corepack yarn install --immutable
corepack yarn test:unit
corepack yarn build
```

Use targeted lint, unit, shared-package, or Playwright commands from
[`docs/testing/README.md`](docs/testing/README.md).

## Documentation requirement

Documentation changes are part of the implementation, not follow-up work.
Follow [`docs/governance/documentation.md`](docs/governance/documentation.md)
and run:

```bash
node tools/check-docs.mjs
```

Every change must report one of:

```text
Documentation impact: updated <paths>
Documentation impact: none - <specific reason>
```

## API and feature synchronization

API changes must review:

- `FSTService/Api/ApiEndpoints.cs`
- the affected `FSTService/Api/*Endpoints.cs`
- route classification and publication-surface tests/contracts
- `packages/core/src/api/serverTypes.ts`
- `FortniteFestivalWeb/src/api/client.ts`
- `docs/reference/api-contract.md`

Public feature changes must review:

- `FSTService/FeatureOptions.cs`
- `FSTService/Api/FeatureEndpoints.cs`
- `packages/core/src/api/serverTypes.ts`
- `FortniteFestivalWeb/src/contexts/FeatureFlagsContext.tsx`
- `docs/reference/feature-flags.md`

## Dependencies

Yarn 4 is authoritative for `FortniteFestivalWeb`. Other standalone tooling may
use its own package manager and lockfile.

When a third-party npm, NuGet, or bundled dependency changes, update the
license manifest workflow and run:

```bash
cd FortniteFestivalWeb
corepack yarn licenses:generate
corepack yarn licenses:check
```

## Live systems

Repository Compose files are templates. The production project is owned from
`/home/sfenton/Docker/FestivalServiceTracker`.

Do not perform production, database, destructive, or provider changes merely
because a repository command exists. Follow
[`docs/operations/live-safety.md`](docs/operations/live-safety.md), preserve
publication correctness, and keep all FST storage work on the FST drive.
