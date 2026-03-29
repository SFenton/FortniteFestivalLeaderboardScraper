/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * First-run demo: Percentile table using production PlayerPercentileHeader/Row.
 * Shows a static percentile distribution for one instrument.
 */
import { type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { Radius, frostedCardSurface, Gap, Overflow, PointerEvents, CssValue } from '@festival/theme';
import { PlayerPercentileHeader, PlayerPercentileRow } from '../../../../components/player/PlayerPercentileTable';
import FadeIn from '../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';

const TABLE_HEADER_HEIGHT = 40;
const ROW_HEIGHT = 44;
/* v8 ignore start -- NOOP is passed as prop but never invoked in test (pointerEvents: none) */
const NOOP = () => {};
/* v8 ignore stop */

const cardStyle: CSSProperties = { ...frostedCardSurface, borderRadius: Radius.md, overflow: Overflow.hidden };

const DEMO_BUCKETS = [
  { pct: 1, count: 3 },
  { pct: 5, count: 12 },
  { pct: 10, count: 28 },
  { pct: 25, count: 55 },
  { pct: 50, count: 89 },
  { pct: 100, count: 142 },
];

export default function PercentileDemo() {
  const { t } = useTranslation();
  const h = useSlideHeight();

  const availableForRows = h
    ? h - TABLE_HEADER_HEIGHT
    : DEMO_BUCKETS.length * ROW_HEIGHT;
  const maxRows = Math.max(1, Math.floor(availableForRows / ROW_HEIGHT));
  const visibleBuckets = DEMO_BUCKETS.slice(0, maxRows);

  return (
    <div style={{ width: CssValue.full, pointerEvents: PointerEvents.none }}>
      <FadeIn delay={0} style={{ ...cardStyle, marginBottom: Gap.md }}>
        <PlayerPercentileHeader
          percentileLabel={t('player.percentileHeader')}
          songsLabel={t('player.songsHeader')}
        />
        {visibleBuckets.map((b, i) => (
          <PlayerPercentileRow
            key={b.pct}
            pct={b.pct}
            count={b.count}
            isLast={i === visibleBuckets.length - 1}
            onClick={NOOP}
          />
        ))}
      </FadeIn>
    </div>
  );
}
