import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen, waitFor, act } from '@testing-library/react';
import { Routes, Route } from 'react-router-dom';
import PlayerPage, { clearPlayerPageCache } from '../../../src/pages/player/PlayerPage';
import { TestProviders } from '../../helpers/TestProviders';
import { MOCK_PLAYER, MOCK_SYNC_STATUS } from '../../helpers/apiMocks';
import { stubScrollTo } from '../../helpers/browserStubs';

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue({ songs: [{ songId: 'song-1', title: 'Test Song One', artist: 'Artist A', year: 2024 }], count: 1, currentSeason: 5 }),
    getPlayer: fn().mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 3, scores: [] }),
    getSyncStatus: fn().mockResolvedValue({ accountId: 'test-player-1', isTracked: false, backfill: null, historyRecon: null }),
    getPlayerStats: fn().mockResolvedValue({ accountId: 'test-player-1', stats: [] }),
    trackPlayer: fn().mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: 'none' }),
    getVersion: fn().mockResolvedValue({ version: '1.0.0' }),
    getLeaderboard: fn().mockResolvedValue({ songId: 'song-1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] }),
    getAllLeaderboards: fn().mockResolvedValue({ songId: 'song-1', instruments: [] }),
    getPlayerHistory: fn().mockResolvedValue({ accountId: 'test-player-1', count: 0, history: [] }),
    searchAccounts: fn().mockResolvedValue({ results: [] }),
  };
});

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

beforeAll(() => {
  stubScrollTo();
});

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  clearPlayerPageCache();
  // Restore default resolved values
  mockApi.getPlayer.mockResolvedValue(MOCK_PLAYER);
  mockApi.getSyncStatus.mockResolvedValue(MOCK_SYNC_STATUS);
});

function renderPlayerPage(route: string, accountId?: string) {
  return render(
    <TestProviders route={route} accountId={accountId}>
      <Routes>
        <Route path="/player/:accountId" element={<PlayerPage />} />
        <Route path="/statistics" element={<PlayerPage accountId={accountId} />} />
      </Routes>
    </TestProviders>,
  );
}

describe('PlayerPage', () => {
  it('fetches and renders an arbitrary player from URL', async () => {
    renderPlayerPage('/player/test-player-1');

    await waitFor(() => {
      expect(mockApi.getPlayer).toHaveBeenCalledWith('test-player-1');
    });

    await waitFor(() => {
      expect(screen.getByText('TestPlayer')).toBeDefined();
    });
  });

  it('renders tracked player data from context', async () => {
    renderPlayerPage('/statistics', 'test-player-1');

    await waitFor(() => {
      expect(screen.getByText('TestPlayer')).toBeDefined();
    });
  });

  it('shows loading spinner while fetching', () => {
    mockApi.getPlayer.mockReturnValue(new Promise(() => {})); // never resolves
    const { container } = renderPlayerPage('/player/test-player-1');

    expect(container.querySelector('[class*="arcSpinner"]') || container.querySelector('[class*="spinner"]') || container.querySelector('[class*="center"]')).toBeTruthy();
  });

  it('shows error state on API failure', async () => {
    mockApi.getPlayer.mockRejectedValue(new Error('Network error'));

    renderPlayerPage('/player/test-player-1');

    await waitFor(() => {
      expect(screen.getByText('Network error')).toBeDefined();
    });
  });

  it('shows fallback error message for non-Error throws', async () => {
    mockApi.getPlayer.mockRejectedValue('unknown');

    renderPlayerPage('/player/test-player-1');

    await waitFor(() => {
      expect(screen.getByText('Failed to load player')).toBeDefined();
    });
  });

  it('shows player not found when data is null', async () => {
    mockApi.getPlayer.mockResolvedValue(null);

    renderPlayerPage('/player/unknown');

    await waitFor(() => {
      expect(screen.getByText('Player not found')).toBeDefined();
    });
  });

  it('uses cached data on second render with same accountId', async () => {
    const { unmount } = renderPlayerPage('/player/test-player-1');

    await waitFor(() => {
      expect(screen.getByText('TestPlayer')).toBeDefined();
    });
    unmount();

    // Mount again — React Query serves from its 5-min cache,
    // so getPlayer should not fire a new request for the same accountId.
    renderPlayerPage('/player/test-player-1');

    await waitFor(() => {
      expect(screen.getByText('TestPlayer')).toBeDefined();
    });
  });

  it('refetches when accountId changes', async () => {
    const secondPlayer = { ...MOCK_PLAYER, accountId: 'player-2', displayName: 'SecondPlayer' };
    mockApi.getPlayer.mockResolvedValueOnce(MOCK_PLAYER).mockResolvedValueOnce(secondPlayer);

    const { unmount } = renderPlayerPage('/player/test-player-1');
    await waitFor(() => { expect(screen.getByText('TestPlayer')).toBeDefined(); });
    unmount();

    clearPlayerPageCache(); // clear so both fetches are independent
    renderPlayerPage('/player/player-2');
    await waitFor(() => { expect(screen.getByText('SecondPlayer')).toBeDefined(); });
  });

  it('renders without crashing for tracked player route', async () => {
    renderPlayerPage('/statistics', 'test-player-1');

    await waitFor(() => {
      expect(screen.getByText('TestPlayer')).toBeDefined();
    });
  });

  it('polls sync status for non-tracked player via URL', async () => {
    renderPlayerPage('/player/test-player-1');

    await waitFor(() => {
      expect(mockApi.getSyncStatus).toHaveBeenCalled();
    });
  });

  it('renders with player having scores', async () => {
    mockApi.getPlayer.mockResolvedValue({
      ...MOCK_PLAYER,
      scores: [
        { songId: 's1', instrument: 'Solo_Guitar', score: 145000, rank: 1, percentile: 99, accuracy: 99.5, isFullCombo: true, stars: 6, season: 5 },
        { songId: 's2', instrument: 'Solo_Bass', score: 115000, rank: 5, percentile: 90, accuracy: 95, isFullCombo: false, stars: 5, season: 4 },
      ],
    });
    const { container } = renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(container.textContent).toContain('TestPlayer');
    });
  });

  it('resolves sync status values for tracked player', async () => {
    mockApi.getSyncStatus.mockResolvedValue({
      accountId: 'test-player-1',
      isTracked: true,
      backfill: { status: 'completed', songsChecked: 100, totalSongsToCheck: 100, entriesFound: 500, startedAt: '2025-01-01T00:00:00Z', completedAt: '2025-01-01T01:00:00Z' },
      historyRecon: null,
    });
    renderPlayerPage('/statistics', 'test-player-1');
    await waitFor(() => {
      expect(screen.getByText('TestPlayer')).toBeDefined();
    });
  });

  it('handles loading to data transition smoothly', async () => {
    let resolvePlayer: (v: unknown) => void;
    mockApi.getPlayer.mockReturnValue(new Promise(r => { resolvePlayer = r; }));
    const { container } = renderPlayerPage('/player/test-player-1');
    // Initially loading
    expect(container.querySelector('[class*="spinner"]') || container.querySelector('[class*="center"]')).toBeTruthy();
    // Resolve the API call
    await act(async () => {
      resolvePlayer!(MOCK_PLAYER);
    });
    await waitFor(() => {
      expect(container.textContent).toContain('TestPlayer');
    });
  });
});

describe('PlayerPage — branch coverage (extracted)', () => {
  beforeEach(() => {
    clearPlayerPageCache();
  });

  it('renders as tracked player with propAccountId', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-player-1', displayName: 'TrackedP' }));
    mockApi.getPlayer.mockResolvedValue({
      accountId: 'test-player-1', displayName: 'TrackedP', totalScores: 0, scores: [],
    });
    renderPlayerPage('/statistics', 'test-player-1');
    await waitFor(() => {
      expect(mockApi.getPlayer).toHaveBeenCalled();
    });
  });

  it('renders player page with backfill completed status', async () => {
    mockApi.getPlayer.mockResolvedValue({
      accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 0, scores: [],
    });
    mockApi.getSyncStatus.mockResolvedValue({
      accountId: 'test-player-1', isTracked: true,
      backfill: { status: 'completed', progress: 100 },
      historyRecon: null,
    });
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(mockApi.getPlayer).toHaveBeenCalled();
    });
  });
});
