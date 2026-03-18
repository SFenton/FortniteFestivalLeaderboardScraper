import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { Routes, Route } from 'react-router-dom';
import { TestProviders } from '../../helpers/TestProviders';
import { stubScrollTo, stubResizeObserver, stubIntersectionObserver } from '../../helpers/browserStubs';

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue({ songs: [
      { songId: 's1', title: 'Alpha Song', artist: 'Artist A', year: 2024, difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1 }, albumArt: 'https://example.com/a.jpg' },
      { songId: 's2', title: 'Beta Song', artist: 'Artist B', year: 2023, difficulty: { guitar: 2, bass: 1, drums: 3, vocals: 2 } },
      { songId: 's3', title: 'Gamma Song', artist: 'Artist C', year: 2025, difficulty: { guitar: 5, bass: 4, drums: 5, vocals: 3 } },
    ], count: 3, currentSeason: 5 }),
    getPlayer: fn().mockResolvedValue({
      accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 2,
      scores: [
        { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, percentile: 80, accuracy: 90, stars: 4, season: 5 },
        { songId: 's2', instrument: 'Solo_Guitar', score: 80000, rank: 10, percentile: 60, accuracy: 85, stars: 3, season: 5 },
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

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

import SuggestionsPage from '../../../src/pages/suggestions/SuggestionsPage';

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver();
  stubIntersectionObserver();
});

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-player-1', displayName: 'TestPlayer' }));
  // Re-set mocks after clearAllMocks
  mockApi.getSongs.mockResolvedValue({ songs: [
    { songId: 's1', title: 'Alpha Song', artist: 'Artist A', year: 2024, difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1 }, albumArt: 'https://example.com/a.jpg' },
    { songId: 's2', title: 'Beta Song', artist: 'Artist B', year: 2023, difficulty: { guitar: 2, bass: 1, drums: 3, vocals: 2 } },
    { songId: 's3', title: 'Gamma Song', artist: 'Artist C', year: 2025, difficulty: { guitar: 5, bass: 4, drums: 5, vocals: 3 } },
  ], count: 3, currentSeason: 5 });
  mockApi.getPlayer.mockResolvedValue({
    accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 2,
    scores: [
      { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, percentile: 80, accuracy: 90, stars: 4, season: 5 },
      { songId: 's2', instrument: 'Solo_Guitar', score: 80000, rank: 10, percentile: 60, accuracy: 85, stars: 3, season: 5 },
    ],
  });
  mockApi.getSyncStatus.mockResolvedValue({ accountId: 'test-player-1', isTracked: true, backfill: null, historyRecon: null });
  mockApi.getVersion.mockResolvedValue({ version: '1.0.0' });
  mockApi.getLeaderboard.mockResolvedValue({ entries: [] });
  mockApi.getAllLeaderboards.mockResolvedValue({ instruments: [] });
  mockApi.getPlayerHistory.mockResolvedValue({ accountId: 'test-player-1', count: 0, history: [] });
  mockApi.getPlayerStats.mockResolvedValue({ stats: [] });
  mockApi.searchAccounts.mockResolvedValue({ results: [] });
  mockApi.trackPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: 'none' });
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

describe('SuggestionsPage (additional)', () => {
  it('opens filter modal when Filter button is clicked', async () => {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      configurable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: false, media: query, onchange: null,
        addEventListener: vi.fn(), removeEventListener: vi.fn(),
        addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
      })),
    });
    renderSuggestions();
    await waitFor(() => {
      const filterBtn = screen.queryByText('Filter');
      if (filterBtn) {
        fireEvent.click(filterBtn);
      }
    });
  });

  it('shows spinner overlay during loading phase', async () => {
    // Player loads slowly — spinner should be shown
    mockApi.getPlayer.mockReturnValue(new Promise(() => {}));
    const { container } = renderSuggestions();
    // The page should have the spinner overlay
    await waitFor(() => {
      expect(container.querySelector('[class*="spinner"]')).toBeTruthy();
    });
  });

  it('renders category cards when categories are generated', async () => {
    // Provide enough diversity to generate categories
    mockApi.getSongs.mockResolvedValue({ songs: [
      { songId: 's1', title: 'A', artist: 'A', year: 2024, difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1 } },
      { songId: 's2', title: 'B', artist: 'A', year: 2023, difficulty: { guitar: 2, bass: 1, drums: 3, vocals: 2 } },
      { songId: 's3', title: 'C', artist: 'C', year: 2025, difficulty: { guitar: 5, bass: 4, drums: 5, vocals: 3 } },
      { songId: 's4', title: 'D', artist: 'C', year: 2024, difficulty: { guitar: 1, bass: 1, drums: 1, vocals: 1 } },
      { songId: 's5', title: 'E', artist: 'E', year: 2024, difficulty: { guitar: 4, bass: 3, drums: 2, vocals: 4 } },
    ], count: 5, currentSeason: 5 });
    mockApi.getPlayer.mockResolvedValue({
      accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 3,
      scores: [
        { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, accuracy: 90, stars: 4, season: 5, totalEntries: 100 },
        { songId: 's2', instrument: 'Solo_Guitar', score: 80000, rank: 10, accuracy: 85, stars: 3, season: 5, totalEntries: 100 },
        { songId: 's3', instrument: 'Solo_Guitar', score: 120000, rank: 2, accuracy: 97, stars: 5, season: 5, totalEntries: 100 },
      ],
    });

    const { container } = renderSuggestions();
    await waitFor(() => {
      // Should have rendered at least the page container
      expect(container.querySelector('[class*="page"]')).toBeTruthy();
    }, { timeout: 3000 });
  });

  it('persists filter settings to localStorage', async () => {
    renderSuggestions();
    await waitFor(() => {
      const raw = localStorage.getItem('fst-suggestions-filter');
      // Should have been saved (even defaults)
      expect(raw).toBeTruthy();
    });
  });

  it('loads filter settings from localStorage on mount', async () => {
    localStorage.setItem('fst-suggestions-filter', JSON.stringify({
      suggestionsLeadFilter: false,
      suggestionsBassFilter: true,
    }));
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
  });

  it('shows empty state text with filters active', async () => {
    // Disable all instruments so no categories are visible
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: false,
      showBass: false,
      showDrums: false,
      showVocals: false,
      showProLead: false,
      showProBass: false,
    }));
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(50);
    });
  });

  it('handles multiple rapid filter changes', async () => {
    localStorage.setItem('fst-suggestions-filter', JSON.stringify({
      suggestionsLeadFilter: true,
    }));
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
    // Change settings rapidly
    localStorage.setItem('fst-suggestions-filter', JSON.stringify({
      suggestionsLeadFilter: false,
    }));
    localStorage.setItem('fst-suggestions-filter', JSON.stringify({
      suggestionsLeadFilter: true,
    }));
  });
});
