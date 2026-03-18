import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import React from 'react';
import { useSyncStatus } from '../../hooks/data/useSyncStatus';
import { TestProviders } from '../helpers/TestProviders';

vi.mock('../../api/client', () => ({
  api: {
    getSyncStatus: vi.fn(),
    trackPlayer: vi.fn(),
    getSongs: vi.fn().mockResolvedValue({ songs: [], season: 0 }),
    getPlayerData: vi.fn().mockResolvedValue(null),
  },
}));

import { api } from '../../api/client';
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

  it('clearCompleted resets justCompleted', async () => {
    mockGetStatus.mockResolvedValue({
      backfill: { status: 'complete', songsChecked: 100, totalSongsToCheck: 100, entriesFound: 10, startedAt: null, completedAt: null },
      historyRecon: null } as any);
    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();
    act(() => { result.current.clearCompleted(); });
    expect(result.current.justCompleted).toBe(false);
  });

  it('combined progress is 0.5 * backfill when in backfill phase', async () => {
    mockGetStatus.mockResolvedValue({
      backfill: { status: 'in_progress', songsChecked: 50, totalSongsToCheck: 100, entriesFound: 5, startedAt: null, completedAt: null },
      historyRecon: null } as any);
    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();
    expect(result.current.progress).toBeCloseTo(0.25); // 0.5 * 0.5
  });

  it('combined progress is 0.5 + 0.5 * history when in history phase', async () => {
    mockGetStatus.mockResolvedValue({
      backfill: { status: 'complete', songsChecked: 100, totalSongsToCheck: 100, entriesFound: 10, startedAt: null, completedAt: null },
      historyRecon: { status: 'in_progress', songsProcessed: 50, totalSongsToProcess: 100, seasonsQueried: 0, historyEntriesFound: 0, startedAt: null, completedAt: null }, } as any);
    const { result } = renderHook(() => useSyncStatus('acc1'), { wrapper });
    await flush();
    expect(result.current.progress).toBeCloseTo(0.75); // 0.5 + 0.5 * 0.5
  });
});
