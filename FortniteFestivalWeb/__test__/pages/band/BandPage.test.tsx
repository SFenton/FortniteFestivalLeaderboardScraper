import { describe, it, expect, vi, beforeAll, beforeEach, afterEach } from 'vitest';
import { render, screen, act, fireEvent, waitFor } from '@testing-library/react';
import { Route, Routes, useLocation } from 'react-router-dom';
import type { QueryClient } from '@tanstack/react-query';
import { ACCURACY_SCALE } from '@festival/core';
import { Colors, IconSize, Layout } from '@festival/theme';
import { DEFAULT_INSTRUMENT, type BandDetailResponse } from '@festival/core/api/serverTypes';
import { queryKeys } from '../../../src/api/queryKeys';
import { createTestQueryClient, TestProviders } from '../../helpers/TestProviders';
import { stubElementDimensions, stubResizeObserver, stubScrollTo } from '../../helpers/browserStubs';
import type { AppliedBandComboFilter } from '../../../src/types/bandFilter';
import type { SelectedBandProfile } from '../../../src/hooks/data/useSelectedProfile';

const mockApi = vi.hoisted(() => ({
  getSongs: vi.fn(),
  getBandDetail: vi.fn(),
  getPlayerBandsByType: vi.fn(),
  getBandRanking: vi.fn(),
  getBandRankHistory: vi.fn(),
  getBandSongs: vi.fn(),
}));
const mockUseIsMobile = vi.hoisted(() => vi.fn(() => false));

vi.mock('../../../src/api/client', () => ({ api: mockApi }));
vi.mock('../../../src/hooks/ui/useIsMobile', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../src/hooks/ui/useIsMobile')>();
  return { ...actual, useIsMobile: mockUseIsMobile };
});

const SELECTED_PROFILE_STORAGE_KEY = 'fst:selectedProfile';
const SELECT_BAND_PROFILE_SHADOW_GUTTER = 20;
const DEFAULT_CONFIGURATIONS = [
  {
    rawInstrumentCombo: '0:1',
    comboId: 'Solo_Guitar+Solo_Bass',
    instruments: ['Solo_Guitar', 'Solo_Bass'],
    assignmentKey: 'p1=Solo_Guitar|p2=Solo_Bass',
    appearanceCount: 2,
    memberInstruments: { p1: 'Solo_Guitar', p2: 'Solo_Bass' },
  },
];
const DUET_COMBO_FILTER: AppliedBandComboFilter = {
  bandId: 'selected-band-guid',
  bandType: 'Band_Duets',
  teamKey: 'selected-a:selected-b',
  comboId: 'Solo_Guitar+Solo_Bass',
  assignments: [
    { accountId: 'selected-a', instrument: 'Solo_Guitar' },
    { accountId: 'selected-b', instrument: 'Solo_Bass' },
  ],
  configurations: DEFAULT_CONFIGURATIONS,
};

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver({ width: 1024, height: 800 });
  stubElementDimensions(800);
  const createRange = document.createRange.bind(document);
  document.createRange = () => {
    const range = createRange();
    range.getBoundingClientRect = () => ({
      top: 0,
      left: 0,
      bottom: 20,
      right: 120,
      width: 120,
      height: 20,
      x: 0,
      y: 0,
      toJSON() { return this; },
    });
    return range;
  };
});

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  localStorage.clear();
  vi.clearAllMocks();
  mockUseIsMobile.mockReturnValue(false);
  mockApi.getSongs.mockResolvedValue({
    count: 6,
    currentSeason: 1,
    songs: [
      { songId: 'song_1', title: 'Song Alpha', artist: 'Artist A', year: 2024 },
      { songId: 'song_2', title: 'Song Beta', artist: 'Artist B', year: 2024 },
      { songId: 'song_3', title: 'Song Gamma', artist: 'Artist C', year: 2024 },
      { songId: 'song_4', title: 'Song Delta', artist: 'Artist D', year: 2024 },
      { songId: 'song_5', title: 'Song Epsilon', artist: 'Artist E', year: 2024 },
      { songId: 'song_6', title: 'Song Zeta', artist: 'Artist Z', year: 2024 },
    ],
  });
  mockApi.getBandDetail.mockResolvedValue({
    band: {
      bandId: 'band-guid-1',
      teamKey: 'p1:p2',
      bandType: 'Band_Duets',
      appearanceCount: 2,
      members: [
        { accountId: 'p1', displayName: 'Player One', instruments: ['Solo_Guitar'] },
        { accountId: 'p2', displayName: 'Player Two', instruments: ['Solo_Bass'] },
      ],
    },
    ranking: {
      bandId: 'band-guid-1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      teamMembers: [
        { accountId: 'p1', displayName: 'Player One' },
        { accountId: 'p2', displayName: 'Player Two' },
      ],
      songsPlayed: 2,
      totalChartedSongs: 10,
      coverage: 0.2,
      rawSkillRating: 12,
      adjustedSkillRating: 10,
      adjustedSkillRank: 7,
      weightedRating: 9,
      weightedRank: 8,
      fcRate: 0.5,
      fcRateRank: 9,
      totalScore: 123456,
      totalScoreRank: 10,
      avgAccuracy: 98.5 * ACCURACY_SCALE,
      fullComboCount: 1,
      avgStars: 5.5,
      bestRank: 1,
      avgRank: 4.5,
      rawWeightedRating: 9,
      computedAt: '2026-04-24T00:00:00Z',
      totalRankedTeams: 50,
      configurations: DEFAULT_CONFIGURATIONS,
    },
    configurations: DEFAULT_CONFIGURATIONS,
  });
  mockApi.getPlayerBandsByType.mockResolvedValue({
    accountId: 'p1',
    bandType: 'Band_Duets',
    totalCount: 1,
    entries: [{
      bandId: 'band-guid-1',
      teamKey: 'p1:p2',
      bandType: 'Band_Duets',
      appearanceCount: 2,
      members: [
        { accountId: 'p1', displayName: 'Player One', instruments: ['Solo_Guitar'] },
        { accountId: 'p2', displayName: 'Player Two', instruments: ['Solo_Bass'] },
      ],
    }],
  });
  mockApi.getBandRanking.mockResolvedValue({
    bandId: 'band-guid-1',
    bandType: 'Band_Duets',
    teamKey: 'p1:p2',
    teamMembers: [
      { accountId: 'p1', displayName: 'Player One' },
      { accountId: 'p2', displayName: 'Player Two' },
    ],
    members: [
      { accountId: 'p1', displayName: 'Player One', instruments: ['Solo_Guitar'] },
      { accountId: 'p2', displayName: 'Player Two', instruments: ['Solo_Bass'] },
    ],
    songsPlayed: 2,
    totalChartedSongs: 10,
    coverage: 0.2,
    rawSkillRating: 12,
    adjustedSkillRating: 10,
    adjustedSkillRank: 7,
    weightedRating: 9,
    weightedRank: 8,
    fcRate: 0.5,
    fcRateRank: 9,
    totalScore: 123456,
    totalScoreRank: 10,
    avgAccuracy: 98.5 * ACCURACY_SCALE,
    fullComboCount: 1,
    avgStars: 5.5,
    bestRank: 1,
    avgRank: 4.5,
    rawWeightedRating: 9,
    computedAt: '2026-04-24T00:00:00Z',
    totalRankedTeams: 50,
    configurations: DEFAULT_CONFIGURATIONS,
  });
  mockApi.getBandRankHistory.mockResolvedValue({
    bandType: 'Band_Duets',
    teamKey: 'p1:p2',
    comboId: null,
    days: 30,
    history: [
      {
        snapshotDate: '2026-04-23',
        snapshotTakenAt: '2026-04-23T00:00:00Z',
        adjustedSkillRank: 8,
        weightedRank: 9,
        fcRateRank: 10,
        totalScoreRank: 11,
        adjustedSkillRating: 11,
        weightedRating: 10,
        fcRate: 0.4,
        totalScore: 100000,
        songsPlayed: 1,
        coverage: 0.1,
        fullComboCount: 0,
        totalChartedSongs: 10,
        totalRankedTeams: 50,
        rawWeightedRating: 10,
        rawSkillRating: 11,
      },
      {
        snapshotDate: '2026-04-24',
        snapshotTakenAt: '2026-04-24T00:00:00Z',
        adjustedSkillRank: 7,
        weightedRank: 8,
        fcRateRank: 9,
        totalScoreRank: 10,
        adjustedSkillRating: 10,
        weightedRating: 9,
        fcRate: 0.5,
        totalScore: 123456,
        songsPlayed: 2,
        coverage: 0.2,
        fullComboCount: 1,
        totalChartedSongs: 10,
        totalRankedTeams: 50,
        rawWeightedRating: 9,
        rawSkillRating: 10,
      },
    ],
  });
  mockApi.getBandSongs.mockResolvedValue({
    bandType: 'Band_Duets',
    teamKey: 'p1:p2',
    comboId: null,
    limit: 5,
    best: [
      { songId: 'song_1', rank: 1, totalEntries: 100, percentile: 1, score: 100000 },
      { songId: 'song_2', rank: 2, totalEntries: 100, percentile: 2, score: 99000 },
      { songId: 'song_3', rank: 3, totalEntries: 100, percentile: 3, score: 98000 },
      { songId: 'song_4', rank: 4, totalEntries: 100, percentile: 4, score: 97000 },
      { songId: 'song_5', rank: 5, totalEntries: 100, percentile: 5, score: 96000 },
    ],
    worst: [
      { songId: 'song_6', rank: 80, totalEntries: 100, percentile: 80, score: 50000 },
    ],
  });
});

afterEach(() => {
  vi.useRealTimers();
});

const { default: BandPage } = await import('../../../src/pages/band/BandPage');

function LocationProbe() {
  const location = useLocation();
  const state = location.state as { preserveShellScrollKey?: string } | null;
  return (
    <>
      <div data-testid="current-location">{`${location.pathname}${location.search}`}</div>
      <div data-testid="location-preserve-scroll">{state?.preserveShellScrollKey ? 'true' : 'false'}</div>
    </>
  );
}

function renderBandPage(route: string, queryClient: QueryClient = createTestQueryClient(), bandFilter?: AppliedBandComboFilter | null, statisticsBand?: SelectedBandProfile | null) {
  return render(
    <TestProviders route={route} queryClient={queryClient} bandFilter={bandFilter}>
      <LocationProbe />
      <Routes>
        <Route path="/statistics" element={<BandPage statisticsBand={statisticsBand} />} />
        <Route path="/bands" element={<BandPage />} />
        <Route path="/bands/:bandId" element={<BandPage />} />
      </Routes>
    </TestProviders>,
  );
}

async function advancePastSpinner() {
  await act(async () => { await Promise.resolve(); });
  await act(async () => { await vi.advanceTimersByTimeAsync(600); });
  await act(async () => { await Promise.resolve(); });
  await act(async () => { await vi.advanceTimersByTimeAsync(600); });
}

describe('BandPage', () => {
  it('renders selected band statistics on /statistics without canonicalizing to band detail', async () => {
    const selectedBand: SelectedBandProfile = {
      type: 'band',
      bandId: 'band-guid-1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      displayName: 'Player One + Player Two',
      members: [
        { accountId: 'p1', displayName: 'Player One' },
        { accountId: 'p2', displayName: 'Player Two' },
      ],
    };

    renderBandPage('/statistics', createTestQueryClient(), null, selectedBand);
    await advancePastSpinner();

    expect(screen.getByTestId('current-location')).toHaveTextContent('/statistics');
    expect(mockApi.getBandRanking).toHaveBeenCalledWith('Band_Duets', 'p1:p2');
    expect(mockApi.getPlayerBandsByType).not.toHaveBeenCalled();
    expect(mockApi.getBandDetail).not.toHaveBeenCalled();
    expect(await screen.findByText('Player One + Player Two')).toBeTruthy();
    const statisticsSection = screen.getByTestId('band-section-statistics');
    expect(statisticsSection).toHaveTextContent('2 / 10');
    expect(statisticsSection).toHaveTextContent('#7');
  });

  it('renders band details from a direct band id route', async () => {
    const { container } = renderBandPage('/bands/band-guid-1');
    await advancePastSpinner();

    expect(mockApi.getBandDetail).toHaveBeenCalledWith('band-guid-1');
    expect(await screen.findByText('Player One + Player Two')).toBeTruthy();
    expect(screen.getByText('Band Summary')).toBeTruthy();
    expect(screen.getByText('Band Statistics')).toBeTruthy();
    expect(screen.getByText('Band Rank History')).toBeTruthy();
    expect(screen.getByText('Five Best Songs')).toBeTruthy();
    expect(screen.getByText('Five Worst Songs')).toBeTruthy();
    expect(screen.getAllByText('#7').length).toBeGreaterThan(0);
    expect(mockApi.getBandRankHistory).toHaveBeenCalledWith('Band_Duets', 'p1:p2', 30, undefined);
    expect(mockApi.getBandSongs).toHaveBeenCalledWith('Band_Duets', 'p1:p2', 5, undefined);

    expect(Array.from(container.querySelectorAll('h2')).map(heading => heading.textContent)).toEqual([
      'Members',
      'Band Summary',
      'Band Statistics',
      'Five Best Songs',
      'Five Worst Songs',
    ]);

    const memberCards = screen.getAllByTestId('band-member-card');
    expect(memberCards).toHaveLength(2);
    expect(screen.getAllByTestId('band-member-chevron')).toHaveLength(2);
    expect(screen.getByTestId('band-section-members').querySelector('[style*="grid-template-columns"]')).toHaveStyle({ gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' });
    expect(memberCards[0]).toHaveTextContent('Player One');
    expect(memberCards[0]).not.toHaveTextContent('p1');
    expect(memberCards[0]).toHaveAttribute('href', '/player/p1');
    expect(memberCards[1]).toHaveTextContent('Player Two');
    expect(memberCards[1]).not.toHaveTextContent('p2');

    const summarySection = screen.getByTestId('band-section-summary');
    expect(summarySection).toHaveTextContent('Duos');
    expect(summarySection).toHaveTextContent('Appearances');
    expect(summarySection).toHaveTextContent('Members');
    expect(summarySection).not.toHaveTextContent('p1:p2');
    expect(summarySection.querySelector('[style*="grid-template-columns"]')).toHaveStyle({ gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' });

    const statisticsSection = screen.getByTestId('band-section-statistics');
    expect(statisticsSection.querySelector('[style*="grid-template-columns"]')).toHaveStyle({ gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' });

    expect(screen.getAllByTestId('band-stat-card')).toHaveLength(15);
    expect(screen.getByText('Adjusted Percentile Rank')).toBeTruthy();
    expect(screen.getByText('Weighted Percentile Rank')).toBeTruthy();
    expect(screen.getByText('FC Rate Rank')).toBeTruthy();
    expect(screen.getByText('Total Score Rank')).toBeTruthy();
    expect(screen.getByText('Songs Played')).toBeTruthy();
    expect(screen.getByText('2 / 10')).toBeTruthy();
    expect(screen.getByText('Full Combos')).toBeTruthy();
    expect(screen.getByText('1 / 10')).toBeTruthy();
    expect(screen.getByText('98.5%')).toBeTruthy();
    expect(screen.getByText('Avg Stars')).toBeTruthy();
    expect(screen.getByText('5.5')).toBeTruthy();
    expect(screen.getByText('Best Song Rank')).toBeTruthy();
    expect(screen.getByText('Avg Rank')).toBeTruthy();
    expect(screen.getByText('#4.5')).toBeTruthy();
    expect(screen.getByText('Song Alpha')).toBeTruthy();
    expect(screen.getByText('Song Zeta')).toBeTruthy();
  });

  it.each([
    ['Adjusted Percentile Rank', '/leaderboards/bands/Band_Duets?rankBy=adjusted&page=1'],
    ['Weighted Percentile Rank', '/leaderboards/bands/Band_Duets?rankBy=weighted&page=1'],
    ['FC Rate Rank', '/leaderboards/bands/Band_Duets?rankBy=fcrate&page=1'],
    ['Total Score Rank', '/leaderboards/bands/Band_Duets?rankBy=totalscore&page=1'],
  ])('navigates from the %s card to the matching band leaderboard', async (label, expectedRoute) => {
    renderBandPage('/bands/band-guid-1');
    await advancePastSpinner();

    fireEvent.click(screen.getByText(label));

    expect(screen.getByTestId('current-location')).toHaveTextContent(expectedRoute);
  });

  it('selects the band and filters Songs when Songs Played is clicked', async () => {
    renderBandPage('/bands/band-guid-1');
    await advancePastSpinner();

    fireEvent.click(screen.getByText('Songs Played'));

    expect(screen.getByTestId('current-location')).toHaveTextContent('/songs');
    expect(JSON.parse(localStorage.getItem(SELECTED_PROFILE_STORAGE_KEY)!)).toMatchObject({
      type: 'band',
      bandId: 'band-guid-1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
    });
    expect(JSON.parse(localStorage.getItem('fst:songSettings')!)).toMatchObject({
      instrument: null,
      sortMode: 'title',
      sortAscending: true,
      filters: { hasScores: { [DEFAULT_INSTRUMENT]: true } },
    });
  });

  it('selects the band and filters Songs when Full Combos is clicked', async () => {
    renderBandPage('/bands/band-guid-1');
    await advancePastSpinner();

    fireEvent.click(screen.getByText('Full Combos'));

    expect(screen.getByTestId('current-location')).toHaveTextContent('/songs');
    expect(JSON.parse(localStorage.getItem('fst:songSettings')!)).toMatchObject({
      instrument: null,
      sortMode: 'title',
      sortAscending: true,
      filters: { hasFCs: { [DEFAULT_INSTRUMENT]: true } },
    });
  });

  it('does not select a band just because the band page loads', async () => {
    renderBandPage('/bands/band-guid-1');
    await advancePastSpinner();

    expect(screen.getByRole('button', { name: 'Select Band Profile' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Select Band Profile' })).toHaveStyle({ backgroundColor: Colors.accentPurple });
    expect(screen.getByTestId('band-select-profile-slot')).toHaveStyle({
      maxWidth: `${360 + (SELECT_BAND_PROFILE_SHADOW_GUTTER * 2)}px`,
      overflow: 'hidden',
      padding: `${SELECT_BAND_PROFILE_SHADOW_GUTTER}px`,
      margin: `-${SELECT_BAND_PROFILE_SHADOW_GUTTER}px`,
      opacity: '1',
    });
    expect(localStorage.getItem(SELECTED_PROFILE_STORAGE_KEY)).toBeNull();
  });

  it('renders Select Band Profile on mobile as a compact pulsing two-person circle', async () => {
    mockUseIsMobile.mockReturnValue(true);

    renderBandPage('/bands/band-guid-1');
    await advancePastSpinner();

    const selectButton = screen.getByRole('button', { name: 'Select Band Profile' });
    const selectButtonStyle = selectButton.getAttribute('style') ?? '';
    expect(selectButtonStyle).toContain(`width: ${Layout.pillButtonHeight}px`);
    expect(selectButtonStyle).toContain(`height: ${Layout.pillButtonHeight}px`);
    expect(selectButtonStyle).toContain('border-radius: 999px');
    expect(selectButton.className).toContain('profileCircleBreathe');

    const selectIcon = selectButton.querySelector('svg');
    expect(selectIcon).not.toBeNull();
    expect(selectIcon).toHaveAttribute('height', `${IconSize.action}`);
    expect(selectIcon).toHaveAttribute('width', `${IconSize.action}`);

    expect(screen.getByTestId('band-select-profile-slot')).toHaveStyle({
      maxWidth: `${Layout.pillButtonHeight}px`,
      opacity: '1',
    });
  });

  it('selects the current band with member summaries when Select Band Profile is clicked', async () => {
    renderBandPage('/bands/band-guid-1');
    await advancePastSpinner();

    fireEvent.click(screen.getByRole('button', { name: 'Select Band Profile' }));

    expect(JSON.parse(localStorage.getItem(SELECTED_PROFILE_STORAGE_KEY)!)).toEqual({
      type: 'band',
      bandId: 'band-guid-1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      displayName: 'Player One + Player Two',
      members: [
        { accountId: 'p1', displayName: 'Player One' },
        { accountId: 'p2', displayName: 'Player Two' },
      ],
    });
    await waitFor(() => {
      expect(screen.getByTestId('current-location')).toHaveTextContent('/statistics');
    });
    expect(screen.getByTestId('location-preserve-scroll')).toHaveTextContent('true');
  });

  it('does not show an on-page deselect action for the selected band', async () => {
    localStorage.setItem(SELECTED_PROFILE_STORAGE_KEY, JSON.stringify({
      type: 'band',
      bandId: 'band-guid-1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      displayName: 'Player One + Player Two',
      members: [
        { accountId: 'p1', displayName: 'Player One' },
        { accountId: 'p2', displayName: 'Player Two' },
      ],
    }));

    renderBandPage('/bands/band-guid-1');
    await advancePastSpinner();

    expect(screen.queryByRole('button', { name: 'Deselect Band' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Select Band Profile' })).toBeNull();
    expect(JSON.parse(localStorage.getItem(SELECTED_PROFILE_STORAGE_KEY)!)).toMatchObject({
      type: 'band',
      bandId: 'band-guid-1',
      teamKey: 'p1:p2',
    });
  });

  it('quietly rewrites to statistics after selecting the current band profile', async () => {
    renderBandPage('/bands/band-guid-1');
    await advancePastSpinner();

    fireEvent.click(screen.getByRole('button', { name: 'Select Band Profile' }));

    await waitFor(() => {
      expect(screen.getByTestId('current-location')).toHaveTextContent('/statistics');
    });
    expect(screen.getByTestId('location-preserve-scroll')).toHaveTextContent('true');
  });

  it('uses friendly fallback text instead of account ids when member names are missing', async () => {
    mockApi.getBandDetail.mockResolvedValueOnce({
      band: {
        bandId: 'band-guid-1',
        teamKey: 'account-one-guid:account-two-guid',
        bandType: 'Band_Duets',
        appearanceCount: 1,
        members: [
          { accountId: 'account-one-guid', displayName: '', instruments: ['Solo_Guitar'] },
          { accountId: 'account-two-guid', displayName: '   ', instruments: ['Solo_Bass'] },
        ],
      },
      ranking: null,
    });

    renderBandPage('/bands/band-guid-1');
    await advancePastSpinner();

    const memberCards = screen.getAllByTestId('band-member-card');
    expect(memberCards).toHaveLength(2);
    expect(memberCards[0]).toHaveTextContent('Unknown User');
    expect(memberCards[0]).not.toHaveTextContent('account-one-guid');
    expect(memberCards[1]).toHaveTextContent('Unknown User');
    expect(memberCards[1]).not.toHaveTextContent('account-two-guid');
    expect(screen.queryByText('account-one-guid:account-two-guid')).toBeNull();
    expect(screen.getByText('No ranking calculated yet.')).toBeTruthy();
  });

  it('shows friendly names from the URL while band detail is still loading', async () => {
    mockApi.getBandDetail.mockReturnValue(new Promise(() => {}));

    const { container } = renderBandPage('/bands/band-guid-1?names=Friendly%20One%20%2B%20Friendly%20Two');
    await act(async () => { await Promise.resolve(); });

    expect(screen.getByText('Friendly One + Friendly Two')).toBeTruthy();
    expect(container.querySelector('[style*="visibility: hidden"]')).toBeTruthy();
    expect(mockApi.getBandDetail).toHaveBeenCalledWith('band-guid-1');
    expect(screen.queryByText('Band Summary')).toBeNull();
  });

  it('falls back to the generic band title while loading without friendly names', async () => {
    mockApi.getBandDetail.mockReturnValue(new Promise(() => {}));

    renderBandPage('/bands/band-guid-1');
    await act(async () => { await Promise.resolve(); });

    expect(screen.getByText('Band')).toBeTruthy();
    expect(screen.queryByText('Player One + Player Two')).toBeNull();
  });

  it('adds resolved friendly names to direct band URLs when missing', async () => {
    renderBandPage('/bands/band-guid-1');
    await advancePastSpinner();

    expect(await screen.findByText('Player One + Player Two')).toBeTruthy();
    expect(screen.getByTestId('current-location')).toHaveTextContent('/bands/band-guid-1?names=Player+One+%2B+Player+Two');
  });

  it('resolves older lookup links before fetching band details', async () => {
    renderBandPage('/bands?accountId=p1&bandType=Band_Duets&teamKey=p1%3Ap2&names=Player%20One%20%2B%20Player%20Two');
    await advancePastSpinner();

    expect(mockApi.getPlayerBandsByType).toHaveBeenCalledWith('p1', 'Band_Duets');
    expect(mockApi.getBandRanking).toHaveBeenCalledWith('Band_Duets', 'p1:p2');
    expect(mockApi.getBandDetail).not.toHaveBeenCalled();
    expect(await screen.findByText('Player One + Player Two')).toBeTruthy();
    expect(screen.getByTestId('current-location')).toHaveTextContent('/bands/band-guid-1?accountId=p1&bandType=Band_Duets&teamKey=p1%3Ap2&names=Player%20One%20%2B%20Player%20Two');
  });

  it('loads band detail pages from team context without requiring an account id', async () => {
    renderBandPage('/bands/band-guid-1?bandType=Band_Duets&teamKey=p1%3Ap2&names=Player%20One%20%2B%20Player%20Two');
    await advancePastSpinner();

    expect(mockApi.getBandRanking).toHaveBeenCalledWith('Band_Duets', 'p1:p2');
    expect(mockApi.getPlayerBandsByType).not.toHaveBeenCalled();
    expect(mockApi.getBandDetail).not.toHaveBeenCalled();
    expect(await screen.findByText('Player One + Player Two')).toBeTruthy();
    expect(screen.getByText('Band Summary')).toBeTruthy();
    expect(screen.getByText('2 / 10')).toBeTruthy();
  });

  it('applies a same-type combo filter to another viewed band page', async () => {
    const identityRanking = {
      bandId: 'band-guid-2',
      bandType: 'Band_Duets',
      teamKey: 'p3:p4',
      teamMembers: [
        { accountId: 'p3', displayName: 'Player Three' },
        { accountId: 'p4', displayName: 'Player Four' },
      ],
      members: [
        { accountId: 'p3', displayName: 'Player Three', instruments: ['Solo_Guitar'] },
        { accountId: 'p4', displayName: 'Player Four', instruments: ['Solo_Bass'] },
      ],
      songsPlayed: 9,
      totalChartedSongs: 10,
      coverage: 0.9,
      rawSkillRating: 12,
      adjustedSkillRating: 10,
      adjustedSkillRank: 6,
      weightedRating: 9,
      weightedRank: 7,
      fcRate: 0.5,
      fcRateRank: 8,
      totalScore: 987654,
      totalScoreRank: 9,
      avgAccuracy: 99 * ACCURACY_SCALE,
      fullComboCount: 5,
      avgStars: 5.8,
      bestRank: 2,
      avgRank: 5.5,
      rawWeightedRating: 9,
      computedAt: '2026-04-24T00:00:00Z',
      totalRankedTeams: 60,
      configurations: DEFAULT_CONFIGURATIONS,
    };
    const scopedRanking = {
      ...identityRanking,
      songsPlayed: 4,
      adjustedSkillRank: 26,
      weightedRank: 27,
      fcRateRank: 28,
      totalScoreRank: 29,
      totalScore: 432100,
      fullComboCount: 3,
      fcRate: 0.75,
      avgStars: 6,
      bestRank: 4,
      avgRank: 8.25,
    };
    mockApi.getBandRanking.mockImplementation((bandType, teamKey, comboId) => {
      if (bandType === 'Band_Duets' && teamKey === 'p3:p4' && comboId === DUET_COMBO_FILTER.comboId) return Promise.resolve(scopedRanking);
      if (bandType === 'Band_Duets' && teamKey === 'p3:p4') return Promise.resolve(identityRanking);
      return Promise.resolve(scopedRanking);
    });

    renderBandPage(
      '/bands/band-guid-2?bandType=Band_Duets&teamKey=p3%3Ap4&names=Player%20Three%20%2B%20Player%20Four',
      createTestQueryClient(),
      DUET_COMBO_FILTER,
    );
    await advancePastSpinner();

    expect(mockApi.getBandRanking).toHaveBeenCalledWith('Band_Duets', 'p3:p4');
    expect(mockApi.getBandRanking).toHaveBeenCalledWith('Band_Duets', 'p3:p4', DUET_COMBO_FILTER.comboId);
    expect(mockApi.getBandRankHistory).toHaveBeenCalledWith('Band_Duets', 'p3:p4', 30, DUET_COMBO_FILTER.comboId);
    expect(mockApi.getBandSongs).toHaveBeenCalledWith('Band_Duets', 'p3:p4', 5, DUET_COMBO_FILTER.comboId);

    const statisticsSection = screen.getByTestId('band-section-statistics');
    expect(statisticsSection).toHaveTextContent('4 / 10');
    expect(statisticsSection).toHaveTextContent('3 / 10');
    expect(statisticsSection).toHaveTextContent('432,100');
    expect(statisticsSection).toHaveTextContent('#26');
    expect(statisticsSection).not.toHaveTextContent('987,654');
  });

  it('does not apply a duet combo filter to a different band type', async () => {
    mockApi.getBandRanking.mockResolvedValueOnce({
      bandId: 'quad-band-guid',
      bandType: 'Band_Quad',
      teamKey: 'q1:q2:q3:q4',
      teamMembers: [
        { accountId: 'q1', displayName: 'Quad One' },
        { accountId: 'q2', displayName: 'Quad Two' },
        { accountId: 'q3', displayName: 'Quad Three' },
        { accountId: 'q4', displayName: 'Quad Four' },
      ],
      members: [
        { accountId: 'q1', displayName: 'Quad One', instruments: ['Solo_Guitar'] },
        { accountId: 'q2', displayName: 'Quad Two', instruments: ['Solo_Bass'] },
        { accountId: 'q3', displayName: 'Quad Three', instruments: ['Solo_Drums'] },
        { accountId: 'q4', displayName: 'Quad Four', instruments: ['Solo_Vocals'] },
      ],
      songsPlayed: 6,
      totalChartedSongs: 10,
      coverage: 0.6,
      rawSkillRating: 12,
      adjustedSkillRating: 10,
      adjustedSkillRank: 11,
      weightedRating: 9,
      weightedRank: 12,
      fcRate: 0.2,
      fcRateRank: 13,
      totalScore: 654321,
      totalScoreRank: 14,
      avgAccuracy: 97 * ACCURACY_SCALE,
      fullComboCount: 2,
      avgStars: 5,
      bestRank: 3,
      avgRank: 7,
      rawWeightedRating: 9,
      computedAt: '2026-04-24T00:00:00Z',
      totalRankedTeams: 70,
      configurations: [],
    });

    renderBandPage(
      '/bands/quad-band-guid?bandType=Band_Quad&teamKey=q1%3Aq2%3Aq3%3Aq4&names=Quad%20One%20%2B%20Quad%20Two%20%2B%20Quad%20Three%20%2B%20Quad%20Four',
      createTestQueryClient(),
      DUET_COMBO_FILTER,
    );
    await advancePastSpinner();

    expect(mockApi.getBandRanking).toHaveBeenCalledWith('Band_Quad', 'q1:q2:q3:q4');
    expect(mockApi.getBandRanking).not.toHaveBeenCalledWith('Band_Quad', 'q1:q2:q3:q4', DUET_COMBO_FILTER.comboId);
    expect(mockApi.getBandRankHistory).toHaveBeenCalledWith('Band_Quad', 'q1:q2:q3:q4', 30, undefined);
    expect(mockApi.getBandSongs).toHaveBeenCalledWith('Band_Quad', 'q1:q2:q3:q4', 5, undefined);
  });

  it('seeds band detail cache from contextual ranking configurations', async () => {
    const queryClient = createTestQueryClient();
    renderBandPage('/bands/band-guid-1?bandType=Band_Duets&teamKey=p1%3Ap2&names=Player%20One%20%2B%20Player%20Two', queryClient);
    await advancePastSpinner();

    const cached = queryClient.getQueryData<BandDetailResponse>(queryKeys.bandDetail('band-guid-1'));
    expect(mockApi.getBandRanking).toHaveBeenCalledWith('Band_Duets', 'p1:p2');
    expect(mockApi.getBandDetail).not.toHaveBeenCalled();
    expect(cached?.band.bandId).toBe('band-guid-1');
    expect(cached?.ranking.bandId).toBe('band-guid-1');
    expect(cached?.configurations).toEqual(DEFAULT_CONFIGURATIONS);
  });

  it('uses route names as member fallbacks for ranking-only band context', async () => {
    mockApi.getBandRanking.mockResolvedValueOnce({
      bandId: 'band-guid-2',
      bandType: 'Band_Duets',
      teamKey: 'p3:p4',
      teamMembers: [
        { accountId: 'p3', displayName: null },
        { accountId: 'p4', displayName: '' },
      ],
      members: [
        { accountId: 'p3', displayName: null, instruments: ['Solo_Drums'] },
        { accountId: 'p4', displayName: '', instruments: ['Solo_Vocals'] },
      ],
      songsPlayed: 3,
      totalChartedSongs: 10,
      coverage: 0.3,
      rawSkillRating: 12,
      adjustedSkillRating: 10,
      adjustedSkillRank: 7,
      weightedRating: 9,
      weightedRank: 8,
      fcRate: 0.5,
      fcRateRank: 9,
      totalScore: 123456,
      totalScoreRank: 10,
      avgAccuracy: 98.5 * ACCURACY_SCALE,
      fullComboCount: 1,
      avgStars: 5.5,
      bestRank: 1,
      avgRank: 4.5,
      rawWeightedRating: 9,
      computedAt: '2026-04-24T00:00:00Z',
      totalRankedTeams: 50,
    });

    renderBandPage('/bands/band-guid-2?bandType=Band_Duets&teamKey=p3%3Ap4&names=Friendly%20Three%2C%20Friendly%20Four');
    await advancePastSpinner();

    expect(mockApi.getBandDetail).not.toHaveBeenCalled();
    expect(await screen.findByText('Friendly Three + Friendly Four')).toBeTruthy();
    const memberCards = screen.getAllByTestId('band-member-card');
    expect(memberCards[0]).toHaveTextContent('Friendly Three');
    expect(memberCards[1]).toHaveTextContent('Friendly Four');
  });

  it('does not fall back to the clean band detail endpoint when contextual ranking fails', async () => {
    mockApi.getBandRanking.mockRejectedValueOnce(new Error('Team not found'));

    renderBandPage('/bands/band-guid-missing?bandType=Band_Duets&teamKey=missing-a%3Amissing-b&names=Missing%20Band');
    await advancePastSpinner();

    expect(mockApi.getBandRanking).toHaveBeenCalledWith('Band_Duets', 'missing-a:missing-b');
    expect(mockApi.getBandDetail).not.toHaveBeenCalled();
    expect(await screen.findByText('Band not found')).toBeTruthy();
    expect(screen.getByText('Team not found')).toBeTruthy();
  });
});
