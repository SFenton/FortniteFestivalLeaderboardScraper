/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * First-run demo: Single instrument header with ranking entry rows below.
 */
import { useMemo, type CSSProperties } from 'react';
import { RankingEntry } from '../../components/RankingEntry';
import InstrumentHeader from '../../../../components/display/InstrumentHeader';
import FadeIn from '../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { DEMO_RANKINGS } from '../../../../firstRun/demoData';
import { InstrumentHeaderSize } from '@festival/core';
import {
  Font, Gap, Radius, Layout, Display, Align,
  CssValue, PointerEvents, frostedCard, flexColumn, padding,
} from '@festival/theme';

const ENTRY_HEIGHT = Layout.entryRowHeight;
const HEADER_HEIGHT = 40;

export default function RankingsOverviewDemo() {
  const h = useSlideHeight();
  const s = useStyles();

  const budget = h || 320;
  const maxRows = Math.max(1, Math.floor((budget - HEADER_HEIGHT - Gap.md) / (ENTRY_HEIGHT + Gap.sm)));
  const visible = DEMO_RANKINGS.slice(0, Math.min(maxRows, 8));

  return (
    <div style={s.wrapper}>
      <FadeIn delay={0} style={s.header}>
        <InstrumentHeader instrument="Solo_Guitar" size={InstrumentHeaderSize.SM} />
      </FadeIn>
      <div style={s.list}>
        {visible.map((e, i) => (
          <FadeIn key={e.rank} delay={(i + 1) * 80} style={s.row}>
            <RankingEntry rank={e.rank} displayName={e.displayName} ratingLabel={e.ratingLabel} />
          </FadeIn>
        ))}
      </div>
    </div>
  );
}

function useStyles() {
  return useMemo(() => ({
    wrapper: {
      ...flexColumn,
      gap: Gap.md,
      width: CssValue.full,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    header: {
      paddingBottom: Gap.xs,
    } as CSSProperties,
    list: {
      ...flexColumn,
      gap: Gap.sm,
    } as CSSProperties,
    row: {
      ...frostedCard,
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.xl,
      padding: padding(0, Gap.xl),
      height: Layout.entryRowHeight,
      borderRadius: Radius.md,
      fontSize: Font.md,
    } as CSSProperties,
  }), []);
}
