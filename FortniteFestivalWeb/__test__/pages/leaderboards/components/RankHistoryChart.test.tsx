import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Colors, Gap } from '@festival/theme';
import RankHistoryChart from '../../../../src/pages/leaderboards/components/RankHistoryChart';
import { TestProviders } from '../../../helpers/TestProviders';
import { stubMatchMedia } from '../../../helpers/browserStubs';

const originalTimeZone = process.env.TZ;
const mockUseRankHistoryAll = vi.fn();

vi.mock('../../../../src/components/common/GraphCard', () => ({
  default: ({ data, renderDetailCard, listData, renderListItem }: {
    data: unknown[];
    renderDetailCard?: (point: unknown) => React.ReactNode;
    listData?: unknown[];
    renderListItem?: (point: unknown, index: number, phase: 'idle' | 'in' | 'out') => React.ReactNode;
  }) => (
    <div>
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
    for (const label of screen.getAllByText('Bayesian-Calculated Rank:')) {
      expect(label.parentElement?.style.paddingTop).toBe('');
      expect(label.parentElement?.parentElement).toHaveStyle({ gap: `${Gap.xl}px` });
    }
  });
});