import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import React from 'react';
import { useSyncStatus } from '../../../src/hooks/data/useSyncStatus';
import { TestProviders } from '../../helpers/TestProviders';

vi.mock('../../../src/api/client', () => ({
  api: {
    getSyncStatus: vi.fn(),
    trackPlayer: vi.fn(),
    getSongs: vi.fn().mockResolvedValue({ songs: [], season: 0 }),
    getPlayerData: vi.fn().mockResolvedValue(null),
  },
}));

const mockSend = vi.fn();
const wsHandlers = new Set<(msg: any) => void>();
const wsOpenHandlers = new Set<() => void>();
const mockSubscribe = vi.fn((handler: (msg: any) => void) => {
  wsHandlers.add(handler);
  return () => { wsHandlers.delete(handler); };
});
const mockSubscribeOpen = vi.fn((handler: () => void) => {
  wsOpenHandlers.add(handler);
  return () => { wsOpenHandlers.delete(handler); };
});
vi.mock('../../../src/hooks/data/useAppWebSocket', () => ({
  useAppWebSocket: () => ({
    connected: true,
    subscribe: mockSubscribe,
    send: mockSend,
    subscribeOpen: mockSubscribeOpen,
  }),
}));

import { api } from '../../../src/api/client';
const mockGetStatus = vi.mocked(api.getSyncStatus);
const mockTrackPlayer = vi.mocked(api.trackPlayer);

function wrapper({ children }: { children: React.ReactNode }) {
  return React.createElement(TestProviders, null, children);
}

describe('useSyncStatus', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    mockGetStatus.mockReset();
    mockTrackPlayer.mockReset();
    mockTrackPlayer.mockResolvedValue(undefined as any);
    mockSend.mockReset();
    mockSubscribe.mockClear();
    mockSubscribeOpen.mockClear();
    wsHandlers.clear();
    wsOpenHandlers.clear();
  });
  afterEach(() => { vi.useRealTimers(); });

  // Helper: flush all pending promises + timers
  async function flush() {
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
  }

  it('returns idle state when no accountId', () => {
    const { result } = renderHook(() => useSyncStatus(undefined), { wrapper });
    expect(result.current.isSyncing).toBe(false);
    expect(result.current.phase).toBe('idle');
    expect(result.current.progress).toBe(0);
  });

  it('resets queued state when accountId becomes undefined', async () => {
    mockGetStatus.mockResolvedValue({
      accountId: 'acc1',
      isTracked: true,
      pendingRankUpdate: true,
      backfill: { status: 'deferred', songsChecked: 0, totalSongsToCheck: 100, entriesFound: 12, startedAt: null, completedAt: null, rankingsPending: true, deferredReason: 'server_update_in_progress' },
      historyRecon: null,
      rivals: null,
      postScrape: null,
    } as any);

    const { result, rerender } = renderHook(
      ({ accountId }) => useSyncStatus(accountId, { track: false, useWebSocket: false }),
      { initialProps: { accountId: 'acc1' as string | undefined }, wrapper },
    );
    await flush();

    expect(result.current.isSyncing).toBe(true);
    expect(result.current.phase).toBe('queued');
    expect(result.current.totalItems).toBe(100);
    expect(result.current.entriesFound).toBe(12);
    expect(result.current.pendingRankUpdate).toBe(true);

    rerender({ accountId: undefined });
    await flush();

    expect(result.current.isTracked).toBe(false);
    expect(result.current.syncStatusLoaded).toBe(false);
    expect(result.current.isSyncing).toBe(false);
    expect(result.current.phase).toBe('idle');
    expect(result.current.progress).toBe(0);
    expect(result.current.totalItems).toBe(0);
    expect(result.current.entriesFound).toBe(0);
    expect(result.current.currentSongName).toBeNull();
    expect(result.current.pendingRankUpdate).toBe(false);
    expect(result.current.justCompleted).toBe(false);
    expect(mockGetStatus).not.toHaveBeenCalledWith(undefined);
  });

  it('does not expose previous account queued state while the next account loads', async () => {
    mockTrackPlayer
      .mockResolvedValueOnce({ syncDeferred: true } as any)
      .mockResolvedValue(undefined as any);
    mockGetStatus.mockResolvedValue({ accountId: 'acc2', isTracked: true, backfill: null, historyRecon: null } as any);

    const { result, rerender } = renderHook(
      ({ accountId }) => useSyncStatus(accountId, { useWebSocket: false }),
      { initialProps: { accountId: 'acc1' as string | undefined }, wrapper },
    );
    await flush();

    expect(result.current.isSyncing).toBe(true);
    expect(result.current.phase).toBe('queued');

    rerender({ accountId: 'acc2' });

    expect(result.current.isSyncing).toBe(false);
    expect(result.current.phase).toBe('idle');
    expect(result.current.progress).toBe(0);

    await flush();

    expect(mockTrackPlayer).toHaveBeenCalledWith('acc2');
    expect(mockGetStatus).toHaveBeenCalledWith('acc2');
    expect(result.current.isSyncing).toBe(false);
    expect(result.current.phase).toBe('idle');
  });

  it('ignores late status responses for a deselected account', async () => {
    let resolveAcc1Status: ((value: unknown) => void) | undefined;
    mockGetStatus.mockImplementation((requestedAccountId: string) => {
      if (requestedAccountId === 'acc1') {
        return new Promise(resolve => { resolveAcc1Status = resolve; }) as any;
      }

      return Promise.resolve({ accountId: requestedAccountId, isTracked: false, backfill: null, historyRecon: null } as any);
    });

    const { result, rerender } = renderHook(
      ({ accountId }) => useSyncStatus(accountId, { track: false, useWebSocket: false }),
      { initialProps: { accountId: 'acc1' as string | undefined }, wrapper },
    );
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });

    expect(mockGetStatus).toHaveBeenCalledWith('acc1');
    expect(resolveAcc1Status).toBeDefined();

    rerender({ accountId: undefined });
    await flush();

    expect(result.current.isSyncing).toBe(false);
    expect(result.current.phase).toBe('idle');

    await act(async () => {
      resolveAcc1Status?.({
        accountId: 'acc1',
        isTracked: true,
        pendingRankUpdate: true,
        backfill: { status: 'deferred', songsChecked: 0, totalSongsToCheck: 100, entriesFound: 12, startedAt: null, completedAt: null, rankingsPending: true, deferredReason: 'server_update_in_progress' },
        historyRecon: null,
        rivals: null,
        postScrape: null,
      });
      await vi.advanceTimersByTimeAsync(0);
    });

    expect(result.current.isTracked).toBe(false);
    expect(result.current.isSyncing).toBe(false);
    expect(result.current.phase).toBe('idle');
    expect(result.current.pendingRankUpdate).toBe(false);
  });

  it('tracks player and checks status on mount', async () => {
    mockGetStatus.mockResolvedValue({ backfill: null, historyRecon: null } as any);
    renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();
    expect(mockTrackPlayer).toHaveBeenCalledWith('acc1');
    expect(mockGetStatus).toHaveBeenCalledWith('acc1');
  });

  it('detects backfill in progress', async () => {
    mockGetStatus.mockResolvedValue({
      backfill: { status: 'in_progress', songsChecked: 50, totalSongsToCheck: 100, entriesFound: 10, startedAt: null, completedAt: null },
      historyRecon: null } as any);
    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();
    expect(result.current.isSyncing).toBe(true);
    expect(result.current.phase).toBe('backfill');
    expect(result.current.backfillProgress).toBe(0.5);
  });

  it('detects history recon in progress', async () => {
    mockGetStatus.mockResolvedValue({
      backfill: { status: 'complete', songsChecked: 100, totalSongsToCheck: 100, entriesFound: 10, startedAt: null, completedAt: null },
      historyRecon: { status: 'in_progress', songsProcessed: 30, totalSongsToProcess: 100, seasonsQueried: 0, historyEntriesFound: 0, startedAt: null, completedAt: null }, } as any);
    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();
    expect(result.current.phase).toBe('history');
    expect(result.current.historyProgress).toBe(0.3);
  });

  it('detects complete state', async () => {
    mockGetStatus.mockResolvedValue({
      backfill: { status: 'complete', songsChecked: 100, totalSongsToCheck: 100, entriesFound: 10, startedAt: null, completedAt: null },
      historyRecon: { status: 'complete', songsProcessed: 100, totalSongsToProcess: 100, seasonsQueried: 0, historyEntriesFound: 0, startedAt: null, completedAt: null }, } as any);
    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();
    expect(result.current.phase).toBe('complete');
    expect(result.current.progress).toBe(1);
  });

  it('detects error state', async () => {
    mockGetStatus.mockResolvedValue({
      backfill: { status: 'error', songsChecked: 0, totalSongsToCheck: 0, entriesFound: 0, startedAt: null, completedAt: null },
      historyRecon: null } as any);
    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();
    expect(result.current.phase).toBe('error');
  });

  it('handles API error gracefully', async () => {
    mockGetStatus.mockRejectedValue(new Error('Network'));
    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();
    // Should not crash, stays idle
    expect(result.current.phase).toBe('idle');
  });

  it('handles track failure gracefully', async () => {
    mockTrackPlayer.mockRejectedValue(new Error('Track failed'));
    mockGetStatus.mockResolvedValue({ backfill: null, historyRecon: null } as any);
    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();
    expect(result.current.phase).toBe('idle');
  });

  it('skips trackPlayer when track option is false', async () => {
    mockGetStatus.mockResolvedValue({ backfill: null, historyRecon: null } as any);
    renderHook(() => useSyncStatus('acc1', { track: false }), { wrapper });
    await flush();
    expect(mockTrackPlayer).not.toHaveBeenCalled();
    expect(mockGetStatus).toHaveBeenCalledWith('acc1');
  });

  it('clearCompleted resets justCompleted', async () => {
    mockGetStatus.mockResolvedValue({
      backfill: { status: 'complete', songsChecked: 100, totalSongsToCheck: 100, entriesFound: 10, startedAt: null, completedAt: null },
      historyRecon: null } as any);
    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();
    act(() => { result.current.clearCompleted(); });
    expect(result.current.justCompleted).toBe(false);
  });

  it('combined progress is (1/3) * backfill when in backfill phase', async () => {
    mockGetStatus.mockResolvedValue({
      backfill: { status: 'in_progress', songsChecked: 50, totalSongsToCheck: 100, entriesFound: 5, startedAt: null, completedAt: null },
      historyRecon: null } as any);
    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();
    expect(result.current.progress).toBeCloseTo(1/3 * 0.5); // (1/3) * 0.5
  });

  it('combined progress is (1/3) + (1/3) * history when in history phase', async () => {
    mockGetStatus.mockResolvedValue({
      backfill: { status: 'complete', songsChecked: 100, totalSongsToCheck: 100, entriesFound: 10, startedAt: null, completedAt: null },
      historyRecon: { status: 'in_progress', songsProcessed: 50, totalSongsToProcess: 100, seasonsQueried: 0, historyEntriesFound: 0, startedAt: null, completedAt: null }, } as any);
    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();
    expect(result.current.progress).toBeCloseTo(1/3 + 1/3 * 0.5); // (1/3) + (1/3) * 0.5
  });

  // ── WebSocket account subscription ──

  it('sends subscribe_sync when accountId is provided', async () => {
    mockGetStatus.mockResolvedValue({ backfill: null, historyRecon: null } as any);
    renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();
    expect(mockSend).toHaveBeenCalledWith(
      JSON.stringify({ action: 'subscribe_sync', accountId: 'acc1' }),
    );
  });

  it('replays subscribe_sync when the shared socket opens again', async () => {
    mockGetStatus.mockResolvedValue({ backfill: null, historyRecon: null } as any);
    renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();

    mockSend.mockReset();

    act(() => {
      wsOpenHandlers.forEach(handler => handler());
    });

    expect(mockSend).toHaveBeenCalledWith(
      JSON.stringify({ action: 'subscribe_sync', accountId: 'acc1' }),
    );
  });

  it('sends subscribe_sync again when the accountId changes', async () => {
    mockGetStatus.mockResolvedValue({ backfill: null, historyRecon: null } as any);
    const { rerender } = renderHook(
      ({ accountId }) => useSyncStatus(accountId, { track: false }),
      { initialProps: { accountId: 'acc1' }, wrapper },
    );
    await flush();

    mockSend.mockReset();

    rerender({ accountId: 'acc2' });
    await flush();

    expect(mockSend).toHaveBeenCalledWith(
      JSON.stringify({ action: 'subscribe_sync', accountId: 'acc2' }),
    );
  });

  it('does not send subscribe_sync when no accountId', async () => {
    renderHook(() => useSyncStatus(undefined), { wrapper });
    await flush();
    expect(mockSend).not.toHaveBeenCalled();
  });

  it('sends unsubscribe_sync on unmount', async () => {
    mockGetStatus.mockResolvedValue({ backfill: null, historyRecon: null } as any);
    const { unmount } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();
    mockSend.mockReset();
    unmount();
    expect(mockSend).toHaveBeenCalledWith(
      JSON.stringify({ action: 'unsubscribe_sync' }),
    );
  });

  it('does not use the shared WebSocket when useWebSocket is false', async () => {
    mockGetStatus.mockResolvedValue({ backfill: null, historyRecon: null } as any);
    renderHook(() => useSyncStatus('acc1', { track: false, useWebSocket: false }));
    await flush();

    expect(mockSend).not.toHaveBeenCalled();
    expect(mockSubscribe).not.toHaveBeenCalled();
    expect(mockSubscribeOpen).not.toHaveBeenCalled();
  });

  // ── PostScrape phase via WebSocket ──

  it('maps postscrape WS phase to syncing state', async () => {
    mockGetStatus.mockResolvedValue({ backfill: null, historyRecon: null } as any);
    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();

    // Dispatch a postscrape WS message through all registered handlers
    await act(async () => {
      const msg = {
        type: 'sync_progress',
        accountId: 'acc1',
        phase: 'postscrape',
        itemsCompleted: 30,
        totalItems: 100,
        entriesFound: 5,
      } as any;
      wsHandlers.forEach(h => h(msg));
    });

    expect(result.current.isSyncing).toBe(true);
    expect(result.current.phase).toBe('postscrape');
    expect(result.current.itemsCompleted).toBe(30);
    expect(result.current.totalItems).toBe(100);
    expect(result.current.entriesFound).toBe(5);
    expect(result.current.progress).toBeCloseTo(0.3);
  });

  it('maps deferred HTTP backfill status to queued state', async () => {
    mockGetStatus.mockResolvedValue({
      pendingRankUpdate: false,
      backfill: { status: 'deferred', songsChecked: 0, totalSongsToCheck: 100, entriesFound: 0, startedAt: null, completedAt: null, rankingsPending: false, deferredReason: 'server_update_in_progress' },
      historyRecon: null,
      rivals: null,
      postScrape: null,
    } as any);

    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();

    expect(result.current.isSyncing).toBe(true);
    expect(result.current.phase).toBe('queued');
    expect(result.current.progress).toBe(0);
  });

  it('maps postscrape HTTP status to syncing state', async () => {
    mockGetStatus.mockResolvedValue({
      backfill: { status: 'complete', songsChecked: 100, totalSongsToCheck: 100, entriesFound: 3, startedAt: null, completedAt: null },
      historyRecon: null,
      rivals: null,
      postScrape: { status: 'in_progress', itemsCompleted: 25, totalItems: 100, entriesFound: 4, currentSongName: 'Song A' },
    } as any);

    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();

    expect(result.current.isSyncing).toBe(true);
    expect(result.current.phase).toBe('postscrape');
    expect(result.current.progress).toBeCloseTo(0.25);
    expect(result.current.entriesFound).toBe(4);
    expect(result.current.currentSongName).toBe('Song A');
  });

  it('surfaces pending rank update from HTTP completion status', async () => {
    mockGetStatus.mockResolvedValue({
      pendingRankUpdate: true,
      backfill: { status: 'complete', songsChecked: 100, totalSongsToCheck: 100, entriesFound: 10, startedAt: null, completedAt: null, rankingsPending: true },
      historyRecon: null,
      rivals: null,
    } as any);

    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();

    expect(result.current.phase).toBe('complete');
    expect(result.current.pendingRankUpdate).toBe(true);
  });

  // ── backfillKicked deferred poll ──

  it('shows queued state when trackPlayer defers sync behind an active update', async () => {
    mockTrackPlayer.mockResolvedValue({ syncDeferred: true } as any);
    mockGetStatus.mockResolvedValue({ backfill: null, historyRecon: null } as any);

    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();

    expect(result.current.isSyncing).toBe(true);
    expect(result.current.phase).toBe('queued');
    expect(mockGetStatus).not.toHaveBeenCalled();
  });

  it('defers first checkStatus when backfillKicked to preserve optimistic banner', async () => {
    mockTrackPlayer.mockResolvedValue({ backfillKicked: true } as any);
    // getSyncStatus returns idle (simulating stale cache)
    mockGetStatus.mockResolvedValue({ backfill: null, historyRecon: null } as any);

    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();

    // Optimistic state should be active immediately after trackPlayer resolves
    expect(result.current.isSyncing).toBe(true);
    expect(result.current.phase).toBe('backfill');

    // getSyncStatus should NOT have been called yet (deferred by SYNC_POLL_ACTIVE_MS)
    expect(mockGetStatus).not.toHaveBeenCalled();

    // Advance past the deferred poll interval (3s)
    await act(async () => { await vi.advanceTimersByTimeAsync(3100); });

    // Now the first poll should have fired
    expect(mockGetStatus).toHaveBeenCalledWith('acc1');
  });
});
