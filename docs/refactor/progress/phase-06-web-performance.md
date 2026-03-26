# Phase 6: Web Performance

**Status:** ⬜ Not Started
**Depends on:** Nothing
**Parallel with:** Anything

## Goal
Fix moderate-to-severe performance gaps in the web app.

## Steps

- [ ] Remove `will-change: scroll-position` from SongsPage `.scrollArea`
- [ ] Move frosted-glass SVG noise to single page-level `::before` pseudo (not per-element)
- [ ] Extract `albumArtMap`/`yearMap` from RivalDetailPage + RivalryPage into FestivalContext derived state
- [ ] Migrate module-level page caches to React Query (`staleTime: Infinity`, auto-gc)
- [ ] Evaluate replacing `react-infinite-scroll-component` with `useInfiniteQuery` + IntersectionObserver
- [ ] Fix `useScrollFade`: replace per-child style writes with IntersectionObserver
- [ ] Fix `AnimatedBackground`: replace biased `.sort(() => Math.random() - 0.5)` with Fisher-Yates
- [ ] Split `FestivalContext` to reduce tree-wide re-renders (separate songs vs season queries)
- [ ] Deduplicate `ShopContext` filtered collections (shopUrlMap + shopSongs)

## Verification Checks

- [ ] Chrome DevTools Performance: paint time per frame on Songs page (50+ rows) decreased
- [ ] No `let _cached` variables remain in page files
- [ ] React Query DevTools shows page data in query cache
- [ ] Smooth 60fps scroll on songs page with 500+ items
- [ ] All existing tests pass
