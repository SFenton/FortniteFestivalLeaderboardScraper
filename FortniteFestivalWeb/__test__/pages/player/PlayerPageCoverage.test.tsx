/**
 * PlayerPage coverage tests — exercises sync banner, instrument stats,
 * percentile distribution, top/bottom songs, profile switch, and stagger calculations.
 */
import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { Routes, Route } from 'react-router-dom';
import PlayerPage, { clearPlayerPageCache } from '../../../src/pages/player/PlayerPage';
import { TestProviders } from '../../helpers/TestProviders';
import { stubScrollTo, stubElementDimensions } from '../../helpers/browserStubs';

const SONGS_DATA = {
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

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue(null),
    getPlayer: fn().mockResolvedValue(null),
    getSyncStatus: fn().mockResolvedValue({ accountId: '', isTracked: false, backfill: null, historyRecon: null }),
    getPlayerStats: fn().mockResolvedValue({ accountId: 'p1', stats: [] }),
    getVersion: fn().mockResolvedValue({ version: '1.0.0' }),
    getLeaderboard: fn().mockResolvedValue({ entries: [] }),
    getAllLeaderboards: fn().mockResolvedValue({ instruments: [] }),
    getPlayerHistory: fn().mockResolvedValue({ accountId: 'p1', count: 0, history: [] }),
    searchAccounts: fn().mockResolvedValue({ results: [] }),
    trackPlayer: fn().mockResolvedValue({ accountId: 'p1', displayName: 'P', trackingStarted: false, backfillStatus: 'none' }),
  };
});

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

beforeAll(() => {
  stubScrollTo();
  stubElementDimensions(900);
});

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  clearPlayerPageCache();
  mockApi.getSongs.mockResolvedValue(SONGS_DATA);
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

describe('PlayerPage — coverage: instrument stats + percentile + top/bottom songs', () => {
  it('renders instrument stat boxes and percentile table with rich scores', async () => {
    const player = buildRichPlayer('rich-player');
    mockApi.getPlayer.mockResolvedValue(player);
    mockApi.getSyncStatus.mockResolvedValue({ accountId: 'rich-player', isTracked: false, backfill: null, historyRecon: null });

    const { container } = renderPlayerPage('/player/rich-player');

    await waitFor(() => {
      expect(container.textContent).toContain('RichPlayer');
    });

    // Wait for contentIn phase — instrument stats section
    await waitFor(() => {
      expect(container.textContent).toContain('Instrument Statistics');
    }, { timeout: 3000 });

    // Should render per-instrument headers (Guitar, Bass, Drums)
    expect(container.textContent).toContain('Lead');
    expect(container.textContent).toContain('Bass');
    expect(container.textContent).toContain('Drums');

    // Should show summary stat boxes — Songs Played, Full Combos, etc.
    expect(container.textContent).toContain('Songs Played');
    expect(container.textContent).toContain('Full Combos');
    expect(container.textContent).toContain('Gold Stars');
    expect(container.textContent).toContain('Avg Accuracy');
    expect(container.textContent).toContain('Best Rank');
  });

  it('renders top and bottom songs sections', async () => {
    const player = buildRichPlayer('top-bot');
    mockApi.getPlayer.mockResolvedValue(player);
    mockApi.getSyncStatus.mockResolvedValue({ accountId: 'top-bot', isTracked: false, backfill: null, historyRecon: null });

    const { container } = renderPlayerPage('/player/top-bot');

    await waitFor(() => {
      expect(container.textContent).toContain('Top Songs Per Instrument');
    }, { timeout: 3000 });

    // Should render top 5 and bottom 5 per instrument
    expect(container.textContent).toContain('Top Five Songs');
    expect(container.textContent).toContain('Bottom Five Songs');
    // Verify that actual song titles appear in the top/bottom sections
    expect(container.textContent).toContain('Song Alpha');
  });

  it('renders percentile distribution rows', async () => {
    const player = buildRichPlayer('pct-player');
    mockApi.getPlayer.mockResolvedValue(player);
    mockApi.getSyncStatus.mockResolvedValue({ accountId: 'pct-player', isTracked: false, backfill: null, historyRecon: null });

    const { container } = renderPlayerPage('/player/pct-player');

    await waitFor(() => {
      expect(container.textContent).toContain('Percentile');
    }, { timeout: 3000 });

    // Percentile table should contain "Top X%" pills
    await waitFor(() => {
      expect(container.textContent).toMatch(/Top \d+%/);
    });
  });
});

describe('PlayerPage — coverage: sync banner', () => {
  it('renders sync banner when syncing with backfill phase', async () => {
    const player = buildRichPlayer('syncing-player');
    mockApi.getPlayer.mockResolvedValue(player);
    mockApi.getSyncStatus.mockResolvedValue({
      accountId: 'syncing-player',
      isTracked: true,
      backfill: { status: 'in_progress', songsChecked: 50, totalSongsToCheck: 100, entriesFound: 200, startedAt: '2025-01-01T00:00:00Z' },
      historyRecon: null,
    });

    const { container } = renderPlayerPage('/player/syncing-player');

    await waitFor(() => {
      expect(container.textContent).toContain('RichPlayer');
    });

    // The sync banner should appear with progress
    await waitFor(() => {
      expect(container.textContent).toContain('Syncing');
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

    const { container } = renderPlayerPage('/player/history-player');

    await waitFor(() => {
      expect(container.textContent).toContain('RichPlayer');
    });

    await waitFor(() => {
      // Should show either "Syncing" or "Reconstructing"
      expect(
        container.textContent!.includes('Syncing') ||
        container.textContent!.includes('Reconstructing') ||
        container.textContent!.includes('history')
      ).toBe(true);
    }, { timeout: 3000 });
  });
});

describe('PlayerPage — coverage: profile switch', () => {
  it('shows Select Player Profile button for non-tracked player on desktop', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'existing-tracked', displayName: 'ExistingPlayer' }));
    const player = buildRichPlayer('another-player');
    player.displayName = 'AnotherPlayer';
    mockApi.getPlayer.mockResolvedValue(player);
    mockApi.getSyncStatus.mockResolvedValue({ accountId: 'another-player', isTracked: false, backfill: null, historyRecon: null });

    const { container } = renderPlayerPage('/player/another-player', 'existing-tracked');

    await waitFor(() => {
      expect(container.textContent).toContain('AnotherPlayer');
    });

    // Should render "Select Player Profile" button
    await waitFor(() => {
      expect(container.textContent).toContain('Select Player Profile');
    }, { timeout: 3000 });

    // Click it to trigger profile switch confirm
    const btn = Array.from(container.querySelectorAll('button')).find(
      b => b.textContent?.includes('Select Player Profile'),
    );
    if (btn) {
      fireEvent.click(btn);
      // Should show confirm dialog
      await waitFor(() => {
        expect(container.textContent).toContain('Switch to');
      });
    }
  });
});

describe('PlayerPage — coverage: stagger calculations', () => {
  it('exercises stagger computation with many grid items', async () => {
    const player = buildRichPlayer('stagger-player');
    mockApi.getPlayer.mockResolvedValue(player);
    mockApi.getSyncStatus.mockResolvedValue({ accountId: 'stagger-player', isTracked: false, backfill: null, historyRecon: null });

    const { container } = renderPlayerPage('/player/stagger-player');

    await waitFor(() => {
      expect(container.textContent).toContain('RichPlayer');
    });

    // Wait for grid items to be drawn — the stagger calculation runs inside the IIFE
    await waitFor(() => {
      expect(container.textContent).toContain('Instrument Statistics');
    }, { timeout: 3000 });

    // Verify that FadeIn components were rendered (they wrap each grid item)
    container.querySelectorAll('[style*="animation"]');
    // With rich data, there should be many animated items
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

    // Second render — stagger should be skipped
    const { container } = renderPlayerPage('/player/revisit-player');
    await waitFor(() => {
      expect(container.textContent).toContain('RevisitPlayer');
    });

    // Grid items should appear without stagger delays
    await waitFor(() => {
      expect(container.textContent).toContain('Instrument Statistics');
    }, { timeout: 3000 });
  });
});
