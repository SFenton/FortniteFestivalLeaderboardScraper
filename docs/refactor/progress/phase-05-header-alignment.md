# Phase 5: Header Alignment

**Status:** ⬜ Not Started
**Depends on:** Phase 2
**Parallel with:** Phases 3, 4, 9, 10

## Goal
Every page's top-level content left-aligns with the sidebar's "Songs" link on wide desktop.

## Steps

- [ ] Establish alignment contract in `<Page>`: `before` slot applies same `max-width + padding-h` as content
- [ ] Ensure `<PageHeader>` inherits alignment (no own max-width/padding)
- [ ] Audit SongsPage: search/toolbar → `<Page before={}>`
- [ ] Audit SuggestionsPage: filter pill → `<Page before={}>`
- [ ] Audit PlayerPage: player name bar → `<PageHeader>`
- [ ] Audit RivalsPage: sticky title → `<PageHeader>` in `<Page before={}>`
- [ ] Audit RivalDetailPage: sticky title → `<PageHeader>`
- [ ] Audit RivalCategoryPage: sticky title → `<PageHeader>`
- [ ] Audit AllRivalsPage: title → `<PageHeader>`
- [ ] Audit SettingsPage: heading → `<PageHeader>`
- [ ] Audit ShopPage: toolbar → `<Page before={}>`
- [ ] Audit LeaderboardPage: SongInfoHeader → `<Page before={}>`
- [ ] Audit SongDetailPage: SongDetailHeader → `<Page before={}>`
- [ ] Verify `--layout-padding-h-pinned` aligns content with sidebar items
- [ ] Remove per-page `max-width` / `padding-h` / `margin: 0 auto` from header CSS

## Verification Checks

- [ ] Wide desktop: all page headers align at same x-coordinate
- [ ] That x-coordinate matches sidebar "Songs" link left edge
- [ ] Mobile: alignment unaffected (full-width padding)
- [ ] All existing tests pass
