import { test, expect } from '../fixtures/fre';
import { goto, gotoFresh } from '../fixtures/navigation';

/*
 * Leaderboards FRE — 3 possible slides:
 *   Always:   leaderboards-overview
 *   Gated (experimentalRanksEnabled):   leaderboards-experimental-metrics
 *   Gated (hasPlayer):   leaderboards-your-rank
 */

test.describe('Leaderboards FRE', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('fresh, no player, no experimental — shows 1 slide', async ({ page, fre }) => {
    await goto(page, '/leaderboards');
    await fre.waitForVisible();

    await fre.assertSlideCount(1);
  });

  test('fresh, with player, no experimental — shows 2 slides', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/leaderboards');
    await fre.waitForVisible();

    await fre.assertSlideCount(2);
  });

  test('fresh, with player + experimental — shows all 3 slides', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await freState.setSettings({ enableExperimentalRanks: true });
    await goto(page, '/leaderboards');
    await fre.waitForVisible();

    await fre.assertSlideCount(3);
  });

  test('progressive: enable experimental ranks → revisit shows new slide', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/leaderboards');
    await fre.waitForVisible();

    // 2 slides (overview + your-rank)
    await fre.assertSlideCount(2);
    await fre.dismiss();

    // Enable experimental in settings
    await freState.setSettings({ enableExperimentalRanks: true });
    await gotoFresh(page, '/leaderboards');
    await fre.waitForVisible();

    // Should show 1 new slide (experimental-metrics)
    await fre.assertSlideCount(1);
  });

  test('direct URL entry', async ({ page, fre }) => {
    await goto(page, '/leaderboards');
    await fre.waitForVisible();

    const count = await fre.slideCount();
    expect(count).toBeGreaterThanOrEqual(1);
  });
});
