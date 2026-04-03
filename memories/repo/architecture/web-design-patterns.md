# Web Design Patterns — FortniteFestivalWeb

> Source of truth for visual design, UX patterns, and styling conventions.
> Maintained by web-principal-designer. Last updated: 2026-04-03.

---

## Styling Approach

### Architecture: Inline Styles via `useStyles()` + Minimal CSS Modules

FortniteFestivalWeb uses a **hybrid styling model**:

- **Primary**: Inline style objects returned by `useStyles()` factory functions at the bottom of each component file
- **Secondary**: CSS Modules (`.module.css`) used **only** for features impossible inline — `@keyframes`, pseudo-elements (`::before`, `::after`), `backdrop-filter`, container queries, media queries for grid layouts
- **Global**: Two global CSS files — `index.css` (reset + base), `animations.css` (shared keyframes), `theme.css` (CSS custom properties for module.css files)

### CSS Modules (5 total — intentionally minimal)

| File | Purpose |
|---|---|
| `src/styles/effects.module.css` | Frosted glass pseudo-elements, scroll masks, responsive shop grid, glow hover |
| `src/styles/animations.module.css` | Spinner, shop pulse/breathe, CHOpt pulse, album pulse |
| `src/styles/rivals.module.css` | Rival row container queries, winning/losing gradients |
| `src/components/songs/headers/SongInfoHeader.module.css` | Scroll-linked header collapse (album art scale, font transitions) |
| `src/components/common/MarqueeText.module.css` | Marquee scrolling text animation |

### Naming Conventions

- **CSS classes**: camelCase (`rivalRow`, `shopGrid`, `spinnerWrap`)
- **State suffixes**: `Winning`, `Losing`, `Pulse`, `Breathe`, `Frosted`
- **Component prefixes**: `rivalRow*`, `shop*`, `spinner*`
- **Global selectors**: `:global([style*="--frosted-card"])` for theme mixin markers

### CSS Migration Rules (from `docs/refactor/CSS_MIGRATION_RULES.md`)

Key principles (37 rules):
1. Every migrated component MUST have a `function useStyles(...)` at bottom
2. All CSS values come from `@festival/theme` — no magic numbers
3. CSS keywords use enum constants (`Display.flex`, not `'flex'`)
4. Pseudo-elements/keyframes stay in minimal `.module.css`; everything else → `useStyles()`
5. Test assertions target style objects, not class names
6. All durations from theme animation constants
7. Grid templates as enum values
8. `as const` casts forbidden on style objects

---

## Design Tokens

All tokens live in `@festival/theme` (`packages/theme/src/`).

### Color Palette

| Category | Key Tokens |
|---|---|
| **Backgrounds** | `backgroundApp: #1A0830`, `backgroundCard: #0B1220`, `backgroundBlack: #000`, `backgroundBoot: #0B0B0D` |
| **Surfaces** | `surfaceFrosted: rgba(18,24,38,0.78)`, `surfaceElevated: #1A2940`, `surfaceSubtle: #162133` |
| **Frosted Glass** | `glassCard: rgba(11,18,32,0.35)`, `glassBorder: rgba(255,255,255,0.08)`, `glassNav: rgba(11,3,24,0.40)` |
| **Overlays** | `overlayModal: rgba(0,0,0,0.55)`, `overlayModal60: rgba(0,0,0,0.6)`, `overlayDark: rgba(0,0,0,0.7)` |
| **Text** | `textPrimary: #FFF`, `textSecondary: #D7DEE8`, `textTertiary: #9AA6B2`, `textMuted: #8899AA`, `textDisabled: #607089` |
| **Borders** | `borderPrimary: #2B3B55`, `borderCard: #263244`, `borderSubtle: #1E2A3A` |
| **Accent** | `accentBlue: #2D82E6`, `accentPurple: #7C3AED`, `gold: #FFD700` |
| **Status** | `statusGreen: #2ECC71`, `statusRed: #C62828` |
| **Difficulty** | Easy `#34D399`, Medium `#FBBF24`, Hard `#F87171`, Expert `#C084FC` |

### Typography Scale

| Token | Size (px) | Usage |
|---|---|---|
| `Font.xs` | 11 | Captions, badges |
| `Font.sm` | 12 | Secondary text, labels |
| `Font.md` | 14 | Body text (default) |
| `Font.lg` | 16 | Emphasized body |
| `Font.xl` | 20 | Section headings |
| `Font.title` | 22 | Page titles |
| `Font['2xl']` | 24 | Large headings |
| `Font.display` | 28 | Display/hero text |

**Weights**: `normal: 400`, `semibold: 600`, `bold: 700`, `heavy: 800`
**Font family**: System stack (`-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif`)

### Spacing / Gap Scale

| Token | Value (px) |
|---|---|
| `Gap.none` | 0 |
| `Gap.xs` | 2 |
| `Gap.sm` | 4 |
| `Gap.md` | 8 |
| `Gap.lg` | 10 |
| `Gap.xl` | 12 |
| `Gap.section` | 24 |

### Border Radius

| Token | Value (px) |
|---|---|
| `Radius.xs` | 8 |
| `Radius.sm` | 10 |
| `Radius.md` | 12 |
| `Radius.lg` | 16 |
| `Radius.full` | 999 |

### Z-Index Scale

| Token | Value | Purpose |
|---|---|---|
| `ZIndex.background` | -1 | Background layers |
| `ZIndex.base` | 1 | Default content |
| `ZIndex.overlay` | 2 | Overlapping content |
| `ZIndex.spinner` | 5 | Loading spinners |
| `ZIndex.dropdown` | 10 | Dropdown menus |
| `ZIndex.fixedFooter` | 50 | Fixed footers |
| `ZIndex.popover` | 100 | Popovers |
| `ZIndex.searchDropdown` | 300 | Search dropdowns |
| `ZIndex.modalOverlay` | 1000 | Modal backdrop |
| `ZIndex.confirmOverlay` | 1100 | Confirm dialogs |
| `ZIndex.changelogOverlay` | 1200 | Changelog modal (top) |

### Shadow Tokens

- `Shadow.tooltip`: `0 4px 12px rgba(0,0,0,0.4)`
- `Shadow.elevated`: `0 8px 24px rgba(0,0,0,0.5)`
- `Shadow.frostedActive`: `inset 0 1px 0 rgba(255,255,255,0.06), 0 4px 20px rgba(0,0,0,0.4)`

### Style Factories (spread patterns)

| Factory | Properties |
|---|---|
| `...flexColumn` | `display: flex, flexDirection: column` |
| `...flexRow` | `display: flex, alignItems: center` |
| `...flexCenter` | `display: flex, alignItems: center, justifyContent: center` |
| `...flexBetween` | `display: flex, alignItems: center, justifyContent: space-between` |
| `...truncate` | `overflow: hidden, textOverflow: ellipsis, whiteSpace: nowrap` |
| `...absoluteFill` | `position: absolute, inset: 0` |
| `...fixedFill` | `position: fixed, inset: 0` |
| `...frostedCard` | Frosted glass surface with noise texture + border + `--frosted-card` marker |
| `...purpleGlass` | Purple accent glass with inset glow + boxShadow |
| `...modalOverlay` | Fixed full-screen dark overlay |
| `...modalCard` | Frosted card with backdrop-filter blur |

### CSS Helper Functions

- `border(width, color, style?)` → `'1px solid #2B3B55'`
- `padding(top, right?, bottom?, left?)` → `'16px 20px'`
- `margin(top, right?, bottom?, left?)` → `'0 auto'`
- `transition(property, durationMs, easing?)` → `'opacity 300ms ease'`
- `transitions(...items)` → comma-joined transitions
- `scale(factor)` / `translateY(px)` / `scaleTranslateY(s, y)` → transform strings

### CSS Enums (no raw strings)

All CSS keyword values use typed enums: `Display.flex`, `Position.relative`, `Align.center`, `Justify.between`, `Overflow.hidden`, `Cursor.pointer`, `TextAlign.center`, `BoxSizing.borderBox`, etc.

---

## Responsive Strategy

### Breakpoints

| Name | Value | Query Constant | Purpose |
|---|---|---|---|
| **Narrow** | 420px | `QUERY_NARROW_GRID` (max), `QUERY_SHOW_ACCURACY` (min) | Accuracy column, narrow grid |
| **Medium** | 520px | `QUERY_SHOW_SEASON` | Season column visible |
| **Mobile** | 768px | `QUERY_MOBILE` (max), `QUERY_SHOW_STARS` (min) | Layout switch (mobile ↔ desktop) |
| **Wide Desktop** | 1440px | `QUERY_WIDE_DESKTOP` | Pinned sidebar |

Additional CSS-only breakpoints: 600px, 860px, 1100px (shop grid columns).

### Approach: Hook-Based Responsive + CSS Grid Queries

- **Layout branches**: `useIsMobile()`, `useIsWideDesktop()` hooks return booleans for conditional rendering
- **Column visibility**: `useLeaderboardColumns()` — accuracy (420+), season (520+), stars (768+)
- **Grid responsiveness**: CSS `@media` queries in `.module.css` files for grid column counts
- **Container queries**: `@container` in `rivals.module.css` for component-level responsiveness
- **Virtual keyboard**: `useVisualViewportHeight()` handles iOS PWA keyboard offset
- **No responsive context** — each component calls hooks directly

### Viewport Tiers

| Tier | Width | UI Features |
|---|---|---|
| **Narrow Mobile** | ≤420px | Single column, no accuracy column, simplified grid |
| **Mobile** | 421–768px | Bottom nav, FAB, mobile header, season column at 520+ |
| **Compact Desktop** | 769–1439px | Desktop nav, hamburger sidebar, no pinned sidebar |
| **Wide Desktop** | ≥1440px | Pinned sidebar (256px), center content (max 1080px), right gutter |

### Playwright Test Viewports

| Name | Dimensions | Matches |
|---|---|---|
| mobile | 375×812 | Narrow + Mobile tier |
| wide-mobile | 520×900 | Mobile tier (QUERY_SHOW_SEASON) |
| compact-desktop | 1024×768 | Compact Desktop |
| wide-desktop | 1600×900 | Wide Desktop |

### Mobile-Specific Features

- Bottom navigation bar (Songs, Rivals, Suggestions, Compete, Settings)
- Floating Action Button (player/song search)
- Mobile header with back navigation
- Bottom-sheet modals (`translateY` slide-up)
- Per-tab scroll restoration via `useTabNavigation()`
- Safe area insets (`env(safe-area-inset-*)`)

### Desktop-Specific Features

- Pinned left sidebar (256px) with player card + filters
- Header overlay with search bar + profile button
- Centered content column (max-width 1080px)
- Centered modals with opacity fade
- Proximity-based glow on frosted cards (mouse tracking)

---

## Animation Patterns

### Core Animation System: CSS Keyframes + Stagger Hooks

**No external animation libraries** — all custom-built with CSS `@keyframes`, `requestAnimationFrame`, and `setTimeout`.

### Keyframe Animations

| Animation | Duration | Effect | Usage |
|---|---|---|---|
| `fadeInUp` | 400ms | Opacity 0→1 + translateY 12px→0 | Primary entrance animation |
| `fadeIn` | 400ms | Opacity 0→1 | Simple fade |
| `fadeOut` | variable | Opacity 1→0 | Exit animation |
| `fadeOutDown` | variable | Opacity 1→0 + translateY 0→12px | Exit with slide |
| `slideUp` | variable | translateY 12px→0 | Position entrance |
| `spin` | 800ms | rotate 0→360deg (infinite) | Loading spinner |
| `shopBreathe` | 3s | Opacity pulse 0.5→1→0.5 (infinite) | New shop item glow |
| `shopPulse` | 2s | Border opacity pulse (infinite) | Item highlight |
| `marqueeScroll` | 6s | translateX scroll loop | Overflowing text |
| `choptIconPulse` | 2s | Opacity 1→0.4→1 (infinite) | Invalid score indicator |

### Stagger System

Three complementary hooks for sequential entrance animations:

1. **`useStagger(shouldStagger, interval?)`** — Primary list stagger
   - `forDelay(ms)`: fixed delay
   - `forIndex(i, offset)`: index-based delay
   - `next(offset)`: auto-incrementing counter
   - `clearAnim(e)`: onAnimationEnd cleanup

2. **`useStaggerStyle(delayMs, options?)`** — Single-element stagger hook
   - Returns `{ style, onAnimationEnd }`

3. **`buildStaggerStyle(delayMs)` / `clearStaggerStyle(e)`** — Pure functions for `.map()` loops

### Page Transition Orchestration

**Load phase state machine**: `Loading → SpinnerOut → ContentIn`

- **`useLoadPhase(isReady)`** — Core state machine: spinner visible → fade out (500ms) → content in
- **`usePageTransition(cacheKey, isReady, hasCached)`** — Page-level: tracks visited pages, skips stagger on return visits
- **`useViewTransition()`** — Imperative view changes (grid↔list toggle): triggers stagger without loading
- **`useStaggerRush(containerRef)`** — Scroll interruption: collapses remaining stagger delays to 0ms if user scrolls

### Animation Timing Constants (from `@festival/theme`)

| Constant | Value | Purpose |
|---|---|---|
| `STAGGER_INTERVAL` | 125ms | Delay between sequential items |
| `FADE_DURATION` | 400ms | Main entrance animation |
| `SPINNER_FADE_MS` | 500ms | Spinner fade-out duration |
| `QUICK_FADE_MS` | 150ms | Short UI transitions |
| `FAST_FADE_MS` | 200ms | Medium transitions |
| `TRANSITION_MS` | 300ms | Standard CSS transition |
| `MIN_SPINNER_MS` | 400ms | Minimum spinner display time |
| `STAGGER_ENTRY_OFFSET` | 80ms | Offset before first stagger item |
| `STAGGER_ROW_MS` | 60ms | Compact row stagger (leaderboards) |
| `MODAL_STAGGER_MS` | 150ms | Modal content stagger |
| `NAV_TRANSITION_MS` | 150ms | Navigation hover/focus |
| `LINK_TRANSITION_MS` | 250ms | Sidebar link state |

### Easing Curves

- `EASE_SMOOTH`: `cubic-bezier(0.4, 0, 0.2, 1)` — Standard smooth
- `EASE_OVERSHOOT`: `cubic-bezier(0.34, 1.56, 0.64, 1)` — Bouncy overshoot

### Special Techniques

- **Proximity glow** (`useProximityGlow`): Mouse-tracking CSS variable updates (`--glow-x`, `--glow-y`) on frosted cards, desktop-only
- **Synchronized spinners**: All ArcSpinner instances rotate in phase via `performance.now() % SPIN_DURATION_MS`
- **Reduced motion**: MarqueeText respects `prefers-reduced-motion: reduce`

---

## Loading / Error / Empty States

### Loading: ArcSpinner + LoadGate

**ArcSpinner** (`src/components/common/ArcSpinner.tsx`):
- Rotating border animation synced across all instances
- Three sizes: `SpinnerSize.SM` (24px), `SpinnerSize.MD` (36px), `SpinnerSize.LG` (48px, default)
- Purple accent border-top color

**LoadGate** (`src/components/page/LoadGate.tsx`):
- Controls visibility based on `LoadPhase` (Loading | SpinnerOut | ContentIn)
- Optional fixed overlay mode
- Manages spinner fadeOut animation
- Children render only when `phase === ContentIn`

**SuspenseFallback** (`src/components/common/SuspenseFallback.tsx`):
- React.lazy() chunk-loading fallback — centered ArcSpinner in fixed overlay

**useFadeSpinner** (`src/hooks/ui/useFadeSpinner.ts`):
- For spinners that fade in (not start visible) — used in search dropdowns
- Double `rAF` forces reflow, manages mount/unmount with transition

### Canonical Loading Pattern

```tsx
const { phase, shouldStagger } = usePageTransition(cacheKey, dataReady, hasCached);
return (
  <Page>
    <LoadGate phase={phase}>
      {phase === LoadPhase.ContentIn && <Content shouldStagger={shouldStagger} />}
    </LoadGate>
  </Page>
);
```

### Error: parseApiError → EmptyState

**parseApiError** (`src/utils/apiError.ts`):
- Parses `"API 404: ..."` strings via regex
- Maps status codes: 404→notFound, 4xx→clientError, 500→serverError, 502-504→serviceUnavailable
- Returns i18n-localized `{ title, subtitle }`

**Full-page error pattern** (early return):
```tsx
if (error) {
  const parsed = parseApiError(error);
  return <EmptyState fullPage title={parsed.title} subtitle={parsed.subtitle} />;
}
```

**Inline card error**: Shows `parseApiError(error).title` as text in card context.

**Multi-query error**: Show error only if ALL parallel queries failed.

### Empty / No-Data: EmptyState Component

**EmptyState** (`src/components/common/EmptyState.tsx`):
- Props: `title` (required), `subtitle?`, `icon?`, `fullPage?`, `style?`, `onAnimationEnd?`, `className?`
- `fullPage` mode: `minHeight: calc(100vh - shellChromeHeight)` for viewport centering
- Center-aligned flex column layout

**ErrorBoundary** (`src/components/page/ErrorBoundary.tsx`):
- Class component wrapping every route in `App.tsx`
- Catches unhandled render errors → shows title + message + reload button

### Key Principles

1. **Early error returns** — If async error, return `<EmptyState fullPage />` immediately
2. **Phase gating** — Content renders only when `phase === LoadPhase.ContentIn`
3. **Stagger discipline** — `shouldStagger` gates all animations; controlled by visit tracking
4. **Sync spinners** — All instances rotate together
5. **No skeleton loaders** — Spinner → full content transition, not skeleton screens

---

## Accessibility

### Current ARIA Usage

| Attribute | Status | Usage |
|---|---|---|
| `aria-label` | Extensive | Button labels, search inputs, close/nav actions |
| `aria-hidden="true"` | 5 uses | Decorative SVGs, placeholder pills |
| `aria-modal="true"` | 1 use | ModalShell dialog |
| `role="dialog"` | 2 uses | ModalShell, PathsModal |
| `role="button"` | 12 uses | Clickable divs (paired with tabIndex + onKeyDown) |
| `role="presentation"` | 1 use | Backdrop overlay |
| `aria-labelledby` | Not used | — |
| `aria-live` | Not used | No dynamic announcements |
| `aria-expanded` | Not used | Dropdown/modal state not announced |

### Keyboard Navigation

- **Enter key**: Widespread — RadioRow, SearchBar, CompetePage, RivalsPage (category/combo/instrument navigation)
- **Enter + Space**: InvalidScoreIcon handles both
- **ArrowLeft/ArrowRight**: Optional keyboard prop on Paginator
- **Escape**: Closes modals (ModalShell via `useLayoutEffect`)
- **`tabIndex={0}`**: 15 uses on non-button interactive elements
- **`tabIndex={-1}`**: Paginator dots removed from tab order

### Focus Management

- **SearchBar**: Exposes `focus()` / `blur()` via ref; optional `autoFocus` prop
- **HeaderSearch**: Uses `searchRef.current` for imperative focus
- **MobilePlayerSearchModal**: Blur input on Enter

### Known Gaps

| Gap | Impact | Recommendation |
|---|---|---|
| No `aria-labelledby` | Headers not linked to content | Add where title IDs exist |
| No `aria-live` regions | Dynamic data updates not announced | Add to real-time data sections |
| `role="button"` on divs | Prefer semantic `<button>` elements | Migrate where possible |
| No sr-only utility class | No visually hidden text for screen readers | Create utility |
| No `aria-expanded` | Dropdown/modal state not announced | Add to toggleable UI |
| No focus trap in modals | Modal focus not constrained | Implement focus barrier in ModalShell |

---

## Layout Patterns

### Page Shell: `<Page>` Component

Every page wraps in `<Page>` (`src/pages/Page.tsx`):
- Manages scroll container, scroll restoration, scroll masking, stagger rush
- Props: `scrollRestoreKey`, `variant` (default/withBg/withBgClip), `loadPhase`, `fabSpacer`, `headerCollapse`
- Headers rendered via `before` prop → portaled to fixed header overlay (outside scroll area)
- FAB spacing: `'end'` (spacer div), `'fixed'` (margin on scroll), `'none'` (manual)

### App-Level Layout

**Mobile**:
- Vertical scroll container (full-width)
- Mobile header (title + nav icons)
- Page content
- Bottom navigation (5 tabs)
- FAB overlay

**Wide Desktop** (≥1440px):
- Left pinned sidebar (256px, overlay, `pointer-events: none`)
- Center scrollable content (max-width: 1080px)
- Right gutter (symmetry)
- Header overlay (fixed top, portaled content)

### Grid Conventions

- Responsive card grids: `gridTemplateColumns: repeat(N, 1fr)` with CSS media queries for column count
- Container queries: `container-type: inline-size` for component-level responsive sizing
- Gap values: `Gap.sm|md|lg|xl` from theme

### Common Layout Components

| Component | Purpose |
|---|---|
| `PageHeader` | Title + subtitle + actions row |
| `FrostedCard` | Frosted glass card wrapper |
| `BackgroundImage` | Fixed background with opacity fade |
| `FadeIn` | Polymorphic stagger animation wrapper |
| `LoadGate` | Conditional render by LoadPhase |
| `ErrorBoundary` | Error fallback UI wrapper |

### Portal System

- **ScrollContainerContext**: provides `useScrollContainer()`, `useHeaderPortal()`, `useShellRefs()`
- Header portal height synced via CSS custom property `--header-portal-h` (avoids re-render)
- Header collapse tracked via `--collapse: 0→1` CSS variable
- Pages portal headers via `createPortal(before, portalTarget)`
- Modals portal independently via `createPortal(modal, document.body)`

### Frosted Glass Pattern

Three tiers of frosted cards:
1. `frostedCard` — Full texture (noise SVG + border + `--frosted-card` marker for glow)
2. `frostedCardSurface` — Texture without glow marker
3. `frostedCardLight` — No texture, just color + border

---

## Icon System

### Library: react-icons v5.6.0 — Ionic Icons (io5)

All icons imported from `react-icons/io5` (Io prefix).

### Icon Inventory

| Category | Icons |
|---|---|
| **Navigation** | `IoChevronBack`, `IoChevronForward`, `IoMenu`, `IoGrid`, `IoList` |
| **UI Actions** | `IoClose`, `IoSearch`, `IoOptions` |
| **State/Status** | `IoWarning`, `IoAlertCircleOutline` |
| **User/Profile** | `IoPerson`, `IoPersonAdd` |
| **Actions** | `IoSwapVerticalSharp`, `IoFunnel`, `IoFlash` |
| **Content** | `IoMusicalNotes`, `IoTrophy`, `IoSparkles`, `IoStatsChart`, `IoSettings`, `IoPeople` |
| **Commerce** | `IoBagHandle` |
| **Help** | `IoHelpCircleOutline` |

### Icon Sizing

Uses theme constants: `IconSize.default` (20px), `IconSize.chevron` (16px), `IconSize.fab` (18px), `IconSize.back` (22px), etc.

### Usage Pattern

```tsx
import { IoClose } from 'react-icons/io5';

<button aria-label={t('common.close')}>
  <IoClose size={IconSize.default} />
</button>
```

> Note: The AGENTS.md mentions FiAlertCircle (Feather icons) in the UX consistency registry, but the actual codebase uses **Ionic icons (Io prefix)** exclusively from `react-icons/io5`.

---

## Modal / Overlay System

### Three Tiers

#### 1. ModalShell (Base Layer)

- Overlay div (dark background, click-to-close)
- Panel div (animated entry/exit)
- Mobile: bottom sheet (`translateY(100%)` → `translateY(0)`)
- Desktop: centered (`translate(-50%, -50%)` with opacity fade)
- Escape key + click-outside handling
- `role="dialog"`, `aria-modal="true"`

#### 2. Modal (Draft Pattern — extends ModalShell)

- Sort/filter modals with draft state (`useModalState<T>()`)
- Content scroll with fade mask (`useScrollMask`)
- Apply + Reset footer buttons
- Used by: FilterModal, SortModal, InstrumentPickerModal

#### 3. ConfirmAlert (Alert Dialog)

- Centered card with scale + translate animation
- Yes/No buttons with optional exit animation
- Portaled to `document.body`
- Z-index: `confirmOverlay: 1100` (above modal)

#### 4. Custom Modals (Special)

- ChangelogModal, PathsModal, FirstRunCarousel
- Own animation state machine: `mounted → animIn → [visible] → animOut → unmounted`
- `requestAnimationFrame` + `setTimeout` for timing

### Modal Lifecycle

```
not visible → (trigger) → mounted=true → rAF → animIn=true (300ms animate in)
→ [modal open] → (close) → animIn=false (300ms animate out)
→ setTimeout 300ms → mounted=false (unmount)
```

### Portal Strategy

All modals use `createPortal(content, document.body)` for z-index stacking independence from page layout.

### Modal Z-Index Layers

1. `modalOverlay: 1000` — Standard modals
2. `confirmOverlay: 1100` — Confirm alerts (above modals)
3. `changelogOverlay: 1200` — Changelog (topmost)

---

## Appendix: File Reference

### Theme Package (`packages/theme/src/`)

- `index.ts` — All exports
- `colors.ts` — Color palette (~80 tokens)
- `spacing.ts` — Radius, Font, Weight, ZIndex, Gap, Opacity, Size, Layout, etc.
- `animation.ts` — Timing constants (~30 values)
- `breakpoints.ts` — Breakpoint values + query strings
- `cssEnums.ts` — CSS keyword enums (Display, Position, Align, etc.)
- `cssHelpers.ts` — border(), padding(), margin(), transition(), scale(), translateY()
- `factories.ts` — Spread patterns (flexColumn, flexRow, truncate, absoluteFill, etc.)
- `frostedStyles.ts` — Frosted glass, modal, button factories
- `goldStyles.ts` — Gold/full-combo styling
- `pagination.ts` — Page sizes, batch sizes
- `polling.ts` — Poll intervals

### Key Component Paths

- Loading: `src/components/common/ArcSpinner.tsx`, `src/components/page/LoadGate.tsx`
- Empty: `src/components/common/EmptyState.tsx`
- Error: `src/utils/apiError.ts`, `src/components/page/ErrorBoundary.tsx`
- Modal: `src/components/modals/components/ModalShell.tsx`
- Page shell: `src/pages/Page.tsx`
- Stagger: `src/hooks/ui/useStagger.ts`, `src/hooks/ui/useStaggerStyle.ts`
- Transitions: `src/hooks/ui/usePageTransition.ts`, `src/hooks/data/useLoadPhase.ts`
- Responsive: `src/hooks/ui/useIsMobile.ts`, `src/hooks/ui/useMediaQuery.ts`
