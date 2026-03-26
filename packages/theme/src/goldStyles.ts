import { Colors } from './colors';
import { Gap, Radius, Border, Weight } from './spacing';
import { Display, FontStyle, CssValue, BoxSizing, TextAlign } from './cssEnums';
import { border, padding } from './cssHelpers';

/** Skew angle for gold FC/top-tier badges. */
export const GOLD_SKEW = 'skewX(-8deg)';

/** Gold fill — 1px border, filled background. Use as a style mixin. */
export const goldFill = {
  color: Colors.gold,
  backgroundColor: Colors.goldBg,
  border: border(Border.thin, Colors.goldStroke),
} as const;

/** Gold outline — 2px border, transparent background. Use as a style mixin. */
export const goldOutline = {
  color: Colors.gold,
  backgroundColor: CssValue.transparent,
  padding: padding(Gap.xs, Gap.sm),
  borderRadius: Radius.xs,
  border: border(Border.thick, Colors.goldStroke),
  fontWeight: Weight.bold,
  display: Display.inlineBlock,
  textAlign: TextAlign.center,
  boxSizing: BoxSizing.borderBox,
} as const;

/** Gold outline with italic skew (for FC / accuracy badges). */
export const goldOutlineSkew = {
  ...goldOutline,
  fontStyle: FontStyle.italic,
  transform: GOLD_SKEW,
} as const;
