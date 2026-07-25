import { memo, useMemo, type CSSProperties } from 'react';
import { accuracyBgColor } from '@festival/core/app/formatters';
import { ACCURACY_SCALE } from '@festival/core/runtime';
import { Colors, MetadataSize, Weight, goldOutlineSkew, Display, TextAlign, BoxSizing, CssValue, border, padding } from '@festival/theme';
import { Border, Gap, Radius } from '@festival/theme';
import { formatAccuracyText } from '../../../utils/formatters';

export interface AccuracyDisplayProps {
  accuracy: number | null | undefined;
  isFullCombo?: boolean;
}

const AccuracyDisplay = memo(function AccuracyDisplay({
  accuracy,
  isFullCombo,
}: AccuracyDisplayProps) {
  const s = useStyles();
  const pct = (accuracy != null && accuracy > 0) ? accuracy / ACCURACY_SCALE : 0;
  const text = formatAccuracyText(pct);

  if (isFullCombo) {
    return <span style={s.fcBadge}>{text}</span>;
  }

  return <span style={{ ...s.accuracyPill, backgroundColor: accuracyBgColor(pct) }}>{text}</span>;
});

export default AccuracyDisplay;

export { formatAccuracyText };

function useStyles() {
  return useMemo(() => ({
    fcBadge: {
      ...goldOutlineSkew,
      minWidth: MetadataSize.accuracyPillMinWidth,
    } as CSSProperties,
    accuracyPill: {
      padding: padding(Gap.xs, Gap.sm),
      borderRadius: Radius.xs,
      display: Display.inlineBlock,
      textAlign: TextAlign.center,
      boxSizing: BoxSizing.borderBox,
      minWidth: MetadataSize.accuracyPillMinWidth,
      fontWeight: Weight.semibold,
      color: Colors.textPrimary,
      border: border(Border.thick, CssValue.transparent),
    } as CSSProperties,
  }), []);
}
