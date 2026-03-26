/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { InstrumentHeaderSize } from '@festival/core';
import { STAGGER_INTERVAL, frostedCard, Radius, Font, Gap, Weight, Display, Align, Justify, CssValue, PointerEvents, Layout, Colors, Position, Overflow, flexColumn, padding } from '@festival/theme';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import InstrumentHeader from '../../../../components/display/InstrumentHeader';
import { LeaderboardEntry } from '../../../leaderboard/global/components/LeaderboardEntry';
import FadeIn from '../../../../components/page/FadeIn';
import anim from '../../../../styles/animations.module.css';

const ENTRY_HEIGHT = 44;
const LABEL_HEIGHT = 32;
const BTN_HEIGHT = 44;
const GAP = 8;

type DemoEntry = { rank: number; name: string; score: number; accuracy: number; isFC: boolean; stars: number };

const ENTRIES: DemoEntry[] = [
  { rank: 1, name: 'AceSolo', score: 486500, accuracy: 1000000, isFC: true, stars: 6 },
  { rank: 2, name: 'RiffMaster', score: 412300, accuracy: 980000, isFC: false, stars: 5 },
  { rank: 3, name: 'ChordKing', score: 347100, accuracy: 970000, isFC: false, stars: 5 },
  { rank: 4, name: 'PickSlayer', score: 289600, accuracy: 960000, isFC: false, stars: 5 },
];

export default function TopScoresDemo() {
  const { t } = useTranslation();
  const h = useSlideHeight();

  const maxEntries = useMemo(() => {
    if (!h) return 4;
    const available = h - LABEL_HEIGHT - BTN_HEIGHT - GAP * 2;
    return Math.max(1, Math.min(4, Math.floor(available / (ENTRY_HEIGHT + GAP))));
  }, [h]);

  const s = useStyles();

  return (
    <div style={s.wrapper}>
      <FadeIn delay={0}>
        <div style={s.cardLabel}>
          <InstrumentHeader instrument="Solo_Guitar" size={InstrumentHeaderSize.MD} />
        </div>
      </FadeIn>
      <div style={s.list}>
        {ENTRIES.slice(0, maxEntries).map((entry, i) => (
          <FadeIn key={i} delay={(i + 1) * STAGGER_INTERVAL}>
            <div style={s.entryRow}>
              <LeaderboardEntry
                rank={entry.rank}
                displayName={entry.name}
                score={entry.score}
                accuracy={entry.accuracy}
                isFullCombo={entry.isFC}
                stars={entry.stars}
                showAccuracy
                showStars
                scoreWidth="7ch"
              />
            </div>
          </FadeIn>
        ))}
        <FadeIn delay={(maxEntries + 1) * STAGGER_INTERVAL}>
          <div className={anim.pulseWrap} style={s.pulseWrap}>
            <div style={s.viewAllRow}>
              {t('songDetail.viewFullLeaderboard', 'View full leaderboard')}
            </div>
          </div>
        </FadeIn>
      </div>
    </div>
  );
}

function useStyles() {
  return useMemo(() => ({
    wrapper: { width: CssValue.full, pointerEvents: PointerEvents.none } as CSSProperties,
    cardLabel: { display: Display.flex, alignItems: Align.center, gap: Gap.md, paddingBottom: Gap.xs } as CSSProperties,
    list: { ...flexColumn, gap: Gap.sm } as CSSProperties,
    entryRow: {
      ...frostedCard,
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.xl,
      padding: padding(0, Gap.xl),
      height: Layout.entryRowHeight,
      borderRadius: Radius.md,
      fontSize: Font.md,
    } as CSSProperties,
    pulseWrap: {
      position: Position.relative,
      overflow: Overflow.hidden,
      borderRadius: Radius.md,
    } as CSSProperties,
    viewAllRow: {
      ...frostedCard,
      display: Display.flex,
      alignItems: Align.center,
      justifyContent: Justify.center,
      height: Layout.entryRowHeight,
      borderRadius: Radius.md,
      color: Colors.textPrimary,
      fontSize: Font.md,
      fontWeight: Weight.semibold,
    } as CSSProperties,
  }), []);
}
