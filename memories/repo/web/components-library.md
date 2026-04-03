# FortniteFestivalWeb — Component Library Reference

> Last updated: 2026-04-03

## Directory Structure

```
src/components/
├── common/          # 17 files — Shared primitives used across all features
├── display/         # 3 files  — Instrument icon/badge rendering
├── songs/           # 3 dirs   — Song metadata display components
│   ├── cards/       # (empty)
│   ├── headers/     # SongInfoHeader + CSS module
│   └── metadata/    # 10 components — score, difficulty, stars, etc.
├── page/            # 6 files  — Page-level wrappers, error boundaries, loading
├── shell/           # 4+ files — App shell, navigation
│   ├── desktop/     # 6 files  — Desktop nav, sidebar, header search
│   ├── fab/         # 2 files  — Floating action button
│   └── mobile/      # 4 files  — Mobile header, bottom nav, back link
├── modals/          # 4 files  — Modal system
│   └── components/  # 3 files  — ModalShell, ModalSection, BulkActions
├── leaderboard/     # 2 files  — PaginatedLeaderboard + styles
├── player/          # 5 files  — Player-specific UI (search, stats, percentile)
├── routing/         # 1 file   — FeatureGate
├── sort/            # 3 files  — Drag-and-drop reorder (dnd-kit)
└── firstRun/        # 1 file   — FirstRunCarousel onboarding
```

**Total: ~60 component/style files across 11 directories**

---

## Component Catalog

### Common Components (`common/`)

| Component | Purpose | Key Props | Export |
|---|---|---|---|
| `Accordion` | Collapsible section with chevron | `title, hint?, icon?, defaultOpen?, children` | Named |
| `ActionPill` | Pill button for toolbar actions (sort/filter) | `icon, label, onClick, active?, dot?, className?, style?` | Named |
| `ArcSpinner` | Wall-clock-synced loading spinner | `size?: SpinnerSize, className?, style?` | Default |
| `DirectionSelector` | Asc/Desc toggle with animated circle | `ascending, onChange, title?, hint?` | Named (memo) |
| `EmptyState` | No-content placeholder (icon + title + subtitle) | `title, subtitle?, icon?, fullPage?, style?, className?` | Default |
| `FrostedCard` | Frosted-glass surface wrapper | `children, className?, style?` | Named (forwardRef) |
| `InstrumentSelector` | Instrument row w/ collapsible content | `instruments, selected, onSelect, required?, compact?, children` | Named (generic) |
| `MarqueeText` | Auto-scrolling overflow text | `text, as?, speed?, gap?, className?, style?` | Default |
| `Math` | KaTeX LaTeX renderer | `tex, block?` | Default |
| `PageHeader` | Standardized page header (title + actions) | `title?, subtitle?, actions?, style?, className?` | Default |
| `Paginator` | Prev/Next navigation layout | `onPrev?, onNext?, onSkipPrev?, onSkipNext?, keyboard?, children` | Named (compound: `.Dot`) |
| `RadioRow` | Single-choice row in modal forms | `label, hint?, selected, onSelect, onInfo?` | Named (memo) |
| `SearchBar` | Search input with icon | `value, onChange, placeholder?, enterKeyHint?, hideIcon?` | Default (forwardRef) |
| `SectionHeader` | Section title with description | `title, description?, flush?` | Default (memo) |
| `SuspenseFallback` | Route-level lazy loading spinner | *(none)* | Default |
| `ToggleRow` | Toggle switch row for modal forms | `label, hint?, value, onChange` | Named (memo) |

### Display Components (`display/`)

| Component | Purpose | Key Props | Export |
|---|---|---|---|
| `InstrumentChip` | Status badge (FC/score/none) with instrument icon | `instrument, hasScore, isFC, size?` | Named |
| `InstrumentHeader` | Icon + text header (5 size variants XS→XL) | `instrument, size, label?, subtitle?, iconOnly?` | Default |
| `InstrumentIcon` | Instrument PNG icon renderer | `instrument, size?, style?` | Named |

**Also exports:** `INSTRUMENT_STATUS_COLORS`, `getInstrumentStatusVisual()` utility

### Song Metadata Components (`songs/metadata/`)

| Component | Purpose | Key Props | Export |
|---|---|---|---|
| `AccuracyDisplay` | Formatted accuracy % with FC gold badge | `accuracy, isFullCombo?, fallback?` | Default |
| `AlbumArt` | Image with spinner, lazy-load, pulse animation | `src?, size, priority?, pulse?, pulseRed?` | Default |
| `DifficultyBars` | 7-bar SVG parallelogram meter | `level, raw?` | Default |
| `DifficultyPill` | E/M/H/X difficulty label badge | `difficulty` | Default |
| `GoldStars` | Horizontal row of gold star PNGs | `size?, count?` | Named |
| `MiniStars` | Compact bordered star circles (1-5) | `starsCount, isFullCombo` | Default |
| `PercentilePill` | Percentile tier display (Top 1%/5%) | `display` | Default |
| `ScorePill` | Formatted score with tabular nums | `score, width?, bold?, className?` | Default |
| `SeasonPill` | Season number badge (current highlighted) | `season, current?` | Default |
| `SongInfo` | Song thumbnail + title/artist summary | `albumArt?, title, artist, year?` | Default |

### Song Header (`songs/headers/`)

| Component | Purpose | Key Props | Export |
|---|---|---|---|
| `SongInfoHeader` | Full-width song detail header with scroll-collapse | `song, songId, collapsed, instrument?, actions?, animate?, shopUrl?, shopPulse?` | Default |

### Page Shell Components (`page/`)

| Component | Purpose | Key Props | Export |
|---|---|---|---|
| `BackgroundImage` | Faded background image layer | `src, dimOpacity?` | Default (memo) |
| `ErrorBoundary` | Root error boundary (class component) | `children, fallback?` | Default |
| `FadeIn` | Polymorphic fadeInUp animation wrapper | `as?, delay?, hidden?, children` | Default (memo) |
| `LoadGate` | Spinner→Content phase transition gate | `phase, fadeDuration?, overlay?, children` | Named |
| `RouteErrorFallback` | Crash recovery UI for lazy routes | *(none)* | Default |
| `SyncBanner` | Backfill/history progress banner | `phase, backfillProgress, historyProgress` | Default (memo) |

### Shell Components (`shell/`)

| Component | Purpose | Key Props | Export |
|---|---|---|---|
| `AnimatedBackground` | Ken Burns album art background cycle | `songs, dimOpacity?` | Default |
| `HamburgerButton` | Menu trigger button | `onClick, size?, style?` | Default |

#### Desktop (`shell/desktop/`)

| Component | Purpose | Key Props | Export |
|---|---|---|---|
| `DesktopNav` | Top navigation bar | `hasPlayer, onOpenSidebar, onProfileClick, isWideDesktop?` | Default |
| `HeaderProfileButton` | Profile icon in header | `hasPlayer, onClick` | Default |
| `HeaderSearch` | Player search w/ dropdown in header | *(none — self-contained)* | Default + `headerSearchStyles` |
| `PinnedSidebar` | Always-visible compact desktop sidebar | `player, onDeselect, onSelectPlayer` | Default + `usePinnedSidebarStyles` |
| `Sidebar` | Animated slide-out mobile sidebar | `player, open, onClose, onDeselect, onSelectPlayer` | Default |

#### FAB (`shell/fab/`)

| Component | Purpose | Key Props | Export |
|---|---|---|---|
| `FloatingActionButton` | Circular FAB with search + action menu | `mode, defaultOpen?, placeholder?, icon?, actionGroups?, onPress` | Default |
| `FABMenu` | Popup action group menu above FAB | `groups, visible, onAction` | Default |

#### Mobile (`shell/mobile/`)

| Component | Purpose | Key Props | Export |
|---|---|---|---|
| `BackLink` | History-based back navigation | `fallback, animate?` | Default |
| `BottomNav` | Bottom tab bar | `player, activeTab, onTabClick` | Default + `bottomNavCss` |
| `MobileHeader` | Conditional title/back header | `navTitle, backFallback, shouldAnimate, locationKey, songInstrument, isSongsRoute` | Default |
| `MobilePlayerSearchModal` | Full-screen player search modal | `visible, onClose, onSelect, player, onDeselect, title?` | Default |

### Modal System (`modals/`)

| Component | Purpose | Key Props | Export |
|---|---|---|---|
| `Modal` | Adaptive filter/sort modal (sheet on mobile, flyout on desktop) | `visible, title, onClose, onApply, onReset?, children` | Default |
| `ModalShell` | Base modal infra (overlay + panel + escape + animation) | `visible, title, onClose, children, transitionMs?` | Default |
| `ModalSection` | Grouped section with title/hint | `title?, hint?, children` | Named (memo) |
| `BulkActions` | Select All / Clear All button pair | `onSelectAll, onClearAll` | Named |
| `ChangelogModal` | Standalone changelog display | `onDismiss, onExitComplete?` | Default |
| `ConfirmAlert` | Yes/No confirmation dialog | `title, message, onNo, onYes` | Default |

### Leaderboard Components (`leaderboard/`)

| Component | Purpose | Key Props | Export |
|---|---|---|---|
| `PaginatedLeaderboard<T>` | Generic paginated leaderboard shell + animation lifecycle | `entries, page, totalPages, onGoToPage, renderRow, entryKey, isPlayerEntry, loading` | Named (generic) |

### Player Components (`player/`)

| Component | Purpose | Key Props | Export |
|---|---|---|---|
| `PlayerPercentileHeader` | Header row for percentile table | `percentileLabel, songsLabel` | Named (memo) |
| `PlayerPercentileRow` | Data row with percentile pill + count | `pct, count, isLast, onClick` | Named (memo) |
| `PlayerSearchBar` | Autocomplete search with dropdown | `onSelect, placeholder?, reopenOnFocus?` | Default |
| `SelectProfilePill` | Animated profile selection button | `visible, onClick` | Named |
| `StatBox` | Metric display card (label + value) | `label, value, color?, onClick?` | Default (memo) |

### Routing (`routing/`)

| Component | Purpose | Key Props | Export |
|---|---|---|---|
| `FeatureGate` | Feature flag guard — redirects if disabled | `flag: keyof FeatureFlags, children` | Default |

### Sort/Reorder (`sort/`)

| Component | Purpose | Key Props | Export |
|---|---|---|---|
| `ReorderList` | Drag-and-drop reorderable list (dnd-kit) | `items: ReorderItem[], onReorder` | Named |
| `SortableRow` | Individual draggable row | `item: ReorderItem` | Named |

### First Run (`firstRun/`)

| Component | Purpose | Key Props | Export |
|---|---|---|---|
| `FirstRunCarousel` | Onboarding slides with swipe/keyboard | `slides, onDismiss, onExitComplete?` | Default |

---

## Styling Patterns

### Distribution Summary

| Pattern | Count | Components |
|---|---|---|
| **Inline styles via `useStyles()` hook** | ~35 | Majority of components |
| **Shared style modules (`.ts`)** | 4 | `modalStyles.ts`, `sidebarStyles.ts`, `paginatedLeaderboardStyles.ts`, `playerPageStyles.ts` |
| **CSS Modules (`.module.css`)** | 2 | `MarqueeText.module.css`, `SongInfoHeader.module.css` |
| **Shared CSS Modules (from `styles/`)** | 2 | `effects.module.css` (SearchBar, BottomNav), `animations.module.css` (AlbumArt, SongInfoHeader) |
| **No styles (logic only)** | 1 | `FeatureGate.tsx` |

### Primary Pattern: `useStyles()` Hook

The dominant styling approach is a local `useStyles(deps)` function/hook returning a memoized `Record<string, CSSProperties>` object. This pattern:
- Accepts component state as arguments (e.g., `useStyles(isActive, size)`)
- Returns an object of named style entries (e.g., `{ container, title, pill }`)
- Uses `useMemo` for memoization keyed on reactive deps
- All values from `@festival/theme` token system (Colors, Font, Gap, Radius, etc.)

### Theme Token System (`@festival/theme`)

All inline styles use tokens from the shared theme package:
- **Surfaces:** `frostedCard`, `frostedCardSurface`, `frostedCardLight`, `modalOverlay`, `modalCard`, `purpleGlass`, `btnPrimary`, `btnDanger`
- **Layout:** `flexRow`, `flexColumn`, `flexCenter`, `flexBetween`, `truncate`, `absoluteFill`, `fixedFill`
- **Spacing:** `Gap.xs/sm/md/lg/xl`, `Padding.*`, `Layout.*`
- **Typography:** `Font.xs/sm/md/lg/xl`, `Weight.*`, `FontVariant.tabularNums`
- **Colors:** `Colors.textPrimary/Secondary/Muted`, `Colors.bg*`, `Colors.purple*`, `Colors.gold*`
- **Animation:** `transition()`, `TRANSITION_MS`, `QUICK_FADE_MS`, `fadeInUp` keyframe string
- **Enums:** `TextAlign`, `Overflow`, `ObjectFit`, `PointerEvents`, `CssValue.circle`

### CSS Modules (Minority)

Only used when CSS features are needed that inline styles can't express:
- **Keyframe animations**: `MarqueeText.module.css` (scroll), `SongInfoHeader.module.css` (scroll-linked collapse via `--collapse` CSS variable)
- **Pseudo-elements**: `effects.module.css` (placeholder styling, frosted nav)
- **Animation classes**: `animations.module.css` (albumPulse, spinnerCircle)
- **Reduced motion**: `@media (prefers-reduced-motion: reduce)` in MarqueeText

### Shared Style Modules (`.ts` exports)

Centralized style objects imported by multiple components:
- **`modalStyles`** — ~70 style entries consumed by Modal, ModalShell, ModalSection, BulkActions, RadioRow, ToggleRow, Accordion, ReorderList
- **`sidebarStyles`** — Sidebar navigation styles
- **`paginatedLeaderboardStyles` (`plbStyles`)** — Row, pagination, footer positioning
- **`playerPageStyles`** — Grid layout utilities

---

## Composition Patterns

### 1. Compound Components
- `Paginator` exports `Paginator.Dot` as a sub-component (dot indicator for page navigation)

### 2. Generic Components
- `PaginatedLeaderboard<T>` — Generic over entry type, consumers provide `renderRow`, `entryKey`, `isPlayerEntry`
- `InstrumentSelector<K>` — Generic over instrument key type (`InstrumentKey | ServerInstrumentKey`)

### 3. Polymorphic Components
- `FadeIn` uses `as` prop to render as any element type (div, Link, a, etc.) with proper TypeScript typing via `ElementType`

### 4. forwardRef Components
- `FrostedCard` — Exposes underlying `<div>` ref
- `SearchBar` — Exposes `SearchBarRef` with `focus()`/`blur()` via `useImperativeHandle`

### 5. Portal Components
Components rendering to `document.body`:
- `ModalShell` (all modals)
- `ChangelogModal`
- `ConfirmAlert`
- `FirstRunCarousel`
- `PaginatedLeaderboard` (fixed footer + pagination)

### 6. Animation Lifecycle Pattern
Multi-phase animation state machines:
- **LoadGate**: `Spinner → SpinnerOut → ContentIn`
- **PaginatedLeaderboard**: `loading → cached/stale → contentIn` with stagger delays
- **ChangelogModal / ConfirmAlert**: `mount → animIn → animOut → unmount` with exit callbacks
- **FirstRunCarousel**: `animIn → entranceDone → (slide transitions) → animOut`
- **SongInfoHeader**: Scroll-linked collapse via `--collapse` CSS variable (0→1)

### 7. Responsive Pattern
- `ModalShell` — Bottom sheet (mobile ≤768px) vs centered flyout (desktop)
- `InstrumentSelector` — Auto-compact via `ResizeObserver` when container too narrow
- `FirstRunCarousel` — Different card sizing for mobile vs desktop
- `FloatingActionButton` — PWA-aware bottom positioning
- `BottomNav` — PWA-aware bottom padding

### 8. Stagger Animation Pattern
Many components accept `style` + `onAnimationEnd` props for parent-coordinated stagger:
- `PageHeader`, `EmptyState`, `FadeIn`, `ActionPill`
- Stagger delay calculated via `staggerDelay()` from `@festival/ui-utils`

### 9. Memoization Pattern
Components wrapped in `memo()`:
- `DirectionSelector`, `RadioRow`, `ToggleRow`, `SectionHeader`
- `BackgroundImage`, `FadeIn`, `SyncBanner`, `StatBox`
- `PlayerPercentileHeader`, `PlayerPercentileRow`
- `ModalSection`, `Paginator.Dot`

### 10. Context Consumption
Components that read React contexts:
- `FeatureGate` → `useFeatureFlags()`
- `PinnedSidebar`, `Sidebar` → `useSettings()`, `useFeatureFlags()`
- `SeasonPill` → `FestivalContext` (currentSeason)
- `FloatingActionButton` → `useSearchQuery()`
- `BottomNav` → `useFeatureFlags()`

---

## Common Components (Cross-Feature Usage)

**Most reused components** (appear across 3+ feature areas):
1. `FrostedCard` — Base surface for cards, panels, rows everywhere
2. `ArcSpinner` — Loading indicator in LoadGate, SuspenseFallback, PlayerSearchBar, PaginatedLeaderboard, SyncBanner
3. `PageHeader` — Standard header on every page
4. `EmptyState` — No-data placeholders on every list page
5. `SearchBar` — Player search (header, modal, FAB), song search
6. `InstrumentIcon` — Everywhere instruments appear
7. `FadeIn` — Stagger animation wrapper on most pages
8. `MarqueeText` — Song titles, sidebar player name, headers
9. `AlbumArt` — Song cards, detail pages, leaderboard rows
10. `Paginator` — Leaderboard pages, first-run carousel

---

## Display Components (Data Visualization)

**Instrument display hierarchy:**
- `InstrumentIcon` → bare PNG icon
- `InstrumentChip` → icon + status circle (FC/score/none)
- `InstrumentHeader` → icon + text labels (5 sizes)
- `InstrumentSelector` → row of selectable instrument circles

**Score/metadata display hierarchy:**
- `ScorePill` → formatted number
- `AccuracyDisplay` → percentage with FC gold badge
- `DifficultyBars` → SVG bar meter
- `DifficultyPill` → E/M/H/X label
- `GoldStars` / `MiniStars` → star ratings
- `PercentilePill` → tier display (Top 1%/5%)
- `SeasonPill` → season badge
- `SongInfo` → thumbnail + title/artist composite

---

## Page Shell Components (Layout Structure)

**Page rendering stack (outside → inside):**
1. `ErrorBoundary` — Catches render crashes
2. `AnimatedBackground` — Album art Ken Burns cycle
3. `BackgroundImage` — Page-specific faded background
4. Shell chrome:
   - Desktop: `DesktopNav` (top) + `PinnedSidebar` (left) or `Sidebar` (overlay)
   - Mobile: `MobileHeader` (top) + `BottomNav` (bottom) + `FloatingActionButton`
5. `LoadGate` — Spinner → content transition
6. `FadeIn` — Entry animation wrapper
7. `PageHeader` — Title + actions
8. Page content
9. `SyncBanner` — Progress overlay (PlayerPage only)

**Modal layer (portaled to body):**
- `ModalShell` → `Modal` (filter/sort) or `MobilePlayerSearchModal`
- `ChangelogModal` (standalone)
- `ConfirmAlert` (standalone)
- `FirstRunCarousel` (standalone)
