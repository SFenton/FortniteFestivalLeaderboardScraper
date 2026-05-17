import { afterEach, describe, expect, it } from 'vitest';
import {
  applyScrollFadeTestMode,
  DISABLE_SCROLL_FADE_CLASS,
  DISABLE_SCROLL_FADE_STORAGE_KEY,
  isScrollFadeTestModeEnabled,
} from '../../src/diagnostics/scrollFadeTestMode';

describe('scrollFadeTestMode', () => {
  afterEach(() => {
    window.localStorage.clear();
    document.documentElement.classList.remove(DISABLE_SCROLL_FADE_CLASS);
    window.history.replaceState(null, '', '/');
  });

  it('enables scroll fade suppression from the root query and persists it', () => {
    window.history.replaceState(null, '', '/?disableScrollFade=1#/songs/song-1');

    expect(applyScrollFadeTestMode()).toBe(true);
    expect(document.documentElement.classList.contains(DISABLE_SCROLL_FADE_CLASS)).toBe(true);
    expect(window.localStorage.getItem(DISABLE_SCROLL_FADE_STORAGE_KEY)).toBe('1');

    window.history.replaceState(null, '', '/#/songs/song-1');
    document.documentElement.classList.remove(DISABLE_SCROLL_FADE_CLASS);

    expect(applyScrollFadeTestMode()).toBe(true);
    expect(document.documentElement.classList.contains(DISABLE_SCROLL_FADE_CLASS)).toBe(true);
  });

  it('supports hash-route query flags for local testing', () => {
    window.history.replaceState(null, '', '/#/songs/song-1?disableScrollFade=on');

    expect(isScrollFadeTestModeEnabled()).toBe(true);
    expect(window.localStorage.getItem(DISABLE_SCROLL_FADE_STORAGE_KEY)).toBeNull();

    expect(applyScrollFadeTestMode()).toBe(true);
    expect(window.localStorage.getItem(DISABLE_SCROLL_FADE_STORAGE_KEY)).toBe('1');
  });

  it('disables scroll fade suppression and clears persisted state from the query', () => {
    window.localStorage.setItem(DISABLE_SCROLL_FADE_STORAGE_KEY, '1');
    document.documentElement.classList.add(DISABLE_SCROLL_FADE_CLASS);
    window.history.replaceState(null, '', '/?disableScrollFade=0#/songs/song-1');

    expect(applyScrollFadeTestMode()).toBe(false);
    expect(document.documentElement.classList.contains(DISABLE_SCROLL_FADE_CLASS)).toBe(false);
    expect(window.localStorage.getItem(DISABLE_SCROLL_FADE_STORAGE_KEY)).toBeNull();
  });
});