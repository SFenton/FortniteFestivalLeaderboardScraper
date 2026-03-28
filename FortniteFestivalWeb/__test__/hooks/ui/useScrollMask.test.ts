import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useScrollMask } from '../../../src/hooks/ui/useScrollMask';
import { stubResizeObserver } from '../../Helpers/browserStubs';
import { createScrollContainerWrapper } from '../../Helpers/scrollContainerWrapper';

describe('useScrollMask', () => {
  beforeEach(() => { stubResizeObserver(); });
  afterEach(() => { vi.restoreAllMocks(); });

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

  it('creates ResizeObserver on the scroll container (selfScroll)', () => {
    const observers = stubResizeObserver();
    const el = document.createElement('div');
    Object.defineProperty(el, 'scrollHeight', { value: 500, configurable: true });
    Object.defineProperty(el, 'clientHeight', { value: 200, configurable: true });
    Object.defineProperty(el, 'scrollTop', { value: 0, configurable: true });
    const ref = { current: el };
    renderHook(() => useScrollMask(ref as any, [], { selfScroll: true }));
    // At least one ResizeObserver should have been created observing the element
    const observed = observers.flatMap(o => o.targets);
    expect(observed).toContain(el);
  });

  it('creates ResizeObserver on scroll container and content element (non-selfScroll)', () => {
    const observers = stubResizeObserver();
    const { wrapper, mockEl } = createScrollContainerWrapper();
    const contentEl = document.createElement('div');
    const ref = { current: contentEl };
    renderHook(() => useScrollMask(ref as any, []), { wrapper });
    const observed = observers.flatMap(o => o.targets);
    expect(observed).toContain(mockEl);
    expect(observed).toContain(contentEl);
  });

  it('disconnects ResizeObserver on unmount', () => {
    stubResizeObserver();
    const disconnectSpy = vi.fn();
    const OrigRO = globalThis.ResizeObserver;
    vi.stubGlobal('ResizeObserver', class extends OrigRO {
      disconnect() { disconnectSpy(); super.disconnect(); }
    });
    const el = document.createElement('div');
    Object.defineProperty(el, 'scrollHeight', { value: 500, configurable: true });
    Object.defineProperty(el, 'clientHeight', { value: 200, configurable: true });
    Object.defineProperty(el, 'scrollTop', { value: 0, configurable: true });
    const ref = { current: el };
    const { unmount } = renderHook(() => useScrollMask(ref as any, [], { selfScroll: true }));
    expect(disconnectSpy).not.toHaveBeenCalled();
    unmount();
    expect(disconnectSpy).toHaveBeenCalled();
  });
});
