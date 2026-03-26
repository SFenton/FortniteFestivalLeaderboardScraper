import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useScrollRestore, clearScrollCache } from '../../../src/hooks/ui/useScrollRestore';
import { createScrollContainerWrapper } from '../../Helpers/scrollContainerWrapper';

describe('useScrollRestore', () => {
  beforeEach(() => {
    clearScrollCache();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('saves scroll position via returned function', () => {
    const { wrapper, mockEl } = createScrollContainerWrapper();
    const scrollToSpy = vi.fn();
    mockEl.scrollTo = scrollToSpy;

    const { result } = renderHook(() => useScrollRestore('test-key', 'PUSH'), { wrapper });

    Object.defineProperty(mockEl, 'scrollTop', { value: 300, writable: true, configurable: true });
    act(() => { result.current(); });

    // Verify by mounting a new hook — the restore effect calls container.scrollTo
    scrollToSpy.mockClear();
    renderHook(() => useScrollRestore('test-key', 'POP'), { wrapper });
    expect(scrollToSpy).toHaveBeenCalledWith(0, 300);
  });

  it('preserves stored position on PUSH navigation', () => {
    const { wrapper, mockEl } = createScrollContainerWrapper();
    const scrollToSpy = vi.fn();
    mockEl.scrollTo = scrollToSpy;

    const { result } = renderHook(() => useScrollRestore('test-key', 'POP'), { wrapper });
    Object.defineProperty(mockEl, 'scrollTop', { value: 500, writable: true, configurable: true });
    act(() => { result.current(); });

    scrollToSpy.mockClear();
    renderHook(() => useScrollRestore('test-key', 'PUSH'), { wrapper });
    expect(scrollToSpy).toHaveBeenCalledWith(0, 500);
  });

  it('clearScrollCache clears all entries', () => {
    const { wrapper, mockEl } = createScrollContainerWrapper();
    const scrollToSpy = vi.fn();
    mockEl.scrollTo = scrollToSpy;

    const { result } = renderHook(() => useScrollRestore('key1', 'POP'), { wrapper });
    Object.defineProperty(mockEl, 'scrollTop', { value: 100, writable: true, configurable: true });
    act(() => { result.current(); });

    clearScrollCache();

    scrollToSpy.mockClear();
    renderHook(() => useScrollRestore('key1', 'POP'), { wrapper });
    expect(scrollToSpy).not.toHaveBeenCalledWith(0, 100);
  });

  it('clearScrollCache with specific key', () => {
    const { wrapper, mockEl } = createScrollContainerWrapper();
    const scrollToSpy = vi.fn();
    mockEl.scrollTo = scrollToSpy;

    const { result: r1 } = renderHook(() => useScrollRestore('key-a', 'POP'), { wrapper });
    Object.defineProperty(mockEl, 'scrollTop', { value: 200, writable: true, configurable: true });
    act(() => { r1.current(); });

    const { result: r2 } = renderHook(() => useScrollRestore('key-b', 'POP'), { wrapper });
    Object.defineProperty(mockEl, 'scrollTop', { value: 400, writable: true, configurable: true });
    act(() => { r2.current(); });

    clearScrollCache('key-a');

    scrollToSpy.mockClear();
    renderHook(() => useScrollRestore('key-a', 'POP'), { wrapper });
    expect(scrollToSpy).not.toHaveBeenCalledWith(0, 200);
  });
});
