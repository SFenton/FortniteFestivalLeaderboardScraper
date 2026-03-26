# Plan: Web App Layout & UX Architecture Refactor

## TL;DR

Refactor the FortniteFestivalWeb app to eliminate per-page layout duplication by consolidating scroll management, stagger animations, frosted-glass styling, header alignment, and the page shell into shared, composable primitives. Move the scroll container from per-page `overflow-y: auto` divs to the browser's native scrollbar (with sticky sidebar on wide desktop), unify all pages through the `<Page>` shell, and extract reusable base components for frosted rows/cards and section containers. The visual result is identical to today, but the architecture is centralized, consistent, and resistant to per-page drift.

---

## Phase 1: Scroll Model — Move to Browser-Native Scroll

**Goal**: Eliminate per-page scroll containers. The browser scrollbar controls all scrolling. Sidebar remains sticky on wide desktop. Content is scrollable by hovering anywhere in the viewport (except modals).

### Steps

1. **Remove `overflow: hidden` from `.shell` in App.module.css** and remove `overflow-y: auto` + `overscroll-behavior` from `.content`. Let the shell flow naturally with `min-height: 100dvh` instead of `height: 100dvh`.

2. **Make PinnedSidebar `position: sticky; top: 0; height: 100dvh`** — it stays fixed while browser scroll moves the main content. The sidebar already uses `position: sticky` but currently inside a flex container that doesn't scroll. After step 1, its parent scrolls, so sticky works natively.

3. **Remove `overflow-y: auto` from `shared.module.css .scrollContainer`** — replace with `min-height: 0` (flex shrink helper). The Page shell's `scrollArea` class stops being a scroll container and becomes a flow wrapper.

4. **Update `<Page>` component**: Remove `useScrollMask` and `useStaggerRush` from the scroll container's onScroll. Instead, attach scroll listeners to `window` (since the browser is now the scroller). Provide `scrollRef` pointing to the window for hooks that need scroll position.

5. **Adapt `useScrollRestore`**: Change from reading `el.scrollTop` to reading `window.scrollY`. Save/restore via `window.scrollTo()`.

6. **Adapt `useHeaderCollapse`**: Listen to `window` scroll instead of a ref'd div.

7. **Adapt `useScrollMask`**: If scroll-mask fade is still desired on the content area, use `position: sticky` top/bottom fade overlays instead of mask-image on the scroll container. Alternatively, use `IntersectionObserver` on sentinel elements at the top/bottom of content to toggle fade classes.

8. **Adapt `useStaggerRush`**: Listen to `window` scroll instead of container ref scroll. Rush logic remains the same — collapse pending `fadeInUp` animations on first user scroll.

9. **Update `ScrollToTop` utility in App.tsx**: Change from `document.getElementById('main-content').scrollTo(0, 0)` to `window.scrollTo(0, 0)` on PUSH navigation.

10. **Keep modals as `position: fixed` with their own internal scroll** — modals already use `overflow-y: auto` inside `position: fixed` overlays, unaffected by this change.

**Relevant files:**
- `FortniteFestivalWeb/src/App.module.css` — remove shell overflow clamp
- `FortniteFestivalWeb/src/App.tsx` — update ScrollToTop
- `FortniteFestivalWeb/src/styles/shared.module.css` — update `.scrollContainer`
- `FortniteFestivalWeb/src/pages/Page.tsx` / `Page.module.css` — remove scroll container role
- `FortniteFestivalWeb/src/hooks/ui/useScrollRestore.ts` — switch to window.scrollY
- `FortniteFestivalWeb/src/hooks/ui/useHeaderCollapse.ts` — switch to window scroll
- `FortniteFestivalWeb/src/hooks/ui/useScrollMask.ts` — switch to sentinel/observer approach
- `FortniteFestivalWeb/src/hooks/ui/useStaggerRush.ts` — switch to window scroll
- `FortniteFestivalWeb/src/components/shell/desktop/PinnedSidebar.module.css` — ensure sticky + 100dvh

**Verification:**
- On wide desktop, scrolling anywhere (sidebar area, content area, right spacer) scrolls the page content. Sidebar stays fixed.
- On mobile, scroll behavior unchanged (no sidebar).
- Modals/popups still have their own internal scroll and don't scroll the page behind them.
- All hooks (scroll restore, header collapse, stagger rush) respond to browser scroll events.

---

## Phase 2: Universal Page Shell — All Pages Through `<Page>`

**Goal**: Every page renders through the `<Page>` shell. No page implements its own `.page { composes: pageShell }` + `.scrollArea { composes: scrollContainer }` + `.container { composes: pageContent }` triple. This eliminates 11 duplicate page shell declarations.

### Steps

1. **Migrate LeaderboardPage** to `<Page>`:
   - Move `SongInfoHeader` to `<Page before={...}>` slot
   - Move the player footer to `<Page after={...}>` slot
   - Remove its custom `.page`, `.scrollArea`, `.container` CSS

2. **Migrate PlayerHistoryPage** to `<Page>`:
   - Same pattern: header into `before`, custom shell CSS removed

3. **Migrate ShopPage** to `<Page>`:
   - Toolbar goes into `<Page before={...}>`
   - Grid/list content goes into children
   - Remove custom `.page`, `.scrollArea` CSS
   - `<Page>` provides a `containerClassName` prop for the wider grid max-width

4. **Migrate SongDetailPage** to `<Page>`:
   - Background image via `<Page variant="withBg">` (already supported)
   - Sticky header via `<Page before={...}>`
   - Remove custom shell CSS

5. **Migrate all Rivals pages** (RivalsPage, RivalDetailPage, RivalryPage, AllRivalsPage):
   - Sticky header via `<Page before={...}>`
   - Remove custom `.page`, `.scrollArea`, `.container` + spinner overlay CSS from each

6. **Migrate SettingsPage** to `<Page>`:
   - Minimal changes — already close to using the shell pattern

7. **Migrate SuggestionsPage** to `<Page>`:
   - Header/filter row via `<Page before={...}>`
   - InfiniteScroll inside children

8. **Migrate SongsPage and PlayerPage** (they're closest to Page already):
   - Ensure they fully delegate to `<Page>` with no custom shell CSS remaining

9. **Extend `<Page>` props if needed**:
   - `stickyBefore?: boolean` — makes the `before` slot `position: sticky; top: 0` (for header bars)
   - `pageBackground?: string` — renders the album-art `BackgroundImage` automatically
   - `fabPadding?: boolean` — adds the 80px FAB spacer at the bottom

10. **Delete all per-page shell CSS** (`.page`, `.scrollArea`, `.container` declarations that duplicate shared.module.css patterns) from: `SongsPage.module.css`, `SuggestionsPage.module.css`, `RivalsPage.module.css`, `RivalDetailPage.module.css`, `RivalCategoryPage.module.css`, `LeaderboardPage.module.css`, `PlayerHistoryPage.module.css`, `ShopPage.module.css`, `SongDetailPage.module.css`, `SettingsPage.module.css`, `PlayerPage.module.css`.

**Relevant files:**
- `FortniteFestivalWeb/src/pages/Page.tsx` — extend with new props
- `FortniteFestivalWeb/src/pages/Page.module.css` — add sticky-before, fab-spacer variants
- All 11 page files listed above — refactor to use `<Page>`
- All 11 page CSS modules — delete shell boilerplate

**Verification:**
- Every page renders identically to before (visual regression: screenshot comparison or Playwright snapshots)
- `grep -r "composes: pageShell" src/pages/` returns zero matches (only Page.module.css should have it)
- `grep -r "composes: scrollContainer" src/pages/` returns zero matches

---

## Phase 3: Stagger & Animation Consolidation

**Goal**: Eliminate per-page module-level render flags (`_hasRendered`, `_rivalsHasRendered`, etc.), module-level data caches, and duplicated stagger/spinner logic. Replace with a unified system that handles: first-visit stagger, return-visit quick fade, and scroll-position restore.

### Steps

1. **Create `usePageTransition` hook** — consolidates `useLoadPhase`, skip-animation logic, and the module-level `_hasRendered` pattern:
   ```
   usePageTransition(cacheKey: string, isReady: boolean) →
     { phase, shouldStagger, fadeStyle, clearAnim }
   ```
   - Internally tracks a module-level `Set<string>` of visited cache keys
   - On first visit (key not in set): full stagger (`shouldStagger = true`)
   - On return visit (POP + key in set): quick 200ms fade (`shouldStagger = false`, `fadeStyle` = quick opacity transition), scroll position restored
   - On PUSH to same key: treat as first visit (re-stagger)
   - Provides `clearAnim` callback for `onAnimationEnd`
   - Internally calls `useLoadPhase` with `skipAnimation` derived from the visited set

2. **Create `useStagger` hook** — consolidates the `staggerIdx` counter pattern used in 6+ pages:
   ```
   useStagger(shouldStagger: boolean, interval?: number) →
     { next(): CSSProperties | undefined, forIndex(i: number): CSSProperties | undefined, clearAnim }
   ```
   - Encapsulates the `opacity: 0; animation: fadeInUp ...` style generation
   - `next()` auto-increments the delay counter (replaces the mutable `let staggerIdx = 0` pattern)
   - `forIndex(i)` computes delay from a given index (for virtualized lists)
   - `clearAnim` is the shared `onAnimationEnd` handler

3. **Move module-level caches to a shared `PageCacheRegistry`**:
   - Currently: `_cachedInstrumentRivals`, `_cachedDetailSongs`, `_cachedRivalrySongs`, `_cachedAllRivalsKey`, `leaderboardCache`, `songDetailCache` — 6 separate module-level `let` variables
   - Create `src/api/pageCacheRegistry.ts` with a typed `Map<string, unknown>` + TTL support
   - Each page stores its data under a key namespace (e.g., `rivals:${accountId}`, `rivalDetail:${key}`)
   - `usePageTransition` checks this registry for cache existence
   - Clearing works the same way (on setting change, invalidate all)

4. **Replace per-page spinner overlays** with a `<Page>` prop:
   - `<Page loading={phase !== 'contentIn'} spinnerFading={phase === 'spinnerOut'}>`
   - Page renders the `ArcSpinner` + fadeOut overlay internally
   - Remove the duplicated `.spinnerOverlay` CSS from all 8 page modules

5. **Replace per-page `FadeIn` + quick-fade with Page shell**:
   - On return-visit, `<Page>` wraps children in a single `<div>` with `opacity: 0 → 1` transition (200ms), then removes the wrapper
   - Pages don't need to individually manage fade states

6. **Delete module-level flags**: Remove all `let _hasRendered`, `let _cachedXxx` from: `RivalsPage.tsx`, `RivalDetailPage.tsx`, `RivalryPage.tsx`, `AllRivalsPage.tsx`, `SettingsPage.tsx`, `SongDetailPage.tsx`, `LeaderboardPage.tsx`.

**Relevant files:**
- New: `FortniteFestivalWeb/src/hooks/ui/usePageTransition.ts`
- New: `FortniteFestivalWeb/src/hooks/ui/useStagger.ts`
- New: `FortniteFestivalWeb/src/api/pageCacheRegistry.ts`
- `FortniteFestivalWeb/src/pages/Page.tsx` — add loading/spinner props
- All 11 page TSX files — replace manual stagger + cache logic
- `FortniteFestivalWeb/src/hooks/data/useLoadPhase.ts` — retained, called internally by usePageTransition

**Verification:**
- First visit to any page: spinner → stagger animation (identical to today)
- Return to page (back-nav): instant 200ms fade, scroll position restored, no re-stagger
- `grep -r "_hasRendered\|_cached" src/pages/` returns zero matches
- `grep -r "spinnerOverlay" src/pages/**/*.module.css` returns zero matches

---

## Phase 4: Base Components — FrostedCard, SectionCard, PageHeader

**Goal**: Extract shared visual primitives so pages compose from them instead of re-declaring frosted glass styles, section containers, and page headers independently.

### Steps

1. **Create `<FrostedCard>` component** (`src/components/common/FrostedCard.tsx`):
   - Renders a `<div>` with the frosted glass surface (composes `frostedCard` from shared.module.css)
   - Props: `variant?: 'standard' | 'light'`, `radius?: keyof typeof Radius`, `className?`, `style?`, `as?: ElementType`
   - Applies `border-radius: var(--radius-md)` by default
   - **Does NOT include layout** (padding, gap, flex) — purely visual surface

2. **Create `<SectionCard>` component** (`src/components/common/SectionCard.tsx`):
   - Extends `FrostedCard` with layout: padding, optional header slot, optional clickable header with chevron
   - Props: `title?`, `description?`, `onClick?`, `headerRight?`, `children`
   - Replaces the pattern in: RivalsPage (`.card` + `.cardHeader` + `.cardHeaderClickable`), RivalDetailPage (same), SuggestionsPage (`CategoryCard`'s outer card), SettingsPage (`.card`)
   - The header-with-chevron-and-"See All" pattern used in rivals becomes a render prop

3. **Create `<PageHeader>` component** (`src/components/common/PageHeader.tsx`):
   - Props: `title`, `subtitle?`, `sticky?: boolean`, `collapsed?: boolean`, `children?` (for right-side actions like filter pills)
   - Applies `max-width: var(--max-width-card); margin: 0 auto; padding: 0 var(--layout-padding-h)` — guaranteed alignment with content below
   - When `sticky`, renders with `position: sticky; top: 0; z-index: 10`
   - Transition: `padding` animates on `collapsed` change (matching current SongDetail behavior)
   - Replaces: RivalsPage `.stickyHeader`, RivalDetailPage `.stickyHeader`, RivalCategoryPage `.stickyHeader`, PlayerPage `.playerNameBar`, SongsPage toolbar header, SuggestionsPage header row

4. **Create `<ItemList>` component** (`src/components/common/ItemList.tsx`):
   - A `<div>` with `display: flex; flex-direction: column; gap: 2px` + optional `container-type: inline-size`
   - Replaces: `.rivalList`, `.songList`, `.list` from RivalsPage, RivalDetailPage, RivalCategoryPage, LeaderboardPage
   - Trivial but eliminates drift in gap values across pages

5. **Refactor per-page row components to compose from `<FrostedCard>`**:
   - `SongRow` — wrap content in `<FrostedCard as={Link}>` instead of composing CSS
   - `RivalRow` — wrap in `<FrostedCard>`, add gradient overlay via `::after` pseudo (or a `<FrostedCard overlay="winning">` variant)
   - `RivalSongRow` — same
   - `LeaderboardEntry` — same
   - `CategoryCard` — use `<SectionCard>` for outer shell, keep inner song rows
   - `ShopCard` — use `<FrostedCard variant="light">` for the grid card surface

6. **Extract empty-state component** (`src/components/common/EmptyState.tsx`):
   - Props: `title`, `subtitle?`, `icon?`
   - Replaces the `.emptyState` / `.emptyTitle` / `.emptySubtitle` pattern duplicated in 6+ pages

**Relevant files:**
- New: `FortniteFestivalWeb/src/components/common/FrostedCard.tsx` + `.module.css`
- New: `FortniteFestivalWeb/src/components/common/SectionCard.tsx` + `.module.css`
- New: `FortniteFestivalWeb/src/components/common/PageHeader.tsx` + `.module.css`
- New: `FortniteFestivalWeb/src/components/common/ItemList.tsx`
- New: `FortniteFestivalWeb/src/components/common/EmptyState.tsx` + `.module.css`
- Existing row components: `SongRow.tsx`, `RivalRow.tsx`, `RivalSongRow.tsx`, `LeaderboardEntry.tsx`, `ShopCard.tsx`
- Existing cards: `CategoryCard.tsx`, `InstrumentCard.tsx`
- All page CSS modules — delete duplicated card/header/list/empty CSS

**Verification:**
- Visual regression: all pages look identical
- `FrostedCard` used in every row/card component (search: `<FrostedCard`)
- `PageHeader` used by every page that has a top-level header/toolbar
- No page CSS module defines its own `.card { composes: frostedCard }` — only `FrostedCard.module.css` does

---

## Phase 5: Header Alignment — Content Alignment with Sidebar

**Goal**: Every page's top-level content (title, search bar, toolbar) left-aligns with the sidebar's navigation items in wide desktop mode.

### Steps

1. **Establish alignment contract in `<Page>`**:
   - The `<Page>` container applies `max-width: var(--max-width-card); margin: 0 auto; padding: 0 var(--layout-padding-h)` uniformly
   - The `<Page before={...}>` slot applies the SAME padding and max-width
   - This guarantees `before` content (headers) aligns with `children` content

2. **Ensure `<PageHeader>` uses the alignment contract**:
   - `PageHeader` **must not** set its own `padding` or `max-width` — it inherits from `<Page>`'s `before` slot container
   - Exception: when `sticky`, it renders its own wrapper with the same `max-width + padding-h` values

3. **Audit each page's header/top element for alignment** — verify against sidebar:
   - SongsPage: SongsToolbar (search bar) — should align. Move to `<Page before={}>`
   - SuggestionsPage: Filter action pill — should align. Move to `<Page before={}>`
   - PlayerPage: Player name bar — currently sets its own `padding: var(--gap-lg) var(--layout-padding-h)`. Migrate to `<PageHeader>`
   - RivalsPage: `.stickyHeader` with `.headerContent` using `padding: 0 var(--layout-padding-h)`. Migrate to `<PageHeader>`
   - RivalDetailPage: Same pattern as RivalsPage. Migrate to `<PageHeader>`
   - RivalCategoryPage: Same. Migrate to `<PageHeader>`
   - SettingsPage: Inline heading. Wrap in `<PageHeader>`
   - ShopPage: Title + toggle toolbar. Move to `<Page before={}>`
   - LeaderboardPage: `SongInfoHeader` — already managed separately. Ensure it gets the alignment padding
   - SongDetailPage: `SongDetailHeader` — same

4. **Set `--layout-padding-h-pinned`** value to match sidebar content padding so the left edge of page content aligns precisely with the left edge of sidebar links.

5. **Remove per-page `max-width`/`margin: 0 auto` declarations** — these now come from `<Page>`.

**Relevant files:**
- `FortniteFestivalWeb/src/pages/Page.tsx` — enforce alignment in `before` slot
- `FortniteFestivalWeb/src/pages/Page.module.css` — add `.beforeSlot` with consistent alignment
- `FortniteFestivalWeb/src/styles/theme.css` — verify `--layout-padding-h-pinned` alignment
- All 11 page TSX files — move headers into `<Page before={}>`
- `FortniteFestivalWeb/src/components/common/PageHeader.tsx`
- `FortniteFestivalWeb/src/components/songs/headers/SongInfoHeader.tsx` — ensure alignment values

**Verification:**
- In wide desktop mode, visually inspect that the left edge of: Songs search bar, Suggestions filter, Statistics player name, Rivals title, Settings heading, Shop title, SongDetail song title, Leaderboard header — all share the same x-coordinate
- That x-coordinate matches the left edge of "Songs" in the sidebar
- On mobile, alignment is unaffected (full-width padding)

---

## Phase 6: Performance — Eliminate Moderate-to-Severe Gaps

### Steps

1. **Remove `will-change: scroll-position`** from SongsPage `.scrollArea` — this forces GPU compositing on the entire scroll container, consuming significant memory. With browser-native scroll, this is unnecessary.

2. **Reduce `.frostedCard` SVG noise paint cost**: The inline SVG `data:` URL for fractal noise is re-decoded and rasterized by every element that composes `frostedCard`. On pages with 50+ rows (Songs, Leaderboard), this is expensive.
   - Option A: Cache the noise texture as a single CSS `background-image` on the page container and use `background-attachment: fixed` — all children share one decoded image
   - Option B (recommended): Move the noise to a single `::before` pseudo-element on the `<Page>` container with `position: fixed; pointer-events: none` — one composite layer for the entire page. Individual `FrostedCard` elements only set `background-color` + `border` + `box-shadow`.

3. **Consolidate `albumArtMap` and `yearMap` creation**: `RivalDetailPage` and `RivalryPage` both build identical `Map<songId, albumArt>` and `Map<songId, year>` from the festival context. Extract to a shared hook (`useAlbumArtMap`, `useYearMap`) or add as derived state on `FestivalContext`.

4. **Replace module-level caches with `@tanstack/react-query`**:
   - The current module-level `_cachedXxx` variables for rivals, leaderboard, songDetail duplicate what React Query already provides
   - Convert `api.getRivalsList()`, `api.getRivalDetail()`, `api.getLeaderboard()` to `useQuery()` with `staleTime: Infinity` (or appropriate TTL)
   - React Query's `gcTime` handles cache eviction; `useQuery` provides `isLoading`/`data` directly
   - The `pageCache.ts` LRU cache can be retired in favor of React Query's built-in cache
   - This eliminates ~150 lines of manual cache management across rivals pages alone

5. **Audit InfiniteScroll in SuggestionsPage**: The `react-infinite-scroll-component` library is unmaintained (last update 2022). With browser-native scroll, consider replacing with a `useInfiniteQuery` + `IntersectionObserver` sentinel pattern (no library needed).

**Relevant files:**
- `FortniteFestivalWeb/src/pages/songs/SongsPage.module.css` — remove `will-change`
- `FortniteFestivalWeb/src/styles/shared.module.css` — modify `.frostedCard` noise strategy
- `FortniteFestivalWeb/src/pages/Page.tsx` — add noise background layer
- `FortniteFestivalWeb/src/pages/rivals/RivalDetailPage.tsx`, `RivalryPage.tsx` — extract albumArtMap
- `FortniteFestivalWeb/src/api/pageCache.ts` — migrate to React Query
- `FortniteFestivalWeb/src/pages/rivals/*.tsx` — convert to `useQuery()`
- `FortniteFestivalWeb/src/pages/leaderboard/global/LeaderboardPage.tsx` — convert to `useQuery()`
- `FortniteFestivalWeb/src/pages/songinfo/SongDetailPage.tsx` — convert to `useQuery()`

**Verification:**
- Chrome DevTools Performance tab: paint time per frame on Songs page (50+ rows) should decrease
- Memory usage: GPU memory (chrome://gpu) should decrease with noise optimization
- React Query DevTools shows all page data in the query cache
- No `let _cached` variables remain in page files

---

## Decisions

- **Scroll model**: Browser-native scrollbar. Sidebar remains `position: sticky`. No per-page scroll containers.
- **Return-visit behavior**: Quick 200ms opacity fade-in (no per-row stagger), scroll position restored.
- **Page shell**: ALL pages go through `<Page>`. No exceptions.
- **Frosted glass noise**: Move to single page-level composite layer (Option B).
- **Data caching migration**: Move to React Query incrementally (rivals first, then leaderboard/songDetail).
- **Excluded from scope**: Mobile navigation changes (BottomNav, FAB), FirstRun carousel system, modal architecture, React Native app.

## Further Considerations

1. **Playwright visual regression suite**: Before starting, capture baseline screenshots of every page in desktop and mobile viewports. Use these as the regression oracle throughout. If E2E tests already exist with snapshots, extend them; otherwise create a minimal set.

2. **Phase ordering**: Phases 1-2 are sequential (Phase 2 depends on Phase 1's scroll model). Phases 3-5 can proceed in parallel after Phase 2. Phase 6 can proceed independently at any time.

3. **`useScrollFade` (per-child mask)**: SuggestionsPage uses `useScrollFade` instead of `useScrollMask` to preserve `backdrop-filter` on cards. With browser-native scroll, this hook needs evaluation — it may need to switch to `IntersectionObserver`-based class toggling. Flag this for Phase 1 evaluation.

---

## Phase 7: Testability — Eliminate All `v8 ignore` Blocks

**Goal**: Remove every `/* v8 ignore */` directive from the codebase (~200 markers across ~40 files). After this, agents are prohibited from inserting new `v8 ignore` blocks.

### Root Cause Analysis

| Root Cause | % of Blocks | Solution |
|---|---|---|
| jsdom lacks DOM APIs (scrollTop, rAF, ResizeObserver, touch, VisualViewport, Web Animations, image onLoad) | ~60% | Extract DOM calls into thin adapter functions; mock in unit tests; real DOM exercised by Playwright |
| Code too deeply nested in render (App.tsx alone: ~250 ignored lines) | ~20% | Decompose into smaller, independently testable components |
| Bundler/framework magic (lazy(), Vite defines) | ~2% | Provide test-time replacements in setup.ts |
| Defensive guards (impossible null checks) | ~5% | Remove guards or restructure types to eliminate impossible states |
| Tests not written yet (async callbacks, polling, WebSocket) | ~13% | Write the missing tests — the code is already mockable |

### Steps

**7.1 — Extract DOM Side Effects Into Thin Adapters**

Create `src/utils/domAdapters.ts` with one-liner functions for all DOM operations that jsdom can't handle. Hooks import these instead of calling DOM APIs directly. Tests mock the module; Playwright runs the real thing.

Functions to extract:
- `getScrollTop(el)` / `setScrollTop(el, v)` — used by useScrollRestore, useHeaderCollapse, useScrollMask
- `raf(cb)` / `cancelRaf(id)` — used by FirstRunCarousel (13 blocks), Sidebar, AnimatedBackground, CategoryCardDemo, InfiniteScrollDemo
- `getComputedStyleProp(el, prop)` — used by useStaggerRush
- `getBoundingClientRect(el)` — used by Sidebar, useFirstRun
- `observeResize(el, cb)` — used by FirstRunCarousel, useChartDimensions, InstrumentFilterDemo
- `imageOnLoad(img, cb)` — used by AlbumArt, BackgroundImage
- `touchStart/touchMove/touchEnd` — used by FirstRunCarousel
- `webAnimate(el, keyframes, opts)` — used by AnimatedBackground
- `isPWA()` — used by FloatingActionButton, BottomNav (replaces static IS_PWA import)

**Files to refactor:** useScrollRestore.ts, useHeaderCollapse.ts, useScrollMask.ts, useStaggerRush.ts, Sidebar.tsx, FirstRunCarousel.tsx (13 blocks), AnimatedBackground.tsx, FloatingActionButton.tsx, BottomNav.tsx, useVisualViewport.ts, AlbumArt.tsx, BackgroundImage.tsx, useChartDimensions.ts, ConfirmAlert.tsx, InstrumentFilterDemo.tsx, CategoryCardDemo.tsx, InfiniteScrollDemo.tsx, useFirstRun.ts

**7.2 — Decompose App.tsx**

App.tsx has 18 `v8 ignore` blocks (~250 lines). `AppShell` is a 300-line monolith. Extract:
- `DesktopLayout.tsx` — wide-desktop shell (sidebar + content + spacer)
- `MobileLayout.tsx` — mobile shell (header + content + bottom nav)
- Route-specific FAB config already partially extracted (MobileFabController) but ~138 lines remain inline in App.tsx — move to `RouteFabConfig.tsx`
- `ScrollToTop.tsx` — extract from inline function

After extraction, AppShell becomes a ~50-line orchestrator. Each extracted component is small enough to test directly.

**7.3 — Cover Bundler/Framework Blocks**

- `lazy()` imports: Define test-time replacements via `vi.mock()` that resolve synchronously, or add a test that validates each lazy import path
- `__APP_VERSION__` / `__BUILD_TIME__`: Already defined in vite.config.ts test section — just need to be present

**7.4 — Remove Defensive Guards**

8 blocks guard impossible states. For each: either delete the guard (let TypeScript prevent the state), or write a test that triggers it.
- `SearchQueryContext.tsx` — unreachable default factory: delete, TypeScript enforces provider usage
- `AnimatedBackground.tsx` — `imageUris.length > 0` after filtering: use non-empty type assertion
- `InstrumentFilterDemo.tsx` (4 guards) — `instruments` always has entries: use TypeScript NonEmptyArray or `instruments[0]!`
- `ErrorBoundary.tsx` — class component getDerivedStateFromError: test with a component that throws

**7.5 — Write Missing Tests for Async Code**

These blocks are mockable today; they just lack tests:
- `useSyncStatus.ts` (lines 49-120): Mock `api.getSyncStatus`, test polling loop with fake timers
- `useShopWebSocket.ts` (lines 16-122): Mock WebSocket constructor, test message handling + reconnect
- `PlayerDataContext.tsx` (lines 47-69): Extend existing tests to cover sync invalidation
- `RivalsPage.tsx` (lines 65-129): Extend to cover all 3 fetch effect blocks
- `useDemoSongs.ts` (lines 64-151): Provide mock contexts, exercise timer-based rotation
- `useFilteredSongs.ts` (lines 106-153): Test percentile cap + sort tiebreaker chain
- `SuggestionsPage.tsx` (lines 50-373): Remove the v8 ignore wrapping the entire component — existing 42 integration tests cover it
- `SettingsPage.tsx` (LeewaySlider, DnD, version fetch): Write targeted tests for each

**7.6 — Add CI Ban on `v8 ignore`**

Add to CI pipeline:
```bash
if grep -rn "v8 ignore" FortniteFestivalWeb/src/; then echo "FAIL: v8 ignore found"; exit 1; fi
```

Or add ESLint `no-restricted-syntax` rule:
```js
'no-restricted-syntax': ['error', {
  selector: ':matches(Line, Block):has([value*="v8 ignore"])',
  message: 'v8 ignore directives are prohibited.'
}]
```

**Verification:**
- `grep -rn "v8 ignore" FortniteFestivalWeb/src/` → zero matches
- Coverage stays at 95% per-file threshold with NO v8 ignore blocks
- CI enforces the ban

---

## Phase 8: Playwright E2E Integration Test Suite

**Goal**: Comprehensive Playwright tests serving as integration tests, achieving maximum code coverage by exercising the real browser DOM, animations, scroll, rAF — everything jsdom cannot.

### 8.0 — Infrastructure Setup

**playwright.config.ts:**
- `baseURL`: `http://localhost:3000`
- `webServer`: `{ command: 'npm run dev', port: 3000, reuseExistingServer: true }`
- 4 viewport projects: `mobile` (375×667), `wide-mobile` (520×900), `compact-desktop` (1024×768), `wide-desktop` (1600×900)
- `use.video: 'retain-on-failure'`
- `retries: 1`

**Fixtures directory:**
- `e2e/fixtures/mockApi.ts` — `page.route('/api/*', ...)` interceptors returning fixture data for all 14 API endpoints
- `e2e/fixtures/fixtures.ts` — Reusable data (songs, players, leaderboard entries, rivals, shop items)
- `e2e/fixtures/localStorage.ts` — Seed `fst:appSettings`, `fst:firstRun`, `fst:trackedPlayer` before tests
- `e2e/fixtures/helpers.ts` — `waitForContentIn()`, `waitForSpinnerGone()`, `waitForStaggerComplete()` etc.

### Breakpoint Map

| Project | Width | CSS Behavior |
|---|---|---|
| mobile | 375px | `QUERY_MOBILE` matches: bottom nav, FAB, stacked song rows, single-column cards |
| wide-mobile | 520px | `QUERY_SHOW_SEASON` matches: season columns visible; `QUERY_SHOW_ACCURACY` matches at >420px |
| compact-desktop | 1024px | Desktop nav, hamburger sidebar, no pinned sidebar |
| wide-desktop | 1600px | `QUERY_WIDE_DESKTOP` matches: pinned sidebar, right spacer, full-width content centering |

### 8.1 — Landing Page Tests (`e2e/tests/landing.spec.ts`)

All URLs a user can directly navigate to (HashRouter: `/#/...`):

| URL Pattern | Page | What to Assert |
|---|---|---|
| `/` or `/#/` | Redirect | Redirects to `/#/songs` |
| `/#/songs` | SongsPage | Song list renders, first song visible |
| `/#/songs/:songId` | SongDetailPage | Song header, instrument grid, chart placeholder |
| `/#/songs/:songId/:instrument` | LeaderboardPage | Leaderboard entries, pagination |
| `/#/songs/:songId/:instrument/history` | PlayerHistoryPage | Requires tracked player or redirects |
| `/#/player/:accountId` | PlayerPage | Player name, stats sections |
| `/#/rivals` | RivalsPage | Redirects to /songs if no player; shows rivals if player |
| `/#/rivals/:rivalId` | RivalDetailPage | Category sections render |
| `/#/rivals/:rivalId/rivalry?mode=X` | RivalryPage | Song comparison list |
| `/#/rivals/all?category=X` | AllRivalsPage | Full rival list for category |
| `/#/suggestions` | SuggestionsPage | Redirects if no player; shows categories if player |
| `/#/shop` | ShopPage | Grid renders |
| `/#/settings` | SettingsPage | All sections render |
| `/#/statistics` | PlayerPage | Redirects if no player |

~14 tests

### 8.2 — Navigation Flow Tests (`e2e/tests/navigation.spec.ts`)

- Songs → Song Detail → Leaderboard → Player History drill-down
- Back nav at each level: content in place, scroll restored, no re-stagger
- Tab switching (mobile bottom nav): Songs ↔ Suggestions ↔ Statistics ↔ Settings
- Sidebar navigation (desktop): same tabs
- Selecting player unlocks Rivals/Suggestions/Statistics
- Deselecting player hides them and redirects
- Deep links preserve instrument context and rival state

~16 tests

### 8.3 — Page Interaction Tests (`e2e/tests/interactions.spec.ts`)

Per-page interactive behaviors:
- Songs: search debounce, sort modal, filter modal, virtual scroll, score metadata
- SongDetail: chart interaction, bar select, instrument card click, paths modal, header collapse
- Leaderboard: pagination, player highlight, stagger on first load, fade on return
- Player: instrument cards, top songs, percentile table, stat box navigation
- Rivals: common rivals section, rival row click, category drill-down, song comparison
- Suggestions: infinite scroll, filter modal, category card previews
- Shop: grid/list toggle, pulse animation
- Settings: all toggles (detailed in 8.4)

~34 tests

### 8.4 — Settings Toggle Impact Tests (`e2e/tests/settings-impact.spec.ts`)

Every setting key and its visible effect:
- Instrument toggles (6): hide from song detail grid, player stats, rivals, suggestions
- Metadata toggles (6): show/hide score pill, percentile, accuracy, etc. on song rows
- Filter invalid scores + leeway slider: affects leaderboard, triggers cache invalidation
- hideItemShop: removes from nav; direct URL still works
- disableShopHighlighting: removes pulse animation
- songRowVisualOrder: column reordering on song rows

~14 tests

### 8.5 — First Run Experience Tests (`e2e/tests/first-run.spec.ts`)

FRE page keys: `songs` (7 slides), `songinfo` (6 slides), `playerhistory` (slides), `statistics` (5 slides), `suggestions` (4 slides)

- Fresh install: carousel shows on each page's first visit
- Dismissal persists to localStorage
- Return visit: no carousel
- Gate conditions: shopHighlight slide gated, paths slide differs mobile/desktop, statistics gated on player
- Interaction: swipe, keyboard arrows, dot indicators, dismiss animation
- Settings replay: "Show First Run" replays per-page, "Reset All" replays all
- Suppresses changelog modal while active
- Sequential ordering: dismiss songs FRE → navigate to song detail → song detail FRE shows
- Mid-session player selection triggers statistics FRE

~17 tests

### 8.6 — Responsive Layout Tests (`e2e/tests/responsive.spec.ts`)

Run across all 4 viewport projects (parameterized or one test per project):
- Mobile: bottom nav, FAB, mobile header, stacked rows, single-column cards
- Wide mobile: accuracy column visible, season column visible
- Compact desktop: desktop nav, hamburger sidebar, no pinned sidebar
- Wide desktop: pinned sidebar, right spacer, content centering, browser scroll works everywhere, header alignment

~17 tests

### 8.7 — Resize During Operation Tests (`e2e/tests/resize.spec.ts`)

- Desktop → mobile: bottom nav appears, sidebar disappears
- Mobile → desktop: reverse
- Resize while on song detail: grid reflows
- Resize while leaderboard visible: columns adjust
- Resize while modal open: modal stays centered
- Resize while FRE carousel open: carousel adjusts
- Resize while shop in grid view: column count changes

~7 tests

### 8.8 — Scroll & Animation Tests (`e2e/tests/scroll-animation.spec.ts`)

- Scroll position restored on back-nav
- Scroll cleared on PUSH nav
- Header collapse on scroll threshold
- Stagger rush: scrolling during stagger completes all items
- First visit stagger vs return visit fade
- Animated background Ken Burns effect visible

~8 tests

### 8.9 — Changelog Tests (`e2e/tests/changelog.spec.ts`)

- Appears on first visit
- Dismissed persists
- Re-appears on version change
- Suppressed by active FRE

~4 tests

### Directory Structure

```
FortniteFestivalWeb/
  playwright.config.ts
  e2e/
    fixtures/
      mockApi.ts
      fixtures.ts
      localStorage.ts
      helpers.ts
    tests/
      landing.spec.ts          (14 tests)
      navigation.spec.ts       (16 tests)
      interactions.spec.ts     (34 tests)
      settings-impact.spec.ts  (14 tests)
      first-run.spec.ts        (17 tests)
      responsive.spec.ts       (17 tests)
      resize.spec.ts           (7 tests)
      scroll-animation.spec.ts (8 tests)
      changelog.spec.ts        (4 tests)
```

**Total: ~131 Playwright tests** × 4 viewport projects = ~524 test executions.

### Phase Ordering Update

- Phases 1-2: Sequential (scroll model → page shell)
- Phases 3-5: Parallel after Phase 2 (stagger, base components, alignment)
- Phase 6: Independent (performance)
- **Phase 7**: Can start immediately, parallel with everything. Steps 7.1-7.4 are pure refactoring. Step 7.5 is test-writing. Step 7.6 is CI config.
- **Phase 8**: Depends on Phase 7 (adapters must exist for real DOM coverage to work). Infrastructure (8.0) can start in parallel with Phase 7. Test writing starts after Phases 1-5 are complete (so tests validate the refactored architecture).

### Updated Decisions

- **v8 ignore ban**: Absolute. No exceptions. CI enforces with grep + eslint.
- **Coverage model**: Vitest (jsdom) handles logic/state/rendering. Playwright handles DOM, animations, scroll, responsive, FRE, navigation flows.
- **Playwright scope**: Integration tests, not visual regression. Assert behavior and DOM state, not pixel screenshots.
- **Excluded**: React Native app, FSTService backend tests, PercentileService.

---

## Phase 9: Component Consolidation — Pagination, Modals, Shared Patterns

**Goal**: Unify the 7 distinct pagination/navigation patterns into 2-3 composable primitives, consolidate modal draft-state logic, and eliminate underutilization of existing shared components.

### 9.1 — Unified Pagination Primitives

Currently 7 patterns exist:
1. **FirstRunCarousel**: Dots + arrows + touch/keyboard
2. **LeaderboardPage**: PaginationButton (First/Prev/Next/Last + badge)
3. **ScoreHistoryChart**: 4-button point/offset skipper
4. **InstrumentSelector**: Tab row OR compact arrows
5. **DirectionSelector**: Binary toggle
6. **BottomNav**: Fixed tab bar
7. **Chart animation state machines**: Custom per-chart

**Create `<Paginator>` component** (`src/components/common/Paginator.tsx`):
- Props: `mode: 'dots' | 'arrows' | 'numbered'`, `current`, `total`, `onChange`, `disabled?`, `keyboard?`, `touch?`
- `dots` mode: Renders clickable dot indicators with active highlighting (used by FirstRunCarousel)
- `arrows` mode: Renders left/right arrows with disabled state at boundaries (used by InstrumentSelector compact, chart pagination)
- `numbered` mode: Renders First/Prev/Page N of M/Next/Last (used by LeaderboardPage)
- Keyboard support: optional ArrowLeft/ArrowRight handler
- Touch swipe: optional touch handler with configurable threshold

**Refactor targets:**
- FirstRunCarousel → uses `<Paginator mode="dots" keyboard touch>`
- LeaderboardPage → uses `<Paginator mode="numbered">`
- InstrumentSelector compact → uses `<Paginator mode="arrows">`
- ScoreHistoryChart → keeps custom (too domain-specific), but can use arrow primitives

**Leave alone:** DirectionSelector (binary, not n-ary), BottomNav (tab navigation, not pagination), chart animation state machines (domain-specific).

### 9.2 — Modal Draft State Hook

All 4 modals (SortModal, FilterModal, PlayerScoreSortModal, SuggestionsFilterModal) duplicate identical change-detection + confirmation logic (~25-30 lines each):

**Create `useModalDraft<T>(draft, savedDraft, onCancel)`:**
- Returns `{ hasChanges, confirmOpen, setConfirmOpen, handleClose, confirmDiscard }`
- Encapsulates `JSON.stringify` comparison, confirm-discard flow
- **Saves ~100 lines** across 4 modals

### 9.3 — Adopt Existing Shared Components

Several existing shared components are underutilized:
- **DirectionSelector**: Exists in `common/` but SortModal and PlayerScoreSortModal reimplement it inline (~60 lines each). Refactor both to use the shared component.
- **InstrumentSelector**: Exists but FilterModal and SuggestionsFilterModal reimplement instrument icon pickers. Refactor to use shared.
- **PageMessage / EmptyState**: 3 of 8+ pages that show empty states don't use the shared component.

### 9.4 — StatusRow Component

RivalRow and RivalSongRow share an identical pattern: frostedCard + status tint gradient + semantic button attributes + animation plumbing. Extract to `<StatusRow status="winning|losing|neutral" onClick style onAnimationEnd>`.

### 9.5 — ToggleAll Hook

FilterModal and SuggestionsFilterModal both implement identical "toggle all instruments" logic. Extract to `useToggleAll(map, keys)` → `{ allOn, toggle }`.

**Files created:**
- New: `src/components/common/Paginator.tsx` + `.module.css`
- New: `src/hooks/ui/useModalDraft.ts`
- New: `src/hooks/ui/useToggleAll.ts`
- New: `src/components/common/StatusRow.tsx` + `.module.css`

**Files refactored:**
- `FirstRunCarousel.tsx` — extract pagination to `<Paginator>`
- `LeaderboardPage.tsx` — replace custom pagination controls with `<Paginator>`
- `InstrumentSelector.tsx` — extract compact arrow logic to `<Paginator>`
- `SortModal.tsx`, `PlayerScoreSortModal.tsx` — use `DirectionSelector` + `useModalDraft`
- `FilterModal.tsx`, `SuggestionsFilterModal.tsx` — use `InstrumentSelector` + `useModalDraft` + `useToggleAll`
- `RivalRow.tsx`, `RivalSongRow.tsx` — compose from `<StatusRow>`

**Verification:**
- All pagination UI looks identical to today
- Modal confirm-discard flow works the same
- `grep -r "JSON.stringify(draft)" src/pages/` → only in `useModalDraft.ts`

---

## Phase 10: Page Shell — Action Bar Architecture

**Goal**: The `<Page>` component's `before` slot must support a standardized "action bar" pattern for filter/sort/modal-trigger pills, so pages don't independently implement toolbar wiring.

### Current Problem

6 pages implement their own header action patterns:
- SongsPage: SongsToolbar with search + instrument icon + sort/filter pills, complex fade animations
- SuggestionsPage: Single ActionPill for filter
- PlayerHistoryPage: Sort pill (via SongInfoHeader.actions slot)
- ShopPage: Title + count + grid/list toggle
- SongDetailPage: Paths button (conditional)
- LeaderboardPage: No actions, but pagination below the header

Each wires modal state independently, registers FAB actions independently, and handles responsive visibility independently.

### Solution: `<PageActionBar>` Component

**Create `<PageActionBar>` (`src/components/common/PageActionBar.tsx`):**
- Renders inside `<Page before={...}>` with correct alignment
- Props: `left?: ReactNode`, `right?: ReactNode`, `search?: { value, onChange, placeholder }`, `sticky?: boolean`
- Internally renders left/right action slots with consistent gap/alignment
- Pages pass `<ActionPill>`s, toggles, or custom content as children

**Refactor targets:**
- SongsPage: `<PageActionBar search={...} right={<><ActionPill sort /><ActionPill filter /></>}>`
- SuggestionsPage: `<PageActionBar right={<ActionPill filter />}>`
- ShopPage: `<PageActionBar left={<Title />} right={<ViewToggle />}>`
- PlayerHistoryPage: `<PageActionBar right={<ActionPill sort />}>`

### FAB Registration Consolidation

Currently each page independently calls `fabSearch.registerXxxActions(...)`. Instead, `<PageActionBar>` can auto-register its action buttons with the FAB system by accepting a `fabActions` prop that maps action buttons to FAB entries.

**Verification:**
- All toolbar layouts look identical
- FAB still triggers the same modals on mobile
- Desktop pills and mobile FAB stay in sync

---

## Phase 11: FSTService Architecture Improvements

### 11.1 — Extract Shared Auth to Core (HIGH PRIORITY)

**Problem**: PercentileService and FSTService both independently implement ~300 lines of identical Epic OAuth (device-code flow, token refresh, credential storage).

**Solution**: Move to `FortniteFestival.Core/Auth/`:
- `EpicDeviceAuth.cs` — device-code grant flow, token exchange
- `EpicTokenRefresh.cs` — access token refresh
- `IDeviceAuthStore.cs` — interface for credential persistence
- `FileDeviceAuthStore.cs` — file-based implementation (shared)

Both services consume Core auth. PercentileService's `EpicTokenManager` and FSTService's `EpicAuthService` both replaced.

**Impact**: ~300 lines of duplicated C# eliminated, single source of truth for OAuth changes.

### 11.2 — Decompose ScraperWorker (CRITICAL)

**Problem**: 1,020-line monolithic god class with 22 injected dependencies orchestrating 11 sequential phases.

**Solution**: Split into domain-specific orchestrators with a thin coordinator:

- `ScrapeOrchestrator.cs` — phases 1-4 (auth, catalog sync, path gen, global scrape)
- `EnrichmentOrchestrator.cs` — phases 5-8 (FirstSeenSeason, name resolution, personal DB, post-scrape refresh)
- `BackfillOrchestrator.cs` — phases 9-10 (backfill, history reconstruction)
- `ScraperCoordinator.cs` — thin ~100-line coordinator that sequences orchestrators

Each orchestrator has 3-5 dependencies (vs 22), is independently testable, and has clear input/output contracts.

### 11.3 — Extract ApiEndpoints Into Feature Modules

**Problem**: 1,440-line static method mapping ~50 endpoints.

**Solution**: Split into feature endpoint classes:
- `SongEndpoints.cs` — `/api/songs`, `/api/leaderboard/*`
- `PlayerEndpoints.cs` — `/api/player/*`
- `RivalsEndpoints.cs` — `/api/player/*/rivals/*`
- `AdminEndpoints.cs` — `/api/register`, `/api/backfill/*`, `/api/progress`
- `AuthEndpoints.cs` — `/api/auth/*` (already partially split)

Register via `app.MapGroup()` pattern.

### 11.4 — Database Performance Improvements

**SQLite quick wins (immediate):**
- Extract PRAGMA setup to `SqlitePragmaHelper.SetupOnce(conn)` — eliminates ~80 lines of duplicate pragma calls across 4 DB classes
- Add missing indexes: `ScoreHistory(ChangedAt)`, `LeaderboardEntries(AccountId, Season, Score)`, reverse `UserRivals(RivalAccountId, UserId)` — 50-100ms improvement per affected query
- Fix double materialization: `accountIds.ToList() → Skip/Take.ToList()` in MetaDatabase (2-3× heap reduction)

**SQLite strategic:**
- Pre-compile high-frequency statements (UPSERT, leaderboard SELECT) — 5-10% latency reduction
- Change `ConcurrentBag` in pipeline aggregation to chunked DB inserts — reduces 20MB memory spike per scrape pass
- Add `BoundedChannelCapacity` to ScraperOptions (currently hardcoded 32, insufficient at DOP 512)

**Data accuracy fixes:**
- Fix Rank column defaulting to 0 (indistinguishable from "rank zero"): use NULL default
- Fix UPSERT overwriting EndTime with NULL when API omits it: use COALESCE to preserve existing
- Rename ambiguous rank columns: `SeasonRank` → `SeasonRankFinal`, `AllTimeRank` → `AllTimeRankAtLookup`

**Storage optimization:**
- Normalize difficulty columns out of score-level tables into song-level: ~480 MB savings at scale
- Add missing foreign key indexes in personal DBs: `IX_Scores_SongId`

### 11.5 — Consolidate HTTP Client Patterns

**Problem**: 4 classes across 2 services manually construct `HttpRequestMessage`, add headers, handle retries.

**Solution**: Extend `ResilientHttpExecutor` or create a shared `EpicHttpClient` in Core:
- Configurable retry/circuit-breaker
- Automatic auth header injection
- Structured error parsing via `HttpErrorHelper`
- Both services consume it

### 11.6 — Deprecate `POST /api/register`

Already superseded by `/api/auth/login` per design docs. Add `Deprecation` response header. Remove after 2 minor versions.

### 11.7 — Remove Diagnostic Code

ScraperWorker backfill-only mode contains a hardcoded song "092c2537" lookup for #1 player verification. Delete or move to a dedicated diagnostic CLI tool.

---

## Phase 12: Dead Code Pruning

### Web App

| Item | File | Action |
|---|---|---|
| Duplicate LoadGate.tsx | `src/components/common/LoadGate.tsx` | **Delete** — broken imports, never used; real one is in `src/components/page/LoadGate.tsx` |
| Unused `.frostedCardLight` | `shared.module.css`, `frostedStyles.ts`, `theme/index.ts` | **Delete from all 3** |
| Unused gold style exports | `goldStyles.ts` (`goldOutlineSkew`), `theme/index.ts` (`goldFill`, `goldOutline`) | **Delete** |
| Coverage exclusion for deleted file | `vite.config.ts` excludes `src/components/common/LoadGate.tsx` | **Remove exclusion** after file deleted |
| SuggestionsPage single v8-ignore wrapping entire component | `SuggestionsPage.tsx` lines 50-373 | **Remove** — 42 existing integration tests already cover it |

### Service

| Item | File | Action |
|---|---|---|
| Diagnostic hardcoded lookup | `ScraperWorker.cs` lines 159-180 | **Delete** |
| Vestigial migration methods | `InstrumentDatabase.cs` `MigrateDropColumn`/`MigrateAddColumn` | **Audit if migrations complete → delete** |
| Nested index drop+recreate | `InstrumentDatabase.cs` lines 76-82 | **Delete** — index already exists |

---

## Phase 13: Performance, Memory & Storage Audit (Cross-cutting)

### Web App Performance

| Issue | Severity | Fix |
|---|---|---|
| `useScrollFade` writes style to EVERY child on EVERY scroll event | **HIGH** | Replace with `IntersectionObserver` on sentinel elements; or CSS `scroll-timeline` if supported |
| Unbounded `pageCache` Maps (songDetail + leaderboard) | **HIGH** | Migrate to React Query (`staleTime + gcTime`) — auto-eviction, deduplication, loading states built in |
| `AnimatedBackground` uses biased shuffle | **MEDIUM** | Replace `.sort(() => Math.random() - 0.5)` with Fisher-Yates |
| `FestivalContext` triggers tree-wide re-renders | **MEDIUM** | Split into `SongsContext` + `SeasonContext`, or use React Query `select` for fine-grained subscriptions |
| `ShopContext` duplicate filtered collections | **LOW** | Deduplicate `shopUrlMap` + `shopSongs` |
| Frosted card SVG noise re-decoded per element | **MEDIUM** | Move to single page-level composite layer (already in Phase 6) |
| `will-change: scroll-position` on SongsPage | **LOW** | Remove — unnecessary GPU compositing |

### Service Performance

| Issue | Severity | Fix |
|---|---|---|
| `ConcurrentBag` unbounded aggregation in pipeline | **HIGH** | Chunked DB inserts, flush in batches |
| N+1 personal DB builds (6 separate instrument queries) | **HIGH** | Batch-load all instruments in single cross-DB query |
| Double materialization in MetaDatabase pagination | **MEDIUM** | Lazy evaluation or single-pass |
| PRAGMA executed per connection (no-op after first) | **LOW** | Cache result in static helper |
| No statement pre-compilation | **MEDIUM** | Pre-compile UPSERT + SELECT hot paths |
| Hardcoded channel capacity (32) vs DOP 512 | **MEDIUM** | Add to ScraperOptions |

### Storage

| Issue | Severity | Fix | Savings |
|---|---|---|---|
| Difficulty columns denormalized into score tables | **HIGH** | Normalize to Songs table only | ~480 MB |
| Missing FK indexes in personal DBs | **MEDIUM** | Add `IX_Scores_SongId` | Query speed |
| Stale percentile never refreshed | **MEDIUM** | TTL-based refresh or nightly recalculation | Accuracy |
| Oversized 4-column composite PK in UserRivals | **LOW** | Consider surrogate key | ~800 MB |

---

## Final Phase Ordering

| Phase | Depends On | Can Parallel With |
|---|---|---|
| 1. Scroll Model | — | 7, 9, 11, 12 |
| 2. Page Shell | 1 | 7, 9, 11, 12 |
| 3. Stagger Consolidation | 2 | 4, 5, 9, 10 |
| 4. Base Components | 2 | 3, 5, 9, 10 |
| 5. Header Alignment | 2 | 3, 4, 9, 10 |
| 6. Web Performance | — | anything |
| **7. Testability** | — | **anything** |
| **8. Playwright** | 7; test writing after 1-5 | 6 |
| **9. Component Consolidation** | — | 1-5, 7 |
| **10. Page Action Bar** | 2 | 3-5, 9 |
| **11. Service Architecture** | — | **anything (fully independent)** |
| **12. Dead Code** | — | **anything** |
| **13. Perf/Memory/Storage** | — | **anything** |

### Final Scope & Decisions

- **Total new web files**: ~15-20 (components, hooks, fixtures)
- **Total web files refactored**: ~60-70
- **Total new Playwright tests**: ~131 specs × 4 viewports
- **Total new vitest tests**: ~15-20
- **FSTService refactor**: Split ScraperWorker (3 orchestrators), split ApiEndpoints (5 modules), extract Core auth
- **Cross-service**: Auth consolidation to Core, HTTP client consolidation
- **Dead code**: 5 web items + 3 service items
- **Performance wins**: useScrollFade fix, pageCache migration, pipeline batching, index additions, difficulty normalization
- **Storage savings**: ~480 MB (difficulty normalization) + ~800 MB (PK optimization)
- **Excluded**: React Native app, MAUI app, full PercentileService merge (deferred)

---

## Phase 14: Documentation Overhaul — Comprehensive Wiki

**Goal**: Replace all stale documentation with an exhaustive, Wikipedia-style documentation system. Multiple dedicated pages per area. Every algorithm, every UX flow, every configuration option, every component documented in depth. Updated as Phases 1-13 are implemented.

### 14.0 — Cleanup: Delete Stale Docs

Delete 4 root-level duplicates: `docs/EpicLoginDesign.md`, `docs/FSTServiceDatabaseDesign.md`, `docs/UserDeviceRegistrationDesign.md`, `docs/UserRegistrationBackfillDesign.md`.

Update all 7 design docs with implementation status headers (`✅ IMPLEMENTED` / `⚠️ DESIGN PHASE` / `🔨 PARTIAL`).

### 14.1 — Root & Index Pages

| Doc | Contents |
|---|---|
| **README.md** (root) | Complete rewrite: project name, status badges, monorepo component table, quick-start commands, architecture diagram (Mermaid), links to all docs |
| **docs/README.md** | Master documentation index — every page listed with category, status badge, last-updated date |

### 14.2 — Architecture (`docs/architecture/`)

| Page | Contents |
|---|---|
| **Overview.md** | Monorepo component diagram (Mermaid), component responsibilities, data flow (Epic API → FSTService → SQLite → Web/RN), deployment topology, shared package relationships |
| **DataFlow.md** | End-to-end lifecycle of a score: Epic API leaderboard → scrape → UPSERT → change detection → ScoreHistory → personal DB → Web API → client cache → render. Every hop, every transformation. |
| **DeploymentTopology.md** | Docker Compose topology, FSTService + PercentileService relationship, volume mounts, networking, health checks, readiness probes |
| **SharedPackages.md** | @festival/core (types, API client), @festival/theme (design tokens, breakpoints), @festival/ui-utils (formatters, platform detection). What lives where, why, import conventions. |
| **SecurityModel.md** | Auth layers: no auth (public), X-API-Key (admin), Bearer JWT (user). Path traversal guard middleware. Rate limiting (per-endpoint categories with limits). CORS. Input validation. |

### 14.3 — FSTService Documentation (`docs/service/`)

Existing 5 pages stay (Overview, ApiReference, AuthSecurity, DeploymentConfig, ScraperPipeline). Add:

| Page | Contents |
|---|---|
| **RunModes.md** | Every CLI mode in depth: normal (loop), --api-only, --setup (device code flow), --test "Song" (single song), --once, --resolve-only, --backfill-only. When to use each, what they do, what they skip. |
| **ScrapePhase1-Auth.md** | Epic device auth flow step-by-step. Token lifecycle. Refresh logic. Credential file format. Error recovery. |
| **ScrapePhase2-CatalogSync.md** | FestivalService.InitializeAsync(), calendar API, song model, how new songs are detected, how removed songs are handled. |
| **ScrapePhase3-PathGeneration.md** | MIDI download → AES-ECB decrypt → MidiTrackRenamer → CHOpt execution → max score extraction → path image generation. File formats, key management, parallelism. |
| **ScrapePhase4-GlobalScrape.md** | Pipelined architecture: song iteration → per-instrument channel → writer tasks. InstrumentDatabase UPSERT. Change detection (ChangedAccountIds). DOP management. AdaptiveConcurrencyLimiter AIMD algorithm. |
| **ScrapePhase5-FirstSeenSeason.md** | Algorithm: MIN across 6 DBs → probe (MIN-1) → set. Skip logic. Immutability after calculation. |
| **ScrapePhase6-NameResolution.md** | Batch API (100 per request). AccountNames table. LastResolved timestamp. Best-effort retry. |
| **ScrapePhase7-PersonalDbBuild.md** | Per-user/device SQLite generation. Schema. Data sources (instrument DBs, meta DB). Trigger conditions (changed users). N+1 problem documentation. |
| **ScrapePhase8-PostScrapeRefresh.md** | Batched V2 lookups. Users per request (500). Stale entry detection. Current-season session capture (RefreshCurrentSeasonSessions). |
| **ScrapePhase9-Backfill.md** | Per-user score acquisition for below-60K entries. BackfillStatus state machine. BackfillProgress tracking. Resumption. |
| **ScrapePhase10-HistoryRecon.md** | Season window discovery. Seasonal leaderboard walks. ScoreHistory deduplication (unique index + COALESCE merge). OldScore tracking for improvement detection. FirstSeenSeason optimization. |
| **ScrapePhase11-Cleanup.md** | Expired session removal. |
| **AdaptiveConcurrency.md** | Full AIMD algorithm documentation. Evaluation window (500 ops). Error rate thresholds (1% increase, 5% decrease). Increase magnitude (+16). Decrease factor (×0.75). Clamping. Thread safety. |
| **ResilientHttp.md** | Retry policy. Circuit breaker. Timeout handling. HttpErrorHelper parsing. Error classification. |
| **ItemShopService.md** | Shop data source, scrape schedule, WebSocket broadcast, shop URL mapping. |

### 14.4 — Database Documentation (`docs/database/`)

Existing FSTServiceDatabaseDesign.md stays. Add:

| Page | Contents |
|---|---|
| **MetaDatabase.md** | All 11 tables with full column definitions, types, defaults, constraints, indexes. ScoreHistory deduplication logic (ON CONFLICT DO UPDATE with COALESCE). Rank semantics (SeasonRankFinal vs AllTimeRankAtLookup). BackfillStatus state machine diagram. |
| **InstrumentDatabases.md** | LeaderboardEntries schema. Sharding rationale (1 DB per instrument). PRAGMA configuration. Write lock pattern. Migration history. Index definitions. |
| **PersonalDatabases.md** | Per-user/device schema. Build trigger conditions. Data staleness model. Sync protocol with mobile client. |
| **CoreSongDatabase.md** | fst-service.db schema. FestivalService catalog. Song model fields. Calendar API mapping. |
| **DataAccuracyGuide.md** | Known accuracy gaps: Rank=0 ambiguity, stale percentiles, EndTime NULL overwrites, SeasonRank = final (not point-in-time). Mitigation strategies for each. |
| **StorageOptimization.md** | Difficulty denormalization analysis (~480MB savings). 4-column PK analysis (~800MB savings). FK index gaps. WAL configuration. Vacuum strategy. |

### 14.5 — API Documentation (`docs/api/`)

| Page | Contents |
|---|---|
| **Endpoints.md** | All 14+ endpoints: method, path, auth requirement, rate limit category, request params, response schema (TypeScript types), example request/response JSON, error codes. |
| **Authentication.md** | Three auth schemes in depth. X-API-Key: where configured, how validated. Bearer JWT: token structure, claims, expiry, refresh flow. Public: which endpoints, rate limits. |
| **RateLimiting.md** | Fixed-window strategy. Per-category limits: public (60/min), auth (10/min), protected (30/min), global (200/min). Response headers. 429 behavior. |
| **WebSocket.md** | Shop WebSocket endpoint. Connection protocol. Message format (JSON). Reconnection with exponential backoff (1s → 30s cap). Client-side handling (useShopWebSocket). |
| **ErrorHandling.md** | Error response format. HttpErrorHelper parsing. Epic API error codes. Retry-after headers. Circuit breaker behavior from client perspective. |

### 14.6 — Web App Documentation (`docs/web/`)

#### Architecture Pages

| Page | Contents |
|---|---|
| **Architecture.md** | Provider stack (7 nested contexts), page shell model, routing (HashRouter, lazy loading), responsive breakpoint system, code splitting strategy. |
| **DesignTokens.md** | Complete token reference: every CSS variable (colors, spacing, radius, fonts, z-index, animation durations) with visual swatches. JS exports (Colors, Radius, Font, Gap, Size, Layout). Theme file locations. |
| **ResponsiveDesign.md** | 4 viewport tiers: mobile (≤768px), wide-mobile (≥420px/≥520px), compact desktop (≤1439px), wide desktop (≥1440px). Which features appear/disappear at which tier. Media queries reference. |
| **StateManagement.md** | All 7 contexts: what each manages, provider hierarchy, consumer hooks, re-render impact. React Query integration. Module-level caches (pre-refactor: pageCache, _cached* variables). localStorage keys. |
| **ScrollModel.md** | (Post-refactor) Browser-native scroll. Sticky sidebar. useScrollRestore → window.scrollY. useHeaderCollapse → window scroll. useStaggerRush → window scroll. Modal scroll isolation. |
| **AnimationSystem.md** | Stagger system (usePageTransition, useStagger). Load phase state machine (Loading → SpinnerOut → ContentIn). FadeIn component. Stagger rush on scroll. Return-visit 200ms fade. CSS keyframes reference (fadeInUp, fadeOut, spin, slideUp). |
| **CachingStrategy.md** | React Query configuration (staleTime, gcTime). Page cache registry. Module-level caches (migration plan). Cache invalidation triggers (settings change, filter change). |
| **TestingGuide.md** | Vitest setup. TestProviders wrapper. API mocking (createApiMock). Browser stubs (stubScrollTo, stubElementDimensions, stubResizeObserver). Timer helpers (advanceThroughLoadPhase). DOM adapter mocking. 95% per-file threshold. v8 ignore ban. |
| **BuildAndDeploy.md** | Vite config. Build output (→ FSTService/wwwroot). Dev proxy (/api/* → localhost:8080). Path aliases. Code coverage configuration. ESLint/Stylelint rules. |

#### Per-Page Documentation

| Page | Doc |
|---|---|
| **SongsPage.md** | Song list rendering (virtual scroll with TanStack Virtualizer, ROW_HEIGHT 122/68px). Search (debounced 250ms, case-insensitive). Sorting (13 modes × ascending/descending). Filtering (difficulty, stars, season, percentile, missing scores). Instrument icon visibility. Metadata column reordering. Shop highlighting (pulse animation). Sync banner during active sync. Stagger animation (per-viewport estimation). Scroll cache key 'songs'. FAB registration (search, sort, filter). |
| **SongDetailPage.md** | Album art background (fixed position, dim overlay). Collapsing header (40px threshold). Score history chart (Recharts, bar/line hybrid, pagination by point/offset, tooltip, instrument selector auto-compact). Instrument card grid (2-column auto-fill, stagger per row). Paths modal (zoomable image, per-instrument paths). Shop button (conditional on shop URL). First-run carousel (6 slides, 3 gated on hasPlayer). |
| **LeaderboardPage.md** | Paginated list (25 per page, First/Prev/Page/Next/Last). Player highlight (tracked player row). Responsive columns (accuracy ≥420px, season ≥520px, stars ≥768px). Header collapse. Animation modes (first/paginate/cached). Stagger on first load, instant on pagination. Player footer (sticky, FAB-aware). Cache per songId:instrument. |
| **PlayerHistoryPage.md** | Virtual scroll list of score history entries. Sort modal (score/date/accuracy × ascending/descending). SongInfoHeader with collapse. Stagger on first load, reset on sort change. Platform-conditional sort button (hidden on mobile FAB). |
| **PlayerPage.md** | Overall summary section. Per-instrument stat cards: completion%, FC%, avgPercentile, overallPercentile. Percentile bracket table (17 thresholds). Top songs section. Stat box navigation (click → filtered song list). Sync status with progress indicators. Skip-stagger for re-renders of same account. |
| **RivalsPage.md** | Common rivals (intersection across all enabled instruments, 2+ required). Combo rivals (combined instrument key). Per-instrument sections. Preview count (3 per section). Section header → navigate to AllRivals. Rival row → navigate to RivalDetail. nameWidthVar CSS variable for name alignment. Module-level cache for back-nav. |
| **RivalDetailPage.md** | Head-to-head breakdown. 6 categorized sections: closest battles (top 5 by |rankDelta|), almost passed (rival leads, closer half), slipping away (rival leads, farther half), barely winning (user leads, closest third), pulling forward (middle third), dominating them (farthest third). Song comparison rows with score delta, rank delta, album art, year. Navigation to RivalryPage per category. |
| **RivalryPage.md** | Full song list for a rivalry category. Score delta width computed for alignment. Stagger per song row. Module-level cache for back-nav. |
| **AllRivalsPage.md** | Full rival list for category: common (intersection), combo, or per-instrument. Parallel data fetching (all instruments simultaneously for common). Direction labels (above/below). Rival row click → RivalDetail. |
| **SuggestionsPage.md** | Category cards with InfiniteScroll (react-infinite-scroll-component). Filter modal (difficulty, stars, season, percentile, instrument toggles). Category card structure: header + song preview grid. FadeIn per card with revealed count tracking. useScrollFade for per-child mask. Stagger with load phase. |
| **ShopPage.md** | Grid view (2/3/4/5 columns by viewport width) and list view toggle. ShopCard with pulse animation (shopPulse CSS). SongRow reuse in list mode. View mode persisted to localStorage. Stagger regeneration on view toggle. |
| **SettingsPage.md** | App Settings section: instrument icon visibility, metadata column reordering (drag-and-drop), score filter toggle + leeway slider. Item Shop section: hide shop, disable highlighting. Instruments section: 6 toggles. Instrument Metadata section: 6 toggles. Version info. First Run Guide replay (per-page + reset all). Reset section (danger zone). FadeIn per section with stagger. |

#### First Run Experience

| Page | Doc |
|---|---|
| **FREOverview.md** | FRE system architecture: FirstRunContext, useRegisterFirstRun, useFirstRun, FirstRunCarousel. localStorage persistence (fst:firstRun). Content hashing (djb2) for change detection. Version bumping for rewrites. Gate system (hasPlayer, shopHighlightEnabled). Active carousel mutex (prevents changelog overlap). |
| **FRESongsSlides.md** | 7 slides: Song List (v3), Sort (v5), Navigation (v5, mobile/desktop variants), Filter (v4, gated on hasPlayer), Song Icons (v3, gated), Metadata (v3, gated), Shop Highlight (v1, gated on shopHighlightEnabled). Each with: id, version, title/description i18n keys, gate condition, stagger count, demo component description. |
| **FRESongInfoSlides.md** | 6 slides: Chart (v2, gated), Bar Select (v2, gated), View All (v2, gated), Top Scores (v2), Paths (v2, mobile/desktop), Shop Button (v1, mobile variant). |
| **FREStatisticsSlides.md** | 5 slides: Drill Down (v1), Overview (v2), Instrument Breakdown (v1), Percentiles (v1), Top Songs (v1). |
| **FRESuggestionsSlides.md** | 4 slides: Category Card (v1), Global Filter (v1), Instrument Filter (v1), Infinite Scroll (v1). |
| **FREPlayerHistorySlides.md** | 2 slides: Score List (v1), Sort Controls (v1, mobile/desktop variant). |

#### Algorithms & Math

| Page | Doc |
|---|---|
| **ScoringMath.md** | Score representation (raw integer). Accuracy (×10,000 format, e.g. 9800 = 98%). Stars (0-6 scale, 6 = gold). Full combo (boolean). Max score derivation (CHOpt). Invalid score detection formula: `score > maxScore × (1 + leeway/100)`. |
| **PercentileCalculation.md** | Raw percentile: `rank / totalEntries × 100`. Per-instrument percentile: mean across played songs. Overall percentile: `(∑(rank/totalEntries) + unplayedCount) / totalSongs × 100` (unplayed penalized as 100th percentile). 17 bracket thresholds. Percentile sourcing precedence: API field → rank/totalEntries fallback. |
| **RivalMatchingAlgorithm.md** | How rivals are identified (server-side). rivalScore metric. Common rivals: intersection across all enabled instruments (2+ required). Combo rivals: multi-instrument key derivation. Direction determination: majority vote across instruments. Preview sorting (rivalScore descending). |
| **RivalCategorizationAlgorithm.md** | Song-level categorization into 6 buckets: closest battles (top 5 by |rankDelta|), rival leads split 50/50 (almost passed / slipping away), user leads split into thirds (barely winning / pulling forward / dominating them). Math: `third = ceil(length / 3)`, `half = ceil(length / 2)`. |
| **SongFilteringPipeline.md** | 6-stage filter pipeline: search (substring) → instrument missing/has toggles (OR logic) → season blacklist → percentile bracket → stars range → difficulty range. Sort modes (13 total) with tiebreaker chain: primary → title → artist → year. |
| **StaggerAnimationMath.md** | Delay calculation: `baseDelay + index × interval`. Viewport estimation: `maxVisible = floor(viewportHeight / rowHeight)`. Interval capping: `interval = max(minInterval, floor(totalDuration / maxVisible))`. Rush-on-scroll: all pending animations collapse to 0ms delay. |

#### Component Reference

| Page | Doc |
|---|---|
| **PageShell.md** | `<Page>` component: props, variants (default, withBg, withBgClip), scroll area variants, container variants. before/after slots. Scroll infrastructure (useScrollMask, useStaggerRush). |
| **FrostedCard.md** | Visual surface: frostedCard (grain + shadows), frostedCardLight (minimal), purpleGlass (active state), modalCard (backdrop blur). CSS composition pattern. SVG noise texture specification. |
| **PageHeader.md** | Alignment contract. Sticky mode. Collapse transition. max-width + padding-h inheritance from Page. |
| **Paginator.md** | 3 modes: dots, arrows, numbered. Keyboard support. Touch swipe. Props reference. |
| **ActionPill.md** | Pill button for toolbar actions. Active state. Dot indicator. Frosted surface. |
| **ModalSystem.md** | Modal.tsx structure. ModalShell overlay. ConfirmAlert flow. useModalState hook. useModalDraft hook (change detection + confirm-discard). Animation: enter/exit. |
| **InstrumentSelector.md** | Full mode (icon row) vs compact mode (arrows). Auto-compact via ResizeObserver. required prop. Icon rendering. Color system. |

### 14.7 — PercentileService Documentation (`docs/percentile/`)

| Page | Contents |
|---|---|
| **Overview.md** | Purpose, V1 API integration, relationship to FSTService, deployment, configuration. |
| **PercentileAlgorithm.md** | V1 leaderboard API query with teamAccountIds. Population derivation. Percentile extraction. Data flow back to FSTService. |

### 14.8 — FortniteFestival.Core Documentation (`docs/core/`)

| Page | Contents |
|---|---|
| **Overview.md** | Multi-targeting (net472 + net9.0). Module inventory: Services, Models, Config, Persistence, Auth, IO, Scraping, Serialization, Suggestions. |
| **FestivalService.md** | Calendar API integration. Song catalog sync. Song model fields. Season detection. |
| **Models.md** | All shared model types with field definitions. |

### 14.9 — Component READMEs

| README | Purpose |
|---|---|
| **FortniteFestivalWeb/README.md** | Quick-start, stack, dev commands, testing commands, build output, coverage config. Links to docs/web/ for deep dives. |
| **FSTService/README.md** | Quick-start, run modes, config, Docker. Links to docs/service/ for deep dives. |
| **FortniteFestival.Core/README.md** | Purpose, targets, modules. Links to docs/core/. |
| **PercentileService/README.md** | Purpose, deployment, config. Links to docs/percentile/. |

### 14.10 — Doc-as-You-Go Rule

Every structural change in Phases 1-13 must update the relevant documentation pages in the same PR. Documentation is part of implementation, not an afterthought.

### Documentation Page Count

| Section | Pages |
|---|---|
| Root + Index | 2 |
| Architecture | 5 |
| Service (new) | 14 |
| Database (new) | 6 |
| API | 5 |
| Web Architecture | 9 |
| Web Per-Page | 12 |
| Web FRE | 6 |
| Web Algorithms & Math | 6 |
| Web Component Reference | 7 |
| PercentileService | 2 |
| Core | 3 |
| READMEs | 4 |
| **Total** | **~81 documentation pages** |

**Verification:**
- Every page renders on GitHub with correct Markdown
- No broken cross-links between pages
- docs/README.md index lists every page with status
- Every algorithm page includes the actual formula/pseudocode
- Every UX page includes a description of every interactive element
- Every FRE page includes all slide IDs, versions, gates, and stagger counts

---

## Phase 15: Agent Tooling — MCP Server, Agent, and Instructions

**Goal**: Build an MCP server with project-specific tools that help an AI agent rapidly navigate, understand, and modify the codebase. Create a custom agent that uses these tools and enforces coding/testing guidelines.

### 15.1 — FST MCP Server (`tools/fst-mcp/`)

A lightweight Node.js MCP server providing project-specific tools that go beyond generic file search:

**Tools to implement:**

| Tool | Purpose | Returns |
|---|---|---|
| `fst_find_page` | Given a page name or route, returns the page file, its CSS module, all child components, hooks used, and the route pattern | File paths + imports |
| `fst_find_component` | Given a component name, returns the file, its CSS module, all usages across the codebase, and its props interface | File paths + usage locations |
| `fst_find_hook` | Given a hook name, returns the file, all call sites, dependencies it uses | File paths + call sites |
| `fst_page_tree` | Given a page, returns the full component tree (what components render what) | Tree structure |
| `fst_api_endpoints` | Lists all FSTService API endpoints with method, path, auth requirement, and handler location | Endpoint table |
| `fst_db_schema` | Given a database name (meta, instrument, personal, core), returns the current schema (parsed from EnsureSchema code) | Table definitions |
| `fst_settings_map` | Returns all AppSettings keys, their types, defaults, and which components react to each setting | Settings reference |
| `fst_fre_slides` | Returns all FirstRun page keys, slide IDs, gate conditions, and registration locations | FRE reference |
| `fst_route_map` | Returns the complete route table: URL pattern → page component → required context (player, etc.) | Route table |
| `fst_coverage_check` | Runs `vitest run --coverage` and returns files below 95% threshold | Coverage report |
| `fst_lint_check` | Runs `eslint` and returns violations grouped by rule | Lint report |
| `fst_v8_ignore_check` | Searches for any `v8 ignore` markers in src/ and reports violations | Violation list (should be empty) |
| `fst_test_check` | Runs `dotnet test` or `vitest run` for a specific component and returns pass/fail | Test results |
| `fst_design_token` | Given a token name (color, spacing, radius, etc.), returns its CSS variable, JS constant, and current value | Token reference |

**Implementation:**
- Node.js with `@modelcontextprotocol/sdk`
- Tools that need code analysis use `grep` / `ast-grep` / regex on the file system (fast, no build required)
- Tools that need test execution shell out to `vitest` / `dotnet test`
- Schema tools parse the `EnsureSchema()` methods directly from C# source
- Add to `.vscode/mcp.json` alongside existing Playwright server

**Directory structure:**
```
tools/fst-mcp/
  package.json
  tsconfig.json
  src/
    index.ts          — MCP server entry point
    tools/
      findPage.ts     — fst_find_page implementation
      findComponent.ts
      findHook.ts
      pageTree.ts
      apiEndpoints.ts
      dbSchema.ts
      settingsMap.ts
      freSlides.ts
      routeMap.ts
      coverageCheck.ts
      lintCheck.ts
      v8IgnoreCheck.ts
      testCheck.ts
      designToken.ts
    utils/
      fileSearch.ts   — Grep/regex helpers
      astHelpers.ts   — JSX/TSX parsing helpers
```

### 15.2 — Custom Agent: FST Guardian (`.github/agents/fst-guardian.agent.md`)

A custom Copilot agent that uses the MCP tools and enforces coding standards:

**Agent identity:**
- Name: `fst-guardian`
- Description: "Fortnite Festival Score Tracker codebase guardian. Enforces coding standards, testing requirements, and architecture patterns. Use when: modifying components, adding features, reviewing code, checking coverage."

**Agent capabilities:**
- Before modifying any file, runs `fst_find_component` / `fst_find_page` / `fst_find_hook` to understand the full dependency tree
- After any code change, runs `fst_v8_ignore_check` and `fst_lint_check` to verify no violations
- When adding a new page, verifies it uses `<Page>` shell, `<PageHeader>`, `<FrostedCard>`, etc.
- When adding a new component, verifies it has a corresponding test file
- When modifying settings, runs `fst_settings_map` to verify all consumers are updated
- When modifying FRE slides, runs `fst_fre_slides` to verify registration

**Tool restrictions:**
- Has access to: all MCP tools, file read/write, terminal
- Blocked from: `v8 ignore` insertion (enforced by hook)

### 15.3 — Coding Guidelines Instructions (`.github/instructions/`)

**`.github/instructions/coding-standards.instructions.md`:**
```yaml
---
applyTo: "FortniteFestivalWeb/src/**"
---
```
- No `v8 ignore` directives — ever
- No inline styles (use CSS modules; `react/forbid-dom-props` enforces)
- No magic numbers (eslint enforces with whitelist)
- All pages must use `<Page>` shell

---

## Phase 17: String Union Types → Const Enum Migration

**Goal**: Replace all string union types used for discriminated mode/state switching with `const enum` values for type safety, exhaustive checking, and elimination of magic string comparisons scattered across components.

**Motivation**: Several domain-level string union types are compared via raw string literals throughout the codebase (`case 'score':`, `mode === 'title'`, `sortMode: 'percentile' as const`). These bypass TypeScript's enum exhaustiveness guarantees, are fragile to typos (e.g., `'seasonachieved'` vs `'seasonAchieved'`), and force `as const` casts when used in object literals. Converting to `const enum` provides minification (inlined numeric values), IDE rename support, and explicit exhaustiveness in switch statements.

**Scope**: Domain-level mode/state types — NOT CSS keyword strings (those are covered by Phase 16's cssEnums). NOT React component prop unions that are consumed by external callers.

See [docs/design/StringUnionEnumMigration.md](../design/StringUnionEnumMigration.md) for the full design document.

### 17.1 — Identify All String Union Types

**Candidates** (string unions used in switch/case or comparison):
- `SongSortMode` — 11 variants (`'title'`, `'artist'`, `'year'`, `'shop'`, `'hasfc'`, `'score'`, `'percentage'`, `'percentile'`, `'stars'`, `'seasonachieved'`, `'intensity'`)
- `MetadataSortKey` — 10 variants (subset of SongSortMode)
- `SongRowVisualKey` — 6 variants (subset)
- `SyncPhase` — 3 variants (`'idle'`, `'backfill'`, `'history'`)
- `SongSortMode` appears in both `@festival/core` and `FortniteFestivalWeb/src/utils/songSettings.ts` (duplication)

### 17.2 — Convert to Const Enums

For each type, create a `const enum` in `@festival/core`:

```ts
export const enum SongSortMode {
  Title = 0,
  Artist = 1,
  Year = 2,
  Shop = 3,
  HasFC = 4,
  Score = 5,
  Percentage = 6,
  Percentile = 7,
  Stars = 8,
  SeasonAchieved = 9,
  Intensity = 10,
}
```

### 17.3 — Update All Callers

- Replace `'score'` → `SongSortMode.Score` in all switch/case/comparison sites
- Replace `sortMode: 'title' as const` → `sortMode: SongSortMode.Title`
- Update `INSTRUMENT_SORT_MODES` and `METADATA_SORT_DISPLAY` to use enum keys
- Update localStorage serialization to convert between enum ↔ string (migration layer)
- Update test files

### 17.4 — Handle Serialization

String unions are currently persisted to localStorage. Add a migration layer:
- `saveSongSettings()` serializes enum → string for backward compat
- `loadSongSettings()` deserializes string → enum
- Existing localStorage data automatically migrates on next load

### Phase 17 File Impact

| Metric | Estimate |
|---|---|
| Types converted | 4-5 |
| Files updated | 30-40 |
| Tests updated | 10-15 |
| New enum files | 1-2 (in `@festival/core`) |
| Migration helpers | 1 (serialization adapter) |

**Verification:**
- `grep -rn "as const" src/ | grep SongSortMode` returns zero matches
- All switch statements on sort/sync modes use enum constants
- localStorage round-trip preserves settings across upgrade
- TypeScript reports exhaustiveness errors for unhandled enum cases
- All frosted surfaces must use `<FrostedCard>`
- All page headers must use `<PageHeader>`
- All lists must use `<ItemList>`
- All empty states must use `<EmptyState>`
- All modals must use `useModalDraft` for draft state
- All stagger animations must use `usePageTransition` + `useStagger`
- Coverage threshold: 95% per file, enforced per-file

**`.github/instructions/testing-standards.instructions.md`:**
```yaml
---
applyTo: "FortniteFestivalWeb/__test__/**"
---
```
- Use `TestProviders` wrapper for all component tests
- Use `createApiMock()` for API mocking
- Use `stubScrollTo()` / `stubElementDimensions()` for DOM stubs
- Use `advanceThroughLoadPhase()` for phase transitions
- Mock `domAdapters` module for DOM-heavy tests
- Never mock React Query — use `createTestQueryClient()` instead
- Test files mirror source structure

**`.github/instructions/service-standards.instructions.md`:**
```yaml
---
applyTo: "FSTService/**/*.cs"
---
```
- Parameterized SQL only — no string interpolation in queries
- Use `Path.Combine` / `Path.GetFullPath` — no raw string paths
- Propagate `CancellationToken` in all async methods
- Use `ILogger<T>` — no Console.Write
- New database operations: use pragma helper, add indexes
- New endpoints: add to appropriate feature endpoint class (not ApiEndpoints.cs)
- Coverage: 95% minimum, no `[ExcludeFromCodeCoverage]` without documented justification

### 15.4 — Pre-commit Hook (`.github/hooks/`)

**`.github/hooks/pre-commit.json`:**
```json
{
  "event": "PreToolUse",
  "tools": ["write_file", "edit_file"],
  "steps": [{
    "command": "grep -n 'v8 ignore' ${filePath} && echo 'BLOCKED: v8 ignore directive found' && exit 1 || exit 0"
  }]
}
```

Blocks any file write that introduces a `v8 ignore` marker.

### 15.5 — Prompt Templates (`.github/prompts/`)

**`add-page.prompt.md`:**
- Template for adding a new page: creates file, CSS module, test file, adds to `App.tsx` routes, uses `<Page>` shell, registers with router

**`add-component.prompt.md`:**
- Template for adding a new shared component: creates file, CSS module, test file, adds to barrel exports

**`add-api-endpoint.prompt.md`:**
- Template for adding a new FSTService endpoint: creates handler in correct feature module, adds tests, updates API docs

**`add-fre-slide.prompt.md`:**
- Template for adding a new FRE slide: creates slide definition, demo component, registers in page's firstRun/index.ts

### 15.6 — MCP Server Config Update

Update `.vscode/mcp.json`:
```json
{
  "servers": {
    "playwright": {
      "command": "npx",
      "args": ["@playwright/mcp@latest", "--browser", "msedge"]
    },
    "fst": {
      "command": "node",
      "args": ["tools/fst-mcp/dist/index.js"],
      "cwd": "${workspaceFolder}"
    }
  }
}
```

**Verification:**
- `fst_find_page Songs` returns SongsPage.tsx + SongsPage.module.css + SongsToolbar + SongRow + route
- `fst_v8_ignore_check` returns empty list
- `fst_coverage_check` returns all files ≥ 95%
- FST Guardian agent correctly blocks `v8 ignore` insertion
- All .instructions.md files load correctly in Copilot
- All .prompt.md templates produce valid output

---

## Updated Final Phase Ordering

| Phase | Depends On | Can Parallel With |
|---|---|---|
| 1. Scroll Model | — | 7, 9, 11, 12, 14, 15 |
| 2. Page Shell | 1 | 7, 9, 11, 12, 14, 15 |
| 3. Stagger Consolidation | 2 | 4, 5, 9, 10 |
| 4. Base Components | 2 | 3, 5, 9, 10 |
| 5. Header Alignment | 2 | 3, 4, 9, 10 |
| 6. Web Performance | — | anything |
| 7. Testability | — | anything |
| 8. Playwright | 7; test writing after 1-5 | 6 |
| 9. Component Consolidation | — | 1-5, 7 |
| 10. Page Action Bar | 2 | 3-5, 9 |
| 11. Service Architecture | — | anything |
| 12. Dead Code | — | anything |
| 13. Perf/Memory/Storage | — | anything |
| **14. Documentation** | — | **anything (updated as phases complete)** |
| **15. Agent Tooling** | — | **anything (MCP server independent)** |
| **16. CSS → useStyles** | **After 1-5** (architecture stable) | 7, 8, 11, 14, 15 |

**Phase 16 ordering note**: The theme package restructure (16.1) can start immediately. The useStyles hook creation (16.4) and shared factories (16.5) can start immediately. The actual CSS module migration (16.6) should happen after Phases 1-5 so that we migrate the final architecture, not the pre-refactor layout. Otherwise we'd be converting CSS modules that are about to be restructured.

### Updated Scope

- **Total phases**: 16
- **Total new web files**: ~15-20 (components, hooks, fixtures) + ~10 new theme files
- **Total web files refactored**: ~60-70 (architecture) + ~96 CSS modules eliminated
- **Total CSS modules eliminated**: 96 → 0 (replaced by useStyles)
- **Total new Playwright tests**: ~131 specs × 4 viewports
- **Total new vitest tests**: ~15-20
- **Total documentation pages**: ~90+ (comprehensive wiki across 12+ sections)
- **Total docs deleted**: 4 (duplicates) + 5 (dead web theme files)
- **Total docs updated**: 7 (status headers on design docs)
- **MCP server**: 1 (14 tools, Node.js)
- **Agent files**: 1 agent, 3 instructions, 4 prompts, 1 hook
- **FSTService refactor**: 3 orchestrators, 5 endpoint modules, Core auth extraction
- **Excluded**: React Native app, MAUI app, full PercentileService merge

---

## Phase 16: CSS Modules → useStyles() + Theme Package Restructure ✅ COMPLETE

**Status**: Completed March 25, 2026.

**Goal**: Eliminate CSS module files. All styling moves to JS via `useStyles()` hooks backed by `@festival/theme` constants. Theme package restructured with single-responsibility modules, CSS enum constants, helper functions, and factory spreads.

### Actual Results

| Metric | Plan | Actual |
|---|---|---|
| CSS modules at start | 96 | 96 |
| CSS modules deleted | 96 | 92 (fully deleted) |
| CSS modules remaining | 0 | 4 (consolidated minimal files in `src/styles/`) |
| Reason for remaining 4 | — | CSS-only features: `::after`, `@keyframes`, `@media`, `@container`, `:hover`, `backdrop-filter`, `mask-image`, `::placeholder` |
| CSS lines eliminated | ~6,500 | ~6,200 (~95%) |
| CSS lines remaining | 0 | ~300 (across 4 files — irreducible minimum) |
| Theme constants added | — | 100+ (Layout, Animation, CssEnum, Shadow, etc.) |
| Theme helpers added | — | `border()`, `padding()`, `margin()`, `transition()`, `transitions()`, `scale()`, `translateY()`, `scaleTranslateY()` |
| Theme factories added | — | `frostedCard`, `modalOverlay`, `modalCard`, `btnPrimary`, `btnDanger`, `purpleGlass`, `flexColumn`, `flexRow`, `flexCenter`, `flexBetween`, `truncate`, `absoluteFill`, `fixedFill`, `centerVertical` |
| Theme enums added | — | `Display`, `Position`, `Align`, `Justify`, `TextAlign`, `FontStyle`, `FontVariant`, `WordBreak`, `WhiteSpace`, `Isolation`, `TransformOrigin`, `TextTransform`, `BoxSizing`, `BorderStyle`, `Overflow`, `ObjectFit`, `Cursor`, `PointerEvents`, `CssValue`, `CssProp`, `GridTemplate` |
| Migration rules documented | — | 37 rules in `docs/refactor/CSS_MIGRATION_RULES.md` |
| Translation keys added | — | 20+ (en.json: error, format, shop, leaderboard sections) |
| Shared style exports created | — | `modalStyles.ts`, `playerPageStyles.ts`, `songRowStyles.ts`, `filterStyles.ts`, `sidebarStyles.ts`, `appStyles.ts`, `useRivalsSharedStyles.ts` |
| Tests passing | — | 2,274 / 2,274 (172 test files) |

### Final CSS Module Inventory (4 files)

```
src/styles/
  animations.module.css      — @keyframes + ::after/::before animation pseudo-elements
  instrumentSelector.module.css — InstrumentSelector className API (external component contract)
  rivals.module.css          — Rival gradient ::after overlays + @container responsive queries
  effects.module.css         — backdrop-filter, mask-image, ::placeholder, :hover, @media grids
```

### What was NOT done (deferred or unnecessary)

- **16.1 — Theme package file splitting**: Not needed. The flat structure with well-named constants works. The planned 18-file split would have added complexity without benefit. Instead, added `cssEnums.ts`, `cssHelpers.ts`, and `factories.ts` alongside existing files.
- **16.2 — Delete dead web theme files**: Already done in Phase 12.
- **16.3 — Delete theme.css**: Still present (provides CSS custom properties for the 4 remaining `.module.css` files). Will be fully deletable only when all CSS modules are eliminated.
- **16.4 — Generic useStyles() hook**: Not needed. Per-file `function useStyles(...)` at bottom with `useMemo` is simpler, more discoverable, and doesn't require a shared hook.

### 16.1 — Restructure @festival/theme Package

**Current state**: 9 files. `spacing.ts` is overloaded with 8 unrelated concepts (typography + spacing + sizing + layout + surfaces). `colors.ts` has 73+ colors in a flat object with 14 semantic groups. `animation.ts` mixes timing, stagger, debounce, and interaction thresholds.

**New structure** (`packages/theme/src/`):

```
colors/
  base.ts          — Backgrounds, Surfaces, Frosted Glass, Overlays, Text, Borders
  semantic.ts      — Status (green/red), Accent/Brand (blue/purple), Gold
  components.ts    — Difficulty badges, Score badges, Button semantics, Profile
  dataViz.ts       — Distribution chart colors (Top1-Below50), Accuracy gradient RGB
  index.ts         — Re-exports all as unified Colors object (backward compat)

typography.ts      — Font sizes (xs-display), LineHeight, FontWeight
spacing.ts         — Gap (xs-section), Opacity states
sizing.ts          — Size constants: icons, album art, thumbs, controls, dots, bars, pills, chart heights
layout.ts          — Layout padding/margins, MaxWidth, heights (songRow, sidebar, fabPadding, sectionHeading)
surfaces.ts        — Radius (xs-full), frostedCard/frostedCardLight/purpleGlass/modalCard style objects
gold.ts            — goldFill, goldOutline, goldOutlineSkew
zIndex.ts          — Z-index stacking: background, base, dropdown, popover, modalOverlay
breakpoints.ts     — (unchanged) Pixel values + media query strings
animations.ts      — Stagger (interval, offset), fade/transition durations, spinner/chart timing
interaction.ts     — Debounce (250ms), resize debounce (150ms), swipe threshold (50px), demo interval
polling.ts         — (unchanged)
pagination.ts      — (unchanged)
index.ts           — Barrel re-exports everything
```

**Key rules:**
- Every constant gets exactly one home. No duplicates between files.
- Some constants may have **semantic aliases** in different modules (e.g., `Font.title` = 22 AND `Size.pageTitleFont` = 22 if both are needed for different consumption contexts). The alias explicitly references the canonical source.
- React Native compatible: no CSS variables, no DOM APIs. Pure numeric/string constants.
- Each file ≤ 80 lines. If it grows beyond, split further.

### 16.2 — Delete Dead Web Theme Files

Delete the entire `FortniteFestivalWeb/src/theme/` directory (5 files). Everything already imports from `@festival/theme`. These are 100% dead duplicates:
- `src/theme/colors.ts` — duplicate of package
- `src/theme/frostedStyles.ts` — duplicate
- `src/theme/goldStyles.ts` — duplicate
- `src/theme/spacing.ts` — partial duplicate (6 of 50+ keys)
- `src/theme/index.ts` — barrel re-export of duplicates

### 16.3 — Delete theme.css (CSS Custom Properties)

All 120+ CSS custom properties in `src/styles/theme.css` become JS constants. Delete the file. Also delete `src/styles/animations.css` (keyframes move to JS-based animation strings or a shared animation utilities module).

CSS variables currently referenced from `.module.css` files will instead come from `useStyles()` return values backed by theme constants.

### 16.4 — Create useStyles() Hook

**Create `@festival/theme/src/useStyles.ts`** (or web-specific: `FortniteFestivalWeb/src/hooks/ui/useStyles.ts`):

```ts
type StyleFactory<T extends string> = () => Record<T, React.CSSProperties>;

export function useStyles<T extends string>(factory: StyleFactory<T>): Record<T, React.CSSProperties> {
  return useMemo(() => factory(), []);
}
```

**Usage in components:**

```tsx
import { Colors, Gap, Radius, Font } from '@festival/theme';

const styles = useStyles(() => ({
  card: {
    backgroundColor: Colors.surfaceFrosted,
    borderRadius: Radius.md,
    border: `1px solid ${Colors.glassBorder}`,
    padding: Gap.section,
  },
  title: {
    fontSize: Font.title,
    fontWeight: 700,
    color: Colors.textPrimary,
  },
}));

return <div style={styles.card}><h2 style={styles.title}>...</h2></div>;
```

**Why `useMemo` and not just `const`?**
- Memoized per-component instance
- Avoids recreating style objects on every render
- Factory pattern allows future dynamic theming injection (dark mode, user preferences)
- Static analysis tooling can verify theme constant usage

### 16.5 — Create Shared Style Factories

For patterns that appear across 10+ components, create named factories in the theme package:

**`@festival/theme/src/factories.ts`:**

```ts
export const flexColumn = { display: 'flex', flexDirection: 'column' } as const;
export const flexRow = { display: 'flex', alignItems: 'center' } as const;
export const flexCenter = { display: 'flex', alignItems: 'center', justifyContent: 'center' } as const;
export const textBold = { fontWeight: 700 } as const;
export const textSemibold = { fontWeight: 600 } as const;
export const truncate = { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' } as const;
```

Components spread these in their `useStyles` factories: `{ ...flexColumn, gap: Gap.md }`.

### 16.6 — Migrate Components (Batch Strategy)

Migrate in batches, smallest-first:

**Batch 1: Single-rule files (22 files)** — Inline the 1-4 rules directly into the component. Delete the .module.css file entirely. ~22 files deleted, 0 new files.

**Batch 2: Shared utilities (4 files)** — Convert `shared.module.css`, `songRow.module.css`, `animations.css`, `index.css` to JS factories + global style injection.

**Batch 3: Common components (11 files)** — Accordion, ActionPill, ArcSpinner, DirectionSelector, InstrumentSelector, LoadGate, PaginationButton, RadioRow, SearchBar, SectionHeader, ToggleRow.

**Batch 4: Display components (4 files)** — InstrumentChip, InstrumentHeader, InstrumentIcons + CSS.

**Batch 5: Song metadata (7 files)** — AccuracyDisplay, AlbumArt, DifficultyBars, GoldStars, MiniStars, PercentilePill, ScorePill, SeasonPill, SongInfo.

**Batch 6: Shell components (9 files)** — AnimatedBackground, DesktopNav, Sidebar, PinnedSidebar, MobileHeader, BottomNav, FAB, BackLink.

**Batch 7: Modal system (5 files)** — Modal, ConfirmAlert, ChangelogModal, ModalShell, BulkActions.

**Batch 8: Page components (12 files)** — One per page: Songs, SongDetail, Leaderboard, PlayerHistory, Player, Rivals (4 pages), Suggestions, Shop, Settings.

**Batch 9: Page sub-components (22 files)** — Row components, chart components, path components, filter/sort modals, FRE demos.

**Batch 10: App shell (1 file)** — App.module.css → last, since it's the outermost layout.

### 16.7 — Global Styles Strategy

Some CSS must remain global (not per-component): `@keyframes`, body/html resets, scrollbar styling, font imports. These go into a minimal `src/index.css` (~30 lines) that Vite imports via `main.tsx`.

Keyframe animations referenced by name in JS (`animation: 'fadeInUp 400ms ease-out'`) need to be defined globally. Create `src/styles/keyframes.css`:
```css
@keyframes fadeInUp { from { opacity: 0; transform: translateY(8px); } to { opacity: 1; transform: none; } }
@keyframes fadeOut { from { opacity: 1; } to { opacity: 0; } }
@keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }
```

### 16.8 — Prune Dead Styles During Migration

As each .module.css file is converted, audit every class:
- Is this class actually used? (Check component TSX for `css.className` references)
- Are there styles that duplicate something already in the theme? (e.g., `background-color: var(--color-surface-frosted)` → just use `Colors.surfaceFrosted`)
- Are there responsive styles that can be replaced by the breakpoint system? (`@media (max-width: 768px)` → conditional in component using `useIsMobile()`)

**Expected pruning**: 10-15% of styles are dead or redundant based on the audit showing `.frostedCardLight` unused, several gold exports unused, and duplicate gap/radius declarations across files.

### 16.9 — Update ESLint Rules

After migration:
- Remove `react/forbid-dom-props` warning on `style` (no longer applies — `style` is the primary styling mechanism)
- Add rule: warn on `.module.css` imports (catch leftover CSS module usage)
- Add rule: enforce `useStyles()` pattern for style definitions (optional, could be too strict)

### Phase 16 File Impact (Actual)

| Metric | Count |
|---|---|
| CSS modules fully deleted | 92 |
| CSS modules consolidated to `src/styles/` | 4 (minimal, CSS-only features) |
| Dead CSS files deleted | shared.module.css, songRow.module.css, 90 component-level .module.css files |
| Shared style exports created | 7 (.ts files replacing CSS imports) |
| Theme package files added/modified | 6 (cssEnums.ts, cssHelpers.ts, factories.ts, frostedStyles.ts, spacing.ts, animation.ts) |
| Theme constants added | 100+ |
| Translation keys added | 20+ |
| Test assertions updated | 50+ (class name → style property/DOM structure queries) |
| Global CSS remaining | 3 (index.css, keyframes.css, theme.css — ~120 lines total) |
| Net CSS reduction | ~95% of all CSS lines |

**Verification:**
- Zero `.module.css` files remain in `src/`
- All styling flows through `useStyles()` or inline `style={}` backed by theme constants
- `@festival/theme` has single-responsibility modules, each ≤ 80 lines
- React Native can import any theme module without web-specific dependencies
- ESLint warns on any `.module.css` import

---

## Additional Documentation Pages (Phase 14 Additions)

### Agent & MCP Server Documentation (`docs/tooling/`)

| Page | Contents |
|---|---|
| **MCP-Overview.md** | FST MCP Server architecture: Node.js, 14 tools, how it parses the codebase (regex/grep, no build required). How to add new tools. Configuration in .vscode/mcp.json. |
| **MCP-ToolReference.md** | All 14 tools: name, description, parameters, example input/output, implementation file. fst_find_page, fst_find_component, fst_find_hook, fst_page_tree, fst_api_endpoints, fst_db_schema, fst_settings_map, fst_fre_slides, fst_route_map, fst_coverage_check, fst_lint_check, fst_v8_ignore_check, fst_test_check, fst_design_token. |
| **Agent-Guardian.md** | FST Guardian agent: identity, capabilities, tool access, enforcement rules,  when to use it, what it blocks. |
| **CodingStandards.md** | Combined reference for all 3 `.instructions.md` files: web coding standards, web testing standards, service standards. Every rule with rationale. |
| **PromptTemplates.md** | All 4 prompt templates (add-page, add-component, add-api-endpoint, add-fre-slide) with example invocations and expected output. |

### Styling Documentation (`docs/web/`)

| Page | Contents |
|---|---|
| **StylingGuide.md** | useStyles() pattern: why (file reduction, RN compatibility, dynamic theming), how (factory function, useMemo), when to use inline vs useStyles vs global CSS. Shared factories (flexColumn, flexRow, textBold). Migration from CSS modules. |
| **ThemePackage.md** | @festival/theme structure: every file, every export, every constant with its value. Organization principles (single-responsibility, ≤80 lines, semantic aliases). How to add a new constant. What goes in theme vs what's component-local. |
| **DesignTokenReference.md** | Complete visual reference: every color (with hex swatch description), every spacing value, every radius, every font size, every z-index layer, every animation timing. Organized by category. The definitive token Bible. |
| **ResponsivePatterns.md** | How responsive styling works without CSS media queries: useIsMobile(), useIsWideDesktop(), conditional style objects. Breakpoint reference. When to use which hook. |

### Testing Documentation (`docs/web/`)

| Page | Contents |
|---|---|
| **VitestGuide.md** | Philosophy: unit + integration via jsdom. TestProviders wrapper. API mocking (createApiMock factory, vi.hoisted/vi.mock pattern). Browser stubs (stubScrollTo, stubElementDimensions, stubResizeObserver). DOM adapter mocking. Timer helpers (advanceThroughLoadPhase). React Query: never mock directly, use createTestQueryClient(). Coverage: 95% per-file, v8 ignore ban. File organization: mirrors src/. When to use renderHook vs render. When to use fake timers. |
| **PlaywrightGuide.md** | Philosophy: integration tests exercising real browser. 4 viewport projects. Fixture system (mockApi, fixtures, localStorage seeding). Helper utilities (waitForContentIn, waitForSpinnerGone). What to test in Playwright vs Vitest (DOM/scroll/animation/responsive → Playwright; logic/state/rendering → Vitest). Page object pattern (if adopted). CI integration. Debugging (video on failure). |

### Updated Doc Count

| Section | Pages |
|---|---|
| Root + Index | 2 |
| Architecture | 5 |
| Service | 14 |
| Database | 6 |
| API | 5 |
| Web Architecture | 9 |
| Web Per-Page | 12 |
| Web FRE | 6 |
| Web Algorithms & Math | 6 |
| Web Component Reference | 7 |
| **Web Styling** | **4** |
| **Web Testing** | **2** |
| **Tooling (Agent + MCP)** | **5** |
| PercentileService | 2 |
| Core | 3 |
| READMEs | 4 |
| **Total** | **~92 documentation pages** |
