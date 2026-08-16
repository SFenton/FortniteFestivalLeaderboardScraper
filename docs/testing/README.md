---
status: canonical
owner: repository
last_verified: 2026-08-15
last_verified_commit: 354f87eb
sources:
  - FSTService.Tests/FSTService.Tests.csproj
  - FSTService.Tests/coverage.runsettings
  - FSTService.Tests/Unit/PostScrapeOrchestratorTests.cs
  - FSTService.Tests/Unit/ScrapePhaseResolverTests.cs
  - FSTService.Tests/Unit/PhaseProgressCatalogTests.cs
  - FSTService.Tests/Unit/MaxScoreMaintenanceCommandTests.cs
  - FSTService.Tests/Unit/MaxScoreMaintenancePersistenceTests.cs
  - FSTService.Tests/Unit/MaxScoreMaintenanceScoreHistoryEvidenceTests.cs
  - FSTService.Tests/Unit/MaxScoreMaintenanceWorkflowTests.cs
  - FSTService.Tests/Unit/ScraperOptionsAndModelsTests.cs
  - FSTService.Tests/Unit/PlayerStatsTierPersistenceTests.cs
  - FSTService/Scraping/Replay/TierZeroRegularFile.cs
  - FSTService.Tests/Unit/ReplayContractTests.cs
  - FSTService.Tests/Integration/TierOneReplayIntegrationTests.cs
  - tools/postgres-tier1-replay-drill.test.mjs
  - FortniteFestivalWeb/package.json
  - FortniteFestivalWeb/playwright.config.ts
  - FortniteFestivalWeb/playwright.component.config.ts
  - FortniteFestivalWeb/playwright.publication.config.ts
  - FortniteFestivalWeb/.node-version
  - FortniteFestivalWeb/performance-budgets.json
  - FortniteFestivalWeb/scripts/check-performance-budgets.mjs
  - FortniteFestivalWeb/scripts/check-coverage-ignores.mjs
  - FortniteFestivalWeb/scripts/generate-manual-image-variants.mjs
  - FortniteFestivalWeb/scripts/generate-theme-css.mjs
  - FortniteFestivalWeb/scripts/shared-package-boundary-plugin.mjs
  - FortniteFestivalWeb/e2e/README.md
  - .github/workflows/publish-image.yml
  - .github/workflows/web-performance.yml
  - .github/workflows/web-playwright-nightly.yml
  - tools/check-docs.mjs
  - tools/check-coverage-ignores.test.mjs
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

Focused dead/no-op phase cleanup validation:

```bash
dotnet test FSTService.Tests/FSTService.Tests.csproj \
  --filter 'FullyQualifiedName~PostScrapeOrchestratorTests|FullyQualifiedName~ScrapePhaseResolverTests|FullyQualifiedName~PhaseProgressCatalogTests|FullyQualifiedName~ScraperWorkerTests|FullyQualifiedName~GlobalLeaderboardPersistenceTests'
```

This matrix locks legacy-write rank behavior on/off, intentional skip
dispositions, reserved phase IDs, direct legacy band-mode reachability,
PostgreSQL checkpoint/cache-warm contract absence, recurring refresh ownership,
and criticality-aware resume/publication treatment. Focused API/shared tests
also require `reserved: true` on the two retired v2 descriptors, exclude them
from active counts, and prove Tier-0 phase manifests remain unchanged.

The corresponding live acceptance used matched full scrapes `1299` and `1300`.
The candidate retained exact manifest/source/cache key sets and publication
behavior, produced zero critical skips or retired phase rows, and recorded
three nonblocking best-effort retention skips with durable pressure reasons.
No speed claim was made; the unchanged 800/32/4 network lane remained a
control.

The aggregate line denominator excludes long-running external/process/database
orchestration already validated through focused contract and integration
tests. This includes the max-score mutation coordinator and versioned manifest
model, its derived-state orchestration, CHOpt process coordination, and the
player-stat tier rebuilder extracted from the already-excluded post-scrape
orchestrator. Their focused tests still run in every service suite; the
exclusion prevents orchestration plumbing from diluting the 94% unit-testable
core floor.

Focused max-score cache/scope/tier/report validation:

```bash
dotnet test FSTService.Tests/FSTService.Tests.csproj \
  --filter 'FullyQualifiedName~MaxScoreMaintenance|FullyQualifiedName~PlayerStatsTierPersistenceTests|FullyQualifiedName~RankingsCalculatorTests|FullyQualifiedName~ScrapeTimePrecomputerTests|FullyQualifiedName~MetaDatabaseTests'
```

Focused score-history selector differential and cleanup validation:

```bash
dotnet test FSTService.Tests/FSTService.Tests.csproj \
  --filter 'FullyQualifiedName~Score_history_evidence_|FullyQualifiedName~Plan_apply_and_resume_preserve_evidence'
```

This matrix covers the `caches_staged` non-owner lease/DML/truncate fence and
owner resume, immutable cache-entry evidence, zero-entry published
`song_stats`, active-only row/ranking removal, complete affected-account tier
replacement, unrelated-account preservation, frozen-scope cache filtering,
strict plan report/digest version 5 cutoff serialization/rejection, strict
apply/resume report version 3 compatibility/rejection, null/exact/boundary
observed-score cases, integer-floor rounding, live-shaped promotion evidence,
and plan/apply/resume digest consistency. It also covers the max-score timeout
default/environment binding/bounds, stage-specific timeout reporting, and
identical configured evidence timeouts across plan, apply revalidation, and
resume. Final-completion coverage verifies that PostgreSQL uses the configured
timeout for immutable cache validation, retains the `5s` lock timeout and
serializable transaction, restores the `120s` mutation timeout, and leaves
validation failures frozen.

The focused score-history matrix compares the optimized selector/branch
aggregates with the exact pre-optimization SQL on a deterministic randomized
fixture. PostgreSQL 17 golden rows pin the canonical JSON text and both
`hashtextextended` seeds for null fields, microsecond timestamps, and signed
scores/ranks; a full golden fingerprint spans both registered and
nonregistered branches plus established multi-device registration
multiplicity. Named cases cover registered history outside affected scopes,
player fallback on another instrument, ranking fallback on another song,
strict current/history thresholds, player/ranking overlap, and
snapshot/overlay precedence. Lock-blocked cancellation and shared-deadline
timeout cases require savepoint cleanup, no remaining selector temp tables,
and two successful repeated invocations in the same repeatable-read
transaction. Workflow assertions compare plan evidence with the master SQL
oracle and require apply/resume revalidation to persist that same evidence.

The Tier-0 native filesystem syscall shim is excluded from the aggregate line
denominator because its branches are operating-system ABI specific. Focused
contract tests execute the supported Linux no-follow/openat2 behavior,
special-file rejection, lock contention, atomic moves, and ancestor-symlink
guards; the package, manifest, lifecycle, and verifier logic remains in the
normal coverage gate.

Focused replay validation:

```bash
dotnet test FSTService.Tests/FSTService.Tests.csproj \
  --filter 'FullyQualifiedName~Replay'
bash -n tools/postgres-tier1-replay-drill.sh
node --test tools/postgres-tier1-replay-drill.test.mjs
```

The replay integration suite uses fresh test-container databases to prove
source/production target refusal, canonical marker/object inventory, typed
bounded import, direct production-builder reuse, no publication tables,
deterministic output parity, corrupt/parent mismatch rejection, stale-attempt
refusal, cancellation evidence, and incomplete-output comparison failure.
Tests also require output/comparison version `2`,
`productionComparableTiming=false`, the exact deterministic-override reason,
canonical hash sensitivity to the field, and rejection of a relabeled
production-comparable package.

The separate FST-drive drill is the no-published-port/process-isolation proof.
It runs baseline/candidate images in network-none PostgreSQL namespaces and
must retain exact output hashes while cleaning containers and PGDATA.

## Web and shared TypeScript packages

```bash
cd FortniteFestivalWeb
corepack yarn test:unit
corepack yarn test:shared
corepack yarn lint
corepack yarn lint:css
corepack yarn check:coverage-ignores
corepack yarn theme:css:check
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

Representative Songs, Suggestions, Leaderboards, Settings, and Manual routes
must have no moderate, serious, or critical axe violations in the focused
accessibility suite. The same suite owns skip navigation, route
title/announcement, PUSH/POP focus, one-main-landmark behavior, reduced-motion,
Save-Data, and friendly instrument image semantics. WebKit mobile runs this
focused accessibility surface on every PR; WebKit desktop and Firefox desktop
retain it in the nightly matrix.

Component UX uses Playwright's stable stories-and-gallery model through
`playwright.component.config.ts`; publication transitions use a dedicated
network-publication server through `playwright.publication.config.ts`.
Breakpoint widths are parameterized in focused tests rather than represented
as full-suite projects.

Coverage-ignore directives are validated before coverage:

```bash
corepack yarn check:coverage-ignores
```

The checker pins `ast-v8-to-istanbul` parser semantics, requires every
`start/stop` range to be a real comment, rejects nesting, orphaned or EOF
ranges, and caps each range at 50 inclusive lines. Counted `ignore next N`
directives are unsupported. Only four verified `ignore next` directives in
Suggestions first-run demos are permitted; every other executable branch must
be tested or covered by a bounded range. Coverage thresholds remain 88% lines,
79% branches, 86% statements, and 87% functions.

Theme/package contracts are also static gates. `theme:css:check` compares the
generated 115-variable CSS surface with TypeScript tokens, shared-package tests
exercise root-only portal exports and side-effect declarations, and the
deprecated `Size` boundary allows reductions while rejecting new consumers,
properties, or count growth.

The pull-request browser tier runs:

```bash
corepack yarn e2e:ci
```

The scheduled engine/component tier runs:

```bash
corepack yarn e2e:nightly
```

## Web bundle budgets

Web bundle measurements use the exact Node patch in
`FortniteFestivalWeb/.node-version`. Run the budget check with that runtime:

```bash
cd FortniteFestivalWeb
corepack yarn build
node scripts/check-performance-budgets.mjs --out performance-artifacts/bundle.json
```

The report records required, active, and Docker Node versions plus zlib, Vite,
app, core, and theme versions. The check fails when either runtime differs from
the pinned version, when an existing raw, gzip, Brotli, or largest-chunk ceiling
is exceeded, or when entry gzip headroom falls below 5,000 bytes.

The production build also enforces the Rank By interaction boundary. Normal
route and Rank By closures must exclude metric-help definitions,
`FirstRunCarousel`, `Math.tsx`, KaTeX, and KaTeX CSS. The lazy metric-help
closure must contain those modules and retain the direct dynamic edge from Rank
By.
The same Vite plugin compares every `src/**/*.ts` and `src/**/*.tsx` file with
the complete production and lazy chunk graph. Only component stories and the
documented type-only allowlist may be unreachable; any other source file fails
the build as unclassified dead code. It also requires App Manual, Settings, and
First Run English resources to remain outside the entry and inside their
declared lazy owner closures.

Playwright request tests separately prove that KaTeX JS/CSS waits for the
per-instrument info action, KaTeX fonts wait for a formula slide, and band,
combo, and solo-family controls never expose the instrument-only help.

Manual authoring PNGs are outside `public/`; only generated deploy assets enter
the Vite and embedded bundles. Validate source hashes, image metadata,
responsive variants, full-resolution WebP fallbacks, legacy alias files, and
the 17.5 MB public closure with:

```bash
corepack yarn manual:images:check
```

The normal bundle-budget command additionally caps the embedded Manual
directory at 18 MB. Chromium wide/mobile tests protect responsive selection,
request bounds, dimensions, lazy mounting, and layout stability. The
cross-engine browser suite forces one selected WebP candidate to fail and
requires exactly one successful full-resolution WebP fallback without a PNG
request or retry loop.

`specs/browser/i18n-namespaces.spec.ts` navigates Songs to Manual to Settings,
opens a First Run replay, and observes every DOM mutation. Chromium, WebKit,
and Firefox must render translated content without exposing an
`appManual.*`, `settings.*`, or `firstRun.*` key.

## Suggestions performance

The deterministic long-scroll benchmark runs in the primary Chromium desktop
project and can be invoked directly:

```bash
cd FortniteFestivalWeb
corepack yarn performance:suggestions
```

It fixes the generator clock, drives at least 100 accepted load triggers, and
records generated/rendered category counts, total DOM nodes, frosted markers,
scroll height, available JS heap, long tasks, mousemove geometry reads, and
back/forward scroll restoration. Set `SUGGESTIONS_METRICS_PATH` to persist the
JSON report outside the repository and `SUGGESTIONS_TRIGGER_TARGET` to run a
larger manual profile up to 150 triggers. `SUGGESTIONS_CPU_THROTTLE` applies a
Chromium CPU-throttling multiplier for local slow-device validation. CI runs
the benchmark in a dedicated one-worker pass after the normal Chromium desktop
suite. The same pass also
drives a fully filtered session to the 1,000-category ceiling and verifies the
explicit fresh-mix reset.

The accepted PR 4 unvirtualized baseline produced 540 generated/rendered
categories, about 22.7k DOM nodes, 1,471 frosted markers, about 50.6 MB of
post-GC heap growth, a 1.28 s worst observed long task, and 1,471 frosted-card
geometry reads for one mousemove. Structural counts and scroll restoration are
deterministic; heap and long-task observations remain runtime measurements.

The accepted PR 5 virtualized candidate produced 533 generated categories but
only 12 mounted category cards, 519 total DOM nodes, 38 frosted markers, about
8 MB of post-GC heap growth, zero list-growth long tasks above 50 ms, one
geometry read for a hovered row, and exact 83,398 px restoration. CI enforces
20 mounted categories, fewer than 2,500 DOM nodes, fewer than 200 frosted
markers, at most one pointer geometry read, no list-growth long task above
50 ms, less than 20 MB heap growth, and pixel restoration within 4 px.

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
- bundle-performance and source-encoding checks;
- dependency and Manual-image checks;
- Playwright Chromium desktop/mobile, wide responsive, WebKit mobile,
  component, and publication suites;
- relevant service and web Docker image builds.

The image classifier treats `tools/chopt-cli-linux/` as service-affecting so a
binary-only CHOpt update cannot merge without building and publishing a new
FSTService image. Validate classifier changes with:

```bash
node --test tools/validate-publish-image-workflow.test.mjs
```

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
