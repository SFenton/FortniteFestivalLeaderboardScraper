import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';

const mockGetNext = vi.fn().mockReturnValue([]);
const mockSetSource = vi.fn();
const mockResetForEndless = vi.fn();

vi.mock('@festival/core/suggestions/suggestionGenerator', () => {
  return {
    SuggestionGenerator: class {
      setSource = mockSetSource;
      getNext = mockGetNext;
      resetForEndless = mockResetForEndless;
    },
  };
});

// Import AFTER mock is set up
import { useSuggestions } from '../../../hooks/data/useSuggestions';

describe('useSuggestions', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockGetNext.mockReturnValue([]);
    // Reset module cache by dynamically re-importing
    vi.resetModules();
  });

  it('returns empty categories when no songs', () => {
    const { result } = renderHook(() => useSuggestions('acc1', [], {}, 1));
    expect(result.current.categories).toEqual([]);
    expect(result.current.hasMore).toBe(true);
  });

  it('initializes generator and returns first batch when songs are provided', () => {
    const batch = [{ key: 'cat1', title: 'Category 1', songs: [] }];
    mockGetNext.mockReturnValue(batch);

    const songs = [{ _title: 'Song1', track: { su: 's1', tt: 'Song1', an: 'Artist' } }] as any[];
    const scores = { s1: { songId: 's1' } } as any;

    const { result } = renderHook(() => useSuggestions('acc2', songs, scores, 1));
    expect(mockSetSource).toHaveBeenCalledWith(songs, scores);
    expect(result.current.categories).toHaveLength(1);
    expect(result.current.categories[0]!.key).toBe('cat1');
  });

  it('loadMore fetches next batch', () => {
    const batch1 = [{ key: 'cat1', title: 'C1', songs: [] }];
    const batch2 = [{ key: 'cat2', title: 'C2', songs: [] }];
    mockGetNext.mockReturnValueOnce(batch1).mockReturnValueOnce(batch2);

    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];
    const { result } = renderHook(() => useSuggestions('acc3', songs, {}, 1));

    act(() => { result.current.loadMore(); });
    expect(result.current.categories).toHaveLength(2);
  });

  it('sets hasMore=false when generator returns empty', () => {
    const batch1 = [{ key: 'cat1', title: 'C1', songs: [] }];
    mockGetNext.mockReturnValueOnce(batch1) // initial
      .mockReturnValueOnce([]) // loadMore first try
      .mockReturnValueOnce([]); // after resetForEndless

    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];
    const { result } = renderHook(() => useSuggestions('acc4', songs, {}, 1));

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
    const { result } = renderHook(() => useSuggestions('acc5', songs, {}, 1));

    act(() => { result.current.loadMore(); });
    expect(mockResetForEndless).toHaveBeenCalled();
    expect(result.current.categories).toHaveLength(2);
    expect(result.current.hasMore).toBe(true);
  });

  it('loadMore does nothing before generator is ready', () => {
    const { result } = renderHook(() => useSuggestions('acc6', [], {}, 1));
    act(() => { result.current.loadMore(); });
    expect(result.current.categories).toEqual([]);
  });

  it('does not re-initialize when coreSongs stay the same', () => {
    const batch = [{ key: 'c1', title: 'C', songs: [] }];
    mockGetNext.mockReturnValue(batch);
    const songs = [{ _title: 'S', track: { su: 's1', tt: 'S', an: 'A' } }] as any[];
    const { result, rerender } = renderHook(
      ({ s }) => useSuggestions('acc7', s, {}, 1),
      { initialProps: { s: songs } },
    );
    expect(result.current.categories).toHaveLength(1);
    // Re-render with same songs should not re-initialize
    rerender({ s: songs });
    expect(mockSetSource).toHaveBeenCalledTimes(1);
  });
});
