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

function getSnapshot(): number {
  return window.visualViewport?.height ?? window.innerHeight;
}

function getServerSnapshot(): number {
  return 900;
}

export function useVisualViewportHeight(): number {
  return useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);
}
