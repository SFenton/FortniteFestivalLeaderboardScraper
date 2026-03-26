/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useMemo, type CSSProperties } from 'react';
import { PercentileTier } from '@festival/core';
import { Colors, MetadataSize, Weight, goldOutline, goldOutlineSkew, Display, TextAlign, BoxSizing, CssValue, border, padding } from '@festival/theme';
import { Border, Gap, Radius } from '@festival/theme';

export interface PercentilePillProps {
  display: string | null | undefined;
}

const PercentilePill = memo(function PercentilePill({ display }: PercentilePillProps) {
  const s = useStyles();
  if (!display) return null;

  const isTop1 = display === PercentileTier.Top1;
  const isTop5 = !isTop1 && /^Top [2-5]%$/.test(display);

  const style = isTop1 ? s.top1 : isTop5 ? s.top5 : s.default;

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
