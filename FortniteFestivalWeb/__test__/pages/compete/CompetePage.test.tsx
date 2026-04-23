import { describe, it, expect, vi, beforeAll, beforeEach, afterEach } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { Route, Routes, useLocation } from 'react-router-dom';
import { stubElementDimensions, stubResizeObserver, stubScrollTo } from '../../helpers/browserStubs';
import { TestProviders } from '../../helpers/TestProviders';
import type { ComboPageResponse, RankingsPageResponse, RivalsListResponse } from '@festival/core/api/serverTypes';
import { contentHash } from '../../../src/firstRun/types';
import { competeSlides } from '../../../src/pages/compete/firstRun';

const mockApi = vi.hoisted(() => ({
  getComboRankings: vi.fn(),
  getPlayerComboRanking: vi.fn(),
  getRankings: vi.fn(),
  getPlayerRanking: vi.fn(),
  getRivalsList: vi.fn(),
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
  localStorage.clear();
  localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-player', displayName: 'Test Player' }));
  seedCompeteFirstRun();

  mockApi.getComboRankings.mockImplementation(async (comboId: string) => ({
    comboId,
    rankBy: 'totalscore',
    page: 1,
    pageSize: 10,
    totalAccounts: 100,
    entries: [{
      rank: 1,
      accountId: `top-${comboId}`,
      displayName: `Top ${comboId}`,
      adjustedRating: 0.9,
      weightedRating: 0.8,
      fcRate: 0.7,
      totalScore: 123456,
      maxScorePercent: 0.88,
      songsPlayed: 25,
      fullComboCount: 20,
      computedAt: '2026-01-01T00:00:00Z',
    }],
  } satisfies ComboPageResponse));
  mockApi.getPlayerComboRanking.mockImplementation(async (_accountId: string, comboId: string) => ({
    comboId,
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
  }));
  mockApi.getRankings.mockImplementation(async (instrument: string) => ({
    instrument,
    rankBy: 'totalscore',
    page: 1,
    pageSize: 10,
    totalAccounts: 100,
    entries: [{
      accountId: `top-${instrument}`,
      displayName: `Top ${instrument}`,
      adjustedSkillRank: 1,
      weightedRank: 1,
      fcRateRank: 1,
      totalScoreRank: 1,
      maxScorePercentRank: 1,
      rawSkillRating: 0.9,
      weightedRating: 0.8,
      rawWeightedRating: 0.8,
      totalChartedSongs: 25,
      songsPlayed: 25,
      totalScore: 555555,
      maxScorePercent: 0.9,
      rawMaxScorePercent: 0.9,
      fullComboCount: 20,
      fcRate: 0.8,
      avgAccuracy: 95,
      avgStars: 5,
      bestRank: 1,
      avgRank: 1,
      computedAt: '2026-01-01T00:00:00Z',
    }],
  } satisfies RankingsPageResponse));
  mockApi.getPlayerRanking.mockImplementation(async (instrument: string) => ({
    instrument,
    totalRankedAccounts: 100,
    accountId: 'test-player',
    displayName: 'Test Player',
    adjustedSkillRank: 10,
    weightedRank: 10,
    fcRateRank: 10,
    totalScoreRank: 10,
    maxScorePercentRank: 10,
    rawSkillRating: 0.6,
    weightedRating: 0.5,
    rawWeightedRating: 0.5,
    totalChartedSongs: 25,
    songsPlayed: 20,
    totalScore: 444444,
    maxScorePercent: 0.7,
    rawMaxScorePercent: 0.7,
    fullComboCount: 12,
    fcRate: 0.48,
    avgAccuracy: 90,
    avgStars: 5,
    bestRank: 10,
    avgRank: 10,
    computedAt: '2026-01-01T00:00:00Z',
  }));
  mockApi.getRivalsList.mockImplementation(async (_accountId: string, scope: string) => ({
    combo: scope,
    above: [{ accountId: `above-${scope}`, displayName: `Above ${scope}`, sharedSongCount: 5, rivalScore: 100, aheadCount: 2, behindCount: 3, avgSignedDelta: 1.2 }],
    below: [{ accountId: `below-${scope}`, displayName: `Below ${scope}`, sharedSongCount: 5, rivalScore: 90, aheadCount: 3, behindCount: 2, avgSignedDelta: -1.1 }],
  } satisfies RivalsListResponse));
});

afterEach(() => {
  vi.useRealTimers();
});

const { default: CompetePage } = await import('../../../src/pages/compete/CompetePage');

function LocationEcho() {
  const location = useLocation();
  return <div data-testid="location-search">{location.pathname}{location.search}</div>;
}

async function advancePastPageTransition() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1200);
  });
}

function renderCompete(route = '/compete') {
  return render(
    <TestProviders route={route} accountId="test-player">
      <Routes>
        <Route path="/compete" element={<CompetePage />} />
        <Route path="/leaderboards/all" element={<LocationEcho />} />
      </Routes>
    </TestProviders>,
  );
}

function seedCompeteFirstRun() {
  const seen: Record<string, { version: number; hash: string; seenAt: string }> = {};
  for (const slide of competeSlides) {
    seen[slide.id] = {
      version: slide.version,
      hash: contentHash(slide.contentKey ?? (slide.title + slide.description)),
      seenAt: new Date().toISOString(),
    };
  }

  localStorage.setItem('fst:firstRun', JSON.stringify(seen));
}

describe('CompetePage', () => {
  it('renders combo-first sections and per-instrument sections for multi-select families', async () => {
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: false,
      showDrums: true,
      showVocals: false,
      showProLead: false,
      showProBass: false,
      showPeripheralVocals: true,
      showPeripheralCymbals: true,
      showPeripheralDrums: false,
    }));

    renderCompete();
    await advancePastPageTransition();

    expect(mockApi.getComboRankings).toHaveBeenCalledWith('05', 'totalscore', 1, 10);
    expect(mockApi.getComboRankings).not.toHaveBeenCalledWith('c0', 'totalscore', 1, 10);
    expect(mockApi.getComboRankings).not.toHaveBeenCalledWith('c5', 'totalscore', 1, 10);
    expect(mockApi.getRankings).toHaveBeenCalledWith('Solo_Guitar', 'totalscore', 1, 10);
    expect(mockApi.getRankings).toHaveBeenCalledWith('Solo_Drums', 'totalscore', 1, 10);
    expect(mockApi.getRankings).toHaveBeenCalledWith('Solo_PeripheralVocals', 'totalscore', 1, 10);
    expect(mockApi.getRankings).toHaveBeenCalledWith('Solo_PeripheralCymbals', 'totalscore', 1, 10);
    expect(mockApi.getPlayerComboRanking).toHaveBeenCalledWith('test-player', '05', 'totalscore');
    expect(mockApi.getPlayerComboRanking).not.toHaveBeenCalledWith('test-player', 'c0', 'totalscore');
    expect(mockApi.getPlayerRanking).toHaveBeenCalledWith('Solo_Guitar', 'test-player', 'totalscore');
    expect(mockApi.getPlayerRanking).toHaveBeenCalledWith('Solo_Drums', 'test-player', 'totalscore');
    expect(mockApi.getPlayerRanking).toHaveBeenCalledWith('Solo_PeripheralVocals', 'test-player', 'totalscore');
    expect(mockApi.getPlayerRanking).toHaveBeenCalledWith('Solo_PeripheralCymbals', 'test-player', 'totalscore');
    expect(mockApi.getRivalsList).toHaveBeenCalledWith('test-player', '05');
    expect(mockApi.getRivalsList).not.toHaveBeenCalledWith('test-player', 'c0');
    expect(mockApi.getRivalsList).toHaveBeenCalledWith('test-player', 'Solo_Guitar');
    expect(mockApi.getRivalsList).toHaveBeenCalledWith('test-player', 'Solo_Drums');
    expect(mockApi.getRivalsList).toHaveBeenCalledWith('test-player', 'Solo_PeripheralVocals');
    expect(mockApi.getRivalsList).toHaveBeenCalledWith('test-player', 'Solo_PeripheralCymbals');
    expect((await screen.findAllByRole('button', { name: /Lead \+ Drums/i })).length).toBe(2);
    expect((await screen.findAllByText(/^Lead$/i)).length).toBe(2);
    expect((await screen.findAllByText(/^Drums$/i)).length).toBe(2);
    expect((await screen.findAllByText(/^Mic Mode$/i)).length).toBe(2);
    expect((await screen.findAllByText(/^Pro Drums \+ Cymbals$/i)).length).toBe(2);
    expect(screen.queryByRole('button', { name: /Mic Mode \+ Pro Drums \+ Cymbals/i })).not.toBeInTheDocument();

    const leadDrumsButtons = await screen.findAllByRole('button', { name: /Lead \+ Drums/i });
    expect(Array.from(leadDrumsButtons[0]?.querySelectorAll('img') ?? [])).toHaveLength(2);
  });

  it('keeps combo sections before solo sections within a family', async () => {
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: false,
      showDrums: true,
      showVocals: false,
      showProLead: false,
      showProBass: false,
      showPeripheralVocals: false,
      showPeripheralCymbals: true,
      showPeripheralDrums: false,
    }));

    renderCompete();
    await advancePastPageTransition();

    expect(mockApi.getComboRankings).toHaveBeenCalledWith('05', 'totalscore', 1, 10);
    expect(mockApi.getRankings).toHaveBeenCalledWith('Solo_Guitar', 'totalscore', 1, 10);
    expect(mockApi.getRankings).toHaveBeenCalledWith('Solo_Drums', 'totalscore', 1, 10);
    expect(mockApi.getRankings).toHaveBeenCalledWith('Solo_PeripheralCymbals', 'totalscore', 1, 10);
    expect(mockApi.getPlayerComboRanking).toHaveBeenCalledWith('test-player', '05', 'totalscore');
    expect(mockApi.getPlayerRanking).toHaveBeenCalledWith('Solo_Guitar', 'test-player', 'totalscore');
    expect(mockApi.getPlayerRanking).toHaveBeenCalledWith('Solo_Drums', 'test-player', 'totalscore');
    expect(mockApi.getPlayerRanking).toHaveBeenCalledWith('Solo_PeripheralCymbals', 'test-player', 'totalscore');
    expect(mockApi.getRivalsList).toHaveBeenCalledWith('test-player', '05');
    expect(mockApi.getRivalsList).toHaveBeenCalledWith('test-player', 'Solo_Guitar');
    expect(mockApi.getRivalsList).toHaveBeenCalledWith('test-player', 'Solo_Drums');
    expect(mockApi.getRivalsList).toHaveBeenCalledWith('test-player', 'Solo_PeripheralCymbals');

    const leaderboardButtons = await screen.findAllByRole('button');
    const buttonLabels = leaderboardButtons.map((button) => button.textContent ?? '');
    const leadDrumsIndex = buttonLabels.findIndex((text) => /Lead \+ Drums/.test(text));
    const leadIndex = buttonLabels.findIndex((text) => /^LeadSee All$/.test(text));
    const drumsIndex = buttonLabels.findIndex((text) => /^DrumsSee All$/.test(text));
    expect(leadDrumsIndex).toBeGreaterThanOrEqual(0);
    expect(leadIndex).toBeGreaterThan(leadDrumsIndex);
    expect(drumsIndex).toBeGreaterThan(leadIndex);
  });

  it('navigates combo leaderboard scopes to the combo full-rankings route', async () => {
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: false,
      showDrums: true,
      showVocals: false,
      showProLead: false,
      showProBass: false,
      showPeripheralVocals: false,
      showPeripheralCymbals: false,
      showPeripheralDrums: false,
    }));

    renderCompete();
    await advancePastPageTransition();

    const [leaderboardsHeader] = await screen.findAllByRole('button', { name: /Lead \+ Drums/i });

    fireEvent.click(leaderboardsHeader);

    expect(await screen.findByTestId('location-search')).toHaveTextContent('/leaderboards/all?combo=05&rankBy=totalscore');
  });

  it('renders empty-state cards for empty leaderboard and rivals scopes', async () => {
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: false,
      showDrums: false,
      showVocals: false,
      showProLead: false,
      showProBass: false,
      showPeripheralVocals: false,
      showPeripheralCymbals: false,
      showPeripheralDrums: false,
    }));

    mockApi.getRankings.mockResolvedValueOnce({
      instrument: 'Solo_Guitar',
      rankBy: 'totalscore',
      page: 1,
      pageSize: 10,
      totalAccounts: 0,
      entries: [],
    } satisfies RankingsPageResponse);
    mockApi.getPlayerRanking.mockResolvedValueOnce(null);
    mockApi.getRivalsList.mockResolvedValueOnce({
      combo: 'Solo_Guitar',
      above: [],
      below: [],
    } satisfies RivalsListResponse);

    renderCompete();
    await advancePastPageTransition();

    expect(await screen.findByText('No scores yet')).toBeInTheDocument();
    expect(await screen.findByText('No scores recorded yet for Lead.')).toBeInTheDocument();
    expect(await screen.findByText('No rivals yet')).toBeInTheDocument();
    expect(await screen.findByText('No rivals found for Lead yet.')).toBeInTheDocument();
    expect(screen.queryByText('See All')).not.toBeInTheDocument();
    expect(screen.queryByText('View full leaderboards')).not.toBeInTheDocument();
    expect(screen.queryByText('View all rivals')).not.toBeInTheDocument();
  });

  it('shows quick links only for the two top-level sections', async () => {
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: false,
      showDrums: true,
      showVocals: false,
      showProLead: false,
      showProBass: false,
      showPeripheralVocals: false,
      showPeripheralCymbals: false,
      showPeripheralDrums: false,
    }));

    renderCompete();
    await advancePastPageTransition();

    fireEvent.click(await screen.findByRole('button', { name: 'Quick Links' }));

    const list = await screen.findByTestId('compete-quick-links-modal-list');
    expect(screen.getByTestId('compete-quick-link-leaderboards')).toBeInTheDocument();
    expect(screen.getByTestId('compete-quick-link-rivals')).toBeInTheDocument();
    expect(list.querySelectorAll('button')).toHaveLength(2);
    expect(screen.queryByTestId('compete-quick-link-solo-guitar')).not.toBeInTheDocument();
    expect(screen.queryByTestId('compete-quick-link-05')).not.toBeInTheDocument();
  });
});
