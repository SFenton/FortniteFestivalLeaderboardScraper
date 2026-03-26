# Phase 7: Testability — Eliminate All v8 ignore

**Status:** ⬜ Not Started
**Depends on:** Nothing
**Blocking:** Phase 8

## Goal
Remove every `/* v8 ignore */` directive from the codebase (~200 markers, ~40 files). CI ban enforced.

## Steps

### 7.1 — DOM Adapter Extraction
- [ ] Create `src/utils/domAdapters.ts` with thin wrappers (getScrollTop, raf, observeResize, etc.)
- [ ] Refactor useScrollRestore → import from domAdapters
- [ ] Refactor useHeaderCollapse → import from domAdapters
- [ ] Refactor useScrollMask → import from domAdapters
- [ ] Refactor useStaggerRush → import from domAdapters
- [ ] Refactor Sidebar.tsx → import rAF + getBoundingClientRect adapters
- [ ] Refactor FirstRunCarousel.tsx (13 blocks) → import adapters
- [ ] Refactor AnimatedBackground.tsx → import webAnimate adapter
- [ ] Refactor FloatingActionButton.tsx → import adapters + isPWA()
- [ ] Refactor BottomNav.tsx → import isPWA()
- [ ] Refactor useVisualViewport.ts → import adapters
- [ ] Refactor AlbumArt.tsx, BackgroundImage.tsx → import imageOnLoad
- [ ] Refactor useChartDimensions.ts → import observeResize
- [ ] Refactor ConfirmAlert.tsx → import animation adapters
- [ ] Refactor FRE demo files (InstrumentFilter, CategoryCard, InfiniteScroll)

### 7.2 — Decompose App.tsx
- [ ] Extract DesktopLayout.tsx
- [ ] Extract MobileLayout.tsx
- [ ] Extract RouteFabConfig.tsx (FAB per-route config)
- [ ] Extract ScrollToTop.tsx
- [ ] Slim AppShell to ~50 lines

### 7.3 — Bundler/Framework Blocks
- [ ] Add test-time `lazy()` replacement or validate import paths
- [ ] Verify `__APP_VERSION__` / `__BUILD_TIME__` defined in test config

### 7.4 — Remove Defensive Guards
- [ ] SearchQueryContext.tsx — delete unreachable factory
- [ ] AnimatedBackground.tsx — tighten type or remove guard
- [ ] InstrumentFilterDemo.tsx — remove 4 impossible-state guards
- [ ] ErrorBoundary.tsx — add test that triggers getDerivedStateFromError

### 7.5 — Write Missing Async Tests
- [ ] useSyncStatus.ts — mock API, test polling with fake timers
- [ ] useShopWebSocket.ts — mock WebSocket, test messages + reconnect
- [ ] PlayerDataContext.tsx — extend to cover sync invalidation
- [ ] RivalsPage.tsx — extend to cover all 3 fetch blocks
- [ ] useDemoSongs.ts — provide mock contexts, test timer rotation
- [ ] useFilteredSongs.ts — test percentile cap + sort tiebreaker
- [ ] SuggestionsPage.tsx — remove v8 ignore (42 integration tests cover it)
- [ ] SettingsPage.tsx — test LeewaySlider, DnD, version fetch

### 7.6 — CI Ban
- [ ] Add `grep -rn "v8 ignore" src/ && exit 1` to CI pipeline
- [ ] Optionally add ESLint no-restricted-syntax rule

## Verification Checks

- [ ] `grep -rn "v8 ignore" FortniteFestivalWeb/src/` → zero matches
- [ ] Coverage stays at 95% per-file with NO v8 ignore blocks
- [ ] CI pipeline enforces the ban
- [ ] All existing tests pass
