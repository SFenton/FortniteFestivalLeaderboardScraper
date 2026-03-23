/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { memo, type CSSProperties } from 'react';
import { Colors } from '@festival/theme';
import css from './MiniStars.module.css';

const BASE = import.meta.env.BASE_URL;

/**
 * Compact star display matching the mobile MiniStars component.
 * Shows 1-5 stars; 6+ stars renders 5 gold stars.
 */
const MiniStars = memo(function MiniStars({ starsCount, isFullCombo }: { starsCount: number; isFullCombo: boolean }) {
  const allGold = starsCount >= 6;
  const displayCount = allGold ? 5 : Math.max(1, starsCount);
  const src = allGold ? `${BASE}star_gold.png` : `${BASE}star_white.png`;
  const outline = (isFullCombo || allGold) ? Colors.gold : 'transparent';
  return (
    <span className={css.row}>
      {Array.from({ length: displayCount }).map((_, i) => (
        <span key={i} className={css.circle} style={{ '--star-outline': outline } as CSSProperties}>
          <img src={src} alt="★" className={css.icon} />
        </span>
      ))}
    </span>
  );
});

export default MiniStars;
