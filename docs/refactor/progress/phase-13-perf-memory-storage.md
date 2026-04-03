# Phase 13: Performance, Memory & Storage

**Status:** ⬜ Not Started
**Depends on:** Nothing
**Parallel with:** Anything

## Goal
Fix performance bottlenecks, memory leaks, and storage inefficiencies across both products.

## Steps

### Web App Performance
- [ ] Fix `useScrollFade`: replace per-child style writes with IntersectionObserver
- [ ] Migrate unbounded `pageCache` Maps to React Query (auto-eviction)
- [ ] Fix `AnimatedBackground` biased shuffle → Fisher-Yates
- [ ] Split `FestivalContext` or use React Query `select` for fine-grained subscriptions
- [ ] Deduplicate `ShopContext` (shopUrlMap + shopSongs)
- [ ] Move frosted-glass SVG noise to single page-level composite layer
- [ ] Remove `will-change: scroll-position` from SongsPage

### Service Performance
- [ ] Fix unbounded ConcurrentBag in pipeline → chunked DB inserts
- [ ] Fix double materialization in MetaDatabase pagination
- [ ] Cache PRAGMA result in static helper (avoid per-connection no-op)
- [ ] Pre-compile high-frequency statements (UPSERT, SELECT)
- [ ] Add configurable `BoundedChannelCapacity` to ScraperOptions

### Storage Optimization
- [ ] Normalize difficulty columns (score-level → song-level): ~480 MB savings
- [ ] Implement TTL-based percentile refresh for accuracy
- [ ] Evaluate 4-column composite PK in UserRivals: ~800 MB savings

## Verification Checks

- [ ] Chrome DevTools Performance: improved paint time on Songs page
- [ ] No `ConcurrentBag` in pipeline code
- [ ] `dotnet test` + `vitest` all pass
- [ ] Storage audit shows reduced DB sizes after normalization
