import { describe, it, expect, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useAccordionToggle } from '../../../src/hooks/ui/useAccordionToggle';

describe('useAccordionToggle', () => {
  it('starts with all sections closed', () => {
    const { result } = renderHook(() => useAccordionToggle(3));
    expect(result.current[0]).toEqual([false, false, false]);
  });

  it('opens a section on toggle', () => {
    const { result } = renderHook(() => useAccordionToggle(3));
    act(() => { result.current[1](1); });
    expect(result.current[0]).toEqual([false, true, false]);
  });

  it('closes an open section on toggle', () => {
    const { result } = renderHook(() => useAccordionToggle(3));
    act(() => { result.current[1](1); });
    expect(result.current[0][1]).toBe(true);
    act(() => { result.current[1](1); });
    expect(result.current[0][1]).toBe(false);
  });

  it('closes current before opening another (with delay)', async () => {
    vi.useFakeTimers();
    const { result } = renderHook(() => useAccordionToggle(3, 100));

    // Open section 0
    act(() => { result.current[1](0); });
    expect(result.current[0]).toEqual([true, false, false]);

    // Toggle section 2 — should close 0 immediately
    act(() => { result.current[1](2); });
    expect(result.current[0]).toEqual([false, false, false]);

    // After delay, section 2 opens
    act(() => { vi.advanceTimersByTime(100); });
    expect(result.current[0]).toEqual([false, false, true]);

    vi.useRealTimers();
  });

  it('resets all sections', () => {
    const { result } = renderHook(() => useAccordionToggle(3));
    act(() => { result.current[1](1); });
    expect(result.current[0][1]).toBe(true);
    act(() => { result.current[2](); }); // reset
    expect(result.current[0]).toEqual([false, false, false]);
  });
});
