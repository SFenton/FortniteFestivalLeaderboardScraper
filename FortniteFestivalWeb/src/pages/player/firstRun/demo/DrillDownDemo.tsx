/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo } from 'react';
import { Radius, frostedCard, Colors, Gap, Layout, Position, Overflow, PointerEvents, CssValue, GridTemplate, STAGGER_ENTRY_OFFSET } from '@festival/theme';
import StatBox from '../../../../components/player/StatBox';
import FadeIn from '../../../../components/page/FadeIn';
import { useIsMobileChrome } from '../../../../hooks/ui/useIsMobile';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { playerPageStyles as pps } from '../../../../components/player/playerPageStyles';
import anim from '../../../../styles/animations.module.css';

const cardStyle = { ...frostedCard, borderRadius: Radius.md };
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
  const st = useStyles(isMobile);
  const cols = isMobile ? 1 : 2;
  const maxItems = h ? Math.max(1, Math.floor((h + Gap.md) / (Layout.demoCardHeight + Gap.md)) * cols) : DEMO_BOXES.length;
  const visible = DEMO_BOXES.slice(0, maxItems);

  return (
    <div style={{ ...pps.gridList, ...st.grid }}>
      {visible.map((box, i) => (
        <FadeIn key={box.label} delay={i * STAGGER_ENTRY_OFFSET} style={cardStyle}>
          <div className={box.clickable ? anim.pulseWrap : undefined} style={box.clickable ? st.pulseWrap : undefined}>
            <StatBox label={box.label} value={box.value} color={box.color} onClick={box.clickable ? NOOP : undefined} />
          </div>
        </FadeIn>
      ))}
    </div>
  );
}

function useStyles(isMobile: boolean) {
  return useMemo(() => ({
    grid: {
      width: CssValue.full,
      overflow: Overflow.visible,
      ...(isMobile ? { gridTemplateColumns: GridTemplate.single } : {}),
    },
    pulseWrap: {
      position: Position.relative,
      overflow: Overflow.hidden,
      borderRadius: Radius.md,
      pointerEvents: PointerEvents.none,
    },
  }), [isMobile]);
}
