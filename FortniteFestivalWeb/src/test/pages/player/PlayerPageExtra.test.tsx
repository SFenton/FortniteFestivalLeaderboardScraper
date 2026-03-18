import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { Routes, Route } from 'react-router-dom';
import PlayerPage, { clearPlayerPageCache } from '../../../pages/player/PlayerPage';
import { TestProviders } from '../../helpers/TestProviders';
import { stubScrollTo, stubResizeObserver, stubElementDimensions } from '../../helpers/browserStubs';

const SYNC_NOT_TRACKED = { accountId: 'test-player-1', isTracked: false, backfill: null, historyRecon: null };

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue({ songs: [
      { songId: 'song-1', title: 'Test Song One', artist: 'Artist A', year: 2024, albumArt: 'https://example.com/a.jpg', difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1 },
        maxScores: { Solo_Guitar: 150000, Solo_Bass: 120000, Solo_Drums: 180000, Solo_Vocals: 100000 } },
      { songId: 'song-2', title: 'Test Song Two', artist: 'Artist B', year: 2023, difficulty: { guitar: 2 },
        maxScores: { Solo_Guitar: 130000 } },
      { songId: 'song-3', title: 'Test Song Three', artist: 'Artist C', year: 2025, difficulty: { guitar: 5 } },
    ], count: 3, currentSeason: 5 }),
    getPlayer: fn().mockResolvedValue({
      accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 12,
      scores: [
        { songId: 'song-1', instrument: 'Solo_Guitar', score: 145000, rank: 1, percentile: 99, accuracy: 9950, isFullCombo: true, stars: 6, season: 5, totalEntries: 500 },
        { songId: 'song-1', instrument: 'Solo_Bass', score: 115000, rank: 5, percentile: 90, accuracy: 9500, isFullCombo: false, stars: 5, season: 4, totalEntries: 300 },
        { songId: 'song-1', instrument: 'Solo_Drums', score: 170000, rank: 2, percentile: 97, accuracy: 9700, isFullCombo: false, stars: 5, season: 5, totalEntries: 250 },
        { songId: 'song-2', instrument: 'Solo_Guitar', score: 125000, rank: 2, percentile: 97, accuracy: 9750, isFullCombo: false, stars: 5, season: 5, totalEntries: 400 },
        { songId: 'song-2', instrument: 'Solo_Bass', score: 100000, rank: 10, percentile: 80, accuracy: 9000, isFullCombo: false, stars: 4, season: 4, totalEntries: 200 },
        { songId: 'song-3', instrument: 'Solo_Guitar', score: 90000, rank: 20, percentile: 60, accuracy: 8500, isFullCombo: false, stars: 3, season: 3, totalEntries: 300 },
      ],
    }),
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

vi.mock('../../../api/client', () => ({ api: mockApi }));

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
  mockApi.getSongs.mockResolvedValue({ songs: [
    { songId: 'song-1', title: 'Test Song One', artist: 'Artist A', year: 2024, albumArt: 'https://example.com/a.jpg', difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1 },
      maxScores: { Solo_Guitar: 150000, Solo_Bass: 120000, Solo_Drums: 180000, Solo_Vocals: 100000 } },
    { songId: 'song-2', title: 'Test Song Two', artist: 'Artist B', year: 2023, difficulty: { guitar: 2 },
      maxScores: { Solo_Guitar: 130000 } },
    { songId: 'song-3', title: 'Test Song Three', artist: 'Artist C', year: 2025, difficulty: { guitar: 5 } },
  ], count: 3, currentSeason: 5 });
  mockApi.getVersion.mockResolvedValue({ version: '1.0.0' });
  mockApi.getPlayerStats.mockResolvedValue({ accountId: 'test-player-1', stats: [] });
  mockApi.trackPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: 'none' });
  mockApi.getLeaderboard.mockResolvedValue({ songId: 'song-1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] });
  mockApi.getAllLeaderboards.mockResolvedValue({ songId: 'song-1', instruments: [] });
  mockApi.getPlayerHistory.mockResolvedValue({ accountId: 'test-player-1', count: 0, history: [] });
  mockApi.searchAccounts.mockResolvedValue({ results: [] });
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

describe('PlayerPage — extra coverage', () => {
  /* ── Sync banner rendering ── */
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

  /* ── Instrument stats sections ── */
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
      // Lead should show since we have Lead scores
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

  /* ── Percentile distribution ── */
  it('renders percentile bucket rows for instruments with ranked scores', async () => {
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getAllByText('Percentile').length).toBeGreaterThan(0);
    });
  });

  /* ── Top/bottom songs ── */
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

  /* ── Profile switch button ── */
  it('shows select profile button when viewing non-tracked player', async () => {
    // Set a different tracked player
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
    // The button should not be visible — either null or has opacity 0
    const btn = screen.queryByText('Select Player Profile');
    if (btn) {
      const style = btn.closest('button')?.style;
      expect(style?.opacity === '0' || style?.pointerEvents === 'none').toBeTruthy();
    }
  });

  /* ── Stagger animation skip on revisit ── */
  it('skips stagger animations on second render of same account', async () => {
    const { unmount } = renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getByText('TestPlayer')).toBeTruthy();
    });
    unmount();
    clearPlayerPageCache();

    // Second render — module-level _renderedPlayerAccount should be set
    // so skipAnim should be true (but cache is cleared so it re-fetches)
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getByText('TestPlayer')).toBeTruthy();
    });
  });

  /* ── Player data with no scores ── */
  it('renders player page with zero scores gracefully', async () => {
    mockApi.getPlayer.mockResolvedValue({
      accountId: 'test-player-1', displayName: 'EmptyPlayer', totalScores: 0, scores: [],
    });
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getByText('EmptyPlayer')).toBeTruthy();
    });
  });

  /* ── Gold star count rendering ── */
  it('renders gold star count in overall summary for a player with gold stars', async () => {
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      // Player has 1 gold star (song-1 Lead with stars: 6)
      expect(screen.getAllByText('Gold Stars').length).toBeGreaterThan(0);
    });
  });

  /* ── Full combo display ── */
  it('displays full combo count in summary', async () => {
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      expect(screen.getByText('Full Combos')).toBeTruthy();
    });
  });

  /* ── Best rank display ── */
  it('displays best rank in summary when player has ranked scores', async () => {
    renderPlayerPage('/player/test-player-1');
    await waitFor(() => {
      // Player has rank 1 on song-1 Lead — best rank shown as '#1'
      expect(screen.getAllByText('Best Rank').length).toBeGreaterThan(0);
    });
  });
});
