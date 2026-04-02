import { test, expect } from '../fixtures/fre';
import { goto, gotoFresh } from '../fixtures/navigation';

/*
 * Settings-first navigation tests — going to /settings first, changing
 * toggles, then visiting pages to verify FRE slide effects.
 */

test.describe('Settings-Direct FRE', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('direct to /settings, toggle Item Shop off → /songs has no shop slides', async ({ page, fre, freState }) => {
    await freState.setSettings({ hideItemShop: true });
    await goto(page, '/songs');
    await fre.waitForVisible();

    // No shop slides at all (hideItemShop makes isShopVisible = false)
    // Only 3 ungated slides (no player)
    await fre.assertSlideCount(3);
  });

  test('direct to /settings, toggle shop pulse off → /songs has no highlighting slides', async ({ page, fre, freState }) => {
    await freState.setSettings({ disableShopHighlighting: true });
    await goto(page, '/songs');
    await fre.waitForVisible();

    // Shop is visible but highlighting is off → shopHighlightEnabled = false
    // 3 ungated slides (no player), no shop-highlight/leaving-tomorrow
    await fre.assertSlideCount(3);
  });

  test('set player → /statistics shows all 5 slides', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/statistics');
    await fre.waitForVisible();

    await fre.assertSlideCount(5);
  });

  test('enable experimental + set player → /compete shows all 3 slides', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await freState.setSettings({ enableExperimentalRanks: true });
    await goto(page, '/compete');
    await fre.waitForVisible();

    await fre.assertSlideCount(3);
  });

  test('settings replay: clearing seen state and revisiting shows FRE again', async ({ page, fre, freState }) => {
    // Visit songs to see and dismiss FRE
    await freState.setSettings({ hideItemShop: true });
    await goto(page, '/songs');
    await fre.waitForVisible();
    await fre.dismiss();

    // Verify seen state was saved
    const seen = await freState.getSeenSlides();
    expect(Object.keys(seen).length).toBeGreaterThan(0);

    // Clear seen state (simulates what Settings reset does)
    await freState.clearFirstRunState();

    // Revisit songs with fresh load — FRE should reappear
    await gotoFresh(page, '/songs');
    await fre.waitForVisible();
    const count = await fre.slideCount();
    expect(count).toBeGreaterThanOrEqual(3);
  });

  test('settings reset → revisit page → FRE shows again', async ({ page, fre, freState }) => {
    // Visit songs and dismiss FRE
    await freState.setSettings({ hideItemShop: true });
    await goto(page, '/songs');
    await fre.waitForVisible();
    await fre.dismiss();

    // Verify no carousel on revisit
    await goto(page, '/settings');
    await goto(page, '/songs');
    await page.waitForTimeout(2000);
    expect(await fre.isVisible()).toBe(false);

    // Reset FRE state
    await freState.clearFirstRunState();

    // Revisit songs — FRE should reappear
    await gotoFresh(page, '/songs');
    await fre.waitForVisible();
    await fre.assertSlideCount(3);
  });
});
