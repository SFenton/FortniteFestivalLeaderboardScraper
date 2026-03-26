# Phase 16: CSS Modules → useStyles() + Theme Restructure

**Status:** ⬜ Not Started
**Depends on:** Phases 1-5 (architecture stable before migrating styles)
**Parallel with:** 7, 8, 11, 14, 15

## Goal
Eliminate all 96 .module.css files. All styling via `useStyles()` hook + `@festival/theme` constants. Theme package restructured for RN compatibility.

## Steps

### 16.1 — Restructure @festival/theme
- [ ] Split `colors.ts` → `colors/base.ts`, `colors/semantic.ts`, `colors/components.ts`, `colors/dataViz.ts`, `colors/index.ts`
- [ ] Split `spacing.ts` → `typography.ts`, `spacing.ts` (gap/opacity only), `sizing.ts`, `layout.ts`, `surfaces.ts`
- [ ] Move `SWIPE_THRESHOLD` out of `animation.ts` → new `interaction.ts`
- [ ] Create `zIndex.ts` (background, base, dropdown, popover, modalOverlay)
- [ ] Extend Font (add 2xl, display)
- [ ] Add all CSS-only constants (carousel sizing, font weights, etc.) to appropriate modules
- [ ] Create `factories.ts` (flexColumn, flexRow, flexCenter, textBold, textSemibold, truncate)
- [ ] Verify barrel export (index.ts) re-exports everything
- [ ] Verify every file ≤ 80 lines

### 16.2 — Delete Dead Web Theme Files
- [ ] Delete `src/theme/colors.ts`
- [ ] Delete `src/theme/frostedStyles.ts`
- [ ] Delete `src/theme/goldStyles.ts`
- [ ] Delete `src/theme/spacing.ts`
- [ ] Delete `src/theme/index.ts`

### 16.3 — Delete CSS Custom Properties
- [ ] Delete `src/styles/theme.css`
- [ ] Delete `src/styles/animations.css`
- [ ] Delete `src/styles/shared.module.css`
- [ ] Create minimal `src/styles/keyframes.css` (~15 lines: fadeInUp, fadeOut, spin)
- [ ] Slim `src/index.css` to ~30 lines (resets, font imports, keyframe import)

### 16.4 — Create useStyles Hook
- [ ] Create `useStyles<T>(factory)` hook (useMemo-based)
- [ ] Add to appropriate location (web-specific or theme package)

### 16.5 — Migrate CSS Modules (10 batches)

**Batch 1: Single-rule files (22 files)**
- [ ] Inline 1-4 rule files into components, delete .module.css

**Batch 2: Shared utilities (4 files)**
- [ ] Convert shared.module.css patterns to JS factories
- [ ] Convert songRow.module.css
- [ ] Convert animations.css references

**Batch 3: Common components (11 files)**
- [ ] Accordion → useStyles
- [ ] ActionPill → useStyles
- [ ] ArcSpinner → useStyles
- [ ] DirectionSelector → useStyles
- [ ] InstrumentSelector → useStyles
- [ ] LoadGate → useStyles
- [ ] PaginationButton → useStyles
- [ ] RadioRow → useStyles
- [ ] SearchBar → useStyles
- [ ] SectionHeader → useStyles
- [ ] ToggleRow → useStyles

**Batch 4: Display components (4 files)**
- [ ] InstrumentChip → useStyles
- [ ] InstrumentHeader → useStyles
- [ ] InstrumentIcons → useStyles

**Batch 5: Song metadata (7+ files)**
- [ ] AccuracyDisplay, AlbumArt, DifficultyBars, GoldStars, MiniStars, PercentilePill, ScorePill, SeasonPill, SongInfo → useStyles

**Batch 6: Shell components (9 files)**
- [ ] AnimatedBackground, DesktopNav, Sidebar, PinnedSidebar, MobileHeader, BottomNav, FAB, BackLink → useStyles

**Batch 7: Modal system (5 files)**
- [ ] Modal, ConfirmAlert, ChangelogModal, ModalShell, BulkActions → useStyles

**Batch 8: Page components (12 files)**
- [ ] Songs, SongDetail, Leaderboard, PlayerHistory, Player, Rivals (4), Suggestions, Shop, Settings → useStyles

**Batch 9: Page sub-components (22 files)**
- [ ] Row components, chart components, path components, filter/sort modals, FRE demos → useStyles

**Batch 10: App shell (1 file)**
- [ ] App.module.css → useStyles

### 16.6 — Prune Dead Styles
- [ ] Audit every class during migration: is it actually used?
- [ ] Remove styles duplicating theme constants
- [ ] Replace responsive @media with conditional hooks (useIsMobile, etc.)

### 16.7 — Update ESLint
- [ ] Remove `react/forbid-dom-props` warning on `style`
- [ ] Add warn rule on `.module.css` imports (catch leftovers)

## Verification Checks

- [ ] Zero `.module.css` files in `src/`
- [ ] All styling via `useStyles()` or inline `style={}` backed by theme constants
- [ ] `@festival/theme` modules are each ≤ 80 lines
- [ ] React Native can import any theme module without web dependencies
- [ ] ESLint warns on `.module.css` imports
- [ ] ~50 lines of global CSS remaining (keyframes + resets)
- [ ] Visual regression: all pages identical
- [ ] All tests pass
