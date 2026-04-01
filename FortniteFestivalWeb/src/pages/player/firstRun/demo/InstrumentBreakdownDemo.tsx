/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * First-run demo: Per-instrument stat cards in the production 2-column grid.
 * Renders a single instrument's stats via buildInstrumentStatsItems() with static data.
 */
import { type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { Radius, frostedCard, Gap } from '@festival/theme';
import { buildInstrumentStatsItems } from '../../sections/InstrumentStatsSection';
import { computeInstrumentStats } from '../../helpers/playerStats';
import type { PlayerScore } from '@festival/core/api/serverTypes';
import FadeIn from '../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { useIsMobileChrome } from '../../../../hooks/ui/useIsMobile';
import { playerPageStyles as pps } from '../../../../components/player/playerPageStyles';

const TOTAL_SONGS = 206;
const CARD_HEIGHT = 100;
const HEADER_HEIGHT = 64;
/* v8 ignore start -- NOOP is passed as prop but never invoked in test (pointerEvents: none) */
const NOOP = () => {};
/* v8 ignore stop */

const cardStyle: CSSProperties = { ...frostedCard, borderRadius: Radius.md };

/** Static scores for one instrument that produce a realistic stat card set. */
/* eslint-disable no-magic-numbers -- static demo data */
const DEMO_SCORES: PlayerScore[] = Array.from({ length: 98 }, (_, i) => ({
  songId: `demo-${i}`,
  instrument: 'Solo_Guitar',
  score: 100000 + i * 1000,
  rank: i + 1,
  totalEntries: 500,
  accuracy: 900000 + i * 1000,
  isFullCombo: i < 24,
  stars: i < 8 ? 6 : i < 23 ? 5 : i < 43 ? 4 : i < 55 ? 3 : 2,
  season: 10 + (i % 4),
}));
/* eslint-enable no-magic-numbers */

export default function InstrumentBreakdownDemo() {
  const { t } = useTranslation();
  const h = useSlideHeight();
  const isMobile = useIsMobileChrome();

  const items = buildInstrumentStatsItems(
    t, 'Solo_Guitar', computeInstrumentStats(DEMO_SCORES, TOTAL_SONGS), 'Player', NOOP, NOOP, cardStyle,
  );

  const headerItem = items[0];
  const cardItems = items.slice(1).filter(it => !it.span);
  const availableAfterHeader = h ? h - HEADER_HEIGHT - Gap.md : 0;
  const cols = isMobile ? 1 : 2;
  const maxCards = availableAfterHeader > 0
    ? Math.max(1, Math.floor((availableAfterHeader + Gap.md) / (CARD_HEIGHT + Gap.md)) * cols)
    : cardItems.length;
  const visibleCards = cardItems.slice(0, maxCards);

  const gridStyle = isMobile
    ? { width: '100%', overflow: 'visible' as const, pointerEvents: 'none' as const, gridTemplateColumns: '1fr' }
    : { width: '100%', overflow: 'visible' as const, pointerEvents: 'none' as const };

  return (
    <div style={{ ...pps.gridList, ...gridStyle }}>
      {headerItem && (
        <FadeIn key={headerItem.key} delay={0} style={pps.gridFullWidth}>
          {headerItem.node}
        </FadeIn>
      )}
      {visibleCards.map((item, i) => (
        <FadeIn key={item.key} delay={(i + 1) * 80} style={item.style}>
          {item.node}
        </FadeIn>
      ))}
    </div>
  );
}
