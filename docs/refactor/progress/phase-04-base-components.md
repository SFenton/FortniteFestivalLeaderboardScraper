# Phase 4: Base Components

**Status:** ⬜ Not Started
**Depends on:** Phase 2
**Parallel with:** Phases 3, 5, 9, 10

## Goal
Extract shared visual primitives so pages compose from them instead of re-declaring styles.

## Steps

### New Components
- [ ] Create `<FrostedCard>` — visual surface (background, border, shadow, radius). Props: variant, radius, className, as
- [ ] Create `<SectionCard>` — extends FrostedCard with header slot, optional clickable header/chevron
- [ ] Create `<PageHeader>` — title + subtitle + right actions. Respects Page alignment contract. Optional sticky
- [ ] Create `<ItemList>` — flex-column, gap: 2px, optional container-type: inline-size
- [ ] Create `<EmptyState>` — centered title + subtitle + optional icon

### Refactor Rows to Compose from FrostedCard
- [ ] SongRow → `<FrostedCard as={Link}>`
- [ ] RivalRow → `<FrostedCard>` + gradient overlay
- [ ] RivalSongRow → `<FrostedCard>`
- [ ] LeaderboardEntry → `<FrostedCard as={Link}>`
- [ ] ShopCard → `<FrostedCard variant="light">`
- [ ] CategoryCard → `<SectionCard>` for outer shell

### Cleanup
- [ ] Delete per-page `.card { composes: frostedCard }` from all page CSS modules
- [ ] Delete per-page `.emptyState` / `.emptyTitle` / `.emptySubtitle` CSS
- [ ] Delete per-page `.rivalList` / `.songList` / `.list` CSS

## Verification Checks

- [ ] No page CSS module defines `.card { composes: frostedCard }` — only FrostedCard.module.css
- [ ] `<FrostedCard>` used in every surface component
- [ ] `<PageHeader>` used by every page with a top-level title/toolbar
- [ ] Visual regression: all pages identical appearance
- [ ] All existing tests pass
