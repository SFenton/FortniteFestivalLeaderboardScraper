---
status: canonical
owner: repository
last_verified: 2026-08-17
last_verified_commit: dffca41c
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
  - FSTService.Tests/Unit/BandCurrentProjectionOptimizationTests.cs
  - FSTService.Tests/Unit/PlayerStatsTierPersistenceTests.cs
  - FSTService.Tests/Unit/PublicationApiResponseCacheServiceTests.cs
  - FSTService.Tests/Unit/PublicationApiResponseCacheMiddlewareTests.cs
  - FSTService.Tests/Unit/PublicationApiResponseCachePolicyTests.cs
  - FSTService.Tests/Unit/PublicationApiCacheBenchmarkTests.cs
  - FSTService/Scraping/Replay/TierZeroRegularFile.cs
  - FSTService.Tests/Unit/ReplayContractTests.cs
  - FSTService.Tests/Integration/TierOneReplayIntegrationTests.cs
  - tools/postgres-tier1-replay-drill.test.mjs
  - tools/postgres-retire-ix-le-song-rank.test.py
  - tools/postgres-pro-bass-snapshot-rewrite.test.py
  - tools/postgres-pro-bass-snapshot-rewrite-drill.py
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

Focused snapshot-retention policy validation:

```bash
dotnet test FSTService.Tests/FSTService.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~DatabaseMaintenanceDryRunReporterTests|FullyQualifiedName~DatabaseRetentionMaintenanceServiceTests'
```

This proves current, previous, and working publication physical source IDs are
protected through the publication-generation-to-scrape mapping, while stale
source maps for unnamed generations do not remain protected forever.

Pro-bass pilot structural and isolated lifecycle validation:

```bash
bash -n \
  tools/postgres-pro-bass-snapshot-rewrite.sh \
  tools/postgres-pro-bass-snapshot-rewrite-drill.sh

PYTHONDONTWRITEBYTECODE=1 \
  python3 tools/postgres-pro-bass-snapshot-rewrite.test.py

mkdir -p artifacts/pro-bass-pilot-drills
tools/postgres-pro-bass-snapshot-rewrite-drill.sh \
  --work-root "$PWD/artifacts/pro-bass-pilot-drills/<utc-run>" \
  --image postgres:17 \
  --purge-rows 300000 \
  --retained-rows 30000
```

The unit suite locks the production planning-query shape: recursive
leading-index `MIN(snapshot_id)` probes, metadata-only ownership joins,
protected-only fingerprints, no `GROUPING SETS`, no parallel gather, and a
256 MB PostgreSQL temp-file limit. Production stages also require the exact
checksummed verified-live-archive input. This prevents planning-only work from
rehashing all historical rows or consuming the FST emergency reserve.

The drill must report exact archive/restore distribution, content-hash and
full catalog parity, successful
rename-back rollback, successful separate final drop, matching fingerprints,
immediate filesystem reclaim, removed transient containers/PGDATA, retained
archives, and truthful archive/build/swap/rollback recovery after simulating a
missing terminal acknowledgement. It also zeroes copy evidence and truncates
swap evidence after repatriation, then requires catalog-driven scratch
restoration and original rollback. Its measured profile must contain at least
100,000 total and 10,000 retained rows before production capacity planning
accepts it. It is isolated evidence only and never authorizes a production
rewrite.

The final-drop lane also runs `repatriate` and must end with the accepted
partition/catalog in `pg_default`, no scratch-retired relation, and no
temporary tablespace. Unit tests cover dual-filesystem capacity arithmetic,
mount/device/path fencing, repeated cancel-to-terminate emergency handling,
atomic evidence publication, malformed-evidence rejection, archive
distribution/catalog tampering, repatriation dependencies, and final scratch
cleanup.

The production-derived archive restore additionally records one rejected and
one accepted ordering. Restoring parent indexes before archived child indexes
must fail on the duplicate child primary key. Restoring child
table/data/indexes while detached and attaching afterward must produce
`308,536,699` rows, 125 snapshot IDs, exact child catalog/checksum parity, zero
validation temp bytes, exact per-snapshot content fingerprints, full canonical
catalog parity, and complete restore-PGDATA cleanup.

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
  --filter 'FullyQualifiedName~Score_history_evidence_|FullyQualifiedName~Score_history_snapshot_probe_plan|FullyQualifiedName~Plan_apply_and_resume_preserve_evidence'
```

This matrix covers the `caches_staged` non-owner lease/DML/truncate fence and
owner resume, immutable cache-entry evidence, zero-entry published
`song_stats`, active-only row/ranking removal, complete affected-account tier
replacement, unrelated-account preservation, frozen-scope cache filtering,
strict plan report/digest version 6 cutoff serialization/rejection, strict
apply/resume report version 3 compatibility/rejection, null/exact/boundary
observed-score cases, integer-floor rounding, live-shaped promotion evidence,
the `2,045,222,521` pass/`2,045,222,522` reject boundary for all eight maximum
fields, discovery constraints, canonical promotion parsing, manifest
admission, strict observed-report validation, an exact `int.MaxValue` cutoff
without .NET or PostgreSQL overflow, a valid target plan with an unrelated
over-limit catalog maximum saturated to the representable score domain, and
plan/apply/resume digest consistency. It also covers the max-score timeout
default/environment binding/bounds, stage-specific timeout reporting, and
identical configured evidence timeouts across plan, apply revalidation, and
resume. Final-completion coverage verifies that PostgreSQL uses the configured
timeout for immutable cache validation, retains the `5s` lock timeout and
serializable transaction, restores the `120s` mutation timeout, and leaves
validation failures frozen.
Recovery coverage also proves both resume and rollback leases yield public
read locks until each commit fence, resume from
`notifications_quarantined/failed` skips derived rebuild and preserves its
notification audit, and expected affected-account cache rows sort after combo
ID projection.
Evidence-safety coverage pins deterministic bounded account hashes and rejects
raw maintenance account IDs in diagnostic identifiers.

Rollback coverage starts from `paths_promoted`, partial derived progress, and
an ambiguous committed promotion still checkpointed as `rollback_captured`;
the same phase with pre-promotion paths is rejected.
It proves dry-run non-mutation; strict CLI/file/digest/report contracts;
schema upgrade; wrong manifest/plan/rollback/publication/freeze/path/rollback
row/extra-song rejection; active backend/worker/waiting-lock rejection; exact
atomic path restoration; complete derived, tier, notification, and cache
parity; unrelated scope/tier preservation; transaction and final-completion
failure recovery; interruption retry; idempotent already-rolled-back handling;
post-commit acknowledgement reconciliation; final rollback-file revalidation;
restored-maximum-only score-history drift detection; publication-lock yielding
between transaction commit fences; direction-specific apply/rollback
notification audits; stale terminal mutation-gate cleanup; original
apply-audit preservation; atomic notification alignment/checkpoint retry;
terminal dry-run non-mutation; truthful apply/resume rejection from resumable
and terminal rollback phases; pre-mutation report-path reservation; normal
cache-build rejection from rollback cache evidence; truthful terminal
`cleanupPending` reporting and retry;
whole-freeze cache publisher/staging rejection; terminal validation failures
that cannot be success-reconciled; schema reinitialization during an active
freeze; safe removal of unwritten report reservations;
working-pointer-independent cache fencing plus scrape-allocation rejection; and
rollback from `notifications_quarantined`, unrelated/newer-freeze refusal; and
unfreeze only with terminal `rolled_back`. Hosted-mode tests lock rollback into
the strict no-hosted-service one-shot path.

The focused score-history matrix compares the optimized selector/branch
aggregates with the exact pre-optimization SQL on a deterministic randomized
fixture. PostgreSQL 17 golden rows pin the canonical JSON text and both
`hashtextextended` seeds for null fields, microsecond timestamps, and signed
scores/ranks; a full golden fingerprint spans both registered and
nonregistered branches plus established multi-device registration
multiplicity. Named cases cover registered history outside affected scopes,
player fallback on another instrument, ranking fallback on another song,
strict current/history thresholds, a low changed score enabling player
fallback elsewhere, player/ranking overlap, snapshot-high/overlay-low
exclusion, snapshot-low/overlay-high inclusion, overlay-only accounts, and
duplicate registrations. Publication-fence cases reject an unpublished or
wrong publication, a working/non-current generation, incomplete/zero source
rows, and a missing or scrape-mismatched `solo_scope_sources` binding.

The PostgreSQL 17 command-shape/`EXPLAIN (FORMAT JSON)` case forces a generic
prepared plan and verifies the exact snapshot/song/instrument/score predicate,
`Subplans Removed: 8`, no snapshot sequential scan, and an index definition of
`(snapshot_id, song_id, instrument, score DESC)`. Lock-blocked cancellation
and shared-deadline timeout cases require savepoint cleanup, no remaining
selector temp tables, and two successful repeated invocations in the same
repeatable-read transaction. Workflow assertions compare plan evidence with
the master SQL oracle and require apply/resume revalidation to persist that
same evidence.

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

Focused Band current-projection candidate validation:

```bash
dotnet test FSTService.Tests/FSTService.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~BandCurrentProjectionOptimizationTests|FullyQualifiedName~ReplayContractTests|FullyQualifiedName~TierOneReplayIntegrationTests|FullyQualifiedName~DurablePhaseProgressSinkTests|FullyQualifiedName~ScraperOptionsAndModelsTests|FullyQualifiedName~PostScrapeOrchestratorTests.CurrentProjectionCandidateOptionIsForwarded'
```

This matrix covers default-off binding and durable configuration identity;
normal/fallback option forwarding; SQL shape; zero, all-unchanged, one-changed,
mixed, missing-member, nullable-stat, and large bounded scope sets; primary-key
enforcement of member-index uniqueness within the projection correlation key;
the whitespace-insensitive seven-subquery legacy SQL golden shape; exact
projection/scope/global-state hashes; failure rollback, retry, and
cancellation; unchanged successful transaction and derived command/round-trip
counts; and the exact seven-to-one measured plan plus derived aggregation-pass
reduction.

The replay integration suite uses fresh test-container databases to prove
source/production target refusal, canonical marker/object inventory, typed
bounded import, direct production-builder reuse, no publication tables,
deterministic output parity, corrupt/parent mismatch rejection, stale-attempt
refusal, cancellation evidence, and incomplete-output comparison failure.
Tests also require output/comparison version `3`, explicit deterministic and
option-parity profiles, profile-specific timing reasons, operation metrics,
`productionComparableTiming=false`, canonical hash sensitivity, and rejection
of relabeled production-comparable or unknown-profile packages.

Focused stale solo rank-index retirement validation:

```bash
bash -n tools/postgres-retire-ix-le-song-rank.sh
PYTHONDONTWRITEBYTECODE=1 \
  python3 tools/postgres-retire-ix-le-song-rank.test.py
dotnet test FSTService.Tests/FSTService.Tests.csproj -c Release \
  --filter FullyQualifiedName~DatabaseMaintenanceDryRunReporterTests
```

The Python suite uses deterministic fake project/catalog probes. It covers
wrong project/cluster identity, changed definitions/OIDs/bytes, constraint
ownership, active queries/locks/backends, offline-worker enforcement,
unsupported concurrent parent drop, short-timeout transaction failure,
partial-catalog failure, idempotent absence, exact rollback order, reviewed
artifact digests, and truthful byte reporting. A separate isolated PostgreSQL
17 mechanics run must prove 10 objects before, rejection of concurrent parent
drop, zero objects after normal parent drop, and `10|9` after generated
rollback and attachment.

Focused freeze-safe publication API cache validation:

```bash
dotnet test FSTService.Tests/FSTService.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~PublicationApiResponseCache|FullyQualifiedName~Lazy_publication_cache|FullyQualifiedName~SongsCache|FullyQualifiedName~PublicReadGateTests|FullyQualifiedName~FreezeSafePublicationCache|FullyQualifiedName~FreezeSafeFirstPageAlias|FullyQualifiedName~ScrapeTimePrecomputerTests|FullyQualifiedName~MaxScoreMaintenanceWorkflowTests'

dotnet test FSTService.Tests/FSTService.Tests.csproj -c Release \
  --filter FullyQualifiedName~PublicationApiCacheBenchmarkTests \
  --logger 'console;verbosity=detailed'
```

Coverage includes authoritative publication classification, private/
unclassified/conflicting fail-closed admission, rate-limit/auth middleware
order, publication/current-previous identity, same-publication revision
invalidation, exact serializer/body-SHA/byte/ETag/header/query/order/filter
parity, service-restart L2 recovery, frozen hit/miss and no-write behavior,
finite route normalization, canonical alias context isolation, single-flight
waiter sharing, failed/cancelled build recovery, current/previous cleanup,
atomic staging/swap failure recovery, telemetry redaction, and unchanged route
classification/rate limiting.

The refreshed 722,994-byte fixture produced L2 cold p50/p95
`1.452/3.273 ms`, L1 warm p50/p95 `0.199/0.313 ms`, and 10,000-row
write-through p50/p95 `8.366/11.149 ms`. Twenty read-only production samples
per lazy overview variant measured p50 `5.890-8.122 ms` and p95
`6.707-11.114 ms`; every body SHA-256/ETag/size was stable and every variant
remained below the 500 ms target and 1,000 ms hard gate. Provenance and
pre/post safety checks live under
`public-api-cache-review-completion-20260817T133332Z`.

The first live service-only A/B (`public-api-cache-service-ab-20260817T142445Z`)
correctly rejected head `5a227954`: cached overview page 5 was semantically
equal to its uncached endpoint, but two non-ASCII display names were emitted as
`\u` escapes by alias projection, so exact bytes/ETag diverged. Baseline service
and cache were restored. Five protected routes also exceeded the 10% relative
p95 gate (`+13.1%` to `+109.5%`) while staying below 9 ms absolute.
Regression coverage now requires raw UTF-8 equality
for first-page and overview projection plus explicit shared endpoint/precompute
JSON encoder configuration; the repeat A/B must also satisfy the relative-p95
gate.

L1 latency regression coverage also asserts that one authoritative combined
publication/freeze/failed-candidate snapshot is read per warm request, the
publication provider and L2 are not queried again, and publication ID remains
part of the L1 identity.

The accepted repeat evidence is
`public-api-cache-service-ab-repeat-20260817T163341Z`. It records first touch
separately, runs five warm-up cycles, then 120 interleaved samples per route
with rotating route order and six 20-sample p95 batches. Candidate warm p95 was
`1.90-3.47 ms`; all routes improved `55.76-82.97%`, no route met the
predeclared sustained-regression rule, and every candidate p95 remained below
500 ms. Exact live cache/direct parity passed for overview and band; composite
live semantic parity plus image-bound exact projection tests closed the
remaining incident route.

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
