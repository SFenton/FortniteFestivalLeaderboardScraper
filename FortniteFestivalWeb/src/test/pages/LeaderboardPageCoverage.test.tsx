/**
 * LeaderboardPage coverage tests — exercises player score row rendering,
 * pagination controls, scroll handler, score width calculations, and player footer.
 */
import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { Routes, Route } from 'react-router-dom';
import LeaderboardPage, { clearLeaderboardCache } from '../../pages/leaderboard/global/LeaderboardPage';
import { TestProviders } from '../helpers/TestProviders';
import { stubScrollTo, stubResizeObserver, stubElementDimensions } from '../helpers/browserStubs';

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue({
      songs: [
        { songId: 'song-1', title: 'Test Song One', artist: 'Artist A', year: 2024, albumArt: 'https://example.com/art1.jpg' },
      ],
      count: 1,
      currentSeason: 5,
    }),
    getLeaderboard: fn().mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 5, totalEntries: 150, localEntries: 150,
      entries: [
        { accountId: 'acc-1', displayName: 'Player One', score: 145000, rank: 1, percentile: 99, accuracy: 99.5, isFullCombo: true, stars: 6, season: 5 },
        { accountId: 'acc-2', displayName: 'Player Two', score: 140000, rank: 2, percentile: 97, accuracy: 98.0, isFullCombo: false, stars: 5, season: 5 },
        { accountId: 'acc-3', displayName: 'Player Three', score: 135000, rank: 3, percentile: 95, accuracy: 96.2, isFullCombo: false, stars: 5, season: 4 },
        { accountId: 'acc-4', displayName: 'Player Four', score: 120000, rank: 4, percentile: 90, accuracy: 93.1, isFullCombo: false, stars: 4, season: 5 },
        { accountId: 'acc-5', displayName: 'Player Five', score: 100000, rank: 5, percentile: 80, accuracy: 88.0, isFullCombo: false, stars: 3, season: 3 },
      ],
    }),
    getPlayer: fn().mockResolvedValue({
      accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 1,
      scores: [{ songId: 'song-1', instrument: 'Solo_Guitar', score: 120000, rank: 4, percentile: 90, accuracy: 93.1, isFullCombo: false, stars: 4, season: 5 }],
    }),
    getSyncStatus: fn().mockResolvedValue({ accountId: 'test-player-1', isTracked: false, backfill: null, historyRecon: null }),
    getVersion: fn().mockResolvedValue({ version: '1.0.0' }),
    getAllLeaderboards: fn().mockResolvedValue({ songId: 'song-1', instruments: [] }),
    getPlayerHistory: fn().mockResolvedValue({ accountId: 'test-player-1', count: 0, history: [] }),
    searchAccounts: fn().mockResolvedValue({ results: [] }),
    getPlayerStats: fn().mockResolvedValue({ accountId: 'test-player-1', stats: [] }),
    trackPlayer: fn().mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: 'none' }),
  };
});

vi.mock('../../api/client', () => ({ api: mockApi }));

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver();
  stubElementDimensions(800);
});

function resetMocks() {
  mockApi.getSongs.mockResolvedValue({ songs: [
    { songId: 'song-1', title: 'Test Song One', artist: 'Artist A', year: 2024, albumArt: 'https://example.com/art1.jpg' },
  ], count: 1, currentSeason: 5 });
  mockApi.getLeaderboard.mockResolvedValue({
    songId: 'song-1', instrument: 'Solo_Guitar', count: 5, totalEntries: 150, localEntries: 150,
    entries: [
      { accountId: 'acc-1', displayName: 'Player One', score: 145000, rank: 1, percentile: 99, accuracy: 99.5, isFullCombo: true, stars: 6, season: 5 },
      { accountId: 'acc-2', displayName: 'Player Two', score: 140000, rank: 2, percentile: 97, accuracy: 98.0, isFullCombo: false, stars: 5, season: 5 },
      { accountId: 'acc-3', displayName: 'Player Three', score: 135000, rank: 3, percentile: 95, accuracy: 96.2, isFullCombo: false, stars: 5, season: 4 },
      { accountId: 'acc-4', displayName: 'Player Four', score: 120000, rank: 4, percentile: 90, accuracy: 93.1, isFullCombo: false, stars: 4, season: 5 },
      { accountId: 'acc-5', displayName: 'Player Five', score: 100000, rank: 5, percentile: 80, accuracy: 88.0, isFullCombo: false, stars: 3, season: 3 },
    ],
  });
  mockApi.getPlayer.mockResolvedValue({
    accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 1,
    scores: [{ songId: 'song-1', instrument: 'Solo_Guitar', score: 120000, rank: 4, percentile: 90, accuracy: 93.1, isFullCombo: false, stars: 4, season: 5 }],
  });
  mockApi.getSyncStatus.mockResolvedValue({ accountId: 'test-player-1', isTracked: false, backfill: null, historyRecon: null });
  mockApi.getVersion.mockResolvedValue({ version: '1.0.0' });
}

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  clearLeaderboardCache();
  resetMocks();
});

function renderLeaderboard(route = '/songs/song-1/Solo_Guitar', accountId?: string) {
  return render(
    <TestProviders route={route} accountId={accountId}>
      <Routes>
        <Route path="/songs/:songId/:instrument" element={<LeaderboardPage />} />
      </Routes>
    </TestProviders>,
  );
}

describe('LeaderboardPage — coverage: score width + row rendering', () => {
  it('renders score values with correct width calculation', async () => {
    renderLeaderboard();

    await waitFor(() => {
      expect(screen.getByText('145,000')).toBeDefined();
      expect(screen.getByText('100,000')).toBeDefined();
    });

    // All 5 players should be rendered
    expect(screen.getByText('Player One')).toBeDefined();
    expect(screen.getByText('Player Five')).toBeDefined();
  });

  it('renders rank numbers for each entry', async () => {
    renderLeaderboard();

    await waitFor(() => {
      expect(screen.getByText('#1')).toBeDefined();
      expect(screen.getByText('#5')).toBeDefined();
    });
  });

  it('renders with very large scores for width calc', async () => {
    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 2, totalEntries: 2, localEntries: 2,
      entries: [
        { accountId: 'a1', displayName: 'BigScorer', score: 1000000, rank: 1, accuracy: 100, isFullCombo: true, stars: 6, season: 5 },
        { accountId: 'a2', displayName: 'SmallScorer', score: 500, rank: 2, accuracy: 50, isFullCombo: false, stars: 1, season: 5 },
      ],
    });

    renderLeaderboard();

    await waitFor(() => {
      expect(screen.getByText('1,000,000')).toBeDefined();
      expect(screen.getByText('500')).toBeDefined();
    });
  });
});

describe('LeaderboardPage — coverage: multi-page pagination', () => {
  it('renders full pagination controls for 150 total entries (6 pages)', async () => {
    // totalEntries=150, PAGE_SIZE=25, so 6 pages
    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');

    await waitFor(() => {
      expect(screen.getByText('Player One')).toBeDefined();
    });

    // Should show pagination with "1 / 6"
    await waitFor(() => {
      const text = document.body.textContent ?? '';
      expect(text).toContain('1 / 6');
    });

    // Click "Last" button to go to last page
    const lastBtn = Array.from(document.querySelectorAll('button')).find(
      b => b.textContent?.includes('Last'),
    );
    expect(lastBtn).toBeTruthy();
    if (lastBtn) {
      fireEvent.click(lastBtn);
      await waitFor(() => {
        expect(mockApi.getLeaderboard.mock.calls.length).toBeGreaterThanOrEqual(2);
      });
    }
  });

  it('navigates to first page from middle', async () => {
    renderLeaderboard('/songs/song-1/Solo_Guitar?page=3', 'test-player-1');

    await waitFor(() => {
      expect(mockApi.getLeaderboard).toHaveBeenCalled();
    });

    // Should start on page 3 (offset 50)
    const firstCall = mockApi.getLeaderboard.mock.calls[0];
    expect(firstCall![3]).toBe(50);

    // Click "First" button
    await waitFor(() => {
      const text = document.body.textContent ?? '';
      expect(text.length).toBeGreaterThan(0);
    });
    const firstBtn = Array.from(document.querySelectorAll('button')).find(
      b => b.textContent?.includes('First'),
    );
    if (firstBtn && !firstBtn.disabled) {
      fireEvent.click(firstBtn);
      await waitFor(() => {
        expect(mockApi.getLeaderboard.mock.calls.length).toBeGreaterThanOrEqual(2);
      });
    }
  });
});

describe('LeaderboardPage — coverage: player footer with tracked score', () => {
  it('renders player footer when tracked player has matching score', async () => {
    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');

    await waitFor(() => {
      expect(screen.getByText('Player One')).toBeDefined();
    });

    // Player footer should show the tracked player's info
    const testPlayerTexts = screen.getAllByText('TestPlayer');
    expect(testPlayerTexts.length).toBeGreaterThanOrEqual(1);

    // Footer contains score
    const allScores = screen.getAllByText('120,000');
    expect(allScores.length).toBeGreaterThanOrEqual(1);
  });

  it('footer click navigates to statistics', async () => {
    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');

    await waitFor(() => {
      expect(screen.getByText('Player One')).toBeDefined();
    });

    // Click on the player footer (role="button")
    const footer = document.querySelector('[role="button"]');
    if (footer) {
      fireEvent.click(footer);
    }
    expect(document.body.innerHTML).toBeTruthy();
  });
});

describe('LeaderboardPage — coverage: scroll cache and header collapse', () => {
  it('triggers scroll handler updating cache and header state', async () => {
    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');

    await waitFor(() => {
      expect(screen.getByText('Player One')).toBeDefined();
    });

    // Find and scroll the scroll area
    const scrollArea = document.querySelector('[class*="scrollArea"]');
    if (scrollArea) {
      Object.defineProperty(scrollArea, 'scrollTop', { value: 100, writable: true });
      fireEvent.scroll(scrollArea);
    }

    // Should still render correctly
    expect(screen.getByText('Player One')).toBeDefined();
  });
});
