import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useUnifiedSearch, type UnifiedSearchState } from '../../../src/hooks/data/useUnifiedSearch';
import { TestProviders } from '../../helpers/TestProviders';

const mockApi = vi.hoisted(() => ({
  getSongs: vi.fn(),
  getShop: vi.fn(),
  searchAccounts: vi.fn(),
  searchBands: vi.fn(),
}));

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

type SearchSnapshot = Pick<UnifiedSearchState, 'debouncedQuery' | 'debouncing' | 'loading'>;

function cloneSnapshot(state: UnifiedSearchState): SearchSnapshot {
  return {
    debouncedQuery: state.debouncedQuery,
    debouncing: state.debouncing,
    loading: { ...state.loading },
  };
}

describe('useUnifiedSearch', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.clearAllMocks();
    mockApi.getSongs.mockResolvedValue({ count: 0, currentSeason: 5, songs: [] });
    mockApi.getShop.mockResolvedValue({ songs: [] });
  });

  afterEach(() => {
    vi.runOnlyPendingTimers();
    vi.useRealTimers();
  });

  it('keeps player and band targets loading across the debounce-to-request handoff', async () => {
    let resolveAccounts: (value: unknown) => void = () => {};
    let resolveBands: (value: unknown) => void = () => {};
    mockApi.searchAccounts.mockReturnValue(new Promise(resolve => { resolveAccounts = resolve; }));
    mockApi.searchBands.mockReturnValue(new Promise(resolve => { resolveBands = resolve; }));

    const snapshots: SearchSnapshot[] = [];
    const { result, rerender } = renderHook(
      ({ query }) => {
        const state = useUnifiedSearch(query, { debounceMs: 10 });
        snapshots.push(cloneSnapshot(state));
        return state;
      },
      {
        initialProps: { query: '' },
        wrapper: ({ children }) => <TestProviders>{children}</TestProviders>,
      },
    );

    await act(async () => {
      rerender({ query: 'pla' });
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(10);
    });
    await act(async () => {
      await Promise.resolve();
    });

    expect(mockApi.searchAccounts).toHaveBeenCalledWith('pla', 10);
    expect(mockApi.searchBands).toHaveBeenCalledWith({ q: 'pla', page: 1, pageSize: 10 });
    expect(result.current.debouncedQuery).toBe('pla');
    expect(result.current.debouncing).toBe(false);
    expect(result.current.loading.players).toBe(true);
    expect(result.current.loading.bands).toBe(true);

    expect(snapshots).not.toContainEqual(expect.objectContaining({
      debouncedQuery: 'pla',
      debouncing: false,
      loading: expect.objectContaining({ players: false }),
    }));
    expect(snapshots).not.toContainEqual(expect.objectContaining({
      debouncedQuery: 'pla',
      debouncing: false,
      loading: expect.objectContaining({ bands: false }),
    }));

    await act(async () => {
      resolveAccounts({ results: [{ accountId: 'p1', displayName: 'PlayerOne' }] });
      await Promise.resolve();
    });

    expect(result.current.loading.players).toBe(false);
    expect(result.current.loading.bands).toBe(true);

    await act(async () => {
      resolveBands({
        query: 'pla', normalizedQuery: 'pla', rankBy: 'appearance', page: 1, pageSize: 10, totalCount: 0,
        isAmbiguous: false, needsDisambiguation: false, interpretations: [], results: [],
      });
      await Promise.resolve();
    });

    expect(result.current.loading.players).toBe(false);
    expect(result.current.loading.bands).toBe(false);
  });

  it('skips remote searches for disabled targets', async () => {
    mockApi.searchAccounts.mockResolvedValue({ results: [{ accountId: 'p1', displayName: 'PlayerOne' }] });
    mockApi.searchBands.mockResolvedValue({
      query: 'pla', normalizedQuery: 'pla', rankBy: 'appearance', page: 1, pageSize: 10, totalCount: 0,
      isAmbiguous: false, needsDisambiguation: false, interpretations: [], results: [],
    });

    const { result, rerender } = renderHook(
      ({ query }) => useUnifiedSearch(query, { debounceMs: 10, enabledTargets: ['players'] }),
      {
        initialProps: { query: '' },
        wrapper: ({ children }) => <TestProviders>{children}</TestProviders>,
      },
    );

    await act(async () => {
      rerender({ query: 'pla' });
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(10);
    });
    await act(async () => {
      await Promise.resolve();
    });

    expect(mockApi.searchAccounts).toHaveBeenCalledWith('pla', 10);
    expect(mockApi.searchBands).not.toHaveBeenCalled();
    expect(result.current.songResults).toEqual([]);
    expect(result.current.bandResults).toEqual([]);
    expect(result.current.loading.bands).toBe(false);
  });
});
