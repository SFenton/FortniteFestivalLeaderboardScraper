/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { STAGGER_INTERVAL, frostedCard, Radius, Font, Gap, Display, Align, CssValue, PointerEvents, Layout, flexColumn, padding } from '@festival/theme';
import { useFestival } from '../../../../contexts/FestivalContext';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { LeaderboardEntry } from '../../../leaderboard/global/components/LeaderboardEntry';
import FadeIn from '../../../../components/page/FadeIn';

const ROW_HEIGHT = 44;
const DAY_OFFSETS = [0, 7, 21, 35, 56];
const LABELS = DAY_OFFSETS.map((offset) => {
  const d = new Date();
  d.setDate(d.getDate() - offset);
  return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
});
const SCORES = [486500, 412300, 347100, 289600, 218400];
const ACCURACIES = [1000000, 990000, 970000, 950000, 880000];
const STARS = [6, 5, 5, 5, 4];
const IS_FC = [true, false, false, false, false];

function buildScores(currentSeason: number) {
  const minSeason = Math.max(1, currentSeason - 3);
  return LABELS.map((label, i) => ({
    label,
    score: SCORES[i]!,
    accuracy: ACCURACIES[i]!,
    isFC: IS_FC[i]!,
    stars: STARS[i]!,
    // First two rows = current season, rest = random earlier seasons
    season: i < 2 ? currentSeason : minSeason + (i % (currentSeason - minSeason || 1)),
  }));
}

export default function HistoryDemo() {
  const h = useSlideHeight();
  const { state: { currentSeason } } = useFestival();
  const scores = useMemo(() => buildScores(currentSeason || 1), [currentSeason]);

  const maxRows = useMemo(() => {
    if (!h) return 5;
    return Math.max(1, Math.min(5, Math.floor(h / ROW_HEIGHT)));
  }, [h]);

  const s = useStyles();

  return (
    <div style={s.wrapper}>
      <div style={s.list}>
        {scores.slice(0, maxRows).map((sc, i) => (
          <FadeIn key={i} delay={i * STAGGER_INTERVAL}>
            <div style={s.card}>
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
      </div>
    </div>
  );
}

function useStyles() {
  return useMemo(() => ({
    wrapper: { width: CssValue.full, pointerEvents: PointerEvents.none } as CSSProperties,
    list: { ...flexColumn, gap: Gap.sm } as CSSProperties,
    card: {
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
