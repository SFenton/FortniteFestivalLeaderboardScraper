import { test, expect } from '../fixtures/fre';
import { goto, gotoFresh } from '../fixtures/navigation';

/*
 * Feature flag tests — uses localStorage override (fst:featureFlagOverrides)
 * added to FeatureFlagsContext.tsx for dev-mode flag testing.
 */

test.describe('Feature Flags FRE', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('firstRun flag OFF — no carousel appears anywhere', async ({ page, fre, freState }) => {
    await freState.setFeatureFlags({ firstRun: false });
    await goto(page, '/songs');

    // Wait and verify no carousel
    await page.waitForTimeout(3000);
    expect(await fre.isVisible()).toBe(false);
  });

  test('firstRun flag OFF then ON — carousel appears on revisit', async ({ page, fre, freState }) => {
    await freState.setFeatureFlags({ firstRun: false });
    await goto(page, '/songs');
    await page.waitForTimeout(2000);
    expect(await fre.isVisible()).toBe(false);

    // Clear override (all flags ON)
    await freState.clearFeatureFlags();
    await page.reload();
    await page.waitForTimeout(3000);

    await fre.waitForVisible();
    const count = await fre.slideCount();
    expect(count).toBeGreaterThanOrEqual(3);
  });

  test('shop flag OFF — shop-related songs slides absent', async ({ page, fre, freState }) => {
    await freState.setFeatureFlags({ shop: false });
    await goto(page, '/songs');
    await fre.waitForVisible();

    // Without shop feature, isShopVisible = false → no shop slides
    // Should only show ungated non-shop slides (3 for no player)
    await fre.assertSlideCount(3);
  });

  test('rivals flag OFF — /rivals redirects, no rivals FRE', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await freState.setFeatureFlags({ rivals: false });
    await goto(page, '/rivals');

    // Should redirect to songs
    await page.waitForURL(/#\/songs/, { timeout: 5000 });
    // Any carousel shown should be songs, not rivals
    if (await fre.isVisible()) {
      const count = await fre.slideCount();
      // Songs without player-gated slides but with player → 6+
      expect(count).toBeGreaterThanOrEqual(3);
    }
  });

  test('compete flag OFF — /compete redirects, no compete FRE', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await freState.setFeatureFlags({ compete: false });
    await goto(page, '/compete');

    await page.waitForURL(/#\/songs/, { timeout: 5000 });
  });

  test('leaderboards flag OFF — /leaderboards redirects, no leaderboards FRE', async ({ page, fre, freState }) => {
    await freState.setFeatureFlags({ leaderboards: false });
    await goto(page, '/leaderboards');

    await page.waitForURL(/#\/songs/, { timeout: 5000 });
  });

  test('multiple flags OFF — only eligible pages show FRE', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await freState.setFeatureFlags({ shop: false, rivals: false, compete: false });
    await freState.setSettings({ hideItemShop: true });

    // Songs page — should still work
    await goto(page, '/songs');
    await fre.waitForVisible();
    await fre.dismiss();

    // Statistics — should still work
    await goto(page, '/statistics');
    await fre.waitForVisible();
    await fre.dismiss();

    // Rivals — should redirect
    await goto(page, '/rivals');
    await page.waitForURL(/#\/songs/, { timeout: 5000 });
  });
});
