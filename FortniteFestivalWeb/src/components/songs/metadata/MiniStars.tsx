/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useMemo } from 'react';
import { Colors, IconSize, StarSize, Border, flexCenter, Display, Align, Justify, ObjectFit, Overflow, CssValue, border } from '@festival/theme';

const BASE = import.meta.env.BASE_URL;

const MiniStars = memo(function MiniStars({ starsCount, isFullCombo }: { starsCount: number; isFullCombo: boolean }) {
  const allGold = starsCount >= 6;
  const displayCount = allGold ? 5 : Math.max(1, starsCount);
  const src = allGold ? `${BASE}star_gold.png` : `${BASE}star_white.png`;
  const outline = (isFullCombo || allGold) ? Colors.gold : 'transparent';
  const s = useStyles(outline);
  return (
    <span style={s.row}>
      {Array.from({ length: displayCount }).map((_, i) => (
        <span key={i} style={s.circle}>
          <img src={src} alt="★" style={s.icon} />
        </span>
      ))}
    </span>
  );
});

export default MiniStars;

function useStyles(outline: string) {
  return useMemo(() => ({
    row: {
      display: Display.inlineFlex,
      gap: StarSize.gap,
      alignItems: Align.center,
      justifyContent: Justify.end,
      width: StarSize.rowWidth,
    },
    circle: {
      width: IconSize.sm,
      height: IconSize.sm,
      borderRadius: CssValue.circle,
      border: border(Border.medium, outline),
      ...flexCenter,
      overflow: Overflow.hidden,
    },
    icon: {
      width: StarSize.icon,
      height: StarSize.icon,
      objectFit: ObjectFit.contain,
    },
  }), [outline]);
}
