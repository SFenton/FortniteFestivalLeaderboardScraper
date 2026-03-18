import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useAccountSearch } from '../../hooks/data/useAccountSearch';

vi.mock('../../api/client', () => ({
  api: {
    searchAccounts: vi.fn(),
  },
}));

import { api } from '../../api/client';
const mockSearch = vi.mocked(api.searchAccounts);

describe('useAccountSearch', () => {
  beforeEach(() => { vi.useFakeTimers(); mockSearch.mockReset(); });
  afterEach(() => { vi.useRealTimers(); });

  it('initializes with empty state', () => {
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect));
    expect(result.current.query).toBe('');
    expect(result.current.results).toEqual([]);
    expect(result.current.isOpen).toBe(false);
    expect(result.current.loading).toBe(false);
    expect(result.current.activeIndex).toBe(-1);
  });

  it('debounces search on handleChange', async () => {
    mockSearch.mockResolvedValue({ results: [{ accountId: 'a1', displayName: 'Player' }] });
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 100 }));

    act(() => { result.current.handleChange('Pla'); });
    expect(result.current.query).toBe('Pla');
    expect(mockSearch).not.toHaveBeenCalled();

    await act(async () => { vi.advanceTimersByTime(100); });
    expect(mockSearch).toHaveBeenCalledWith('Pla', 10);
  });

  it('does not search for queries shorter than 2 chars', async () => {
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 50 }));

    act(() => { result.current.handleChange('P'); });
    await act(async () => { vi.advanceTimersByTime(50); });
    expect(mockSearch).not.toHaveBeenCalled();
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

  it('selectResult calls onSelect and resets state', () => {
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect));
    const item = { accountId: 'a1', displayName: 'Player' };

    act(() => { result.current.selectResult(item); });
    expect(onSelect).toHaveBeenCalledWith(item);
    expect(result.current.query).toBe('');
    expect(result.current.isOpen).toBe(false);
  });

  it('close sets isOpen to false', () => {
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect));
    act(() => { result.current.close(); });
    expect(result.current.isOpen).toBe(false);
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
