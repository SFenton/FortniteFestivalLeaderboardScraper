import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { render, screen, within } from '@testing-library/react';
import { Colors, Gap } from '@festival/theme';
import RankHistoryChart from '../../../../src/pages/leaderboards/components/RankHistoryChart';
import { computeRankAxisWidth } from '../../../../src/pages/leaderboards/helpers/rankingHelpers';
import { TestProviders } from '../../../helpers/TestProviders';
import { stubMatchMedia } from '../../../helpers/browserStubs';

const originalTimeZone = process.env.TZ;
const mockUseRankHistoryAll = vi.fn();
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
  default: ({ data, renderChart, renderDetailCard, listData, renderListItem }: {
    data: unknown[];
    renderChart?: (args: { visibleData: unknown[]; animating: boolean; selectedPoint: unknown | null; setSelectedPoint: () => void }) => ReactNode;
    renderDetailCard?: (point: unknown) => ReactNode;
    listData?: unknown[];
    renderListItem?: (point: unknown, index: number, phase: 'idle' | 'in' | 'out') => ReactNode;
  }) => (
    <div>
      <div data-testid="chart-host">{renderChart?.({ visibleData: data, animating: false, selectedPoint: null, setSelectedPoint: () => undefined })}</div>
      <div data-testid="detail-card">{data[0] && renderDetailCard ? renderDetailCard(data[0]) : null}</div>
      <div data-testid="history-list">
        {listData?.map((point, index) => (
          <div key={index}>{renderListItem?.(point, index, 'idle')}</div>
        ))}
      </div>
    </div>
  ),
}));

vi.mock('../../../../src/hooks/chart/useRankHistory', async () => {
  const actual = await vi.importActual<typeof import('../../../../src/hooks/chart/useRankHistory')>('../../../../src/hooks/chart/useRankHistory');

  return {
    ...actual,
    useRankHistoryAll: (...args: unknown[]) => mockUseRankHistoryAll(...args),
  };
});

afterEach(() => {
  process.env.TZ = originalTimeZone;
  mockUseRankHistoryAll.mockReset();
  mockYAxisProps.splice(0, mockYAxisProps.length);
  stubMatchMedia(false);
});

describe('RankHistoryChart', () => {
  it('renders detail and list dates from the snapshot calendar day', () => {
    process.env.TZ = 'America/Los_Angeles';

    mockUseRankHistoryAll.mockReturnValue({
      Solo_Guitar: {
        loading: false,
        chartData: [{
          date: '2026-04-23',
          dateLabel: '4/23/26',
          timestamp: new Date(2026, 3, 23, 12, 0, 0, 0).getTime(),
          value: 94466122,
          rank: 5,
          songsPlayed: 220,
          coverage: 0.85,
          fullComboCount: 123,
        }],
      },
    });

    render(
      <TestProviders>
        <RankHistoryChart
          accountId="test-player"
          instruments={['Solo_Guitar']}
          metric="totalscore"
          defaultInstrument="Solo_Guitar"
        />
      </TestProviders>,
    );

    expect(screen.getAllByText('Apr 23, 2026')).toHaveLength(2);
    expect(screen.queryByText('Apr 22, 2026')).toBeNull();
  });

  it('formats rank axis ticks with separators and reserves dynamic axis label space', () => {
    mockUseRankHistoryAll.mockReturnValue({
      Solo_Guitar: {
        loading: false,
        chartData: [{
          date: '2026-04-23',
          dateLabel: '4/23/26',
          timestamp: new Date(2026, 3, 23, 12, 0, 0, 0).getTime(),
          value: 94466122,
          rank: 100000,
          songsPlayed: 220,
          coverage: 0.85,
          fullComboCount: 123,
        }],
      },
    });

    render(
      <TestProviders>
        <RankHistoryChart
          accountId="test-player"
          instruments={['Solo_Guitar']}
          metric="totalscore"
          defaultInstrument="Solo_Guitar"
        />
      </TestProviders>,
    );

    const rankAxis = mockYAxisProps.find(props => props.yAxisId === 'rank');
    expect(rankAxis?.tickFormatter?.(100000)).toBe('#100,000');
    expect(rankAxis?.width).toBe(computeRankAxisWidth([100000], [100000, 100000]));
  });

  it('renders FC-rate history values as a gold FC fraction using total songs', () => {
    mockUseRankHistoryAll.mockReturnValue({
      Solo_Guitar: {
        loading: false,
        chartData: [{
          date: '2026-04-23',
          dateLabel: '4/23/26',
          timestamp: new Date(2026, 3, 23, 12, 0, 0, 0).getTime(),
          value: 0.559,
          rank: 5,
          songsPlayed: 220,
          coverage: 0.5,
          fullComboCount: 123,
          totalChartedSongs: 500,
          rankedAccountCount: 1000,
        }],
      },
    });

    render(
      <TestProviders>
        <RankHistoryChart
          accountId="test-player"
          instruments={['Solo_Guitar']}
          metric="fcrate"
          defaultInstrument="Solo_Guitar"
        />
      </TestProviders>,
    );

    const fractions = screen.getAllByText((_, element) => element?.tagName === 'SPAN' && element.textContent === '123 / 500');
    expect(fractions).toHaveLength(2);
    for (const fcCount of screen.getAllByText('123')) {
      expect(fcCount).toHaveStyle({ color: Colors.gold });
    }
  });

  it('renders mobile adjusted history cards with songs, raw percentile, and Bayesian raw value', () => {
    stubMatchMedia(true);
    mockUseRankHistoryAll.mockReturnValue({
      Solo_Guitar: {
        loading: false,
        chartData: [{
          date: '2026-04-23',
          dateLabel: '4/23/26',
          timestamp: new Date(2026, 3, 23, 12, 0, 0, 0).getTime(),
          value: 0.0056,
          bayesianValue: 0.0409,
          rank: 5,
          songsPlayed: 123,
          coverage: 0.246,
          fullComboCount: 111,
          totalChartedSongs: 500,
          rankedAccountCount: 1000,
        }],
      },
    });

    render(
      <TestProviders>
        <RankHistoryChart
          accountId="test-player"
          instruments={['Solo_Guitar']}
          metric="adjusted"
          defaultInstrument="Solo_Guitar"
        />
      </TestProviders>,
    );

    expect(screen.getAllByText('123 / 500')).toHaveLength(2);
    expect(screen.getAllByText('Top 0.56%')).toHaveLength(2);
    expect(screen.getAllByText('Bayesian-Calculated Rank:')).toHaveLength(2);
    expect(screen.getAllByText('0.0409')).toHaveLength(2);
    const primaryRows = screen.getAllByTestId('rank-history-compact-primary-row');
    const primaryMetadataRows = screen.getAllByTestId('rank-history-compact-primary-metadata');
    const bayesianRows = screen.getAllByTestId('rank-history-compact-bayesian-row');
    expect(primaryRows).toHaveLength(2);
    expect(primaryMetadataRows).toHaveLength(2);
    expect(bayesianRows).toHaveLength(2);
    for (const primaryMetadata of primaryMetadataRows) {
      expect(within(primaryMetadata).getByText('123 / 500')).toBeTruthy();
      expect(within(primaryMetadata).getByText('Top 0.56%')).toBeTruthy();
    }
    for (const primaryRow of primaryRows) {
      expect(within(primaryRow).queryByText('Bayesian-Calculated Rank:')).toBeNull();
    }
    for (const bayesianRow of bayesianRows) {
      expect(within(bayesianRow).getByText('Bayesian-Calculated Rank:')).toBeTruthy();
      expect(within(bayesianRow).getByText('0.0409')).toBeTruthy();
      expect(bayesianRow).toHaveStyle({ justifyContent: 'flex-end' });
      expect(bayesianRow.parentElement).toHaveStyle({ gap: `${Gap.xl}px` });
    }
  });
});