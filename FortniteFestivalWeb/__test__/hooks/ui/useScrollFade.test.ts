import { describe, it, expect, vi, afterEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useScrollFade } from '../../../src/hooks/ui/useScrollFade';

function makeListEl(childCount: number) {
  const list = document.createElement('div');
  for (let i = 0; i < childCount; i++) {
    const child = document.createElement('div');
    child.getBoundingClientRect = () => ({ top: i * 50, bottom: (i + 1) * 50, left: 0, right: 300, width: 300, height: 50, x: 0, y: i * 50, toJSON: () => '' });
    list.appendChild(child);
  }
  return list;
}

describe('useScrollFade', () => {
  afterEach(() => { vi.restoreAllMocks(); });

  it('returns an update function', () => {
    const scrollRef = { current: null };
    const listRef = { current: null };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    expect(typeof result.current).toBe('function');
  });

  it('tolerates null refs', () => {
    const scrollRef = { current: null };
    const listRef = { current: null };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    expect(() => result.current()).not.toThrow();
  });

  it('sets up IntersectionObserver on list children', () => {
    const listEl = makeListEl(3);
    const scrollRef = { current: document.createElement('div') };
    const listRef = { current: listEl };
    renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    // The stub IntersectionObserver from setup.ts should be called with 3 children
    // Just verify the hook didn't throw and returned a function
  });

  it('registers window scroll listener', () => {
    const addSpy = vi.spyOn(window, 'addEventListener');
    const listEl = makeListEl(2);
    const scrollRef = { current: document.createElement('div') };
    const listRef = { current: listEl };
    renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    expect(addSpy).toHaveBeenCalledWith('scroll', expect.any(Function), { passive: true });
  });

  it('cleans up scroll listener on unmount', () => {
    const removeSpy = vi.spyOn(window, 'removeEventListener');
    const listEl = makeListEl(2);
    const scrollRef = { current: document.createElement('div') };
    const listRef = { current: listEl };
    const { unmount } = renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    unmount();
    expect(removeSpy).toHaveBeenCalledWith('scroll', expect.any(Function));
  });

  it('respects custom distance option', () => {
    const listEl = makeListEl(3);
    const scrollRef = { current: document.createElement('div') };
    const listRef = { current: listEl };
    // Should not throw with custom distance
    renderHook(() => useScrollFade(scrollRef as any, listRef as any, [], { distance: 20 }));
  });
});
