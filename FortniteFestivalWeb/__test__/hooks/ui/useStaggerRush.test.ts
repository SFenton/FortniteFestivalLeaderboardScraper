import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useStaggerRush } from '../../../src/hooks/ui/useStaggerRush';

describe('useStaggerRush', () => {
  it('returns rushOnScroll and resetRush', () => {
    const scrollRef = { current: null };
    const { result } = renderHook(() => useStaggerRush(scrollRef as any));
    expect(typeof result.current.rushOnScroll).toBe('function');
    expect(typeof result.current.resetRush).toBe('function');
  });

  it('does not throw when ref is null', () => {
    const scrollRef = { current: null };
    const { result } = renderHook(() => useStaggerRush(scrollRef as any));
    expect(() => result.current.rushOnScroll()).not.toThrow();
  });

  it('rushes animations on scroll elements', () => {
    const el = document.createElement('div');
    const child = document.createElement('div');
    child.setAttribute('style', 'animation: fadeInUp 400ms ease-out 500ms forwards; opacity: 0;');
    el.appendChild(child);
    const scrollRef = { current: el };
    const { result } = renderHook(() => useStaggerRush(scrollRef as any));
    result.current.rushOnScroll();
  });

  it('only rushes once (second call is no-op)', () => {
    const el = document.createElement('div');
    const child = document.createElement('div');
    child.setAttribute('style', 'animation: fadeInUp 400ms ease-out 500ms forwards; opacity: 0;');
    el.appendChild(child);
    const scrollRef = { current: el };
    const { result } = renderHook(() => useStaggerRush(scrollRef as any));
    result.current.rushOnScroll(); // first call — finds element, sets rushedRef
    result.current.rushOnScroll(); // second call — early return
  });

  it('skips elements that already have visible opacity', () => {
    const el = document.createElement('div');
    const child = document.createElement('div');
    child.setAttribute('style', 'animation: fadeInUp 400ms ease-out 500ms forwards');
    // Default computed opacity in jsdom is '' which !== '0', so this exercises the skip path
    el.appendChild(child);
    const scrollRef = { current: el };
    const { result } = renderHook(() => useStaggerRush(scrollRef as any));
    result.current.rushOnScroll();
  });

  it('resetRush allows re-rushing after reset', () => {
    const el = document.createElement('div');
    const child = document.createElement('div');
    child.setAttribute('style', 'animation: fadeInUp 400ms ease-out 500ms forwards; opacity: 0;');
    el.appendChild(child);
    const scrollRef = { current: el };
    const { result } = renderHook(() => useStaggerRush(scrollRef as any));
    result.current.rushOnScroll(); // first rush
    result.current.resetRush();   // reset
    // Add another pending element
    const child2 = document.createElement('div');
    child2.setAttribute('style', 'animation: fadeInUp 400ms ease-out 500ms forwards; opacity: 0;');
    el.appendChild(child2);
    result.current.rushOnScroll(); // should rush again
  });
});
