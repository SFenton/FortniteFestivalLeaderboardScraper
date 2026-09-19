import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TRANSITION_MS } from '@festival/theme';
import { useInitialAppReveal } from '../../../src/hooks/ui/useInitialAppReveal';

let nextFrameId = 1;
let frameCallbacks = new Map<number, FrameRequestCallback>();

function setReducedMotion(matches: boolean) {
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    writable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: query === '(prefers-reduced-motion: reduce)' ? matches : false,
      media: query,
      onchange: null,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });
}

function runNextFrame() {
  const next = frameCallbacks.entries().next().value as [number, FrameRequestCallback] | undefined;
  if (!next) throw new Error('Expected a pending animation frame');
  const [id, callback] = next;
  frameCallbacks.delete(id);
  act(() => callback(performance.now()));
}

describe('useInitialAppReveal', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    nextFrameId = 1;
    frameCallbacks = new Map();
    setReducedMotion(false);
    vi.stubGlobal('requestAnimationFrame', vi.fn((callback: FrameRequestCallback) => {
      const id = nextFrameId++;
      frameCallbacks.set(id, callback);
      return id;
    }));
    vi.stubGlobal('cancelAnimationFrame', vi.fn((id: number) => {
      frameCallbacks.delete(id);
    }));
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('reveals on the next frame and latches after the splash transition', () => {
    const { result, rerender } = renderHook(
      ({ ready }) => useInitialAppReveal(ready),
      { initialProps: { ready: false } },
    );

    expect(result.current.phase).toBe('waiting');
    rerender({ ready: true });
    expect(result.current.phase).toBe('waiting');

    runNextFrame();
    expect(result.current.phase).toBe('revealing');

    act(() => result.current.complete());
    expect(result.current.phase).toBe('entered');

    rerender({ ready: false });
    expect(result.current.phase).toBe('entered');
  });

  it('uses a bounded fallback when transitionend is unavailable', () => {
    const { result } = renderHook(() => useInitialAppReveal(true));
    runNextFrame();
    expect(result.current.phase).toBe('revealing');

    act(() => vi.advanceTimersByTime(TRANSITION_MS + 99));
    expect(result.current.phase).toBe('revealing');

    act(() => vi.advanceTimersByTime(1));
    expect(result.current.phase).toBe('entered');
  });

  it('enters directly when reduced motion is requested', () => {
    setReducedMotion(true);
    const { result } = renderHook(() => useInitialAppReveal(true));

    expect(result.current.phase).toBe('entered');
    expect(requestAnimationFrame).not.toHaveBeenCalled();
  });

  it('cancels a pending reveal frame when readiness is withdrawn', () => {
    const { result, rerender } = renderHook(
      ({ ready }) => useInitialAppReveal(ready),
      { initialProps: { ready: false } },
    );

    rerender({ ready: true });
    expect(frameCallbacks.size).toBe(1);
    rerender({ ready: false });

    expect(cancelAnimationFrame).toHaveBeenCalledTimes(1);
    expect(frameCallbacks.size).toBe(0);
    expect(result.current.phase).toBe('waiting');
  });
});
