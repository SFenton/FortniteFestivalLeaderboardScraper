# FestivalWeb Improvement Roadmap

**Audit date:** 2026-07-10  
**Container:** `festivalweb`  
**Mode:** Read-only best-practices, performance, correctness, and consolidation audit  
**Implementation status:** No web application changes were made during this audit.

## Autonomous execution update — 2026-07-10

### WEB-0.1 source and package type errors

**Decision:** Accepted and deployed.

- Reduced the baseline from 133 TypeScript errors across 43 files to zero
  without `any` suppression or disabling strict checks.
- Repaired all-nine-instrument mappings, compact API helpers, theme token/key
  drift, null/undefined handling, browser timer/event types, selected-member
  score preservation, and stale test fixtures.
- `npx tsc -b --pretty false` and the normal `npm run build` pass.
- Eighteen affected test files passed 422 tests; the explicit adapter/ranking
  fixture passed another 13 tests and covers all nine instruments.
- Deployed `festivalweb` only while scrape 1229 continued. Five monitor samples
  kept service, web, worker, and PostgreSQL healthy.
- A fresh real-browser Songs navigation rendered 281 DOM elements, fetched
  `/api/service-info` with HTTP 200, and produced zero console errors. Shell p95
  changed from 0.347 ms to 0.371 ms (+6.9%); proxied service-info p95 improved
  from 1.659 ms to 1.477 ms.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/roadmap-20260710T2105Z/web-0.1/`

### WEB-0.2 hooks correctness

**Decision:** Accepted and deployed.

- Moved all Rival Detail, Rivalry, and Rivals hooks above missing-player/route
  guards and promoted `react-hooks/rules-of-hooks` from warning to error.
- Added rerender coverage for missing-to-valid player and route transitions.
- Full source lint has zero errors and zero rules-of-hooks findings; the
  remaining 434 warnings belong to later exhaustive-deps/style/backlog tasks.
- Rival page coverage passes 43 tests, TypeScript passes, and the production
  build succeeds.
- A real cache-busted Rivals navigation rendered live content with 450 DOM
  elements and HTTP 200 service status while all production containers stayed
  healthy through five monitor samples.
- Browser validation also exposed an old-open-tab hashed-chunk failure across
  festivalweb replacement. That public-recovery gap is taskized as
  `WEB-0.2-D1` and is not being left as a handoff note.

#### WEB-0.2-D1 stale dynamic chunk recovery

**Decision:** Accepted and deployed.

- Added a guarded `vite:preloadError` recovery that reloads once and leaves a
  second failure to the normal error boundary instead of creating a loop.
- Added `no-cache`/`no-store` entry-document headers while retaining immutable
  hashed asset caching.
- Three focused tests prove first recovery, loop prevention, and later-window
  recovery. TypeScript, lint-errors-only, and production build pass.
- Real browser dispatch set `defaultPrevented`, reloaded the page, preserved the
  reload marker, completed with live content and zero console errors, and did
  not trigger a second reload inside the guard window.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/roadmap-20260710T2105Z/web-0.2/`

## Executive decision

FestivalWeb has strong modern foundations, especially route splitting, list
virtualization, React Query, strict TypeScript settings, viewport coverage, and
mobile interaction work. The current release posture is nevertheless
**Needs Attention** because the build/type gates are red, hooks rules are
violated, style governance is contradictory, several route-level caches bypass
React Query, and heavy route content is mounted far beyond what the user can
see.

The accepted direction is:

1. Restore green correctness and style gates before feature expansion.
2. Reduce mounted and loaded Song Details content before broad component
   rewrites.
3. Consolidate network/cache ownership into React Query and propagate
   cancellation.
4. Reduce initial and route-level bundle cost with measured, reversible lazy
   boundaries.
5. Decompose the application shell, FAB registration, contexts, and duplicated
   view logic.
6. Generate one theme/token source and remove contradictory inline-style
   exceptions.
7. Validate and remove dead code only after production reachability proof.

## Audit report delivery

This roadmap is accompanied by a separate operator e-mail:

`FST Autonomous Agent: Recap - Festival Web Deep Audit · Needs Attention`

The delivery is complete only when the repository renderer produces the
HTML/text artifacts and SMTP accepts the message, or the exact SMTP blocker is
recorded with the outbox artifact paths.

## Current evidence

### Live browser baseline

| Surface | Observed evidence | Assessment |
|---|---|---|
| Initial Songs navigation | Main script 358,000 encoded bytes and 1,033,062 decoded bytes; 39 resources; DOMContentLoaded 83.1 ms on local host | Delivery is fast locally, but the initial code payload is broad |
| Mobile Song Details | 2,698 DOM elements, 261 `<img>` elements, 179 already loaded, only 12 visible | Poor mount/load proportionality |
| Song Details scroll surface | One internal scroll container is 13,111 px tall in a 599 px viewport | All-instrument content is mounted eagerly |
| Repeated image nodes | 150 `star_gold.png` nodes and 70 drum icon nodes | Repeated DOM/image components should be CSS/SVG or active-section only |
| Route chunk | `PressableChartPath` chunk is 131,568 encoded / 381,770 decoded bytes | Chart stack is loaded on Song Details before clear user demand |
| Route requests | Two `/api/service-info` requests occurred during one Song Details navigation | Duplicate status ownership or remount/revalidation needs tracing |
| Cached API latency | `/api/leaderboard/.../all?top=10` 5.4 ms; band payload 18.8 ms locally | Cached server responses are not the primary live route bottleneck |

### Static/tooling baseline

| Surface | Observed evidence | Assessment |
|---|---|---|
| Type checking | 133 errors across 43 files, including production/package errors | Bad: release build is blocked |
| ESLint | 456 warnings: 230 inline-style, 158 magic-number, 35 exhaustive-deps, 14 rules-of-hooks | Poor: important correctness findings are warnings |
| Stylelint | 109 errors | Poor: current CSS-module syntax and tooling disagree |
| Initial bundle | 304,695 bytes gzip in the audit build | Poor for the default route |
| Assets | `public/` about 59 MiB; existing `wwwroot/` about 62 MiB | Needs ownership and duplication review |
| Test inventory | 240 unit-test files and 17 Playwright specs | Good breadth |
| License manifest | Current and passing | Great |

## Great / good / okay / poor / bad

| Rating | Areas |
|---|---|
| Great | Route-level lazy imports; virtualized Songs list; strict TS options; multi-viewport Playwright configuration; license manifest workflow |
| Good | React Query and WebSocket foundations; mobile visual-viewport handling; shared pressable primitives; substantial first-run and notification tests |
| Okay | Manifest-only PWA posture; local cached API speed; current shared package boundaries |
| Poor | Context breadth; shell/FAB coupling; route cache ownership; request cancellation; bundle/assets; style governance; accessibility focus management |
| Bad | Red type/build gate; conditional hooks; release-contract drift; current lint policy allowing correctness violations |

## Priority model

- **P0:** correctness, release gate, hook safety, contract safety, zoom, and
  shared-modal focus safety.
- **P1:** measured mobile route cost and duplicate request/load ownership.
- **P2:** stale data, cancellation, and cache consistency.
- **P3:** bundle and asset delivery.
- **P4:** component/context architecture and duplicate code.
- **P5:** style/token consolidation.
- **P6:** dead-code removal.
- **P7:** expanded route-level accessibility coverage, PWA decision, tests, and
  documentation.

## Autonomous execution windows

| Phase/task family | Execution class | Worker scrape gate |
|---|---|---|
| WEB-0 through WEB-7 web-only code/style/test work | `continuous-safe` | Implement, test, deploy, and browser A/B without stopping `fstworker`; verify the public shell and representative API route after deploy |
| Shared API DTO/client contract changes | `scrape-boundary-deploy` with the matching service task | Wait for the active scrape/post-process/publication boundary before coordinated service/client deployment |
| Web changes that alter publication/freeze interpretation | `full-scrape-ab` with SERVICE-0 and PG-1 | Run the coordinated published-source live scrape parity window |

The autonomous executor owns the exact wait-stop-deploy-run-stop-decide cycle.
Web-only tasks do not consume a full scrape unnecessarily. They may be
implemented, deployed by recreating only `festivalweb`, and browser A/B tested
while `fstworker` continues unless the task explicitly depends on post-publish
data or would contaminate an active worker/database A/B.

## Phase WEB-0: Restore trustworthy gates

**Decision:** Accepted  
**Dependencies:** None  
**Rollback:** Small commits grouped by error family; revert an individual family
without changing runtime behavior.

### WEB-0.1 - Repair source and package type errors

**Evidence**

- `src/api/suggestionAdapter.ts:11` lacks current instrument mappings.
- `src/pages/songinfo/SongDetailPage.tsx:394` assumes an invalid score/member
  shape.
- `src/components/common/ConfirmAlert.tsx:161,171` references missing theme
  properties.
- `src/pages/songinfo/components/chart/RankHistoryChart.tsx:335,388` uses
  invalid style keys.
- `packages/core/src/api/serverTypes.ts:1433-1435,1650` has unsafe indexed
  values.

**Work**

1. Group type failures into API-contract, theme-token, test-fixture, and local
   component errors.
2. Fix production/package errors before test-only errors.
3. Add generated or schema-checked service DTOs so `serverTypes.ts` stops being
   an unaudited manual mirror.
4. Add all nine current instruments to shared fixtures and compile-time maps.

**Acceptance**

- `tsc --noEmit` and the normal production build pass with zero errors.
- Contract fixtures cover every service DTO and instrument.
- No new cast to `any` or broad type suppression is introduced.

### WEB-0.2 - Make hooks correctness non-negotiable

**Evidence**

- Conditional hooks occur after early returns in
  `RivalDetailPage.tsx:137-168`, `RivalryPage.tsx:151-186`, and
  `RivalsPage.tsx:338-488`.

**Work**

1. Move all hooks above parameter/error branches.
2. Split invalid-parameter rendering into child components when needed.
3. Promote `rules-of-hooks` from warning to error.
4. Add rerender tests that transition from missing to valid parameters.

**Acceptance**

- Zero hooks-rule findings.
- Missing-to-valid route transitions do not change hook order.

### WEB-0.3 - Repair stylelint scope and policy

**Evidence**

- `.stylelintrc.json` does not understand the repository's CSS Modules
  `composes` and `:global` usage.
- The lint rule discourages DOM `style`, while the migration pattern uses
  `useStyles` and many files disable the rule.

**Work**

1. Define the intended endpoint: CSS Modules, generated CSS variables, or
   typed style objects.
2. Configure stylelint for the selected CSS Modules syntax.
3. Remove blanket per-file disables only after the target pattern is clear.
4. Make CI fail on new style-rule violations while the existing backlog is
   burned down by phase.

**Acceptance**

- Stylelint is green.
- ESLint no longer reports contradictory guidance for the chosen style path.

### WEB-0.4 - Persist performance budgets

**Work**

1. Store raw/gzip/Brotli chunk sizes in CI artifacts.
2. Add route-specific browser baselines for Songs, Song Details, Leaderboards,
   Rivals, Suggestions, Settings, and Manual.
3. Record DOM nodes, loaded images, requests, transferred bytes, JS heap, and
   long tasks at mobile and desktop widths.

**Acceptance**

- Regressions fail a budget instead of relying on manual inspection.

### WEB-0.5 - Restore baseline accessibility safety

**Evidence**

- The global viewport disables pinch zoom.
- `ModalShell.tsx:65-153` lacks initial focus, focus containment, focus
  restoration, inert background, and scroll lock.

**Work**

1. Restore user zoom.
2. Add focus entry, trap, restoration, Escape behavior, and inert background.
3. Add a keyboard-only Playwright test and axe serious/critical gate.

**Acceptance**

- Pinch zoom remains available.
- Keyboard focus cannot escape an open modal and returns to the launcher.
- Axe reports zero serious/critical violations on the shared modal fixture.

## Phase WEB-1: Reduce Song Details mount and image cost

**Decision:** Accepted  
**Dependencies:** WEB-0  
**Projected outcome:** 60-80% fewer mounted image nodes on initial mobile Song
Details; materially lower style/layout work and memory.

### WEB-1.1 - Mount only the active instrument/section

**Evidence**

- Live mobile Song Details mounted 2,698 elements and 261 images.
- Only 12 images were visible; 179 were already loaded.

**Work**

1. Identify which instrument panels and band sections are visually active.
2. Keep summary metadata mounted, but lazy-mount detailed leaderboard rows
   when a panel becomes active or near-viewport.
3. Preserve scroll restoration and keyboard navigation.
4. Consider row virtualization only after active-section mounting is measured.

**Acceptance**

- Initial mobile Song Details has fewer than 1,000 elements and fewer than 80
  image nodes.
- Fewer than 20 hidden images are loaded before user navigation.
- All nine solo instruments and band sections remain reachable.

### WEB-1.2 - Replace repeated decorative image elements

**Evidence**

- One route creates 150 `star_gold.png` nodes and 70 drum icon nodes.

**Work**

1. Replace repeated stars with CSS masks, background layers, SVG symbols, or a
   single semantic rating component.
2. Use one icon component with cached asset ownership.
3. Keep accessible labels independent of decorative image count.

**Acceptance**

- Repeated star images do not create one network/image element per star.
- Visual and screen-reader output remains equivalent.

### WEB-1.3 - Lazy-load chart code at the interaction boundary

**Evidence**

- Song Details loads a 131,568-byte encoded chart-related chunk.

**Work**

1. Split score/rank history charts from the default leaderboard view.
2. Load Recharts only when the chart/history panel opens.
3. Provide a lightweight skeleton that does not reserve the full chart runtime.

**Acceptance**

- Default Song Details navigation does not download Recharts/chart chunks.
- Chart-open latency remains within an agreed p95 budget.

### WEB-1.4 - Remove duplicate service-info requests

**Work**

1. Trace provider remounts, polling hooks, and route loaders.
2. Make one React Query key own service status.
3. Share freshness and polling across consumers.

**Acceptance**

- One route transition produces at most one service-info fetch unless a
  configured polling interval expires.

## Phase WEB-2: Consolidate cache and request ownership

**Decision:** Accepted  
**Dependencies:** WEB-0

### WEB-2.1 - Restrict `pageCache` to navigation state

**Evidence**

- `src/utils/pageCache.ts:1-57` claims not to cache data but stores complete
  leaderboard/history objects in unbounded Maps.
- Rival and Compete routes add more module caches:
  `RivalsPage.tsx:44-47`, `AllRivalsPage.tsx:28-31`,
  `RivalDetailPage.tsx:35-37`, `RivalryPage.tsx:44-46`,
  `CompetePage.tsx:85-86`.

**Work**

1. Move all server data to React Query.
2. Retain only filters, selected tab, and scroll/navigation state in page cache.
3. Define stale time, cache time, invalidation, and profile scope per query.

**Acceptance**

- Remote data has one owner and is visible in React Query devtools.
- Cache entries are garbage-collected after the configured idle period.

### WEB-2.2 - Propagate cancellation through every GET

**Evidence**

- Only a few query paths consume React Query's `AbortSignal`.

**Work**

1. Add optional `AbortSignal` to the API client GET helpers.
2. Pass the signal through every query function.
3. Replace boolean-only effect cancellation with actual request cancellation.

**Acceptance**

- Rapid route/filter changes leave no obsolete requests completing in HAR.

### WEB-2.3 - Move Shop and duplicate local-storage parsing into shared owners

**Work**

1. Move `ShopContext` fetching into React Query.
2. Parse the Songs cache once per update.
3. Validate profile-aware ETag/Vary semantics before sharing URL-only cache
   entries across profiles.

**Acceptance**

- No duplicate synchronous JSON parse for the same payload.
- Profile switches cannot reuse incompatible cached content.

## Phase WEB-3: Reduce initial bundle and static assets

**Decision:** Accepted  
**Dependencies:** WEB-0 and WEB-1

### WEB-3.1 - Narrow shared-package barrels

**Evidence**

- `packages/core/src/index.ts:1-20` re-exports the large suggestion generator
  into the default Songs dependency graph.

**Work**

1. Add feature-specific entry points.
2. Mark package side effects accurately.
3. Import suggestion generation only from the lazy Suggestions route.

**Acceptance**

- Initial JS is at or below 275 KiB gzip.
- The generator is absent from the default Songs chunk.

### WEB-3.2 - Lazy-load shell modals and secondary controls

**Work**

1. Lazy-load search/profile/notification/band-filter modal implementations.
2. Keep only small launch buttons in the initial shell.
3. Evaluate whether DnD Kit is needed before sort interaction.

**Acceptance**

- Initial JS is reduced by at least 10% without interaction regressions.

### WEB-3.3 - Fix Manual asset waterfall

**Evidence**

- `ManualPage.tsx:262-300,351-390` mounts all sections/carousels and their first
  images.
- The first 48 mobile screenshots total about 9.07 MiB.

**Work**

1. Lazy-mount near-viewport sections.
2. Use responsive AVIF/WebP sources and explicit dimensions.
3. Deduplicate byte-identical screenshot pairs.

**Acceptance**

- Initial Manual navigation transfers less than 1 MiB of images.

## Phase WEB-4: Decompose shell, contexts, and large components

**Decision:** Accepted  
**Dependencies:** WEB-0 through WEB-2

### WEB-4.1 - Replace route-specific FAB assembly with a registry

**Evidence**

- `App.tsx:381-1320` owns route-specific FAB construction.
- `FabSearchContext.tsx:20-75,102-286` exposes roughly 50 values.

**Work**

1. Define typed route feature registrations.
2. Let feature routes provide FAB/search actions through narrow adapters.
3. Split state contexts from action contexts or adopt selectors.

**Acceptance**

- Updating one FAB/search state does not rerender unrelated routes.

### WEB-4.2 - Split oversized components by behavior

**Targets**

- `FloatingActionButton.tsx` (about 1,708 lines)
- `SettingsPage.tsx` (about 1,656 lines)
- `SongsPage.tsx` (about 1,440 lines)
- `App.tsx` (about 1,346 lines)

**Acceptance**

- Each extracted unit has one state owner and focused tests.
- React Profiler shows reduced unrelated rerenders.

### WEB-4.3 - Consolidate duplicate UI mechanics

**Candidates**

- Header transition state machines:
  `PageHeaderTransition.tsx` and `PageHeaderActionsTransition.tsx`.
- Mobile keyboard insets:
  `SearchModal.tsx:85-226` and `FloatingActionButton.tsx:325-499`.
- Rank-history chart scaffolding.
- Score-width calculations.
- Suggestions filters/storage.
- Percentile threshold constants.
- Responsive resize tracking.

**Acceptance**

- One tested primitive replaces each duplicated behavior family.

## Phase WEB-5: Create one style and token system

**Decision:** Accepted  
**Dependencies:** WEB-0

### WEB-5.1 - Generate CSS variables from `@festival/theme`

**Evidence**

- `theme.css` and TypeScript token files duplicate values and have already
  drifted (`--line-height-tight` versus missing `LineHeight.tight`).

**Acceptance**

- CSS and TS consume one generated source.
- No undefined CSS custom properties.

### WEB-5.2 - Consolidate duplicate animations and gradients

**Candidates**

- Duplicate `spin` keyframes.
- Three pulse/breathe families.
- Rival gradient/overlay variants.
- Unused selectors and animation names.

**Acceptance**

- One parameterized animation family.
- Zero unused selectors in the audited modules.

### WEB-5.3 - Eliminate blanket style-rule disables

**Evidence**

- 1,587 `style={...}` uses, 323 raw literals, and 150 files disabling
  `react/forbid-dom-props`.

**Acceptance**

- New raw inline styles are blocked.
- Existing exceptions are explicit, justified, and steadily reduced.

## Phase WEB-6: Prove and remove dead code

**Decision:** Accepted after reachability proof  
**Dependencies:** WEB-0 and bundle graph tooling

Candidates include:

- `components/search/SearchPill.tsx`
- `components/shell/desktop/HeaderProfileButton.tsx`
- `components/shell/desktop/HeaderSearch.tsx`
- `components/player/PlayerSearchBar.tsx`
- `hooks/data/useAccountSearch.ts`
- `hooks/data/useAvailableSeasons.ts`
- `hooks/ui/useFadeSpinner.ts`
- `hooks/ui/useLeaderboardColumns.ts`
- `pages/leaderboard/player/components/PlayerHistoryEntry.tsx`
- `pages/rivals/components/LeaderboardNeighborRow.tsx`
- old Song Detail headers/chart/path components
- standalone Songs filter toggles
- `utils/suggestionsFilter.ts`

**Proof plan**

1. Generate production reachability from `main.tsx`.
2. Exclude test-only imports from production reachability.
3. Remove one chain at a time.
4. Compare route chunks, screenshots, and tests.

## Phase WEB-7: Accessibility, PWA decision, tests, and docs

**Decision:** Accepted

### WEB-7.1 - Complete route-level accessibility coverage

Extend the WEB-0 modal gate to every route, preserve visible scroll affordance
where scrollbars are hidden, and add route-level keyboard/axe coverage.

### WEB-7.2 - Bound Suggestions growth

**Evidence**

- `useSuggestions.ts:20-31,101-177` retains mutable module state and appends
  after generator reset.
- `SuggestionsPage.tsx:401-420` renders accumulated categories.

**Acceptance**

- DOM and heap remain bounded after 100 load-more operations.

### WEB-7.3 - Make an explicit PWA decision

The current manifest-only posture is acceptable. A service worker should be
implemented only with tested update, invalidation, offline, and rollback
semantics. "Add a service worker" is not accepted as an unmeasured default.

### WEB-7.4 - Refresh documentation and test policy

1. Make routine test commands enforce intended coverage.
2. Reduce and justify `v8 ignore` directives.
3. Add bundle, Lighthouse, axe, abort-navigation, and long-list memory gates.
4. Replace stale refactor/README claims.

## Projected outcomes

| Outcome | Promotion target |
|---|---|
| Release safety | Build, typecheck, hooks lint, ESLint, and stylelint green |
| Initial delivery | At least 10% initial JS reduction; target <=275 KiB gzip |
| Song Details | <1,000 initial DOM elements, <80 image nodes, <20 hidden loaded images |
| Request behavior | One service-info owner and cancellable GETs |
| Cache correctness | Server data only in React Query with profile-aware keys |
| Maintainability | Route/FAB registry, narrower contexts, shared mechanics, one token source |
| Accessibility | Keyboard-safe modals and zero serious/critical axe findings |

## Explicitly rejected shortcuts

- Do not hide type failures with broad casts or disable strict checks.
- Do not delete dead-code candidates based only on filename/import grep.
- Do not add a service worker without update/offline tests.
- Do not optimize cached local API latency while leaving DOM/bundle cost
  unmeasured.
- Do not replace one duplicated style system with another hand-maintained
  parallel source.
