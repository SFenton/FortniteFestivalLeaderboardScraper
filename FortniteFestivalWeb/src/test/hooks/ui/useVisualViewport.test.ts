/**
 * Tests for useVisualViewport — exercises the subscribe/getSnapshot functions
 * via renderHook which triggers useSyncExternalStore.
 */
import { describe, it, expect } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useVisualViewportHeight, useVisualViewportOffsetTop } from '../../../hooks/ui/useVisualViewport';

describe('useVisualViewportHeight', () => {
  it('returns window.innerHeight by default', () => {
    const { result } = renderHook(() => useVisualViewportHeight());
    expect(result.current).toBe(window.innerHeight);
  });

  it('updates when VisualViewport is available', () => {
    const listeners: Record<string, Function[]> = {};
    const vv = {
      height: 600,
      offsetTop: 0,
      addEventListener: (ev: string, cb: Function) => {
        listeners[ev] = listeners[ev] || [];
        listeners[ev].push(cb);
      },
      removeEventListener: (ev: string, cb: Function) => {
        listeners[ev] = (listeners[ev] || []).filter(fn => fn !== cb);
      },
    };
    Object.defineProperty(window, 'visualViewport', { value: vv, writable: true, configurable: true });

    const { result } = renderHook(() => useVisualViewportHeight());
    expect(result.current).toBe(600);

    vv.height = 400;
    act(() => { (listeners['resize'] || []).forEach(fn => fn()); });
    expect(result.current).toBe(400);

    Object.defineProperty(window, 'visualViewport', { value: undefined, writable: true, configurable: true });
  });
});

describe('useVisualViewportOffsetTop', () => {
  it('returns 0 by default', () => {
    const { result } = renderHook(() => useVisualViewportOffsetTop());
    expect(result.current).toBe(0);
  });

  it('returns offsetTop when VisualViewport available', () => {
    const listeners: Record<string, Function[]> = {};
    const vv = {
      height: 600,
      offsetTop: 50,
      addEventListener: (ev: string, cb: Function) => {
        listeners[ev] = listeners[ev] || [];
        listeners[ev].push(cb);
      },
      removeEventListener: () => {},
    };
    Object.defineProperty(window, 'visualViewport', { value: vv, writable: true, configurable: true });

    const { result } = renderHook(() => useVisualViewportOffsetTop());
    expect(result.current).toBe(50);

    Object.defineProperty(window, 'visualViewport', { value: undefined, writable: true, configurable: true });
  });
});

describe('useVisualViewportHeight — fallback', () => {
  it('falls back to window resize when no visualViewport', () => {
    const origVV = window.visualViewport;
    Object.defineProperty(window, 'visualViewport', { value: null, writable: true, configurable: true });

    const { result, unmount } = renderHook(() => useVisualViewportHeight());
    expect(typeof result.current).toBe('number');
    unmount();

    Object.defineProperty(window, 'visualViewport', { value: origVV, writable: true, configurable: true });
  });
});
