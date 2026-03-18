/**
 * Percentile badge/pill component with tier-based styling.
 * Reusable wherever "Top X%" badges appear.
 */
import { memo } from 'react';
import { PercentileTier } from '@festival/core';
import s from './PercentilePill.module.css';

export interface PercentilePillProps {
  /** Display text, e.g. "Top 1%", "Top 5%". */
  display: string | null | undefined;
}

const PercentilePill = memo(function PercentilePill({
  display,
}: PercentilePillProps) {
  if (!display) return null;

  const isTop1 = display === PercentileTier.Top1;
  const isTop5 = !isTop1 && /^Top [2-5]%$/.test(display);

  const className = isTop1 ? s.top1 : isTop5 ? s.top5 : s.default;

  return <span className={className}>{display}</span>;
});

export default PercentilePill;
