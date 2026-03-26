# Phase 2: Universal Page Shell

**Status:** ✅ Complete
**Depends on:** Phase 1
**Blocking:** Phases 3, 4, 5, 10, 16

## Goal
Every page renders through `<Page>`. No page declares its own `.page`/`.scrollArea`/`.container` shell CSS triple.

## Steps

### Scroll Infrastructure Consolidation (Completed)
- [x] Remove dead `handleScroll` + `updateScrollMask` + `rushOnScroll` from all 10 pages
- [x] Remove dead `onScroll` prop from `<Page>` interface
- [x] Remove dead `saveScroll` assignments from 9 pages
- [x] All hooks (useScrollMask, useStaggerRush) now auto-listen to window

### Page Migrations
- [ ] Extend `<Page>` props: `stickyBefore`, `pageBackground`, `fabPadding`, `loading`, `spinnerFading`
- [ ] Migrate LeaderboardPage → `<Page before={<SongInfoHeader />} after={footer}>`
- [ ] Migrate PlayerHistoryPage → `<Page before={header}>`
- [ ] Migrate ShopPage → `<Page before={toolbar} containerClassName={...}>`
- [ ] Migrate SongDetailPage → `<Page variant="withBg" before={<SongDetailHeader />}>`
- [x] Migrate RivalsPage → `<Page before={header+spinner}>`
- [x] Migrate RivalDetailPage → `<Page before={header+spinner}>`
- [x] Migrate RivalryPage → `<Page before={header+spinner}>`
- [x] Migrate AllRivalsPage → `<Page before={header+spinner}>`
- [x] Migrate SettingsPage → `<Page containerClassName={...} after={modals}>`
- [ ] Migrate SuggestionsPage → `<Page before={filter}>`
- [ ] Migrate SongsPage → ensure fully on `<Page>`, no custom shell CSS
- [ ] Migrate PlayerPage → ensure fully on `<Page>`, no custom shell CSS

### Cleanup
- [x] Delete `.page` / `.scrollArea` / `.container` from SettingsPage.module.css
- [ ] Delete shell CSS from SuggestionsPage.module.css
- [x] Delete shell CSS from RivalsPage.module.css
- [x] Delete shell CSS from RivalDetailPage.module.css
- [x] Delete shell CSS from RivalCategoryPage.module.css
- [ ] Delete shell CSS from LeaderboardPage.module.css
- [ ] Delete shell CSS from PlayerHistoryPage.module.css
- [ ] Delete shell CSS from ShopPage.module.css
- [ ] Delete shell CSS from SongDetailPage.module.css
- [ ] Delete shell CSS from PlayerPage.module.css
- [ ] Delete `.spinnerOverlay` CSS from all page modules (now in `<Page>`)
- [ ] Delete shell CSS from SongsPage.module.css

## Verification Checks

- [ ] `grep -r "composes: pageShell" src/pages/` → zero (only Page.module.css)
- [ ] `grep -r "composes: scrollContainer" src/pages/` → zero
- [ ] All pages render identically (visual regression)
- [ ] All existing tests pass
