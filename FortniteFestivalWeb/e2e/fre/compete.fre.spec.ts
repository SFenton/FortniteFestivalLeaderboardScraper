import { test, expect } from '../fixtures/fre';
import { goto, gotoFresh } from '../fixtures/navigation';

/*
 * Compete FRE — 3 possible slides:
 *   Always:   compete-hub
 *   Gated (experimentalRanksEnabled):   compete-leaderboards
 *   Gated (hasPlayer):   compete-rivals
 *
 * Route requires player + compete feature flag.
 */

test.describe('Compete FRE', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('fresh, with player, no experimental — shows 2 slides (hub + rivals)', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/compete');
    await fre.waitForVisible();

    await fre.assertSlideCount(2);
  });

  test('fresh, with player + experimental — shows all 3 slides', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await freState.setSettings({ enableExperimentalRanks: true });
    await goto(page, '/compete');
    await fre.waitForVisible();

    await fre.assertSlideCount(3);
  });

  test('progressive: enable experimental → revisit shows new slides', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/compete');
    await fre.waitForVisible();

    await fre.assertSlideCount(2);
    await fre.dismiss();

    // Enable experimental ranks
    await freState.setSettings({ enableExperimentalRanks: true });
    await gotoFresh(page, '/compete');
    await fre.waitForVisible();

    // 1 new slide (leaderboards)
    await fre.assertSlideCount(1);
  });
});
