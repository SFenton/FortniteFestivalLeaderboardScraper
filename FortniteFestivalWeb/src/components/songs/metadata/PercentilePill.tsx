/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useMemo, type CSSProperties } from 'react';
import { Colors, MetadataSize, Weight, goldOutline, goldOutlineSkew, Display, TextAlign, BoxSizing, CssValue, border, padding } from '@festival/theme';
import { Border, Gap, Radius } from '@festival/theme';

export interface PercentilePillProps {
  display: string | null | undefined;
  color?: string;
}

const PercentilePill = memo(function PercentilePill({ display, color }: PercentilePillProps) {
  const s = useStyles();
  if (!display) return null;

  const match = display.match(/^Top\s+([\d.]+)%$/);
  const pct = match ? parseFloat(match[1]) : NaN;
  const style = !isNaN(pct) && pct <= 1 ? s.top1 : !isNaN(pct) && pct <= 5 ? s.top5 : color ? { ...s.default, backgroundColor: color, color: Colors.textPrimary } : s.default;

  return <span style={style}>{display}</span>;
});

export default PercentilePill;

function useStyles() {
  return useMemo(() => {
    const base: CSSProperties = {
      padding: padding(Gap.xs, Gap.sm),
      borderRadius: Radius.xs,
      display: Display.inlineBlock,
      textAlign: TextAlign.center,
      boxSizing: BoxSizing.borderBox,
      minWidth: MetadataSize.percentilePillMinWidth,
    };
    return {
      top1: {
        ...base,
        ...goldOutlineSkew,
        minWidth: MetadataSize.percentilePillMinWidth,
      } as CSSProperties,
      top5: {
        ...base,
        ...goldOutline,
        minWidth: MetadataSize.percentilePillMinWidth,
      } as CSSProperties,
      default: {
        ...base,
        color: Colors.textSecondary,
        backgroundColor: Colors.surfaceWhiteSubtle,
        border: border(Border.thick, CssValue.transparent),
        fontWeight: Weight.semibold,
      } as CSSProperties,
    };
  }, []);
}
