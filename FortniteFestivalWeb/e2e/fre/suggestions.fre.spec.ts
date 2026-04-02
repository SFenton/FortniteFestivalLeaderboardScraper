import { test, expect } from '../fixtures/fre';
import { goto } from '../fixtures/navigation';

/*
 * Suggestions FRE — 4 slides, no gates:
 *   suggestions-category-card, suggestions-global-filter,
 *   suggestions-instrument-filter, suggestions-infinite-scroll
 *
 * Route requires a tracked player; redirects to /songs without one.
 */

test.describe('Suggestions FRE', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('fresh, with player — shows all 4 slides', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/suggestions');
    await fre.waitForVisible();

    await fre.assertSlideCount(4);
    const titles = await fre.collectAllTitles();
    expect(titles).toHaveLength(4);
  });

  test('direct URL, no player — redirects to /songs', async ({ page, fre }) => {
    await goto(page, '/suggestions');

    await page.waitForURL(/#\/songs/, { timeout: 5000 });
    await fre.waitForVisible();

    // Songs FRE, not suggestions
    const count = await fre.slideCount();
    expect(count).toBeGreaterThanOrEqual(3);
  });
});
