import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { queryKeys } from '../../../src/api/queryKeys';

const mockApi = vi.hoisted(() => ({
  getPlayerBandsList: vi.fn(),
}));

const featureFlags = vi.hoisted(() => ({
  playerBands: true,
}));

vi.mock('../../../src/api/client', () => ({ api: mockApi }));
vi.mock('../../../src/contexts/FeatureFlagsContext', () => ({
  useFeatureFlags: () => ({
    compete: true,
    leaderboards: true,
    difficulty: true,
    playerBands: featureFlags.playerBands,
    experimentalRanks: true,
  }),
}));

const { usePlayerBandsPrefetch } = await import('../../../src/hooks/data/usePlayerBandsPrefetch');

type TestIdleDeadline = { didTimeout: boolean; timeRemaining: () => number };
type TestIdleCallback = (deadline: TestIdleDeadline) => void;

let idleCallbacks: Map<number, TestIdleCallback>;
let nextIdleHandle: number;

function createClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

function makeWrapper(queryClient: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
  };
}

async function runIdleCallbacks() {
  await act(async () => {
    const callbacks = Array.from(idleCallbacks.entries());
    idleCallbacks.clear();
    for (const [, callback] of callbacks) {
      callback({ didTimeout: false, timeRemaining: () => 50 });
    }
    await Promise.resolve();
  });
}

function emptyBands(accountId = 'p1', group = 'all') {
  return { accountId, group, totalCount: 0, entries: [] };
}

describe('usePlayerBandsPrefetch', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    featureFlags.playerBands = true;
    mockApi.getPlayerBandsList.mockImplementation((accountId: string, group: string) => Promise.resolve(emptyBands(accountId, group)));
    idleCallbacks = new Map();
    nextIdleHandle = 1;
    vi.stubGlobal('requestIdleCallback', vi.fn((callback: TestIdleCallback) => {
      const handle = nextIdleHandle++;
      idleCallbacks.set(handle, callback);
      return handle;
    }));
    vi.stubGlobal('cancelIdleCallback', vi.fn((handle: number) => {
      idleCallbacks.delete(handle);
    }));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('does not prefetch without a selected account', () => {
    const queryClient = createClient();

    renderHook(() => usePlayerBandsPrefetch(undefined), { wrapper: makeWrapper(queryClient) });

    expect(mockApi.getPlayerBandsList).not.toHaveBeenCalled();
    expect(idleCallbacks.size).toBe(0);
  });

  it('does not prefetch when player bands are disabled', () => {
    featureFlags.playerBands = false;
    const queryClient = createClient();

    renderHook(() => usePlayerBandsPrefetch('p1'), { wrapper: makeWrapper(queryClient) });

    expect(mockApi.getPlayerBandsList).not.toHaveBeenCalled();
    expect(idleCallbacks.size).toBe(0);
  });

  it('prefetches all bands immediately and filtered groups during idle time', async () => {
    const queryClient = createClient();

    renderHook(() => usePlayerBandsPrefetch('p1'), { wrapper: makeWrapper(queryClient) });

    await waitFor(() => expect(mockApi.getPlayerBandsList).toHaveBeenCalledTimes(1));
    expect(mockApi.getPlayerBandsList).toHaveBeenCalledWith(
      'p1',
      'all',
      1,
      25,
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );

    await runIdleCallbacks();

    await waitFor(() => expect(mockApi.getPlayerBandsList).toHaveBeenCalledTimes(4));
    expect(mockApi.getPlayerBandsList).toHaveBeenCalledWith('p1', 'duos', 1, 25, expect.objectContaining({ signal: expect.any(AbortSignal) }));
    expect(mockApi.getPlayerBandsList).toHaveBeenCalledWith('p1', 'trios', 1, 25, expect.objectContaining({ signal: expect.any(AbortSignal) }));
    expect(mockApi.getPlayerBandsList).toHaveBeenCalledWith('p1', 'quads', 1, 25, expect.objectContaining({ signal: expect.any(AbortSignal) }));
  });

  it('cancels and clears old selected account queries when the selected account changes', async () => {
    const queryClient = createClient();
    const cancelSpy = vi.spyOn(queryClient, 'cancelQueries');
    const removeSpy = vi.spyOn(queryClient, 'removeQueries');

    const { rerender } = renderHook(
      ({ accountId }: { accountId: string }) => usePlayerBandsPrefetch(accountId),
      { wrapper: makeWrapper(queryClient), initialProps: { accountId: 'p1' } },
    );

    await waitFor(() => expect(mockApi.getPlayerBandsList).toHaveBeenCalledWith('p1', 'all', 1, 25, expect.any(Object)));

    rerender({ accountId: 'p2' });

    expect(cancelSpy).toHaveBeenCalledWith({ queryKey: ['playerBandsList', 'p1'], type: 'inactive' });
    expect(removeSpy).toHaveBeenCalledWith({ queryKey: ['playerBandsList', 'p1'], type: 'inactive' });
    await waitFor(() => expect(mockApi.getPlayerBandsList).toHaveBeenCalledWith('p2', 'all', 1, 25, expect.any(Object)));

    await runIdleCallbacks();

    const accountGroupCalls = mockApi.getPlayerBandsList.mock.calls.map(([accountId, group]) => [accountId, group]);
    expect(accountGroupCalls).not.toContainEqual(['p1', 'duos']);
    expect(accountGroupCalls).not.toContainEqual(['p1', 'trios']);
    expect(accountGroupCalls).not.toContainEqual(['p1', 'quads']);
    expect(accountGroupCalls).toContainEqual(['p2', 'duos']);
    expect(accountGroupCalls).toContainEqual(['p2', 'trios']);
    expect(accountGroupCalls).toContainEqual(['p2', 'quads']);
  });

  it('removes prefetched cache for the selected account on unmount', () => {
    const queryClient = createClient();
    const key = queryKeys.playerBandsList('p1', 'all', 1, 25);
    queryClient.setQueryData(key, emptyBands());

    const { unmount } = renderHook(() => usePlayerBandsPrefetch('p1'), { wrapper: makeWrapper(queryClient) });

    unmount();

    expect(queryClient.getQueryData(key)).toBeUndefined();
  });

  it('aborts an in-flight background request on unmount', async () => {
    let capturedSignal: AbortSignal | undefined;
    mockApi.getPlayerBandsList.mockImplementation((_accountId: string, _group: string, _page: number, _pageSize: number, options?: { signal?: AbortSignal }) => {
      capturedSignal = options?.signal;
      return new Promise((_resolve, reject) => {
        capturedSignal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
      });
    });
    const queryClient = createClient();

    const { unmount } = renderHook(() => usePlayerBandsPrefetch('p1'), { wrapper: makeWrapper(queryClient) });

    await waitFor(() => expect(capturedSignal).toBeInstanceOf(AbortSignal));
    unmount();

    await waitFor(() => expect(capturedSignal?.aborted).toBe(true));
  });
});