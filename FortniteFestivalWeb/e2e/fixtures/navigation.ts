import type { Page } from '@playwright/test';

/**
 * Navigate to a hash-based route and wait for the page to settle.
 * Uses a full page load (not just hash change) to ensure React
 * re-reads localStorage state set during test setup.
 */
export async function goto(page: Page, route: string) {
  await page.goto(`/#${route}`, { waitUntil: 'load' });
  // Allow React to mount + any initial data fetches
  await page.waitForTimeout(2000);
}

/**
 * Change the hash route and reload to force React to re-read
 * localStorage. Use this mid-test after mutating localStorage
 * when you need the app to pick up the new state.
 */
export async function gotoFresh(page: Page, route: string) {
  await page.goto(`/#${route}`, { waitUntil: 'load' });
  await page.reload({ waitUntil: 'load' });
  await page.waitForTimeout(2000);
}

/**
 * Fetch the first available song ID from the API response
 * by intercepting the /api/songs call.
 * Returns the songId string or null if unavailable.
 */
export async function getFirstSongId(page: Page): Promise<string | null> {
  try {
    const response = await page.request.get('/api/songs');
    if (!response.ok()) return null;
    const data = await response.json();
    // Songs may be an array or object with an array field
    const songs = Array.isArray(data) ? data : (data.songs ?? data.items ?? []);
    if (songs.length === 0) return null;
    return songs[0].trackId ?? songs[0].id ?? songs[0].songId ?? null;
  } catch {
    return null;
  }
}
