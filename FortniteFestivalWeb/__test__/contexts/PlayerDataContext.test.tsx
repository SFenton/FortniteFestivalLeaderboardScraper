import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';

vi.mock('../../src/api/client', () => ({
  api: {
    getPlayer: vi.fn().mockResolvedValue({ accountId: 'acc-1', displayName: 'TestPlayer', totalScores: 0, scores: [] }),
    getSyncStatus: vi.fn().mockResolvedValue({ accountId: 'acc-1', isTracked: false, backfill: null, historyRecon: null }),
    trackPlayer: vi.fn().mockResolvedValue({ accountId: 'acc-1', displayName: 'TestPlayer', trackingStarted: true }),
    searchAccounts: vi.fn(),
    getSongs: vi.fn().mockResolvedValue({ songs: [], season: 1 }),
  },
}));

vi.mock('../../src/hooks/data/useSyncStatus', () => ({
  useSyncStatus: () => ({
    isSyncing: false,
    phase: 'idle',
    backfillProgress: 0,
    historyProgress: 0,
    justCompleted: false,
    clearCompleted: vi.fn(),
  }),
}));

import { PlayerDataProvider, usePlayerData } from '../../src/contexts/PlayerDataContext';
import { SettingsProvider } from '../../src/contexts/SettingsContext';
import { FestivalProvider } from '../../src/contexts/FestivalContext';

function createWrapper(accountId?: string) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={qc}>
        <SettingsProvider>
          <FestivalProvider>
            <PlayerDataProvider accountId={accountId}>
              {children}
            </PlayerDataProvider>
          </FestivalProvider>
        </SettingsProvider>
      </QueryClientProvider>
    );
  };
}

describe('PlayerDataContext', () => {
  beforeEach(() => { vi.clearAllMocks(); });

  it('provides default values when no accountId', () => {
    const { result } = renderHook(() => usePlayerData(), {
      wrapper: createWrapper(undefined),
    });
    expect(result.current.playerData).toBeNull();
    expect(result.current.playerLoading).toBe(false);
    expect(result.current.playerError).toBeNull();
    expect(result.current.isSyncing).toBe(false);
  });

  it('loads player data when accountId is provided', async () => {
    const { result } = renderHook(() => usePlayerData(), {
      wrapper: createWrapper('acc-1'),
    });
    await waitFor(() => {
      expect(result.current.playerData).not.toBeNull();
    });
    expect(result.current.playerData?.accountId).toBe('acc-1');
    expect(result.current.playerData?.displayName).toBe('TestPlayer');
  });

  it('refreshPlayer triggers refetch', async () => {
    const { result } = renderHook(() => usePlayerData(), {
      wrapper: createWrapper('acc-1'),
    });
    await waitFor(() => { expect(result.current.playerData).not.toBeNull(); });
    await result.current.refreshPlayer();
    // Should not throw
    expect(result.current.playerData?.accountId).toBe('acc-1');
  });

  it('throws when usePlayerData is used outside provider', () => {
    expect(() => {
      renderHook(() => usePlayerData());
    }).toThrow('usePlayerData must be used within a PlayerDataProvider');
  });

  it('refreshPlayer is a no-op when accountId is undefined', async () => {
    const { result } = renderHook(() => usePlayerData(), {
      wrapper: createWrapper(undefined),
    });
    // Call refreshPlayer — should not throw
    await act(async () => {
      await result.current.refreshPlayer();
    });
    expect(result.current.playerData).toBeNull();
  });

  it('syncBannerDismissed defaults to false', () => {
    const { result } = renderHook(() => usePlayerData(), {
      wrapper: createWrapper('acc-1'),
    });
    expect(result.current.syncBannerDismissed).toBe(false);
  });

  it('dismissSyncBanner sets dismissed and persists to localStorage', () => {
    const { result } = renderHook(() => usePlayerData(), {
      wrapper: createWrapper('acc-1'),
    });
    act(() => { result.current.dismissSyncBanner(); });
    expect(result.current.syncBannerDismissed).toBe(true);
    const stored = JSON.parse(localStorage.getItem('fst:syncBannerDismissed')!);
    expect(stored).toEqual({ accountId: 'acc-1', dismissed: true });
  });

  it('hydrates dismissal state from localStorage on mount', () => {
    localStorage.setItem('fst:syncBannerDismissed', JSON.stringify({ accountId: 'acc-1', dismissed: true }));
    const { result } = renderHook(() => usePlayerData(), {
      wrapper: createWrapper('acc-1'),
    });
    expect(result.current.syncBannerDismissed).toBe(true);
  });

  it('does not hydrate dismissal for a different accountId', () => {
    localStorage.setItem('fst:syncBannerDismissed', JSON.stringify({ accountId: 'other', dismissed: true }));
    const { result } = renderHook(() => usePlayerData(), {
      wrapper: createWrapper('acc-1'),
    });
    expect(result.current.syncBannerDismissed).toBe(false);
  });
});
