import { describe, expect, it, vi } from 'vitest';
import type { Virtualizer } from '@tanstack/react-virtual';
import { getScrollViewportRect, observeScrollViewportRect } from '../../src/utils/scrollViewport';

function elementWithViewport() {
  const element = document.createElement('div');
  let height = 600;
  Object.defineProperties(element, {
    clientTop: { value: 64 },
    clientLeft: { value: 2 },
    clientWidth: { value: 1000 },
    clientHeight: { get: () => height },
    offsetHeight: { value: 664 },
  });
  element.getBoundingClientRect = () => new DOMRect(10, 20, 1004, 664);
  return { element, resize: (next: number) => { height = next; } };
}

function instance(element: HTMLElement | null, targetWindow: object | null, framed = false) {
  return {
    scrollElement: element,
    targetWindow,
    options: { useAnimationFrameWithResizeObserver: framed },
  } as unknown as Virtualizer<HTMLElement, Element>;
}

function observerWindow() {
  let resize = () => {};
  let frame: FrameRequestCallback = () => {};
  const observe = vi.fn();
  const disconnect = vi.fn();
  const requestAnimationFrame = vi.fn((callback: FrameRequestCallback) => { frame = callback; return 42; });
  const cancelAnimationFrame = vi.fn();
  class Observer {
    constructor(callback: () => void) { resize = callback; }
    observe = observe;
    disconnect = disconnect;
  }
  return {
    targetWindow: { ResizeObserver: Observer, requestAnimationFrame, cancelAnimationFrame },
    observe,
    disconnect,
    requestAnimationFrame,
    cancelAnimationFrame,
    resize: () => resize(),
    flushFrame: () => frame(0),
  };
}

describe('scroll viewport geometry', () => {
  it('excludes borders from the visible bounds', () => {
    const { element } = elementWithViewport();
    expect(getScrollViewportRect(element)).toEqual({
      top: 84, left: 12, right: 1012, bottom: 684, width: 1000, height: 600,
    });
  });

  it('keeps borderless scroll geometry unchanged', () => {
    const element = document.createElement('div');
    Object.defineProperties(element, { clientWidth: { value: 400 }, clientHeight: { value: 300 } });
    element.getBoundingClientRect = () => new DOMRect(10, 20, 400, 300);
    expect(getScrollViewportRect(element)).toEqual({
      top: 20, left: 10, right: 410, bottom: 320, width: 400, height: 300,
    });
  });

  it('observes client-height changes even when the border box stays fixed', () => {
    const { element, resize } = elementWithViewport();
    const owner = observerWindow();
    const callback = vi.fn();
    const cleanup = observeScrollViewportRect(instance(element, owner.targetWindow), callback);
    expect(callback).toHaveBeenLastCalledWith({ width: 1000, height: 600 });
    expect(owner.observe).toHaveBeenCalledWith(element);
    resize(520);
    owner.resize();
    expect(element.offsetHeight).toBe(664);
    expect(callback).toHaveBeenLastCalledWith({ width: 1000, height: 520 });
    cleanup?.();
    expect(owner.disconnect).toHaveBeenCalledOnce();
  });

  it('coalesces requested frames and cancels pending work on cleanup', () => {
    const { element, resize } = elementWithViewport();
    const owner = observerWindow();
    const callback = vi.fn();
    const cleanup = observeScrollViewportRect(instance(element, owner.targetWindow, true), callback);
    resize(480);
    owner.resize();
    owner.resize();
    expect(owner.requestAnimationFrame).toHaveBeenCalledOnce();
    expect(callback).toHaveBeenCalledTimes(1);
    owner.flushFrame();
    expect(callback).toHaveBeenLastCalledWith({ width: 1000, height: 480 });
    owner.resize();
    cleanup?.();
    expect(owner.disconnect).toHaveBeenCalledOnce();
    expect(owner.cancelAnimationFrame).toHaveBeenCalledWith(42);
  });

  it('handles missing elements, owner windows, and ResizeObserver', () => {
    const { element } = elementWithViewport();
    const callback = vi.fn();
    expect(observeScrollViewportRect(instance(null, {}), callback)).toBeUndefined();
    expect(observeScrollViewportRect(instance(element, null), callback)).toBeUndefined();
    expect(callback).not.toHaveBeenCalled();
    expect(observeScrollViewportRect(instance(element, {}), callback)).toBeUndefined();
    expect(callback).toHaveBeenCalledWith({ width: 1000, height: 600 });
  });
});
