import { test, expect } from '../fixtures/fre';
import { goto, gotoFresh, getFirstSongId } from '../fixtures/navigation';

/*
 * Cross-page FRE flow tests — verifying progressive revelation across
 * multiple pages, settings changes, and singleton carousel behavior.
 */

test.describe('Cross-Page FRE Flow', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('full journey: no player → visit multiple pages → add player → see new slides', async ({ page, fre, freState }) => {
    // 1. Songs page — no player, shop hidden → 3 ungated slides
    await freState.setSettings({ hideItemShop: true });
    await goto(page, '/songs');
    await fre.waitForVisible();
    await fre.assertSlideCount(3);
    await fre.dismiss();

    // 2. Leaderboards — no player → 1 slide (overview)
    await goto(page, '/leaderboards');
    await fre.waitForVisible();
    await fre.assertSlideCount(1);
    await fre.dismiss();

    // 3. Song detail — no player → 4 ungated slides
    const songId = await getFirstSongId(page);
    if (songId) {
      await goto(page, `/songs/${songId}`);
      await fre.waitForVisible();
      await fre.assertSlideCount(4);
      await fre.dismiss();
    }

    // 4. Set a player
    await freState.setTrackedPlayer();

    // 5. Songs — should show 3 NEW player-gated slides
    await gotoFresh(page, '/songs');
    await fre.waitForVisible();
    await fre.assertSlideCount(3);
    await fre.dismiss();

    // 6. Statistics — all 5 slides (no gates)
    await goto(page, '/statistics');
    await fre.waitForVisible();
    await fre.assertSlideCount(5);
    await fre.dismiss();

    // 7. Leaderboards — should show 1 NEW player-gated slide (your-rank)
    await goto(page, '/leaderboards');
    await fre.waitForVisible();
    await fre.assertSlideCount(1);
    await fre.dismiss();
  });

  test('settings toggle: shop highlighting off → songs → on → songs shows new slides', async ({ page, fre, freState }) => {
    // Start with highlighting disabled
    await freState.setSettings({ disableShopHighlighting: true });
    await goto(page, '/songs');
    await fre.waitForVisible();

    // No shop slides
    const count = await fre.slideCount();
    // 3 (no player) or 5+ if some other state
    await fre.dismiss();

    // Enable highlighting
    await freState.setSettings({ disableShopHighlighting: false });
    await gotoFresh(page, '/songs');
    await fre.waitForVisible();

    // Shop slides now appear as new unseen slides
    const newCount = await fre.slideCount();
    expect(newCount).toBe(2); // songs-shop-highlight + songs-leaving-tomorrow
  });

  test('settings toggle: experimental ranks → leaderboards shows new slide', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/leaderboards');
    await fre.waitForVisible();

    // 2 slides (overview + your-rank, no experimental)
    await fre.assertSlideCount(2);
    await fre.dismiss();

    // Enable experimental ranks
    await freState.setSettings({ enableExperimentalRanks: true });
    await gotoFresh(page, '/leaderboards');
    await fre.waitForVisible();

    // 1 new slide (experimental-metrics)
    await fre.assertSlideCount(1);
  });

  test('singleton: only one carousel visible at a time', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/songs');
    await fre.waitForVisible();

    // Carousel should be visible
    expect(await fre.isVisible()).toBe(true);

    // Count overlays — should be exactly 1
    const overlayCount = await page.locator('[data-testid="fre-overlay"]').count();
    expect(overlayCount).toBe(1);
  });
});
