/**
 * FabSearchContext — exercises all register/open functions.
 */
import { describe, it, expect, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { ReactNode } from 'react';
import { FabSearchProvider, useFabSearch, usePlayerPageSelect } from '../../src/contexts/FabSearchContext';

function wrapper({ children }: { children: ReactNode }) {
  return <FabSearchProvider>{children}</FabSearchProvider>;
}

describe('FabSearchContext', () => {
  it('provides default no-op functions', () => {
    const { result } = renderHook(() => useFabSearch(), { wrapper });
    expect(typeof result.current.openSort).toBe('function');
    expect(typeof result.current.openFilter).toBe('function');
    expect(typeof result.current.openSuggestionsFilter).toBe('function');
    expect(typeof result.current.openPlayerHistorySort).toBe('function');
    expect(typeof result.current.openPaths).toBe('function');
  });

  it('registerActions + openSort calls registered sort', () => {
    const { result } = renderHook(() => useFabSearch(), { wrapper });
    const mockSort = vi.fn();
    const mockFilter = vi.fn();
    act(() => result.current.registerActions({ openSort: mockSort, openFilter: mockFilter }));
    act(() => result.current.openSort());
    expect(mockSort).toHaveBeenCalledTimes(1);
  });

  it('registerActions + openFilter calls registered filter', () => {
    const { result } = renderHook(() => useFabSearch(), { wrapper });
    const mockFilter = vi.fn();
    act(() => result.current.registerActions({ openSort: vi.fn(), openFilter: mockFilter }));
    act(() => result.current.openFilter());
    expect(mockFilter).toHaveBeenCalledTimes(1);
  });

  it('registerSuggestionsActions + openSuggestionsFilter works', () => {
    const { result } = renderHook(() => useFabSearch(), { wrapper });
    const mockFilter = vi.fn();
    act(() => result.current.registerSuggestionsActions({ openFilter: mockFilter }));
    act(() => result.current.openSuggestionsFilter());
    expect(mockFilter).toHaveBeenCalledTimes(1);
  });

  it('registerPlayerHistoryActions + openPlayerHistorySort works', () => {
    const { result } = renderHook(() => useFabSearch(), { wrapper });
    const mockSort = vi.fn();
    act(() => result.current.registerPlayerHistoryActions({ openSort: mockSort }));
    act(() => result.current.openPlayerHistorySort());
    expect(mockSort).toHaveBeenCalledTimes(1);
  });

  it('registerSongDetailActions + openPaths works', () => {
    const { result } = renderHook(() => useFabSearch(), { wrapper });
    const mockPaths = vi.fn();
    act(() => result.current.registerSongDetailActions({ openPaths: mockPaths }));
    act(() => result.current.openPaths());
    expect(mockPaths).toHaveBeenCalledTimes(1);
  });

  it('registerPlayerPageSelect sets and clears playerPageSelect', () => {
    const { result } = renderHook(() => usePlayerPageSelect(), { wrapper });
    expect(result.current.playerPageSelect).toBeNull();

    const action = { displayName: 'Test', onSelect: vi.fn() };
    act(() => result.current.registerPlayerPageSelect(action));
    expect(result.current.playerPageSelect).toBe(action);

    act(() => result.current.registerPlayerPageSelect(null));
    expect(result.current.playerPageSelect).toBeNull();
  });

  it('default opens do not throw before registration', () => {
    const { result } = renderHook(() => useFabSearch(), { wrapper });
    expect(() => { act(() => result.current.openSort()); }).not.toThrow();
    expect(() => { act(() => result.current.openFilter()); }).not.toThrow();
    expect(() => { act(() => result.current.openSuggestionsFilter()); }).not.toThrow();
    expect(() => { act(() => result.current.openPlayerHistorySort()); }).not.toThrow();
    expect(() => { act(() => result.current.openPaths()); }).not.toThrow();
  });
});
