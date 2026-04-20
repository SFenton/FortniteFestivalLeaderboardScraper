/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useMemo } from 'react';
import { Colors, IconSize, StarSize, Border, flexCenter, Display, Align, Justify, ObjectFit, Overflow, CssValue, border } from '@festival/theme';

const BASE = import.meta.env.BASE_URL;

type MiniStarsAlignment = 'start' | 'end';

const MiniStars = memo(function MiniStars({
  starsCount,
  isFullCombo,
  align = 'end',
}: {
  starsCount: number;
  isFullCombo: boolean;
  align?: MiniStarsAlignment;
}) {
  const allGold = starsCount >= 6;
  const displayCount = allGold ? 5 : Math.max(1, starsCount);
  const src = allGold ? `${BASE}star_gold.png` : `${BASE}star_white.png`;
  const outline = (isFullCombo || allGold) ? Colors.gold : 'transparent';
  const s = useStyles(outline, align);
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

function useStyles(outline: string, align: MiniStarsAlignment) {
  return useMemo(() => ({
    row: {
      display: Display.inlineFlex,
      gap: StarSize.gap,
      alignItems: Align.center,
      justifyContent: align === 'start' ? Justify.start : Justify.end,
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
  }), [align, outline]);
}
