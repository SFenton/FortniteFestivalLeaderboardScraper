# FestivalWeb Improvement Roadmap

**Audit date:** 2026-07-10  
**Container:** `festivalweb`  
**Mode:** Read-only best-practices, performance, correctness, and consolidation audit  
**Implementation status:** No web application changes were made during this audit.

## Same-publication repair cache revalidation - 2026-08-03

**Decision:** Accepted and deployed.

- Publication-change refetches now use browser HTTP `no-cache` revalidation
  after either a generation change or a same-publication maintenance refresh.
- This prevents a repaired ranking response from remaining hidden behind its
  prior 30-minute browser cache entry after the server has refreshed
  publication-bound caches.
- Focused publication tests and the production web build pass.
- The Songs client retries a browser-generated `304` without application cache
  using a full no-store response, and accepts legacy partial max-score records
  containing null instrument values.
- Production image: `festivalweb:songs-partial-max-609ffa94`.

## Publication-maintenance retry update - 2026-08-01

**Decision:** Accepted and deployed.

- React Query no longer retries HTTP `502`, `503`, or `504` responses. These
  responses represent a deliberate maintenance/publication boundary or an
  upstream outage; immediate client retries previously multiplied user-visible
  errors and service load without improving availability.
- Player-band background prefetch is explicitly single-attempt.
- HTTP `408`, `425`, `429`, network failures, and ordinary retryable errors
  retain the existing bounded policy.
- Focused retry/error tests passed `5/5`; the TypeScript/Vite production build
  completed successfully.
- Production now runs `festivalweb:maintenance-retry-2f88be87-clean`
  (`sha256:722dfb281aeffe615dc4ea79ed3d826836d35fe94145d3fd76a897456c7765ba`).
  Rollback is `festivalweb:atomic-pub-phase2-fb6e143b`.
- Post-deploy browser validation loaded
  `assets/index-CK5f0Quw.js`, rendered Nick Bodnick at adjusted Max Score %
  rank `4` / `93.9%`, produced zero current console errors, and returned HTTP
  `200` for publication, service info, player, sync status, notifications,
  bands, shop, rankings, and account-name refresh (`/api/songs` correctly
  revalidated with HTTP `304`).
- Publication `6` / scrape `1273` remained current and unfrozen; FSTService,
  FestivalWeb, and PostgreSQL stayed healthy. This web-only deployment did not
  restart or re-arm `fstworker`.
- Implementation commit `2f88be87` is pushed.

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

### WEB-0.3 stylelint scope and policy

**Decision:** Accepted and deployed.

- Chose the documented mixed endpoint: CSS Modules for larger static rule sets,
  typed inline objects for small/runtime-dependent styles.
- Configured stylelint for CSS Modules `composes` and `:global`, intentional
  WebKit compatibility properties, and existing camel-case module keyframes.
- Applied semantic-preserving modern color/media syntax and moved the remaining
  strict pill literals to theme variables.
- Stylelint improved from 109 errors to zero. ESLint now reports zero
  rules-of-hooks errors and zero contradictory `react/forbid-dom-props`
  findings; 150 obsolete file-level disables were removed and total warnings
  fell from 434 to 200.
- TypeScript, production build, and 67 focused UI tests pass. A real browser
  rendered 592 elements with zero console errors; the frosted surface retained
  `rgba(18, 24, 38, 0.78)` and `blur(18px)`.
- The default full Vitest run exhausted its 4 GB worker heap. An 8 GB,
  two-worker retry progressed further but stalled a worker. Both attempts were
  stopped and preserved as evidence; deterministic full-suite resource work is
  taskized as `WEB-7.4-D1` while all affected focused tests remain green.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/roadmap-20260710T2105Z/web-0.3/`

### WEB-0.4 persisted performance budgets

**Decision:** Accepted.

- Added checked raw/gzip/Brotli metrics for every JS/CSS chunk and hard budgets
  for the entry bundle and largest lazy chunk.
- Added Chromium route capture for Songs, Song Details, Leaderboards, Rivals,
  Suggestions, Settings, and Manual at desktop and mobile widths. Captures
  include DOM/image counts, hidden loaded images, requests, transferred and
  decoded bytes, JS heap, long tasks, console/server errors, and navigation
  timings.
- Added CI bundle enforcement/artifacts on pull requests and a dispatchable
  deployed-route browser budget job with uploaded JSON evidence.
- Current entry bundle is 1,033,730 raw bytes, 304,468 gzip bytes, and 252,786
  Brotli bytes. The largest non-entry chunk is 113,108 gzip bytes; all bundle
  budgets pass.
- Fourteen settled live route captures pass their baseline budgets. The
  intentionally visible debt includes desktop Song Details at 4,374 elements,
  639 loaded images, and 625 hidden loaded images; these budgets protect against
  further regression while WEB-1 implements the much lower promotion targets.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/roadmap-20260710T2105Z/web-0.4/`

### WEB-0.5 baseline accessibility safety

**Decision:** Accepted and deployed.

- Removed viewport zoom suppression.
- Shared `ModalShell` now moves initial focus into the dialog, traps Tab and
  Shift+Tab, handles Escape only on the top modal, restores launcher focus,
  inerts/aria-hides the background, inerts underlying nested dialogs, and locks
  body scrolling with reference-counted cleanup.
- Added a deterministic modal accessibility fixture plus keyboard-only
  Playwright coverage and an axe-core serious/critical gate.
- The deployed fixture passed the same Playwright/axe test on festivalweb:
  initial focus was Close, Shift+Tab wrapped to the last action, Escape closed
  the dialog, launcher focus returned, root inert/aria-hidden and body overflow
  were restored, and serious/critical violations were zero.
- Modal unit coverage passes 26 tests; related modal/search/filter regressions,
  TypeScript, lint, stylelint, production build, bundle budget, and generated
  license manifest checks pass.
- Added `axe-core` 4.12.1 (MPL-2.0) for test-only accessibility analysis and
  updated the license generator to embed package-provided license text for
  valid SPDX types that are not built into its shared text catalog.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/roadmap-20260710T2105Z/web-0.5/`

### WEB-1.4 service-info request consolidation

**Decision:** Accepted and deployed.

- Moved the application-wide `QueryClientProvider` above the backend
  availability gate and gave `/api/service-info` one profile-independent query
  key and hook owner. Settings now shares the gate's fresh result instead of
  starting its own timer/request.
- The shared query has an explicit 3-second timeout, no automatic retry,
  5-second Settings freshness/polling, 30-second healthy availability polling,
  a 5-second unavailable recovery interval, and a 10-minute idle cache lifetime.
  A failed availability refetch enters maintenance even when an older successful
  response remains cached.
- Desktop and mobile production captures reduced cold Settings requests from
  `2` to `1`, pre-expiry route-transition requests from `1` to `0`, and
  11.5-second request totals from `5` to `3` (`-40%`). Maximum concurrency
  stayed at one. Settled Settings polling was `5.004` seconds and availability
  polling was `30.004` seconds in both viewports.
- Browser service-info aggregate duration improved `81.3%` desktop and `30.1%`
  mobile; p95 improved `74.0%` desktop and `3.1%` mobile. Thirty-request shell
  and service-info checks also improved or held within the 10% gate. Settings
  became visible `2.9%` faster desktop and `1.1%` faster mobile, with zero
  console errors.
- Focused API/query-key/hook/gate/Settings coverage passed `138/138`; desktop
  and mobile ownership Playwright passed `2/2`. TypeScript, ESLint errors-only,
  production build, bundle budgets, and the unchanged-dependency license check
  passed.
- Production runs `festivalweb:web14-b843ef61`
  (`sha256:0c571eace67b74cc4b05c7a1eabe2dbce34811dee883d2425c22d14920781d8b`).
  FestivalWeb, FSTService, and PostgreSQL are healthy; published scrape `1236`
  remains safe and unfrozen; the worker remains held/offline; mapped solo reads
  remain HTTP `200`; and isolated derived, band-song, and export routes remain
  HTTP `503`. Rollback is the prior `festivalweb:service02-824415e9` image.
- Final FST-drive free space is `48,544,817,152` bytes, preserving
  `3,396,591,616` bytes above the measured scrape boundary.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/web-1.4-service-info-20260725T163705Z/`

### WEB-2.1 remote-data cache ownership

**Decision:** Accepted and deployed.

- Restricted `src/api/pageCache.ts` to navigation state: Song Details retains
  scroll, song leaderboards retain page/scroll, and rankings retain page/scroll.
  Complete leaderboard, history, score, instrument, band, profile, and rival
  payloads no longer enter those Maps.
- Removed response caches from Rivals, All Rivals, Leaderboard Rivals, Rival
  Detail, Rivalry, and Compete. React Query now owns those payloads with
  profile/song/instrument/band-scoped keys, five-minute freshness, ten-minute
  idle collection, targeted invalidation, same-song/instrument placeholders,
  and no cross-profile placeholder reuse.
- Removed the unbounded generic API ETag response Map. Shop keeps one dedicated
  ETag owner until WEB-2.3; this phase did not move Shop ownership.
- Fail-closed query errors remain in React Query across route remounts without
  retry storms. The first deployed candidate, `festivalweb:web21-10c3c084`,
  was rejected and rolled back when matched A/B showed Rivals p95
  `397 -> 1,513 ms` and requests `6 -> 51`. Commits `8712e426` and
  `ed161ef6` repaired cached-error and transition semantics before promotion.
- The accepted 20-sample matched A/B held route-ready p95 within the 10% gate:
  Player `1,295 -> 1,295 ms`, song Leaderboard `36 -> 32 ms`, Rivals
  `11 -> 11 ms`, and Compete `24 -> 14 ms`. Route-window requests were Player
  `330 -> 352` (`+6.7%`), Leaderboard `0 -> 0`, Rivals `5 -> 2`, and Compete
  `664 -> 5`. DOM p95 changed `84/651/104/92 -> 85/652/105/102`; heap p95
  stayed effectively flat at about `9.7-13.1 MB`.
- Focused API/cache/page coverage passed `199/199`; the ownership browser flow
  passed `6/6` across every configured viewport. TypeScript, ESLint
  errors-only, Stylelint, encoding, production build, and bundle budgets pass.
  The final entry is `1,037,632` raw, `305,891` gzip, and `253,882` Brotli
  bytes; the largest lazy chunk is `113,108` gzip bytes.
- The broad 450-case Playwright matrix was also run but rejected as a release
  gate: 138 unrelated FRE/notification/scroll cases assume healthy
  derived/player/band APIs, while the approved live state intentionally returns
  HTTP `503` for those paths. The focused deterministic WEB-2.1 browser matrix
  is green and validates dedupe, back navigation, pagination state, and profile
  switching.
- Production runs `festivalweb:web21-ed161ef6`
  (`sha256:8fe4a21ca24304510e1ac1752e91d6a2730bd3608634d0bec2358b6c93e223a9`).
  FestivalWeb, FSTService, and PostgreSQL are healthy; published scrape `1236`
  remains unfrozen; the worker remains offline; mapped solo reads remain HTTP
  `200`; and isolated player-derived/band routes remain HTTP `503`. Final
  browser console output contains only those expected `503` resource messages,
  with no JavaScript/page errors. Rollback is `festivalweb:web14-b843ef61`.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/web-2.1-remote-cache-20260725T180703Z/`

### WEB-2.2 request cancellation

**Decision:** Accepted and deployed.

- Added typed optional request options to `48/49` GET/search API helpers while
  preserving selected-profile headers, Songs ETag plus `no-cache`, service-info
  `no-store`, response parsing, and existing call defaults. Shop remains the
  sole exception and keeps its dedicated manual ETag/websocket owner for
  WEB-2.3.
- All `56/56` React Query query functions now consume the supplied
  `AbortSignal`. Manual account and unified search, Suggestions rival data,
  sync-status polling, Settings version/profile polling, Player History, and
  Paths text data now abort real requests instead of only suppressing late
  state writes. POST writes, websocket ownership, `pageCache`, and cache
  policies were not changed.
- Service-info caller cancellation remains a React Query cancellation with no
  user-visible error or retry. Its independent three-second timeout now throws
  a distinct `TimeoutError`.
- In the matched 20-sample production-image browser A/B, obsolete completed
  responses changed from `100 -> 0` and explicit aborts from `0 -> 100`.
  Completed requests changed from `749 -> 613` (`-18.2%`) and request-count p95
  from `38 -> 31` (`-18.4%`).
- Rapid transition p95 stayed within the 10% gate: route
  `906 -> 914 ms` (`+0.9%`), filter `23 -> 21 ms` (`-8.7%`), and profile
  `874 -> 876 ms` (`+0.2%`). Baseline and candidate captures had zero console
  errors, page errors, or unhandled rejections.
- Focused cancellation coverage passed `156/156`; affected band/Compete,
  route, and search/filter suites passed `70/70`, `339/339`, and `126/126`.
  The focused React Query ownership plus cancellation Playwright matrix passed
  `12/12` across all six configured viewports. TypeScript, ESLint errors-only,
  Stylelint, encoding, production build, and bundle budgets passed.
- The final entry bundle is `1,038,787` raw, `306,337` gzip, and `254,200`
  Brotli bytes; the largest lazy chunk is `113,106` gzip bytes.
- Production runs `festivalweb:web22-a3a8ca01`
  (`sha256:08dbbea51609e5e0a1a9cb2ed4de9d4c98738d1400e60362f95e2c25e650c3ea`).
  FestivalWeb, FSTService, and PostgreSQL are healthy; published scrape `1236`
  remains unfrozen; the worker remains offline; mapped solo reads remain HTTP
  `200`; and player-derived, band, and export routes remain HTTP `503`.
  Rollback is `festivalweb:web21-ed161ef6`.
- Implementation commit `a3a8ca01` and browser evidence repair `26b241dd`
  are pushed.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/web-2.2-request-cancellation-20260725T181349Z/`

### WEB-2.3 Shop and persisted-cache ownership

**Decision:** Accepted and deployed.

- `ShopContext` now consumes one profile-invariant React Query entry with a
  five-minute stale time, ten-minute idle lifetime, disabled automatic retry,
  targeted invalidation, shared-request dedupe, and real `AbortSignal`
  cancellation. The browser HTTP cache owns Shop ETag revalidation while React
  Query owns parsed Shop data; the previous module-level Shop response/ETag
  cache and boolean-only effect cleanup are gone.
- The Shop websocket remains the live-update source. Query-backed IDs,
  leaving-tomorrow state, new-item state, URLs, and enriched cards remain the
  fallback until a websocket snapshot/delta becomes authoritative; failed
  refetches retain last-good query data and offline startup still fails closed
  to an empty Shop until HTTP or websocket data arrives.
- `src/api/songsCache.ts` is now the only Songs local-storage parser. It
  validates required and optional normalized song fields, versions and scopes
  the public cache, migrates valid version-2 entries, removes invalid entries,
  memoizes by raw storage value, and shares the same normalized object between
  `FestivalContext` placeholder rendering and `api.getSongs`.
- URL-only Songs and Shop cache sharing is proven safe rather than assumed.
  Both service endpoints use global `SongsCacheService`/`ShopCacheService`
  payloads without selected-profile reads. Live anonymous, player, and band
  requests produced identical body hashes and ETags, and each cross-profile
  `If-None-Match` request returned HTTP `304`; `Vary` contains only
  `Accept-Encoding`. Query and storage scopes therefore remain explicitly
  `public`.
- App settings and Suggestions filters now validate known persisted fields,
  merge explicit defaults, carry storage versions, preserve supported legacy
  records, and rewrite malformed, wrong-typed, or unsupported-version values to
  visible defaults. The duplicate Suggestions utility parser is now only a
  compatibility re-export of the production owner.
- Focused ownership coverage passed `201/201`, Settings context/page coverage
  passed `82/82`, and Songs plus first-run coverage passed `427/427`. The
  focused production-image Playwright flow passed `20/20` for both baseline and
  candidate and `6/6` candidate viewports. TypeScript, project and changed-file
  ESLint, Stylelint, encoding, production build, and bundle budgets passed.
  The monolithic Vitest process was rejected as a release gate after its Node
  worker exhausted the existing 4 GiB heap; bounded affected suites supplied
  the release evidence instead.
- Matched browser p95 remained within the 10% gate: Songs
  `1,005.6 -> 1,014.2 ms` (`+0.86%`), Shop `385.9 -> 388.2 ms`
  (`+0.58%`), and profile switch `109.2 -> 109.5 ms` (`+0.32%`).
  Songs cache parses fell `3 -> 1`; Songs and Shop stayed at one request each;
  profile switching added zero catalog requests; heap p95 improved
  `10,305,831 -> 10,221,845` bytes. Long-task maximum held `52 -> 52 ms`,
  and both captures had zero console errors, page errors, or unhandled
  rejections.
- The final entry bundle is `1,041,819` raw, `307,240` gzip, and `254,947`
  Brotli bytes; the largest lazy chunk is `113,107` gzip bytes.
- Production runs `festivalweb:web23-ac6b4773`
  (`sha256:ca4a8ac0526235e62a791a9ae5dee9b8191d881a0647ed8296b6faa3ea26a85c`).
  FestivalWeb, FSTService, and PostgreSQL are healthy; published scrape `1236`
  remains unfrozen; the worker remains held/offline; Songs, Shop, service info,
  and mapped solo reads return HTTP `200`; isolated player-derived, band, and
  export routes remain HTTP `503`. Live browser verification showed Songs,
  Shop, and the selected-profile control with one Songs request, one Shop
  request, zero catalog requests on profile switch, and zero JavaScript errors.
  Rollback is `festivalweb:web22-a3a8ca01`.
- Implementation commit `ac6b4773` and deterministic browser-evidence commit
  `c7acb57b` are pushed.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/web-2.3-shop-storage-20260725T192900Z/`

### WEB-3.1 shared-package barrel narrowing

**Decision:** Accepted and deployed.

- The measured static path was
  `main.tsx -> App.tsx -> SongsPage.tsx -> @festival/core -> index.ts`.
  Because the package had no `sideEffects` declaration, Rollup conservatively
  retained the root barrel's `suggestionGenerator.ts` re-export in the initial
  entry. The generator contributed `79,634` rendered module bytes, and the
  initial graph contained seven `@festival/core` modules totaling `96,415`
  rendered bytes.
- `@festival/core` now publishes explicit `runtime`, `config`, `api`, `app`,
  `suggestions`, `types`, and `persistence` entries plus compatibility
  subpaths. Web TypeScript and Vite resolve the package through those exports
  instead of source aliases. The backward-compatible root remains intact for
  React Native and other consumers.
- All Web and shared-native runtime sources now avoid the root barrel.
  Type-only imports use the type surface where appropriate, API contracts use
  the API entry, and only the lazy Suggestions feature imports suggestion
  generation. Package-resolution, source import-graph, and production Rollup
  assertions fail if the root or generator returns to the initial graph.
- `sideEffects: false` is based on an explicit core-package audit. No core
  module registers globals, installs polyfills, mutates browser state, or
  performs host-visible import-time work. The package-local `combos` IIFE,
  HTTP-error Map, and i18n fallback registry are observed only through called
  exports. Environment-sensitive sibling packages were not reclassified.
- The production entry moved from `1,041,819` to `991,967` raw bytes
  (`-49,852`, `-4.79%`), `307,240` to `296,511` gzip bytes
  (`-10,729`, `-3.49%`), and `254,947` to `246,663` Brotli bytes
  (`-8,284`, `-3.25%`). The largest lazy chunk remained `113,107` gzip bytes.
  The initial core graph fell from seven modules to five, and the generator is
  now present only in the lazy `SuggestionsPage` chunk.
- Live browser delivery confirmed the graph change. Songs loaded only
  `index-E6kVq9vj.js`; its decoded size was `991,967` bytes and transfer size
  fell `361,474 -> 348,030` bytes (`-13,444`, `-3.72%`). Suggestions then
  requested `SuggestionsPage-0oDd_KWK.js`; total route-script transfer held
  `382,171 -> 381,731` bytes because the deferred generator moved out of the
  entry rather than being duplicated.
- Core and Web TypeScript, production build, ESLint, Stylelint, license
  generation/check, tightened bundle budgets, and `27/27` package-boundary
  focused tests passed. Independently bounded runs passed `53/56` changed test
  files. The other three files changed only import specifiers: two retain
  pre-existing stale provider/animation expectations and the Rivals aggregate
  retains the documented full-suite resource stall. Default and one-worker
  monolithic Vitest runs reproduced the existing `WEB-7.4-D1` resource issue;
  no WEB-3.1 regression was isolated.
- Production runs `festivalweb:web31-a8d76359`
  (`sha256:7326540d5c2b3b3330f3fd0e2f1917ff59dab0f71bbb811114c858f764c28dbb`).
  FestivalWeb, FSTService, and PostgreSQL are healthy; published scrape `1236`
  remains unfrozen; the worker remains held/offline; Songs, service info, and
  mapped solo reads return HTTP `200`; isolated player-derived, band, and
  export routes remain HTTP `503`. Baseline and candidate browser captures
  both had zero page errors and the same ten expected failed-resource console
  messages from those fail-closed routes.
- Implementation commit `a8d76359` is pushed. Rollback is
  `festivalweb:web23-ac6b4773`.
- The original cross-phase `275 KiB` gzip target is not yet complete:
  `296,511` bytes is `14,911` bytes above `275 KiB`. WEB-3.2 owns the remaining
  initial-shell reduction without folding shell modal, DnD, chart, or Manual
  asset work into this phase.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/web-3.1-shared-barrels-20260725T201725Z/`

### WEB-3.2 shell modal and secondary-control lazy loading

**Decision:** Accepted and deployed.

- The production Rollup audit found the remaining initial-shell cost in
  `SearchModal` (`29,399` rendered bytes), `MobileNotificationsModal`
  (`39,220`) plus notification presentation (`32,775`), Songs Sort/Filter
  (`10,626`/`20,045`), and DnD Kit (`120,608`). The hidden DnD path was
  `SettingsContext -> PathDataTable -> @dnd-kit`; the path-column settings
  contract now lives in a lightweight module. RankBy/KaTeX was already outside
  the initial closure behind route chunks. The primary mobile FAB and its
  `2,738`-byte menu stayed eager because splitting the launch surface was
  shell decomposition for negligible savings.
- Search/profile, notifications, selected-band filtering, Songs Sort, and
  Songs Filter now cross explicit interaction boundaries. Intent from header,
  toolbar, band-filter, and FAB controls starts a deduplicated preload.
  First-open loading and failure remain accessible modals with close, Escape,
  focus trapping/restoration, and reload actions. Once loaded, controls stay
  mounted through their close animation and reopen without another request.
- Notification feed types, mock fixtures, and surface filtering were separated
  from the visual modal so header unread state remains eager while the
  presentation chunk is deferred. Source and production Rollup gates reject
  any return of the modal implementations, RankBy/KaTeX, PathDataTable, or DnD
  Kit to the initial Songs graph and prove DnD remains reachable from Sort.
- The entry moved `991,967 -> 858,619` raw bytes (`-13.44%`),
  `296,511 -> 258,107` gzip bytes (`-12.95%`), and
  `246,663 -> 216,080` Brotli bytes (`-12.40%`). This clears both the
  `275 KiB` target and the roadmap's `10%` gate. Largest lazy-chunk gzip held
  `113,107 -> 113,108` bytes.
- New on-demand gzip chunks include DnD/shared sortable code (`15,431`),
  notifications (`9,158`), Search (`5,543`), Filter (`2,783`), Sort (`1,927`),
  the band picker (`2,051`), and the band-filter shell (`570`). Live initial
  script transfer fell `348,030 -> 302,982` bytes (`-45,048`, `-12.94%`).
- The first deployed candidate was rejected and rolled back because React
  Suspense delayed actual control readiness to about `800 ms` despite showing
  the loading shell immediately. Commit `6c9c4040` added an external import
  readiness gate and a synchronously fulfilled React-lazy handoff. Matched
  final p95 control-ready latency was Search `26.3 -> 26.9 ms`, profile
  `22.7 -> 29.8`, notifications `29.0 -> 50.3`, band filter
  `20.9 -> 32.6`, Sort `22.8 -> 30.9`, Filter `53.4 -> 54.4`, and mobile
  Search `21.7 -> 32.3`. Every path stayed below the `100 ms` absolute gate
  and within the `25 ms` sub-frame/noise-floor regression allowance; reopen
  p95 stayed below `43 ms`.
- Focused Vitest passed `237/237`; desktop/mobile Playwright passed all five
  applicable lazy-interaction scenarios. TypeScript, production build,
  tightened bundle budgets, ESLint (`0` errors; repository baseline warnings
  only), Stylelint, encoding, and license-manifest checks passed. Live browser
  smoke covered first open, close/reopen, focus restoration, mobile
  input/Enter behavior, six notification rows, profile target restriction,
  selected-band filtering, Sort apply, and deferred network requests.
- Live captures had zero page errors. Initial Songs, Search, profile, and Sort
  had zero console errors; notification/filter/band contexts retained only the
  expected HTTP `503` failed-resource messages from unavailable derived reads.
  An actual WEB-3.1 tab survived the deployment: its missing old chunks set the
  stale-chunk reload marker, reloaded `index-C9Uudn0F.js`, preserved
  `#/settings`, and rendered Settings with zero page errors.
- Production runs `festivalweb:web32-6c9c4040`
  (`sha256:6c13cd19860bb387bc97221f4c32bbda0fb81954caf07dde7f186ea0c959be67`).
  FestivalWeb, FSTService, and PostgreSQL are healthy; published scrape `1236`
  remains unfrozen and the worker remains offline. Songs, service info, and
  mapped solo reads return HTTP `200`; isolated player-derived, band, and
  export reads remain HTTP `503`. Rollback is `festivalweb:web31-a8d76359`.
- Implementation commits `68c100fb` and `6c9c4040` are pushed. WEB-3.2 did
  not include chart lazy loading, Manual assets, or shell/context
  decomposition.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/web-3.2-shell-lazy-20260725T205821Z/`

### WEB-3.3 Manual asset waterfall optimization

**Decision:** Accepted and deployed.

- The Manual contains `12` top-level sections, `36` subsections, and `48`
  carousels. The baseline shipped `144` PNG captures totaling `59,985,540`
  bytes; its `48` mobile-first images totaled `9,508,688` bytes and decoded to
  `63,198,720` RGBA bytes. Cold-cache browser interception exposed the full
  eager/repeated waterfall at up to `144` requests and `28,569,264` transferred
  bytes, with `47/48` loaded images outside the viewport.
- Each carousel now keeps a stable shell in document order but mounts its
  image, controls, and state only after remaining within a `400 px`
  near-viewport margin. A short dwell prevents fast/smooth scrolling from
  mounting transient sections. Quick-link intent mounts the destination
  carousel immediately and suppresses intermediate observations, so jumping
  to Settings requests only the destination and adjacent near assets rather
  than the intervening Manual.
- Mounted carousels remain mounted, preserving selected viewport, swipe,
  previous/next buttons, translated labels/alt text, deep-link geometry, and
  responsive behavior. The visible first carousel stays eager; later near
  carousels use native lazy loading and asynchronous decode. Explicit source
  dimensions keep the existing `16:10` frame stable.
- The asset pipeline generates `376` content-addressed WebP variants totaling
  `16,433,530` bytes from `141` canonical PNG fallbacks. Mobile captures use
  `240/390w`, compact captures `480/768/1024w`, and wide captures
  `480/800/1440w`. Sample fidelity measured SSIM
  `0.993201-0.998104` and PSNR `46.49-51.74 dB`.
- Three byte-identical `song-detail-cards` PNGs now alias
  `song-detail-overview`, removing `647,323` source bytes. The duplicate
  maskable PWA icon now uses the existing `512 px` icon, removing another
  `121,965` bytes. Nginx preserves all old URLs through internal aliases and
  now serves WebP/AVIF with immutable caching, so deployed or installed stale
  clients keep working.
- Matched p95 desktop/mobile image transfer moved
  `28,569,264 -> 133,482/72,818` bytes (`-99.53%/-99.75%`), requests
  `144 -> 4`, decoded pixels `63,198,720 -> 1,082,000/172,800`
  (`-98.29%/-99.73%`), and DOM elements `1,403/1,202 -> 998/797`
  (`-28.87%/-33.69%`). Heap moved `16.1 -> 15.2/11.2 MB`.
- Desktop heading/first-image p95 improved `898.8/910.9 ->
  892.6/906.3 ms`; mobile moved `864.7/874.9 -> 875.1/883.7 ms`
  (`+1.20%/+1.01%`). Carousel-ready p95 improved
  `32.23 -> 7.75 ms` desktop and `29.79 -> 10.07 ms` mobile. CLS was
  unchanged. Final slow-4G-style initial image transfer was `75,334` bytes
  desktop and `40,358` mobile.
- Focused Vitest passed `17/17`; the applicable desktop/mobile Playwright
  scenarios passed `2/2`. TypeScript, ESLint (`0` errors; repository baseline
  warnings only), Stylelint, encoding, production build, license checks,
  generated-asset checks, bundle budgets, and tightened Manual route budgets
  passed. Final route captures had `14` requests, `0` long tasks, `0` console
  errors, and `0` server errors.
- Production runs `festivalweb:web33-b1ca6606`
  (`sha256:1aa147afdbbf54ad09abbe1e7ec54b4e0a333c9b5fed62e8337162055b91b5cb`).
  FestivalWeb, FSTService, and PostgreSQL are healthy; published scrape `1236`
  remains unfrozen and the worker remains offline. The App Manual feature flag
  remains fail-closed; browser validation enabled only the response in its
  isolated context.
- A live WEB-3.3 tab survived the final deployment: the missing old chunk set
  the stale-reload marker, reloaded `index-D9N8vVJj.js`, preserved
  `#/manual`, and had zero page errors. The one console error was the expected
  missing old chunk that triggered recovery. Rollback is
  `festivalweb:web32-6c9c4040`.
- Implementation commits `b08b9150`, `bc71da99`, and `b1ca6606` are pushed.
  WEB-4.1 is the next web roadmap task.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/web-3.3-manual-waterfall-20260725T221635Z/`

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

**Decision:** Accepted and deployed on 2026-07-25.
**Next dependency:** WEB-2.1 can now restrict `pageCache` to navigation state;
WEB-2.2 remains separate generic cancellation work.

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

**Decision:** Accepted and deployed on 2026-07-25.

**Next dependency:** WEB-2.2 can propagate cancellation through remaining GET
paths. WEB-2.3 remains the separate Shop/local-storage ownership phase.

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
- Focused tests prove scoped invalidation, profile switching, request dedupe,
  cached-error remount behavior, ten-minute idle collection, and navigation
  state preservation without response payloads in page/module Maps.

### WEB-2.2 - Propagate cancellation through every GET

**Decision:** Accepted and deployed on 2026-07-25.

**Next dependency:** WEB-2.3 can move Shop and duplicate local-storage parsing
into shared owners. Shop was deliberately not moved during WEB-2.2.

**Evidence**

- Baseline inventory found `49` GET/search API methods with only `5`
  signal-aware and `56` query functions with only `3` signal-aware.
- The accepted implementation leaves only `getShop` and its existing
  boolean-cleanup owner for WEB-2.3; every other GET/search helper and every
  query function is cancellation-aware.
- Matched production-image network captures prove obsolete route, filter, and
  profile requests are aborted rather than completed, with no request-count or
  p95 regression.

**Work**

1. Add optional `AbortSignal` to the API client GET helpers.
2. Pass the signal through every query function.
3. Replace boolean-only effect cancellation with actual request cancellation.

**Acceptance**

- Rapid route/filter changes leave no obsolete requests completing in HAR.

### WEB-2.3 - Move Shop and duplicate local-storage parsing into shared owners

**Decision:** Accepted and deployed on 2026-07-25.

**Next dependency:** WEB-2 cache/request ownership is complete. WEB-3.1 can
narrow shared-package barrels and remove the suggestion generator from the
default Songs dependency graph.

**Evidence**

- Shop HTTP data has one cancellable, deduplicated, profile-invariant React
  Query owner with explicit stale, garbage-collection, invalidation, and
  cached-failure semantics.
- Songs local storage has one validated, versioned, memoized parser shared by
  placeholder and API paths.
- Source-contract tests plus live anonymous/player/band body, ETag, `Vary`, and
  conditional-request captures prove the public cache scope is profile
  invariant.
- Invalid settings and Suggestions JSON is visibly reset to versioned defaults
  while valid legacy records migrate.
- Matched production-image browser evidence reduced Songs parses from `3` to
  `1`, retained one Songs request and one Shop request, added no catalog request
  on profile switch, and held all route p95 changes below `1%`.

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

**Decision:** Accepted and deployed on 2026-07-25.

**Next dependency:** WEB-3.2 can lazy-load shell modals and secondary controls.
The generator boundary is complete; the broader `275 KiB` gzip target remains
`14,911` bytes away and must not be closed by folding later WEB-3 scope into
this task.

**Evidence**

- `packages/core/src/index.ts:1-20` re-exports the large suggestion generator
  into the default Songs dependency graph.
- The accepted Rollup graph removes the generator from the initial closure and
  places it only in the lazy Suggestions chunk.

**Work**

1. Add feature-specific entry points.
2. Mark package side effects accurately.
3. Import suggestion generation only from the lazy Suggestions route.

**Acceptance**

- Initial JS moved `307,240 -> 296,511` gzip bytes; the cross-WEB-3
  `275 KiB` target remains open for WEB-3.2.
- The generator is absent from the default Songs chunk.

### WEB-3.2 - Lazy-load shell modals and secondary controls

**Decision:** Accepted and deployed on 2026-07-25.

**Next dependency:** WEB-3.3 can address the Manual image waterfall. The
cross-WEB-3 initial-shell target is complete; chart loading and shell/context
decomposition remain owned by their existing tasks.

**Evidence**

- Initial JS is `258,107` gzip bytes, down `12.95%` from WEB-3.1 and below
  `275 KiB`.
- Search/profile, notification, selected-band, Songs Sort, and Songs Filter
  implementations are absent from the initial graph.
- DnD Kit loads with Sort/path interaction rather than through
  `SettingsContext`; RankBy/KaTeX remains route-deferred.

**Work**

1. Lazy-load search/profile/notification/band-filter modal implementations.
2. Keep only small launch buttons in the initial shell.
3. Evaluate whether DnD Kit is needed before sort interaction.

**Acceptance**

- Initial JS is reduced `12.95%` gzip with all control-ready p95 values below
  `55 ms`, focus/keyboard/reopen behavior preserved, and stale old tabs
  recovering to the deployed entry.

### WEB-3.3 - Fix Manual asset waterfall

**Decision:** Accepted and deployed on 2026-07-25.

**Next dependency:** WEB-4.1 can replace route-specific FAB assembly with a
registry. Manual quick links now avoid loading intermediate sections during
their smooth scroll.

**Evidence**

- Initial image transfer is `133,482` bytes desktop and `72,818` mobile at
  p95, below the `1 MiB` gate.
- Only the visible and adjacent near carousel images load initially. Quick-link
  navigation mounts the target and adjacent near assets without loading the
  intervening Manual.
- Responsive content-addressed WebP sources retain canonical PNG fallbacks,
  explicit dimensions, translated alt text, and stale-client URL aliases.

**Work**

1. Lazy-mount near-viewport sections.
2. Use responsive AVIF/WebP sources and explicit dimensions.
3. Deduplicate byte-identical screenshot pairs.

**Acceptance**

- Initial Manual navigation transfers less than `1 MiB` of images, with zero
  browser errors, unchanged CLS, and ready/interaction p95 within the `10%`
  regression gate.

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
