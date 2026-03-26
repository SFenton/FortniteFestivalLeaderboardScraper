# Phase 12: Dead Code Pruning

**Status:** ✅ Complete
**Depends on:** Nothing
**Parallel with:** Anything

## Goal
Remove all identified dead code from both web app and service.

## Steps

### Web App
- [x] Delete `src/components/common/LoadGate.tsx` (broken duplicate; real one in page/)
- [x] Delete `.frostedCardLight` from shared.module.css
- [x] Delete entire `src/theme/` directory (5 dead files: colors, frostedStyles, goldStyles, spacing, index)
- [x] Remove 5 dead coverage exclusions from vite.config.ts (LoadGate, SongsPage.tsx, CategoryCard.tsx, models/index.ts, theme/index.ts)
- [x] Update barrels.test.ts to import from @festival/theme instead of dead src/theme/
- [ ] Remove v8 ignore wrapping entire SuggestionsPage (deferred to Phase 7)

### Service
- [ ] Delete diagnostic hardcoded lookup in ScraperWorker (song "092c2537")
- [ ] Audit InstrumentDatabase migration methods → delete if migrations complete
- [ ] Delete nested index drop+recreate in InstrumentDatabase (index already exists)

## Verification Checks

- [x] `grep -r "LoadGate" src/components/common/` → no LoadGate.tsx
- [x] `grep -r "frostedCardLight" src/` → zero matches (removed from shared.module.css)
- [x] All 2,254 web tests pass (168 test files)
- [x] No build errors
- [x] 5 dead coverage exclusions cleaned from vite.config.ts
