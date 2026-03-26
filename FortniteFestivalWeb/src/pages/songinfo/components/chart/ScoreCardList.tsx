/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Top score cards displayed beneath the chart.
 * Extracted from ScoreHistoryChart for readability.
 */
import { memo } from 'react';
import { ACCURACY_SCALE } from '@festival/core';
import { Colors, Gap, Radius, Font, Size, QUERY_SHOW_ACCURACY, QUERY_SHOW_SEASON, frostedCard, padding, border, transition } from '@festival/theme';
import { LeaderboardEntry } from '../../../leaderboard/global/components/LeaderboardEntry';
import { useMediaQuery } from '../../../../hooks/ui/useMediaQuery';
import type { ChartPoint } from '../../../../hooks/chart/useChartData';

type ListPhase = 'idle' | 'in' | 'out';

const scoreListCardBase: React.CSSProperties = {
  ...frostedCard, display: 'flex', alignItems: 'center', gap: Gap.xl,
  padding: padding(0, Gap.xl), height: Size.xl, borderRadius: Radius.md,
  fontSize: Font.md, color: 'inherit', transition: transition('border-color', 150),
};
const scoreListCardBestStyle: React.CSSProperties = {
  ...scoreListCardBase,
  backgroundColor: Colors.purpleHighlight,
  border: border(1, Colors.purpleHighlightBorder),
};

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
    <div style={{
      overflow: 'clip',
      overflowClipMargin: 24,
      transition: 'height 0.3s ease',
      height: listHeight,
      marginTop: Gap.xl,
    }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: Gap.sm }}>
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
            <div key={point.date} style={{ ...(i === 0 ? scoreListCardBestStyle : scoreListCardBase), ...animStyle }}>
              <LeaderboardEntry
                label={new Date(point.date).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })}
                displayName=""
                score={point.score}
                season={point.season}
                accuracy={point.accuracy * ACCURACY_SCALE}
                isFullCombo={!!point.isFullCombo}
                isPlayer={i === 0}
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
