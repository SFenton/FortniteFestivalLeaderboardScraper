# Phase 10: Page Action Bar

**Status:** ⬜ Not Started
**Depends on:** Phase 2
**Parallel with:** Phases 3-5, 9

## Goal
Standardize the "action bar" pattern (filter/sort/modal-trigger pills) across pages.

## Steps

- [ ] Create `<PageActionBar>` component — left/right slots, optional search, optional sticky
- [ ] Add `fabActions` prop for auto-registering actions with mobile FAB
- [ ] Refactor SongsPage → `<PageActionBar search={...} right={sort + filter pills}>`
- [ ] Refactor SuggestionsPage → `<PageActionBar right={filter pill}>`
- [ ] Refactor ShopPage → `<PageActionBar left={title} right={view toggle}>`
- [ ] Refactor PlayerHistoryPage → `<PageActionBar right={sort pill}>`
- [ ] Remove per-page FAB registration boilerplate (fabSearch.registerXxxActions)

## Verification Checks

- [ ] All toolbar layouts look identical to before
- [ ] FAB still triggers same modals on mobile
- [ ] Desktop pills and mobile FAB stay in sync
- [ ] All existing tests pass
