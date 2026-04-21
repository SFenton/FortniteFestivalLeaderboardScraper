import { describe, it, expect, vi, beforeAll, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { stubElementDimensions, stubResizeObserver, stubScrollTo } from '../../helpers/browserStubs';
import { TestProviders } from '../../helpers/TestProviders';

const mockApi = vi.hoisted(() => ({
  getComboRankings: vi.fn(),
  getPlayerComboRanking: vi.fn(),
  getRankings: vi.fn(),
  getPlayerRanking: vi.fn(),
}));

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

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
});
