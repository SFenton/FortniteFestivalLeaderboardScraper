export const Colors = {
  // Backgrounds
  backgroundApp: '#1A0830',
  backgroundCard: '#0B1220',
  backgroundBlack: '#000000',
  backgroundBoot: '#0B0B0D',
  backgroundCardAlt: '#111827',
  backgroundCardAlt2: '#0F172A',

  // Surfaces
  surfaceFrosted: 'rgba(18,24,38,0.78)',
  surfaceElevated: '#1A2940',
  surfaceSubtle: '#162133',
  surfacePressed: '#101826',
  surfaceMuted: '#223047',

  // Overlays
  overlayModal: 'rgba(0,0,0,0.55)',
  overlayScrim: 'rgba(0,0,0,0.35)',
  overlayDark: 'rgba(0,0,0,0.7)',

  // Text
  textPrimary: '#FFFFFF',
  textSecondary: '#D7DEE8',
  textTertiary: '#9AA6B2',
  textMuted: '#8899AA',
  textSubtle: '#B8C0CC',
  textDisabled: '#607089',
  textNearWhite: '#F2F6FF',
  textSemiTransparent: 'rgba(255,255,255,0.78)',
  textPlaceholder: 'rgba(255,255,255,0.4)',
  textVeryMuted: '#556677',
  textMutedCaption: '#9CA3AF',

  // Borders
  borderPrimary: '#2B3B55',
  borderCard: '#263244',
  borderSubtle: '#1E2A3A',
  borderSeparator: '#1A2535',

  // Accent / Brand
  accentBlue: '#2D82E6',
  accentBlueBright: '#4C7DFF',
  accentBlueDark: '#1A5FB4',
  accentPurple: '#7C3AED',
  accentPurpleDark: '#4B0F63',

  // Gold / Full Combo
  gold: '#FFD700',
  goldBg: '#332915',
  goldStroke: '#CFA500',

  // Score Badges
  badgeBlueBg: '#1D3A71',

  // Status
  statusGreen: '#2ECC71',
  statusGreenStroke: '#1E7F46',
  statusRed: '#C62828',
  statusRedStroke: '#8B0000',

  // Distribution Chart
  chartTop1: '#27ae60',
  chartTop5: '#2ecc71',
  chartTop10: '#f1c40f',
  chartTop25: '#e67e22',
  chartTop50: '#e74c3c',
  chartBelow50: '#7f8c8d',

  // Difficulty Badges
  diffEasyBg: '#1B3A2F',
  diffEasyAccent: '#34D399',
  diffMediumBg: '#3A351B',
  diffMediumAccent: '#FBBF24',
  diffHardBg: '#3A1B1B',
  diffHardAccent: '#F87171',
  diffExpertBg: '#2D1B3A',
  diffExpertAccent: '#C084FC',

  // Semantic Buttons
  dangerBg: 'rgba(198,40,40,0.4)',
  successBg: 'rgba(40,167,69,0.4)',
  chipSelectedBg: 'rgba(45,130,230,0.4)',
  chipSelectedBgSubtle: 'rgba(45,130,230,0.18)',
  purpleButtonBg: 'rgba(124,58,237,0.4)',

  // Misc
  transparent: 'transparent',
  whiteOverlaySubtle: 'rgba(255,255,255,0.08)',
  whiteOverlay: 'rgba(255,255,255,0.18)',
  purplePlaceholder: 'rgba(122,43,149,0.3)',
  cardOverlay: 'rgba(34,48,71,0.6)',
  purpleTabActive: '#7A2B95',
} as const;

export type ColorKey = keyof typeof Colors;
