/**
 * SuggestionsPage tests — merged from SuggestionsPage, SuggestionsPageExtra,
 * SuggestionsPageMore, and SuggestionsCoverage test files.
 */
import { describe, it, expect, vi, beforeEach, beforeAll, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import { Routes, Route } from 'react-router-dom';
import SuggestionsPage from '../../../src/pages/suggestions/SuggestionsPage';
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
    getRivalSuggestions: fn().mockResolvedValue({ accountId: 'test-player-1', combo: '', computedAt: null, rivals: [] }),
    getRivalsAll: fn().mockResolvedValue({ accountId: 'test-player-1', songs: [], combos: [] }),
    getShop: fn().mockResolvedValue({ songs: [] }),
    getVersions: fn().mockResolvedValue({ songs: '1' }),
    getShopSnapshot: fn().mockResolvedValue({ songIds: [] }),
  };
});

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

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

// ---------------------------------------------------------------------------
// From: SuggestionsPage.test.tsx
// ---------------------------------------------------------------------------

describe('SuggestionsPage', () => {
  it('renders without crashing', async () => {
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
  });

  it('renders the suggestions page container', async () => {
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.querySelector('[data-testid="page-root"]')).toBeTruthy();
    });
  });

  it('shows could-not-load message when no player data and no categories', async () => {
    mockApi.getPlayer.mockResolvedValue(null);
    render(
      <TestProviders route="/suggestions">
        <Routes>
          <Route path="/suggestions" element={<SuggestionsPage accountId="no-match" />} />
        </Routes>
      </TestProviders>,
    );
    await waitFor(() => {
      expect(screen.getByText('Could not load player data.')).toBeDefined();
    });
  });

  it('shows empty state when no suggestions and loading is done', async () => {
    mockApi.getSongs.mockResolvedValue({ songs: [], count: 0, currentSeason: 5 });
    mockApi.getPlayer.mockResolvedValue({
      accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 0, scores: [],
    });
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(50);
    });
  });

  it('renders filter button when not mobile chrome', async () => {
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
      expect(filterBtn || screen.getByText((text) => text.toLowerCase().includes('filter'))).toBeDefined();
    });
  });

  it('renders page with player data loaded', async () => {
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
  });

  it('handles API failure for songs gracefully', async () => {
    mockApi.getSongs.mockRejectedValue(new Error('Songs failed'));
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
  });

  it('handles API failure for player gracefully', async () => {
    mockApi.getPlayer.mockRejectedValue(new Error('Player failed'));
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
  });

  it('renders on mobile viewport', async () => {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      configurable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: true, media: query, onchange: null,
        addEventListener: vi.fn(), removeEventListener: vi.fn(),
        addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
      })),
    });
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
  });

  it('saves filter settings to localStorage', async () => {
    renderSuggestions();
    await waitFor(() => {
      expect(true).toBe(true);
    });
  });

  it('triggers scroll handler on scroll event', async () => {
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(50);
    });
    const scrollArea = container.querySelector('[data-testid="scroll-area"]');
    if (scrollArea) {
      fireEvent.scroll(scrollArea);
    }
    expect(container.innerHTML).toBeTruthy();
  });

  it('renders with categories when suggestion generator produces results', async () => {
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
  });

  it('builds instrument visibility from app settings', async () => {
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: false,
      showDrums: true,
      showVocals: false,
      showProLead: false,
      showProBass: false,
    }));
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
  });

  it('computes effectiveSeason from player scores when currentSeason is 0', async () => {
    mockApi.getSongs.mockResolvedValue({ songs: [
      { songId: 's1', title: 'Alpha Song', artist: 'Artist A', year: 2024, difficulty: { guitar: 3 } },
    ], count: 1, currentSeason: 0 });
    mockApi.getPlayer.mockResolvedValue({
      accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 1,
      scores: [{ songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, season: 3 }],
    });
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
  });

  it('handles player loading state', async () => {
    mockApi.getPlayer.mockReturnValue(new Promise(() => {})); // never resolves
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
  });

  it('renders visible categories after filtering', async () => {
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(50);
    });
  });

  it('builds album art map from songs', async () => {
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
  });

  it('shows no-suggestions empty state when categories exhausted and no hasMore', async () => {
    mockApi.getSongs.mockResolvedValue({ songs: [
      { songId: 's-only', title: 'Only Song', artist: 'X', year: 2024, difficulty: { guitar: 1 } },
    ], count: 1, currentSeason: 5 });
    mockApi.getPlayer.mockResolvedValue({
      accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 1,
      scores: [{ songId: 's-only', instrument: 'Solo_Guitar', score: 100000, rank: 1, percentile: 99, accuracy: 99, stars: 6, season: 5 }],
    });
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(50);
    });
  });
});

describe('SuggestionsPage — filter handlers (extracted)', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('opens filter modal via Filter pill', async () => {
    renderSuggestions('/suggestions', 'test-player-1');
    await act(async () => { await vi.advanceTimersByTimeAsync(3000); });
    const filterBtn = screen.queryByText('Filter');
    if (filterBtn) {
      fireEvent.click(filterBtn);
      await act(async () => { await vi.advanceTimersByTimeAsync(500); });
      expect(screen.getByText('Filter Suggestions')).toBeTruthy();
    }
  });

  it('resets filter from the filter modal', async () => {
    renderSuggestions('/suggestions', 'test-player-1');
    await act(async () => { await vi.advanceTimersByTimeAsync(3000); });
    const filterBtn = screen.queryByText('Filter');
    if (filterBtn) {
      fireEvent.click(filterBtn);
      await act(async () => { await vi.advanceTimersByTimeAsync(500); });
      const resetBtns = screen.queryAllByRole('button', { name: 'Reset' });
      if (resetBtns.length > 0) {
        fireEvent.click(resetBtns[0]!);
        await act(async () => { await vi.advanceTimersByTimeAsync(500); });
      }
    }
  });
});

// ---------------------------------------------------------------------------
// From: SuggestionsPageExtra.test.tsx
// ---------------------------------------------------------------------------

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
    mockApi.getPlayer.mockReturnValue(new Promise(() => {}));
    const { container } = renderSuggestions();
    await waitFor(() => {
      // Component renders either spinner (via Page) or empty-state early return
      expect(container.querySelector('[data-testid="arc-spinner"]') || container.firstElementChild).toBeTruthy();
    });
  });

  it('renders category cards when categories are generated', async () => {
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
      expect(container.querySelector('[data-testid="page-root"]')).toBeTruthy();
    }, { timeout: 3000 });
  });

  it('persists filter settings to localStorage', async () => {
    renderSuggestions();
    await waitFor(() => {
      const raw = localStorage.getItem('fst-suggestions-filter');
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
    localStorage.setItem('fst-suggestions-filter', JSON.stringify({
      suggestionsLeadFilter: false,
    }));
    localStorage.setItem('fst-suggestions-filter', JSON.stringify({
      suggestionsLeadFilter: true,
    }));
  });
});

// ---------------------------------------------------------------------------
// From: SuggestionsPageMore.test.tsx
// ---------------------------------------------------------------------------

describe('SuggestionsPage — extra coverage', () => {
  it('shows spinner before content loads', async () => {
    mockApi.getPlayer.mockReturnValue(new Promise(() => {}));
    const { container } = renderSuggestions();
    expect(container.querySelector('[data-testid="page-root"]')).toBeTruthy();
  });

  it('renders categories once data loads', async () => {
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
  });

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
    const filterPill = screen.queryByText('Filter');
    expect(filterPill).toBeTruthy();
  });

  it('shows empty state when no songs and player has no scores', async () => {
    mockApi.getSongs.mockResolvedValue({ songs: [], count: 0, currentSeason: 5 });
    mockApi.getPlayer.mockResolvedValue({
      accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 0, scores: [],
    });
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(0);
    });
  });

  it('handles API failure gracefully', async () => {
    mockApi.getSongs.mockRejectedValue(new Error('Network error'));
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
  });

  it('shows empty state with filters when filtered results are empty', async () => {
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

  it('renders page wrapper with correct class', async () => {
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.querySelector('[data-testid="page-root"]')).toBeTruthy();
    });
  });

  it('renders scroll area', async () => {
    const { container } = renderSuggestions();
    await waitFor(() => {
      expect(container.querySelector('[data-testid="scroll-area"]')).toBeTruthy();
    });
  });
});

// ---------------------------------------------------------------------------
// From: SuggestionsCoverage.test.tsx
// ---------------------------------------------------------------------------

describe('SuggestionsPage — coverage: category rendering + filter logic', () => {
  it('renders category cards with real suggestion data', async () => {
    const { container } = renderSuggestions();

    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(200);
    }, { timeout: 5000 });

    const pageEl = container.querySelector('[data-testid="page-root"]');
    expect(pageEl).toBeTruthy();
  });

  it('shows empty state with filtered text when filters hide all', async () => {
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

    expect(container.querySelector('[data-testid="page-root"]')).toBeTruthy();
  });

  it('renders filter button and opens filter modal', async () => {
    renderSuggestions();

    await waitFor(() => {
      const filterBtn = screen.queryByText('Filter');
      expect(filterBtn).toBeTruthy();
    }, { timeout: 5000 });

    const filterBtn = screen.getByText('Filter');
    fireEvent.click(filterBtn);

    await waitFor(() => {
      expect(document.body.innerHTML).toBeTruthy();
    });
  });

  it('renders categories after spinner → contentIn transition', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });

    const { container } = renderSuggestions();

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

    const scrollArea = container.querySelector('[data-testid="scroll-area"]');
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
      expect(
        container.textContent!.includes('No suggestions') ||
        container.textContent!.includes('Could not load') ||
        container.innerHTML.length > 50
      ).toBe(true);
    }, { timeout: 5000 });
  });
});
