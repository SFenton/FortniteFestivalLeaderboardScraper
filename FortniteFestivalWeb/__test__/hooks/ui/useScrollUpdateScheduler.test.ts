import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useScrollUpdateScheduler } from '../../../src/hooks/ui/useScrollUpdateScheduler';

describe('useScrollUpdateScheduler', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => (
      window.setTimeout(() => callback(performance.now()), 16)
    ));
    vi.stubGlobal('cancelAnimationFrame', (id: number) => window.clearTimeout(id));
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('coalesces updates into one animation frame', () => {
    const update = vi.fn();
    const { result } = renderHook(() => useScrollUpdateScheduler(update));

    act(() => {
      result.current.scheduleUpdate();
      result.current.scheduleUpdate();
      vi.advanceTimersByTime(16);
    });

    expect(update).toHaveBeenCalledTimes(1);
  });

  it('runs synchronously and cancels pending work', () => {
    const update = vi.fn();
    const { result } = renderHook(() => useScrollUpdateScheduler(update));

    act(() => {
      result.current.scheduleUpdate();
      result.current.updateNow();
      vi.runAllTimers();
    });

    expect(update).toHaveBeenCalledTimes(1);
  });

  it('settles viewport changes and cleans up timers on disable', () => {
    const update = vi.fn();
    const addWindow = vi.spyOn(window, 'addEventListener');
    const removeWindow = vi.spyOn(window, 'removeEventListener');
    const { rerender, unmount } = renderHook(
      ({ disabled }) => useScrollUpdateScheduler(update, disabled),
      { initialProps: { disabled: false } },
    );

    act(() => {
      window.dispatchEvent(new Event('resize'));
      vi.advanceTimersByTime(400);
    });
    expect(update).toHaveBeenCalledTimes(4);
    expect(addWindow).toHaveBeenCalledWith('resize', expect.any(Function));

    rerender({ disabled: true });
    act(() => {
      window.dispatchEvent(new Event('resize'));
      vi.runAllTimers();
    });
    expect(update).toHaveBeenCalledTimes(4);

    unmount();
    expect(removeWindow).toHaveBeenCalledWith('resize', expect.any(Function));
  });
});
