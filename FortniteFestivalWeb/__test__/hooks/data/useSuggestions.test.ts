import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createElement, type ReactNode } from 'react';
import { queryKeys } from '../../../src/api/queryKeys';
import { getSuggestionsScrollRestoreState } from '../../../src/pages/suggestions/suggestionsSessionCache';

const mockGetNext = vi.fn().mockReturnValue([]);
const mockSetSource = vi.fn();
const mockResetForEndless = vi.fn();
const mockGeneratorOptions = vi.fn();
const mockGeneratorInstances: Array<{ setRivalData: ReturnType<typeof vi.fn> }> = [];
const mockGetRivalsAll = vi.fn();
const mockScrollContainerRef: { current: HTMLElement | null } = { current: null };
const mockBuildRivalDataIndex = vi.fn((response: unknown) => ({
  songRivals: [],
  byRival: new Map(),
  closestRivalBySong: new Map(),
  topRivalBySong: new Map(),
  response,
}));

vi.mock('@festival/core/suggestions', () => {
  return {
    SuggestionGenerator: class {
      setRivalData = vi.fn();
      setSource = mockSetSource;
      getNext = mockGetNext;
      resetForEndless = mockResetForEndless;

      constructor(options: unknown) {
        mockGeneratorOptions(options);
        mockGeneratorInstances.push(this);
      }
    },
  };
});

vi.mock('../../../src/contexts/SettingsContext', () => ({
  useSettings: () => ({ settings: { instruments: {} }, updateSettings: vi.fn() }),
}));

vi.mock('../../../src/contexts/ScrollContainerContext', () => ({
  useScrollContainer: () => mockScrollContainerRef,
}));

vi.mock('../../../src/api/client', () => ({
  api: { getRivalsAll: mockGetRivalsAll },
}));

vi.mock('../../../src/utils/suggestionAdapter', () => ({
  buildRivalDataIndexFromRivalsAll: mockBuildRivalDataIndex,
}));

vi.mock('../../../src/pages/rivals/helpers/comboUtils', () => ({
  deriveComboFromSettings: vi.fn().mockReturnValue('01'),
}));

type UseSuggestionsModule = typeof import('../../../src/hooks/data/useSuggestions');
let useSuggestions: UseSuggestionsModule['useSuggestions'];
let SUGGESTIONS_CATEGORY_LIMIT: UseSuggestionsModule['SUGGESTIONS_CATEGORY_LIMIT'];
let queryClient: QueryClient;

function wrapper({ children }: { children: ReactNode }) {
  return createElement(QueryClientProvider, { client: queryClient }, children);
}

describe('useSuggestions', () => {
  beforeEach(async () => {
    vi.clearAllMocks();
    vi.resetModules();
    mockGeneratorInstances.length = 0;
    mockScrollContainerRef.current = null;
    window.location.hash = '';
    mockGetNext.mockReturnValue([]);
    mockGetRivalsAll.mockResolvedValue({ accountId: 'acc1', songs: [], combos: [] });
    queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false, gcTime: Infinity },
      },
    });
    ({ useSuggestions, SUGGESTIONS_CATEGORY_LIMIT } = await import('../../../src/hooks/data/useSuggestions'));
  });

  it('returns empty categories when no songs', () => {
    const { result } = renderHook(() => useSuggestions('acc1', [], {}, 1), { wrapper });
    expect(result.current.categories).toEqual([]);
    expect(result.current.hasMore).toBe(true);
  });

  it('initializes generator and returns first batch when songs are provided', () => {
    const batch = [{ key: 'cat1', title: 'Category 1', songs: [] }];
    mockGetNext.mockReturnValue(batch);

    const songs = [{ _title: 'Song1', track: { su: 's1', tt: 'Song1', an: 'Artist' } }] as any[];
    const scores = { s1: { songId: 's1' } } as any;

    const { result } = renderHook(() => useSuggestions('acc2', songs, scores, 1), { wrapper });
    expect(mockSetSource).toHaveBeenCalledWith(songs, scores);
    expect(result.current.categories).toHaveLength(1);
    expect(result.current.categories[0]!.key).toBe('cat1');
  });

  it('loadMore fetches next batch', () => {
    const batch1 = [{ key: 'cat1', title: 'C1', songs: [] }];
    const batch2 = [{ key: 'cat2', title: 'C2', songs: [] }];
    mockGetNext.mockReturnValueOnce(batch1).mockReturnValueOnce(batch2);

    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];
    const { result } = renderHook(() => useSuggestions('acc3', songs, {}, 1), { wrapper });

    act(() => { result.current.loadMore(); });
    expect(result.current.categories).toHaveLength(2);
  });

  it('coalesces repeated load triggers until the previous batch commits', () => {
    const batch1 = [{ key: 'cat1', title: 'C1', songs: [] }];
    const batch2 = [{ key: 'cat2', title: 'C2', songs: [] }];
    const batch3 = [{ key: 'cat3', title: 'C3', songs: [] }];
    mockGetNext.mockReturnValueOnce(batch1).mockReturnValueOnce(batch2).mockReturnValueOnce(batch3);

    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];
    const { result } = renderHook(() => useSuggestions('acc-dedup', songs, {}, 1), { wrapper });

    act(() => {
      result.current.loadMore();
      result.current.loadMore();
    });
    expect(mockGetNext).toHaveBeenCalledTimes(2);
    expect(result.current.categories).toHaveLength(2);
    expect(result.current.loadTriggerCount).toBe(1);

    act(() => { result.current.loadMore(); });
    expect(mockGetNext).toHaveBeenCalledTimes(3);
    expect(result.current.categories).toHaveLength(3);
    expect(result.current.loadTriggerCount).toBe(2);
  });

  it('sets hasMore=false when generator returns empty', () => {
    const batch1 = [{ key: 'cat1', title: 'C1', songs: [] }];
    mockGetNext.mockReturnValueOnce(batch1) // initial
      .mockReturnValueOnce([]) // loadMore first try
      .mockReturnValueOnce([]); // after resetForEndless

    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];
    const { result } = renderHook(() => useSuggestions('acc4', songs, {}, 1), { wrapper });

    act(() => { result.current.loadMore(); });
    expect(mockResetForEndless).toHaveBeenCalled();
    expect(result.current.hasMore).toBe(false);
  });

  it('resets generator for endless mode when batch is empty but still has data after reset', () => {
    const batch1 = [{ key: 'cat1', title: 'C1', songs: [] }];
    const batch2 = [{ key: 'cat2', title: 'C2', songs: [] }];
    mockGetNext.mockReturnValueOnce(batch1) // initial
      .mockReturnValueOnce([]) // loadMore first try - empty
      .mockReturnValueOnce(batch2); // after resetForEndless - has data

    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];
    const { result } = renderHook(() => useSuggestions('acc5', songs, {}, 1), { wrapper });

    act(() => { result.current.loadMore(); });
    expect(mockResetForEndless).toHaveBeenCalled();
    expect(result.current.categories).toHaveLength(2);
    expect(result.current.hasMore).toBe(true);
  });

  it('loadMore does nothing before generator is ready', () => {
    const { result } = renderHook(() => useSuggestions('acc6', [], {}, 1), { wrapper });
    act(() => { result.current.loadMore(); });
    expect(result.current.categories).toEqual([]);
  });

  it('does not re-initialize when coreSongs stay the same', () => {
    const batch = [{ key: 'c1', title: 'C', songs: [] }];
    mockGetNext.mockReturnValue(batch);
    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];
    const { result, rerender } = renderHook(
      ({ s }) => useSuggestions('acc7', s, {}, 1),
      { initialProps: { s: songs }, wrapper },
    );
    expect(result.current.categories).toHaveLength(1);
    // Re-render with same songs should not re-initialize
    rerender({ s: songs });
    expect(mockSetSource).toHaveBeenCalledTimes(1);
  });

  it('caps the cached session at 1,000 categories with a partial final batch', () => {
    let categoryNumber = 0;
    let firstBatch = true;
    const requestedCounts: number[] = [];
    mockGetNext.mockImplementation((count: number) => {
      requestedCounts.push(count);
      const resultCount = firstBatch ? 9 : count;
      firstBatch = false;
      return Array.from({ length: resultCount }, () => {
        categoryNumber += 1;
        return { key: `cat-${categoryNumber}`, title: `Category ${categoryNumber}`, songs: [] };
      });
    });

    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];
    const { result } = renderHook(() => useSuggestions('acc-limit', songs, {}, 1), { wrapper });

    for (let trigger = 0; trigger < 166; trigger += 1) {
      act(() => { result.current.loadMore(); });
    }

    expect(result.current.categories).toHaveLength(SUGGESTIONS_CATEGORY_LIMIT);
    expect(result.current.limitReached).toBe(true);
    expect(result.current.hasMore).toBe(false);
    expect(result.current.loadTriggerCount).toBe(166);
    expect(requestedCounts[requestedCounts.length - 1]).toBe(1);

    const callCountAtLimit = mockGetNext.mock.calls.length;
    act(() => { result.current.loadMore(); });
    expect(mockGetNext).toHaveBeenCalledTimes(callCountAtLimit);
    expect(result.current.loadTriggerCount).toBe(166);
  });

  it('starts a fresh mix with a distinct identity and reset trigger count', () => {
    const dateNow = vi.spyOn(Date, 'now').mockReturnValue(1_000);
    let categoryNumber = 0;
    mockGetNext.mockImplementation(() => {
      categoryNumber += 1;
      return [{ key: `cat-${categoryNumber}`, title: `Category ${categoryNumber}`, songs: [] }];
    });

    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];
    const { result } = renderHook(() => useSuggestions('acc-reset', songs, {}, 1), { wrapper });
    const originalMixKey = result.current.mixKey;

    act(() => { result.current.loadMore(); });
    expect(result.current.loadTriggerCount).toBe(1);

    act(() => { result.current.startNewMix(); });
    expect(result.current.mixKey).not.toBe(originalMixKey);
    expect(result.current.categories).toHaveLength(1);
    expect(result.current.loadTriggerCount).toBe(0);
    expect(mockGeneratorOptions.mock.calls.map(([options]) => (
      (options as { seed: number }).seed
    ))).toEqual([1_001, 1_002]);
    dateNow.mockRestore();
  });

  it('reuses a cached mix and rivals query when the same source remounts', async () => {
    const batch = [{ key: 'cached', title: 'Cached', songs: [] }];
    mockGetNext.mockReturnValue(batch);
    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];

    const first = renderHook(() => useSuggestions('acc-cached', songs, {}, 1), { wrapper });
    const mixKey = first.result.current.mixKey;
    await waitFor(() => {
      expect(mockGetRivalsAll).toHaveBeenCalledTimes(1);
    });
    first.unmount();

    const second = renderHook(() => useSuggestions('acc-cached', songs, {}, 1), { wrapper });
    expect(second.result.current.mixKey).toBe(mixKey);
    expect(second.result.current.categories).toEqual(batch);
    expect(mockGeneratorOptions).toHaveBeenCalledTimes(1);
    expect(mockGetRivalsAll).toHaveBeenCalledTimes(1);
    expect(mockGeneratorInstances[0]!.setRivalData).toHaveBeenCalledTimes(1);
  });

  it('discards a cached mix when a different source commits before it is ready', () => {
    const batch = [{ key: 'cached', title: 'Cached', songs: [] }];
    mockGetNext.mockReturnValue(batch);
    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];

    const first = renderHook(() => useSuggestions('acc-first', songs, {}, 1), { wrapper });
    expect(first.result.current.mixKey).toMatch(/^solo:acc-first:mix:/);
    first.unmount();

    const second = renderHook(
      () => useSuggestions('acc-second', songs, {}, 1, { sourceReady: false }),
      { wrapper },
    );
    expect(second.result.current.mixKey).toBeNull();
    second.unmount();

    const returning = renderHook(
      () => useSuggestions('acc-first', songs, {}, 1, { sourceReady: false }),
      { wrapper },
    );
    expect(returning.result.current.mixKey).toBeNull();
    expect(returning.result.current.categories).toEqual([]);
    expect(mockGeneratorOptions).toHaveBeenCalledTimes(1);
  });

  it('injects cached rival data before a fresh mix generates categories', async () => {
    mockGetNext.mockReturnValue([{ key: 'cat', title: 'Category', songs: [] }]);
    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];
    const { result } = renderHook(
      () => useSuggestions('acc-fresh-rivals', songs, {}, 1),
      { wrapper },
    );

    await waitFor(() => {
      expect(mockGeneratorInstances[0]!.setRivalData).toHaveBeenCalledTimes(1);
    });

    act(() => {
      result.current.startNewMix();
    });

    expect(mockGeneratorInstances).toHaveLength(2);
    expect(mockGeneratorInstances[1]!.setRivalData).toHaveBeenCalledTimes(1);
    expect(mockGeneratorInstances[1]!.setRivalData.mock.invocationCallOrder[0])
      .toBeLessThan(mockGetNext.mock.invocationCallOrder[1]!);
  });

  it('injects updated rivals data without replacing the active mix', async () => {
    mockGetNext.mockReturnValue([{ key: 'cat', title: 'Category', songs: [] }]);
    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];
    const { result } = renderHook(
      () => useSuggestions('acc-updated', songs, {}, 1),
      { wrapper },
    );
    const mixKey = result.current.mixKey;

    await waitFor(() => {
      expect(mockGeneratorInstances[0]!.setRivalData).toHaveBeenCalledTimes(1);
    });

    act(() => {
      queryClient.setQueryData(queryKeys.rivalsAll('acc-updated'), {
        accountId: 'acc-updated',
        songs: [{ songId: 'song-2' }],
        combos: [],
      });
    });

    await waitFor(() => {
      expect(mockGeneratorInstances[0]!.setRivalData).toHaveBeenCalledTimes(2);
    });
    expect(result.current.mixKey).toBe(mixKey);
    expect(mockGeneratorInstances).toHaveLength(1);
  });

  it('reactivates an exhausted mix when updated rival data queues work', async () => {
    mockGetNext
      .mockReturnValueOnce([{ key: 'initial', title: 'Initial', songs: [] }])
      .mockReturnValueOnce([])
      .mockReturnValueOnce([])
      .mockReturnValueOnce([{ key: 'rival', title: 'Rival', songs: [] }]);
    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];
    const { result } = renderHook(
      () => useSuggestions('acc-reactivate', songs, {}, 1),
      { wrapper },
    );

    await waitFor(() => {
      expect(mockGeneratorInstances[0]!.setRivalData).toHaveBeenCalledTimes(1);
    });
    act(() => {
      result.current.loadMore();
    });
    expect(result.current.hasMore).toBe(false);

    act(() => {
      queryClient.setQueryData(queryKeys.rivalsAll('acc-reactivate'), {
        accountId: 'acc-reactivate',
        songs: [{ songId: 'song-2' }],
        combos: [],
      });
    });
    await waitFor(() => {
      expect(result.current.hasMore).toBe(true);
    });

    act(() => {
      result.current.loadMore();
    });
    expect(result.current.categories.map(category => category.key)).toEqual([
      'initial',
      'rival',
    ]);
  });

  it('captures the final scroll position during route cleanup', () => {
    mockGetNext.mockReturnValue([{ key: 'cat', title: 'Category', songs: [] }]);
    const scrollElement = document.createElement('div');
    mockScrollContainerRef.current = scrollElement;
    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];
    const hook = renderHook(
      () => useSuggestions('acc-scroll-cleanup', songs, {}, 1),
      { wrapper },
    );
    const mixKey = hook.result.current.mixKey!;
    scrollElement.scrollTop = 432;
    window.location.hash = '#/settings';

    hook.unmount();

    expect(getSuggestionsScrollRestoreState(mixKey)).toEqual({
      matches: true,
      restorable: false,
      scrollY: 432,
    });
  });

  it('ignores a rival response for a replaced mix', async () => {
    let resolveFirst!: (value: { accountId: string; songs: never[]; combos: never[] }) => void;
    mockGetRivalsAll
      .mockImplementationOnce(() => new Promise(resolve => { resolveFirst = resolve; }))
      .mockResolvedValueOnce({ accountId: 'acc-next', songs: [], combos: [] });
    mockGetNext.mockReturnValue([{ key: 'cat', title: 'Category', songs: [] }]);
    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];

    const { rerender } = renderHook(
      ({ accountId }) => useSuggestions(accountId, songs, {}, 1),
      { initialProps: { accountId: 'acc-stale' }, wrapper },
    );
    rerender({ accountId: 'acc-next' });
    await waitFor(() => {
      expect(mockGetRivalsAll).toHaveBeenCalledTimes(2);
    });

    await act(async () => {
      resolveFirst({ accountId: 'acc-stale', songs: [], combos: [] });
      await Promise.resolve();
    });

    expect(mockGeneratorInstances).toHaveLength(2);
    expect(mockGeneratorInstances[0]!.setRivalData).not.toHaveBeenCalled();
    await waitFor(() => {
      expect(mockGeneratorInstances[1]!.setRivalData).toHaveBeenCalledTimes(1);
    });
  });
});
