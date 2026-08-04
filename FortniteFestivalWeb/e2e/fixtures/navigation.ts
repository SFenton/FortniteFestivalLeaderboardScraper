import type { Page } from '@playwright/test';

/**
 * Navigate to a hash-based route and wait for the page to settle.
 * Uses a full page load (not just hash change) to ensure React
 * re-reads localStorage state set during test setup.
 */
export async function goto(page: Page, route: string) {
  await page.goto(`/#${route}`, { waitUntil: 'load' });
}

/**
 * Replace the hash without dispatching a route change, then reload once so
 * React reads mutated localStorage before the destination route mounts.
 */
export async function gotoFresh(page: Page, route: string) {
  await page.evaluate(nextRoute => {
    window.history.replaceState(null, '', `/#${nextRoute}`);
  }, route);
  await page.reload({ waitUntil: 'load' });
}

/**
 * Fetch the first available song ID from the API response
 * by intercepting the /api/songs call.
 * Returns the songId string or null if unavailable.
 */
export async function getFirstSongId(page: Page): Promise<string | null> {
  try {
    return await page.evaluate(async () => {
      const response = await fetch('/api/songs');
      if (!response.ok) return null;
      const data = await response.json();
      const songs = Array.isArray(data) ? data : (data.songs ?? data.items ?? []);
      if (songs.length === 0) return null;
      return songs[0].trackId ?? songs[0].id ?? songs[0].songId ?? null;
    });
  } catch {
    return null;
  }
}
