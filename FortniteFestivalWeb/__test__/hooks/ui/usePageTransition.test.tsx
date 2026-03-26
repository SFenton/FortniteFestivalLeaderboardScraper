import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { usePageTransition, clearPageTransitionCache } from '../../../src/hooks/ui/usePageTransition';
import { LoadPhase } from '@festival/core';

function wrapper({ children }: { children: React.ReactNode }) {
  return <MemoryRouter>{children}</MemoryRouter>;
}

describe('usePageTransition', () => {
  beforeEach(() => {
    clearPageTransitionCache();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('returns Loading phase when data is not ready', () => {
    const { result } = renderHook(
      () => usePageTransition('test-key', false),
      { wrapper },
    );
    expect(result.current.phase).toBe(LoadPhase.Loading);
    expect(result.current.shouldStagger).toBe(true);
  });

  it('transitions to ContentIn when data becomes ready', async () => {
    const { result, rerender } = renderHook(
      ({ ready }) => usePageTransition('test-key', ready),
      { wrapper, initialProps: { ready: false } },
    );
    expect(result.current.phase).toBe(LoadPhase.Loading);

    rerender({ ready: true });
    expect(result.current.phase).toBe(LoadPhase.SpinnerOut);

    // Advance past spinner fade (500ms + buffer)
    await act(async () => { vi.advanceTimersByTime(600); });
    expect(result.current.phase).toBe(LoadPhase.ContentIn);
  });

  it('skips animation on return visit (key already visited + POP)', async () => {
    // First visit
    const { unmount } = renderHook(
      () => usePageTransition('return-key', true),
      { wrapper },
    );
    await act(async () => { vi.advanceTimersByTime(600); });
    unmount();

    // Second visit with cached data
    const { result } = renderHook(
      () => usePageTransition('return-key', true, true),
      { wrapper },
    );
    expect(result.current.phase).toBe(LoadPhase.ContentIn);
  });

  it('clearPageTransitionCache clears all visited keys', async () => {
    renderHook(() => usePageTransition('key1', true), { wrapper });
    await act(async () => { vi.advanceTimersByTime(600); });
    clearPageTransitionCache();

    const { result } = renderHook(
      () => usePageTransition('key1', true, true),
      { wrapper },
    );
    // After clearing, should stagger again
    expect(result.current.shouldStagger).toBe(true);
  });

  it('clearPageTransitionCache with specific key', async () => {
    renderHook(() => usePageTransition('key-a', true), { wrapper });
    renderHook(() => usePageTransition('key-b', true), { wrapper });
    await act(async () => { vi.advanceTimersByTime(600); });

    clearPageTransitionCache('key-a');

    const { result: rA } = renderHook(
      () => usePageTransition('key-a', true, true),
      { wrapper },
    );
    expect(rA.current.shouldStagger).toBe(true);
  });
});
