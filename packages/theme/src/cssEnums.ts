/**
 * CSS enum constants — eliminates raw string literals in useStyles.
 * Every CSS keyword value used in the app should live here.
 */

/* ── Display ── */
export const Display = {
  none: 'none',
  flex: 'flex',
  inlineFlex: 'inline-flex',
  block: 'block',
  inlineBlock: 'inline-block',
  grid: 'grid',
  contents: 'contents',
} as const;

/* ── Position ── */
export const Position = {
  relative: 'relative',
  absolute: 'absolute',
  fixed: 'fixed',
  sticky: 'sticky',
} as const;

/* ── FlexAlign / FlexJustify ── */
export const Align = {
  start: 'flex-start',
  end: 'flex-end',
  center: 'center',
  stretch: 'stretch',
  baseline: 'baseline',
} as const;

export const Justify = {
  start: 'flex-start',
  end: 'flex-end',
  center: 'center',
  between: 'space-between',
  around: 'space-around',
  evenly: 'space-evenly',
} as const;

/* ── Text ── */
export const TextAlign = {
  left: 'left',
  center: 'center',
  right: 'right',
} as const;

export const FontStyle = {
  normal: 'normal',
  italic: 'italic',
} as const;

export const TextTransform = {
  none: 'none',
  uppercase: 'uppercase',
  lowercase: 'lowercase',
  capitalize: 'capitalize',
} as const;

export const FontVariant = {
  tabularNums: 'tabular-nums',
  normal: 'normal',
} as const;

export const WordBreak = {
  breakWord: 'break-word',
  breakAll: 'break-all',
  normal: 'normal',
} as const;

export const WhiteSpace = {
  nowrap: 'nowrap',
  normal: 'normal',
  pre: 'pre',
  preWrap: 'pre-wrap',
} as const;

export const Isolation = {
  isolate: 'isolate',
  auto: 'auto',
} as const;

export const TransformOrigin = {
  bottomRight: 'bottom right',
  center: 'center',
  topLeft: 'top left',
} as const;

/* ── Box ── */
export const BoxSizing = {
  borderBox: 'border-box',
  contentBox: 'content-box',
} as const;

export const BorderStyle = {
  none: 'none',
  solid: 'solid',
  dashed: 'dashed',
  dotted: 'dotted',
} as const;

export const Overflow = {
  hidden: 'hidden',
  auto: 'auto',
  visible: 'visible',
  scroll: 'scroll',
} as const;

export const ObjectFit = {
  contain: 'contain',
  cover: 'cover',
  fill: 'fill',
  none: 'none',
} as const;

export const Cursor = {
  pointer: 'pointer',
  default: 'default',
  text: 'text',
  grab: 'grab',
} as const;

export const PointerEvents = {
  none: 'none',
  auto: 'auto',
} as const;

/* ── Common Values ── */
export const CssValue = {
  transparent: 'transparent',
  none: 'none',
  inherit: 'inherit',
  auto: 'auto',
  full: '100%',
  circle: '50%',
  marginCenter: '0 auto',
  viewportFull: '100vh',
} as const;

/* ── CSS Property Names (for transition/animation targets) ── */
export const CssProp = {
  opacity: 'opacity',
  color: 'color',
  transform: 'transform',
  backgroundColor: 'background-color',
  borderColor: 'border-color',
  boxShadow: 'box-shadow',
  width: 'width',
  height: 'height',
  gridTemplateRows: 'grid-template-rows',
  all: 'all',
} as const;

/* ── Grid ── */
export const GridTemplate = {
  single: '1fr',
  twoEqual: '1fr 1fr',
  threeEqual: '1fr 1fr 1fr',
  /** repeat(auto-fill, minmax(min(420px, 100%), 1fr)) — responsive 420px min cards. */
  autoFillInstrument: 'repeat(auto-fill, minmax(min(420px, 100%), 1fr))',
} as const;
