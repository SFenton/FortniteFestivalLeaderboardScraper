/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * First-run demo: Mini leaderboard with the player's rank highlighted
 * and a "View all rankings" button at the bottom.
 */
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { RankingEntry } from '../../components/RankingEntry';
import FadeIn from '../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { DEMO_NEIGHBORHOOD } from '../../../../firstRun/demoData';
import anim from '../../../../styles/animations.module.css';
import {
  Colors, Font, Weight, Gap, Radius, Layout, Border, Display, Align, Justify, Cursor, Position, Overflow,
  CssValue, PointerEvents, frostedCard, flexColumn, padding, border,
  FAST_FADE_MS, CssProp, transition,
} from '@festival/theme';

const ENTRY_HEIGHT = Layout.entryRowHeight;

export default function YourRankDemo() {
  const { t } = useTranslation();
  const h = useSlideHeight();
  const s = useStyles();

  const budget = h || 320;
  // Priority items: player row + View All button (always shown)
  const fixedHeight = 2 * (ENTRY_HEIGHT + Gap.sm);
  const availableForContext = budget - fixedHeight;
  const contextSlots = Math.max(0, Math.floor(availableForContext / (ENTRY_HEIGHT + Gap.sm)));

  // Split context rows evenly above/below the player row
  const playerIdx = DEMO_NEIGHBORHOOD.findIndex(e => e.isPlayer);
  const abovePool = DEMO_NEIGHBORHOOD.slice(0, playerIdx);
  const belowPool = DEMO_NEIGHBORHOOD.slice(playerIdx + 1);
  const aboveCount = Math.min(Math.ceil(contextSlots / 2), abovePool.length);
  const belowCount = Math.min(contextSlots - aboveCount, belowPool.length);

  const visible = [
    ...abovePool.slice(abovePool.length - aboveCount),
    DEMO_NEIGHBORHOOD[playerIdx]!,
    ...belowPool.slice(0, belowCount),
  ];

  return (
    <div style={s.wrapper}>
      {visible.map((e, i) => (
        <FadeIn key={e.rank} delay={i * 80} style={e.isPlayer ? s.playerRow : s.row}>
          <RankingEntry
            rank={e.rank}
            displayName={e.displayName}
            ratingLabel={e.ratingLabel}
            isPlayer={e.isPlayer}
          />
        </FadeIn>
      ))}
      <FadeIn delay={visible.length * 80}>
        <div className={anim.pulseWrap} style={s.pulseWrap}>
          <div style={s.viewAllButton}>
            {t('rankings.viewAllRankings')}
          </div>
        </div>
      </FadeIn>
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
      row: { ...rowBase } as CSSProperties,
      playerRow: {
        ...rowBase,
        backgroundColor: Colors.purpleHighlight,
        border: border(Border.thin, Colors.purpleHighlightBorder),
      } as CSSProperties,
      pulseWrap: {
        position: Position.relative,
        overflow: Overflow.hidden,
        borderRadius: Radius.md,
      } as CSSProperties,
      viewAllButton: {
        ...frostedCard,
        display: Display.flex,
        alignItems: Align.center,
        justifyContent: Justify.center,
        height: Layout.entryRowHeight,
        borderRadius: Radius.md,
        color: Colors.textPrimary,
        fontSize: Font.md,
        fontWeight: Weight.semibold,
        cursor: Cursor.pointer,
        transition: transition(CssProp.backgroundColor, FAST_FADE_MS),
      } as CSSProperties,
    };
  }, []);
}
