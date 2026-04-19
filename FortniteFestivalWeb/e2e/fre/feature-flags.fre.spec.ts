import { test, expect } from '../fixtures/fre';
import { goto } from '../fixtures/navigation';

/*
 * Remaining feature-gate tests for leaderboards-driven routes.
 * Rivals and first-run are always-on; Compete stays gated by Leaderboards.
 */

test.describe('Leaderboards Feature Gates', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('leaderboards flag OFF — /leaderboards redirects', async ({ page, freState }) => {
    await freState.setFeatureFlags({ leaderboards: false });
    await goto(page, '/leaderboards');

    await page.waitForURL(/#\/songs/, { timeout: 5000 });
  });

  test('leaderboards flag OFF — /compete redirects because Compete is derived from Leaderboards', async ({ page, freState }) => {
    await freState.setTrackedPlayer();
    await freState.setFeatureFlags({ leaderboards: false });
    await goto(page, '/compete');

    await page.waitForURL(/#\/songs/, { timeout: 5000 });
  });

  test('leaderboards flag OFF still leaves always-on pages eligible for FRE', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await freState.setSettings({ hideItemShop: true });
    await freState.setFeatureFlags({ leaderboards: false });

    await goto(page, '/songs');
    await fre.waitForVisible();
    await fre.dismiss();

    // Statistics — should still work
    await goto(page, '/statistics');
    await fre.waitForVisible();
    await fre.dismiss();

    await goto(page, '/compete');
    await page.waitForURL(/#\/songs/, { timeout: 5000 });
  });
});
