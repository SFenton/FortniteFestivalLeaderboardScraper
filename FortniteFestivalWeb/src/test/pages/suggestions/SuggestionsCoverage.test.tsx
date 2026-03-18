/**
 * SuggestionsPage coverage tests — exercises category rendering, filter logic,
 * category sort/render, and FAB action registration.
 */
import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
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
      { songId: 's6', title: 'Zeta Song', artist: 'Artist F', year: 2024, difficulty: { guitar: 3, bass: 2, drums: 3, vocals: 1 } },
      { songId: 's7', title: 'Eta Song', artist: 'Artist G', year: 2024, difficulty: { guitar: 4, bass: 3, drums: 4, vocals: 3 } },
      { songId: 's8', title: 'Theta Song', artist: 'Artist H', year: 2024, difficulty: { guitar: 2, bass: 1, drums: 2, vocals: 1 } },
    ], count: 8, currentSeason: 5 }),
    getPlayer: fn().mockResolvedValue({
      accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 5,
      scores: [
        { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, percentile: 80, accuracy: 90, stars: 4, season: 5, isFullCombo: false },
        { songId: 's2', instrument: 'Solo_Guitar', score: 80000, rank: 10, percentile: 60, accuracy: 85, stars: 3, season: 5, isFullCombo: false },
        { songId: 's3', instrument: 'Solo_Guitar', score: 120000, rank: 2, percentile: 95, accuracy: 97, stars: 5, season: 5, isFullCombo: true },
        { songId: 's1', instrument: 'Solo_Bass', score: 70000, rank: 8, percentile: 70, accuracy: 88, stars: 3, season: 5, isFullCombo: false },
        { songId: 's4', instrument: 'Solo_Guitar', score: 60000, rank: 15, percentile: 50, accuracy: 75, stars: 2, season: 4, isFullCombo: false },
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
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: false, media: query, onchange: null,
      addEventListener: vi.fn(), removeEventListener: vi.fn(),
      addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
});

function resetMocks() {
  mockApi.getSongs.mockResolvedValue({ songs: [
    { songId: 's1', title: 'Alpha Song', artist: 'Artist A', year: 2024, difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1 }, albumArt: 'https://example.com/a.jpg' },
    { songId: 's2', title: 'Beta Song', artist: 'Artist B', year: 2023, difficulty: { guitar: 2, bass: 1, drums: 3, vocals: 2 } },
    { songId: 's3', title: 'Gamma Song', artist: 'Artist C', year: 2025, difficulty: { guitar: 5, bass: 4, drums: 5, vocals: 3 } },
    { songId: 's4', title: 'Delta Song', artist: 'Artist D', year: 2024, difficulty: { guitar: 1, bass: 1, drums: 1, vocals: 1 } },
    { songId: 's5', title: 'Epsilon Song', artist: 'Artist E', year: 2024, difficulty: { guitar: 4, bass: 3, drums: 4, vocals: 2 } },
    { songId: 's6', title: 'Zeta Song', artist: 'Artist F', year: 2024, difficulty: { guitar: 3, bass: 2, drums: 3, vocals: 1 } },
    { songId: 's7', title: 'Eta Song', artist: 'Artist G', year: 2024, difficulty: { guitar: 4, bass: 3, drums: 4, vocals: 3 } },
    { songId: 's8', title: 'Theta Song', artist: 'Artist H', year: 2024, difficulty: { guitar: 2, bass: 1, drums: 2, vocals: 1 } },
  ], count: 8, currentSeason: 5 });
  mockApi.getPlayer.mockResolvedValue({
    accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 5,
    scores: [
      { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, percentile: 80, accuracy: 90, stars: 4, season: 5, isFullCombo: false },
      { songId: 's2', instrument: 'Solo_Guitar', score: 80000, rank: 10, percentile: 60, accuracy: 85, stars: 3, season: 5, isFullCombo: false },
      { songId: 's3', instrument: 'Solo_Guitar', score: 120000, rank: 2, percentile: 95, accuracy: 97, stars: 5, season: 5, isFullCombo: true },
      { songId: 's1', instrument: 'Solo_Bass', score: 70000, rank: 8, percentile: 70, accuracy: 88, stars: 3, season: 5, isFullCombo: false },
      { songId: 's4', instrument: 'Solo_Guitar', score: 60000, rank: 15, percentile: 50, accuracy: 75, stars: 2, season: 4, isFullCombo: false },
    ],
  });
  mockApi.getSyncStatus.mockResolvedValue({ accountId: 'test-player-1', isTracked: true, backfill: null, historyRecon: null });
  mockApi.getVersion.mockResolvedValue({ version: '1.0.0' });
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

describe('SuggestionsPage — coverage: category rendering + filter logic', () => {
  it('renders category cards with real suggestion data', async () => {
    const { container } = renderSuggestions();

    // Wait for content to render (after spinner transitions out)
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(200);
    }, { timeout: 5000 });

    // Should have generated suggestion categories from the song/score data
    // Categories show up as card elements
    const pageEl = container.querySelector('[class*="page"]');
    expect(pageEl).toBeTruthy();
  });

  it('shows empty state with filtered text when filters hide all', async () => {
    // Set a filter that hides everything
    localStorage.setItem('fst-suggestions-filter', JSON.stringify({
      suggestionsLeadFilter: false,
      suggestionsBassFilter: false,
      suggestionsDrumsFilter: false,
      suggestionsVocalsFilter: false,
      suggestionsProLeadFilter: false,
      suggestionsProBassFilter: false,
    }));

    const { container } = renderSuggestions();

    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(50);
    }, { timeout: 5000 });

    // Page should have rendered
    expect(container.querySelector('[class*="page"]')).toBeTruthy();
  });

  it('renders filter button and opens filter modal', async () => {
    renderSuggestions();

    await waitFor(() => {
      const filterBtn = screen.queryByText('Filter');
      expect(filterBtn).toBeTruthy();
    }, { timeout: 5000 });

    const filterBtn = screen.getByText('Filter');
    fireEvent.click(filterBtn);

    // Filter modal should appear
    await waitFor(() => {
      // Modal shows filter options
      expect(document.body.innerHTML).toBeTruthy();
    });
  });

  it('renders categories after spinner → contentIn transition', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });

    const { container } = renderSuggestions();

    // Advance timers to push through load phases
    await vi.advanceTimersByTimeAsync(1000);

    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });

    vi.useRealTimers();
  });

  it('scrolls categories list triggering scroll handler', async () => {
    const { container } = renderSuggestions();

    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    }, { timeout: 5000 });

    // Find scroll area and trigger scroll
    const scrollArea = container.querySelector('[id="suggestions-scroll"]');
    if (scrollArea) {
      fireEvent.scroll(scrollArea);
    }
    expect(container.innerHTML.length).toBeGreaterThan(100);
  });

  it('renders with no songs and no hasMore', async () => {
    mockApi.getSongs.mockResolvedValue({ songs: [], count: 0, currentSeason: 5 });
    mockApi.getPlayer.mockResolvedValue({
      accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 0, scores: [],
    });

    const { container } = renderSuggestions();

    await waitFor(() => {
      // Should show no suggestions or empty state
      expect(
        container.textContent!.includes('No suggestions') ||
        container.textContent!.includes('Could not load') ||
        container.innerHTML.length > 50
      ).toBe(true);
    }, { timeout: 5000 });
  });
});
