import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const mockApi = vi.hoisted(() => ({
  searchAccounts: vi.fn(),
}));

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

import { useAccountSearch } from '../../../src/hooks/data/useAccountSearch';

describe('useAccountSearch cancellation', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    mockApi.searchAccounts.mockReset();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('aborts the obsolete search and renders only the latest response', async () => {
    const pending = new Map<string, { resolve: (value: unknown) => void; signal: AbortSignal }>();
    mockApi.searchAccounts.mockImplementation((query: string, _limit: number, options?: { signal?: AbortSignal }) => (
      new Promise((resolve, reject) => {
        const signal = options?.signal;
        if (!signal) throw new Error('Expected an AbortSignal');
        const abort = () => reject(signal.reason ?? new DOMException('Aborted', 'AbortError'));
        signal.addEventListener('abort', abort, { once: true });
        pending.set(query, {
          signal,
          resolve: value => {
            signal.removeEventListener('abort', abort);
            resolve(value);
          },
        });
      })
    ));

    const { result } = renderHook(() => useAccountSearch(vi.fn(), { debounceMs: 10, limit: 5 }));

    act(() => result.current.handleChange('old'));
    await act(async () => vi.advanceTimersByTimeAsync(10));
    expect(pending.has('old')).toBe(true);

    act(() => result.current.handleChange('new'));
    expect(pending.get('old')?.signal.aborted).toBe(true);
    await act(async () => vi.advanceTimersByTimeAsync(10));
    expect(pending.has('new')).toBe(true);

    await act(async () => {
      pending.get('new')?.resolve({
        results: [{ accountId: 'new-account', displayName: 'Latest Player' }],
      });
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(result.current.results).toEqual([{ accountId: 'new-account', displayName: 'Latest Player' }]);
    expect(result.current.isOpen).toBe(true);
    expect(result.current.loading).toBe(false);
    expect(mockApi.searchAccounts).toHaveBeenCalledTimes(2);
    expect(mockApi.searchAccounts).toHaveBeenNthCalledWith(1, 'old', 5, {
      signal: expect.any(AbortSignal),
    });
    expect(mockApi.searchAccounts).toHaveBeenNthCalledWith(2, 'new', 5, {
      signal: expect.any(AbortSignal),
    });
  });
});
