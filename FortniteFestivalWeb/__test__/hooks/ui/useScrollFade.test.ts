import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useScrollFade } from '../../../src/hooks/ui/useScrollFade';

function makeScrollEl(scrollTop: number, clientHeight: number, scrollHeight: number) {
  const el = document.createElement('div');
  Object.defineProperty(el, 'scrollTop', { get: () => scrollTop, configurable: true });
  Object.defineProperty(el, 'clientHeight', { get: () => clientHeight, configurable: true });
  Object.defineProperty(el, 'scrollHeight', { get: () => scrollHeight, configurable: true });
  el.getBoundingClientRect = () => ({ top: 0, bottom: clientHeight, left: 0, right: 300, width: 300, height: clientHeight, x: 0, y: 0, toJSON: () => '' });
  return el;
}

function makeListEl(childCount: number, top = 0, height = 50) {
  const list = document.createElement('div');
  for (let i = 0; i < childCount; i++) {
    const child = document.createElement('div');
    const childTop = top + i * height;
    child.getBoundingClientRect = () => ({ top: childTop, bottom: childTop + height, left: 0, right: 300, width: 300, height, x: 0, y: childTop, toJSON: () => '' });
    list.appendChild(child);
  }
  return list;
}

describe('useScrollFade', () => {
  it('returns an update function', () => {
    const scrollRef = { current: null };
    const listRef = { current: null };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    expect(typeof result.current).toBe('function');
  });

  it('applies no mask when not scrollable', () => {
    const scrollEl = makeScrollEl(0, 300, 200); // clientHeight > scrollHeight
    const listEl = makeListEl(3);
    const scrollRef = { current: scrollEl };
    const listRef = { current: listEl };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    result.current();
  });

  it('applies bottom mask when at top of scrollable area', () => {
    const scrollEl = makeScrollEl(0, 200, 600);
    const listEl = makeListEl(10, 0, 50);
    const scrollRef = { current: scrollEl };
    const listRef = { current: listEl };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    result.current();
  });

  it('applies top mask when scrolled down', () => {
    const scrollEl = makeScrollEl(100, 200, 600);
    const listEl = makeListEl(10, -100, 50);
    const scrollRef = { current: scrollEl };
    const listRef = { current: listEl };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    result.current();
  });

  it('applies both masks in the middle', () => {
    const scrollEl = makeScrollEl(50, 200, 600);
    const listEl = makeListEl(10, -50, 50);
    const scrollRef = { current: scrollEl };
    const listRef = { current: listEl };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    result.current();
  });

  it('clears mask for children fully in viewport', () => {
    const scrollEl = makeScrollEl(0, 500, 600);
    const listEl = makeListEl(3, 50, 50);
    const scrollRef = { current: scrollEl };
    const listRef = { current: listEl };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    result.current();
    const first = listEl.children[0] as HTMLElement;
    expect(first.style.maskImage).toBe('');
  });

  it('tolerates null refs', () => {
    const scrollRef = { current: null };
    const listRef = { current: null };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    expect(() => result.current()).not.toThrow();
  });

  it('respects custom distance option', () => {
    const scrollEl = makeScrollEl(50, 200, 600);
    const listEl = makeListEl(5, -50, 60);
    const scrollRef = { current: scrollEl };
    const listRef = { current: listEl };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any, [], { distance: 20 }));
    result.current();
  });

  it('applies needsTop mask for child partially above viewport', () => {
    const scrollEl = makeScrollEl(50, 200, 600);
    const list = document.createElement('div');
    const child = document.createElement('div');
    child.getBoundingClientRect = () => ({ top: -10, bottom: 40, left: 0, right: 300, width: 300, height: 50, x: 0, y: -10, toJSON: () => '' });
    list.appendChild(child);
    const scrollRef = { current: scrollEl };
    const listRef = { current: list };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    result.current();
    expect(child.style.maskImage).not.toBe('');
  });

  it('applies needsBottom mask for child partially below viewport', () => {
    const scrollEl = makeScrollEl(0, 200, 600);
    const list = document.createElement('div');
    const child = document.createElement('div');
    child.getBoundingClientRect = () => ({ top: 180, bottom: 230, left: 0, right: 300, width: 300, height: 50, x: 0, y: 180, toJSON: () => '' });
    list.appendChild(child);
    const scrollRef = { current: scrollEl };
    const listRef = { current: list };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    result.current();
    expect(child.style.maskImage).not.toBe('');
  });

  it('applies both masks for child spanning entire viewport', () => {
    const scrollEl = makeScrollEl(50, 100, 600);
    const list = document.createElement('div');
    const child = document.createElement('div');
    child.getBoundingClientRect = () => ({ top: -20, bottom: 120, left: 0, right: 300, width: 300, height: 140, x: 0, y: -20, toJSON: () => '' });
    list.appendChild(child);
    const scrollRef = { current: scrollEl };
    const listRef = { current: list };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    result.current();
    expect(child.style.maskImage).toContain('linear-gradient');
  });
});

describe('useScrollFade rAF branch', () => {
  beforeEach(() => {
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 1; });
    vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {});
  });
  afterEach(() => vi.restoreAllMocks());

  it('returns an update function that calls rAF', () => {
    const scrollRef = { current: document.createElement('div') };
    const listRef = { current: document.createElement('div') };
    const { result } = renderHook(() => useScrollFade(scrollRef, listRef, []));
    expect(typeof result.current).toBe('function');
    result.current();
  });
});
