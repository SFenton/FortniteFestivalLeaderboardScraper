const ua = navigator.userAgent;
const _params = new URLSearchParams(window.location.search);
const _forceIos = _params.has('forceios');
const _forceAndroid = _params.has('forceandroid');
const _forcePwa = _params.has('forcepwa');
const _forceDesktop = _params.has('forcedesktop');
const _hasForce = _forceIos || _forceAndroid || _forcePwa || _forceDesktop;

const _isIos =
  /iPad|iPhone|iPod/.test(ua) ||
  (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
const _isAndroid = /Android/.test(ua);
const _isPwa = window.matchMedia('(display-mode: standalone)').matches ||
  (navigator as unknown as { standalone?: boolean }).standalone === true;

export const IS_IOS = _hasForce ? (_forceIos && !_forceDesktop) : _isIos;
export const IS_ANDROID = _hasForce ? (_forceAndroid && !_forceDesktop) : _isAndroid;
export const IS_PWA = _hasForce ? (_forcePwa && !_forceDesktop) : _isPwa;
export const IS_MOBILE_DEVICE = IS_IOS || IS_ANDROID;

/** True when this page load is a browser refresh (F5 / reload button). */
export const IS_PAGE_RELOAD =
  (performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming | undefined)?.type === 'reload';
