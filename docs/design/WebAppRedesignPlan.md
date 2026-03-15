# Plan: FortniteFestivalWeb Comprehensive Redesign

**TL;DR**: Restructure the web app from a monolithic component tree into a modular, performant architecture with CSS Modules, shared packages, memoized components, centralized constants, extracted hooks, and i18n infrastructure — while keeping the UI pixel-identical to the end user. Light API additions to FSTService where they reduce client-side work.

---

## Decisions & Scope

- **Localization**: English-only initially; set up i18n framework so all user-facing strings are extractable
- **Service changes**: Light API parameter additions allowed (no new major endpoints)
- **Shared packages**: Structure `packages/` for future RN consumption but don't wire RN imports yet
- **Styling**: Migrate from inline JS styles to **CSS Modules** (`.module.css` per component)
- **Testing**: Add tests for all new/extracted hooks and utilities; skip visual component tests
- **Exclusions**: No RN app modifications; no major FSTService architectural changes; no UI/visual changes

---

## Phase 1: Foundation — Shared Packages & Theme Extraction

*Goal: Establish the shared package structure and centralize scattered constants.*

### Step 1.1: Create `packages/theme` package
- Create `packages/theme/package.json` (private, `"main": "src/index.ts"`)
- Move `FortniteFestivalWeb/src/theme/colors.ts` → `packages/theme/src/colors.ts`
- Move `FortniteFestivalWeb/src/theme/spacing.ts` → `packages/theme/src/spacing.ts`
- Move `FortniteFestivalWeb/src/theme/frostedStyles.ts` → `packages/theme/src/frostedStyles.ts`
- Move `FortniteFestivalWeb/src/theme/goldStyles.ts` → `packages/theme/src/goldStyles.ts`
- Create `packages/theme/src/index.ts` barrel export
- Update `FortniteFestivalWeb/package.json` to add `"@festival/theme": "portal:../packages/theme"`
- Update `FortniteFestivalWeb/vite.config.ts` alias: `'@festival/theme' → packages/theme/src`
- Update `FortniteFestivalWeb/tsconfig.json` paths: `"@festival/theme": ["../packages/theme/src"]`
- Update all web imports from `'../theme'` / `'../../theme'` → `'@festival/theme'`

### Step 1.2: Create `packages/ui-utils` package
- Create `packages/ui-utils/package.json`
- Move `FortniteFestivalWeb/src/utils/stagger.ts` → `packages/ui-utils/src/stagger.ts`
- Move `FortniteFestivalWeb/src/utils/platform.ts` → `packages/ui-utils/src/platform.ts`
- Delete `FortniteFestivalWeb/src/utils/isPwa.ts` (trivial re-export; inline `IS_PWA` import from platform)
- Create `packages/ui-utils/src/index.ts` barrel
- Update web imports accordingly

### Step 1.3: Centralize scattered constants
- Create `packages/theme/src/breakpoints.ts`:
  - `MOBILE_BREAKPOINT = 768` (currently hardcoded in `useIsMobile.ts` QUERY string)
  - `NARROW_BREAKPOINT = 420` (hardcoded in `LeaderboardPage.tsx`)
  - `MEDIUM_BREAKPOINT = 520` (hardcoded in `LeaderboardPage.tsx`)
- Create `packages/theme/src/animation.ts`:
  - `STAGGER_INTERVAL = 125` (used in SongsPage, LeaderboardPage, PlayerHistoryPage, SuggestionsPage)
  - `FADE_DURATION = 400` (used in stagger animations across all pages)
  - `SPINNER_FADE_MS = 500` (used in multiple page loading sequences)
  - `DEBOUNCE_MS = 300` (search debounce in PlayerSearch, HeaderSearch, SongsPage)
  - `RESIZE_DEBOUNCE_MS = 150` (window resize handler in LeaderboardPage)
- Create `packages/theme/src/pagination.ts`:
  - `LEADERBOARD_PAGE_SIZE = 25` (hardcoded in LeaderboardPage)
  - `SUGGESTIONS_BATCH_SIZE = 6` (hardcoded in useSuggestions)
  - `SUGGESTIONS_INITIAL_BATCH = 10` (hardcoded in useSuggestions)
- Create `packages/theme/src/polling.ts`:
  - `SYNC_POLL_ACTIVE_MS = 3000` (hardcoded in useSyncStatus)
  - `SYNC_POLL_IDLE_MS = 60000` (hardcoded in useSyncStatus)
- Update `packages/theme/src/index.ts` to re-export all new modules
- Replace all hardcoded magic numbers in web app with imported constants

### Step 1.4: Align theme with RN
- Compare `packages/theme/src/colors.ts` with `FortniteFestivalRN/packages/ui/src/theme/colors.ts`
- Ensure identical tokens exist; add any web-only tokens with clear `// web-only` comments
- Same for spacing tokens — ensure `Radius`, `Font`, `Gap`, `Size`, `MaxWidth`, `Layout` match

**Verification:**
- `cd FortniteFestivalWeb && npx tsc --noEmit` passes with zero errors
- `cd FortniteFestivalWeb && npx vite build` succeeds
- All existing tests pass: `cd FortniteFestivalWeb && npx vitest run`
- Grep for any remaining direct `'../theme'` imports in web src — should be zero

---

## Phase 2: CSS Modules Migration

*Goal: Replace inline JS style objects with CSS Modules for better performance, cacheability, and developer ergonomics.*

### Step 2.1: Set up CSS Modules infrastructure
- Vite supports CSS Modules out of the box (files named `*.module.css`)
- Create `FortniteFestivalWeb/src/types/css.d.ts` with module declaration:
  ```
  declare module '*.module.css' { const classes: Record<string, string>; export default classes; }
  ```
- Add to `tsconfig.json` include array

### Step 2.2: Migrate shared style mixins to CSS
- Create `FortniteFestivalWeb/src/styles/shared.module.css`:
  - `.frostedCard` class (replaces `frostedCard` JS object spread)
  - `.goldFill`, `.goldOutline`, `.goldOutlineSkew` classes
  - `.fadeInUp` animation class (parametric via CSS custom properties `--delay`)
- Create `FortniteFestivalWeb/src/styles/layout.module.css`:
  - `.scrollContainer` (shared scroll container pattern used by every page)
  - `.pageContent` (common max-width + padding pattern)
  - `.headerCollapsed` / `.headerExpanded` (header collapse transition)
  - `.gridTwoColumn` / `.gridSingleColumn` (PlayerPage grid)

### Step 2.3: Migrate components (smallest first, one at a time)
Migrate each component's `const styles: Record<string, React.CSSProperties>` to a co-located `.module.css`:

**Batch A — Leaf components** (*parallel with each other*):
1. `SeasonPill.tsx` → `SeasonPill.module.css` (16 lines, trivial)
2. `BackLink.tsx` → `BackLink.module.css` (32 lines)
3. `AlbumArt.tsx` → `AlbumArt.module.css` (69 lines)
4. `InstrumentIcons.tsx` → `InstrumentIcons.module.css` (53 lines)
5. `ConfirmAlert.tsx` → `ConfirmAlert.module.css` (129 lines)

**Batch B — Modal components** (*sequential — Modal.tsx first, then children*):
1. `Modal.tsx` → `Modal.module.css` (627 lines — largest modal)
2. `FilterModal.tsx` → `FilterModal.module.css` (412 lines)
3. `SortModal.tsx` → `SortModal.module.css` (213 lines)
4. `SuggestionsFilterModal.tsx` → `SuggestionsFilterModal.module.css` (222 lines)
5. `PathsModal.tsx` → `PathsModal.module.css` (480 lines)
6. `PlayerScoreSortModal.tsx` → `PlayerScoreSortModal.module.css` (129 lines)

**Batch C — Complex components** (*sequential*):
1. `PlayerSearch.tsx` → `PlayerSearch.module.css` (183 lines)
2. `ChangelogModal.tsx` → `ChangelogModal.module.css` (155 lines)
3. `AnimatedBackground.tsx` → `AnimatedBackground.module.css` (212 lines)
4. `ScoreHistoryChart.tsx` → `ScoreHistoryChart.module.css` (1,183 lines)

**Batch D — Pages** (*sequential — each page is large*):
1. `SettingsPage.tsx` → `SettingsPage.module.css` (400 lines — cleanest)
2. `PlayerHistoryPage.tsx` → `PlayerHistoryPage.module.css` (420 lines)
3. `SongDetailPage.tsx` → `SongDetailPage.module.css` (580 lines)
4. `LeaderboardPage.tsx` → `LeaderboardPage.module.css` (650 lines)
5. `SongsPage.tsx` → `SongsPage.module.css` (895 lines)
6. `SuggestionsPage.tsx` → `SuggestionsPage.module.css` (900 lines)
7. `PlayerPage.tsx` → `PlayerPage.module.css` (1,100 lines)

**Batch E — App shell** (*depends on all above*):
1. `App.tsx` → `App.module.css` (1,000+ lines)

### Step 2.4: Migrate SettingsPage dynamic slider styles
- Currently injects `<style>` tag into `<head>` dynamically for range slider
- Move slider styles to `SettingsPage.module.css` using CSS custom properties for colors
- Remove imperative `document.createElement('style')` + `document.head.appendChild` code

### Step 2.5: Clean up `index.css`
- Replace hardcoded color values (`#1A0830`, `#FFFFFF`) with CSS custom properties that reference theme tokens
- Move animation keyframes (`fadeInUp`, `fadeOut`, `spin`, etc.) to `src/styles/animations.css` (imported globally)
- Keep only global resets and CSS custom property definitions in `index.css`

**Verification:**
- Visual regression: manually compare screenshots before/after on every page/modal (pixel-identical)
- `npx tsc --noEmit` passes
- `npx vite build` succeeds with no CSS warnings
- All existing tests pass
- No remaining `const styles: Record<string, React.CSSProperties>` blocks in any component

---

## Phase 3: Memoization & Performance

*Goal: Fix all identified memoization gaps and performance bottlenecks.*

### Step 3.1: Fix context value memoization (CRITICAL)
- **`FestivalContext.tsx`**: Wrap Provider value in `useMemo` with deps `[songs, currentSeason, isLoading, error, refresh]`
- **`SettingsContext.tsx`**: Wrap Provider value in `useMemo` with deps `[settings, setSettings, updateSettings, resetSettings]`
- **`FabSearchContext.tsx`**: Wrap Provider value in `useMemo` with deps `[query, setQuery, registerActions, openSort, openFilter, registerSuggestionsActions, openSuggestionsFilter, registerPlayerHistoryActions, openPlayerHistorySort, registerSongDetailActions, openPaths, registerPlayerPageSelect, playerPageSelect]`

### Step 3.2: Split `FabSearchContext` into focused contexts
Currently a "god context" with 14 methods. Split into:
- **`SearchQueryContext`**: `query`, `setQuery` (consumed by search inputs)
- **`PageActionsContext`**: `registerActions`, `openSort`, `openFilter`, etc. (consumed by FAB + pages)
- **`PlayerSelectContext`**: `registerPlayerPageSelect`, `playerPageSelect` (consumed by PlayerPage + FAB)

Each smaller context changes less frequently → fewer consumer re-renders.

### Step 3.3: Memoize leaf components with `React.memo`
Wrap the following pure presentational components:
- `SeasonPill` — trivial, renders `<span>` from `season` prop
- `InstrumentIcon` — pure, renders `<img>` from `instrument` + `size`
- `AlbumArt` — pure after load, props are `src` + `size`
- `BackLink` — pure, props are `fallback` + `animate`
- `ConfirmAlert` — pure modal content

### Step 3.4: Memoize complex components
- **`ScoreHistoryChart`** (1,183 lines): Wrap in `React.memo` with custom `areEqual` comparing `songId`, `accountId`, `history` length + last entry
- **`AnimatedBackground`**: Wrap in `React.memo` comparing `songs.length` and `dimOpacity`

### Step 3.5: Fix inline style allocation in hot paths
Multiple pages create style objects inside render functions:

- **SongsPage** line ~697: `staggerDelay` computed inline per row → move to CSS Module class with `--delay` custom property
- **LeaderboardPage** line ~193: conditional `paddingTop` + `transition` object → create two CSS classes (`.headerExpanded`, `.headerCollapsed`) and toggle className
- **PlayerHistoryPage** line ~213: IIFE for accuracy color → extract to `getAccuracyStyle()` utility, memoize per row in useMemo
- **PlayerPage** lines 1070-1100: grid visibility calculation walks entire items array → memoize `visibleCount` with `useMemo` on `[items, window.innerHeight]`

### Step 3.6: Fix `FadeInDiv` duplication and allocation
`FadeInDiv` is defined independently in `PlayerPage.tsx`, `SuggestionsPage.tsx`, and implicitly in other pages. Issues:
- Creates new `useCallback` per instance (50+ in PlayerPage)
- Inline style object allocated per render

**Fix:**
- Extract to `src/components/FadeInDiv.tsx`
- Use CSS Module class `.fadeInDiv` with `--delay` custom property
- Use `animationend` event via CSS, not imperative `el.style.opacity = ''`
- Wrap in `React.memo`

### Step 3.7: Optimize `useStaggerRush`
Current: `querySelectorAll('[style*="fadeInUp"]')` + `getComputedStyle(el).opacity` on every scroll
- Replace `[style*="fadeInUp"]` selector with a `data-stagger` attribute added by `FadeInDiv`
- Cache the NodeList reference; only re-query when stagger key changes
- Use `el.dataset.rushed` flag instead of reading computed style

### Step 3.8: Fix window resize re-renders
**`LeaderboardPage.tsx`**: `useState(windowWidth)` causes full re-render on every resize (after 150ms debounce)

**Fix:** Replace with multiple `useSyncExternalStore`-based hooks:
- `useMediaQuery('(min-width: 420px)')` → `showAccuracy`
- `useMediaQuery('(min-width: 520px)')` → `showSeason`
- `useMediaQuery('(min-width: 768px)')` → `showStars`
- Each only triggers re-render when its boolean changes, not on every pixel change

### Step 3.9: PlayerPage `computeInstrumentStats` memoization
Currently called inline in render loop for each visible instrument:
```
for (const inst of visibleKeys) {
  const stats = computeInstrumentStats(scores, songs.length); // ❌ every render
}
```
**Fix:** Create `instrumentStats` useMemo:
```
const instrumentStats = useMemo(() => {
  const map = new Map();
  for (const inst of visibleKeys) {
    map.set(inst, computeInstrumentStats(byInstrument.get(inst) ?? [], songs.length));
  }
  return map;
}, [visibleKeys, byInstrument, songs.length]);
```

### Step 3.10: SongDetailPage scroll-to-instrument optimization
Currently walks DOM to find scroll container via `getComputedStyle()` in a loop.
**Fix:** Use a `ref` for the scroll container (already exists as `scrollRef`) and pass it directly:
```
scrollRef.current.scrollTo({ top: targetOffset, behavior: 'smooth' });
```
No need to traverse DOM.

**Verification:**
- React DevTools Profiler: before/after comparison on SongsPage (500+ songs), PlayerPage, SongDetailPage
- Measure re-render count when: (a) opening/closing modals, (b) switching tabs, (c) scrolling, (d) resizing window
- No visual changes
- All tests pass

---

## Phase 4: Hook Extraction & Deduplication

*Goal: Extract duplicate patterns into shared hooks.*

### Step 4.1: Extract `useAccountSearch` hook
Search + autocomplete pattern duplicated in 4 places:
- `PlayerSearch.tsx` (SearchInput)
- `App.tsx` (HeaderSearch)
- `App.tsx` (FloatingActionButton child search)
- `App.tsx` (MobilePlayerSearchModal)

Create `src/hooks/useAccountSearch.ts`:
```typescript
export function useAccountSearch(opts?: { debounceMs?: number; limit?: number }) {
  // Returns: query, setQuery, results, activeIndex, setActiveIndex, isOpen, close
  // Encapsulates: debounced API call, keyboard nav, click-outside detection
}
```

Update all 4 consumers to use this hook.

### Step 4.2: Extract `useHeaderCollapse` hook
Header collapse-on-scroll pattern duplicated in:
- `SongDetailPage.tsx` (collapse at 40px)
- `LeaderboardPage.tsx` (collapse at 40px)
- `PlayerHistoryPage.tsx` (collapse at 40px, desktop only)

Create `src/hooks/useHeaderCollapse.ts`:
```typescript
export function useHeaderCollapse(scrollRef: RefObject<HTMLElement>, threshold?: number): boolean
```

### Step 4.3: Extract `usePageLoadPhase` hook
Spinner → content transition pattern duplicated in ALL 7 pages:
```
useState('loading' | 'spinnerOut' | 'contentIn')
useEffect(() => { if loading → 'loading'; else → 'spinnerOut'; setTimeout → 'contentIn' })
```

Create `src/hooks/usePageLoadPhase.ts`:
```typescript
export function usePageLoadPhase(isLoading: boolean, opts?: { spinnerFadeMs?: number }):
  { phase: 'loading' | 'spinnerOut' | 'contentIn'; shouldStagger: boolean }
```

### Step 4.4: Extract `useModuleCache` hook
Module-level caching pattern duplicated in:
- `SongsPage.tsx` (`_savedScrollTop`, `_songsHasRendered`)
- `SongDetailPage.tsx` (`songDetailCache`)
- `LeaderboardPage.tsx` (`leaderboardCache`)
- `PlayerPage.tsx` (`_renderedPlayerAccount`)
- `SuggestionsPage.tsx` (`_cache`)
- `useSuggestions.ts` (`_cache`)
- `ScoreHistoryChart.tsx` (`historyCache`)

Create `src/hooks/useModuleCache.ts`:
```typescript
// Typed module-level cache that persists across unmounts but clears on signal
export function useModuleCache<K, V>(cacheId: string): {
  get(key: K): V | undefined;
  set(key: K, value: V): void;
  clear(): void;
  has(key: K): boolean;
}
```
- Manages cache invalidation centrally (when settings change, all caches clear)
- Replaces ad-hoc `let _cache` module-level variables
- Testable (can mock/reset between tests)

### Step 4.5: Extract `useScrollRestore` hook
Scroll position save/restore duplicated in:
- `SongsPage.tsx` (saves on scroll, restores on POP navigation)
- `SongDetailPage.tsx` (saves to songDetailCache, restores on POP)
- `LeaderboardPage.tsx` (saves to leaderboardCache, restores on POP)
- `SuggestionsPage.tsx` (saves to _cache, restores on mount)

Create `src/hooks/useScrollRestore.ts`:
```typescript
export function useScrollRestore(scrollRef: RefObject<HTMLElement>, cacheKey: string): {
  handleScroll: () => void;  // call from onScroll
}
```

### Step 4.6: Extract `useMediaQuery` hook (generalized)
Currently `useIsMobile` uses `useSyncExternalStore` for a single breakpoint. Generalize:

Create `src/hooks/useMediaQuery.ts`:
```typescript
export function useMediaQuery(query: string): boolean
```

Then redefine:
```typescript
export const useIsMobile = () => useMediaQuery(`(max-width: ${MOBILE_BREAKPOINT}px)`);
export const useIsMobileChrome = () => useMediaQuery(`(max-width: ${MOBILE_BREAKPOINT}px) and (hover: none)`);
```

Also used by Step 3.8 for responsive column visibility in LeaderboardPage.

**Verification:**
- All 4 search instances use `useAccountSearch` and behave identically
- Header collapse works on all 3 pages with same timing
- Page load phases work on all 7 pages
- Module caches invalidate properly when settings change
- Scroll restoration works for back navigation on all pages
- Add unit tests for each new hook (Step 4.1–4.6)

---

## Phase 5: Component Decomposition & Reusability

*Goal: Break up oversized components into focused, reusable pieces.*

### Step 5.1: Decompose `App.tsx` (~1,000 lines)
Split into focused modules:

- `src/components/shell/AppShell.tsx` — Provider tree + route layout
- `src/components/shell/Sidebar.tsx` — Desktop sidebar (extracted from App.tsx inline component)
- `src/components/shell/BottomNav.tsx` — Mobile bottom navigation tabs
- `src/components/shell/FloatingActionButton.tsx` — FAB with menu (extracted from App.tsx)
- `src/components/shell/MobileHeader.tsx` — Mobile header with search
- `src/components/shell/DesktopHeader.tsx` — Desktop header with back link + profile
- `src/components/shell/MobilePlayerSearchModal.tsx` — Player search modal

App.tsx becomes a thin wrapper assembling these shell pieces.

### Step 5.2: Decompose `ScoreHistoryChart.tsx` (~1,183 lines)
Split into:
- `src/components/chart/ScoreHistoryChart.tsx` — Main chart wrapper (props, data fetch, state machine)
- `src/components/chart/ChartCard.tsx` — Individual score card below chart
- `src/components/chart/ChartTooltip.tsx` — Custom Recharts tooltip
- `src/components/chart/ChartPagination.tsx` — Arrow navigation for chart points
- `src/components/chart/useChartData.ts` — Hook for data preparation, caching, pagination logic

### Step 5.3: Decompose `PlayerPage.tsx` (~1,100 lines)
Split into:
- `src/pages/PlayerPage.tsx` — Route entry, data loading, layout
- `src/components/player/PlayerSyncBanner.tsx` — Sync status banner
- `src/components/player/PlayerOverallStats.tsx` — Summary stat cards (top row)
- `src/components/player/InstrumentSection.tsx` — Per-instrument stat grid + top/bottom songs
- `src/components/player/StatBox.tsx` — Individual stat display box (already exists inline)
- `src/components/player/PercentileTable.tsx` — Percentile distribution table
- `src/hooks/usePlayerStats.ts` — Hook wrapping `computeOverallStats` + `computeInstrumentStats` with memoization

### Step 5.4: Decompose `SongsPage.tsx` (~895 lines)
Split into:
- `src/pages/SongsPage.tsx` — Route entry, data loading, filter/sort state
- `src/components/songs/SongRow.tsx` — Individual song list row (memoize with `React.memo`)
- `src/components/songs/SongRowMetadata.tsx` — Metadata display below song title (score, %, percentile, stars, etc.)
- `src/components/songs/SongListToolbar.tsx` — Search + filter/sort buttons

### Step 5.5: Decompose `SuggestionsPage.tsx` (~900 lines)
Split into:
- `src/pages/SuggestionsPage.tsx` — Route entry, filter state, infinite scroll shell
- `src/components/suggestions/SuggestionCategoryCard.tsx` — Individual category card
- `src/components/suggestions/SuggestionSongRow.tsx` — Song within a category
- `src/components/suggestions/useFilteredSuggestions.ts` — Hook replacing complex filter-exhaustion logic

### Step 5.6: Create reusable `LoadingSpinner` component
Currently every page has its own spinner → fadeOut → content-fadeIn logic.
- Create `src/components/LoadingSpinner.tsx` (uses `usePageLoadPhase` hook from Step 4.3)
- Props: `isLoading: boolean; children: ReactNode`
- Handles the spinner-to-content transition universally

### Step 5.7: Create reusable `PageScrollContainer` component
Every page has: scroll container with `onScroll` for mask, stagger rush, header collapse, scroll restore.
- Create `src/components/PageScrollContainer.tsx`
- Props: `cacheKey: string; onHeaderCollapse?: (collapsed: boolean) => void; children: ReactNode`
- Wires up: `useScrollMask`, `useStaggerRush`, `useScrollRestore`, `useHeaderCollapse`

**Verification:**
- Each decomposed component renders identically to before
- `App.tsx` < 200 lines after decomposition
- `ScoreHistoryChart.tsx` < 400 lines after decomposition
- `PlayerPage.tsx` < 400 lines after decomposition
- `SongsPage.tsx` < 300 lines after decomposition
- All tests pass

---

## Phase 6: Data Layer Optimization

*Goal: Reduce unnecessary client-side computation and leverage the server.*

### Step 6.1: Add server-side `?instrument=` filter to `/api/player/{accountId}`
Currently the web app fetches ALL scores for a player, then filters locally by visible instruments.
- **FSTService change**: Add optional `?instruments=Solo_Guitar,Solo_Bass` query parameter to `GET /api/player/{accountId}`
- In `InstrumentDatabase.GetPlayerScores()`, add WHERE clause: `AND Instrument IN (...)` when parameter present
- **Web change**: Pass `visibleInstruments(settings)` to the API call in `PlayerDataContext.tsx`
- Reduces payload size by up to 67% when user hides Pro instruments

### Step 6.2: Add server-side `?season=` filter to `/api/player/{accountId}/history`
Currently the web fetches all history, filters by season locally.
- **FSTService change**: Add optional `?season=N` parameter to history endpoint
- **Web change**: Use when PlayerHistoryPage filters by season

### Step 6.3: Expose `totalEntries` in leaderboard response (already exists)
The `LeaderboardResponse` already includes `totalEntries` — verify web is using it for "X total players" display instead of computing from response array length.

### Step 6.4: API client response caching layer
Create `src/api/cache.ts`:
```typescript
const apiCache = new Map<string, { data: unknown; timestamp: number }>();
const TTL = 5 * 60 * 1000; // 5 minutes

export function cachedGet<T>(path: string, fetcher: () => Promise<T>): Promise<T>
```
- Cache `getSongs()` (changes rarely — once per scrape cycle ~4 hours)
- Cache `getPlayerStats()` for 5 minutes
- Cache `getVersion()` for 30 minutes
- Do NOT cache: `getLeaderboard`, `getPlayer`, `getPlayerHistory` (these change frequently)

### Step 6.5: Optimize SongsPage filtering
Currently: `useMemo(filtered)` does a multi-pass filter + sort across ALL songs on every dependency change.
- Pre-compute a `songScoreIndex` in the `PlayerDataContext` that maps `songId → instrumentScores`
- Use the index in SongsPage filter instead of nested loops
- Move the song sorting comparator functions to `@festival/core` (`packages/core/src/app/songFiltering.ts` already has some)

### Step 6.6: Optimize useSuggestions cache invalidation
Currently the module-level `_cache` never invalidates when the player changes.
- Key the cache on `accountId`
- Clear cache when `accountId` changes in the hook
- Clear cache when settings (visible instruments) change

**Verification:**
- Network tab: `/api/player/{accountId}` payload size reduced when instruments hidden
- SongsPage filter timing (console.time): < 5ms for 500+ songs
- Suggestions don't show stale data after player switch
- API cache hits visible in Network tab (no duplicate requests within TTL)

---

## Phase 7: Navigation & Routing Cleanup

*Goal: Simplify tab stack management and make navigation more predictable.*

### Step 7.1: Simplify tab route memory
Currently `App.tsx` manages `tabRoutes` state to remember sub-routes per tab. This is fragile.
- Replace with `sessionStorage`-based tab state (survives page refresh)
- Move tab route logic to `src/hooks/useTabNavigation.ts`
- Expose: `navigateToTab(tab: TabKey)`, `currentTab`, `tabRouteFor(tab: TabKey)`

### Step 7.2: Fix mobile-only logic gating
Audit every `useIsMobile()` / `IS_MOBILE_DEVICE` check to ensure:
- Desktop doesn't run mobile-only code (FAB event handlers, bottom sheet positioning, touch event listeners)
- Mobile doesn't run desktop-only code (sidebar event handlers, flyout positioning)
- AnimatedBackground pauses when tab hidden (already done) AND when navigated to non-animated routes
- Ensure `useVisualViewport` hook only subscribes on mobile (currently always subscribes)

### Step 7.3: Route-level code splitting
- Use `React.lazy()` + `Suspense` for page-level components
- This reduces initial bundle size; SuggestionsPage, PlayerPage, SettingsPage loaded on demand
- Keep SongsPage in main bundle (most common entry point)

**Verification:**
- Tab memory works: navigate Songs → SongDetail → switch to Settings → switch back to Songs → SongDetail is restored
- Double-tap tab: returns to tab root
- Bundle size comparison: `vite build` → compare chunk sizes before/after lazy loading
- Mobile: no desktop-only event listeners in React DevTools
- Desktop: no mobile-only event listeners

---

## Phase 8: Localization (i18n) Infrastructure

*Goal: Set up framework so all user-facing strings are extractable; ship English only.*

### Step 8.1: Install and configure `react-i18next`
- `cd FortniteFestivalWeb && npm install react-i18next i18next`
- Create `src/i18n/index.ts` — i18next init with `lng: 'en'`, `fallbackLng: 'en'`
- Create `src/i18n/en.json` — English translations (initially empty, populated in Step 8.3)
- Wrap app in `<I18nextProvider>` in `main.tsx`

### Step 8.2: Create translation key conventions
Namespace by page/component:
```json
{
  "common": { "back": "Back", "cancel": "Cancel", "apply": "Apply", "reset": "Reset", "search": "Search..." },
  "songs": { "title": "Songs", "noResults": "No songs match your filters." },
  "player": { "syncInProgress": "Syncing scores...", "unknownUser": "Unknown User" },
  "settings": { "title": "Settings", "resetAll": "Reset All Settings" },
  "suggestions": { "title": "Suggestions", "noSuggestions": "No suggestions available." },
  "leaderboard": { "title": "Leaderboard", "page": "Page {{current}} of {{total}}" },
  "changelog": { "title": "What's New" }
}
```

### Step 8.3: Extract hardcoded strings
Systematically replace all user-facing string literals across pages and components with `t('key')` calls:
- Page titles and headers
- Button labels (Back, Cancel, Apply, Reset, etc.)
- Empty state messages
- Error messages
- Tooltip text
- Stat labels (Total Score, Full Combos, Average Accuracy, etc.)
- Changelog (keep dynamic, but wrap section titles)

Estimated string count: ~150-200 unique strings across the app.

### Step 8.4: Number and date formatting
- Use `Intl.NumberFormat` for score display (already partially done via `toLocaleString()`)
- Use `Intl.DateTimeFormat` for dates in score history
- Wire locale from i18next into formatters

**Verification:**
- App renders identically (English strings unchanged)
- `npx tsc --noEmit` passes
- Grep for remaining hardcoded strings — should be zero user-facing literals outside `t()` calls
- Add test: switch locale to verify framework works (even if only English exists)

---

## Phase 9: Testing

*Goal: Add tests for all new/extracted hooks and utilities.*

### Step 9.1: Hook tests (Vitest + React Testing Library)
New test files:
- `src/test/useAccountSearch.test.ts` — Mock API, verify debounce, keyboard nav, results
- `src/test/useHeaderCollapse.test.ts` — Verify collapse threshold
- `src/test/usePageLoadPhase.test.ts` — Verify phase transitions
- `src/test/useModuleCache.test.ts` — Verify get/set/clear/invalidation
- `src/test/useScrollRestore.test.ts` — Verify save/restore on POP navigation
- `src/test/useMediaQuery.test.ts` — Verify boolean changes on breakpoint cross
- `src/test/useTabNavigation.test.ts` — Verify tab memory

### Step 9.2: Utility tests
- `packages/ui-utils/src/__tests__/stagger.test.ts` — Verify delay calculation, edge cases
- `packages/ui-utils/src/__tests__/platform.test.ts` — Verify UA parsing, force overrides
- `packages/theme/src/__tests__/constants.test.ts` — Verify no undefined values, type correctness

### Step 9.3: Context memoization tests
- Extend `src/test/SettingsContext.test.tsx` to verify render count when context value doesn't change
- Add `src/test/FestivalContext.test.tsx` to verify no spurious re-renders

### Step 9.4: API cache tests
- `src/test/apiCache.test.ts` — Verify TTL, cache hits/misses, invalidation

**Verification:**
- `npx vitest run` — All tests pass
- Coverage report shows new hooks at 80%+ line coverage

---

## Phase 10: Final Cleanup & Documentation

### Step 10.1: Remove dead code
- Delete `FortniteFestivalWeb/src/utils/isPwa.ts` (replaced in Step 1.2)
- Delete inline `FadeInDiv` definitions from `PlayerPage.tsx`, `SuggestionsPage.tsx` (replaced in Step 3.6)
- Delete module-level cache variables replaced by `useModuleCache` (Step 4.4)
- Delete duplicate search logic in `App.tsx` (replaced by `useAccountSearch` in Step 4.1)

### Step 10.2: File organization audit
Final directory structure:
```
FortniteFestivalWeb/src/
├── api/
│   ├── client.ts           (unchanged)
│   └── cache.ts            (new — Step 6.4)
├── components/
│   ├── chart/              (new — Step 5.2)
│   ├── player/             (new — Step 5.3)
│   ├── shell/              (new — Step 5.1)
│   ├── songs/              (new — Step 5.4)
│   ├── suggestions/        (new — Step 5.5)
│   ├── FadeInDiv.tsx        (new — Step 3.6)
│   ├── LoadingSpinner.tsx   (new — Step 5.6)
│   ├── PageScrollContainer.tsx (new — Step 5.7)
│   ├── AlbumArt.tsx         (refactored)
│   ├── ... (remaining components, each with .module.css)
├── contexts/
│   ├── FestivalContext.tsx   (memoized — Step 3.1)
│   ├── SettingsContext.tsx   (memoized — Step 3.1)
│   ├── PlayerDataContext.tsx (unchanged)
│   ├── SearchQueryContext.tsx (new — Step 3.2)
│   ├── PageActionsContext.tsx (new — Step 3.2)
│   └── PlayerSelectContext.tsx (new — Step 3.2)
├── hooks/
│   ├── useAccountSearch.ts   (new — Step 4.1)
│   ├── useHeaderCollapse.ts  (new — Step 4.2)
│   ├── usePageLoadPhase.ts   (new — Step 4.3)
│   ├── useModuleCache.ts     (new — Step 4.4)
│   ├── useScrollRestore.ts   (new — Step 4.5)
│   ├── useMediaQuery.ts      (new — Step 4.6)
│   ├── useTabNavigation.ts   (new — Step 7.1)
│   ├── useIsMobile.ts        (refactored to use useMediaQuery)
│   ├── ... (remaining hooks)
├── i18n/
│   ├── index.ts              (new — Step 8.1)
│   └── en.json               (new — Step 8.2)
├── pages/                    (refactored, each < 400 lines)
├── styles/
│   ├── shared.module.css     (new — Step 2.2)
│   ├── layout.module.css     (new — Step 2.2)
│   └── animations.css        (new — Step 2.5)
├── test/                     (expanded — Phase 9)
└── App.tsx                   (< 200 lines — Step 5.1)
```

### Step 10.3: Update copilot-instructions.md
Add the web app section documenting:
- CSS Modules convention
- Shared packages (`@festival/theme`, `@festival/ui-utils`)
- Hook inventory and when to use each
- i18n key naming convention
- Component decomposition patterns
- Testing expectations

**Verification:**
- Full build: `npx vite build` succeeds
- Full test suite: `npx vitest run` passes
- TypeScript: `npx tsc --noEmit` clean
- Manual smoke test on every page and modal
- Bundle size comparison: document before/after

---

## Relevant Files

### Packages (new)
- `packages/theme/src/colors.ts` — Moved from web; shared color tokens
- `packages/theme/src/spacing.ts` — Moved from web; shared spacing scale
- `packages/theme/src/animation.ts` — NEW: animation timing constants
- `packages/theme/src/breakpoints.ts` — NEW: responsive breakpoints
- `packages/theme/src/pagination.ts` — NEW: page size constants
- `packages/theme/src/polling.ts` — NEW: polling interval constants
- `packages/ui-utils/src/stagger.ts` — Moved from web; `staggerDelay()`, `estimateVisibleCount()`
- `packages/ui-utils/src/platform.ts` — Moved from web; `IS_IOS`, `IS_ANDROID`, etc.

### FSTService (light changes)
- `FSTService/Api/ApiEndpoints.cs` — Add `?instruments=` param to player endpoint, `?season=` to history
- `FSTService/Persistence/InstrumentDatabase.cs` — Add instrument filtering to `GetPlayerScores()`
- `FSTService/Persistence/MetaDatabase.cs` — Add season filtering to score history query

### Web — Contexts (modify)
- `FortniteFestivalWeb/src/contexts/FestivalContext.tsx` — Add `useMemo` to Provider value
- `FortniteFestivalWeb/src/contexts/SettingsContext.tsx` — Add `useMemo` to Provider value
- `FortniteFestivalWeb/src/contexts/FabSearchContext.tsx` — Split into 3 focused contexts

### Web — Hooks (new)
- `FortniteFestivalWeb/src/hooks/useAccountSearch.ts` — Extracted search pattern
- `FortniteFestivalWeb/src/hooks/useHeaderCollapse.ts` — Extracted header collapse
- `FortniteFestivalWeb/src/hooks/usePageLoadPhase.ts` — Extracted loading state machine
- `FortniteFestivalWeb/src/hooks/useModuleCache.ts` — Centralized module-level caching
- `FortniteFestivalWeb/src/hooks/useScrollRestore.ts` — Extracted scroll save/restore
- `FortniteFestivalWeb/src/hooks/useMediaQuery.ts` — Generalized media query hook
- `FortniteFestivalWeb/src/hooks/useTabNavigation.ts` — Tab route memory

### Web — Components (new)
- `FortniteFestivalWeb/src/components/FadeInDiv.tsx` — Shared stagger animation wrapper
- `FortniteFestivalWeb/src/components/LoadingSpinner.tsx` — Shared loading transition
- `FortniteFestivalWeb/src/components/PageScrollContainer.tsx` — Shared scroll container
- `FortniteFestivalWeb/src/components/shell/*` — Decomposed App.tsx pieces
- `FortniteFestivalWeb/src/components/chart/*` — Decomposed ScoreHistoryChart
- `FortniteFestivalWeb/src/components/player/*` — Decomposed PlayerPage
- `FortniteFestivalWeb/src/components/songs/*` — Decomposed SongsPage
- `FortniteFestivalWeb/src/components/suggestions/*` — Decomposed SuggestionsPage

### Web — Pages (modify, shrink)
- All 7 pages under `FortniteFestivalWeb/src/pages/` — Each reduced to < 400 lines

### Web — Styles (new)
- `FortniteFestivalWeb/src/styles/shared.module.css` — Shared CSS classes
- `FortnineFestivalWeb/src/styles/layout.module.css` — Layout utilities
- `FortnineFestivalWeb/src/styles/animations.css` — Global keyframe animations
- Each component gets co-located `.module.css` file

### Web — i18n (new)
- `FortniteFestivalWeb/src/i18n/index.ts` — i18next config
- `FortniteFestivalWeb/src/i18n/en.json` — English translations

---

## Dependency Order

```
Phase 1 (Foundation) ──────────────────────────┐
                                                │
Phase 2 (CSS Modules)  ← depends on Phase 1    │
         ↓                                      │
Phase 3 (Memoization)  ← parallel with Phase 2 ┤
         ↓                                      │
Phase 4 (Hook Extraction) ← depends on 2 + 3   │
         ↓                                      │
Phase 5 (Decomposition) ← depends on 4         │
         ↓                                      │
Phase 6 (Data Layer) ← parallel with Phase 5   │
         ↓                                      │
Phase 7 (Navigation) ← depends on 5            │
         ↓                                      │
Phase 8 (i18n) ← depends on 5 (final strings)  │
         ↓                                      │
Phase 9 (Testing) ← depends on 4-8             │
         ↓                                      │
Phase 10 (Cleanup) ← depends on all            ┘
```

**Phases 2 and 3 can run in parallel** (CSS migration doesn't affect memoization fixes).
**Phases 5 and 6 can run in parallel** (component decomposition is independent of API changes).
**Phase 8 should run after Phase 5** (need final component structure before extracting strings).
