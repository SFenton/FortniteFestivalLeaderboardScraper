import { Colors } from './colors';
import { Gap, Radius } from './spacing';

/** Gold fill — 1px border, filled background. Use as a style mixin. */
export const goldFill = {
  color: Colors.gold,
  backgroundColor: Colors.goldBg,
  border: `1px solid ${Colors.goldStroke}`,
} as const;

/** Gold outline — 2px border, transparent background. Use as a style mixin. */
export const goldOutline = {
  color: Colors.gold,
  backgroundColor: 'transparent',
  padding: `${Gap.xs}px ${Gap.sm}px`,
  borderRadius: Radius.xs,
  border: `2px solid ${Colors.goldStroke}`,
  fontWeight: 700,
  display: 'inline-block',
} as const;

/** Gold outline with italic skew (for FC / accuracy badges). */
export const goldOutlineSkew = {
  ...goldOutline,
  fontStyle: 'italic' as const,
  transform: 'skewX(-8deg)',
} as const;
