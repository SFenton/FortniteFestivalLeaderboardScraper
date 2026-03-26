import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useScrollRestore, clearScrollCache } from '../../../src/hooks/ui/useScrollRestore';

describe('useScrollRestore', () => {
  beforeEach(() => {
    clearScrollCache();
    vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
    Object.defineProperty(window, 'scrollY', { value: 0, writable: true, configurable: true });
    Object.defineProperty(document.documentElement, 'scrollHeight', { value: 5000, writable: true, configurable: true });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('saves scroll position via returned function', () => {
    const { result } = renderHook(() => useScrollRestore('test-key', 'PUSH'));

    Object.defineProperty(window, 'scrollY', { value: 300, writable: true, configurable: true });
    act(() => { result.current(); });

    // Verify by mounting a new hook — the restore effect calls window.scrollTo
    renderHook(() => useScrollRestore('test-key', 'POP'));
    expect(window.scrollTo).toHaveBeenCalledWith(0, 300);
  });

  it('preserves stored position on PUSH navigation', () => {
    const { result } = renderHook(() => useScrollRestore('test-key', 'POP'));
    Object.defineProperty(window, 'scrollY', { value: 500, writable: true, configurable: true });
    act(() => { result.current(); });

    renderHook(() => useScrollRestore('test-key', 'PUSH'));
    expect(window.scrollTo).toHaveBeenCalledWith(0, 500);
  });

  it('clearScrollCache clears all entries', () => {
    const { result } = renderHook(() => useScrollRestore('key1', 'POP'));
    Object.defineProperty(window, 'scrollY', { value: 100, writable: true, configurable: true });
    act(() => { result.current(); });

    clearScrollCache();

    const scrollToSpy = vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
    scrollToSpy.mockClear();
    renderHook(() => useScrollRestore('key1', 'POP'));
    expect(scrollToSpy).not.toHaveBeenCalledWith(0, 100);
  });

  it('clearScrollCache with specific key', () => {
    const { result: r1 } = renderHook(() => useScrollRestore('key-a', 'POP'));
    Object.defineProperty(window, 'scrollY', { value: 200, writable: true, configurable: true });
    act(() => { r1.current(); });

    const { result: r2 } = renderHook(() => useScrollRestore('key-b', 'POP'));
    Object.defineProperty(window, 'scrollY', { value: 400, writable: true, configurable: true });
    act(() => { r2.current(); });

    clearScrollCache('key-a');

    const scrollToSpy = vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
    scrollToSpy.mockClear();
    renderHook(() => useScrollRestore('key-a', 'POP'));
    expect(scrollToSpy).not.toHaveBeenCalledWith(0, 200);
  });
});
