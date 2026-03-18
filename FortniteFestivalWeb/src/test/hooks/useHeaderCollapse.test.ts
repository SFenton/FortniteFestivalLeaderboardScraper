import { describe, it, expect } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useHeaderCollapse } from '../../hooks/ui/useHeaderCollapse';
import { useRef } from 'react';

describe('useHeaderCollapse', () => {
  it('returns false initially', () => {
    const { result } = renderHook(() => {
      const ref = useRef<HTMLElement>(null);
      return useHeaderCollapse(ref);
    });
    const [collapsed] = result.current;
    expect(collapsed).toBe(false);
  });

  it('returns forcedValue when disabled', () => {
    const { result } = renderHook(() => {
      const ref = useRef<HTMLElement>(null);
      return useHeaderCollapse(ref, { disabled: true, forcedValue: true });
    });
    const [collapsed] = result.current;
    expect(collapsed).toBe(true);
  });

  it('collapses after scroll past threshold', () => {
    const scrollEl = { scrollTop: 0, scrollHeight: 1000, clientHeight: 500 } as unknown as HTMLElement;
    const ref = { current: scrollEl };
    const { result } = renderHook(() => useHeaderCollapse(ref as any));

    // Scroll past threshold
    scrollEl.scrollTop = 50;
    act(() => { result.current[1](); });
    expect(result.current[0]).toBe(true);

    // Scroll back above threshold
    scrollEl.scrollTop = 10;
    act(() => { result.current[1](); });
    expect(result.current[0]).toBe(false);
  });

  it('respects custom threshold', () => {
    const scrollEl = { scrollTop: 0 } as unknown as HTMLElement;
    const ref = { current: scrollEl };
    const { result } = renderHook(() => useHeaderCollapse(ref as any, { threshold: 100 }));

    scrollEl.scrollTop = 50;
    act(() => { result.current[1](); });
    expect(result.current[0]).toBe(false);

    scrollEl.scrollTop = 150;
    act(() => { result.current[1](); });
    expect(result.current[0]).toBe(true);
  });

  it('does not update when disabled', () => {
    const scrollEl = { scrollTop: 100 } as unknown as HTMLElement;
    const ref = { current: scrollEl };
    const { result } = renderHook(() => useHeaderCollapse(ref as any, { disabled: true, forcedValue: false }));

    act(() => { result.current[1](); });
    expect(result.current[0]).toBe(false); // forced value, not scroll-based
  });
});
