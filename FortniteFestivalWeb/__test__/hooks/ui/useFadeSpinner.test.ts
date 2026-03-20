import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useFadeSpinner } from '../../../src/hooks/ui/useFadeSpinner';

describe('useFadeSpinner', () => {
  beforeEach(() => {
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 0; });
  });
  afterEach(() => { vi.restoreAllMocks(); });

  it('starts invisible and opacity 0', () => {
    const { result } = renderHook(() => useFadeSpinner(false));
    expect(result.current.visible).toBe(false);
    expect(result.current.opacity).toBe(0);
  });

  it('becomes visible with opacity 1 when active', () => {
    const { result } = renderHook(() => useFadeSpinner(true));
    expect(result.current.visible).toBe(true);
    expect(result.current.opacity).toBe(1);
  });

  it('fades out when active becomes false', () => {
    const { result, rerender } = renderHook(({ active }) => useFadeSpinner(active), {
      initialProps: { active: true },
    });
    expect(result.current.visible).toBe(true);
    expect(result.current.opacity).toBe(1);

    rerender({ active: false });
    // opacity should go to 0 (fade out), but visible stays true until transition ends
    expect(result.current.opacity).toBe(0);
    expect(result.current.visible).toBe(true);
  });

  it('unmounts after onTransitionEnd when faded out', () => {
    const { result, rerender } = renderHook(({ active }) => useFadeSpinner(active), {
      initialProps: { active: true },
    });
    rerender({ active: false });
    expect(result.current.visible).toBe(true);

    act(() => { result.current.onTransitionEnd(); });
    expect(result.current.visible).toBe(false);
  });

  it('does not unmount on onTransitionEnd if still active', () => {
    const { result } = renderHook(() => useFadeSpinner(true));
    act(() => { result.current.onTransitionEnd(); });
    expect(result.current.visible).toBe(true);
  });

  it('does not unmount on onTransitionEnd if opacity is not 0', () => {
    const { result } = renderHook(() => useFadeSpinner(true));
    // opacity is 1, active is true — should not unmount
    act(() => { result.current.onTransitionEnd(); });
    expect(result.current.visible).toBe(true);
  });

  it('reset sets visible and opacity to initial state', () => {
    const { result } = renderHook(() => useFadeSpinner(true));
    expect(result.current.visible).toBe(true);

    act(() => { result.current.reset(); });
    expect(result.current.visible).toBe(false);
    expect(result.current.opacity).toBe(0);
  });

  it('does not set opacity to 0 when becoming inactive while already invisible', () => {
    // Start inactive (visible=false), stay inactive
    const { result, rerender } = renderHook(({ active }) => useFadeSpinner(active), {
      initialProps: { active: false },
    });
    expect(result.current.visible).toBe(false);
    expect(result.current.opacity).toBe(0);

    // Re-render still inactive — the else-if (visible) branch should NOT fire
    rerender({ active: false });
    expect(result.current.visible).toBe(false);
    expect(result.current.opacity).toBe(0);
  });
});
