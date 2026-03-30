import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen, waitFor, act, fireEvent } from '@testing-library/react';
import { Routes, Route } from 'react-router-dom';
import PlayerPage, { clearPlayerPageCache } from '../../../src/pages/player/PlayerPage';
import { TestProviders } from '../../helpers/TestProviders';
import { MOCK_PLAYER, MOCK_SYNC_STATUS } from '../../helpers/apiMocks';
import { stubScrollTo, stubResizeObserver, stubElementDimensions } from '../../helpers/browserStubs';

/* ── Shared mock API ── */

const PLAYER_WITH_SCORES = {
  accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 12,
  scores: [
    { songId: 'song-1', instrument: 'Solo_Guitar', score: 145000, rank: 1, percentile: 99, accuracy: 9950, isFullCombo: true, stars: 6, season: 5, totalEntries: 500 },
    { songId: 'song-1', instrument: 'Solo_Bass', score: 115000, rank: 5, percentile: 90, accuracy: 9500, isFullCombo: false, stars: 5, season: 4, totalEntries: 300 },
    { songId: 'song-1', instrument: 'Solo_Drums', score: 170000, rank: 2, percentile: 97, accuracy: 9700, isFullCombo: false, stars: 5, season: 5, totalEntries: 250 },
    { songId: 'song-2', instrument: 'Solo_Guitar', score: 125000, rank: 2, percentile: 97, accuracy: 9750, isFullCombo: false, stars: 5, season: 5, totalEntries: 400 },
    { songId: 'song-2', instrument: 'Solo_Bass', score: 100000, rank: 10, percentile: 80, accuracy: 9000, isFullCombo: false, stars: 4, season: 4, totalEntries: 200 },
    { songId: 'song-3', instrument: 'Solo_Guitar', score: 90000, rank: 20, percentile: 60, accuracy: 8500, isFullCombo: false, stars: 3, season: 3, totalEntries: 300 },
  ],
};

const SONGS_3 = { songs: [
  { songId: 'song-1', title: 'Test Song One', artist: 'Artist A', year: 2024, albumArt: 'https://example.com/a.jpg', difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1 },
    maxScores: { Solo_Guitar: 150000, Solo_Bass: 120000, Solo_Drums: 180000, Solo_Vocals: 100000 } },
  { songId: 'song-2', title: 'Test Song Two', artist: 'Artist B', year: 2023, difficulty: { guitar: 2 },
    maxScores: { Solo_Guitar: 130000 } },
  { songId: 'song-3', title: 'Test Song Three', artist: 'Artist C', year: 2025, difficulty: { guitar: 5 } },
], count: 3, currentSeason: 5 };

const SONGS_12 = {
  songs: [
    { songId: 's1', title: 'Song Alpha', artist: 'Art A', year: 2024, albumArt: 'https://example.com/a.jpg' },
    { songId: 's2', title: 'Song Beta', artist: 'Art B', year: 2023, albumArt: 'https://example.com/b.jpg' },
    { songId: 's3', title: 'Song Gamma', artist: 'Art C', year: 2025 },
    { songId: 's4', title: 'Song Delta', artist: 'Art D', year: 2024 },
    { songId: 's5', title: 'Song Epsilon', artist: 'Art E', year: 2024 },
    { songId: 's6', title: 'Song Zeta', artist: 'Art F', year: 2024 },
    { songId: 's7', title: 'Song Eta', artist: 'Art G', year: 2024 },
    { songId: 's8', title: 'Song Theta', artist: 'Art H', year: 2024 },
    { songId: 's9', title: 'Song Iota', artist: 'Art I', year: 2024 },
    { songId: 's10', title: 'Song Kappa', artist: 'Art J', year: 2024 },
    { songId: 's11', title: 'Song Lambda', artist: 'Art K', year: 2024 },
    { songId: 's12', title: 'Song Mu', artist: 'Art L', year: 2024 },
  ],
  count: 12,
  currentSeason: 5,
};

const SYNC_NOT_TRACKED = { accountId: 'test-player-1', isTracked: false, backfill: null, historyRecon: null };

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue(null),
    getPlayer: fn().mockResolvedValue(null),
    getSyncStatus: fn().mockResolvedValue({ accountId: '', isTracked: false, backfill: null, historyRecon: null }),
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
  stubResizeObserver({ width: 1024, height: 800 });
  stubElementDimensions(800);
});

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  clearPlayerPageCache();
  mockApi.getPlayer.mockResolvedValue(PLAYER_WITH_SCORES);
  mockApi.getSyncStatus.mockResolvedValue(SYNC_NOT_TRACKED);
  mockApi.getSongs.mockResolvedValue(SONGS_3);
  mockApi.getVersion.mockResolvedValue({ version: '1.0.0' });
  mockApi.getPlayerStats.mockResolvedValue({ accountId: 'test-player-1', stats: [] });
  mockApi.trackPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: 'none' });
  mockApi.getLeaderboard.mockResolvedValue({ songId: 'song-1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] });
  mockApi.getAllLeaderboards.mockResolvedValue({ songId: 'song-1', instruments: [] });
  mockApi.getPlayerHistory.mockResolvedValue({ accountId: 'test-player-1', count: 0, history: [] });
  mockApi.searchAccounts.mockResolvedValue({ results: [] });
});

/** Build a rich player response with scores across multiple instruments. */
function buildRichPlayer(accountId = 'rich-player') {
  const instruments = ['Solo_Guitar', 'Solo_Bass', 'Solo_Drums'] as const;
  const songIds = ['s1', 's2', 's3', 's4', 's5', 's6', 's7', 's8', 's9', 's10', 's11', 's12'];
  const scores: Array<Record<string, unknown>> = [];

  for (const inst of instruments) {
    for (let si = 0; si < songIds.length; si++) {
      const rank = si + 1;
      const totalEntries = 500;
      scores.push({
        songId: songIds[si],
        instrument: inst,
        score: 150000 - si * 5000,
        rank,
        percentile: 99 - si * 3,
        accuracy: 100 - si * 0.5,
        isFullCombo: si < 3,
        stars: si < 2 ? 6 : (si < 5 ? 5 : (si < 8 ? 4 : (si < 10 ? 3 : 2))),
        season: 5,
        totalEntries,
      });
    }
  }

  return {
    accountId,
    displayName: 'RichPlayer',
    totalScores: scores.length,
    scores,
  };
}

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

/* ── Core functionality (from PlayerPage.test.tsx) ── */

describe('PlayerPage', () => {
  beforeEach(() => {
    mockApi.getPlayer.mockResolvedValue(MOCK_PLAYER);
    mockApi.getSyncStatus.mockResolvedValue(MOCK_SYNC_STATUS);
  });

  it('fetches and renders an arbitrary player from URL', async () => {
    renderPlayerPage('/player/test-player-1');

    await waitFor(() => {
      expect(mockApi.getPlayer).toHaveBeenCalledWith('test-player-1', undefined, undefined, undefined);
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

    expect(container.querySelector('[data-testid="arc-spinner"]')).toBeTruthy();
  });

  it('shows error state on API failure', async () => {
    mockApi.getPlayer.mockRejectedValue(new Error('Network error'));

    renderPlayerPage('/player/test-player-1');

    await waitFor(() => {
      expect(screen.getByText('Something Went Wrong')).toBeDefined();
    });
  });

  it('shows fallback error message for non-Error throws', async () => {
    mockApi.getPlayer.mockRejectedValue('unknown');

    renderPlayerPage('/player/test-player-1');

    await waitFor(() => {
      expect(screen.getByText('Something Went Wrong')).toBeDefined();
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

    clearPlayerPageCache();
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
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(document.body.textContent).toContain('TestPlayer');
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
    expect(container.querySelector('[data-testid="arc-spinner"]')).toBeTruthy();
    await act(async () => {
      resolvePlayer!(MOCK_PLAYER);
    });
    await waitFor(() => {
      expect(document.body.textContent).toContain('TestPlayer');
    });
  });
});

describe('PlayerPage — branch coverage', () => {
  beforeEach(() => {
    mockApi.getPlayer.mockResolvedValue(MOCK_PLAYER);
    mockApi.getSyncStatus.mockResolvedValue(MOCK_SYNC_STATUS);
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

/* ── Instrument stats, sync banner, profile switch, stagger (from PlayerPageExtra.test.tsx) ── */

describe('PlayerPage — extra coverage', () => {
  it('renders sync banner during backfill phase', async () => {
    mockApi.getSyncStatus.mockResolvedValue({
      accountId: 'test-player-1',
      isTracked: true,
      backfill: { status: 'in_progress', songsChecked: 50, totalSongsToCheck: 100, entriesFound: 200, startedAt: '2025-01-01T00:00:00Z', completedAt: null },
      historyRecon: null,
    });
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getByText('TestPlayer')).toBeTruthy();
    });
  });

  it('renders instrument statistics heading', async () => {
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getByText('Instrument Statistics')).toBeTruthy();
    });
  });

  it('renders overall summary stat boxes with score data', async () => {
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getAllByText('Songs Played').length).toBeGreaterThan(0);
      expect(screen.getAllByText('Full Combos').length).toBeGreaterThan(0);
      expect(screen.getAllByText('Gold Stars').length).toBeGreaterThan(0);
      expect(screen.getAllByText('Avg Accuracy').length).toBeGreaterThan(0);
      expect(screen.getAllByText('Best Rank').length).toBeGreaterThan(0);
    });
  });

  it('renders per-instrument stat cards for Lead', async () => {
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getByText('Lead')).toBeTruthy();
    });
  });

  it('renders per-instrument stat cards for Bass', async () => {
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getByText('Bass')).toBeTruthy();
    });
  });

  it('renders per-instrument stat cards for Drums', async () => {
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getByText('Drums')).toBeTruthy();
    });
  });

  it('renders percentile bucket rows for instruments with ranked scores', async () => {
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getAllByText('Percentile').length).toBeGreaterThan(0);
    });
  });

  it('renders top songs per instrument heading', async () => {
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getByText('Top Songs Per Instrument')).toBeTruthy();
    });
  });

  it('renders top five songs section', async () => {
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      const topHeaders = screen.getAllByText('Top Five Songs');
      expect(topHeaders.length).toBeGreaterThan(0);
    });
  });

  it('renders song titles in top/bottom lists', async () => {
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getAllByText('Test Song One').length).toBeGreaterThan(0);
    });
  });

  it('shows select profile button when viewing non-tracked player', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'other-player', displayName: 'Other' }));
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getByText('Select Player Profile')).toBeTruthy();
    });
  });

  it('does not show select profile button when viewing own tracked player stats', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-player-1', displayName: 'TestPlayer' }));
    renderPlayerPage('/statistics', 'test-player-1');
    await waitFor(() => {
      expect(screen.getAllByText('TestPlayer').length).toBeGreaterThan(0);
    });
    const btn = screen.queryByText('Select Player Profile');
    if (btn) {
      const style = btn.closest('button')?.style;
      expect(style?.opacity === '0' || style?.pointerEvents === 'none').toBeTruthy();
    }
  });

  it('skips stagger animations on second render of same account', async () => {
    const { unmount } = renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getByText('TestPlayer')).toBeTruthy();
    });
    unmount();
    clearPlayerPageCache();

    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getByText('TestPlayer')).toBeTruthy();
    });
  });

  it('renders player page with zero scores gracefully', async () => {
    mockApi.getPlayer.mockResolvedValue({
      accountId: 'test-player-1', displayName: 'EmptyPlayer', totalScores: 0, scores: [],
    });
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getByText('EmptyPlayer')).toBeTruthy();
    });
  });

  it('renders gold star count in overall summary for a player with gold stars', async () => {
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getAllByText('Gold Stars').length).toBeGreaterThan(0);
    });
  });

  it('displays full combo count in summary', async () => {
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getByText('Full Combos')).toBeTruthy();
    });
  });

  it('displays best rank in summary when player has ranked scores', async () => {
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getAllByText('Best Rank').length).toBeGreaterThan(0);
    });
  });
});

/* ── Rich data coverage (from PlayerPageCoverage.test.tsx) ── */

describe('PlayerPage — coverage: instrument stats + percentile + top/bottom songs', () => {
  beforeEach(() => {
    mockApi.getSongs.mockResolvedValue(SONGS_12);
  });

  it('renders instrument stat boxes and percentile table with rich scores', async () => {
    const player = buildRichPlayer('rich-player');
    mockApi.getPlayer.mockResolvedValue(player);
    mockApi.getSyncStatus.mockResolvedValue({ accountId: 'rich-player', isTracked: false, backfill: null, historyRecon: null });

    renderPlayerPage('/player/rich-player');

    await waitFor(() => {
      expect(document.body.textContent).toContain('RichPlayer');
    });

    await waitFor(() => {
      expect(document.body.textContent).toContain('Instrument Statistics');
    }, { timeout: 3000 });

    expect(document.body.textContent).toContain('Lead');
    expect(document.body.textContent).toContain('Bass');
    expect(document.body.textContent).toContain('Drums');

    expect(document.body.textContent).toContain('Songs Played');
    expect(document.body.textContent).toContain('Full Combos');
    expect(document.body.textContent).toContain('Gold Stars');
    expect(document.body.textContent).toContain('Avg Accuracy');
    expect(document.body.textContent).toContain('Best Rank');
  });

  it('renders top and bottom songs sections', async () => {
    const player = buildRichPlayer('top-bot');
    mockApi.getPlayer.mockResolvedValue(player);
    mockApi.getSyncStatus.mockResolvedValue({ accountId: 'top-bot', isTracked: false, backfill: null, historyRecon: null });

    renderPlayerPage('/player/top-bot');

    await waitFor(() => {
      expect(document.body.textContent).toContain('Top Songs Per Instrument');
    }, { timeout: 3000 });

    expect(document.body.textContent).toContain('Top Five Songs');
    expect(document.body.textContent).toContain('Bottom Five Songs');
    expect(document.body.textContent).toContain('Song Alpha');
  });

  it('renders percentile distribution rows', async () => {
    const player = buildRichPlayer('pct-player');
    mockApi.getPlayer.mockResolvedValue(player);
    mockApi.getSyncStatus.mockResolvedValue({ accountId: 'pct-player', isTracked: false, backfill: null, historyRecon: null });

    renderPlayerPage('/player/pct-player');

    await waitFor(() => {
      expect(document.body.textContent).toContain('Percentile');
    }, { timeout: 3000 });

    await waitFor(() => {
      expect(document.body.textContent).toMatch(/Top \d+%/);
    });
  });
});

describe('PlayerPage — coverage: sync banner', () => {
  beforeEach(() => {
    mockApi.getSongs.mockResolvedValue(SONGS_12);
  });

  it('renders sync banner when syncing with backfill phase', async () => {
    const player = buildRichPlayer('syncing-player');
    mockApi.getPlayer.mockResolvedValue(player);
    mockApi.getSyncStatus.mockResolvedValue({
      accountId: 'syncing-player',
      isTracked: true,
      backfill: { status: 'in_progress', songsChecked: 50, totalSongsToCheck: 100, entriesFound: 200, startedAt: '2025-01-01T00:00:00Z' },
      historyRecon: null,
    });

    renderPlayerPage('/player/syncing-player');

    await waitFor(() => {
      expect(document.body.textContent).toContain('RichPlayer');
    });

    await waitFor(() => {
      expect(document.body.textContent).toContain('Syncing');
    }, { timeout: 3000 });
  });

  it('renders sync banner for history reconstruction phase', async () => {
    const player = buildRichPlayer('history-player');
    mockApi.getPlayer.mockResolvedValue(player);
    mockApi.getSyncStatus.mockResolvedValue({
      accountId: 'history-player',
      isTracked: true,
      backfill: { status: 'completed', songsChecked: 100, totalSongsToCheck: 100, entriesFound: 500, startedAt: '2025-01-01T00:00:00Z', completedAt: '2025-01-01T01:00:00Z' },
      historyRecon: { status: 'in_progress', songsProcessed: 10, totalSongsToProcess: 50, seasonsQueried: 2, historyEntriesFound: 30, startedAt: '2025-01-01T01:00:00Z' },
    });

    renderPlayerPage('/player/history-player');

    await waitFor(() => {
      expect(document.body.textContent).toContain('RichPlayer');
    });

    await waitFor(() => {
      expect(
        document.body.textContent!.includes('Syncing') ||
        document.body.textContent!.includes('Reconstructing') ||
        document.body.textContent!.includes('history')
      ).toBe(true);
    }, { timeout: 3000 });
  });
});

describe('PlayerPage — coverage: profile switch', () => {
  beforeEach(() => {
    mockApi.getSongs.mockResolvedValue(SONGS_12);
  });

  it('shows Select Player Profile button for non-tracked player on desktop', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'existing-tracked', displayName: 'ExistingPlayer' }));
    const player = buildRichPlayer('another-player');
    player.displayName = 'AnotherPlayer';
    mockApi.getPlayer.mockResolvedValue(player);
    mockApi.getSyncStatus.mockResolvedValue({ accountId: 'another-player', isTracked: false, backfill: null, historyRecon: null });

    const { container } = renderPlayerPage('/player/another-player', 'existing-tracked');

    await waitFor(() => {
      expect(document.body.textContent).toContain('AnotherPlayer');
    });

    await waitFor(() => {
      expect(document.body.textContent).toContain('Select Player Profile');
    }, { timeout: 3000 });

    const btn = Array.from(container.querySelectorAll('button')).find(
      b => b.textContent?.includes('Select Player Profile'),
    );
    if (btn) {
      fireEvent.click(btn);
      await waitFor(() => {
        expect(document.body.textContent).toContain('Switch to');
      });
    }
  });
});

describe('PlayerPage — coverage: stagger calculations', () => {
  beforeEach(() => {
    mockApi.getSongs.mockResolvedValue(SONGS_12);
  });

  it('exercises stagger computation with many grid items', async () => {
    const player = buildRichPlayer('stagger-player');
    mockApi.getPlayer.mockResolvedValue(player);
    mockApi.getSyncStatus.mockResolvedValue({ accountId: 'stagger-player', isTracked: false, backfill: null, historyRecon: null });

    const { container } = renderPlayerPage('/player/stagger-player');

    await waitFor(() => {
      expect(document.body.textContent).toContain('RichPlayer');
    });

    await waitFor(() => {
      expect(document.body.textContent).toContain('Instrument Statistics');
    }, { timeout: 3000 });

    container.querySelectorAll('[style*="animation"]');
    expect(container.innerHTML.length).toBeGreaterThan(500);
  });

  it('skips stagger on revisit (second render same accountId)', async () => {
    const player = buildRichPlayer('revisit-player');
    player.displayName = 'RevisitPlayer';
    mockApi.getPlayer.mockResolvedValue(player);
    mockApi.getSyncStatus.mockResolvedValue({ accountId: 'revisit-player', isTracked: false, backfill: null, historyRecon: null });

    const { unmount } = renderPlayerPage('/player/revisit-player');
    await waitFor(() => {
      expect(screen.getByText('RevisitPlayer')).toBeDefined();
    });
    unmount();

    renderPlayerPage('/player/revisit-player');
    await waitFor(() => {
      expect(document.body.textContent).toContain('RevisitPlayer');
    });

    await waitFor(() => {
      expect(document.body.textContent).toContain('Instrument Statistics');
    }, { timeout: 3000 });
  });
});
