import { memo, useMemo, type CSSProperties } from 'react';
import {
  Colors, Font, Weight, Gap, Radius, Border, IconSize,
  Display, TextAlign,
  border, padding,
} from '@festival/theme';

const LABELS: Record<number, string> = { 0: 'E', 1: 'M', 2: 'H', 3: 'X' };
const BG: Record<number, string> = {
  0: Colors.diffPillEasy,
  1: Colors.diffPillMedium,
  2: Colors.diffPillHard,
  3: Colors.diffPillExpert,
};

export default memo(function DifficultyPill({ difficulty }: { difficulty: number }) {
  const s = useStyles(difficulty);
  return <span style={s.pill}>{LABELS[difficulty] ?? '?'}</span>;
});

function useStyles(difficulty: number) {
  return useMemo(() => ({
    pill: {
      flexShrink: 0,
      width: IconSize.md,
      textAlign: TextAlign.center,
      padding: padding(Gap.xs, Gap.sm),
      borderRadius: Radius.xs,
      backgroundColor: BG[difficulty] ?? Colors.surfaceSubtle,
      color: Colors.textPrimary,
      fontSize: Font.lg,
      fontWeight: Weight.semibold,
      lineHeight: '20px',
      border: border(Border.thick, Colors.borderSubtle),
      display: Display.inlineBlock,
    } as CSSProperties,
  }), [difficulty]);
}
