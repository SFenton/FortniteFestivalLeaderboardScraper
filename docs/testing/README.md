---
status: canonical
owner: repository
last_verified: 2026-08-12
last_verified_commit: 02039c9c
sources:
  - FSTService.Tests/FSTService.Tests.csproj
  - FortniteFestivalWeb/package.json
  - FortniteFestivalWeb/playwright.config.ts
  - FortniteFestivalWeb/playwright.component.config.ts
  - FortniteFestivalWeb/playwright.publication.config.ts
  - FortniteFestivalWeb/e2e/README.md
  - .github/workflows/publish-image.yml
  - .github/workflows/web-playwright-nightly.yml
  - tools/check-docs.mjs
  - tools/fst-worker-compose-guard.test.mjs
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
corepack yarn e2e:typecheck
corepack yarn e2e:component
corepack yarn e2e:publication
```

Playwright browser projects are named by engine and owned layout:

- `chromium-desktop` and `chromium-mobile` run the full functional suite;
- `chromium-wide` owns Manual asset and breakpoint-boundary tests;
- `webkit-mobile` owns critical iPhone-class and accessibility coverage;
- `webkit-desktop` and `firefox-desktop` run in the scheduled matrix.

Component UX uses Playwright's stable stories-and-gallery model through
`playwright.component.config.ts`; publication transitions use a dedicated
network-publication server through `playwright.publication.config.ts`.
Breakpoint widths are parameterized in focused tests rather than represented
as full-suite projects.

The pull-request browser tier runs:

```bash
corepack yarn e2e:ci
```

The scheduled engine/component tier runs:

```bash
corepack yarn e2e:nightly
```

## Merge and release gates

`master` is PR-only with no push bypass. The legacy `version-bump` job name is
retained as a stable required-check context, but the job now only detects
affected images and exposes the workflow SHA. Version changes, generated
license metadata, and the embedded web bundle must be included in the pull
request; the workflow never writes back to `master`.

Every pull request targeting `master` and every update to `master` runs the
same validation workflow without path filtering:

- service build, tests, and coverage;
- web build, embedded-bundle verification, unit/shared tests and coverage;
- dependency and Manual-image checks;
- Playwright Chromium desktop/mobile, wide responsive, WebKit mobile,
  component, and publication suites;
- relevant service and web Docker image builds.

Pull requests build relevant images with `push: false`; they never log in to
GHCR or publish tags. A successful `master` push runs the same gates and
publishes the affected images. The required merge checks are `test`,
`test-web`, `build-and-push-service`, and `build-and-push-web`.

The browser fixture architecture, scenario rules, folder ownership, sharding
environment variables, and targeted commands are documented in
`FortniteFestivalWeb/e2e/README.md`.

Dependency changes that appear on the Licenses page must also run:

```bash
cd FortniteFestivalWeb
corepack yarn licenses:generate
corepack yarn licenses:check
```

## Operational tools

The worker guard has a dependency-free Node harness with stubbed Docker/Compose
behavior, including lock contention, signal cleanup, overall deadlines,
active/frozen cleanup boundaries, runtime qualification failures, dynamic lock
derivation, and live-config-independent checks of repository worker profiles
and restart policies. Its fake Compose implementation omits `fstworker` unless
`--profile worker` is explicit, matching the integration boundary:

```bash
bash -n tools/fst-worker-compose-guard.sh
node --test tools/fst-worker-compose-guard.test.mjs
```

## Documentation

```bash
node tools/check-docs.mjs
git diff --check
```

Documentation-only changes do not require service or web builds unless they
alter executable tooling, generated artifacts, commands, or configuration.
