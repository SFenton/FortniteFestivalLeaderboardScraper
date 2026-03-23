/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Top score cards displayed beneath the chart.
 * Extracted from ScoreHistoryChart for readability.
 */
import { memo } from 'react';
import { ACCURACY_SCALE } from '@festival/core';
import { Gap, QUERY_SHOW_ACCURACY, QUERY_SHOW_SEASON } from '@festival/theme';
import { LeaderboardEntry } from '../../../leaderboard/global/components/LeaderboardEntry';
import { useMediaQuery } from '../../../../hooks/ui/useMediaQuery';
import type { ChartPoint } from '../../../../hooks/chart/useChartData';
import s from './ScoreHistoryChart.module.css';

type ListPhase = 'idle' | 'in' | 'out';

interface Props {
  displayedCards: ChartPoint[];
  listHeight: number;
  listPhase: ListPhase;
  scoreWidth?: string;
}

const ScoreCardList = memo(function ScoreCardList({ displayedCards, listHeight, listPhase, scoreWidth }: Props) {
  const showSeason = useMediaQuery(QUERY_SHOW_SEASON);
  const showAccuracy = useMediaQuery(QUERY_SHOW_ACCURACY);

  if (displayedCards.length === 0 && listHeight === 0) return null;

  return (
    <div className={s.scoreCardListWrap} style={{
      height: listHeight,
      marginTop: Gap.xl,
    }}>
      <div className={s.scoreCardList}>
        {displayedCards.map((point, i) => {
          let animStyle: React.CSSProperties = {};
          if (listPhase === 'out') {
            animStyle = {
              opacity: 0,
              transform: 'translateY(-8px)',
              transition: `opacity 0.15s ease-in ${i * 40}ms, transform 0.15s ease-in ${i * 40}ms`,
            };
          } else if (listPhase === 'in') {
            animStyle = {
              opacity: 0,
              animation: `fadeInUp 300ms ease-out ${i * 60}ms forwards`,
            };
          }

          return (
            <div key={point.date} className={s.scoreListCard} style={animStyle}>
              <LeaderboardEntry
                label={new Date(point.date).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })}
                displayName=""
                score={point.score}
                season={point.season}
                accuracy={point.accuracy * ACCURACY_SCALE}
                isFullCombo={!!point.isFullCombo}
                showSeason={showSeason}
                showAccuracy={showAccuracy}
                scoreWidth={scoreWidth}
              />
            </div>
          );
        })}
      </div>
    </div>
  );
});

export default ScoreCardList;
