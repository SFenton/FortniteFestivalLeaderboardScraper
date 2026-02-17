/**
 * Centralized color constants for the Festival app.
 *
 * Usage:
 * ```ts
 * import {Colors} from '@festival/ui';
 * // …
 * color: Colors.textPrimary,
 * ```
 *
 * When adding a new color, check whether an existing token already covers the
 * use-case before introducing a new one.
 */
export const Colors = {
  // ── Backgrounds ──────────────────────────────────────────
  /** Main app background (deep purple). */
  backgroundApp: '#1A0830',
  /** Dark card / input surface. */
  backgroundCard: '#0B1220',
  /** Pure black — animated bg, scroll fade, nav content. */
  backgroundBlack: '#000000',
  /** Boot overlay background. */
  backgroundBoot: '#0B0B0D',
  /** Alternate dark card bg. */
  backgroundCardAlt: '#111827',
  /** Alternate dark card bg (darker). */
  backgroundCardAlt2: '#0F172A',

  // ── Surfaces ─────────────────────────────────────────────
  /** Frosted glass / card bg (default opacity). */
  surfaceFrosted: 'rgba(18,24,38,0.78)',
  /** Frosted glass card bg (Windows, more opaque). */
  surfaceFrostedWindows: 'rgba(18,24,38,0.97)',
  /** Frosted tab bar bg. */
  surfaceTabBar: 'rgba(18,24,38,0.72)',
  /** Elevated card / footer surface. */
  surfaceElevated: '#1A2940',
  /** Subtle surface — hamburger button, drawer item active. */
  surfaceSubtle: '#162133',
  /** Dark pressed state surface. */
  surfacePressed: '#101826',
  /** Muted panel bg. */
  surfaceMuted: '#223047',

  // ── Overlays ─────────────────────────────────────────────
  /** Modal backdrop overlay. */
  overlayModal: 'rgba(0,0,0,0.55)',
  /** Scrim / dimming overlay. */
  overlayScrim: 'rgba(0,0,0,0.35)',
  /** Dark overlay behind content. */
  overlayDark: 'rgba(0,0,0,0.7)',

  // ── Text ─────────────────────────────────────────────────
  /** Primary text — titles, labels, button text. */
  textPrimary: '#FFFFFF',
  /** Secondary text — subtitles, hints, descriptions. */
  textSecondary: '#D7DEE8',
  /** Tertiary text — inactive tabs, subtle labels. */
  textTertiary: '#9AA6B2',
  /** Muted text — icon tint, switch thumb off, descriptors. */
  textMuted: '#8899AA',
  /** Subtle text — artist text, light muted. */
  textSubtle: '#B8C0CC',
  /** Disabled / placeholder text. */
  textDisabled: '#607089',
  /** Near-white text — used for subtitles. */
  textNearWhite: '#F2F6FF',
  /** Semi-transparent white text. */
  textSemiTransparent: 'rgba(255,255,255,0.78)',
  /** Placeholder input text. */
  textPlaceholder: 'rgba(255,255,255,0.4)',
  /** Very muted text — instrument separators. */
  textVeryMuted: '#556677',
  /** Muted label text — sync screen. */
  textMutedAlt: '#92A0B2',
  /** Muted caption text — suggestions. */
  textMutedCaption: '#9CA3AF',

  // ── Borders ──────────────────────────────────────────────
  /** Primary border — input borders, choice pills, tabs. */
  borderPrimary: '#2B3B55',
  /** Card / surface border, switch track off. */
  borderCard: '#263244',
  /** Subtle border — dividers, hairlines. */
  borderSubtle: '#1E2A3A',
  /** Footer top separator. */
  borderSeparator: '#1A2535',

  // ── Accent / Brand ──────────────────────────────────────
  /** Blue accent — active state, selected sort/filter icon. */
  accentBlue: '#2D82E6',
  /** Bright blue — primary action button, slider fill. */
  accentBlueBright: '#4C7DFF',
  /** Darker blue — button border. */
  accentBlueDark: '#1A5FB4',
  /** Purple accent — nav theme primary & notification. */
  accentPurple: '#7C3AED',
  /** Dark purple — instrument card header bg. */
  accentPurpleDark: '#4B0F63',

  // ── Gold / Full Combo ────────────────────────────────────
  /** Gold accent — full-combo border, text, outline. */
  gold: '#FFD700',
  /** Gold badge dark background. */
  goldBg: '#332915',
  /** Gold darker stroke. */
  goldStroke: '#CFA500',

  // ── Score Badges ─────────────────────────────────────────
  /** Default score badge / pill background. */
  badgeBlueBg: '#1D3A71',

  // ── Status (instrument visuals) ──────────────────────────
  /** Green — has score indicator fill. */
  statusGreen: '#2ECC71',
  /** Green — has score indicator stroke. */
  statusGreenStroke: '#1E7F46',
  /** Red — no score indicator fill. */
  statusRed: '#C62828',
  /** Red — no score indicator stroke. */
  statusRedStroke: '#8B0000',

  // ── Distribution Chart ───────────────────────────────────
  chartTop1: '#27ae60',
  chartTop5: '#2ecc71',
  chartTop10: '#f1c40f',
  chartTop25: '#e67e22',
  chartTop50: '#e74c3c',
  chartBelow50: '#7f8c8d',

  // ── Difficulty Badges ────────────────────────────────────
  diffEasyBg: '#1B3A2F',
  diffEasyAccent: '#34D399',
  diffMediumBg: '#3A351B',
  diffMediumAccent: '#FBBF24',
  diffHardBg: '#3A1B1B',
  diffHardAccent: '#F87171',
  diffExpertBg: '#2D1B3A',
  diffExpertAccent: '#C084FC',

  // ── Switch / Toggle ──────────────────────────────────────
  switchTrackOn: 'rgba(45,130,230,1)',
  /** Switch track off matches borderCard. */
  switchTrackOff: '#263244',
  starEmpty: '#666666',

  // ── Semantic Buttons (rgba) ──────────────────────────────
  /** Danger / delete / destructive button bg & border. */
  dangerBg: 'rgba(198,40,40,0.4)',
  /** Success / confirm button bg. */
  successBg: 'rgba(40,167,69,0.4)',
  /** Selected chip bg (blue). */
  chipSelectedBg: 'rgba(45,130,230,0.4)',
  /** Subtle chip bg (blue, lighter). */
  chipSelectedBgSubtle: 'rgba(45,130,230,0.18)',
  /** Purple button bg. */
  purpleButtonBg: 'rgba(124,58,237,0.4)',

  // ── Misc ─────────────────────────────────────────────────
  /** Transparent. */
  transparent: 'transparent',
  /** Very subtle white overlay (stats cards). */
  whiteOverlaySubtle: 'rgba(255,255,255,0.08)',
  /** White overlay (progress bar bg). */
  whiteOverlay: 'rgba(255,255,255,0.18)',
  /** Purple placeholder bg (sliding rows). */
  purplePlaceholder: 'rgba(122,43,149,0.3)',
  /** Muted card overlay bg (settings). */
  cardOverlay: 'rgba(34,48,71,0.6)',
  /** Purple tab active bg. */
  purpleTabActive: '#7A2B95',
} as const;

/** Type-safe color key for programmatic use. */
export type ColorKey = keyof typeof Colors;
