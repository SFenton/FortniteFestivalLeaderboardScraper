import { test, expect } from '../fixtures/fre';
import { goto, gotoFresh } from '../fixtures/navigation';

/*
 * Songs page FRE — 8 possible slides:
 *   Always:   songs-song-list, songs-sort, songs-navigation (platform variant)
 *   Gated (hasPlayer):   songs-filter, songs-icons, songs-metadata
 *   Gated (shopHighlightEnabled):   songs-shop-highlight, songs-leaving-tomorrow
 */

test.describe('Songs FRE', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('fresh, no player, no shop — shows 3 ungated slides', async ({ page, fre, freState }) => {
    // Disable shop highlighting so shop slides are gated out
    await freState.setSettings({ hideItemShop: true });
    await goto(page, '/songs');
    await fre.waitForVisible();

    await fre.assertSlideCount(3);
    const titles = await fre.collectAllTitles();
    expect(titles).toHaveLength(3);
  });

  test('fresh, no player, shop enabled — shows 5 slides', async ({ page, fre }) => {
    // Default settings: shop is visible, highlighting not disabled
    await goto(page, '/songs');
    await fre.waitForVisible();

    await fre.assertSlideCount(5);
  });

  test('fresh, with player, shop disabled — shows 6 slides', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await freState.setSettings({ hideItemShop: true });
    await goto(page, '/songs');
    await fre.waitForVisible();

    await fre.assertSlideCount(6);
  });

  test('fresh, with player + shop — shows all 8 slides', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/songs');
    await fre.waitForVisible();

    await fre.assertSlideCount(8);
    const titles = await fre.collectAllTitles();
    expect(titles).toHaveLength(8);
    // Every title should be non-empty (translated)
    for (const t of titles) {
      expect(t.length).toBeGreaterThan(0);
    }
  });

  test('progressive: no player → select player → revisit shows new slides', async ({ page, fre, freState }) => {
    await freState.setSettings({ hideItemShop: true });
    await goto(page, '/songs');
    await fre.waitForVisible();

    // See 3 ungated slides and dismiss
    await fre.assertSlideCount(3);
    await fre.dismiss();
    expect(await fre.isVisible()).toBe(false);

    // Now set a player (simulating profile selection)
    await freState.setTrackedPlayer();

    // Navigate away and back with fresh reload to pick up new state
    await gotoFresh(page, '/songs');
    await fre.waitForVisible();

    // Should show 3 NEW player-gated slides (filter, icons, metadata)
    await fre.assertSlideCount(3);
    await fre.dismiss();

    // Revisiting again should show nothing
    await gotoFresh(page, '/songs');
    // Give a moment for any carousel to appear
    await page.waitForTimeout(2000);
    expect(await fre.isVisible()).toBe(false);
  });

  test('dismissing persists — revisit shows no carousel', async ({ page, fre, freState }) => {
    await freState.setSettings({ hideItemShop: true });
    await goto(page, '/songs');
    await fre.waitForVisible();
    await fre.dismiss();

    // Reload the page
    await page.reload();
    await page.waitForTimeout(3000);
    expect(await fre.isVisible()).toBe(false);
  });
});
