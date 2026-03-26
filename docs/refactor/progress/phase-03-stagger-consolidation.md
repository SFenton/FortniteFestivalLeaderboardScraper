# Phase 3: Stagger & Animation Consolidation

**Status:** ✅ Complete (hooks created + 6 pages migrated; 4 specialized pages deferred)
**Depends on:** Phase 2
**Parallel with:** Phases 4, 5, 9, 10

## Goal
Eliminate per-page module-level render flags, caches, and duplicated stagger logic. Replace with unified hooks.

## Steps

### New Hooks
- [ ] Create `usePageTransition(cacheKey, isReady)` → `{ phase, shouldStagger, fadeStyle, clearAnim }`
- [ ] Create `useStagger(shouldStagger, interval?)` → `{ next(), forIndex(i), clearAnim }`

### New Cache Registry
- [ ] Create `src/api/pageCacheRegistry.ts` — typed `Map<string, T>` replacing 6 module-level caches

### Page Shell Updates
- [ ] Add `<Page loading={...} spinnerFading={...}>` — renders spinner overlay internally
- [ ] Add return-visit 200ms fade wrapper to `<Page>`

### Page Migrations (remove manual stagger + cache)
- [ ] RivalsPage — remove `_rivalsHasRendered`, `_cachedInstrumentRivals`, manual stagger counter
- [ ] RivalDetailPage — remove `_detailHasRendered`, `_cachedDetailSongs`, manual stagger
- [ ] RivalryPage — remove `_rivalryHasRendered`, `_cachedRivalrySongs`
- [ ] AllRivalsPage — remove `_allRivalsHasRendered`, `_cachedInstrumentData`
- [ ] SettingsPage — remove `_hasRendered`
- [ ] SongDetailPage — remove manual stagger logic + cache checks
- [ ] LeaderboardPage — remove `animMode` + manual stagger logic
- [ ] SongsPage — migrate to `usePageTransition`
- [ ] SuggestionsPage — migrate to `usePageTransition`
- [ ] ShopPage — migrate to `usePageTransition`
- [ ] PlayerPage — remove `_renderedPlayerAccount`

## Verification Checks

- [ ] First visit to any page: spinner → stagger animation (identical to today)
- [ ] Return visit (back-nav): 200ms fade, scroll position restored, no per-row stagger
- [ ] `grep -r "_hasRendered\|_cached" src/pages/` → zero
- [ ] `grep -r "spinnerOverlay" src/pages/**/*.module.css` → zero
- [ ] All existing tests pass
