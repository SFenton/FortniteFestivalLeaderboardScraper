/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * First-run demo: Stat boxes with animated pulse on clickable cards.
 * Shows which cards have chevrons (clickable) vs. which don't.
 */
import { type CSSProperties } from 'react';
import { Radius, frostedCard, Colors, Gap } from '@festival/theme';
import StatBox from '../../../../components/player/StatBox';
import FadeIn from '../../../../components/page/FadeIn';
import { useIsMobileChrome } from '../../../../hooks/ui/useIsMobile';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import s from '../../../../components/player/PlayerPage.module.css';
import css from './DrillDownDemo.module.css';

const cardStyle: CSSProperties = { ...frostedCard, borderRadius: Radius.md };
const CARD_HEIGHT = 100;
/* v8 ignore start -- NOOP is passed as prop but never invoked in test (pointerEvents: none) */
const NOOP = () => {};
/* v8 ignore stop */

const DEMO_BOXES: { label: string; value: string; color?: string; clickable: boolean }[] = [
  { label: 'Songs Played', value: '142', color: undefined, clickable: true },
  { label: 'Gold Stars', value: '12', color: Colors.gold, clickable: false },
  { label: 'Avg Accuracy', value: '96.2%', color: undefined, clickable: false },
  { label: 'Full Combos', value: '38 (26.8%)', color: undefined, clickable: true },
];

export default function DrillDownDemo() {
  const isMobile = useIsMobileChrome();
  const h = useSlideHeight();
  const cols = isMobile ? 1 : 2;
  const maxItems = h ? Math.max(1, Math.floor((h + Gap.md) / (CARD_HEIGHT + Gap.md)) * cols) : DEMO_BOXES.length;
  const visible = DEMO_BOXES.slice(0, maxItems);
  const gridStyle = isMobile
    ? { width: '100%', overflow: 'visible' as const, gridTemplateColumns: '1fr' }
    : { width: '100%', overflow: 'visible' as const };

  return (
    <div className={s.gridList} style={gridStyle}>
      {visible.map((box, i) => (
        <FadeIn key={box.label} delay={i * 80} style={cardStyle}>
          <div className={box.clickable ? css.pulseWrap : undefined}>
            <StatBox label={box.label} value={box.value} color={box.color} onClick={box.clickable ? NOOP : undefined} />
          </div>
        </FadeIn>
      ))}
    </div>
  );
}
