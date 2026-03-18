import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useAccountSearch } from '../hooks/data/useAccountSearch';

// Mock the API module
vi.mock('../api/client', () => ({
  api: {
    searchAccounts: vi.fn(),
  },
}));

import { api } from '../api/client';
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
});
