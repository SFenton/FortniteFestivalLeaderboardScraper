import { describe, it, expect, afterEach, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useHeaderCollapse } from '../../../src/hooks/ui/useHeaderCollapse';

describe('useHeaderCollapse', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('returns false initially', () => {
    const { result } = renderHook(() => useHeaderCollapse());
    const [collapsed] = result.current;
    expect(collapsed).toBe(false);
  });

  it('returns forcedValue when disabled', () => {
    const { result } = renderHook(() => useHeaderCollapse({ disabled: true, forcedValue: true }));
    const [collapsed] = result.current;
    expect(collapsed).toBe(true);
  });

  it('collapses after scroll past threshold', () => {
    Object.defineProperty(window, 'scrollY', { value: 0, writable: true, configurable: true });
    const { result } = renderHook(() => useHeaderCollapse());

    // Scroll past threshold
    Object.defineProperty(window, 'scrollY', { value: 50, writable: true, configurable: true });
    act(() => { result.current[1](); });
    expect(result.current[0]).toBe(true);

    // Scroll back above threshold
    Object.defineProperty(window, 'scrollY', { value: 10, writable: true, configurable: true });
    act(() => { result.current[1](); });
    expect(result.current[0]).toBe(false);
  });

  it('respects custom threshold', () => {
    Object.defineProperty(window, 'scrollY', { value: 0, writable: true, configurable: true });
    const { result } = renderHook(() => useHeaderCollapse({ threshold: 100 }));

    Object.defineProperty(window, 'scrollY', { value: 50, writable: true, configurable: true });
    act(() => { result.current[1](); });
    expect(result.current[0]).toBe(false);

    Object.defineProperty(window, 'scrollY', { value: 150, writable: true, configurable: true });
    act(() => { result.current[1](); });
    expect(result.current[0]).toBe(true);
  });

  it('does not update when disabled', () => {
    Object.defineProperty(window, 'scrollY', { value: 100, writable: true, configurable: true });
    const { result } = renderHook(() => useHeaderCollapse({ disabled: true, forcedValue: false }));

    act(() => { result.current[1](); });
    expect(result.current[0]).toBe(false); // forced value, not scroll-based
  });
});
