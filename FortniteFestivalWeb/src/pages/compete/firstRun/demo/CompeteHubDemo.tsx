/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * First-run demo: Alternates between a leaderboard layout and a rivals layout.
 * No stagger animation — each layout fades in/out as a whole.
 */
import { useState, useEffect, useCallback, useRef, useMemo, type CSSProperties } from 'react';
import { RankingEntry } from '../../../leaderboards/components/RankingEntry';
import RivalRow from '../../../rivals/components/RivalRow';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { DEMO_RANKINGS, DEMO_PLAYER_ENTRY, DEMO_RIVALS_ABOVE, DEMO_RIVALS_BELOW } from '../../../../firstRun/demoData';
import {
  Colors, Font, Weight, Gap, Radius, Layout, Border, Display, Align, Opacity,
  CssValue, PointerEvents, frostedCard, flexColumn, padding, border,
  FADE_DURATION, DEMO_SWAP_INTERVAL_MS,
} from '@festival/theme';

const ENTRY_HEIGHT = Layout.entryRowHeight;
const RIVAL_HEIGHT = 100;
const LABEL_HEIGHT = 28;
const NOOP = () => {};

export default function CompeteHubDemo() {
  const h = useSlideHeight();
  const s = useStyles();
  const budget = h || 320;

  const [mode, setMode] = useState<'leaderboard' | 'rivals'>('leaderboard');
  const [visible, setVisible] = useState(true);
  const modeRef = useRef<'leaderboard' | 'rivals'>('leaderboard');

  const rotate = useCallback(() => {
    setVisible(false);
    setTimeout(() => {
      modeRef.current = modeRef.current === 'leaderboard' ? 'rivals' : 'leaderboard';
      setMode(modeRef.current);
      requestAnimationFrame(() => setVisible(true));
    }, FADE_DURATION);
  }, []);

  useEffect(() => {
    const timer = setInterval(rotate, DEMO_SWAP_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [rotate]);

  useEffect(() => {
    const raf = requestAnimationFrame(() => setVisible(true));
    return () => cancelAnimationFrame(raf);
  }, []);

  // Leaderboard layout sizing
  const maxLbRows = Math.max(2, Math.floor((budget - ENTRY_HEIGHT - Gap.sm) / (ENTRY_HEIGHT + Gap.sm)));
  const lbEntries = DEMO_RANKINGS.slice(0, Math.min(maxLbRows, 8));

  // Rivals layout sizing
  const totalRivalRows = Math.max(2, Math.floor((budget - 2 * LABEL_HEIGHT - 2 * Gap.md) / (RIVAL_HEIGHT + 2)));
  const rivalHalf = Math.ceil(totalRivalRows / 2);
  const aboveCount = Math.min(rivalHalf, 3);
  const belowCount = Math.min(totalRivalRows - aboveCount, 3);

  const transStyle: CSSProperties = {
    transition: `opacity ${FADE_DURATION}ms ease, transform ${FADE_DURATION}ms ease`,
    opacity: visible ? 1 : Opacity.none,
    transform: visible ? 'translateY(0)' : 'translateY(6px)',
  };

  return (
    <div style={{ ...s.wrapper, ...transStyle }}>
      {mode === 'leaderboard' ? (
        <>
          <div style={s.list}>
            {lbEntries.map(e => (
              <div key={e.rank} style={s.row}>
                <RankingEntry rank={e.rank} displayName={e.displayName} ratingLabel={e.ratingLabel} />
              </div>
            ))}
          </div>
          <div style={s.playerRow}>
            <RankingEntry rank={DEMO_PLAYER_ENTRY.rank} displayName={DEMO_PLAYER_ENTRY.displayName} ratingLabel={DEMO_PLAYER_ENTRY.ratingLabel} isPlayer />
          </div>
        </>
      ) : (
        <>
          <span style={s.sectionLabel}>Above You</span>
          <div style={s.rivalList}>
            {DEMO_RIVALS_ABOVE.slice(0, aboveCount).map(r => (
              <RivalRow key={r.accountId} rival={r} direction="above" onClick={NOOP} />
            ))}
          </div>
          <span style={s.sectionLabel}>Below You</span>
          <div style={s.rivalList}>
            {DEMO_RIVALS_BELOW.slice(0, belowCount).map(r => (
              <RivalRow key={r.accountId} rival={r} direction="below" onClick={NOOP} />
            ))}
          </div>
        </>
      )}
    </div>
  );
}

function useStyles() {
  return useMemo(() => {
    const rowBase: CSSProperties = {
      ...frostedCard,
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.xl,
      padding: padding(0, Gap.xl),
      height: Layout.entryRowHeight,
      borderRadius: Radius.md,
      fontSize: Font.md,
    };
    return {
      wrapper: {
        ...flexColumn,
        gap: Gap.sm,
        width: CssValue.full,
        pointerEvents: PointerEvents.none,
      } as CSSProperties,
      list: {
        ...flexColumn,
        gap: Gap.sm,
      } as CSSProperties,
      row: { ...rowBase } as CSSProperties,
      playerRow: {
        ...rowBase,
        backgroundColor: Colors.purpleHighlight,
        border: border(Border.thin, Colors.purpleHighlightBorder),
      } as CSSProperties,
      sectionLabel: {
        fontSize: Font.lg,
        fontWeight: Weight.bold,
        color: Colors.textPrimary,
      } as CSSProperties,
      rivalList: {
        ...flexColumn,
        gap: 2,
      } as CSSProperties,
    };
  }, []);
}
