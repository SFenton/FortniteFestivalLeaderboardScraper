/**
 * Centralized design tokens for the Festival app.
 *
 * Usage:
 * ```ts
 * import {Radius, Font, Gap, Opacity, Layout} from '@festival/ui';
 * // …
 * borderRadius: Radius.md,
 * fontSize: Font.md,
 * gap: Gap.md,
 * opacity: Opacity.pressed,
 * paddingHorizontal: Layout.paddingHorizontal,
 * ```
 */

// ── Border Radius ──────────────────────────────────────────────────────

export const Radius = {
  /** 8 — small chips, pills, badges */
  xs: 8,
  /** 10 — inputs, thumbnails, pill badges */
  sm: 10,
  /** 12 — buttons, controls, cards */
  md: 12,
  /** 16 — frosted surfaces, large cards */
  lg: 16,
  /** 999 — capsule / full-round shapes */
  full: 999,
} as const;

// ── Font Size ──────────────────────────────────────────────────────────

export const Font = {
  /** 11 — tiny labels, bar values */
  xs: 11,
  /** 12 — captions, meta text, badge text */
  sm: 12,
  /** 14 — body text, row titles, subtitles, descriptions */
  md: 14,
  /** 16 — section / card titles, button labels */
  lg: 16,
  /** 20 — large section headers */
  xl: 20,
  /** 22 — screen / modal titles */
  title: 22,
} as const;

// ── Line Height ────────────────────────────────────────────────────────

export const LineHeight = {
  /** 16 — pairs with Font.sm (12) */
  sm: 16,
  /** 18 — pairs with Font.md (14) */
  md: 18,
  /** 20 — pairs with Font.md/lg (14–16) */
  lg: 20,
} as const;

// ── Gap / Spacing Scale ────────────────────────────────────────────────

export const Gap = {
  /** 2 — micro gaps (text stacks, bar spacing) */
  xs: 2,
  /** 4 — tight inline gaps */
  sm: 4,
  /** 8 — standard gap (card body, control rows) */
  md: 8,
  /** 10 — list/row gaps, medium separation */
  lg: 10,
  /** 12 — section-level gaps, card padding */
  xl: 12,
  /** 24 — large modal / section spacing */
  section: 24,
} as const;

// ── Opacity ────────────────────────────────────────────────────────────

export const Opacity = {
  /** 0.85 — pressed state, secondary text */
  pressed: 0.85,
  /** 0.5 — disabled controls */
  disabled: 0.5,
  /** 0.92 — default icon opacity */
  icon: 0.92,
} as const;

// ── Fixed Sizes ────────────────────────────────────────────────────────

export const Size = {
  /** 44 — song row thumbnail */
  thumb: 44,
  /** 40 — instrument icons, filter circles */
  iconLg: 40,
  /** 28 — card header icons */
  iconMd: 28,
  /** 24 — small inline icons */
  iconSm: 24,
  /** 34 — square order buttons, compact thumbs */
  control: 34,
  /** 80 — score badge pill minimum width */
  pillMinWidth: 80,
} as const;

// ── Max Widths ─────────────────────────────────────────────────────────

export const MaxWidth = {
  /** 1080 — standard card container */
  card: 1080,
  /** 2170 — wide grid layouts */
  grid: 2170,
  /** 600 — settings, narrow card */
  narrow: 600,
} as const;

// ── Screen-Level Layout ────────────────────────────────────────────────

export const Layout = {
  /** 20 — horizontal padding for screen-level containers */
  paddingHorizontal: 20,
  /** 16 — top padding for screen-level containers */
  paddingTop: 16,
  /** 4 — bottom padding for screen-level containers */
  paddingBottom: 4,
  /** 32 — default height of the fade gradient (FadeScrollView) */
  fadeHeight: 32,
} as const;
