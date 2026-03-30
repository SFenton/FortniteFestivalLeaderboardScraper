import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useAccountSearch } from '../../../src/hooks/data/useAccountSearch';

// Mock the API module
vi.mock('../../../src/api/client', () => ({
  api: {
    searchAccounts: vi.fn(),
  },
}));

import { api } from '../../../src/api/client';
const mockSearch = vi.mocked(api.searchAccounts);

describe('useAccountSearch', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('starts with empty state', () => {
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect));
    expect(result.current.query).toBe('');
    expect(result.current.results).toEqual([]);
    expect(result.current.isOpen).toBe(false);
    expect(result.current.loading).toBe(false);
  });

  it('does not search for queries shorter than 2 chars', async () => {
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect));

    act(() => { result.current.handleChange('a'); });
    await act(async () => { vi.advanceTimersByTime(300); });
    expect(mockSearch).not.toHaveBeenCalled();
  });

  it('debounces search calls', async () => {
    mockSearch.mockResolvedValue({ results: [{ accountId: '1', displayName: 'Test' }] });
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect));

    act(() => { result.current.handleChange('te'); });
    act(() => { result.current.handleChange('tes'); });
    act(() => { result.current.handleChange('test'); });

    // Only the last debounce fires
    await act(async () => { vi.advanceTimersByTime(300); });
    expect(mockSearch).toHaveBeenCalledTimes(1);
    expect(mockSearch).toHaveBeenCalledWith('test', 10);
  });

  it('calls onSelect when selectResult is called', () => {
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect));
    const fakeResult = { accountId: 'abc', displayName: 'Player1' };

    act(() => { result.current.selectResult(fakeResult); });
    expect(onSelect).toHaveBeenCalledWith(fakeResult);
    expect(result.current.query).toBe('');
    expect(result.current.isOpen).toBe(false);
  });

  it('closes dropdown on close()', () => {
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect));

    act(() => { result.current.close(); });
    expect(result.current.isOpen).toBe(false);
  });

  it('opens results when search succeeds', async () => {
    mockSearch.mockResolvedValue({ results: [{ accountId: 'a1', displayName: 'Player' }] });
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 10 }));

    act(() => { result.current.handleChange('Pla'); });
    await act(async () => { vi.advanceTimersByTime(10); });
    await act(async () => { await vi.runAllTimersAsync(); });

    expect(result.current.isOpen).toBe(true);
    expect(result.current.results).toHaveLength(1);
  });

  it('handles search error gracefully', async () => {
    mockSearch.mockRejectedValue(new Error('Network error'));
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 10 }));

    act(() => { result.current.handleChange('Test'); });
    await act(async () => { vi.advanceTimersByTime(10); });
    await act(async () => { await vi.runAllTimersAsync(); });

    expect(result.current.isOpen).toBe(false);
    expect(result.current.results).toEqual([]);
  });

  it('handleKeyDown ArrowDown increments activeIndex', async () => {
    mockSearch.mockResolvedValue({ results: [{ accountId: 'a1', displayName: 'A' }, { accountId: 'a2', displayName: 'B' }] });
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 10 }));

    act(() => { result.current.handleChange('AB'); });
    await act(async () => { vi.advanceTimersByTime(10); });
    await act(async () => { await vi.runAllTimersAsync(); });

    const prevent = vi.fn();
    act(() => { result.current.handleKeyDown({ key: 'ArrowDown', preventDefault: prevent } as any); });
    expect(result.current.activeIndex).toBe(0);
    expect(prevent).toHaveBeenCalled();
  });

  it('handleKeyDown ArrowUp decrements activeIndex', async () => {
    mockSearch.mockResolvedValue({ results: [{ accountId: 'a1', displayName: 'A' }, { accountId: 'a2', displayName: 'B' }] });
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 10 }));

    act(() => { result.current.handleChange('AB'); });
    await act(async () => { vi.advanceTimersByTime(10); });
    await act(async () => { await vi.runAllTimersAsync(); });

    // Go down, then up
    act(() => { result.current.handleKeyDown({ key: 'ArrowDown', preventDefault: vi.fn() } as any); });
    act(() => { result.current.handleKeyDown({ key: 'ArrowUp', preventDefault: vi.fn() } as any); });
    expect(result.current.activeIndex).toBe(1); // wraps to last
  });

  it('handleKeyDown Enter selects active result', async () => {
    const item = { accountId: 'a1', displayName: 'A' };
    mockSearch.mockResolvedValue({ results: [item] });
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 10 }));

    act(() => { result.current.handleChange('AB'); });
    await act(async () => { vi.advanceTimersByTime(10); });
    await act(async () => { await vi.runAllTimersAsync(); });

    act(() => { result.current.handleKeyDown({ key: 'ArrowDown', preventDefault: vi.fn() } as any); });
    act(() => { result.current.handleKeyDown({ key: 'Enter', preventDefault: vi.fn() } as any); });
    expect(onSelect).toHaveBeenCalledWith(item);
  });

  it('handleKeyDown Escape closes', async () => {
    mockSearch.mockResolvedValue({ results: [{ accountId: 'a1', displayName: 'A' }] });
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 10 }));

    act(() => { result.current.handleChange('AB'); });
    await act(async () => { vi.advanceTimersByTime(10); });
    await act(async () => { await vi.runAllTimersAsync(); });

    act(() => { result.current.handleKeyDown({ key: 'Escape' } as any); });
    expect(result.current.isOpen).toBe(false);
  });

  it('does nothing on keydown when closed', () => {
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect));
    act(() => { result.current.handleKeyDown({ key: 'ArrowDown', preventDefault: vi.fn() } as any); });
    expect(result.current.activeIndex).toBe(-1);
  });
});

describe('useAccountSearch — additional branch coverage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('ArrowDown wraps around at end of results', async () => {
    mockSearch.mockResolvedValue({ results: [
      { accountId: 'a1', displayName: 'A' },
      { accountId: 'a2', displayName: 'B' },
    ] });
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 10 }));

    act(() => { result.current.handleChange('AB'); });
    await act(async () => { vi.advanceTimersByTime(10); });
    await act(async () => { await vi.runAllTimersAsync(); });

    // Press ArrowDown 3 times with 2 results → wraps to 0
    act(() => { result.current.handleKeyDown({ key: 'ArrowDown', preventDefault: vi.fn() } as any); });
    act(() => { result.current.handleKeyDown({ key: 'ArrowDown', preventDefault: vi.fn() } as any); });
    act(() => { result.current.handleKeyDown({ key: 'ArrowDown', preventDefault: vi.fn() } as any); });
    expect(result.current.activeIndex).toBe(0);
  });

  it('Enter does nothing when activeIndex < 0', async () => {
    mockSearch.mockResolvedValue({ results: [{ accountId: 'a1', displayName: 'A' }] });
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 10 }));

    act(() => { result.current.handleChange('AB'); });
    await act(async () => { vi.advanceTimersByTime(10); });
    await act(async () => { await vi.runAllTimersAsync(); });

    // Don't press ArrowDown first → activeIndex stays at -1
    act(() => { result.current.handleKeyDown({ key: 'Enter', preventDefault: vi.fn() } as any); });
    expect(onSelect).not.toHaveBeenCalled();
  });

  it('unrecognized key does nothing while dropdown is open', async () => {
    mockSearch.mockResolvedValue({ results: [{ accountId: 'a1', displayName: 'A' }] });
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 10 }));

    act(() => { result.current.handleChange('AB'); });
    await act(async () => { vi.advanceTimersByTime(10); });
    await act(async () => { await vi.runAllTimersAsync(); });

    const prevent = vi.fn();
    act(() => { result.current.handleKeyDown({ key: 'a', preventDefault: prevent } as any); });
    expect(prevent).not.toHaveBeenCalled();
    expect(result.current.isOpen).toBe(true);
  });

  it('uses custom limit when explicitly provided', async () => {
    mockSearch.mockResolvedValue({ results: [{ accountId: 'a1', displayName: 'A' }] });
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 10, limit: 5 }));

    act(() => { result.current.handleChange('test'); });
    await act(async () => { vi.advanceTimersByTime(10); });
    await act(async () => { await vi.runAllTimersAsync(); });

    expect(mockSearch).toHaveBeenCalledWith('test', 5);
  });

  it('ArrowUp decrements from non-zero index without wrapping', async () => {
    mockSearch.mockResolvedValue({ results: [
      { accountId: 'a1', displayName: 'A' },
      { accountId: 'a2', displayName: 'B' },
    ] });
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 10 }));

    act(() => { result.current.handleChange('AB'); });
    await act(async () => { vi.advanceTimersByTime(10); });
    await act(async () => { await vi.runAllTimersAsync(); });

    // Move to index 1
    act(() => { result.current.handleKeyDown({ key: 'ArrowDown', preventDefault: vi.fn() } as any); });
    act(() => { result.current.handleKeyDown({ key: 'ArrowDown', preventDefault: vi.fn() } as any); });
    expect(result.current.activeIndex).toBe(1);

    // ArrowUp from 1 → 0 (decrement, not wrap)
    act(() => { result.current.handleKeyDown({ key: 'ArrowUp', preventDefault: vi.fn() } as any); });
    expect(result.current.activeIndex).toBe(0);
  });
});

describe('useAccountSearch — debouncing & resultSeq', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('debouncing is true while waiting for debounce timer', () => {
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 100 }));

    act(() => { result.current.handleChange('test'); });
    expect(result.current.debouncing).toBe(true);
  });

  it('debouncing becomes false when search starts', async () => {
    mockSearch.mockResolvedValue({ results: [{ accountId: 'a1', displayName: 'A' }] });
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 10 }));

    act(() => { result.current.handleChange('test'); });
    expect(result.current.debouncing).toBe(true);

    await act(async () => { vi.advanceTimersByTime(10); });
    await act(async () => { await vi.runAllTimersAsync(); });

    expect(result.current.debouncing).toBe(false);
    expect(result.current.loading).toBe(false);
  });

  it('debouncing resets to false when query drops below 2 chars', () => {
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 100 }));

    act(() => { result.current.handleChange('test'); });
    expect(result.current.debouncing).toBe(true);

    act(() => { result.current.handleChange('t'); });
    expect(result.current.debouncing).toBe(false);
  });

  it('debouncing stays false for short queries', () => {
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 100 }));

    act(() => { result.current.handleChange('a'); });
    expect(result.current.debouncing).toBe(false);
  });

  it('resultSeq increments on each successful search', async () => {
    mockSearch.mockResolvedValue({ results: [{ accountId: 'a1', displayName: 'A' }] });
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 10 }));

    expect(result.current.resultSeq).toBe(0);

    act(() => { result.current.handleChange('test'); });
    await act(async () => { vi.advanceTimersByTime(10); });
    await act(async () => { await vi.runAllTimersAsync(); });
    expect(result.current.resultSeq).toBe(1);

    act(() => { result.current.handleChange('test2'); });
    await act(async () => { vi.advanceTimersByTime(10); });
    await act(async () => { await vi.runAllTimersAsync(); });
    expect(result.current.resultSeq).toBe(2);
  });

  it('resultSeq does not increment on search error', async () => {
    mockSearch.mockRejectedValue(new Error('fail'));
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 10 }));

    act(() => { result.current.handleChange('test'); });
    await act(async () => { vi.advanceTimersByTime(10); });
    await act(async () => { await vi.runAllTimersAsync(); });

    expect(result.current.resultSeq).toBe(0);
  });
});
