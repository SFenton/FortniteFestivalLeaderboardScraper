import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { render, screen } from '@testing-library/react';
import BandRankHistoryChart from '../../../../src/pages/band/components/BandRankHistoryChart';
import { computeRankAxisWidth } from '../../../../src/pages/leaderboards/helpers/rankingHelpers';
import { TestProviders } from '../../../helpers/TestProviders';

const mockUseBandRankHistory = vi.hoisted(() => vi.fn());
const mockYAxisProps = vi.hoisted(() => [] as Array<{ yAxisId?: string; width?: number; tickFormatter?: (value: number) => string }>);

vi.mock('recharts', () => ({
  ResponsiveContainer: ({ children }: { children: ReactNode }) => <div data-testid="responsive-container">{children}</div>,
  ComposedChart: ({ children }: { children: ReactNode }) => <div data-testid="composed-chart">{children}</div>,
  CartesianGrid: () => null,
  XAxis: () => null,
  YAxis: (props: { yAxisId?: string; width?: number; tickFormatter?: (value: number) => string }) => {
    mockYAxisProps.push(props);
    return <div data-testid={`y-axis-${props.yAxisId ?? 'unknown'}`} />;
  },
  Tooltip: () => null,
  Legend: () => null,
  Bar: () => null,
  Line: () => null,
}));

vi.mock('../../../../src/components/common/GraphCard', () => ({
  default: ({ data, subtitle, renderChart }: {
    data: unknown[];
    subtitle?: string;
    renderChart?: (args: { visibleData: unknown[]; animating: boolean; selectedPoint: unknown | null; setSelectedPoint: () => void }) => ReactNode;
  }) => (
    <div data-testid="graph-card">
      <div data-testid="graph-card-subtitle">{subtitle}</div>
      {renderChart?.({ visibleData: data, animating: false, selectedPoint: null, setSelectedPoint: () => undefined })}
    </div>
  ),
}));

vi.mock('../../../../src/hooks/chart/useBandRankHistory', () => ({
  useBandRankHistory: (...args: unknown[]) => mockUseBandRankHistory(...args),
}));

afterEach(() => {
  mockUseBandRankHistory.mockReset();
  mockYAxisProps.splice(0, mockYAxisProps.length);
});

describe('BandRankHistoryChart', () => {
  it('formats rank axis ticks with separators and reserves dynamic axis label space', () => {
    mockUseBandRankHistory.mockReturnValue({
      chartData: [{
        date: '2026-04-23',
        dateLabel: '4/23/26',
        timestamp: new Date(2026, 3, 23, 12, 0, 0, 0).getTime(),
        value: 0.9,
        rank: 3349832,
        songsPlayed: 220,
        coverage: 0.85,
        fullComboCount: 123,
        totalChartedSongs: 500,
        rankedAccountCount: 3349832,
      }],
      loading: false,
      historyStatus: 'ready',
      historyMessage: null,
    });

    render(
      <TestProviders>
        <BandRankHistoryChart
          bandType="Band_Trios"
          teamKey="team-a:team-b:team-c"
          totalRankedTeams={3349832}
          skipAnimation
        />
      </TestProviders>,
    );

    const rankAxis = mockYAxisProps.find(props => props.yAxisId === 'rank');
    expect(rankAxis?.tickFormatter?.(3349832)).toBe('#3,349,832');
    expect(rankAxis?.width).toBe(computeRankAxisWidth([3349832], [3349832, 3349832]));
  });

  it('keeps the base subtitle and hides raw backend failure messages', () => {
    mockUseBandRankHistory.mockReturnValue({
      chartData: [{
        date: '2026-05-08',
        dateLabel: '5/8/26',
        timestamp: new Date(2026, 4, 8, 12, 0, 0, 0).getTime(),
        value: 0.89,
        rank: 3047505,
        songsPlayed: 71,
        coverage: 0.11,
        fullComboCount: 5,
        totalChartedSongs: 655,
        rankedAccountCount: 3349832,
      }],
      loading: false,
      historyStatus: 'failed',
      historyMessage: '53100: could not write to file "base/pgsql_tmp/pgsql_tmp1013127.94": No space left on device',
    });

    render(
      <TestProviders>
        <BandRankHistoryChart
          bandType="Band_Trios"
          teamKey="team-a:team-b:team-c"
          totalRankedTeams={3349832}
          skipAnimation
        />
      </TestProviders>,
    );

    expect(screen.getByTestId('graph-card-subtitle')).toHaveTextContent('Any-combo ranking progression over the past 30 days.');
    expect(screen.queryByText(/pgsql_tmp|No space left on device|temporarily unavailable/i)).not.toBeInTheDocument();
  });
});