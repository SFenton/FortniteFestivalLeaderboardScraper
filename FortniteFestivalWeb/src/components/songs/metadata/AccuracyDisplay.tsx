/**
 * Displays an accuracy percentage with color interpolation (red→green).
 * Shows a gold FC badge when the player has a full combo.
 */
import { memo } from 'react';
import { accuracyColor, ACCURACY_SCALE } from '@festival/core';
import { formatAccuracyText } from '../../../utils/formatters';
import s from './AccuracyDisplay.module.css';

export interface AccuracyDisplayProps {
  /** Raw accuracy value (percent × 10,000: e.g. 98.76% = 987600). */
  accuracy: number | null | undefined;
  /** Whether this score achieved a full combo. */
  isFullCombo?: boolean;
  /** Fallback text when accuracy is null/0. Default: '—' */
  fallback?: string;
}

const AccuracyDisplay = memo(function AccuracyDisplay({
  accuracy,
  isFullCombo,
  fallback = '\u2014',
}: AccuracyDisplayProps) {
  if (accuracy == null || accuracy <= 0) {
    return <>{fallback}</>;
  }

  const pct = accuracy / ACCURACY_SCALE;
  const text = formatAccuracyText(pct);

  if (isFullCombo) {
    return <span className={s.fcBadge}>{text}</span>;
  }

  return <span style={{ color: accuracyColor(pct) }}>{text}</span>;
});

export default AccuracyDisplay;

export { formatAccuracyText };
