import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import type { BandRankingEntry, BandType, ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { stubElementDimensions, stubMatchMedia, stubResizeObserver, stubScrollTo } from '../../helpers/browserStubs';
import { TestProviders } from '../../helpers/TestProviders';
import BandRankingsPage from '../../../src/pages/leaderboards/BandRankingsPage';

const mockApi = vi.hoisted(() => ({
  getBandRankings: vi.fn(),
  getSongs: vi.fn(),
  getShop: vi.fn(),
}));

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

vi.mock('../../../src/hooks/ui/usePageTransition', () => ({
  usePageTransition: () => ({ phase: 'contentIn', shouldStagger: false }),
}));

vi.mock('../../../src/hooks/ui/useStagger', () => ({
  useStagger: () => ({ forIndex: () => ({}), clearAnim: vi.fn() }),
}));

function makeBandEntry(rank: number, names: string[]): BandRankingEntry {
  return {
    bandId: `band-${rank}`,
    teamKey: names.map(name => name.toLowerCase()).join(':'),
    teamMembers: names.map((name, index) => ({ accountId: `acct-${rank}-${index}`, displayName: name })),
    members: names.map((name, index) => ({
      accountId: `acct-${rank}-${index}`,
      displayName: name,
      instruments: (index === 0 ? ['Solo_Guitar', 'Solo_Bass'] : ['Solo_Drums']) as ServerInstrumentKey[],
    })),
    songsPlayed: 120,
    totalChartedSongs: 160,
    coverage: 0.75,
    rawSkillRating: 0.0123,
    adjustedSkillRating: 0.1234,
    adjustedSkillRank: rank,
    weightedRating: 0.2345,
    weightedRank: rank,
    fcRate: 0.42,
    fcRateRank: rank,
    totalScore: 9_876_543 - rank,
    totalScoreRank: rank,
    avgAccuracy: 0.972,
    fullComboCount: 65,
    avgStars: 5.6,
    bestRank: 1,
    avgRank: 7.4,
    rawWeightedRating: 0.2345,
    computedAt: '2026-04-28T00:00:00Z',
  };
}

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver({ width: 1024, height: 800 });
  stubElementDimensions(800);
});

beforeEach(() => {
  vi.clearAllMocks();
  stubMatchMedia(false);
  localStorage.clear();
  mockApi.getSongs.mockResolvedValue({ currentSeason: 9, songs: [] });
  mockApi.getShop.mockResolvedValue({ songs: [] });
  mockApi.getBandRankings.mockImplementation((bandType: BandType, _comboId: string | undefined, rankBy: string, page: number, pageSize: number) => Promise.resolve({
    bandType,
    comboId: null,
    rankBy,
    page,
    pageSize,
    totalTeams: 42,
    entries: [makeBandEntry(page === 1 ? 1 : 26, page === 1 ? ['Alpha', 'Beta'] : ['Gamma', 'Delta'])],
  }));
});

describe('BandRankingsPage', () => {
  it('renders all-up band rankings with shared pagination and band detail links', async () => {
    render(
      <TestProviders route="/leaderboards/bands/Band_Duets?rankBy=totalscore">
        <Routes>
          <Route path="/leaderboards/bands/:bandType" element={<BandRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', undefined, 'totalscore', 1, 25);
    });

    expect(await screen.findByText('Duos Leaderboards')).toBeTruthy();
    expect(await screen.findByText('42 ranked bands')).toBeTruthy();
    const list = await screen.findByTestId('band-rankings-card-list');
    expect(within(list).getAllByTestId('band-rankings-entry-0')).toHaveLength(1);
    const row = screen.getByTestId('band-rankings-entry-0');
    expect(within(row).getByText('Alpha')).toBeTruthy();
    expect(within(row).getByText('Beta')).toBeTruthy();
    const metadata = within(row).getByTestId('band-ranking-metadata');
    expect(within(metadata).getByTestId('ranking-songs-label')).toHaveTextContent('120 / 160');
    expect(within(metadata).getByTestId('ranking-rating-label')).toHaveTextContent('9,876,542');
    expect(within(row).getByAltText('Solo_Guitar')).toBeTruthy();
    expect(within(row).getByAltText('Solo_Bass')).toBeTruthy();
    expect(within(row).getByAltText('Solo_Drums')).toBeTruthy();
    expect(row).toHaveAttribute('href', '/bands/band-1?bandType=Band_Duets&teamKey=alpha%3Abeta&names=Alpha%20%2B%20Beta');
    expect(screen.getByTestId('leaderboard-page-info')).toHaveTextContent('1 / 2');

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', undefined, 'totalscore', 2, 25);
    });
  });
});