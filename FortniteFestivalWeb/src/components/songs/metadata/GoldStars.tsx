/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Gold star row display — shows 5 gold star PNGs inline.
 * Extracted from PlayerPage for reuse.
 */
import { memo } from 'react';
import { Size, Gap } from '@festival/theme';

const BASE = import.meta.env.BASE_URL;

export interface GoldStarsProps {
  /** Size of each star image in px. Default: 18 */
  size?: number;
  /** Number of stars to show. Default: 5 */
  count?: number;
}

const GoldStars = memo(function GoldStars({ size = Size.iconDefault, count = 5 }: GoldStarsProps) {
  return (
    <span style={{ display: 'inline-flex', gap: Gap.xs }}>
      {Array.from({ length: count }, (_, i) => (
        <img key={i} src={`${BASE}star_gold.png`} alt="★" width={size} height={size} />
      ))}
    </span>
  );
});

export default GoldStars;
