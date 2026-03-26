# Phase 1: Scroll Model — Browser-Native Scroll

**Status:** ✅ Complete
**Depends on:** Nothing
**Blocking:** Phase 2

## Goal
Eliminate per-page scroll containers. Browser scrollbar controls all scrolling. Sidebar stays sticky on wide desktop.

## Steps

- [x] Remove `height: 100dvh; overflow: hidden` from `.shell` in App.module.css → `min-height: 100dvh`
- [x] Remove `overflow-y: auto; overscroll-behavior: contain` from `.content` in App.module.css
- [x] Update PinnedSidebar CSS: `position: sticky; top: 0; height: 100dvh; align-self: flex-start`
- [x] Remove `overflow-y: auto` from `.scrollContainer` in shared.module.css → `min-height: 0`
- [x] Update `<Page>` — useScrollMask/useStaggerRush now auto-listen to window; removed manual onScroll wiring
- [x] Adapt `useScrollRestore` → `window.scrollY` / `window.scrollTo()` + auto window scroll listener
- [x] Adapt `useHeaderCollapse` → `window.scrollY` + auto window scroll listener
- [x] Adapt `useScrollMask` → window scroll listener with `getBoundingClientRect` viewport check
- [x] Adapt `useStaggerRush` → auto window scroll listener
- [x] Update `ScrollToTop` in App.tsx → `window.scrollTo(0, 0)`
- [ ] Verify modals still scroll independently (`position: fixed` + internal overflow)

## Test Results
- [x] Updated useScrollRestore tests for new window-based API
- [x] Updated useHeaderCollapse tests for new window-based API
- [x] All 2,254 tests pass across 168 test files

## Verification Checks

- [ ] Wide desktop: scrolling anywhere (sidebar, content, spacer) scrolls page
- [ ] Sidebar stays fixed on wide desktop during scroll
- [ ] Mobile: scroll behavior unchanged
- [ ] Modals have independent scroll, don't scroll page behind
- [ ] `useScrollRestore` saves/restores position on back-nav
- [ ] `useHeaderCollapse` triggers at 40px threshold
- [ ] `useStaggerRush` collapses pending animations on first scroll
- [ ] iOS Safari: no body bounce/overscroll issues
- [ ] All existing unit tests pass
