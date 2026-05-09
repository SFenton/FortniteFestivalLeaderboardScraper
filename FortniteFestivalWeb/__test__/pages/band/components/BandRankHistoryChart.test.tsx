import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { render } from '@testing-library/react';
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
  default: ({ data, renderChart }: {
    data: unknown[];
    renderChart?: (args: { visibleData: unknown[]; animating: boolean; selectedPoint: unknown | null; setSelectedPoint: () => void }) => ReactNode;
  }) => (
    <div data-testid="graph-card">
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
});