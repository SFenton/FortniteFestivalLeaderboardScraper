import { describe, it, expect, vi, beforeAll, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { computeRankWidth } from '../../../src/pages/leaderboards/helpers/rankingHelpers';
import { stubElementDimensions, stubMatchMedia, stubResizeObserver, stubScrollTo } from '../../helpers/browserStubs';
import { TestProviders } from '../../helpers/TestProviders';

const mockApi = vi.hoisted(() => ({
  getComboRankings: vi.fn(),
  getPlayerComboRanking: vi.fn(),
  getRankings: vi.fn(),
  getPlayerRanking: vi.fn(),
  getBandRanking: vi.fn(),
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

const selectedBandProfile = {
  type: 'band',
  bandId: 'band-selected-1',
  bandType: 'Band_Duets',
  teamKey: 'band-a:band-b',
  displayName: 'Alpha + Beta',
  members: [
    { accountId: 'band-a', displayName: 'Alpha' },
    { accountId: 'band-b', displayName: 'Beta' },
  ],
} as const;

function selectBandProfile() {
  localStorage.setItem('fst:selectedProfile', JSON.stringify(selectedBandProfile));
}

function makeBandRankingEntry(overrides: Record<string, unknown> = {}) {
  return {
    bandId: selectedBandProfile.bandId,
    bandType: selectedBandProfile.bandType,
    comboId: null,
    teamKey: selectedBandProfile.teamKey,
    teamMembers: [
      { accountId: 'band-a', displayName: 'Alpha' },
      { accountId: 'band-b', displayName: 'Beta' },
    ],
    members: [
      { accountId: 'band-a', displayName: 'Alpha', instruments: ['Solo_Guitar'] },
      { accountId: 'band-b', displayName: 'Beta', instruments: ['Solo_Drums'] },
    ],
    songsPlayed: 120,
    totalChartedSongs: 160,
    coverage: 0.75,
    rawSkillRating: 0.1234,
    adjustedSkillRating: 0.2345,
    adjustedSkillRank: 7,
    weightedRating: 0.3456,
    weightedRank: 8,
    fcRate: 0.4,
    fcRateRank: 9,
    totalScore: 654321,
    totalScoreRank: 42,
    avgAccuracy: 0.98,
    fullComboCount: 65,
    avgStars: 5.8,
    bestRank: 1,
    avgRank: 7.4,
    rawWeightedRating: 0.3456,
    computedAt: '2026-04-22T00:00:00Z',
    totalRankedTeams: 500,
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
  stubMatchMedia(false);
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
  mockApi.getBandRanking.mockResolvedValue(null);
});

const { default: FullRankingsPage } = await import('../../../src/pages/leaderboards/FullRankingsPage');

describe('FullRankingsPage', () => {
  it('keeps experimental metric deep links available', async () => {
    render(
      <TestProviders route="/leaderboards/all?instrument=Solo_Guitar&rankBy=adjusted" accountId="test-player">
        <Routes>
          <Route path="/leaderboards/all" element={<FullRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(mockApi.getRankings).toHaveBeenCalledWith('Solo_Guitar', 'adjusted', 1, 25);
    });
  });

  it('coerces selected-band Max Score deep links to Total Score', async () => {
    selectBandProfile();
    localStorage.setItem('fst:leaderboardSettings', JSON.stringify({ rankBy: 'maxscore' }));
    mockApi.getBandRanking.mockResolvedValue(makeBandRankingEntry());

    render(
      <TestProviders route="/leaderboards/all?instrument=Solo_Guitar&rankBy=maxscore&page=3">
        <Routes>
          <Route path="/leaderboards/all" element={<FullRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(mockApi.getRankings.mock.calls.some(call => call[0] === 'Solo_Guitar' && call[1] === 'totalscore')).toBe(true);
      expect(mockApi.getRankings.mock.calls.some(call => call[1] === 'maxscore')).toBe(false);
      expect(mockApi.getBandRanking).toHaveBeenCalledWith('Band_Duets', 'band-a:band-b', undefined, 'totalscore');
      expect(JSON.parse(localStorage.getItem('fst:leaderboardSettings') || '{}').rankBy).toBe('totalscore');
    });
  });

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

  it('includes the tracked player rank in desktop scroll-row width calculation', async () => {
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
    const expectedWidth = computeRankWidth([1, 12345]);

    expect(topRank).toHaveStyle({ width: `${expectedWidth}px` });
    expect(playerRank).toHaveStyle({ width: `${computeRankWidth([12345])}px` });
    expect(topRank.style.width).toBe(playerRank.style.width);
  });

  it('keeps mobile scroll-row rank width scoped to page entries only', async () => {
    stubMatchMedia(true);

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

  it('syncs desktop footer rank width to the shared page width when page rows are wider than the player rank', async () => {
    mockApi.getRankings.mockResolvedValue({
      instrument: 'Solo_Guitar',
      rankBy: 'totalscore',
      page: 1,
      pageSize: 25,
      totalAccounts: 12345,
      entries: [makeAccountRankingEntry(12345, { accountId: 'top-player', displayName: 'Top Player' })],
    });
    mockApi.getPlayerRanking.mockResolvedValue(
      makeAccountRankingEntry(42, { accountId: 'test-player', displayName: 'Test Player' }),
    );

    render(
      <TestProviders route="/leaderboards/all?instrument=Solo_Guitar&rankBy=totalscore" accountId="test-player">
        <Routes>
          <Route path="/leaderboards/all" element={<FullRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    const pageRank = await screen.findByText('#12,345');
    const footerRank = await screen.findByText('#42');
    const expectedWidth = computeRankWidth([12345, 42]);

    expect(pageRank).toHaveStyle({ width: `${expectedWidth}px` });
    expect(footerRank).toHaveStyle({ width: `${expectedWidth}px` });
  });

  it('renders adjusted percentile rows with songs, percentile value, and Bayesian rank', async () => {
    mockApi.getRankings.mockResolvedValue({
      instrument: 'Solo_Guitar',
      rankBy: 'adjusted',
      page: 1,
      pageSize: 25,
      totalAccounts: 1000,
      entries: [makeAccountRankingEntry(1, { accountId: 'top-player', displayName: 'Top Player', rawSkillRating: 0.0056, adjustedSkillRating: 0.0409 })],
    });

    render(
      <TestProviders route="/leaderboards/all?instrument=Solo_Guitar&rankBy=adjusted" accountId="test-player">
        <Routes>
          <Route path="/leaderboards/all" element={<FullRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    expect(await screen.findByText('120 / 160')).toBeTruthy();
    expect(await screen.findByText('Top 0.56%')).toBeTruthy();
    expect(await screen.findByText('Bayesian-Calculated Rank:')).toBeTruthy();
    expect(await screen.findByText('0.0409')).toBeTruthy();
  });

  it('uses taller mobile rows for adjusted percentile metadata', async () => {
    stubMatchMedia(true);
    mockApi.getRankings.mockResolvedValue({
      instrument: 'Solo_Guitar',
      rankBy: 'adjusted',
      page: 1,
      pageSize: 25,
      totalAccounts: 1000,
      entries: [makeAccountRankingEntry(1, { accountId: 'top-player', displayName: 'Top Player', rawSkillRating: 0.0056, adjustedSkillRating: 0.0409 })],
    });

    render(
      <TestProviders route="/leaderboards/all?instrument=Solo_Guitar&rankBy=adjusted" accountId="test-player">
        <Routes>
          <Route path="/leaderboards/all" element={<FullRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    const row = (await screen.findByText('Top Player')).closest('a');
    expect(row).toHaveStyle({ height: '76px' });
    expect(await screen.findByText('Top 0.56%')).toBeTruthy();
    expect(await screen.findByText('0.0409')).toBeTruthy();
  });

  it('renders FC rate count numerator in gold on full ranking pages', async () => {
    mockApi.getRankings.mockResolvedValue({
      instrument: 'Solo_Guitar',
      rankBy: 'fcrate',
      page: 1,
      pageSize: 25,
      totalAccounts: 1000,
      entries: [makeAccountRankingEntry(1, { accountId: 'top-player', displayName: 'Top Player', fullComboCount: 65, totalChartedSongs: 160 })],
    });

    render(
      <TestProviders route="/leaderboards/all?instrument=Solo_Guitar&rankBy=fcrate" accountId="test-player">
        <Routes>
          <Route path="/leaderboards/all" element={<FullRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    expect(await screen.findByText('65')).toHaveStyle({ color: '#FFD700' });
  });

  it('renders selected band ranking in the fixed footer', async () => {
    selectBandProfile();
    mockApi.getRankings.mockResolvedValue({
      instrument: 'Solo_Guitar',
      rankBy: 'totalscore',
      page: 1,
      pageSize: 25,
      totalAccounts: 100,
      entries: [makeAccountRankingEntry(1, { accountId: 'top-player', displayName: 'Top Player' })],
    });
    mockApi.getBandRanking.mockResolvedValue(makeBandRankingEntry());

    render(
      <TestProviders route="/leaderboards/all?instrument=Solo_Guitar&rankBy=totalscore">
        <Routes>
          <Route path="/leaderboards/all" element={<FullRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    expect(await screen.findByText('Alpha + Beta')).toBeTruthy();
    expect(await screen.findByText('#42')).toBeTruthy();
    expect(await screen.findByText('654,321')).toBeTruthy();
    expect(mockApi.getBandRanking).toHaveBeenCalledWith('Band_Duets', 'band-a:band-b', undefined, 'totalscore');
  });

  it('links the selected band footer to the band route', async () => {
    selectBandProfile();
    mockApi.getRankings.mockResolvedValue({
      instrument: 'Solo_Guitar',
      rankBy: 'totalscore',
      page: 1,
      pageSize: 25,
      totalAccounts: 100,
      entries: [makeAccountRankingEntry(1, { accountId: 'top-player', displayName: 'Top Player' })],
    });
    mockApi.getBandRanking.mockResolvedValue(makeBandRankingEntry());

    render(
      <TestProviders route="/leaderboards/all?instrument=Solo_Guitar&rankBy=totalscore">
        <Routes>
          <Route path="/leaderboards/all" element={<FullRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    const footerName = await screen.findByText('Alpha + Beta');
    const link = footerName.closest('a');
    expect(link?.getAttribute('href')).toContain('/bands/band-selected-1');
    expect(link?.getAttribute('href')).toContain('bandType=Band_Duets');
    expect(link?.getAttribute('href')).toContain('teamKey=band-a%3Aband-b');
  });

  it('uses the active metric when loading selected band ranking', async () => {
    selectBandProfile();
    mockApi.getRankings.mockResolvedValue({
      instrument: 'Solo_Guitar',
      rankBy: 'adjusted',
      page: 1,
      pageSize: 25,
      totalAccounts: 100,
      entries: [makeAccountRankingEntry(1, { accountId: 'top-player', displayName: 'Top Player' })],
    });
    mockApi.getBandRanking.mockResolvedValue(makeBandRankingEntry());

    render(
      <TestProviders route="/leaderboards/all?instrument=Solo_Guitar&rankBy=adjusted">
        <Routes>
          <Route path="/leaderboards/all" element={<FullRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(mockApi.getBandRanking).toHaveBeenCalledWith('Band_Duets', 'band-a:band-b', undefined, 'adjusted');
    });
  });

  it('passes combo route scope to selected band ranking', async () => {
    selectBandProfile();
    mockApi.getBandRanking.mockResolvedValue(makeBandRankingEntry({ comboId: '05' }));

    render(
      <TestProviders route="/leaderboards/all?combo=05&rankBy=totalscore">
        <Routes>
          <Route path="/leaderboards/all" element={<FullRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(mockApi.getBandRanking).toHaveBeenCalledWith('Band_Duets', 'band-a:band-b', '05', 'totalscore');
    });
  });

  it('renders the selected band footer for the active selected-band combo filter', async () => {
    selectBandProfile();
    mockApi.getRankings.mockResolvedValue({
      instrument: 'Solo_Guitar',
      rankBy: 'totalscore',
      page: 1,
      pageSize: 25,
      totalAccounts: 100,
      entries: [makeAccountRankingEntry(1, { accountId: 'top-player', displayName: 'Top Player' })],
    });
    mockApi.getBandRanking.mockResolvedValue(makeBandRankingEntry());

    render(
      <TestProviders
        route="/leaderboards/all?instrument=Solo_Guitar&rankBy=totalscore"
        bandFilter={{
          bandId: selectedBandProfile.bandId,
          bandType: selectedBandProfile.bandType,
          teamKey: selectedBandProfile.teamKey,
          comboId: 'Solo_Guitar+Solo_Bass',
          assignments: [
            { accountId: 'band-a', instrument: 'Solo_Guitar' },
            { accountId: 'band-b', instrument: 'Solo_Bass' },
          ],
        }}
      >
        <Routes>
          <Route path="/leaderboards/all" element={<FullRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    expect(await screen.findByText('Top Player')).toBeTruthy();
    expect(mockApi.getBandRanking).toHaveBeenCalledWith('Band_Duets', 'band-a:band-b', 'Solo_Guitar+Solo_Bass', 'totalscore');
    expect(document.body.textContent).toContain('Alpha + Beta');
  });

  it('does not query selected band ranking for a selected solo player', async () => {
    render(
      <TestProviders route="/leaderboards/all?instrument=Solo_Guitar&rankBy=totalscore" accountId="test-player">
        <Routes>
          <Route path="/leaderboards/all" element={<FullRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(mockApi.getRankings).toHaveBeenCalled();
    });
    expect(mockApi.getBandRanking).not.toHaveBeenCalled();
  });

  it('does not render a selected band footer when the band ranking is missing', async () => {
    selectBandProfile();
    mockApi.getRankings.mockResolvedValue({
      instrument: 'Solo_Guitar',
      rankBy: 'totalscore',
      page: 1,
      pageSize: 25,
      totalAccounts: 100,
      entries: [makeAccountRankingEntry(1, { accountId: 'top-player', displayName: 'Top Player' })],
    });
    mockApi.getBandRanking.mockResolvedValue(null);

    render(
      <TestProviders route="/leaderboards/all?instrument=Solo_Guitar&rankBy=totalscore">
        <Routes>
          <Route path="/leaderboards/all" element={<FullRankingsPage />} />
        </Routes>
      </TestProviders>,
    );

    await screen.findByText('Top Player');
    await waitFor(() => expect(mockApi.getBandRanking).toHaveBeenCalled());
    expect(document.body.textContent).not.toContain('Alpha + Beta');
  });
});
