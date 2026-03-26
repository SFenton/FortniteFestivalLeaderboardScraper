/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * First-run demo: Overall summary stat boxes in the production 2-column grid.
 * Renders real StatBox components via buildOverallSummaryItems() with static data.
 */
import { type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { Radius, frostedCard, Gap, Overflow, PointerEvents, CssValue, GridTemplate } from '@festival/theme';
import { buildOverallSummaryItems, type OverallStats } from '../../sections/OverallSummarySection';
import { SERVER_INSTRUMENT_KEYS as INSTRUMENT_KEYS } from '@festival/core/api/serverTypes';
import FadeIn from '../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { useIsMobileChrome } from '../../../../hooks/ui/useIsMobile';
import { playerPageStyles as pps } from '../../../../components/player/playerPageStyles';

const DEMO_STATS: OverallStats = {
  songsPlayed: 142,
  fcCount: 38,
  fcPercent: '26.8',
  goldStarCount: 12,
  avgAccuracy: 962000,
  bestRank: 4,
  bestRankSongId: null,
  bestRankInstrument: null,
};

const TOTAL_SONGS = 206;
const CARD_HEIGHT = 100;
/* v8 ignore start -- NOOP is passed as prop but never invoked in test (pointerEvents: none) */
const NOOP = () => {};
/* v8 ignore stop */

const cardStyle: CSSProperties = { ...frostedCard, borderRadius: Radius.md };

export default function OverviewDemo() {
  const { t } = useTranslation();
  const h = useSlideHeight();
  const isMobile = useIsMobileChrome();

  const items = buildOverallSummaryItems(
    t, DEMO_STATS, TOTAL_SONGS, [...INSTRUMENT_KEYS], NOOP, NOOP, cardStyle,
  );

  // On mobile: single column, fit by height; on desktop: 2-column grid
  const cols = isMobile ? 1 : 2;
  const maxItems = h ? Math.max(1, Math.floor((h + Gap.md) / (CARD_HEIGHT + Gap.md)) * cols) : items.length;
  const visible = items.slice(0, maxItems);

  const gridStyle = isMobile
    ? { width: CssValue.full, overflow: Overflow.visible, pointerEvents: PointerEvents.none, gridTemplateColumns: GridTemplate.single }
    : { width: CssValue.full, overflow: Overflow.visible, pointerEvents: PointerEvents.none };

  return (
    <div style={{ ...pps.gridList, ...gridStyle }}>
      {visible.map((item, i) => (
        <FadeIn key={item.key} delay={i * 80}
          /* v8 ignore start -- item.span depends on buildOverallSummaryItems output; not all branches reachable with static data */
          style={item.span ? { ...pps.gridFullWidth, ...item.style } : item.style}
          /* v8 ignore stop */
        >
          {item.node}
        </FadeIn>
      ))}
    </div>
  );
}
