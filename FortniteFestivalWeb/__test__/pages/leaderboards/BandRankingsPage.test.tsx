import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import type { BandConfiguration, BandRankingEntry, BandType, ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { stubElementDimensions, stubMatchMedia, stubResizeObserver, stubScrollTo } from '../../helpers/browserStubs';
import { TestProviders } from '../../helpers/TestProviders';
import BandRankingsPage from '../../../src/pages/leaderboards/BandRankingsPage';
import { SELECTED_PROFILE_STORAGE_KEY, writeSelectedProfile } from '../../../src/state/selectedProfile';

const mockApi = vi.hoisted(() => ({
  getBandRankings: vi.fn(),
  getBandRankingCombos: vi.fn(),
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
  mockApi.getBandRankingCombos.mockResolvedValue({
    bandType: 'Band_Duets',
    combos: [
      { comboId: 'Solo_Guitar+Solo_Bass', instruments: ['Solo_Guitar', 'Solo_Bass'], teamCount: 12 },
      { comboId: 'Solo_Guitar+Solo_Vocals', instruments: ['Solo_Guitar', 'Solo_Vocals'], teamCount: 8 },
    ],
  });
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

function enableExperimentalRanks() {
  localStorage.setItem('fst:appSettings', JSON.stringify({ enableExperimentalRanks: true }));
}

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
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', undefined, 'totalscore', 1, 25, undefined, undefined);
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
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', undefined, 'totalscore', 2, 25, undefined, undefined);
    });
  });

  it('hides the desktop rank pill when experimental ranks are disabled', async () => {
    render(
      <TestProviders route="/leaderboards/bands/Band_Duets?rankBy=totalscore">
        <Routes>
          <Route path="/leaderboards/bands/:bandType" element={<BandRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await screen.findByTestId('band-rankings-card-list');

    expect(screen.getByRole('button', { name: 'Filter by Combo' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Total Score' })).toBeNull();
  });

  it('shows the desktop rank pill when experimental ranks are enabled', async () => {
    enableExperimentalRanks();

    render(
      <TestProviders route="/leaderboards/bands/Band_Duets?rankBy=totalscore">
        <Routes>
          <Route path="/leaderboards/bands/:bandType" element={<BandRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await screen.findByTestId('band-rankings-card-list');

    expect(screen.getByRole('button', { name: 'Filter by Combo' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Total Score' })).toBeTruthy();
  });

  it('coerces experimental rank query params to total score when experimental ranks are disabled', async () => {
    render(
      <TestProviders route="/leaderboards/bands/Band_Duets?rankBy=adjusted">
        <Routes>
          <Route path="/leaderboards/bands/:bandType" element={<BandRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', undefined, 'totalscore', 1, 25, undefined, undefined);
    });
  });

  it('uses compact Bayesian metadata for mobile adjusted band ranking cards', async () => {
    stubMatchMedia(true);
    enableExperimentalRanks();
    mockApi.getBandRankings.mockResolvedValue({
      bandType: 'Band_Duets',
      comboId: null,
      rankBy: 'adjusted',
      page: 1,
      pageSize: 25,
      totalTeams: 42,
      entries: [{
        ...makeBandEntry(1, ['Alpha', 'Beta']),
        rawSkillRating: 0.0056,
        adjustedSkillRating: 0.0409,
      }],
    });

    render(
      <TestProviders route="/leaderboards/bands/Band_Duets?rankBy=adjusted">
        <Routes>
          <Route path="/leaderboards/bands/:bandType" element={<BandRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    const row = await screen.findByTestId('band-rankings-entry-0');
    const metadata = within(row).getByTestId('band-ranking-metadata');
    const primaryRow = within(metadata).getByTestId('ranking-compact-primary-row');
    const primaryMetadata = within(metadata).getByTestId('ranking-compact-primary-metadata');
    const bayesianRow = within(metadata).getByTestId('ranking-compact-bayesian-row');
    expect(within(primaryMetadata).getByTestId('ranking-songs-label')).toHaveTextContent('120 / 160');
    expect(within(primaryMetadata).getByText('Top 0.56%')).toBeTruthy();
    expect(within(primaryRow).queryByText('Bayesian-Calculated Rank:')).toBeNull();
    expect(within(bayesianRow).getByText('Bayesian-Calculated Rank:')).toBeTruthy();
    expect(within(bayesianRow).getByText('0.0409')).toBeTruthy();
    expect(within(bayesianRow).getByText('0.0409').style.minWidth).toBe(within(primaryMetadata).getByText('Top 0.56%').style.minWidth);
    expect(primaryRow).toHaveStyle({ justifyContent: 'flex-end' });
    expect(primaryMetadata).toHaveStyle({ justifyContent: 'flex-end' });
    expect(bayesianRow).toHaveStyle({ justifyContent: 'flex-end' });
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
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', 'Solo_Guitar+Solo_Bass', 'totalscore', 1, 25, undefined, undefined);
    });
  });

  it('uses a route combo before the selected-band combo and preserves it during pagination', async () => {
    render(
      <TestProviders
        route="/leaderboards/bands/Band_Duets?rankBy=totalscore&combo=Solo_Guitar+Solo_Vocals"
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
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', 'Solo_Guitar+Solo_Vocals', 'totalscore', 1, 25, undefined, undefined);
    });
    expect(await screen.findByTestId('leaderboard-page-info')).toHaveTextContent('1 / 2');

    mockApi.getBandRankings.mockClear();
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', 'Solo_Guitar+Solo_Vocals', 'totalscore', 2, 25, undefined, undefined);
    });
  });

  it('uses combo=all to suppress the selected-band combo fallback', async () => {
    render(
      <TestProviders
        route="/leaderboards/bands/Band_Duets?rankBy=totalscore&combo=all"
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
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', undefined, 'totalscore', 1, 25, undefined, undefined);
    });
  });

  it('shows the page-local combo notice when the selected band type matches the viewed band type', async () => {
    writeSelectedProfile({
      type: 'band',
      bandId: 'selected-band',
      bandType: 'Band_Duets',
      teamKey: 'selected:partner',
      displayName: 'Selected + Partner',
      members: [
        { accountId: 'selected', displayName: 'Selected' },
        { accountId: 'partner', displayName: 'Partner' },
      ],
    });

    render(
      <TestProviders
        route="/leaderboards/bands/Band_Duets?rankBy=totalscore"
        bandFilter={{
          bandId: 'selected-band',
          bandType: 'Band_Duets',
          teamKey: 'selected:partner',
          comboId: 'Solo_Guitar+Solo_Bass',
          assignments: [
            { accountId: 'selected', instrument: 'Solo_Guitar' },
            { accountId: 'partner', instrument: 'Solo_Bass' },
          ],
        }}
      >
        <Routes>
          <Route path="/leaderboards/bands/:bandType" element={<BandRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await screen.findByTestId('band-rankings-card-list');
    fireEvent.click(screen.getByTestId('band-filter-pill'));

    expect(await screen.findByRole('dialog', { name: 'Filter Duos by Combo' })).toBeTruthy();
    expect(screen.getByText('Changing this page filter will not change the globally filtered combo for Selected + Partner.')).toBeTruthy();
  });

  it('hides the page-local combo notice when the selected band type does not match the viewed band type', async () => {
    writeSelectedProfile({
      type: 'band',
      bandId: 'selected-band',
      bandType: 'Band_Trios',
      teamKey: 'selected:partner:third',
      displayName: 'Selected Trio',
      members: [
        { accountId: 'selected', displayName: 'Selected' },
        { accountId: 'partner', displayName: 'Partner' },
        { accountId: 'third', displayName: 'Third' },
      ],
    });

    render(
      <TestProviders
        route="/leaderboards/bands/Band_Duets?rankBy=totalscore"
        bandFilter={{
          bandId: 'selected-band',
          bandType: 'Band_Trios',
          teamKey: 'selected:partner:third',
          comboId: 'Solo_Guitar+Solo_Bass+Solo_Drums',
          assignments: [
            { accountId: 'selected', instrument: 'Solo_Guitar' },
            { accountId: 'partner', instrument: 'Solo_Bass' },
            { accountId: 'third', instrument: 'Solo_Drums' },
          ],
        }}
      >
        <Routes>
          <Route path="/leaderboards/bands/:bandType" element={<BandRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await screen.findByTestId('band-rankings-card-list');
    fireEvent.click(screen.getByTestId('band-filter-pill'));

    expect(await screen.findByRole('dialog', { name: 'Filter Duos by Combo' })).toBeTruthy();
    expect(screen.queryByText(/Changing this page filter will not/)).toBeNull();
  });

  it('uses the band instrument selector to apply an arbitrary page-local combo', async () => {
    render(
      <TestProviders route="/leaderboards/bands/Band_Duets?rankBy=totalscore">
        <Routes>
          <Route path="/leaderboards/bands/:bandType" element={<BandRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await screen.findByTestId('band-rankings-card-list');
    fireEvent.click(screen.getByTestId('band-filter-pill'));

    const dialog = await screen.findByRole('dialog', { name: 'Filter Duos by Combo' });
    expect(within(dialog).getByText('Instrument #1')).toBeTruthy();
    expect(within(dialog).getByText('Instrument #2')).toBeTruthy();

    const leadButtons = within(dialog).getAllByTitle('Lead');
    fireEvent.click(leadButtons[0]!);
    expect(within(dialog).getByRole('button', { name: 'Apply' })).toBeDisabled();
    fireEvent.click(leadButtons[1]!);

    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Apply' })).not.toBeDisabled());
    mockApi.getBandRankings.mockClear();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Apply' }));

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', 'Solo_Guitar+Solo_Guitar', 'totalscore', 1, 25, undefined, undefined);
    });
  });

  it('requests and renders the selected band entry when it is outside the current page', async () => {
    localStorage.setItem(SELECTED_PROFILE_STORAGE_KEY, JSON.stringify({
      type: 'band',
      bandId: 'selected-band',
      bandType: 'Band_Duets',
      teamKey: 'selected:partner',
      displayName: 'Selected + Partner',
      members: [
        { accountId: 'selected', displayName: 'Selected' },
        { accountId: 'partner', displayName: 'Partner' },
      ],
    }));
    mockApi.getBandRankings.mockResolvedValue({
      bandType: 'Band_Duets',
      comboId: null,
      rankBy: 'totalscore',
      page: 1,
      pageSize: 25,
      totalTeams: 42,
      entries: [makeBandEntry(1, ['Alpha', 'Beta'])],
      selectedBandEntry: makeBandEntry(13, ['Selected', 'Partner']),
      selectedPlayerEntry: makeBandEntry(14, ['Tracked Player', 'Other Partner']),
    });

    render(
      <TestProviders route="/leaderboards/bands/Band_Duets?rankBy=totalscore">
        <Routes>
          <Route path="/leaderboards/bands/:bandType" element={<BandRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', undefined, 'totalscore', 1, 25, undefined, 'selected:partner');
    });

    const list = await screen.findByTestId('band-rankings-card-list');
    expect(within(list).queryByText('Selected')).toBeNull();
    const footer = await screen.findByTestId('leaderboard-fixed-player-footer');
    expect(footer).toHaveStyle({ position: 'fixed' });
    expect(within(footer).getByText('Selected + Partner')).toBeTruthy();
    expect(within(footer).getByText('#13')).toBeTruthy();
    expect(within(footer).getByRole('link')).toHaveAttribute('href', '/statistics');
    expect(within(footer).queryByText('Tracked Player + Other Partner')).toBeNull();
  });

  it('renders the selected band footer for the active selected-band combo filter', async () => {
    localStorage.setItem(SELECTED_PROFILE_STORAGE_KEY, JSON.stringify({
      type: 'band',
      bandId: 'selected-band',
      bandType: 'Band_Duets',
      teamKey: 'selected:partner',
      displayName: 'Selected + Partner',
      members: [
        { accountId: 'selected', displayName: 'Selected' },
        { accountId: 'partner', displayName: 'Partner' },
      ],
    }));
    mockApi.getBandRankings.mockResolvedValue({
      bandType: 'Band_Duets',
      comboId: 'Solo_Guitar+Solo_Bass',
      rankBy: 'totalscore',
      page: 1,
      pageSize: 25,
      totalTeams: 42,
      entries: [makeBandEntry(1, ['Alpha', 'Beta'])],
      selectedBandEntry: makeBandEntry(13, ['Selected', 'Partner']),
    });

    render(
      <TestProviders
        route="/leaderboards/bands/Band_Duets?rankBy=totalscore"
        bandFilter={{
          bandId: 'selected-band',
          bandType: 'Band_Duets',
          teamKey: 'selected:partner',
          comboId: 'Solo_Guitar+Solo_Bass',
          assignments: [
            { accountId: 'selected', instrument: 'Solo_Guitar' },
            { accountId: 'partner', instrument: 'Solo_Bass' },
          ],
        }}
      >
        <Routes>
          <Route path="/leaderboards/bands/:bandType" element={<BandRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', 'Solo_Guitar+Solo_Bass', 'totalscore', 1, 25, undefined, 'selected:partner');
    });
    expect(await screen.findByTestId('band-rankings-card-list')).toBeTruthy();
    const footer = await screen.findByTestId('leaderboard-fixed-player-footer');
    expect(within(footer).getByText('Selected + Partner')).toBeTruthy();
    expect(within(footer).getByText('#13')).toBeTruthy();
  });

  it('requests and renders the selected player best band in the fixed footer', async () => {
    writeSelectedProfile({
      type: 'player',
      accountId: 'tracked-player',
      displayName: 'Tracked Player',
    });
    mockApi.getBandRankings.mockResolvedValue({
      bandType: 'Band_Duets',
      comboId: null,
      rankBy: 'totalscore',
      page: 1,
      pageSize: 25,
      totalTeams: 42,
      entries: [makeBandEntry(1, ['Alpha', 'Beta'])],
      selectedPlayerEntry: makeBandEntry(13, ['Tracked Player', 'Partner'], undefined),
    });

    render(
      <TestProviders route="/leaderboards/bands/Band_Duets?rankBy=totalscore">
        <Routes>
          <Route path="/leaderboards/bands/:bandType" element={<BandRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Duets', undefined, 'totalscore', 1, 25, 'tracked-player', undefined);
    });

    const list = await screen.findByTestId('band-rankings-card-list');
    expect(within(list).queryByText('Tracked Player')).toBeNull();
    const footer = await screen.findByTestId('leaderboard-fixed-player-footer');
    expect(footer).toHaveStyle({ position: 'fixed' });
    expect(within(footer).getByText('Tracked Player + Partner')).toBeTruthy();
    expect(within(footer).getByText('#13')).toBeTruthy();
    expect(within(footer).getByRole('link')).toHaveAttribute('href', '/bands/band-13?accountId=tracked-player&bandType=Band_Duets&teamKey=tracked%20player%3Apartner&names=Tracked%20Player%20%2B%20Partner');
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

  it('renders only matching instruments for a filtered Trio ranking card', async () => {
    const configurations: BandConfiguration[] = [
      {
        rawInstrumentCombo: '0:1:3',
        comboId: 'Solo_Guitar+Solo_Bass+Solo_Drums',
        instruments: ['Solo_Guitar', 'Solo_Bass', 'Solo_Drums'],
        assignmentKey: 'acct-1-0=Solo_Guitar|acct-1-1=Solo_Bass|acct-1-2=Solo_Drums',
        appearanceCount: 3,
        memberInstruments: {
          'acct-1-0': 'Solo_Guitar',
          'acct-1-1': 'Solo_Bass',
          'acct-1-2': 'Solo_Drums',
        },
      },
      {
        rawInstrumentCombo: '0:2:3',
        comboId: 'Solo_Guitar+Solo_Drums+Solo_Vocals',
        instruments: ['Solo_Guitar', 'Solo_Drums', 'Solo_Vocals'],
        assignmentKey: 'acct-1-0=Solo_Vocals|acct-1-1=Solo_Guitar|acct-1-2=Solo_Drums',
        appearanceCount: 1,
        memberInstruments: {
          'acct-1-0': 'Solo_Vocals',
          'acct-1-1': 'Solo_Guitar',
          'acct-1-2': 'Solo_Drums',
        },
      },
    ];
    mockApi.getBandRankings.mockResolvedValue({
      bandType: 'Band_Trios',
      comboId: 'Solo_Guitar+Solo_Bass+Solo_Drums',
      rankBy: 'totalscore',
      page: 1,
      pageSize: 25,
      totalTeams: 42,
      entries: [{
        ...makeBandEntry(1, ['Alpha', 'Beta', 'Gamma'], configurations),
        members: [
          { accountId: 'acct-1-0', displayName: 'Alpha', instruments: ['Solo_Guitar', 'Solo_Vocals'] },
          { accountId: 'acct-1-1', displayName: 'Beta', instruments: ['Solo_Bass', 'Solo_Drums'] },
          { accountId: 'acct-1-2', displayName: 'Gamma', instruments: ['Solo_Drums', 'Solo_Vocals'] },
        ],
      }],
    });

    render(
      <TestProviders route="/leaderboards/bands/Band_Trios?rankBy=totalscore&combo=Solo_Guitar+Solo_Bass+Solo_Drums">
        <Routes>
          <Route path="/leaderboards/bands/:bandType" element={<BandRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalledWith('Band_Trios', 'Solo_Guitar+Solo_Bass+Solo_Drums', 'totalscore', 1, 25, undefined, undefined);
    });

    const row = await screen.findByTestId('band-rankings-entry-0');
    const rows = within(row).getAllByTestId('band-member-row');
    expect(within(rows[0]!).getByAltText('Solo_Guitar')).toBeTruthy();
    expect(within(rows[0]!).queryByAltText('Solo_Vocals')).toBeNull();
    expect(within(rows[1]!).getByAltText('Solo_Bass')).toBeTruthy();
    expect(within(rows[1]!).queryByAltText('Solo_Drums')).toBeNull();
    expect(within(rows[2]!).getByAltText('Solo_Drums')).toBeTruthy();
    expect(within(rows[2]!).queryByAltText('Solo_Vocals')).toBeNull();
  });
});
