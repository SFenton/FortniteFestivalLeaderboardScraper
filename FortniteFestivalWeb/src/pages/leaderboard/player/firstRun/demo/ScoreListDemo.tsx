import { useMemo } from 'react';
import { STAGGER_INTERVAL } from '@festival/theme';
import { useSlideHeight } from '../../../../../firstRun/SlideHeightContext';
import { LeaderboardEntry } from '../../../global/components/LeaderboardEntry';
import FadeIn from '../../../../../components/page/FadeIn';
import css from './ScoreListDemo.module.css';

const ROW_HEIGHT = 44;
const DAY_OFFSETS = [0, 7, 21, 35, 56];
const LABELS = DAY_OFFSETS.map((offset) => {
  const d = new Date();
  d.setDate(d.getDate() - offset);
  return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
});
const SCORES = [486500, 412300, 347100, 289600, 218400];
const ACCURACIES = [1000000, 990000, 970000, 950000, 880000];
const IS_FC = [true, false, false, false, false];
const HIGH_SCORE_IDX = 0;

export default function ScoreListDemo() {
  const h = useSlideHeight();

  const maxRows = useMemo(() => {
    if (!h) return 5;
    return Math.max(1, Math.min(5, Math.floor(h / ROW_HEIGHT)));
  }, [h]);

  return (
    <div className={css.wrapper}>
      <div className={css.list}>
        {LABELS.slice(0, maxRows).map((label, i) => (
          <FadeIn key={i} delay={i * STAGGER_INTERVAL}>
            <div className={i === HIGH_SCORE_IDX ? css.rowHighlight : css.row}>
              <LeaderboardEntry
                label={label}
                displayName=""
                score={SCORES[i]!}
                accuracy={ACCURACIES[i]!}
                isFullCombo={IS_FC[i]!}
                isPlayer={i === HIGH_SCORE_IDX}
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
