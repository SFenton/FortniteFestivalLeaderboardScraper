/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * First-run demo: Rivals above and below sections.
 * Alternates between swapping all "Above You" and all "Below You" rows.
 */
import { useState, useEffect, useCallback, useRef, useMemo, type CSSProperties } from 'react';
import type { RivalSummary } from '@festival/core/api/serverTypes';
import RivalRow from '../../components/RivalRow';
import FadeIn from '../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { DEMO_RIVALS_ABOVE, DEMO_RIVALS_BELOW } from '../../../../firstRun/demoData';
import {
  Colors, Font, Weight, Gap, Opacity, CssValue, PointerEvents, flexColumn,
  FADE_DURATION, DEMO_SWAP_INTERVAL_MS,
} from '@festival/theme';

const RIVAL_ROW_HEIGHT = 100;
const LABEL_HEIGHT = 28;
const NOOP = () => {};

export default function RivalsOverviewDemo() {
  const h = useSlideHeight();
  const s = useStyles();

  const budget = h || 320;
  const totalRows = Math.max(2, Math.floor((budget - 2 * LABEL_HEIGHT - 2 * Gap.md) / (RIVAL_ROW_HEIGHT + 2)));
  const half = Math.ceil(totalRows / 2);
  const aboveCount = Math.min(half, 3);
  const belowCount = Math.min(totalRows - aboveCount, 3);

  const [above, setAbove] = useState<RivalSummary[]>(() => DEMO_RIVALS_ABOVE.slice(0, aboveCount));
  const [below, setBelow] = useState<RivalSummary[]>(() => DEMO_RIVALS_BELOW.slice(0, belowCount));
  const [fadingGroup, setFadingGroup] = useState<'above' | 'below' | null>(null);
  const nextGroupRef = useRef<'above' | 'below'>('above');
  const poolIdxRef = useRef({ above: aboveCount, below: belowCount });

  // Resize: adjust visible count
  useEffect(() => {
    setAbove(prev => prev.length === aboveCount ? prev : DEMO_RIVALS_ABOVE.slice(0, aboveCount));
    setBelow(prev => prev.length === belowCount ? prev : DEMO_RIVALS_BELOW.slice(0, belowCount));
  }, [aboveCount, belowCount]);

  const rotate = useCallback(() => {
    const group = nextGroupRef.current;
    nextGroupRef.current = group === 'above' ? 'below' : 'above';

    // Fade out the entire group
    setFadingGroup(group);

    setTimeout(() => {
      if (group === 'above') {
        setAbove(() => {
          const start = poolIdxRef.current.above;
          const next = Array.from({ length: aboveCount }, (_, i) =>
            DEMO_RIVALS_ABOVE[(start + i) % DEMO_RIVALS_ABOVE.length]!,
          );
          poolIdxRef.current.above = (start + aboveCount) % DEMO_RIVALS_ABOVE.length;
          return next;
        });
      } else {
        setBelow(() => {
          const start = poolIdxRef.current.below;
          const next = Array.from({ length: belowCount }, (_, i) =>
            DEMO_RIVALS_BELOW[(start + i) % DEMO_RIVALS_BELOW.length]!,
          );
          poolIdxRef.current.below = (start + belowCount) % DEMO_RIVALS_BELOW.length;
          return next;
        });
      }
      setFadingGroup(null);
    }, FADE_DURATION);
  }, [aboveCount, belowCount]);

  useEffect(() => {
    const timer = setInterval(rotate, DEMO_SWAP_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [rotate]);

  let idx = 0;

  return (
    <div style={s.wrapper}>
      <FadeIn delay={(idx++) * 80}>
        <span style={s.label}>Above You</span>
      </FadeIn>
      <div style={{ ...s.list, ...(fadingGroup === 'above' ? s.fading : s.visible) }}>
        {above.map(r => (
          <FadeIn key={r.accountId} delay={(idx++) * 80}>
            <RivalRow rival={r} direction="above" onClick={NOOP} />
          </FadeIn>
        ))}
      </div>
      <FadeIn delay={(idx++) * 80}>
        <span style={s.label}>Below You</span>
      </FadeIn>
      <div style={{ ...s.list, ...(fadingGroup === 'below' ? s.fading : s.visible) }}>
        {below.map(r => (
          <FadeIn key={r.accountId} delay={(idx++) * 80}>
            <RivalRow rival={r} direction="below" onClick={NOOP} />
          </FadeIn>
        ))}
      </div>
    </div>
  );
}

function useStyles() {
  return useMemo(() => {
    const trans = `opacity ${FADE_DURATION}ms ease, transform ${FADE_DURATION}ms ease`;
    return {
      wrapper: {
        ...flexColumn,
        gap: Gap.md,
        width: CssValue.full,
        pointerEvents: PointerEvents.none,
      } as CSSProperties,
      label: {
        fontSize: Font.lg,
        fontWeight: Weight.bold,
        color: Colors.textPrimary,
      } as CSSProperties,
      list: {
        ...flexColumn,
        gap: 2,
      } as CSSProperties,
      visible: {
        transition: trans,
        opacity: 1,
        transform: 'translateY(0)',
      } as CSSProperties,
      fading: {
        transition: trans,
        opacity: Opacity.none,
        transform: 'translateY(4px)',
      } as CSSProperties,
    };
  }, []);
}
