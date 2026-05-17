export const DISABLE_SCROLL_FADE_QUERY_PARAM = 'disableScrollFade';
export const DISABLE_SCROLL_FADE_STORAGE_KEY = 'fst.disableScrollFade';
export const DISABLE_SCROLL_FADE_CLASS = 'fst-disable-scroll-fade';

const ENABLED_VALUES = new Set(['1', 'true', 'yes', 'on']);
const DISABLED_VALUES = new Set(['0', 'false', 'no', 'off']);

type ScrollFadeTestModePreference = {
  disabled: boolean;
  queryPreference: boolean | null;
};

export function applyScrollFadeTestMode(windowRef?: Window): boolean {
  const targetWindow = windowRef ?? (typeof window !== 'undefined' ? window : undefined);
  if (!targetWindow) return false;

  const { disabled, queryPreference } = resolveScrollFadeTestModePreference(targetWindow);
  if (queryPreference != null) setStoredPreference(targetWindow, queryPreference);

  targetWindow.document.documentElement.classList.toggle(DISABLE_SCROLL_FADE_CLASS, disabled);
  return disabled;
}

export function isScrollFadeTestModeEnabled(windowRef?: Window): boolean {
  const targetWindow = windowRef ?? (typeof window !== 'undefined' ? window : undefined);
  if (!targetWindow) return false;

  return resolveScrollFadeTestModePreference(targetWindow).disabled;
}

function resolveScrollFadeTestModePreference(windowRef: Window): ScrollFadeTestModePreference {
  const queryValue = getDisableScrollFadeQueryValue(windowRef);
  if (queryValue != null) {
    const normalized = queryValue.trim().toLowerCase();
    if (ENABLED_VALUES.has(normalized)) {
      return { disabled: true, queryPreference: true };
    }

    if (DISABLED_VALUES.has(normalized)) {
      return { disabled: false, queryPreference: false };
    }
  }

  return { disabled: getStoredPreference(windowRef), queryPreference: null };
}

function getDisableScrollFadeQueryValue(windowRef: Window): string | null {
  const searchValue = new URLSearchParams(windowRef.location.search).get(DISABLE_SCROLL_FADE_QUERY_PARAM);
  if (searchValue != null) return searchValue;

  const queryStart = windowRef.location.hash.indexOf('?');
  if (queryStart < 0) return null;
  return new URLSearchParams(windowRef.location.hash.slice(queryStart + 1)).get(DISABLE_SCROLL_FADE_QUERY_PARAM);
}

function getStoredPreference(windowRef: Window): boolean {
  try {
    const value = windowRef.localStorage.getItem(DISABLE_SCROLL_FADE_STORAGE_KEY);
    return value ? ENABLED_VALUES.has(value.trim().toLowerCase()) : false;
  } catch {
    return false;
  }
}

function setStoredPreference(windowRef: Window, disabled: boolean): void {
  try {
    if (disabled) windowRef.localStorage.setItem(DISABLE_SCROLL_FADE_STORAGE_KEY, '1');
    else windowRef.localStorage.removeItem(DISABLE_SCROLL_FADE_STORAGE_KEY);
  } catch {
    return;
  }
}