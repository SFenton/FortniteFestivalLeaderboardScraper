import { test, expect } from '../fixtures/fre';
import { goto } from '../fixtures/navigation';

/*
 * Statistics FRE — 6 slides, no gates:
 *   statistics-select-profile, statistics-drill-down, statistics-overview, statistics-instrument-breakdown,
 *   statistics-percentiles, statistics-top-songs
 *
 * Route requires a tracked player; redirects to /songs without one.
 */

test.describe('Statistics FRE', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('fresh, with player — shows all 6 slides', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/statistics');
    await fre.waitForVisible();

    await fre.assertSlideCount(6);
    const titles = await fre.collectAllTitles();
    expect(titles).toHaveLength(6);
    for (const t of titles) {
      expect(t.length).toBeGreaterThan(0);
    }
  });

  test('direct URL, no player — redirects to /songs and shows songs FRE', async ({ page, fre }) => {
    await goto(page, '/statistics');

    // Should redirect to songs
    await page.waitForURL(/#\/songs/, { timeout: 15_000 });
    await fre.waitForVisible();

    // The carousel shown should be the songs FRE, not statistics
    const count = await fre.slideCount();
    expect(count).toBeGreaterThanOrEqual(3);
  });

  test('direct URL, with player — shows statistics FRE', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/statistics');
    await fre.waitForVisible();

    await fre.assertSlideCount(6);
  });
});
