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
import { useRivalsSharedStyles } from '../../useRivalsSharedStyles';
import { useContainerWidth } from '../../../../hooks/ui/useContainerWidth';
import {
  Colors, Font, Weight, Gap, Opacity, CssValue, PointerEvents, flexColumn,
  FADE_DURATION, DEMO_SWAP_INTERVAL_MS,
} from '@festival/theme';

const COMPACT_RIVAL_ROW_HEIGHT = 100;
const DEFAULT_RIVAL_ROW_HEIGHT = 76;
const WIDE_RIVAL_ROW_HEIGHT = 60;
const LABEL_HEIGHT = 28;
const NARROW_ROW_BREAKPOINT = 380;
const WIDE_ROW_BREAKPOINT = 620;
const TWO_SECTION_BUFFER = 48;
const NOOP = () => {};

function estimateRivalRowHeight(containerWidth: number | undefined) {
  if (!containerWidth || containerWidth <= NARROW_ROW_BREAKPOINT) {
    return COMPACT_RIVAL_ROW_HEIGHT;
  }

  if (containerWidth >= WIDE_ROW_BREAKPOINT) {
    return WIDE_RIVAL_ROW_HEIGHT;
  }

  return DEFAULT_RIVAL_ROW_HEIGHT;
}

export default function RivalsOverviewDemo() {
  const h = useSlideHeight();
  const s = useStyles();
  const shared = useRivalsSharedStyles();
  const wrapperRef = useRef<HTMLDivElement>(null);
  const measuredWidth = useContainerWidth(wrapperRef);
  const containerWidth = measuredWidth > 0
    ? measuredWidth
    : (typeof window !== 'undefined' ? window.innerWidth : undefined);
  const rowHeightEstimate = estimateRivalRowHeight(containerWidth);

  const budget = h || 320;
  const rowsWithSections = Math.floor((budget - 2 * LABEL_HEIGHT - 2 * Gap.md) / (rowHeightEstimate + 2));
  const twoSectionHeight = 2 * rowHeightEstimate + 2 * LABEL_HEIGHT + 2 * Gap.md + TWO_SECTION_BUFFER;
  const isCompactSingleCard = budget <= twoSectionHeight;
  const totalRows = Math.max(2, rowsWithSections);
  const half = Math.ceil(totalRows / 2);
  const aboveCount = isCompactSingleCard ? 1 : Math.min(half, 3);
  const belowCount = isCompactSingleCard ? 1 : Math.min(totalRows - aboveCount, 3);

  const [above, setAbove] = useState<RivalSummary[]>(() => DEMO_RIVALS_ABOVE.slice(0, aboveCount));
  const [below, setBelow] = useState<RivalSummary[]>(() => DEMO_RIVALS_BELOW.slice(0, belowCount));
  const [fadingGroup, setFadingGroup] = useState<'above' | 'below' | 'compact' | null>(null);
  const [compactGroup, setCompactGroup] = useState<'above' | 'below'>('above');
  const nextGroupRef = useRef<'above' | 'below'>('above');
  const poolIdxRef = useRef({ above: aboveCount, below: belowCount });

  const rotatePool = useCallback((group: 'above' | 'below') => {
    const rivals = group === 'above' ? DEMO_RIVALS_ABOVE : DEMO_RIVALS_BELOW;
    const count = group === 'above' ? aboveCount : belowCount;
    const start = poolIdxRef.current[group];
    const next = Array.from({ length: count }, (_, i) => rivals[(start + i) % rivals.length]!);

    poolIdxRef.current[group] = (start + count) % rivals.length;
    return next;
  }, [aboveCount, belowCount]);

  // Resize: adjust visible count
  useEffect(() => {
    setAbove(prev => prev.length === aboveCount ? prev : DEMO_RIVALS_ABOVE.slice(0, aboveCount));
    setBelow(prev => prev.length === belowCount ? prev : DEMO_RIVALS_BELOW.slice(0, belowCount));
    poolIdxRef.current = { above: aboveCount, below: belowCount };
  }, [aboveCount, belowCount]);

  useEffect(() => {
    if (isCompactSingleCard) {
      setCompactGroup('above');
    }
  }, [isCompactSingleCard]);

  const rotate = useCallback(() => {
    if (isCompactSingleCard) {
      const currentGroup = compactGroup;
      const nextGroup = currentGroup === 'above' ? 'below' : 'above';

      setFadingGroup('compact');

      setTimeout(() => {
        if (currentGroup === 'above') {
          setAbove(() => rotatePool('above'));
        } else {
          setBelow(() => rotatePool('below'));
        }

        setCompactGroup(nextGroup);
        setFadingGroup(null);
      }, FADE_DURATION);

      return;
    }

    const group = nextGroupRef.current;
    nextGroupRef.current = group === 'above' ? 'below' : 'above';

    // Fade out the entire group
    setFadingGroup(group);

    setTimeout(() => {
      if (group === 'above') {
        setAbove(() => rotatePool('above'));
      } else {
        setBelow(() => rotatePool('below'));
      }
      setFadingGroup(null);
    }, FADE_DURATION);
  }, [compactGroup, isCompactSingleCard, rotatePool]);

  useEffect(() => {
    const timer = setInterval(rotate, DEMO_SWAP_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [rotate]);

  let idx = 0;
  const compactRivals = compactGroup === 'above' ? above : below;

  if (isCompactSingleCard) {
    return (
      <div ref={wrapperRef} style={s.wrapper}>
        <div
          data-testid="rivals-fre-compact-list"
          data-compact-direction={compactGroup}
          style={{ ...shared.rivalList, ...(fadingGroup === 'compact' ? s.fading : s.visible) }}
        >
          {compactRivals.map(r => (
            <FadeIn key={`${compactGroup}-${r.accountId}`} delay={(idx++) * 80}>
              <RivalRow rival={r} direction={compactGroup} onClick={NOOP} />
            </FadeIn>
          ))}
        </div>
      </div>
    );
  }

  return (
    <div ref={wrapperRef} style={s.wrapper}>
      <FadeIn delay={(idx++) * 80}>
        <span style={s.label}>Above You</span>
      </FadeIn>
      <div
        data-testid="rivals-fre-above-list"
        style={{ ...shared.rivalList, ...(fadingGroup === 'above' ? s.fading : s.visible) }}
      >
        {above.map(r => (
          <FadeIn key={r.accountId} delay={(idx++) * 80}>
            <RivalRow rival={r} direction="above" onClick={NOOP} />
          </FadeIn>
        ))}
      </div>
      <FadeIn delay={(idx++) * 80}>
        <span style={s.label}>Below You</span>
      </FadeIn>
      <div
        data-testid="rivals-fre-below-list"
        style={{ ...shared.rivalList, ...(fadingGroup === 'below' ? s.fading : s.visible) }}
      >
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
