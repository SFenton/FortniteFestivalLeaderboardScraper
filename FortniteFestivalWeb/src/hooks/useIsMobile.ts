import { useSyncExternalStore } from 'react';
import { IS_IOS, IS_ANDROID, IS_PWA } from '../utils/platform';

const MOBILE_BREAKPOINT = 768;
const QUERY = `(max-width: ${MOBILE_BREAKPOINT}px)`;

function subscribe(callback: () => void) {
  const mql = window.matchMedia(QUERY);
  mql.addEventListener('change', callback);
  return () => mql.removeEventListener('change', callback);
}

function getSnapshot() {
  return window.matchMedia(QUERY).matches;
}

function getServerSnapshot() {
  return false;
}

/** True when viewport is narrow (dimension-based only). Use for layout decisions (card style, grids). */
export function useIsMobile(): boolean {
  return useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);
}

/** True when mobile chrome (bottom nav, FAB, mobile header) should be shown — always on iOS/Android/PWA. */
export function useIsMobileChrome(): boolean {
  const dimensionMobile = useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);
  return dimensionMobile || IS_IOS || IS_ANDROID || IS_PWA;
}
