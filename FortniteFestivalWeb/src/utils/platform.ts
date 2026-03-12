const ua = navigator.userAgent;

export const IS_IOS =
  /iPad|iPhone|iPod/.test(ua) ||
  (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);

export const IS_ANDROID = /Android/.test(ua);

export const IS_MOBILE_DEVICE = IS_IOS || IS_ANDROID;
