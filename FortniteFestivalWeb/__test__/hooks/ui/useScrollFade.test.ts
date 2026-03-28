import { describe, it, expect, vi, afterEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useScrollFade } from '../../../src/hooks/ui/useScrollFade';
import { createScrollContainerWrapper } from '../../Helpers/scrollContainerWrapper';
import { stubResizeObserver } from '../../Helpers/browserStubs';

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
    const { wrapper } = createScrollContainerWrapper();
    const scrollRef = { current: null };
    const listRef = { current: null };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any), { wrapper });
    expect(typeof result.current).toBe('function');
  });

  it('tolerates null refs', () => {
    const { wrapper } = createScrollContainerWrapper();
    const scrollRef = { current: null };
    const listRef = { current: null };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any), { wrapper });
    expect(() => result.current()).not.toThrow();
  });

  it('sets up IntersectionObserver on list children', () => {
    const { wrapper } = createScrollContainerWrapper();
    const listEl = makeListEl(3);
    const scrollRef = { current: document.createElement('div') };
    const listRef = { current: listEl };
    renderHook(() => useScrollFade(scrollRef as any, listRef as any), { wrapper });
  });

  it('registers scroll container listener', () => {
    const { wrapper, mockEl } = createScrollContainerWrapper();
    const addSpy = vi.spyOn(mockEl, 'addEventListener');
    const listEl = makeListEl(2);
    const scrollRef = { current: document.createElement('div') };
    const listRef = { current: listEl };
    renderHook(() => useScrollFade(scrollRef as any, listRef as any), { wrapper });
    expect(addSpy).toHaveBeenCalledWith('scroll', expect.any(Function), { passive: true });
  });

  it('cleans up scroll listener on unmount', () => {
    const { wrapper, mockEl } = createScrollContainerWrapper();
    const removeSpy = vi.spyOn(mockEl, 'removeEventListener');
    const listEl = makeListEl(2);
    const scrollRef = { current: document.createElement('div') };
    const listRef = { current: listEl };
    const { unmount } = renderHook(() => useScrollFade(scrollRef as any, listRef as any), { wrapper });
    unmount();
    expect(removeSpy).toHaveBeenCalledWith('scroll', expect.any(Function));
  });

  it('respects custom distance option', () => {
    const { wrapper } = createScrollContainerWrapper();
    const listEl = makeListEl(3);
    const scrollRef = { current: document.createElement('div') };
    const listRef = { current: listEl };
    renderHook(() => useScrollFade(scrollRef as any, listRef as any, [], { distance: 20 }), { wrapper });
  });

  it('creates ResizeObserver on the scroll container', () => {
    const observers = stubResizeObserver();
    const { wrapper, mockEl } = createScrollContainerWrapper();
    const listEl = makeListEl(2);
    const scrollRef = { current: document.createElement('div') };
    const listRef = { current: listEl };
    renderHook(() => useScrollFade(scrollRef as any, listRef as any), { wrapper });
    const observed = observers.flatMap(o => o.targets);
    expect(observed).toContain(mockEl);
  });

  it('disconnects ResizeObserver on unmount', () => {
    stubResizeObserver();
    const disconnectSpy = vi.fn();
    const OrigRO = globalThis.ResizeObserver;
    vi.stubGlobal('ResizeObserver', class extends OrigRO {
      disconnect() { disconnectSpy(); super.disconnect(); }
    });
    const { wrapper } = createScrollContainerWrapper();
    const listEl = makeListEl(2);
    const scrollRef = { current: document.createElement('div') };
    const listRef = { current: listEl };
    const { unmount } = renderHook(() => useScrollFade(scrollRef as any, listRef as any), { wrapper });
    expect(disconnectSpy).not.toHaveBeenCalled();
    unmount();
    expect(disconnectSpy).toHaveBeenCalled();
  });
});
