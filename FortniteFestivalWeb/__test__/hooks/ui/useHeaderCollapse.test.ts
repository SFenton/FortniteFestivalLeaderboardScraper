import { describe, it, expect, afterEach, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useHeaderCollapse } from '../../../src/hooks/ui/useHeaderCollapse';
import { createScrollContainerWrapper } from '../../Helpers/scrollContainerWrapper';

describe('useHeaderCollapse', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('returns false initially', () => {
    const { wrapper } = createScrollContainerWrapper();
    const { result } = renderHook(() => useHeaderCollapse(), { wrapper });
    const [collapsed] = result.current;
    expect(collapsed).toBe(false);
  });

  it('returns forcedValue when disabled', () => {
    const { wrapper } = createScrollContainerWrapper();
    const { result } = renderHook(() => useHeaderCollapse({ disabled: true, forcedValue: true }), { wrapper });
    const [collapsed] = result.current;
    expect(collapsed).toBe(true);
  });

  it('collapses after scroll past threshold', () => {
    const { wrapper, mockEl } = createScrollContainerWrapper();
    Object.defineProperty(mockEl, 'scrollTop', { value: 0, writable: true, configurable: true });
    const { result } = renderHook(() => useHeaderCollapse(), { wrapper });

    // Scroll past threshold
    Object.defineProperty(mockEl, 'scrollTop', { value: 50, writable: true, configurable: true });
    act(() => { result.current[1](); });
    expect(result.current[0]).toBe(true);

    // Scroll back above threshold
    Object.defineProperty(mockEl, 'scrollTop', { value: 10, writable: true, configurable: true });
    act(() => { result.current[1](); });
    expect(result.current[0]).toBe(false);
  });

  it('respects custom threshold', () => {
    const { wrapper, mockEl } = createScrollContainerWrapper();
    Object.defineProperty(mockEl, 'scrollTop', { value: 0, writable: true, configurable: true });
    const { result } = renderHook(() => useHeaderCollapse({ threshold: 100 }), { wrapper });

    Object.defineProperty(mockEl, 'scrollTop', { value: 50, writable: true, configurable: true });
    act(() => { result.current[1](); });
    expect(result.current[0]).toBe(false);

    Object.defineProperty(mockEl, 'scrollTop', { value: 150, writable: true, configurable: true });
    act(() => { result.current[1](); });
    expect(result.current[0]).toBe(true);
  });

  it('does not update when disabled', () => {
    const { wrapper, mockEl } = createScrollContainerWrapper();
    Object.defineProperty(mockEl, 'scrollTop', { value: 100, writable: true, configurable: true });
    const { result } = renderHook(() => useHeaderCollapse({ disabled: true, forcedValue: false }), { wrapper });

    act(() => { result.current[1](); });
    expect(result.current[0]).toBe(false); // forced value, not scroll-based
  });
});
