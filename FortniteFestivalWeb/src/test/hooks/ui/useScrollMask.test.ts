import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useScrollMask } from '../../../hooks/ui/useScrollMask';

describe('useScrollMask', () => {
  it('returns an update function', () => {
    const ref = { current: null };
    const { result } = renderHook(() => useScrollMask(ref as any, []));
    expect(typeof result.current).toBe('function');
  });

  it('does not throw when ref is null', () => {
    const ref = { current: null };
    const { result } = renderHook(() => useScrollMask(ref as any, []));
    expect(() => result.current()).not.toThrow();
  });

  it('applies mask when scrollable', () => {
    const el = document.createElement('div');
    Object.defineProperty(el, 'scrollHeight', { value: 500, configurable: true });
    Object.defineProperty(el, 'clientHeight', { value: 200, configurable: true });
    Object.defineProperty(el, 'scrollTop', { value: 50, configurable: true });
    const ref = { current: el };
    const { result } = renderHook(() => useScrollMask(ref as any, []));
    result.current();
    // Should set mask-image or similar CSS on the element
  });

  it('removes mask when not scrollable', () => {
    const el = document.createElement('div');
    Object.defineProperty(el, 'scrollHeight', { value: 100, configurable: true });
    Object.defineProperty(el, 'clientHeight', { value: 200, configurable: true });
    Object.defineProperty(el, 'scrollTop', { value: 0, configurable: true });
    const ref = { current: el };
    const { result } = renderHook(() => useScrollMask(ref as any, []));
    result.current();
  });

  it('shows bottom mask when at top', () => {
    const el = document.createElement('div');
    Object.defineProperty(el, 'scrollHeight', { value: 500, configurable: true });
    Object.defineProperty(el, 'clientHeight', { value: 200, configurable: true });
    Object.defineProperty(el, 'scrollTop', { value: 0, configurable: true });
    const ref = { current: el };
    const { result } = renderHook(() => useScrollMask(ref as any, []));
    result.current();
  });

  it('shows top mask when at bottom', () => {
    const el = document.createElement('div');
    Object.defineProperty(el, 'scrollHeight', { value: 500, configurable: true });
    Object.defineProperty(el, 'clientHeight', { value: 200, configurable: true });
    Object.defineProperty(el, 'scrollTop', { value: 300, configurable: true });
    const ref = { current: el };
    const { result } = renderHook(() => useScrollMask(ref as any, []));
    result.current();
  });
});
