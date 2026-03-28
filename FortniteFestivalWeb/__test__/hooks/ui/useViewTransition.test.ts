import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useViewTransition } from '../../../src/hooks/ui/useViewTransition';

describe('useViewTransition', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  it('starts in contentIn with shouldStagger false', () => {
    const { result } = renderHook(() => useViewTransition());
    expect(result.current.phase).toBe('contentIn');
    expect(result.current.shouldStagger).toBe(false);
    expect(result.current.isTransitioning).toBe(false);
  });

  it('trigger() transitions to spinnerOut then contentIn', () => {
    const { result } = renderHook(() => useViewTransition());

    act(() => { result.current.trigger(); });
    expect(result.current.phase).toBe('spinnerOut');
    expect(result.current.shouldStagger).toBe(true);
    expect(result.current.isTransitioning).toBe(true);

    act(() => { vi.advanceTimersByTime(500); });
    expect(result.current.phase).toBe('contentIn');
    expect(result.current.shouldStagger).toBe(true);
    expect(result.current.isTransitioning).toBe(false);
  });

  it('uses custom fadeMs', () => {
    const { result } = renderHook(() => useViewTransition({ fadeMs: 200 }));

    act(() => { result.current.trigger(); });
    expect(result.current.phase).toBe('spinnerOut');

    act(() => { vi.advanceTimersByTime(199); });
    expect(result.current.phase).toBe('spinnerOut');

    act(() => { vi.advanceTimersByTime(1); });
    expect(result.current.phase).toBe('contentIn');
  });

  it('rapid trigger() during spinnerOut does not extend the timer', () => {
    const { result } = renderHook(() => useViewTransition());

    act(() => { result.current.trigger(); });
    act(() => { vi.advanceTimersByTime(300); });
    expect(result.current.phase).toBe('spinnerOut');

    // Trigger again mid-transition — phase is already spinnerOut, so no new timer starts
    act(() => { result.current.trigger(); });
    // Original timer still completes at 500ms from first trigger
    act(() => { vi.advanceTimersByTime(200); });
    expect(result.current.phase).toBe('contentIn');
  });

  it('trigger() works after a completed transition', () => {
    const { result } = renderHook(() => useViewTransition());

    // First complete transition
    act(() => { result.current.trigger(); });
    act(() => { vi.advanceTimersByTime(500); });
    expect(result.current.phase).toBe('contentIn');

    // Second transition
    act(() => { result.current.trigger(); });
    expect(result.current.phase).toBe('spinnerOut');

    act(() => { vi.advanceTimersByTime(500); });
    expect(result.current.phase).toBe('contentIn');
  });

  it('shouldStagger stays true across multiple triggers', () => {
    const { result } = renderHook(() => useViewTransition());

    act(() => { result.current.trigger(); });
    act(() => { vi.advanceTimersByTime(500); });
    expect(result.current.shouldStagger).toBe(true);

    // Trigger again — shouldStagger remains true
    act(() => { result.current.trigger(); });
    expect(result.current.shouldStagger).toBe(true);

    act(() => { vi.advanceTimersByTime(500); });
    expect(result.current.shouldStagger).toBe(true);
  });

  it('trigger() returns a stable function reference', () => {
    const { result, rerender } = renderHook(() => useViewTransition());
    const first = result.current.trigger;
    rerender();
    expect(result.current.trigger).toBe(first);
  });
});
