import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';

type ChangeListener = (e: { matches: boolean }) => void;

function createMockMatchMedia(initialMatches: boolean) {
  let matches = initialMatches;
  const listeners: ChangeListener[] = [];

  const mql = {
    get matches() {
      return matches;
    },
    addEventListener: (_event: string, cb: ChangeListener) => {
      listeners.push(cb);
    },
    removeEventListener: (_event: string, cb: ChangeListener) => {
      const idx = listeners.indexOf(cb);
      if (idx >= 0) listeners.splice(idx, 1);
    },
  };

  const mockFn = vi.fn(() => mql);

  return {
    mockFn,
    setMatches: (value: boolean) => {
      matches = value;
      listeners.forEach((cb) => cb({ matches: value }));
    },
    getListenerCount: () => listeners.length,
  };
}

describe('useIsMobile', () => {
  let originalMatchMedia: typeof window.matchMedia;

  beforeEach(() => {
    originalMatchMedia = window.matchMedia;
  });

  afterEach(() => {
    window.matchMedia = originalMatchMedia;
  });

  it('returns true when viewport is at or below 768px', () => {
    const { mockFn } = createMockMatchMedia(true);
    window.matchMedia = mockFn as unknown as typeof window.matchMedia;

    const { result } = renderHook(() => useIsMobile());
    expect(result.current).toBe(true);
    expect(mockFn).toHaveBeenCalledWith('(max-width: 768px)');
  });

  it('returns false when viewport is above 768px', () => {
    const { mockFn } = createMockMatchMedia(false);
    window.matchMedia = mockFn as unknown as typeof window.matchMedia;

    const { result } = renderHook(() => useIsMobile());
    expect(result.current).toBe(false);
  });

  it('updates when the media query changes from desktop to mobile', () => {
    const { mockFn, setMatches } = createMockMatchMedia(false);
    window.matchMedia = mockFn as unknown as typeof window.matchMedia;

    const { result } = renderHook(() => useIsMobile());
    expect(result.current).toBe(false);

    act(() => setMatches(true));
    expect(result.current).toBe(true);
  });

  it('updates when the media query changes from mobile to desktop', () => {
    const { mockFn, setMatches } = createMockMatchMedia(true);
    window.matchMedia = mockFn as unknown as typeof window.matchMedia;

    const { result } = renderHook(() => useIsMobile());
    expect(result.current).toBe(true);

    act(() => setMatches(false));
    expect(result.current).toBe(false);
  });

  it('cleans up listener on unmount', () => {
    const { mockFn, getListenerCount } = createMockMatchMedia(false);
    window.matchMedia = mockFn as unknown as typeof window.matchMedia;

    const { unmount } = renderHook(() => useIsMobile());
    expect(getListenerCount()).toBe(1);

    unmount();
    expect(getListenerCount()).toBe(0);
  });
});
