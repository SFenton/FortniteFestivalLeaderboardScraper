---
status: canonical
owner: repository
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - FSTService.Tests/FSTService.Tests.csproj
  - FortniteFestivalWeb/package.json
  - FortniteFestivalWeb/playwright.config.ts
  - .github/workflows/publish-image.yml
  - tools/check-docs.mjs
update_triggers:
  - Test runners, scripts, projects, coverage gates, CI, or documentation checks change.
---

# Testing and validation

Use the smallest command that proves the changed behavior.

## Service

```bash
dotnet test FSTService.Tests/FSTService.Tests.csproj
dotnet build FSTService/FSTService.csproj -c Release
```

The service suite uses xUnit. Integration coverage includes hosted-role
selection, API route classification, publication contracts, persistence, and
worker behavior. CI enforces the repository's service coverage gate.

## Web and shared TypeScript packages

```bash
cd FortniteFestivalWeb
corepack yarn test:unit
corepack yarn test:shared
corepack yarn lint
corepack yarn lint:css
corepack yarn build
corepack yarn e2e
```

Playwright defines six project IDs: `wide-desktop`, `desktop-wide`, `desktop`,
`desktop-narrow`, `mobile`, and `mobile-narrow`. Run a targeted project/spec
when the change does not require the full matrix.

Dependency changes that appear on the Licenses page must also run:

```bash
cd FortniteFestivalWeb
corepack yarn licenses:generate
corepack yarn licenses:check
```

## Documentation

```bash
node tools/check-docs.mjs
git diff --check
```

Documentation-only changes do not require service or web builds unless they
alter executable tooling, generated artifacts, commands, or configuration.
