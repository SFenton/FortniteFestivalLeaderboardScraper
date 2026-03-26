/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useMemo } from 'react';
import { accuracyColor, ACCURACY_SCALE } from '@festival/core';
import { goldOutlineSkew, MetadataSize } from '@festival/theme';
import { formatAccuracyText } from '../../../utils/formatters';

export interface AccuracyDisplayProps {
  accuracy: number | null | undefined;
  isFullCombo?: boolean;
  fallback?: string;
}

const AccuracyDisplay = memo(function AccuracyDisplay({
  accuracy,
  isFullCombo,
  fallback = '\u2014',
}: AccuracyDisplayProps) {
  const s = useStyles();
  if (accuracy == null || accuracy <= 0) {
    return <>{fallback}</>;
  }

  const pct = accuracy / ACCURACY_SCALE;
  const text = formatAccuracyText(pct);

  if (isFullCombo) {
    return <span style={s.fcBadge}>{text}</span>;
  }

  return <span style={{ color: accuracyColor(pct) }}>{text}</span>;
});

export default AccuracyDisplay;

export { formatAccuracyText };

function useStyles() {
  return useMemo(() => ({
    fcBadge: {
      ...goldOutlineSkew,
      minWidth: MetadataSize.percentilePillMinWidth,
    },
  }), []);
}
