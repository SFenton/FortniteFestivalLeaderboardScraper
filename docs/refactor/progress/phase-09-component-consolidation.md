# Phase 9: Component Consolidation

**Status:** ⬜ Not Started
**Depends on:** Nothing
**Parallel with:** Phases 1-5, 7

## Goal
Unify pagination patterns, consolidate modal logic, adopt underutilized shared components.

## Steps

### 9.1 — Paginator Component
- [ ] Create `<Paginator>` with 3 modes: dots, arrows, numbered
- [ ] Add optional keyboard support (ArrowLeft/Right)
- [ ] Add optional touch swipe support
- [ ] Refactor FirstRunCarousel → use `<Paginator mode="dots">`
- [ ] Refactor LeaderboardPage → use `<Paginator mode="numbered">`
- [ ] Refactor InstrumentSelector compact → use `<Paginator mode="arrows">`

### 9.2 — Modal Draft State Hook
- [ ] Create `useModalDraft<T>(draft, savedDraft, onCancel)` hook
- [ ] Refactor SortModal → useModalDraft
- [ ] Refactor FilterModal → useModalDraft
- [ ] Refactor PlayerScoreSortModal → useModalDraft
- [ ] Refactor SuggestionsFilterModal → useModalDraft

### 9.3 — Adopt Existing Components
- [ ] Refactor SortModal + PlayerScoreSortModal → use `<DirectionSelector>` (remove inline reimpl)
- [ ] Refactor FilterModal + SuggestionsFilterModal → use `<InstrumentSelector>` (remove reimpl)
- [ ] Audit empty state usage → adopt `<EmptyState>` / `<PageMessage>` consistently

### 9.4 — StatusRow Component
- [ ] Create `<StatusRow>` — frostedCard + status tint gradient + button semantics
- [ ] Refactor RivalRow → compose from `<StatusRow>`
- [ ] Refactor RivalSongRow → compose from `<StatusRow>`

### 9.5 — ToggleAll Hook
- [ ] Create `useToggleAll(map, keys)` → `{ allOn, toggle }`
- [ ] Refactor FilterModal → useToggleAll
- [ ] Refactor SuggestionsFilterModal → useToggleAll

## Verification Checks

- [ ] All pagination UI looks identical
- [ ] Modal confirm-discard flow works the same
- [ ] `grep -r "JSON.stringify(draft)" src/pages/` → only in useModalDraft hook
- [ ] All existing tests pass
