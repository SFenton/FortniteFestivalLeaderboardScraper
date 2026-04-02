import { test, expect } from '../fixtures/fre';
import { goto } from '../fixtures/navigation';

/*
 * Rivals FRE — 3 slides, no gates:
 *   rivals-overview, rivals-instruments, rivals-detail
 *
 * Route requires player + rivals feature flag.
 */

test.describe('Rivals FRE', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('fresh, with player — shows all 3 slides', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/rivals');
    await fre.waitForVisible();

    await fre.assertSlideCount(3);
    const titles = await fre.collectAllTitles();
    expect(titles).toHaveLength(3);
  });

  test('direct URL, no player — redirects to /songs', async ({ page, fre }) => {
    await goto(page, '/rivals');

    await page.waitForURL(/#\/songs/, { timeout: 5000 });
    await fre.waitForVisible();

    // Should be songs FRE
    const count = await fre.slideCount();
    expect(count).toBeGreaterThanOrEqual(3);
  });
});
