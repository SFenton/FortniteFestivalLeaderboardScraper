import { describe, it, expect } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { ReactNode } from 'react';
import { FabSearchProvider, useFabSearch, usePlayerPageSelect } from '../../src/contexts/FabSearchContext';

function wrapper({ children }: { children: ReactNode }) {
  return <FabSearchProvider>{children}</FabSearchProvider>;
}

describe('FabSearchContext', () => {
  it('provides default no-op functions', () => {
    const { result } = renderHook(() => useFabSearch(), { wrapper });
    expect(result.current.openSort).toBeInstanceOf(Function);
    expect(result.current.openFilter).toBeInstanceOf(Function);
    expect(result.current.openSuggestionsFilter).toBeInstanceOf(Function);
    expect(result.current.openPlayerHistorySort).toBeInstanceOf(Function);
    expect(result.current.openPaths).toBeInstanceOf(Function);
    expect(result.current.openPlayerQuickLinks).toBeInstanceOf(Function);
    expect(result.current.hasPlayerQuickLinks).toBe(false);
  });

  it('provides default playerPageSelect as null', () => {
    const { result } = renderHook(() => usePlayerPageSelect(), { wrapper });
    expect(result.current.playerPageSelect).toBeNull();
  });

  it('registerActions → openSort dispatches', () => {
    let sortCalled = false;
    const { result } = renderHook(() => useFabSearch(), { wrapper });
    act(() => { result.current.registerActions({ openSort: () => { sortCalled = true; }, openFilter: () => {} }); });
    act(() => { result.current.openSort(); });
    expect(sortCalled).toBe(true);
  });

  it('registerActions → openFilter dispatches', () => {
    let filterCalled = false;
    const { result } = renderHook(() => useFabSearch(), { wrapper });
    act(() => { result.current.registerActions({ openSort: () => {}, openFilter: () => { filterCalled = true; } }); });
    act(() => { result.current.openFilter(); });
    expect(filterCalled).toBe(true);
  });

  it('registerSuggestionsActions → openSuggestionsFilter dispatches', () => {
    let called = false;
    const { result } = renderHook(() => useFabSearch(), { wrapper });
    act(() => { result.current.registerSuggestionsActions({ openFilter: () => { called = true; } }); });
    act(() => { result.current.openSuggestionsFilter(); });
    expect(called).toBe(true);
  });

  it('registerPlayerHistoryActions → openPlayerHistorySort dispatches', () => {
    let called = false;
    const { result } = renderHook(() => useFabSearch(), { wrapper });
    act(() => { result.current.registerPlayerHistoryActions({ openSort: () => { called = true; } }); });
    act(() => { result.current.openPlayerHistorySort(); });
    expect(called).toBe(true);
  });

  it('registerSongDetailActions → openPaths dispatches', () => {
    let called = false;
    const { result } = renderHook(() => useFabSearch(), { wrapper });
    act(() => { result.current.registerSongDetailActions({ openPaths: () => { called = true; } }); });
    act(() => { result.current.openPaths(); });
    expect(called).toBe(true);
  });

  it('registerPlayerQuickLinks → openPlayerQuickLinks dispatches', () => {
    let called = false;
    const { result } = renderHook(() => useFabSearch(), { wrapper });
    act(() => { result.current.registerPlayerQuickLinks({ openQuickLinks: () => { called = true; } }); });
    expect(result.current.hasPlayerQuickLinks).toBe(true);
    act(() => { result.current.openPlayerQuickLinks(); });
    expect(called).toBe(true);
  });

  it('registerPlayerPageSelect stores action', () => {
    const { result } = renderHook(() => usePlayerPageSelect(), { wrapper });
    act(() => { result.current.registerPlayerPageSelect({ displayName: 'TestPlayer', onSelect: () => {} }); });
    expect(result.current.playerPageSelect?.displayName).toBe('TestPlayer');
  });

  it('registerPlayerPageSelect(null) clears action', () => {
    const { result } = renderHook(() => usePlayerPageSelect(), { wrapper });
    act(() => { result.current.registerPlayerPageSelect({ displayName: 'X', onSelect: () => {} }); });
    act(() => { result.current.registerPlayerPageSelect(null); });
    expect(result.current.playerPageSelect).toBeNull();
  });
});

describe('FabSearchContext — default context (no provider)', () => {
  it('default no-op functions can be called without error', () => {
    // Render WITHOUT wrapper to exercise the createContext default value
    const { result } = renderHook(() => useFabSearch());
    // All default functions should be callable no-ops
    expect(() => result.current.registerActions({ openSort: () => {}, openFilter: () => {} })).not.toThrow();
    expect(() => result.current.openSort()).not.toThrow();
    expect(() => result.current.openFilter()).not.toThrow();
    expect(() => result.current.registerSuggestionsActions({ openFilter: () => {} })).not.toThrow();
    expect(() => result.current.openSuggestionsFilter()).not.toThrow();
    expect(() => result.current.registerPlayerHistoryActions({ openSort: () => {} })).not.toThrow();
    expect(() => result.current.openPlayerHistorySort()).not.toThrow();
    expect(() => result.current.registerSongDetailActions({ openPaths: () => {} })).not.toThrow();
    expect(() => result.current.openPaths()).not.toThrow();
    expect(() => result.current.registerPlayerQuickLinks(null)).not.toThrow();
    expect(() => result.current.openPlayerQuickLinks()).not.toThrow();
    expect(result.current.hasPlayerQuickLinks).toBe(false);
    expect(() => result.current.registerShopActions({ toggleView: () => {} })).not.toThrow();
    expect(() => result.current.shopToggleView()).not.toThrow();
    expect(result.current.shopViewMode).toBe('grid');
    expect(() => result.current.setShopViewMode('list')).not.toThrow();
    expect(() => result.current.registerPlayerPageSelect(null)).not.toThrow();
    expect(result.current.playerPageSelect).toBeNull();
  });
});
