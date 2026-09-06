import type { Rect, Virtualizer } from '@tanstack/react-virtual';

type ScrollViewportRect = Pick<DOMRect, 'top' | 'right' | 'bottom' | 'left' | 'width' | 'height'>;

/** The wide shell's native gutter border is outside its visible scroll viewport. */
export function getScrollViewportRect(element: HTMLElement): ScrollViewportRect {
  const rect = element.getBoundingClientRect();
  const top = rect.top + element.clientTop;
  const left = rect.left + element.clientLeft;
  return {
    top,
    left,
    right: left + element.clientWidth,
    bottom: top + element.clientHeight,
    width: element.clientWidth,
    height: element.clientHeight,
  };
}

export function observeScrollViewportRect<TScrollElement extends HTMLElement, TItemElement extends Element>(
  instance: Virtualizer<TScrollElement, TItemElement>,
  callback: (rect: Rect) => void,
): (() => void) | undefined {
  const element = instance.scrollElement;
  const targetWindow = instance.targetWindow;
  if (!element || !targetWindow) return;

  const update = () => callback({ width: element.clientWidth, height: element.clientHeight });
  update();
  if (!targetWindow.ResizeObserver) return;

  let frame: number | null = null;
  const observer = new targetWindow.ResizeObserver(() => {
    if (!instance.options.useAnimationFrameWithResizeObserver) {
      update();
    } else if (frame === null) {
      frame = targetWindow.requestAnimationFrame(() => {
        frame = null;
        update();
      });
    }
  });
  // Header-border changes resize the client box even when the border box is stable.
  observer.observe(element);
  return () => {
    observer.disconnect();
    if (frame !== null) targetWindow.cancelAnimationFrame(frame);
  };
}
