import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import { createRenderFlag } from '../../../hooks/ui/useRenderFlag';

describe('createRenderFlag', () => {
  it('returns false on first render, true on subsequent renders', () => {
    const useFlag = createRenderFlag();

    // First render: should be false (never rendered before)
    const { result, rerender } = renderHook(() => useFlag());
    expect(result.current).toBe(false);

    // Re-render: still false because useRef captured the initial value
    rerender();
    expect(result.current).toBe(false);
  });

  it('returns true when remounted after first mount', () => {
    const useFlag = createRenderFlag();

    // First mount
    const { unmount } = renderHook(() => useFlag());
    unmount();

    // Second mount: hasRendered is now true
    const { result: result2 } = renderHook(() => useFlag());
    expect(result2.current).toBe(true);
  });

  it('creates independent flags per call', () => {
    const useFlag1 = createRenderFlag();
    const useFlag2 = createRenderFlag();

    // Mount flag1 first
    const { unmount: unmount1 } = renderHook(() => useFlag1());
    unmount1();

    // flag1 should be true, flag2 still false
    const { result: r1 } = renderHook(() => useFlag1());
    const { result: r2 } = renderHook(() => useFlag2());
    expect(r1.current).toBe(true);
    expect(r2.current).toBe(false);
  });
});
