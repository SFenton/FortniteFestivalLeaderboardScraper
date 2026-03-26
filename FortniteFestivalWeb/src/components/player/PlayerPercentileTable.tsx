/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useMemo } from 'react';
import { Colors, Font, Gap, Weight, Border, Display, Justify, Align, TextAlign, TextTransform, Cursor, CssValue, QUICK_FADE_MS, padding, border, transition } from '@festival/theme';
import PercentilePill from '../songs/metadata/PercentilePill';

export const PlayerPercentileHeader = memo(function PlayerPercentileHeader({
  percentileLabel,
  songsLabel,
}: {
  percentileLabel: string;
  songsLabel: string;
}) {
  const s = useStyles(false);
  return (
    <div style={s.headerRow}>
      <span style={s.headerText}>{percentileLabel}</span>
      <span style={s.headerTextRight}>{songsLabel}</span>
    </div>
  );
});

export const PlayerPercentileRow = memo(function PlayerPercentileRow({
  pct,
  count,
  isLast,
  onClick,
}: {
  pct: number;
  count: number;
  isLast: boolean;
  onClick: () => void;
}) {
  const s = useStyles(isLast);
  return (
    <div style={s.rowItem} onClick={onClick}>
      <span>
        <PercentilePill display={`Top ${pct}%`} />
      </span>
      <span style={s.countText}>{count}</span>
    </div>
  );
});

function useStyles(isLast: boolean) {
  return useMemo(() => {
    const headerText = {
      fontSize: Font.sm,
      fontWeight: Weight.semibold,
      color: Colors.textTertiary,
      textTransform: TextTransform.uppercase,
      letterSpacing: Font.letterSpacingWide,
    };
    return {
      headerRow: {
        display: Display.flex,
        justifyContent: Justify.between,
        padding: padding(Gap.md, Gap.xl),
        borderBottom: border(Border.thin, Colors.glassBorder),
      },
      headerText,
      headerTextRight: { ...headerText, textAlign: TextAlign.right },
      rowItem: {
        display: Display.flex,
        justifyContent: Justify.between,
        alignItems: Align.center,
        padding: padding(Gap.md, Gap.xl),
        borderBottom: isLast ? CssValue.none : border(Border.thin, Colors.glassBorder),
        cursor: Cursor.pointer,
        transition: transition('background-color', QUICK_FADE_MS),
        fontSize: Font.md,
        color: Colors.textPrimary,
        minWidth: Gap.none,
      },
      countText: {
        fontWeight: Weight.semibold,
      },
    };
  }, [isLast]);
}
