import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import type { BandConfiguration, BandRankingEntry, BandType, ServerInstrumentKey } from '@festival/core/api/serverTypes';
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

function makeBandEntry(rank: number, names: string[], configurations?: BandConfiguration[]): BandRankingEntry {
  return {
    bandId: `band-${rank}`,
    teamKey: names.map(name => name.toLowerCase()).join(':'),
    teamMembers: names.map((name, index) => ({ accountId: `acct-${rank}-${index}`, displayName: name })),
    configurations,
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

  it('uses the applied selected-band combo when the route band type matches', async () => {
    render(
      <TestProviders
        route="/leaderboards/bands/Band_Duets?rankBy=totalscore"
        bandFilter={{
          bandId: 'selected-band',
          bandType: 'Band_Duets',
          teamKey: 'acct-a:acct-b',
          comboId: 'Solo_Guitar+Solo_Bass',
          assignments: [
            { accountId: 'acct-a', instrument: 'Solo_Guitar' },
            { accountId: 'acct-b', instrument: 'Solo_Bass' },
          ],
        }}
      >
        <Routes>
          <Route path="/leaderboards/bands/:bandType" element={<BandRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', 'Solo_Guitar+Solo_Bass', 'totalscore', 1, 25);
    });
  });

  it('renders observed Duos assignments as compact possibilities on the full band rankings page', async () => {
    const configurations: BandConfiguration[] = [
      {
        rawInstrumentCombo: '0:2',
        comboId: 'Solo_Guitar+Solo_Vocals',
        instruments: ['Solo_Guitar', 'Solo_Vocals'],
        assignmentKey: 'acct-1-0=Solo_Guitar|acct-1-1=Solo_Vocals',
        appearanceCount: 5,
        memberInstruments: { 'acct-1-0': 'Solo_Guitar', 'acct-1-1': 'Solo_Vocals' },
      },
      {
        rawInstrumentCombo: '2:0',
        comboId: 'Solo_Guitar+Solo_Vocals',
        instruments: ['Solo_Guitar', 'Solo_Vocals'],
        assignmentKey: 'acct-1-0=Solo_Vocals|acct-1-1=Solo_Guitar',
        appearanceCount: 1,
        memberInstruments: { 'acct-1-0': 'Solo_Vocals', 'acct-1-1': 'Solo_Guitar' },
      },
      {
        rawInstrumentCombo: '0:1',
        comboId: 'Solo_Guitar+Solo_Bass',
        instruments: ['Solo_Guitar', 'Solo_Bass'],
        assignmentKey: 'acct-1-0=Solo_Guitar|acct-1-1=Solo_Bass',
        appearanceCount: 1,
        memberInstruments: { 'acct-1-0': 'Solo_Guitar', 'acct-1-1': 'Solo_Bass' },
      },
    ];
    mockApi.getBandRankings.mockResolvedValue({
      bandType: 'Band_Duets',
      comboId: 'Solo_Guitar+Solo_Vocals',
      rankBy: 'totalscore',
      page: 1,
      pageSize: 25,
      totalTeams: 42,
      entries: [makeBandEntry(1, ['Alpha', 'Beta'], configurations)],
    });

    render(
      <TestProviders
        route="/leaderboards/bands/Band_Duets?rankBy=totalscore"
        bandFilter={{
          bandId: 'selected-band',
          bandType: 'Band_Duets',
          teamKey: 'acct-a:acct-b',
          comboId: 'Solo_Guitar+Solo_Vocals',
          assignments: [
            { accountId: 'acct-a', instrument: 'Solo_Guitar' },
            { accountId: 'acct-b', instrument: 'Solo_Vocals' },
          ],
        }}
      >
        <Routes>
          <Route path="/leaderboards/bands/:bandType" element={<BandRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    const row = await screen.findByTestId('band-rankings-entry-0');
    expect(within(row).queryByTestId('band-member-lineup')).toBeNull();
    const rows = within(row).getAllByTestId('band-member-row');
    expect(rows).toHaveLength(2);
    expect(within(rows[0]!).getByText('Alpha')).toBeTruthy();
    expect(within(rows[0]!).getByAltText('Solo_Guitar')).toBeTruthy();
    expect(within(rows[0]!).getByAltText('Solo_Vocals')).toBeTruthy();
    expect(within(rows[1]!).getByText('Beta')).toBeTruthy();
    expect(within(rows[1]!).getByAltText('Solo_Guitar')).toBeTruthy();
    expect(within(rows[1]!).getByAltText('Solo_Vocals')).toBeTruthy();
    expect(row).toHaveAttribute('href', '/bands/band-1?bandType=Band_Duets&teamKey=alpha%3Abeta&names=Alpha%20%2B%20Beta');
  });
});
