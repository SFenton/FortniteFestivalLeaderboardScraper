# FortniteFestivalWeb Performance Patterns

> Last updated: 2026-04-03

---

## Code Splitting

### Route-Level Lazy Loading
All pages except `SongsPage` (the home page) use `React.lazy()` in `src/App.tsx` (lines 14–28).

| Route | Lazy Component |
|---|---|
| `/songs/:songId` | `SongDetailPage` |
| `/leaderboard/:comboId` | `LeaderboardPage` |
| `/leaderboard/:comboId/player/:accountId` | `PlayerHistoryPage` |
| `/player` | `PlayerPage` |
| `/suggestions` | `SuggestionsPage` |
| `/settings` | `SettingsPage` |
| `/shop` | `ShopPage` |
| `/rivals` | `RivalsPage` |
| `/rivals/:accountId` | `RivalDetailPage` |
| `/rivals/category/:comboId` | `RivalryPage` |
| `/rivals/all` | `AllRivalsPage` |
| `/leaderboards` | `LeaderboardsOverviewPage` |
| `/leaderboards/full` | `FullRankingsPage` |
| `/compete` | `CompetePage` |

- **Eager load**: Only `SongsPage` is eagerly imported (home route)
- **Suspense boundary**: Single root-level `<Suspense fallback={<SuspenseFallback />}>` wrapping `<Routes>`
- **Fallback**: `SuspenseFallback` renders a centered `ArcSpinner` on a fixed overlay
- **Error boundaries**: Every lazy route wrapped in `<ErrorBoundary fallback={<RouteErrorFallback />}>`

### Chunk Strategy
- Vite default Rollup code splitting — no custom `manualChunks` configuration
- No webpack magic comments (`webpackChunkName`, `webpackPrefetch`, etc.)
- No `import.meta.glob` patterns
- Vite handles automatic chunk naming with content hashing

---

## Memoization

### React.memo (22 components)

**List/entry items** (hot path in virtualized lists):
- `SongRow` — `src/pages/songs/components/SongRow.tsx:109`
- `LeaderboardEntry` — `src/pages/leaderboard/global/components/LeaderboardEntry.tsx:47`
- `PlayerHistoryEntry` — `src/pages/leaderboard/player/components/PlayerHistoryEntry.tsx:33`
- `RankingEntry` — `src/pages/leaderboards/components/RankingEntry.tsx:21`
- `RankingCard` — `src/pages/leaderboards/components/RankingCard.tsx:30`
- `LeaderboardNeighborRow` — `src/pages/rivals/components/LeaderboardNeighborRow.tsx:26`
- `CategoryCard` — `src/pages/suggestions/components/CategoryCard.tsx:70`
- `ShopCard` — `src/pages/shop/components/ShopCard.tsx:15`

**Display primitives**:
- `AlbumArt` — `src/components/songs/metadata/AlbumArt.tsx:17`
- `SeasonPill` — `src/components/songs/metadata/SeasonPill.tsx:10`
- `DifficultyPill` — `src/components/songs/metadata/DifficultyPill.tsx:16`
- `InstrumentIcons` — `src/components/display/InstrumentIcons.tsx:32`
- `InstrumentChip` — `src/components/display/InstrumentChip.tsx:14`
- `ScoreHistoryChart` — `src/pages/songinfo/components/chart/ScoreHistoryChart.tsx:79`
- `InstrumentCard` — `src/pages/songinfo/components/InstrumentCard.tsx:27`
- `ZoomableImage` — `src/pages/songinfo/components/path/ZoomableImage.tsx:11` (memo + forwardRef)

**Settings/form controls**:
- `RadioRow` — `src/components/common/RadioRow.tsx:13`
- `ToggleRow` — `src/components/common/ToggleRow.tsx:14`
- `DirectionSelector` — `src/components/common/DirectionSelector.tsx:22`
- `ModalSection` — `src/components/modals/components/ModalSection.tsx:10`

**Table rows**:
- `PlayerPercentileHeader` — `src/components/player/PlayerPercentileTable.tsx:6`
- `PlayerPercentileRow` — `src/components/player/PlayerPercentileTable.tsx:22`

### useMemo (200+ instances)

**Common patterns**:
1. **Context value stabilization** — Every context provider wraps its value object in `useMemo` to prevent consumer rerenders (SettingsContext, FestivalContext, PlayerDataContext, FeatureFlagsContext, FirstRunContext, FabSearchContext, ShopContext, SearchQueryContext, ScrollContainerContext)
2. **Inline style objects** — ~100+ instances memoizing `CSSProperties` to avoid new reference each render (StatBox, RankingEntry, BottomNav, FABMenu, etc.)
3. **Derived data computations** — Filtering, sorting, mapping operations (SongsPage: scoreMap, enabledInstruments, filtered songs; SuggestionsPage: coreSongs, scoresIndex, visibleCategories; RivalsPage: commonRivals; CompetePage: leaderboardEntries)
4. **Layout calculations** — Responsive width computations (rankWidth, scoreWidth, playerRankWidth in LeaderboardPage, FullRankingsPage, RankingCard)
5. **First-run slide arrays** — Memoized slide definitions to avoid recreation (SongsPage, SettingsPage, PlayerHistoryPage, etc.)

### useCallback (193 instances)

**Common patterns**:
1. **Context-provided functions** — All context setters/actions wrapped (SettingsContext: setSettings/updateSettings/resetSettings; FirstRunContext: register/unregister/getUnseenSlides/markSeen/resetPage/resetAll; FabSearchContext: 10+ register* callbacks)
2. **Navigation handlers** — Route transitions (handleSelect, navigateToSongs, navigateToSongDetail)
3. **Modal open/close/action** — handleDismiss, handleYes, handleNo, handleTransitionEnd
4. **Scroll/animation callbacks** — resetRush, handleHeaderCollapse, handleAnimEnd, doTransition
5. **Filter/search handlers** — handleInstrumentSelect, setSearch, toggleView, setTab

---

## Virtualization

### TanStack React Virtual (`@tanstack/react-virtual` v3.13.22)

**SongsPage** (`src/pages/songs/SongsPage.tsx:429`):
- `useVirtualizer()` for the main song list
- ROW_HEIGHT: 122px (mobile), 68px (desktop)
- overscan: 8 items
- gap: 2px
- Rows measured with `virtualizer.measureElement` ref
- Items rendered via `getVirtualItems().map()` with absolute `translateY` positioning

**PlayerHistoryPage** (`src/pages/leaderboard/player/PlayerHistoryPage.tsx:174`):
- `useVirtualizer()` for score history list
- ROW_HEIGHT: 44px (mobile), 52px (desktop)
- overscan: 10 items
- scrollMargin: Dynamic offset for header

### Infinite Scroll (`react-infinite-scroll-component` v6.1.1)

**SuggestionsPage** (`src/pages/suggestions/SuggestionsPage.tsx:321`):
- `<InfiniteScroll>` component wrapping category cards
- scrollThreshold: `SCROLL_PREFETCH_PX` from `@festival/theme`
- Custom scrollableTarget from ScrollContainerContext
- Known issue: When filters hide content but container height doesn't change, InfiniteScroll may not fire again (lines 139–141)
- Debounced load triggers with 100ms `setTimeout` (lines 187, 199)

---

## Bundle Optimization

### Vite Configuration (`vite.config.ts`)
- **Plugin**: `@vitejs/plugin-react` (Fast Refresh in dev)
- **Output**: Built to `../FSTService/wwwroot` (served by ASP.NET Core)
- **Define**: Version constants injected at build time (`__APP_VERSION__`, `__CORE_VERSION__`, `__THEME_VERSION__`)
- **Aliases**: Monorepo packages resolved to source (`@festival/core`, `@festival/theme`, `@festival/ui-utils`)
- **Stubs**: `react-native` and `react-native-app-auth` stubbed out for shared code compatibility
- **No custom chunk splitting** — relies on Vite/Rollup defaults
- **No bundle analysis** tool configured (no `rollup-plugin-visualizer`)

### Dependencies (performance-relevant)
- React 19.0.0, React DOM 19.0.0
- @tanstack/react-query 5.90.21
- @tanstack/react-virtual 3.13.22
- Recharts 3.7.0 (charting — used in SongDetailPage score history)
- @dnd-kit/core 6.3.1 (drag-drop in settings)
- react-icons 5.6.0
- react-infinite-scroll-component 6.1.1

### Tree Shaking
- No explicit `sideEffects: false` in package.json
- Vite handles tree-shaking automatically via ES module analysis
- Monorepo packages aliased to source (not compiled) for full tree-shaking

---

## Network Optimization

### ETag Caching (`src/api/client.ts`)

**Songs cache (localStorage)**:
- Key: `fst_songs_cache` → `{ data, etag }`
- Sends `If-None-Match` header; on 304, returns cached data
- Persists across sessions for instant cold-start song list display

**Generic ETag cache (in-memory Map)**:
- Per-URL cache via `getWithETag<T>(path)`
- Used by: `getShop()`, `getPlayer()`, other endpoints
- Lost on page refresh (in-memory only)

### Album Art Payload Optimization
- `expandAlbumArt()` in client.ts prepends `https://cdn2.unrealengine.com/` to relative URLs
- Server sends compressed relative paths; client expands on receipt

### React Query Configuration (`src/api/queryClient.ts`)
```
staleTime: 5 min        — data considered fresh, no refetch
gcTime: 10 min           — garbage collect unused queries
retry: 1                 — single retry on failure
refetchOnWindowFocus: false — no auto-refetch on tab switch
```

### WebSocket for Shop Data (`src/hooks/data/useShopWebSocket.ts`)
- Connects to `/api/ws` (auto-detects ws:// vs wss://)
- Messages: `shop_snapshot` (full state), `shop_changed` (delta: added/removed/leavingTomorrow)
- Auto-reconnect with exponential backoff (1s base, 30s max)
- Eliminates polling for shop updates

### Nginx Configuration (`nginx.conf`)
- **Gzip**: Enabled for text/plain, CSS, JS, JSON, XML, SVG (min 256 bytes)
- **Static asset caching**: `expires 1y; Cache-Control: public, immutable` for js/css/images/fonts
- **SPA fallback**: `try_files $uri $uri/ /index.html`
- **WebSocket support**: Upgrade headers proxied for `/api/` routes

### Adaptive Polling (`src/hooks/data/useSyncStatus.ts`)
- Fast poll while syncing, slow poll when idle
- Respects `document.hidden` — pauses polling when tab is inactive

---

## Render Optimization

### React 18/19 Concurrent Features
- **useTransition**: NOT used anywhere
- **useDeferredValue**: NOT used anywhere
- **startTransition**: NOT used anywhere

### Stagger Animation System

**Core** (`packages/ui-utils/src/stagger.ts`):
- `staggerDelay(index, interval, maxItems)` — returns delay in ms, or `undefined` for items beyond viewport
- `estimateVisibleCount(itemHeight)` — calculates visible items from `window.innerHeight`

**Hook** (`src/hooks/ui/useStaggerStyle.ts`):
- Returns `{ style, onAnimationEnd }` for each item
- Sets `willChange: 'transform, opacity'` during animation
- Cleans up `willChange`, `animation`, `opacity` on animation end (prevents compositor layer bloat)
- `buildStaggerStyle()` static variant for non-hook contexts

**Animation lifecycle** (PaginatedLeaderboard pattern):
- Three modes: `'first'` (initial load) → `'paginate'` (page change) → `'cached'` (no animation)
- Timer auto-retires to `'cached'` after stagger window completes
- `staggerWindow = maxVisibleRows * STAGGER_INTERVAL + FADE_DURATION + 100ms`

### Debounce/Throttle Patterns

**rAF throttle** (`src/hooks/ui/useScrollMask.ts:76`):
- Scroll mask updates throttled via `requestAnimationFrame`
- At most one update per animation frame
- ResizeObserver also throttled
- Cleanup cancels pending rAF on unmount

**Scroll restore** (`src/hooks/ui/useScrollRestore.ts`):
- Dual-attempt restore: immediate + `requestAnimationFrame` fallback
- Passive scroll listener saves position to module-level `Map<string, number>`

**Suggestions debounce** (`src/pages/suggestions/SuggestionsPage.tsx:187`):
- 100ms `setTimeout` before triggering `loadMore()` for infinite scroll
- Dual timeout (0ms + 100ms) for scroll position restoration after filter changes

**Stagger reset** (`src/pages/leaderboard/player/PlayerHistoryPage.tsx:101`):
- Defers stagger reset to next frame via `requestAnimationFrame` after programmatic scroll

### Tab Visibility
- `AnimatedBackground` pauses transitions when `document.hidden`
- `useSyncStatus` pauses polling when tab is inactive

### AlbumArt Image Cache (`src/components/songs/metadata/AlbumArt.tsx`)
- Module-level `Set<string>` tracks loaded image URLs
- On remount (e.g., virtualized row recycling), skips spinner/opacity transition if already loaded
- Uses ref callback to detect browser-cached images (`img.complete && img.naturalWidth > 0`)
- All album art `<img>` tags use `loading="lazy"` (native browser lazy loading)

---

## Page State Cache (`src/api/pageCache.ts`)

Module-level `Map` caches for back-navigation restoration (NOT data caches):

| Cache | Key | Stores |
|---|---|---|
| `songDetailCache` | songId | instrumentData, scoreHistory, accountId, scrollTop |
| `leaderboardCache` | comboId | entries, totalEntries, localEntries, page, scrollTop |
| `rankingsCache` | metric+instrument | page, scrollTop |

- All cleared when settings change (App.tsx lines 248–262)
- Used to restore scroll position and skip stagger animations on back-navigation
- Scroll positions also tracked separately in `useScrollRestore` (module Map)

---

## Known Bottlenecks & Opportunities

### Current Limitations
1. **No concurrent rendering** — `useTransition`, `useDeferredValue`, `startTransition` are unused despite React 19. Filtering operations on SongsPage (700+ songs) and SuggestionsPage could benefit from deferred updates.
2. **No bundle analysis** — No `rollup-plugin-visualizer` or similar tool configured. Unknown vendor chunk sizes.
3. **No responsive images** — Album art served as single format from CDN. No `<picture>`, `srcSet`, or WebP/AVIF fallbacks.
4. **No service worker** — No offline support or asset precaching. PWA flags used only for UI layout detection (`IS_PWA`).
5. **InfiniteScroll edge case** — SuggestionsPage: when filters hide items without changing container scroll height, InfiniteScroll may not fire the next load.
6. **Single Suspense boundary** — All lazy routes share one boundary. Nested boundaries per feature area could show partial UI sooner.
7. **No chunk prefetch/preload** — No `<link rel="modulepreload">` hints for likely next routes. No hover-based prefetch.

### Performance Wins Already In Place
1. **Virtual scrolling** on the two heaviest lists (SongsPage, PlayerHistoryPage)
2. **ETag caching** with localStorage persistence for songs (instant cold start)
3. **WebSocket for shop** eliminates polling
4. **Stagger animation cleanup** removes `willChange` after animation to free compositor layers
5. **AlbumArt dedup** prevents flash-of-spinner on virtualized row remount
6. **Adaptive polling** that respects tab visibility
7. **Aggressive static asset caching** (1 year, immutable) via nginx
8. **Context value memoization** across all 9 context providers
9. **200+ useMemo / 193 useCallback** instances preventing unnecessary renders
10. **22 React.memo components** on high-frequency list items

### Light Trails Performance Note
Settings page describes light trails hover effect: "disabling may improve performance" — acknowledged as potentially expensive on lower-end devices.
