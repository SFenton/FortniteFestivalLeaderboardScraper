export const Radius = {
  xs: 8,
  sm: 10,
  md: 12,
  lg: 16,
  full: 999,
  progressBar: 3,
  barCorner: [4, 4, 0, 0] as readonly [number, number, number, number],
} as const;

export const Font = {
  xs: 11,
  sm: 12,
  md: 14,
  lg: 16,
  xl: 20,
  title: 22,
  '2xl': 24,
  display: 28,
  letterSpacingWide: 0.5,
} as const;

export const Weight = {
  normal: 400,
  semibold: 600,
  bold: 700,
  heavy: 800,
} as const;

export const ZIndex = {
  background: -1,
  base: 1,
  overlay: 2,
  spinner: 5,
  dropdown: 10,
  fixedFooter: 50,
  popover: 100,
  searchDropdown: 300,
  modalOverlay: 1000,
  confirmOverlay: 1100,
  changelogOverlay: 1200,
} as const;

export const LineHeight = {
  none: 0,
  sm: 16,
  md: 18,
  lg: 20,
  snug: 1.4,
  relaxed: 1.5,
  loose: 1.6,
} as const;

export const Gap = {
  none: 0,
  xs: 2,
  sm: 4,
  md: 8,
  lg: 10,
  xl: 12,
  section: 24,
  container: 64,
} as const;

export const Opacity = {
  none: 0,
  subtle: 0.1,
  dimmed: 0.3,
  faded: 0.4,
  disabled: 0.5,
  pressed: 0.85,
  backgroundImage: 0.9,
  icon: 0.92,
} as const;

export const Border = {
  thin: 1,
  medium: 1.5,
  thick: 2,
  spinner: 3,
  spinnerLg: 4,
} as const;

export const Shadow = {
  tooltip: '0 4px 12px rgba(0, 0, 0, 0.4)',
  elevated: '0 8px 24px rgba(0, 0, 0, 0.5)',
  frostedActive: 'inset 0 1px 0 rgba(255, 255, 255, 0.06), 0 4px 20px rgba(0, 0, 0, 0.4)',
} as const;

export const enum SpinnerSize {
  SM = 0,
  MD = 1,
  LG = 2,
}

export const Spinner = {
  [SpinnerSize.SM]: { size: 24, border: 3 },
  [SpinnerSize.MD]: { size: 36, border: 3 },
  [SpinnerSize.LG]: { size: 48, border: 4 },
  trackColor: 'rgba(255, 255, 255, 0.10)',
  duration: '0.8s',
} as const;

export const IconSize = {
  xs: 14,
  sm: 24,
  md: 28,
  lg: 40,
  xl: 48,
  profile: 32,
  default: 20,
  fab: 18,
  tab: 20,
  chevron: 16,
  back: 22,
  nav: 22,
  action: 16,
} as const;

export const InstrumentSize = {
  xs: 28,
  sm: 36,
  md: 48,
  lg: 56,
  button: 48,
  chip: 34,
} as const;

export const StarSize = {
  inline: 14,
  icon: 20,
  rowWidth: 132,
  gap: 3,
} as const;

export const AlbumArtSize = {
  collapsed: 80,
  expanded: 120,
} as const;

export const MetadataSize = {
  pillMinWidth: 80,
  percentilePillMinWidth: '5.5em',
  valuePillMinWidth: '5em',
  accuracyPillMinWidth: '3.75em',
  dotSize: 8,
  dotActiveScale: 1.25,
  dotRadius: 4,
  dotRadiusActive: 6,
  control: 34,
} as const;

export const ChartSize = {
  height: 320,
  barSelectionStroke: 3,
} as const;

export const GeneralSize = {
  thumb: 44,
} as const;

/** @deprecated Use IconSize, InstrumentSize, StarSize, AlbumArtSize, MetadataSize, ChartSize, GeneralSize instead. */
export const Size = {
  thumb: GeneralSize.thumb,
  iconLg: IconSize.lg,
  iconMd: IconSize.md,
  iconSm: IconSize.sm,
  iconXs: IconSize.xs,
  iconXl: IconSize.xl,
  iconDefault: IconSize.default,
  iconFab: IconSize.fab,
  iconTab: IconSize.tab,
  iconChevron: IconSize.chevron,
  iconBack: IconSize.back,
  iconNav: IconSize.nav,
  iconAction: IconSize.action,
  iconInstrumentXs: InstrumentSize.xs,
  iconInstrumentSm: InstrumentSize.sm,
  iconInstrument: InstrumentSize.md,
  iconInstrumentLg: InstrumentSize.lg,
  starInline: StarSize.inline,
  starIcon: StarSize.icon,
  starRowWidth: StarSize.rowWidth,
  starGap: StarSize.gap,
  albumArtCollapsed: AlbumArtSize.collapsed,
  albumArtExpanded: AlbumArtSize.expanded,
  control: MetadataSize.control,
  pillMinWidth: MetadataSize.pillMinWidth,
  chartHeight: ChartSize.height,
  dotRadius: MetadataSize.dotRadius,
  dotRadiusActive: MetadataSize.dotRadiusActive,
  barSelectionStroke: ChartSize.barSelectionStroke,
  instrumentBtn: InstrumentSize.button,
  profileCircleSize: 64,
  settingsSliderPadding: 48,
} as const;

export const MaxWidth = {
  card: 1400,
  grid: 2170,
  narrow: 600,
} as const;

export const Layout = {
  paddingHorizontal: 20,
  paddingTop: 16,
  paddingBottom: 4,
  fabPaddingBottom: 96,
  sectionHeadingHeight: 64,
  songRowHeight: 72,
  demoRowHeight: 64,
  demoCardHeight: 100,
  demoRowMobileHeight: 72,
  demoRowMobileIconsHeight: 160,
  demoRowMobileMetaHeight: 160,
  demoRowGap: 4,
  pinnedSidebarItemHeight: 52,
  sidebarItemHeight: 48,
  bottomNavTabMinWidth: 80,
  sortModeRowHeight: 44,
  sortDirectionHeight: 60,
  sortHeaderHeight: 50,
  sortHintPadding: 20,
  filterInstrumentRowHeight: 70,
  demoInstrumentBtn: 64,
  filterHeaderHeight: 30,
  filterToggleRowHeight: 56,
  chartMargin: { top: 16, right: 24, bottom: 0, left: 24 },
  axisLabelOffset: 8,
  /** Height of the sticky desktop nav bar (paddingTop + entryRowHeight + paddingBottom). */
  desktopNavHeight: 72,
  /** Content visible height minus shell chrome (header ~64 + bottom nav ~80 + padding ~56). */
  shellChromeHeight: 200,
  /** Min-height for centered page messages (empty states, loading hints). */
  pageMessageMinHeight: '40vh',
  /** Min-height for error fallback pages. */
  errorFallbackMinHeight: '60vh',
  /** Width for rank column in leaderboard/history rows. */
  rankColumnWidth: 48,
  /** Approximate pixel width of one tabular-numeral character at Font.md (14px). */
  rankCharWidth: 8.5,
  /** Right padding (px) added to dynamic rank column widths. */
  rankColumnPadding: 12,
  /** Width for accuracy column in leaderboard/history rows. */
  accColumnWidth: 64,
  /** Horizontal padding for pill-shaped action buttons. */
  buttonPaddingH: 32,
  /** Height for pill-shaped action buttons (View Paths, Item Shop). */
  pillButtonHeight: 48,
  /** Height for the desktop Item Shop pill in ShopButtonDemo. */
  shopDesktopHeight: 72,
  /** Size of the mobile Item Shop circle icon button. */
  shopCircleSize: 128,
  /** Fixed width for the leading icon cell in mobile headers (hamburger / back chevron). */
  headerIconSlot: 28,
  /** Shared negative left margin for mobile header icon optical alignment. */
  headerIconNudge: -6,
  /** @deprecated Use headerIconNudge instead. */
  backLinkNudge: -6,
  /** Height for progress bar tracks (SyncBanner). */
  progressBarHeight: 6,
  /** Bottom offset for FAB container. */
  fabBottom: 80,
  /** Width/height of the FAB button. */
  fabSize: 56,
  /** Bottom offset for FAB popup menu. */
  fabMenuBottom: 64,
  /** Min-width for the FAB action menu. */
  fabMenuMinWidth: 200,
  /** Height for rival song row top section. */
  rivalTopRowHeight: 64,
  /** Min-width for score columns in rival comparison. */
  scoreColumnMinWidth: 56,
  /** Height for instrument card entry rows. */
  entryRowHeight: 48,
  /** Extra scroll padding when mobile pagination bar is fixed above the FAB spacer. */
  paginationHeight: 56,
  /** Height for player song rows. */
  playerSongRowHeight: 64,

  /** Search input + FAB height. */
  searchInputHeight: 56,
  /** Left padding for shop button pill text. */
  shopButtonPaddingLeft: 36,
  /** Width/height of legend swatch squares. */
  legendSwatchSize: 12,
  /** Font size for chart axis ticks (SVG). */
  chartTickFontSize: 14,
  /** Vertical offset for rotated X-axis labels. */
  chartTickOffset: 16,
  /** X-axis height to accommodate rotated labels. */
  chartXAxisHeight: 60,
  /** X-axis label rotation angle. */
  chartXAxisAngle: -35,
  /** Width of the pinned sidebar on wide desktop. */
  sidebarWidth: 240,
  /** Horizontal padding for pinned (wide desktop) mode. */
  paddingHorizontalPinned: 10,
  /** Max-width for desktop header search input. */
  searchMaxWidth: 320,
  /** Max-height for search dropdown results. */
  searchDropdownMaxHeight: 400,
  /** Min-width for bottom nav tab buttons. */
  bottomNavTabButtonMin: 64,
  /** Height for instrument chip circles in category cards. */
  instrumentChipSize: 34,
  /** Minimum width for accuracy display in category cards. */
  unfcMinWidth: 48,
  /** Size for star PNG images in category rows. */
  starPngSize: 20,
  /** Max-width for the first-run carousel card. */
  carouselMaxWidth: 520,
  /** Carousel card height (desktop). */
  carouselHeight: '80vh' as string,
  /** Carousel card max-height (desktop). */
  carouselMaxHeight: 720,
  /** Carousel card min-height. */
  carouselMinHeight: 400,
  /** Carousel card height (mobile). */
  carouselHeightMobile: '85vh' as string,
  /** Carousel card max-height (mobile). */
  carouselMaxHeightMobile: 640,
  /** Size of circular close buttons. */
  buttonCloseSize: 36,
  /** Size of circular navigation arrow buttons. */
  buttonNavSize: 40,
  /** Size of modal close button circle. */
  modalCloseSize: 32,
  /** Size of radio button dot. */
  radioDotSize: 18,
  /** Width of toggle switch track (standard). */
  toggleTrackWidth: 36,
  /** Height of toggle switch track (standard). */
  toggleTrackHeight: 20,
  /** Radius of toggle switch track (standard). */
  toggleTrackRadius: 10,
  /** Size of toggle switch thumb (standard). */
  toggleThumbSize: 16,
  /** Inset of toggle thumb from track edge. */
  toggleThumbInset: 2,
  /** Offset of thumb when toggle is ON (standard). */
  toggleThumbOnOffset: 18,
  /** Width of toggle switch track (large). */
  toggleTrackLargeWidth: 44,
  /** Height of toggle switch track (large). */
  toggleTrackLargeHeight: 24,
  /** Radius of toggle switch track (large). */
  toggleTrackLargeRadius: 12,
  /** Size of toggle switch thumb (large). */
  toggleThumbLargeSize: 20,
  /** Offset of thumb when large toggle is ON. */
  toggleThumbLargeOnOffset: 22,
  /** Desktop modal panel z-index (overlay + 1). */
  modalPanelZ: 1001,
  /** Minimum width for the confirm alert dialog. */
  confirmMinWidth: 340,
  /** Max-width for the confirm alert dialog. */
  confirmMaxWidth: 340,
  /** Max-width for the changelog modal. */
  changelogMaxWidth: 520,
  /** Max-height for the changelog modal. */
  changelogMaxHeight: '80vh' as string,
  /** Size of modal close button circle. */
  closeBtnSize: 32,
} as const;
