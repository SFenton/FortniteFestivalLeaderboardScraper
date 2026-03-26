/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { STAGGER_INTERVAL, frostedCard, Radius, Font, Gap, Weight, Display, Align, Justify, CssValue, PointerEvents, Layout, Colors, Position, Overflow, flexColumn, padding } from '@festival/theme';
import { useFestival } from '../../../../contexts/FestivalContext';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { LeaderboardEntry } from '../../../leaderboard/global/components/LeaderboardEntry';
import FadeIn from '../../../../components/page/FadeIn';
import css from './ViewAllDemo.module.css';

const ROW_HEIGHT = 44;
const BTN_HEIGHT = 44;
const DAY_OFFSETS = [0, 7, 21, 35];
const LABELS = DAY_OFFSETS.map((offset) => {
  const d = new Date();
  d.setDate(d.getDate() - offset);
  return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
});
const BASE_SCORES = [486500, 412300, 347100, 289600];
const ACCURACIES = [1000000, 990000, 970000, 950000];
const STARS = [6, 5, 5, 5];
const IS_FC = [true, false, false, false];

function buildScores(currentSeason: number) {
  const minSeason = Math.max(1, currentSeason - 3);
  return LABELS.map((label, i) => ({
    label,
    score: BASE_SCORES[i]!,
    accuracy: ACCURACIES[i]!,
    isFC: IS_FC[i]!,
    stars: STARS[i]!,
    season: i < 2 ? currentSeason : minSeason + (i % (currentSeason - minSeason || 1)),
  }));
}

export default function ViewAllDemo() {
  const { t } = useTranslation();
  const h = useSlideHeight();
  const { state: { currentSeason } } = useFestival();
  const scores = useMemo(() => buildScores(currentSeason || 1), [currentSeason]);

  const maxCards = useMemo(() => {
    if (!h) return 3;
    const available = h - BTN_HEIGHT - 16; // button + gap
    return Math.max(1, Math.min(4, Math.floor(available / ROW_HEIGHT)));
  }, [h]);

  const s = useStyles();

  return (
    <div style={s.wrapper}>
      <div style={s.list}>
        {scores.slice(0, maxCards).map((sc, i) => (
          <FadeIn key={i} delay={i * STAGGER_INTERVAL}>
            <div style={i === 0 ? s.cardFaded : s.card}>
              <LeaderboardEntry
                label={sc.label}
                displayName=""
                score={sc.score}
                accuracy={sc.accuracy}
                isFullCombo={sc.isFC}
                showAccuracy
                scoreWidth="7ch"
              />
            </div>
          </FadeIn>
        ))}
        <FadeIn delay={maxCards * STAGGER_INTERVAL}>
          <div className={css.pulseWrap} style={s.pulseWrap}>
            <div style={s.viewAllBtn}>
              {t('chart.viewAllScores')}
            </div>
          </div>
        </FadeIn>
      </div>
    </div>
  );
}

function useStyles() {
  return useMemo(() => {
    const card: CSSProperties = {
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
      wrapper: { width: CssValue.full, pointerEvents: PointerEvents.none } as CSSProperties,
      list: { ...flexColumn, gap: Gap.sm } as CSSProperties,
      card,
      cardFaded: {
        ...card,
        opacity: 1,
        maskImage: 'linear-gradient(to bottom, transparent, black 100%)',
        WebkitMaskImage: 'linear-gradient(to bottom, transparent, black 100%)',
      } as CSSProperties,
      pulseWrap: {
        position: Position.relative,
        overflow: Overflow.hidden,
        borderRadius: Radius.md,
      } as CSSProperties,
      viewAllBtn: {
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
    };
  }, []);
}
