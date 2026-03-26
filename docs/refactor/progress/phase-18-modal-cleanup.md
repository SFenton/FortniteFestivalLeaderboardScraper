# Phase 18: Modal Style & Structure Cleanup

**Status:** ⬜ Not Started

## Goal

Bring all modal components into alignment with the project's `useStyles()` + `useMemo` convention (established in Phase 16), eliminate magic numbers in favor of `@festival/theme` tokens, extract duplicated styles/patterns into shared components, and consolidate modal shell usage so no modal builds its own overlay/panel/animation from scratch.

## Current State (Audit)

### Style Approach by Modal

| File | Current Approach | Compliant? |
|---|---|---|
| `ChangelogModal.tsx` | `useStyles(animIn)` + `useMemo` | **Yes** |
| `ConfirmAlert.tsx` | `useStyles(animIn)` + `useMemo` | **Yes** |
| `Modal.tsx` | Imports module-level `modalStyles` | No |
| `ModalShell.tsx` | Imports module-level `modalStyles` | No |
| `ModalSection.tsx` | Imports module-level `modalStyles` | No |
| `BulkActions.tsx` | Imports module-level `modalStyles` | No |
| `SortModal.tsx` | Module-level `const directionStyles` | No |
| `PlayerScoreSortModal.tsx` | Module-level `const directionStyles` | No |
| `FilterModal.tsx` | No local styles; uses `modalStyles` + `filterStyles` | Inherits non-compliant deps |
| `SuggestionsFilterModal.tsx` | No local styles; uses `filterStyles` | Inherits non-compliant deps |
| `PathsModal.tsx` | `usePathsModalStyles()` for controls; inline consts for shell | Partial |
| `MobilePlayerSearchModal.tsx` | `useModalSearchStyles()` + module-level consts | Partial |

### Magic Numbers Inventory

**modalStyles.ts** (~15 magic numbers):
- `zIndex: 1001` — panel z-index (should be `ZIndex` token)
- `width: 32, height: 32` — close button (should use existing `Layout.closeBtnSize`)
- `width: 18, height: 18` — radio dot (need `Size.radioDot` token)
- `width: 36, height: 20, borderRadius: 10` — toggle track (need `Size.toggleTrack*` tokens)
- `width: 16, height: 16, top: 2, left: 2` — toggle thumb (need `Size.toggleThumb*` tokens)
- `width: 44, height: 24, borderRadius: 12` — large toggle track (need `Size.toggleTrackLg*` tokens)
- `width: 20, height: 20, left: 22` — large toggle thumb

**SortModal.tsx / PlayerScoreSortModal.tsx** (~5 each):
- `size={20}` — arrow icons (should be `Size.iconDefault`)
- `marginRight: -12` — icons negative margin
- `width: 40, height: 40` — icon button (should be `Size.iconBtn`)
- `fontWeight: 700` — should be `Weight.bold`

**PathsModal.tsx** (~12):
- `TRANSITION_MS = 300` — redefined locally (already in `@festival/theme`)
- `width: 64, height: 64` — instrument buttons (should match `filterStyles`)
- `'#2ECC71'` — hardcoded green (should be `Colors` token)
- `maxHeight: 160 / 120` — magic accordion heights
- `size={18/28/16/48}` — various icon sizes
- `vvHeight * 0.2, vvHeight * 0.8, '90vw', '90vh'` — panel sizing differs from ModalShell

**MobilePlayerSearchModal.tsx** (~4):
- `width: 420, height: 600` — desktop modal size
- `height: 48` — search pill height
- `400ms, 300ms, 50ms` — animation/stagger durations

### Duplicated Styles & Patterns

| Duplication | Files | Opportunity |
|---|---|---|
| `directionStyles` (inner, textCol, title, hint, icons, iconBtn, iconCircle, iconCircleActive) | SortModal, PlayerScoreSortModal | Extract `<DirectionPicker>` component or shared `directionStyles` |
| Instrument picker (instrumentRow, instrumentBtn, instrumentCircle/Active, instrumentIconWrap) | filterStyles.ts, PathsModal.tsx | PathsModal should reuse filterStyles; delete local duplicates |
| Inline arrow icon style (`position: relative, zIndex: 1, color conditional, transition`) | SortModal ×2, PlayerScoreSortModal ×2 | Part of DirectionPicker extraction |
| Own overlay/panel/animation/keyboard shell | PathsModal, ChangelogModal, ConfirmAlert | PathsModal → ModalShell; ChangelogModal + ConfirmAlert → CenteredDialogShell (or keep as-is since centered dialogs differ from flyout/sheet) |

### Modal Shell Usage

| Modal | Shell | Notes |
|---|---|---|
| SortModal | `<Modal>` → `<ModalShell>` | Correct |
| FilterModal | `<Modal>` → `<ModalShell>` | Correct |
| SuggestionsFilterModal | `<Modal>` → `<ModalShell>` | Correct |
| PlayerScoreSortModal | `<Modal>` → `<ModalShell>` | Correct |
| MobilePlayerSearchModal | `<ModalShell>` directly | Correct (no apply/reset footer) |
| **PathsModal** | **Builds own shell** | Should use `<ModalShell>` with size overrides |
| **ChangelogModal** | **Builds own shell** | Centered dialog — consider `<CenteredDialogShell>` |
| **ConfirmAlert** | **Builds own shell** | Centered dialog — same |

---

## Steps

### 18.1 — Add Missing Theme Tokens

Add tokens to `@festival/theme` for all magic numbers found in the audit:

- [ ] `ZIndex.modalPanel` (1001)
- [ ] `Size.radioDot` (18)
- [ ] `Size.toggleTrackW` (36), `Size.toggleTrackH` (20), `Size.toggleTrackRadius` (10)
- [ ] `Size.toggleThumb` (16), `Size.toggleThumbOffset` (2), `Size.toggleThumbOnLeft` (18)
- [ ] `Size.toggleTrackLgW` (44), `Size.toggleTrackLgH` (24), `Size.toggleTrackLgRadius` (12)
- [ ] `Size.toggleThumbLg` (20), `Size.toggleThumbLgOnLeft` (22)
- [ ] `Size.directionIconBtn` (40), `Size.directionIconBtnMargin` (-12)
- [ ] `Size.pathsInstrumentBtn` (64), `Size.pathsInstrumentIcon` (48)
- [ ] `Size.pathsAccordionInst` (160), `Size.pathsAccordionDiff` (120)
- [ ] `Size.starSmall` (14)
- [ ] `Layout.searchModalDesktopW` (420), `Layout.searchModalDesktopH` (600), `Layout.searchPillH` (48)

### 18.2 — Migrate modalStyles.ts → useStyles

- [ ] Convert `modalStyles.ts` from module-level `const` export to a `useModalStyles()` factory or keep as module-level (no reactive deps) but replace all magic numbers with theme tokens
- [ ] Update all consumers (Modal, ModalShell, ModalSection, BulkActions, RadioRow, ToggleRow, Accordion, ReorderList, SortableRow, SettingsPage, PathsModal)

### 18.3 — Migrate filterStyles.ts → useStyles

- [ ] Same treatment: replace magic numbers with theme tokens
- [ ] Update consumers (FilterModal, SuggestionsFilterModal, InstrumentSelector)

### 18.4 — Extract Shared DirectionPicker

- [ ] Create `DirectionPicker` component (or shared `directionPickerStyles`) from the duplicated `directionStyles` in SortModal and PlayerScoreSortModal
- [ ] Replace magic numbers (`size={20}`, `width: 40`, `marginRight: -12`, `fontWeight: 700`) with theme tokens
- [ ] Use the shared component/styles in both SortModal and PlayerScoreSortModal
- [ ] Delete `directionStyles` from both files

### 18.5 — Deduplicate PathsModal Instrument Picker

- [ ] Replace PathsModal's local instrument picker styles with `filterStyles` imports
- [ ] Replace hardcoded `'#2ECC71'` with `Colors` token (or parameterize filterStyles if the color differs intentionally)
- [ ] Replace local `TRANSITION_MS = 300` with `@festival/theme` import
- [ ] Replace remaining magic numbers (icon sizes, accordion heights)

### 18.6 — Migrate PathsModal to ModalShell

- [ ] Replace PathsModal's hand-built overlay/panel/animation/escape-key logic with `<ModalShell>` + `desktopStyle`/`mobileStyle` overrides for the 90vw/90vh sizing
- [ ] Move instrument picker + difficulty grid into ModalShell's `before` slot or children
- [ ] Verify mobile sheet animation and desktop centered dialog behavior match current UX

### 18.7 — Migrate Remaining Module-Level Styles

- [ ] SortModal: move any remaining local styles into `useStyles()` or shared imports
- [ ] PlayerScoreSortModal: same
- [ ] MobilePlayerSearchModal: consolidate module-level `searchPill` / `SEARCH_MODAL_DESKTOP` consts into its existing `useModalSearchStyles()`
- [ ] PathsModal: consolidate `usePathsModalStyles()` to cover all local styles (remove inline `const` objects for overlay/panel)

### 18.8 — Consider CenteredDialogShell

- [ ] Evaluate whether ChangelogModal and ConfirmAlert share enough shell code to warrant a `<CenteredDialogShell>` component (centered overlay, scale+fade animation, escape key)
- [ ] If yes, extract and migrate both
- [ ] If no, document the decision and keep them standalone

### 18.9 — Update Tests

- [ ] Update any test assertions referencing removed/changed style objects
- [ ] Verify all 2274+ tests pass
- [ ] Verify coverage stays above 95% threshold

---

## Phase 18 File Impact

| Metric | Estimate |
|---|---|
| `@festival/theme` tokens added | ~15-20 |
| Style files refactored | 2 (modalStyles.ts, filterStyles.ts) |
| Components extracted | 1-2 (DirectionPicker, optionally CenteredDialogShell) |
| Modal files updated | 8-10 |
| Duplicated style blocks removed | 3 (directionStyles ×2, PathsModal instrument picker) |
| Magic numbers eliminated | ~40 |
| Tests updated | 5-10 |

## Verification

- `grep -rn "1001\b" src/` returns zero matches in style objects (replaced by `ZIndex.modalPanel`)
- `grep -rn "directionStyles" src/` returns zero matches outside the shared component
- `grep -rn "#2ECC71" src/` returns zero matches (replaced by theme token)
- All modals using `<Modal>` or `<ModalShell>` — no hand-built overlays except centered dialogs (if CenteredDialogShell not adopted)
- No module-level `const styles` in any modal file (all `useStyles()` or shared imports)
- All tests pass, coverage ≥ 95%
