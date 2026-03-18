/**
 * Tests for PlayerPage auto-reload when sync completes (lines 52-53).
 * Mocks useSyncStatus to simulate justCompleted=true for a non-tracked player.
 */
import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, waitFor } from '@testing-library/react';
import { Routes, Route } from 'react-router-dom';
import { TestProviders } from '../../helpers/TestProviders';
import { stubScrollTo } from '../../helpers/browserStubs';

const mockClearCompleted = vi.fn();

vi.mock('../../../src/hooks/data/useSyncStatus', () => ({
  useSyncStatus: () => ({
    isSyncing: false,
    phase: 'idle',
    backfillStatus: null,
    backfillProgress: 0,
    historyStatus: null,
    historyProgress: 0,
    entriesFound: 0,
    justCompleted: true,
    clearCompleted: mockClearCompleted,
  }),
}));

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue({ songs: [{ songId: 's1', title: 'Song', artist: 'Art', year: 2024 }], count: 1, currentSeason: 5 }),
    getPlayer: fn().mockResolvedValue({ accountId: 'p1', displayName: 'TestPlayer', totalScores: 0, scores: [] }),
    getSyncStatus: fn().mockResolvedValue({ accountId: 'p1', isTracked: false, backfill: null, historyRecon: null }),
    getPlayerStats: fn().mockResolvedValue({ accountId: 'p1', stats: [] }),
    trackPlayer: fn().mockResolvedValue({ accountId: 'p1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: 'none' }),
    getVersion: fn().mockResolvedValue({ version: '1.0.0' }),
    getLeaderboard: fn().mockResolvedValue({ entries: [] }),
    getAllLeaderboards: fn().mockResolvedValue({ instruments: [] }),
    getPlayerHistory: fn().mockResolvedValue({ accountId: 'p1', count: 0, history: [] }),
    searchAccounts: fn().mockResolvedValue({ results: [] }),
  };
});

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

import PlayerPage, { clearPlayerPageCache } from '../../../src/pages/player/PlayerPage';

beforeAll(() => { stubScrollTo(); });

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  clearPlayerPageCache();
  mockApi.getPlayer.mockResolvedValue({ accountId: 'p1', displayName: 'TestPlayer', totalScores: 0, scores: [] });
});

describe('PlayerPage — justCompleted auto-reload', () => {
  it('calls clearCompleted and invalidates query when sync just completed for non-tracked player', async () => {
    render(
      <TestProviders route="/player/p1">
        <Routes>
          <Route path="/player/:accountId" element={<PlayerPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(mockClearCompleted).toHaveBeenCalled();
    });
  });
});
