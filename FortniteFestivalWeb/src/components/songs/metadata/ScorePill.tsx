import { memo, useMemo, type CSSProperties } from 'react';
import {
  Colors, Font, Weight, Display, TextAlign, FontVariant,
} from '@festival/theme';

interface ScorePillProps {
  score: number;
  /** Fixed or dynamic width (e.g. "78px", "6ch"). Omit for auto. */
  width?: string;
  /** Use bold weight (SongRow style) vs semi-bold (leaderboard style). */
  bold?: boolean;
  className?: string;
  /** Text alignment override. Defaults to 'right'. */
  textAlign?: 'left' | 'right' | 'center';
}

const ScorePill = memo(function ScorePill({ score, width, bold, className, textAlign }: ScorePillProps) {
  const s = useStyles(bold, width, textAlign);
  return <span className={className} style={s.pill}>{score.toLocaleString()}</span>;
});

export default ScorePill;

function useStyles(bold?: boolean, width?: string, textAlign?: 'left' | 'right' | 'center') {
  return useMemo(() => ({
    pill: {
      textAlign: textAlign ?? TextAlign.right,
      fontSize: Font.lg,
      fontWeight: bold ? Weight.bold : Weight.semibold,
      color: Colors.textPrimary,
      fontVariantNumeric: FontVariant.tabularNums,
      flexShrink: 0,
      display: Display.inlineBlock,
      width,
    } as CSSProperties,
  }), [bold, width, textAlign]);
}
