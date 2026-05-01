import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import type { BandRankingEntry, BandType, ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { stubElementDimensions, stubMatchMedia, stubResizeObserver, stubScrollTo } from '../../helpers/browserStubs';
import { TestProviders } from '../../helpers/TestProviders';
import LeaderboardsOverviewPage from '../../../src/pages/leaderboards/LeaderboardsOverviewPage';
import { computeRankWidth } from '../../../src/pages/leaderboards/helpers/rankingHelpers';
import { writeSelectedProfile } from '../../../src/state/selectedProfile';

const mockApi = vi.hoisted(() => ({
  getRankings: vi.fn(),
  getPlayerRanking: vi.fn(),
  getBandRankings: vi.fn(),
  getBandRanking: vi.fn(),
  getSongs: vi.fn(),
  getShop: vi.fn(),
}));

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

function makeBandEntry(
  rank: number,
  bandType: BandType,
  names: string[],
  accountIds?: string[],
  memberInstruments?: ServerInstrumentKey[][],
): BandRankingEntry {
  const ids = accountIds ?? names.map((_, index) => `${bandType}-${rank}-${index}`);
  return {
    bandId: `${bandType}-${rank}`,
    teamKey: ids.join(':'),
    teamMembers: names.map((name, index) => ({ accountId: ids[index]!, displayName: name })),
    members: names.map((name, index) => ({
      accountId: ids[index]!,
      displayName: name,
      instruments: memberInstruments?.[index] ?? (index === 0 ? ['Solo_Guitar', 'Solo_Bass'] : ['Solo_Drums']) as ServerInstrumentKey[],
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
  mockApi.getRankings.mockResolvedValue({ entries: [], totalAccounts: 0, instrument: 'Solo_Guitar', rankBy: 'totalscore', page: 1, pageSize: 10 });
  mockApi.getPlayerRanking.mockResolvedValue(null);
  mockApi.getBandRanking.mockRejectedValue(new Error('not found'));
  mockApi.getBandRankings.mockImplementation((bandType: BandType) => Promise.resolve({
    bandType,
    comboId: null,
    rankBy: 'totalscore',
    page: 1,
    pageSize: 10,
    totalTeams: bandType === 'Band_Duets' ? 42 : 12,
    entries: [makeBandEntry(1, bandType, bandType === 'Band_Duets' ? ['Alpha', 'Beta'] : ['Gamma', 'Delta'])],
  }));
});

describe('LeaderboardsOverviewPage band rankings', () => {
  it('renders all-up Duos, Trios, and Quads band ranking cards behind the feature flag', async () => {
    render(
      <TestProviders route="/leaderboards">
        <Routes>
          <Route path="/leaderboards" element={<LeaderboardsOverviewPage />} />
        </Routes>
      </TestProviders>,
    );

    const bandStack = await screen.findByTestId('leaderboards-band-section-stack');
    const instrumentGrid = screen.getByTestId('leaderboards-instrument-grid');
    expect(bandStack).toHaveStyle('width: 100%');
    expect(within(bandStack).getByTestId('band-ranking-card-Band_Duets')).toBeTruthy();
    expect(within(bandStack).getByTestId('band-ranking-card-Band_Trios')).toBeTruthy();
    expect(within(bandStack).getByTestId('band-ranking-card-Band_Quad')).toBeTruthy();
    expect(within(instrumentGrid).queryByTestId('band-ranking-card-Band_Duets')).toBeNull();

    const duosCard = within(bandStack).getByTestId('band-ranking-card-Band_Duets');
    expect(within(duosCard).getByText('Duos')).toBeTruthy();
    const duosEntry = within(duosCard).getByTestId('band-ranking-entry-Band_Duets-0');
    expect(within(duosEntry).getByText('Alpha')).toBeTruthy();
    expect(within(duosEntry).getByText('Beta')).toBeTruthy();
    const metadata = within(duosEntry).getByTestId('band-ranking-metadata');
    expect(within(metadata).getByTestId('ranking-songs-label')).toHaveTextContent('120 / 160');
    expect(within(metadata).getByTestId('ranking-rating-label')).toHaveTextContent('9,876,542');
    expect(within(duosEntry).getByAltText('Solo_Guitar')).toBeTruthy();
    expect(within(duosEntry).getByAltText('Solo_Bass')).toBeTruthy();
    expect(within(duosEntry).getByAltText('Solo_Drums')).toBeTruthy();
    expect(within(duosCard).getByText('View all band rankings (42)')).toBeTruthy();

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', undefined, 'totalscore', 1, 10, undefined, undefined);
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Trios', undefined, 'totalscore', 1, 10, undefined, undefined);
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Quad', undefined, 'totalscore', 1, 10, undefined, undefined);
    });
  });

  it('requests and renders selected-player band rankings using the active metric', async () => {
    writeSelectedProfile({ type: 'player', accountId: 'tracked-player', displayName: 'Tracked Player' });
    mockApi.getBandRankings.mockImplementation((bandType: BandType, _comboId: string | undefined, rankBy: string, page: number, pageSize: number, selectedAccountId?: string) => Promise.resolve({
      bandType,
      comboId: null,
      rankBy,
      page,
      pageSize,
      totalTeams: 42,
      entries: [makeBandEntry(1, bandType, ['Alpha', 'Beta'])],
      selectedPlayerEntry: bandType === 'Band_Duets' && selectedAccountId === 'tracked-player'
        ? makeBandEntry(17, bandType, ['Tracked Player', 'Partner'], ['tracked-player', 'partner-player'])
        : null,
    }));

    render(
      <TestProviders route="/leaderboards?rankBy=weighted">
        <Routes>
          <Route path="/leaderboards" element={<LeaderboardsOverviewPage />} />
        </Routes>
      </TestProviders>,
    );

    const selectedRow = await screen.findByTestId('band-ranking-selected-entry-Band_Duets');
    expect(within(selectedRow).getByText('Tracked Player')).toBeTruthy();
    expect(within(selectedRow).getByText('Partner')).toBeTruthy();
    expect(within(selectedRow).getByText('#17')).toBeTruthy();

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', undefined, 'weighted', 1, 10, 'tracked-player', undefined);
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Trios', undefined, 'weighted', 1, 10, 'tracked-player', undefined);
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Quad', undefined, 'weighted', 1, 10, 'tracked-player', undefined);
    });
  });

  it('requests and renders an exact selected-band ranking only for the matching band type', async () => {
    localStorage.setItem('fst:selectedProfile', JSON.stringify({
      type: 'band',
      bandId: 'selected-band',
      bandType: 'Band_Duets',
      teamKey: 'selected-a:selected-b',
      displayName: 'Selected Duo',
      members: [
        { accountId: 'selected-a', displayName: 'Selected A' },
        { accountId: 'selected-b', displayName: 'Selected B' },
      ],
    }));
    mockApi.getBandRankings.mockImplementation((bandType: BandType, _comboId: string | undefined, rankBy: string, page: number, pageSize: number, selectedAccountId?: string, selectedTeamKey?: string) => Promise.resolve({
      bandType,
      comboId: null,
      rankBy,
      page,
      pageSize,
      totalTeams: 42,
      entries: [makeBandEntry(1, bandType, ['Alpha', 'Beta'])],
      selectedBandEntry: bandType === 'Band_Duets' && selectedAccountId == null && selectedTeamKey === 'selected-a:selected-b'
        ? makeBandEntry(17, bandType, ['Selected A', 'Selected B'], ['selected-a', 'selected-b'])
        : null,
    }));

    render(
      <TestProviders route="/leaderboards?rankBy=weighted">
        <Routes>
          <Route path="/leaderboards" element={<LeaderboardsOverviewPage />} />
        </Routes>
      </TestProviders>,
    );

    const selectedRow = await screen.findByTestId('band-ranking-selected-entry-Band_Duets');
    expect(within(selectedRow).getByText('Selected A')).toBeTruthy();
    expect(within(selectedRow).getByText('Selected B')).toBeTruthy();
    expect(within(selectedRow).getByText('#17')).toBeTruthy();
    expect(screen.queryByTestId('band-ranking-selected-entry-Band_Trios')).toBeNull();
    expect(screen.queryByTestId('band-ranking-selected-entry-Band_Quad')).toBeNull();

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', undefined, 'weighted', 1, 10, undefined, 'selected-a:selected-b');
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Trios', undefined, 'weighted', 1, 10, undefined, undefined);
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Quad', undefined, 'weighted', 1, 10, undefined, undefined);
      expect(mockApi.getBandRanking).toHaveBeenCalledWith('Band_Duets', 'selected-a:selected-b', undefined, 'weighted');
    });
  });

  it('applies the selected-band combo only to the matching band type', async () => {
    localStorage.setItem('fst:selectedProfile', JSON.stringify({
      type: 'band',
      bandId: 'selected-band',
      bandType: 'Band_Duets',
      teamKey: 'selected-a:selected-b',
      displayName: 'Selected Duo',
      members: [
        { accountId: 'selected-a', displayName: 'Selected A' },
        { accountId: 'selected-b', displayName: 'Selected B' },
      ],
    }));

    render(
      <TestProviders
        route="/leaderboards?rankBy=weighted"
        bandFilter={{
          bandId: 'selected-band',
          bandType: 'Band_Duets',
          teamKey: 'selected-a:selected-b',
          comboId: 'Solo_Guitar+Solo_Bass',
          assignments: [
            { accountId: 'selected-a', instrument: 'Solo_Guitar' },
            { accountId: 'selected-b', instrument: 'Solo_Bass' },
          ],
        }}
      >
        <Routes>
          <Route path="/leaderboards" element={<LeaderboardsOverviewPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', 'Solo_Guitar+Solo_Bass', 'weighted', 1, 10, undefined, 'selected-a:selected-b');
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Trios', undefined, 'weighted', 1, 10, undefined, undefined);
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Quad', undefined, 'weighted', 1, 10, undefined, undefined);
      expect(mockApi.getBandRanking).toHaveBeenCalledWith('Band_Duets', 'selected-a:selected-b', 'Solo_Guitar+Solo_Bass', 'weighted');
    });
  });

  it('filters matching band card member instruments by the active selected-band combo', async () => {
    localStorage.setItem('fst:selectedProfile', JSON.stringify({
      type: 'band',
      bandId: 'selected-band',
      bandType: 'Band_Duets',
      teamKey: 'selected-a:selected-b',
      displayName: 'Selected Duo',
      members: [
        { accountId: 'selected-a', displayName: 'Selected A' },
        { accountId: 'selected-b', displayName: 'Selected B' },
      ],
    }));

    mockApi.getBandRankings.mockImplementation((bandType: BandType, comboId: string | undefined, rankBy: string, page: number, pageSize: number, selectedAccountId?: string, selectedTeamKey?: string) => Promise.resolve({
      bandType,
      comboId: comboId ?? null,
      rankBy,
      page,
      pageSize,
      totalTeams: 42,
      entries: [bandType === 'Band_Duets'
        ? makeBandEntry(1, bandType, ['Alpha', 'Beta'], ['duo-a', 'duo-b'], [
          ['Solo_Guitar', 'Solo_Vocals', 'Solo_Drums', 'Solo_PeripheralGuitar'],
          ['Solo_Bass', 'Solo_Drums'],
        ])
        : makeBandEntry(1, bandType, ['Gamma', 'Delta'], undefined, [
          ['Solo_Guitar', 'Solo_Vocals'],
          ['Solo_Bass', 'Solo_Drums'],
        ])],
      selectedBandEntry: bandType === 'Band_Duets' && selectedAccountId == null && selectedTeamKey === 'selected-a:selected-b'
        ? makeBandEntry(17, bandType, ['Selected A', 'Selected B'], ['selected-a', 'selected-b'])
        : null,
    }));

    render(
      <TestProviders
        route="/leaderboards?rankBy=weighted"
        bandFilter={{
          bandId: 'selected-band',
          bandType: 'Band_Duets',
          teamKey: 'selected-a:selected-b',
          comboId: 'Solo_Guitar+Solo_Bass',
          assignments: [
            { accountId: 'selected-a', instrument: 'Solo_Guitar' },
            { accountId: 'selected-b', instrument: 'Solo_Bass' },
          ],
        }}
      >
        <Routes>
          <Route path="/leaderboards" element={<LeaderboardsOverviewPage />} />
        </Routes>
      </TestProviders>,
    );

    const duosEntry = await screen.findByTestId('band-ranking-entry-Band_Duets-0');
    const duosRows = within(duosEntry).getAllByTestId('band-member-row');
    expect(within(duosRows[0]!).getByAltText('Solo_Guitar')).toBeTruthy();
    expect(within(duosRows[0]!).queryByAltText('Solo_Vocals')).toBeNull();
    expect(within(duosRows[0]!).queryByAltText('Solo_Drums')).toBeNull();
    expect(within(duosRows[0]!).queryByAltText('Solo_PeripheralGuitar')).toBeNull();
    expect(within(duosRows[1]!).getByAltText('Solo_Bass')).toBeTruthy();
    expect(within(duosRows[1]!).queryByAltText('Solo_Drums')).toBeNull();

    const triosEntry = await screen.findByTestId('band-ranking-entry-Band_Trios-0');
    const triosRows = within(triosEntry).getAllByTestId('band-member-row');
    expect(within(triosRows[0]!).getByAltText('Solo_Guitar')).toBeTruthy();
    expect(within(triosRows[0]!).getByAltText('Solo_Vocals')).toBeTruthy();
    expect(within(triosRows[1]!).getByAltText('Solo_Bass')).toBeTruthy();
    expect(within(triosRows[1]!).getByAltText('Solo_Drums')).toBeTruthy();

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', 'Solo_Guitar+Solo_Bass', 'weighted', 1, 10, undefined, 'selected-a:selected-b');
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Trios', undefined, 'weighted', 1, 10, undefined, undefined);
    });
  });

  it('falls back to the exact band ranking endpoint when the overview response has no selected band field', async () => {
    localStorage.setItem('fst:selectedProfile', JSON.stringify({
      type: 'band',
      bandId: 'selected-band',
      bandType: 'Band_Duets',
      teamKey: 'selected-a:selected-b',
      displayName: 'Selected Duo',
      members: [
        { accountId: 'selected-a', displayName: 'Selected A' },
        { accountId: 'selected-b', displayName: 'Selected B' },
      ],
    }));
    mockApi.getBandRankings.mockImplementation((bandType: BandType, _comboId: string | undefined, rankBy: string, page: number, pageSize: number, selectedAccountId?: string, selectedTeamKey?: string) => Promise.resolve({
      bandType,
      comboId: null,
      rankBy,
      page,
      pageSize,
      totalTeams: 42,
      entries: [makeBandEntry(1, bandType, ['Alpha', 'Beta'])],
      selectedPlayerEntry: selectedAccountId ? makeBandEntry(12, bandType, ['Tracked', 'Partner']) : null,
      selectedBandEntry: selectedTeamKey ? null : undefined,
    }));
    mockApi.getBandRanking.mockResolvedValue(makeBandEntry(17, 'Band_Duets', ['Selected A', 'Selected B'], ['selected-a', 'selected-b']));

    render(
      <TestProviders route="/leaderboards?rankBy=weighted">
        <Routes>
          <Route path="/leaderboards" element={<LeaderboardsOverviewPage />} />
        </Routes>
      </TestProviders>,
    );

    const selectedRow = await screen.findByTestId('band-ranking-selected-entry-Band_Duets');
    expect(within(selectedRow).getByText('Selected A')).toBeTruthy();
    expect(within(selectedRow).getByText('Selected B')).toBeTruthy();
    expect(within(selectedRow).getByText('#17')).toBeTruthy();
    expect(screen.queryByTestId('band-ranking-selected-entry-Band_Trios')).toBeNull();
    expect(screen.queryByTestId('band-ranking-selected-entry-Band_Quad')).toBeNull();

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', undefined, 'weighted', 1, 10, undefined, 'selected-a:selected-b');
      expect(mockApi.getBandRanking).toHaveBeenCalledWith('Band_Duets', 'selected-a:selected-b', undefined, 'weighted');
    });
  });

  it('shares one rank rail width across top and selected rows within a band type', async () => {
    localStorage.setItem('fst:selectedProfile', JSON.stringify({
      type: 'band',
      bandId: 'selected-band',
      bandType: 'Band_Duets',
      teamKey: 'selected-a:selected-b',
      displayName: 'Selected Duo',
      members: [
        { accountId: 'selected-a', displayName: 'Selected A' },
        { accountId: 'selected-b', displayName: 'Selected B' },
      ],
    }));
    mockApi.getBandRankings.mockImplementation((bandType: BandType, _comboId: string | undefined, rankBy: string, page: number, pageSize: number, _selectedAccountId?: string, selectedTeamKey?: string) => Promise.resolve({
      bandType,
      comboId: null,
      rankBy,
      page,
      pageSize,
      totalTeams: 25_000,
      entries: [makeBandEntry(1, bandType, ['Alpha', 'Beta'])],
      selectedBandEntry: bandType === 'Band_Duets' && selectedTeamKey === 'selected-a:selected-b'
        ? makeBandEntry(12_345, bandType, ['Selected A', 'Selected B'], ['selected-a', 'selected-b'])
        : null,
    }));

    render(
      <TestProviders route="/leaderboards?rankBy=weighted">
        <Routes>
          <Route path="/leaderboards" element={<LeaderboardsOverviewPage />} />
        </Routes>
      </TestProviders>,
    );

    const topRow = await screen.findByTestId('band-ranking-entry-Band_Duets-0');
    const selectedRow = await screen.findByTestId('band-ranking-selected-entry-Band_Duets');
    const expectedWidth = `${computeRankWidth([1, 12_345])}px`;

    expect(within(topRow).getByTestId('band-rank-rail')).toHaveStyle({ width: expectedWidth, minWidth: expectedWidth });
    expect(within(selectedRow).getByTestId('band-rank-rail')).toHaveStyle({ width: expectedWidth, minWidth: expectedWidth });
    expect(within(selectedRow).getByText('#12,345')).toBeTruthy();
  });

  it('does not duplicate a selected band when it is already in the top rows', async () => {
    localStorage.setItem('fst:selectedProfile', JSON.stringify({
      type: 'band',
      bandId: 'Band_Duets-1',
      bandType: 'Band_Duets',
      teamKey: 'Band_Duets-1-0:Band_Duets-1-1',
      displayName: 'Alpha + Beta',
      members: [
        { accountId: 'Band_Duets-1-0', displayName: 'Alpha' },
        { accountId: 'Band_Duets-1-1', displayName: 'Beta' },
      ],
    }));
    const topEntry = makeBandEntry(1, 'Band_Duets', ['Alpha', 'Beta']);
    mockApi.getBandRankings.mockImplementation((bandType: BandType, _comboId: string | undefined, rankBy: string, page: number, pageSize: number, _selectedAccountId?: string, selectedTeamKey?: string) => Promise.resolve({
      bandType,
      comboId: null,
      rankBy,
      page,
      pageSize,
      totalTeams: 42,
      entries: [bandType === 'Band_Duets' ? topEntry : makeBandEntry(1, bandType, ['Gamma', 'Delta'])],
      selectedBandEntry: bandType === 'Band_Duets' && selectedTeamKey === topEntry.teamKey ? topEntry : null,
    }));

    render(
      <TestProviders route="/leaderboards">
        <Routes>
          <Route path="/leaderboards" element={<LeaderboardsOverviewPage />} />
        </Routes>
      </TestProviders>,
    );

    await screen.findByTestId('band-ranking-entry-Band_Duets-0');
    expect(screen.queryByTestId('band-ranking-selected-entry-Band_Duets')).toBeNull();
    expect(screen.getAllByText('Alpha')).toHaveLength(1);
  });

  it('does not fetch or render band ranking cards when player bands are disabled', async () => {
    localStorage.setItem('fst:featureFlagOverrides', JSON.stringify({ playerBands: false }));

    render(
      <TestProviders route="/leaderboards">
        <Routes>
          <Route path="/leaderboards" element={<LeaderboardsOverviewPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => expect(mockApi.getRankings).toHaveBeenCalled());
    expect(mockApi.getBandRankings).not.toHaveBeenCalled();
    expect(screen.queryByTestId('band-ranking-card-Band_Duets')).toBeNull();
    expect(screen.queryByTestId('leaderboards-band-section-stack')).toBeNull();
  });
});