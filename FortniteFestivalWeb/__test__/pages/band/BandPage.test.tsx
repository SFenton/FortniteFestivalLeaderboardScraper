import { describe, it, expect, vi, beforeAll, beforeEach, afterEach } from 'vitest';
import { render, screen, act } from '@testing-library/react';
import { Route, Routes, useLocation } from 'react-router-dom';
import { ACCURACY_SCALE } from '@festival/core';
import { TestProviders } from '../../helpers/TestProviders';
import { stubElementDimensions, stubResizeObserver, stubScrollTo } from '../../helpers/browserStubs';

const mockApi = vi.hoisted(() => ({
  getSongs: vi.fn(),
  getBandDetail: vi.fn(),
  getPlayerBandsByType: vi.fn(),
  getBandRanking: vi.fn(),
  getBandRankHistory: vi.fn(),
  getBandSongs: vi.fn(),
}));

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver({ width: 1024, height: 800 });
  stubElementDimensions(800);
});

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.clearAllMocks();
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
    },
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
  return <div data-testid="current-location">{`${location.pathname}${location.search}`}</div>;
}

function renderBandPage(route: string) {
  return render(
    <TestProviders route={route}>
      <LocationProbe />
      <Routes>
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

    expect(screen.getAllByTestId('band-stat-card')).toHaveLength(11);
    expect(screen.getByText('Adjusted Percentile Rank')).toBeTruthy();
    expect(screen.getByText('Weighted Percentile Rank')).toBeTruthy();
    expect(screen.getByText('FC Rate Rank')).toBeTruthy();
    expect(screen.getByText('Total Score Rank')).toBeTruthy();
    expect(screen.getByText('Songs Played')).toBeTruthy();
    expect(screen.getByText('2 / 10')).toBeTruthy();
    expect(screen.getByText('98.5%')).toBeTruthy();
    expect(screen.getByText('Song Alpha')).toBeTruthy();
    expect(screen.getByText('Song Zeta')).toBeTruthy();
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
});
