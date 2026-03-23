import { useMemo } from 'react';
import { STAGGER_INTERVAL } from '@festival/theme';
import { useFestival } from '../../../../contexts/FestivalContext';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { LeaderboardEntry } from '../../../leaderboard/global/components/LeaderboardEntry';
import FadeIn from '../../../../components/page/FadeIn';
import css from './HistoryDemo.module.css';

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

  return (
    <div className={css.wrapper}>
      <div className={css.list}>
        {scores.slice(0, maxRows).map((s, i) => (
          <FadeIn key={i} delay={i * STAGGER_INTERVAL}>
            <div className={css.card}>
              <LeaderboardEntry
                label={s.label}
                displayName=""
                score={s.score}
                accuracy={s.accuracy}
                isFullCombo={s.isFC}
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
