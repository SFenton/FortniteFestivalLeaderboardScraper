import { memo, type CSSProperties } from 'react';
import { Colors, Size } from '@festival/theme';

const BASE = import.meta.env.BASE_URL;

const miniStarRow: CSSProperties = {
  display: 'inline-flex',
  gap: 3,
  alignItems: 'center',
  justifyContent: 'flex-end',
  width: 132,
};

const miniStarCircle: CSSProperties = {
  width: Size.iconSm,
  height: Size.iconSm,
  borderRadius: '50%',
  border: '1.5px solid transparent',
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
  overflow: 'hidden',
};

const miniStarIcon: CSSProperties = {
  width: 20,
  height: 20,
  objectFit: 'contain',
};

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
    <span style={miniStarRow}>
      {Array.from({ length: displayCount }).map((_, i) => (
        <span key={i} style={{ ...miniStarCircle, borderColor: outline }}>
          <img src={src} alt="★" style={miniStarIcon} />
        </span>
      ))}
    </span>
  );
});

export default MiniStars;
