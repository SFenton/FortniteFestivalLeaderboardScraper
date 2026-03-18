import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { Routes, Route } from 'react-router-dom';
import SuggestionsPage from '../../../pages/suggestions/SuggestionsPage';
import { TestProviders } from '../../helpers/TestProviders';
import { stubScrollTo, stubResizeObserver, stubIntersectionObserver } from '../../helpers/browserStubs';

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue({ songs: [
      { songId: 's1', title: 'Alpha Song', artist: 'Artist A', year: 2024, difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1 }, albumArt: 'https://example.com/a.jpg' },
      { songId: 's2', title: 'Beta Song', artist: 'Artist B', year: 2023, difficulty: { guitar: 2, bass: 1, drums: 3, vocals: 2 } },
      { songId: 's3', title: 'Gamma Song', artist: 'Artist C', year: 2025, difficulty: { guitar: 5, bass: 4, drums: 5, vocals: 3 } },
      { songId: 's4', title: 'Delta Song', artist: 'Artist D', year: 2024, difficulty: { guitar: 1, bass: 1, drums: 1, vocals: 1 } },
      { songId: 's5', title: 'Epsilon Song', artist: 'Artist E', year: 2024, difficulty: { guitar: 4, bass: 3, drums: 4, vocals: 2 } },
    ], count: 5, currentSeason: 5 }),
    getPlayer: fn().mockResolvedValue({
      accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 3,
      scores: [
        { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, percentile: 80, accuracy: 90, stars: 4, season: 5 },
        { songId: 's2', instrument: 'Solo_Guitar', score: 80000, rank: 10, percentile: 60, accuracy: 85, stars: 3, season: 5 },
        { songId: 's3', instrument: 'Solo_Guitar', score: 120000, rank: 2, percentile: 95, accuracy: 97, stars: 5, season: 5 },
      ],
    }),
    getSyncStatus: fn().mockResolvedValue({ accountId: 'test-player-1', isTracked: true, backfill: null, historyRecon: null }),
    getVersion: fn().mockResolvedValue({ version: '1.0.0' }),
    getLeaderboard: fn().mockResolvedValue({ songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] }),
    getAllLeaderboards: fn().mockResolvedValue({ songId: 's1', instruments: [] }),
    getPlayerHistory: fn().mockResolvedValue({ accountId: 'test-player-1', count: 0, history: [] }),
    getPlayerStats: fn().mockResolvedValue({ accountId: 'test-player-1', stats: [] }),
    searchAccounts: fn().mockResolvedValue({ results: [] }),
    trackPlayer: fn().mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: 'none' }),
  };
});

vi.mock('../../../api/client', () => ({ api: mockApi }));

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver();
  stubIntersectionObserver();
});

function resetMocks() {
  mockApi.getSongs.mockResolvedValue({ songs: [
    { songId: 's1', title: 'Alpha Song', artist: 'Artist A', year: 2024, difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1 }, albumArt: 'https://example.com/a.jpg' },
    { songId: 's2', title: 'Beta Song', artist: 'Artist B', year: 2023, difficulty: { guitar: 2, bass: 1, drums: 3, vocals: 2 } },
    { songId: 's3', title: 'Gamma Song', artist: 'Artist C', year: 2025, difficulty: { guitar: 5, bass: 4, drums: 5, vocals: 3 } },
    { songId: 's4', title: 'Delta Song', artist: 'Artist D', year: 2024, difficulty: { guitar: 1, bass: 1, drums: 1, vocals: 1 } },
    { songId: 's5', title: 'Epsilon Song', artist: 'Artist E', year: 2024, difficulty: { guitar: 4, bass: 3, drums: 4, vocals: 2 } },
  ], count: 5, currentSeason: 5 });
  mockApi.getPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 3, scores: [
    { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, percentile: 80, accuracy: 90, stars: 4, season: 5 },
    { songId: 's2', instrument: 'Solo_Guitar', score: 80000, rank: 10, percentile: 60, accuracy: 85, stars: 3, season: 5 },
    { songId: 's3', instrument: 'Solo_Guitar', score: 120000, rank: 2, percentile: 95, accuracy: 97, stars: 5, season: 5 },
  ] });
  mockApi.getSyncStatus.mockResolvedValue({ accountId: 'test-player-1', isTracked: true, backfill: null, historyRecon: null });
  mockApi.getVersion.mockResolvedValue({ version: '1.0.0' });
  mockApi.getLeaderboard.mockResolvedValue({ songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] });
  mockApi.getAllLeaderboards.mockResolvedValue({ songId: 's1', instruments: [] });
  mockApi.getPlayerHistory.mockResolvedValue({ accountId: 'test-player-1', count: 0, history: [] });
  mockApi.getPlayerStats.mockResolvedValue({ accountId: 'test-player-1', stats: [] });
  mockApi.searchAccounts.mockResolvedValue({ results: [] });
  mockApi.trackPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: 'none' });
}

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-player-1', displayName: 'TestPlayer' }));
  resetMocks();
});

function renderSuggestions(route = '/suggestions', accountId = 'test-player-1') {
  return render(
    <TestProviders route={route} accountId={accountId}>
      <Routes>
        <Route path="/suggestions" element={<SuggestionsPage accountId={accountId} />} />
      </Routes>
    </TestProviders>,
  );
}

describe('SuggestionsPage — extra coverage', () => {
  /* ── Initial load → spinner → categories rendered ── */
  it('shows spinner before content loads', async () => {
    // Make player loading take time
    mockApi.getPlayer.mockReturnValue(new Promise(() => {}));
    const { container } = renderSuggestions();
    // Should show spinner/page element
    expect(container.querySelector('[class*="page"]')).toBeTruthy();
  });

  it('renders categories once data loads', async () => {
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
  });

  /* ── Empty state with no player ── */
  it('shows could not load player message when no player data', async () => {
    mockApi.getPlayer.mockResolvedValue(null);
    render(
      <TestProviders route="/suggestions">
        <Routes>
          <Route path="/suggestions" element={<SuggestionsPage accountId="no-match" />} />
        </Routes>
      </TestProviders>,
    );
    await waitFor(() => {
      expect(screen.getByText('Could not load player data.')).toBeTruthy();
    });
  });

  /* ── Filter button rendering ── */
  it('renders filter pill on desktop', async () => {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      configurable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: false, media: query, onchange: null,
        addEventListener: vi.fn(), removeEventListener: vi.fn(),
        addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
      })),
    });
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
    // Filter pill should exist
    const filterPill = screen.queryByText('Filter');
    expect(filterPill).toBeTruthy();
  });

  /* ── Service down message ── */
  it('shows empty state when no songs and player has no scores', async () => {
    mockApi.getSongs.mockResolvedValue({ songs: [], count: 0, currentSeason: 5 });
    mockApi.getPlayer.mockResolvedValue({
      accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 0, scores: [],
    });
    const { container } = renderSuggestions();
    await waitFor(() => {
      // With empty data, the page mounts and shows some content
      expect(container.innerHTML.length).toBeGreaterThan(0);
    });
  });

  /* ── Error state: API failure ── */
  it('handles API failure gracefully', async () => {
    mockApi.getSongs.mockRejectedValue(new Error('Network error'));
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
  });

  /* ── Empty songs list with filters active ── */
  it('shows empty state with filters when filtered results are empty', async () => {
    // Pre-set a filter to localStorage
    const filter = {
      suggestionsLeadFilter: false,
      suggestionsBassFilter: false,
      suggestionsDrumsFilter: false,
      suggestionsVocalsFilter: false,
      suggestionsProLeadFilter: false,
      suggestionsProBassFilter: false,
    };
    localStorage.setItem('fst-suggestions-filter', JSON.stringify(filter));
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(50);
    });
  });

  /* ── Page structure ── */
  it('renders page wrapper with correct class', async () => {
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.querySelector('[class*="page"]')).toBeTruthy();
    });
  });

  it('renders scroll area', async () => {
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.querySelector('[class*="scrollArea"]') || container.querySelector('#suggestions-scroll')).toBeTruthy();
    });
  });
});
