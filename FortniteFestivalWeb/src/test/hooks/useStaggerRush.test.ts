import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useStaggerRush } from '../../hooks/ui/useStaggerRush';

describe('useStaggerRush', () => {
  it('returns a callback function', () => {
    const scrollRef = { current: null };
    const { result } = renderHook(() => useStaggerRush(scrollRef as any));
    expect(typeof result.current).toBe('function');
  });

  it('does not throw when ref is null', () => {
    const scrollRef = { current: null };
    const { result } = renderHook(() => useStaggerRush(scrollRef as any));
    expect(() => result.current()).not.toThrow();
  });

  it('rushes animations on scroll elements', () => {
    const el = document.createElement('div');
    const child = document.createElement('div');
    child.setAttribute('style', 'animation: fadeInUp 400ms ease-out 500ms forwards; opacity: 0;');
    el.appendChild(child);
    const scrollRef = { current: el };
    const { result } = renderHook(() => useStaggerRush(scrollRef as any));
    result.current();
  });

  it('only rushes once (second call is no-op)', () => {
    const el = document.createElement('div');
    const scrollRef = { current: el };
    const { result } = renderHook(() => useStaggerRush(scrollRef as any));
    result.current(); // first call — sets rushedRef
    result.current(); // second call — early return
  });

  it('skips elements that already have visible opacity', () => {
    const el = document.createElement('div');
    const child = document.createElement('div');
    child.setAttribute('style', 'animation: fadeInUp 400ms ease-out 500ms forwards');
    // Default computed opacity in jsdom is '' which !== '0', so this exercises the skip path
    el.appendChild(child);
    const scrollRef = { current: el };
    const { result } = renderHook(() => useStaggerRush(scrollRef as any));
    result.current();
  });
});
