/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * FC Rate "How it works" demo: multiple frosted stat cards showing
 * randomised FC records that follow the Bayesian formula.
 * Cards auto-swap on a timer with fade animation.
 */
import { useState, useEffect, useCallback, useMemo, useRef, type CSSProperties } from 'react';
import FadeIn from '../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import {
  Colors, Font, Weight, Gap, Radius,
  Align, CssValue, PointerEvents, Opacity, CssProp,
  DEMO_SWAP_INTERVAL_MS, STAGGER_INTERVAL,
  flexColumn, frostedCard, padding, transition,
} from '@festival/theme';

const FADE_MS = 300;
const CARD_HEIGHT = 80;
const PARA_HEIGHT = 48;
const M = 50;
const C = 0.5;

/** Generate a random FC record and its Bayesian-adjusted rating. */
function randomFcStat(): { songs: number; fcs: number; rawPct: string; adjusted: string; label: string; value: string } {
  const songs = Math.floor(Math.random() * 180) + 5;
  const rawRate = Math.random() * 0.85 + 0.1; // 10%-95%
  const fcs = Math.round(rawRate * songs);
  const actual = fcs / songs;
  const adjusted = (songs * actual + M * C) / (songs + M);
  const rawPct = `${(actual * 100).toFixed(1)}%`;
  const adjPct = `${(adjusted * 100).toFixed(1)}%`;
  return {
    songs, fcs, rawPct, adjusted: adjPct,
    value: adjPct,
    label: `${fcs} FCs out of ${songs} songs · Raw: ${rawPct}`,
  };
}

function generateBatch(count: number) {
  return Array.from({ length: count }, () => randomFcStat());
}

export default function FcRateHowDemo() {
  const h = useSlideHeight();
  const s = useStaticStyles();

  const budget = h || 300;
  const available = budget - PARA_HEIGHT - Gap.lg * 2;
  const maxCards = Math.max(1, Math.min(4, Math.floor(available / (CARD_HEIGHT + Gap.md))));

  const [stats, setStats] = useState(() => generateBatch(maxCards));
  const [fadingIdx, setFadingIdx] = useState<number | null>(null);
  const initialDoneRef = useRef(false);
  const [initialDone, setInitialDone] = useState(false);

  // Mark initial stagger done
  useEffect(() => {
    const t = setTimeout(() => { initialDoneRef.current = true; setInitialDone(true); }, maxCards * STAGGER_INTERVAL + FADE_MS + 100);
    return () => clearTimeout(t);
  }, [maxCards]);

  // Auto-swap one card at a time
  const swap = useCallback(() => {
    if (!initialDoneRef.current) return;
    const idx = Math.floor(Math.random() * maxCards);
    setFadingIdx(idx);
    setTimeout(() => {
      setStats(prev => {
        const next = [...prev];
        next[idx] = randomFcStat();
        return next;
      });
      setFadingIdx(null);
    }, FADE_MS);
  }, [maxCards]);

  useEffect(() => {
    const id = setInterval(swap, DEMO_SWAP_INTERVAL_MS);
    return () => clearInterval(id);
  }, [swap]);

  // Resize: regenerate if card count changes
  useEffect(() => {
    setStats(prev => {
      if (prev.length === maxCards) return prev;
      return generateBatch(maxCards);
    });
  }, [maxCards]);

  return (
    <div style={s.wrapper}>
      <FadeIn delay={0} style={s.para}>
        FC Rate is the percentage of your played songs where you hit every single note without breaking your streak — a Full Combo.
      </FadeIn>
      {stats.slice(0, maxCards).map((stat, i) => {
        const animStyle: CSSProperties = initialDone
          ? { opacity: fadingIdx === i ? Opacity.none : 1, transition: transition(CssProp.opacity, FADE_MS) }
          : { opacity: Opacity.none, animation: `fadeInUp ${FADE_MS}ms ease-out ${(i + 1) * STAGGER_INTERVAL}ms forwards` };
        return (
          <div key={i} style={{ ...s.statBlock, ...animStyle }}>
            <div style={s.statValue}>{stat.value}</div>
            <div style={s.statLabel}>{stat.label}</div>
          </div>
        );
      })}
    </div>
  );
}

function useStaticStyles() {
  return useMemo(() => ({
    wrapper: {
      ...flexColumn,
      gap: Gap.md,
      width: CssValue.full,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    para: {
      fontSize: Font.md,
      color: Colors.textSecondary,
      lineHeight: 1.5,
      marginBottom: Gap.sm,
    } as CSSProperties,
    statBlock: {
      ...frostedCard,
      ...flexColumn,
      alignItems: Align.center,
      padding: padding(Gap.xl, Gap.lg),
      borderRadius: Radius.md,
      gap: Gap.xs,
    } as CSSProperties,
    statValue: {
      fontSize: Font['2xl'],
      fontWeight: Weight.bold,
      color: Colors.accentBlueBright,
    } as CSSProperties,
    statLabel: {
      fontSize: Font.sm,
      color: Colors.textSecondary,
    } as CSSProperties,
  }), []);
}
