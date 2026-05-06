import { test, expect } from '../fixtures/fre';
import { goto } from '../fixtures/navigation';

/*
 * Legacy feature-flag override checks. UI feature gates are removed, so stale
 * dev overrides must not hide routes or first-run eligibility.
 */

test.describe('Legacy Feature Flag Overrides', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('legacy leaderboards override does not hide /leaderboards', async ({ page, freState }) => {
    await freState.setLegacyFeatureFlagOverrides({ leaderboards: false });
    await goto(page, '/leaderboards');

    await page.waitForURL(/#\/leaderboards/, { timeout: 5000 });
    await expect(page.getByRole('heading', { name: 'Leaderboards' })).toBeVisible();
  });

  test('legacy leaderboards override does not hide /compete', async ({ page, freState }) => {
    await freState.setTrackedPlayer();
    await freState.setLegacyFeatureFlagOverrides({ leaderboards: false });
    await goto(page, '/compete');

    await page.waitForURL(/#\/compete/, { timeout: 5000 });
    await expect(page.getByRole('heading', { name: 'Compete' })).toBeVisible();
  });

  test('legacy leaderboards override leaves formerly gated pages eligible for FRE', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await freState.setSettings({ hideItemShop: true });
    await freState.setLegacyFeatureFlagOverrides({ leaderboards: false });

    await goto(page, '/songs');
    await fre.waitForVisible();
    await fre.dismiss();

    // Statistics — should still work
    await goto(page, '/statistics');
    await fre.waitForVisible();
    await fre.dismiss();

    await goto(page, '/compete');
    await page.waitForURL(/#\/compete/, { timeout: 5000 });
    await fre.waitForVisible();
  });
});
