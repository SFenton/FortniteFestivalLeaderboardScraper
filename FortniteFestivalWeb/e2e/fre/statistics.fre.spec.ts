import { test, expect } from '../fixtures/fre';
import { goto } from '../fixtures/navigation';

/*
 * Statistics FRE — 5 slides, no gates:
 *   statistics-drill-down, statistics-overview, statistics-instrument-breakdown,
 *   statistics-percentiles, statistics-top-songs
 *
 * Route requires a tracked player; redirects to /songs without one.
 */

test.describe('Statistics FRE', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('fresh, with player — shows all 5 slides', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/statistics');
    await fre.waitForVisible();

    await fre.assertSlideCount(5);
    const titles = await fre.collectAllTitles();
    expect(titles).toHaveLength(5);
    for (const t of titles) {
      expect(t.length).toBeGreaterThan(0);
    }
  });

  test('direct URL, no player — redirects to /songs and shows songs FRE', async ({ page, fre }) => {
    await goto(page, '/statistics');

    // Should redirect to songs
    await page.waitForURL(/#\/songs/, { timeout: 5000 });
    await fre.waitForVisible();

    // The carousel shown should be the songs FRE, not statistics
    const count = await fre.slideCount();
    expect(count).toBeGreaterThanOrEqual(3);
  });

  test('direct URL, with player — shows statistics FRE', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/statistics');
    await fre.waitForVisible();

    await fre.assertSlideCount(5);
  });
});
