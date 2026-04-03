# FortniteFestivalWeb Styling System

> Last updated: April 3, 2026

## Theme Token System

All design tokens live in `packages/theme/src/` and are exported from `@festival/theme`. Tokens are JS constants (not CSS custom properties) consumed directly in `useStyles()` hooks.

### Colors (`colors.ts`)
All values are string literals (hex, rgba, or gradient strings).

| Category | Key examples | Format |
|---|---|---|
| **Backgrounds** | `backgroundApp` (#1A0830), `backgroundCard` (#0B1220), `backgroundBlack`, `backgroundBoot`, `backgroundCardAlt`, `backgroundCardAlt2` | Hex |
| **Surfaces** | `surfaceFrosted` (rgba 0.78), `surfaceElevated`, `surfaceSubtle`, `surfacePressed`, `surfaceMuted`, `surfaceWhiteSubtle` | Hex / RGBA |
| **Glass** | `glassCard` (rgba 0.35), `glassBorder` (rgba 0.08), `glassNav` (rgba 0.40), `glowHighlight` (rgba 0.07) | RGBA |
| **Overlays** | `overlayModal` (0.55), `overlayModal60` (0.6), `overlayScrim` (0.35), `overlayDark` (0.7) | RGBA |
| **Text** | `textPrimary` (#FFF), `textSecondary`, `textTertiary`, `textMuted`, `textSubtle`, `textDisabled`, `textNearWhite`, `textSemiTransparent`, `textPlaceholder`, `textVeryMuted`, `textMutedCaption` | Hex / RGBA |
| **Borders** | `borderPrimary`, `borderCard`, `borderSubtle`, `borderSeparator` | Hex |
| **Accents** | `accentBlue` (#2D82E6), `accentBlueBright`, `accentBlueDark`, `accentPurple` (#7C3AED), `accentPurpleDark` | Hex |
| **Gold** | `gold` (#FFD700), `goldBg` (#332915), `goldStroke` (#CFA500) | Hex |
| **Status** | `statusGreen`, `statusGreenStroke`, `statusRed`, `statusRedStroke` | Hex |
| **Rivals** | `rivalGreenBg/Border`, `rivalRedBg/Border` | RGBA |
| **Chart** | `chartTop1..chartBelow50` | Hex |
| **Accuracy** | `accuracyLow` / `accuracyHigh` — RGB objects for gradient interpolation | `{r,g,b}` |
| **Difficulty** | `diffEasyBg/Accent`, `diffMediumBg/Accent`, `diffHardBg/Accent`, `diffExpertBg/Accent`, `diffPillEasy..diffPillExpert` | Hex |
| **Buttons** | `dangerBg`, `successBg`, `chipSelectedBg`, `chipSelected`, `purpleButtonBg` | RGBA |
| **Misc** | `transparent`, `whiteOverlaySubtle`, `whiteOverlay`, `purplePlaceholder`, `cardOverlay`, `purpleTabActive` | Mixed |
| **Gradients** | `scrimGradient` (linear-gradient string), `maskFadeBottom` | CSS string |

### Spacing & Sizing (`spacing.ts`)

| Struct | Values |
|---|---|
| **Radius** | `xs:8, sm:10, md:12, lg:16, full:999, progressBar:3` |
| **Font** | `xs:11, sm:12, md:14, lg:16, xl:20, title:22, 2xl:24, display:28, letterSpacingWide:0.5` |
| **Weight** | `normal:400, semibold:600, bold:700, heavy:800` |
| **ZIndex** | `background:-1, base:1, overlay:2, spinner:5, dropdown:10, fixedFooter:50, popover:100, searchDropdown:300, modalOverlay:1000, confirmOverlay:1100, changelogOverlay:1200` |
| **LineHeight** | `none:0, sm:16, md:18, lg:20, snug:1.4, relaxed:1.5, loose:1.6` |
| **Gap** | `none:0, xs:2, sm:4, md:8, lg:10, xl:12, section:24` |
| **Opacity** | `none:0, subtle:0.1, dimmed:0.3, faded:0.4, disabled:0.5, pressed:0.85, backgroundImage:0.9, icon:0.92` |
| **Border** | `thin:1, medium:1.5, thick:2, spinner:3, spinnerLg:4` |
| **Shadow** | `tooltip`, `elevated`, `frostedActive` (inset + drop) |
| **SpinnerSize** (const enum) | `SM=0, MD=1, LG=2` → maps to `{size, border}` configs |
| **IconSize** | `xs:14, sm:24, md:28, lg:40, xl:48, profile:32, default:20, fab:18, tab:20, chevron:16, back:22, nav:22, action:16` |
| **InstrumentSize** | `xs:28, sm:36, md:48, lg:56, button:48, chip:34` |
| **StarSize** | `inline:14, icon:20, rowWidth:132, gap:3` |
| **AlbumArtSize** | `collapsed:80, expanded:120` |
| **MetadataSize** | `pillMinWidth:80, dotSize:8, dotActiveScale:1.25, control:34` |
| **ChartSize** | `height:320, barSelectionStroke:3` |
| **MaxWidth** | `card:1080, grid:2170, narrow:600` |
| **Layout** | ~80+ named constants: padding, heights, widths, z-indexes, toggle sizes, carousel dimensions, chart layout, modal sizes, sidebar width (240px), shell chrome height (200px), etc. |

### Animation (`animation.ts`)

| Constant | Value | Usage |
|---|---|---|
| `STAGGER_INTERVAL` | 125ms | Default delay between stagger items |
| `FADE_DURATION` | 400ms | Standard fadeInUp animation duration |
| `SPINNER_FADE_MS` | 500ms | Spinner fade out |
| `QUICK_FADE_MS` | 150ms | Quick micro-transitions |
| `FAST_FADE_MS` | 200ms | Fast transitions (accordion) |
| `DEBOUNCE_MS` | 250ms | Input debounce |
| `RESIZE_DEBOUNCE_MS` | 150ms | Window resize debounce |
| `TRANSITION_MS` | 300ms | Standard CSS transition |
| `MIN_SPINNER_MS` | 400ms | Minimum spinner display time |
| `STAGGER_ENTRY_OFFSET` | 80ms | Base offset before first stagger item |
| `STAGGER_ROW_MS` | 60ms | Delay per row in stagger lists |
| `NAV_TRANSITION_MS` | 150ms | Hover/focus state transitions |
| `LINK_TRANSITION_MS` | 250ms | Sidebar link transitions |
| `MODAL_STAGGER_MS` | 150ms | Modal enter/exit stagger |
| `FAB_DISMISS_MS` | 300ms | FAB menu dismiss |
| `FAB_OPEN_MS` | 450ms | FAB menu open |
| `ACCORDION_DELAY_MS` | 300ms | Accordion expand delay |
| `CHART_ANIM_DURATION` | 400ms | Chart animation |
| `SWIPE_THRESHOLD` | 50px | Carousel swipe threshold |
| `MODAL_SCALE_ENTER` | 0.95 | Modal scale-down on enter |
| `PILL_SCALE_HIDDEN` | 0.9 | Pill/button hidden scale |
| `MODAL_SLIDE_OFFSET` | 10px | Modal slide-up offset |
| `EASE_SMOOTH` | `cubic-bezier(0.4, 0, 0.2, 1)` | Layout transitions |
| `EASE_OVERSHOOT` | `cubic-bezier(0.34, 1.56, 0.64, 1)` | Popup scale-in |

### CSS Enums (`cssEnums.ts`)
Eliminates all raw CSS keyword strings. Every `as const` cast is replaced by an enum:

- **Display**: `none, flex, inlineFlex, block, inlineBlock, grid, contents`
- **Position**: `relative, absolute, fixed, sticky`
- **Align**: `start (flex-start), end, center, stretch, baseline`
- **Justify**: `start, end, center, between, around, evenly`
- **TextAlign**: `left, center, right`
- **FontStyle/Variant/WordBreak/WhiteSpace/Isolation/TransformOrigin/TextTransform**
- **BoxSizing, BorderStyle, Overflow, ObjectFit, Cursor, PointerEvents**
- **CssValue**: `transparent, none, inherit, auto, full (100%), circle (50%), marginCenter (0 auto), viewportFull (100vh)`
- **CssProp**: Property name strings for `transition()` — `opacity, color, transform, backgroundColor, borderColor, boxShadow, width, height, left, gridTemplateRows, all`
- **GridTemplate**: `single (1fr), twoEqual, threeEqual, autoFillInstrument`

### CSS Helpers (`cssHelpers.ts`)
Builder functions to eliminate template literal construction:

- `border(width, color, style?)` → `"2px solid #CFA500"`
- `padding(top, right?, bottom?, left?)` → `"8px 12px"`
- `margin(top, right?, bottom?, left?)` → `"8px 0px"`
- `transition(property, durationMs, easing?)` → `"opacity 300ms ease"`
- `transitions(...items)` → comma-joined multiple transitions
- `scale(factor)` → `"scale(1.25)"`
- `translateY(px)` → `"translateY(10px)"`
- `scaleTranslateY(s, y)` → `"scale(0.95) translateY(10px)"`

### Style Factories (`factories.ts`)
Pre-built `CSSProperties` objects to spread:

- `flexColumn` — `display:flex, flexDirection:column`
- `flexRow` — `display:flex, alignItems:center`
- `flexCenter` — `display:flex, alignItems:center, justifyContent:center`
- `flexBetween` — `display:flex, alignItems:center, justifyContent:space-between`
- `textBold` / `textSemibold` — font weight shortcuts
- `truncate` — `overflow:hidden, textOverflow:ellipsis, whiteSpace:nowrap`
- `absoluteFill` / `fixedFill` — `position:absolute/fixed, inset:0`
- `centerVertical` — `top:50%, transform:translateY(-50%)`

### Frosted Styles (`frostedStyles.ts`)
Glass-effect mixins with SVG noise texture:

- `frostedCard` — Semi-transparent bg + SVG feTurbulence noise + glass border + `--frosted-card` marker (triggers proximity glow via CSS)
- `frostedCardSurface` — Same visual but without `--frosted-card` marker (for containers whose children get individual glows)
- `frostedCardLight` — Lightweight variant (no noise, no shadow) for repeated list items
- `modalOverlay` — Fixed fullscreen dark scrim
- `modalCard` — Frosted glass dialog with `backdrop-filter:blur(18px)`
- `btnPrimary` — Blue chip action button
- `btnDanger` — Red danger button
- `purpleGlass` — Purple branded surface

### Gold Styles (`goldStyles.ts`)
- `goldFill` — Gold border + filled background
- `goldOutline` — Gold 2px border, transparent bg, bold, inline-block
- `goldOutlineSkew` — Gold outline + italic + `skewX(-8deg)`
- `GOLD_SKEW` — The skew transform string constant

### Breakpoints (`breakpoints.ts`)

| Constant | Value | Purpose |
|---|---|---|
| `NARROW_BREAKPOINT` | 420px | Hide accuracy column |
| `MEDIUM_BREAKPOINT` | 520px | Hide season column |
| `MOBILE_BREAKPOINT` | 768px | Mobile vs desktop layout |
| `WIDE_DESKTOP_BREAKPOINT` | 1440px | Pinned sidebar |

Pre-built media query strings: `QUERY_SHOW_ACCURACY`, `QUERY_SHOW_SEASON`, `QUERY_SHOW_STARS`, `QUERY_MOBILE`, `QUERY_NARROW_GRID`, `QUERY_WIDE_DESKTOP`

### Pagination (`pagination.ts`)
- `LEADERBOARD_PAGE_SIZE: 25`, `SUGGESTIONS_BATCH_SIZE: 6`, `SUGGESTIONS_INITIAL_BATCH: 10`, `SCROLL_PREFETCH_PX: 600`

### Polling (`polling.ts`)
- `SYNC_POLL_ACTIVE_MS: 3000`, `SYNC_POLL_IDLE_MS: 60000`

---

## useStyles Pattern

The primary styling mechanism. Every component places a `function useStyles(...)` at the bottom of the file. It returns a `useMemo`'d object of named `CSSProperties` applied via `style={s.thing}`.

### Canonical Pattern

```tsx
export default function EmptyState({ title, subtitle, icon, fullPage }: EmptyStateProps) {
  const s = useStyles(fullPage);
  return (
    <div style={s.root}>
      {icon}
      <div style={s.title}>{title}</div>
      {subtitle && <div style={s.subtitle}>{subtitle}</div>}
    </div>
  );
}

function useStyles(fullPage?: boolean) {
  return useMemo(() => ({
    root: {
      ...flexColumn,
      alignItems: Align.center,
      gap: Gap.md,
      padding: padding(48, Gap.xl),
      textAlign: TextAlign.center,
      ...(fullPage ? { minHeight: `calc(100vh - ${Layout.shellChromeHeight}px)` } : undefined),
    },
    title: { fontSize: Font.xl, fontWeight: Weight.bold, color: Colors.textPrimary },
    subtitle: { fontSize: Font.md, color: Colors.textMuted },
  }), []);
}
```

### Key Rules
1. **Always at file bottom** — hoisted, reads like the old CSS module
2. **No magic numbers** — every value from `@festival/theme`
3. **No string enums** — all CSS keywords from `cssEnums.ts`
4. **No raw inline styles** — always `style={s.thing}` from `useStyles()`
5. **Dynamic values** passed as parameters to `useStyles(param)` in `useMemo` deps
6. **Spread factories** for common patterns: `...frostedCard`, `...flexColumn`, `...flexCenter`
7. **CSS helpers** for compound values: `border()`, `padding()`, `transition()`
8. **Named zeros**: `Gap.none`, `Opacity.none`, `LineHeight.none` instead of bare `0`
9. **Class components** use module-level `const styles: Record<string, CSSProperties>` instead

### Shared Style Objects
For cross-component re-use (e.g. song rows used by both production and demo components):
- `src/styles/songRowStyles.ts` — exports `CSSProperties` objects (`songRow`, `songRowMobile`, `detailStrip`, etc.)

---

## CSS Modules

### Migration Status: ✅ Phase 16 Complete (March 25, 2026)

93 of 96 original CSS module files were deleted. **Only 5 CSS module files remain**, all containing CSS features that cannot be expressed as inline styles:

| File | Purpose | Consumers |
|---|---|---|
| `src/styles/animations.module.css` | `@keyframes`, `::after`/`::before` pseudo-elements for spinner, shop pulse/breathe effects (blue + red variants), chopt icon pulse | AlbumArt, SongRow, ShopCard, SongDetailHeader, InvalidScoreIcon, DrillDownDemo, PathImage, ShopLeavingTomorrowDemo, ShopHighlightingDemo, YourRankDemo |
| `src/styles/effects.module.css` | `backdrop-filter`, `::placeholder`, `:hover`, `@media`, `mask-image`, frosted card proximity glow (`::before` + `box-shadow`) | BottomNav, SearchBar, RivalsPage, RivalDetailPage, LeaderboardRivalsTab, ShopPage, CompetePage, InfiniteScrollDemo |
| `src/styles/rivals.module.css` | `::after` pseudo-element overlays (win/lose gradients), `@container` queries for responsive rival grid layouts, `:hover` states | RivalRow, RivalSongRow |
| `src/components/common/MarqueeText.module.css` | `@keyframes marqueeScroll`, `@media (prefers-reduced-motion)` | MarqueeText |
| `src/components/songs/headers/SongInfoHeader.module.css` | Scroll-linked collapse animations via `calc()` with `--collapse` CSS variable, `will-change:transform`, `transform-origin` | SongInfoHeader |

### Why These Remain
CSS modules are kept **only** for features that inline styles cannot express:
- `::before` / `::after` pseudo-elements
- `@keyframes` animations (referenced by name from inline styles)
- `@container` queries
- `@media` queries (responsive grids, reduced motion)
- `backdrop-filter` / `-webkit-backdrop-filter`
- `mask-image` / `-webkit-mask-image`
- `:hover` states
- `::placeholder` color
- Scroll-driven `calc()` with CSS custom properties (`--collapse`)

Components that use CSS modules typically combine both: `className={css.thing}` for the pseudo-element anchor + `style={s.thing}` for all real styles.

---

## CSS Migration Rules

Defined in `docs/refactor/CSS_MIGRATION_RULES.md` (37 rules). Key principles:

1. **No magic numbers** — every value from theme
2. **No string enums** — use `cssEnums.ts` constants
3. **No raw inline styles** — always through `useStyles()`
4. **CSS `composes:` → spread operators** (`...frostedCard`, `...flexColumn`)
5. **CSS `var(--token)` → direct JS constants** (`Colors.textPrimary`)
6. **Template literals → helper functions** (`border()`, `padding()`, `transition()`)
7. **Keyframes stay global** — referenced by name string from `useStyles`
8. **Pseudo-elements stay in minimal CSS** — only pseudo rules, nothing else
9. **Tests assert behavior, not class names** — use `element.style.X` or content queries
10. **`as const` → enum constants** — never `as const` on strings in useStyles
11. **All durations from theme** — no bare numbers
12. **All strings translated** — `t('common.error')` not hardcoded English

---

## Responsive Design

### Breakpoints (from `@festival/theme`)

| Breakpoint | Value | Layout Change |
|---|---|---|
| `NARROW_BREAKPOINT` | 420px | Hide accuracy column, narrow grid (2→1 col) |
| `MEDIUM_BREAKPOINT` | 520px | Hide season column |
| `MOBILE_BREAKPOINT` | 768px | Mobile layout (bottom nav, FAB, mobile header) vs desktop |
| `WIDE_DESKTOP_BREAKPOINT` | 1440px | Pinned sidebar visible |

### Hooks

| Hook | File | Purpose |
|---|---|---|
| `useMediaQuery(query)` | `hooks/ui/useMediaQuery.ts` | Generic media query → boolean, built on `useSyncExternalStore` |
| `useIsMobile()` | `hooks/ui/useIsMobile.ts` | `max-width: 768px` — pure dimension check for layout |
| `useIsMobileChrome()` | `hooks/ui/useIsMobile.ts` | Mobile OR iOS/Android/PWA — controls bottom nav, FAB, mobile header |
| `useIsWideDesktop()` | `hooks/ui/useIsMobile.ts` | `min-width: 1440px` — pinned sidebar |
| `useLeaderboardColumns()` | `hooks/ui/useLeaderboardColumns.ts` | Returns `{showAccuracy, showSeason, showStars}` booleans for progressive column disclosure |

### CSS-Level Responsive
- **effects.module.css**: Shop grid responsive columns (`@media 600px/860px/1100px` → 2/3/4/5 cols), diff grid
- **rivals.module.css**: `@container` queries (620px/380px) for rival row grid layouts
- **effects.module.css**: `@media (hover: none)` suppresses proximity glow on touch devices
- **MarqueeText.module.css**: `@media (prefers-reduced-motion)` disables marquee animation
- **Safe area insets**: `env(safe-area-inset-*)` CSS variables in `index.css` for PWA/iOS

### Container Queries
Used in `rivals.module.css` for component-level responsive layouts (rival rows adapt to their container width, not viewport). Container type is set via inline styles (`containerType: 'inline-size'`).

---

## Animations

### Global Keyframes (`src/styles/animations.css`)
Imported once in `index.css`. Referenced by name in inline styles:

- `spin` — 360° rotation (spinners)
- `fadeOut` — opacity 1→0
- `fadeIn` — opacity 0→1
- `fadeInUp` — opacity 0→1 + translateY(12px)→0 (primary stagger animation)
- `slideUp` — translateY(12px)→0
- `fadeOutDown` — opacity 1→0 + translateY(0)→12px

### CSS Module Keyframes (`animations.module.css`)
Component-specific animations tied to pseudo-elements:

- `shopPulse` / `shopPulseRed` — `::after` border pulse (opacity 0→0.7→0 over 2s, infinite)
- `shopBreathe` / `shopBreatheRed` — `::before` background breathe (opacity 0.5→1→0.5 over 3s, infinite)
- `spinnerFadeIn` — delayed spinner appearance (300ms)
- `choptIconPulse` — invalid score icon pulse
- `marqueeScroll` — text scrolling with dwell pauses at 5%/95%

### Stagger System

The stagger system animates page content entry with sequential `fadeInUp` delays.

#### `useStagger(shouldStagger, interval?)` — Primary stagger hook
Returns helpers for building stagger animation inline styles:
- `forDelay(delayMs)` — explicit delay
- `forIndex(index, offset?)` — index-based delay
- `next(offset?)` — auto-incrementing counter
- `clearAnim` — `onAnimationEnd` handler that removes inline animation styles

Usage: Pages call `useStagger(shouldStagger)` and pass delay values as style props:
```tsx
const { forDelay: stagger, clearAnim } = useStagger(!skipAnim);
<Card style={stagger(0)} onAnimationEnd={clearAnim}>...</Card>
<Card style={stagger(STAGGER_INTERVAL)} onAnimationEnd={clearAnim}>...</Card>
```

#### `useStaggerStyle(delayMs, options?)` — Single-element stagger
For individual elements. Returns `{ style, onAnimationEnd }`.

#### `buildStaggerStyle(delayMs, opts?)` — Non-hook stagger
For use inside `.map()` loops (not a hook). Same output as `useStaggerStyle`.

#### `staggerMs(index, step, offset?, base?)` — Pure delay calculator
Computes delay milliseconds for index-based lists.

#### `clearStaggerStyle(e)` — Standalone cleanup
Clears `opacity`, `animation`, `willChange` from `e.currentTarget`.

#### `useStaggerRush(containerRef)` — Scroll-triggered rush
When user scrolls during stagger animation, collapses all pending delays to 0ms so remaining items appear immediately. Prevents waiting during scroll.

### Proximity Glow (`useProximityGlow`)
Mouse-tracking glow effect on frosted cards:
- Single `mousemove` listener on `document.documentElement`
- Sets CSS variables (`--glow-x`, `--glow-y`, `--glow-opacity`, `--glow-hover`) on frosted cards
- `effects.module.css` uses `::before` pseudo-element with `radial-gradient` that follows cursor
- Cards are auto-discovered via `[style*="--frosted-card"]` selector (set by `frostedCard` mixin)
- Supports `data-glow-scope` for scoping glow to modal/popup regions
- Suppressed on touch devices via `@media (hover: none)`
- Zero React re-renders — all work in rAF callback

### Scroll Fade (`useScrollFade`)
Per-child `mask-image` fading at viewport scroll edges:
- Uses `IntersectionObserver` for efficient edge-child tracking
- 36px fade distance by default with exponential opacity curve
- CSS classes in `effects.module.css`: `fadeBottom`, `fadeTop`, `fadeBoth`

### Transitions
All transitions built with `transition()` / `transitions()` helpers:
```ts
transition(CssProp.opacity, TRANSITION_MS)          // "opacity 300ms ease"
transition(CssProp.backgroundColor, FAST_FADE_MS)   // "background-color 200ms ease"
transitions(
  transition(CssProp.color, NAV_TRANSITION_MS),
  transition(CssProp.transform, NAV_TRANSITION_MS),
)
```

---

## Global Styles

### `src/styles/theme.css`
CSS custom properties duplicating theme tokens for the few remaining CSS module files. **Not the source of truth** — `packages/theme/src/` TypeScript files are canonical. Contains:
- Color variables (`--color-bg-app`, `--color-text-primary`, etc.)
- Spacing variables (`--gap-xs` through `--gap-section`)
- Radius, font, weight, z-index, opacity variables
- Layout variables (`--sidebar-width: 240px`, `--layout-padding-h: 20px`)
- Carousel dimensions, duration aliases
- Glow variables (`--glow-color`, `--glow-size: 125px`)

### `src/index.css`
Global resets and base styles:
- Imports `theme.css` and `animations.css`
- Safe area inset CSS variables (`env(safe-area-inset-*)`)
- Universal box-sizing: `border-box`
- `overscroll-behavior: none` on html/body
- `color-scheme: dark`
- Body: `background-color: var(--color-bg-app)`, system font stack, font smoothing
- Link/button resets (unset all, remove tap highlight)
- Scrollbar hidden globally (`scrollbar-width: none`, `::-webkit-scrollbar { display: none }`)
- Recharts focus outline suppression
- `.sa-top` — safe area top padding for PWA
- `.fab-search-bar` / `.fab-player-footer` — dynamic right margin via `clamp()` to clear FAB at narrow widths

### `src/styles/animations.css`
Global `@keyframes` (see Animations section above). Imported once in `index.css`.

---

## Shared Style Files

| File | Purpose |
|---|---|
| `src/styles/songRowStyles.ts` | Shared `CSSProperties` objects for song row layouts (desktop/mobile variants, detail strip, metadata wrap, instrument chips) — used by SongRow + first-run demos |
| `src/styles/animations.module.css` | Shared animation classes (spinner, shop pulse/breathe, chopt pulse) |
| `src/styles/effects.module.css` | Shared effect classes (frosted glass, placeholder, hover, fade masks, proximity glow, responsive grids) |
| `src/styles/rivals.module.css` | Rival-specific pseudo-element overlays and container queries |
