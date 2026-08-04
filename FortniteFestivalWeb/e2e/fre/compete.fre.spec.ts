import { test, expect } from '../fixtures/fre';
import { goto, gotoFresh } from '../fixtures/navigation';

/*
 * Compete FRE — 3 possible slides:
 *   Always:   compete-hub, compete-leaderboards
 *   Gated (hasPlayer):   compete-rivals
 *
 * Route requires a player.
 */

test.describe('Compete FRE', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('fresh, with player, no experimental — includes generic leaderboard guidance', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/compete');
    await fre.waitForVisible();

    await fre.assertSlideCount(3);
    expect(await fre.collectAllTitles()).toEqual(['Compete Hub', 'Leaderboards', 'Rivals']);
  });

  test('fresh, with player + experimental — keeps the same 3 slides', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await freState.setSettings({ enableExperimentalRanks: true });
    await goto(page, '/compete');
    await fre.waitForVisible();

    await fre.assertSlideCount(3);
    expect(await fre.collectAllTitles()).toEqual(['Compete Hub', 'Leaderboards', 'Rivals']);
  });

  test('enabling experimental ranks does not replay generic leaderboard guidance', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/compete');
    await fre.waitForVisible();

    await fre.assertSlideCount(3);
    await fre.dismiss();

    await freState.setSettings({ enableExperimentalRanks: true });
    await gotoFresh(page, '/compete');
    await page.waitForTimeout(1_000);
    expect(await fre.isVisible()).toBe(false);
  });
});
