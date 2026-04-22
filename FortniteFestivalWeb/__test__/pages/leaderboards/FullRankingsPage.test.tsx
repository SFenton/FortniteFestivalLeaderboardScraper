import { describe, it, expect, vi, beforeAll, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { computeRankWidth } from '../../../src/pages/leaderboards/helpers/rankingHelpers';
import { stubElementDimensions, stubResizeObserver, stubScrollTo } from '../../helpers/browserStubs';
import { TestProviders } from '../../helpers/TestProviders';

const mockApi = vi.hoisted(() => ({
  getComboRankings: vi.fn(),
  getPlayerComboRanking: vi.fn(),
  getRankings: vi.fn(),
  getPlayerRanking: vi.fn(),
}));

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

function makeAccountRankingEntry(rank: number, overrides: Record<string, unknown> = {}) {
  return {
    accountId: `acc-${rank}`,
    displayName: `Player ${rank}`,
    songsPlayed: 120,
    totalChartedSongs: 160,
    coverage: 0.75,
    rawSkillRating: 0.9234,
    adjustedSkillRating: 0.9234,
    adjustedSkillRank: rank,
    weightedRating: 0.9123,
    weightedRank: rank,
    fcRate: 0.42,
    fcRateRank: rank,
    totalScore: 1234567 - rank,
    totalScoreRank: rank,
    maxScorePercent: 0.885,
    maxScorePercentRank: rank,
    avgAccuracy: 0.972,
    fullComboCount: 65,
    avgStars: 5.6,
    bestRank: 1,
    avgRank: 7.4,
    rawMaxScorePercent: 0.885,
    rawWeightedRating: 0.9123,
    computedAt: '2026-04-22T00:00:00Z',
    ...overrides,
  };
}

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver({ width: 1024, height: 800 });
  stubElementDimensions(800);
});

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-player', displayName: 'Test Player' }));

  mockApi.getComboRankings.mockResolvedValue({
    comboId: '05',
    rankBy: 'totalscore',
    page: 1,
    pageSize: 25,
    totalAccounts: 100,
    entries: [{
      rank: 1,
      accountId: 'top-05',
      displayName: 'Top 05',
      adjustedRating: 0.9,
      weightedRating: 0.8,
      fcRate: 0.7,
      totalScore: 123456,
      maxScorePercent: 0.88,
      songsPlayed: 25,
      fullComboCount: 20,
      computedAt: '2026-01-01T00:00:00Z',
    }],
  });
  mockApi.getPlayerComboRanking.mockResolvedValue({
    comboId: '05',
    rankBy: 'totalscore',
    totalAccounts: 100,
    rank: 42,
    accountId: 'test-player',
    displayName: 'Test Player',
    adjustedRating: 0.5,
    weightedRating: 0.4,
    fcRate: 0.3,
    totalScore: 654321,
    maxScorePercent: 0.76,
    songsPlayed: 20,
    fullComboCount: 12,
    computedAt: '2026-01-01T00:00:00Z',
  });
  mockApi.getRankings.mockResolvedValue({ entries: [], totalAccounts: 0, instrument: 'Solo_Guitar', rankBy: 'totalscore', page: 1, pageSize: 25 });
  mockApi.getPlayerRanking.mockResolvedValue(null);
});

const { default: FullRankingsPage } = await import('../../../src/pages/leaderboards/FullRankingsPage');

describe('FullRankingsPage', () => {
  it('loads combo ranking routes with combo leaderboard queries', async () => {
    render(
      <TestProviders route="/leaderboards/all?combo=05&rankBy=totalscore" accountId="test-player">
        <Routes>
          <Route path="/leaderboards/all" element={<FullRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(mockApi.getComboRankings).toHaveBeenCalledWith('05', 'totalscore', 1, 25);
    });
    expect(mockApi.getPlayerComboRanking).toHaveBeenCalledWith('test-player', '05', 'totalscore');
    expect(await screen.findByText('Top 05')).toBeTruthy();
    expect(await screen.findByText('Lead + Drums Leaderboards')).toBeTruthy();
  });

  it('keeps the fixed player footer width independent from the scrollable page rows', async () => {
    mockApi.getRankings.mockResolvedValue({
      instrument: 'Solo_Guitar',
      rankBy: 'totalscore',
      page: 1,
      pageSize: 25,
      totalAccounts: 12345,
      entries: [makeAccountRankingEntry(1, { accountId: 'top-player', displayName: 'Top Player' })],
    });
    mockApi.getPlayerRanking.mockResolvedValue(
      makeAccountRankingEntry(12345, { accountId: 'test-player', displayName: 'Test Player' }),
    );

    render(
      <TestProviders route="/leaderboards/all?instrument=Solo_Guitar&rankBy=totalscore" accountId="test-player">
        <Routes>
          <Route path="/leaderboards/all" element={<FullRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    const topRank = await screen.findByText('#1');
    const playerRank = await screen.findByText('#12,345');

    expect(topRank).toHaveStyle({ width: `${computeRankWidth([1])}px` });
    expect(playerRank).toHaveStyle({ width: `${computeRankWidth([12345])}px` });
    expect(topRank.style.width).not.toBe(playerRank.style.width);
  });
});
