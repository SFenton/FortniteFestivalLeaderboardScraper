import { useSyncExternalStore } from 'react';

/**
 * Returns the current visual viewport height in pixels.
 * On iOS Safari/PWA the visual viewport shrinks when the virtual keyboard
 * opens, so this gives the *actual* visible area — unlike `vh` units which
 * reflect the full layout viewport.
 *
 * Falls back to `window.innerHeight` when the VisualViewport API is
 * unavailable.
 */

/* v8 ignore start — VisualViewport API not available in jsdom */
function subscribe(callback: () => void) {
  const vv = window.visualViewport;
  if (vv) {
    vv.addEventListener('resize', callback);
    vv.addEventListener('scroll', callback);
    return () => {
      vv.removeEventListener('resize', callback);
      vv.removeEventListener('scroll', callback);
    };
  }
  window.addEventListener('resize', callback);
  return () => window.removeEventListener('resize', callback);
}
/* v8 ignore stop */

function getSnapshot(): number {
  return window.visualViewport?.height ?? window.innerHeight;
}

/* v8 ignore start — SSR-only: never called in jsdom */
function getServerSnapshot(): number {
  return 900;
}
/* v8 ignore stop */

export function useVisualViewportHeight(): number {
  return useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);
}

function getOffsetTopSnapshot(): number {
  return window.visualViewport?.offsetTop ?? 0;
}

/* v8 ignore start — SSR-only: never called in jsdom */
function getOffsetTopServerSnapshot(): number {
  return 0;
}
/* v8 ignore stop */

/**
 * Returns the current visual viewport offsetTop in pixels.
 * On iOS Safari, when the virtual keyboard opens, the visual viewport
 * scrolls within the layout viewport — this offset tells you where
 * the visible area begins relative to the layout viewport top.
 */
export function useVisualViewportOffsetTop(): number {
  return useSyncExternalStore(subscribe, getOffsetTopSnapshot, getOffsetTopServerSnapshot);
}
