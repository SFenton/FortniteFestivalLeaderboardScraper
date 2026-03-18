/** Maximum number of display stars (gold stars show as 5). */
export const MAX_DISPLAY_STARS = 5;

/** Star count threshold at or above which all stars render as gold. */
export const GOLD_STARS_THRESHOLD = 6;

/** Returns the number of stars to render, clamping gold to MAX_DISPLAY_STARS. */
export function displayStarCount(starsCount: number): number {
  return starsCount >= GOLD_STARS_THRESHOLD ? MAX_DISPLAY_STARS : Math.max(1, starsCount);
}
