import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useLoadPhase } from '../../hooks/data/useLoadPhase';

describe('useLoadPhase', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  it('starts in loading when not ready', () => {
    const { result } = renderHook(() => useLoadPhase(false));
    expect(result.current.phase).toBe('loading');
    expect(result.current.shouldStagger).toBe(true);
  });

  it('starts in contentIn when skipAnimation is true', () => {
    const { result } = renderHook(() => useLoadPhase(false, { skipAnimation: true }));
    expect(result.current.phase).toBe('contentIn');
    expect(result.current.shouldStagger).toBe(false);
  });

  it('starts in contentIn when already ready', () => {
    const { result } = renderHook(() => useLoadPhase(true));
    // Should immediately transition to spinnerOut then contentIn
    expect(['spinnerOut', 'contentIn']).toContain(result.current.phase);
  });

  it('transitions loading → spinnerOut → contentIn when data becomes ready', () => {
    const { result, rerender } = renderHook(
      ({ ready }: { ready: boolean }) => useLoadPhase(ready),
      { initialProps: { ready: false } },
    );

    expect(result.current.phase).toBe('loading');

    // Data becomes ready
    rerender({ ready: true });
    expect(result.current.phase).toBe('spinnerOut');

    // Wait for spinner fade
    act(() => { vi.advanceTimersByTime(500); });
    expect(result.current.phase).toBe('contentIn');
  });

  it('resets to loading when data becomes unready', () => {
    const { result, rerender } = renderHook(
      ({ ready }: { ready: boolean }) => useLoadPhase(ready),
      { initialProps: { ready: true } },
    );

    // Let it reach contentIn
    act(() => { vi.advanceTimersByTime(500); });
    expect(result.current.phase).toBe('contentIn');

    // Data becomes unready (error/reload)
    rerender({ ready: false });
    expect(result.current.phase).toBe('loading');
  });

  it('uses custom spinnerFadeMs', () => {
    const { result, rerender } = renderHook(
      ({ ready }: { ready: boolean }) => useLoadPhase(ready, { spinnerFadeMs: 200 }),
      { initialProps: { ready: false } },
    );

    rerender({ ready: true });
    expect(result.current.phase).toBe('spinnerOut');

    act(() => { vi.advanceTimersByTime(200); });
    expect(result.current.phase).toBe('contentIn');
  });
});
