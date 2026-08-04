import { test, expect } from '../fixtures/fre';
import { goto, gotoFresh, getFirstSongId } from '../fixtures/navigation';

/*
 * Song Detail FRE — 8 possible slides:
 *   Always:   songinfo-top-scores, songinfo-paths (variant), songinfo-shop-button (variant),
 *             songinfo-new-in-shop (variant), songinfo-leaving-tomorrow (variant)
 *   Gated (hasPlayer):   songinfo-chart, songinfo-bar-select, songinfo-view-all
 */

let songId: string;

test.describe('Song Detail FRE', () => {

  test.beforeEach(async ({ page, freState }) => {
    await freState.resetAppState();
    // Fetch a real song ID from the API
    const id = await getFirstSongId(page);
    songId = id ?? 'fallback-song-id';
  });

  test('fresh, no player — shows 5 ungated slides', async ({ page, fre }) => {
    await goto(page, `/songs/${songId}`);
    await fre.waitForVisible();

    await fre.assertSlideCount(5);
  });

  test('fresh, with player — shows all 8 slides', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, `/songs/${songId}`);
    await fre.waitForVisible();

    await fre.assertSlideCount(8);
    const titles = await fre.collectAllTitles();
    expect(titles).toHaveLength(8);
  });

  test('progressive: no player → select player → revisit shows new slides', async ({ page, fre, freState }) => {
    await goto(page, `/songs/${songId}`);
    await fre.waitForVisible();

    await fre.assertSlideCount(5);
    await fre.dismiss();

    // Set player and revisit
    await freState.setTrackedPlayer();
    await gotoFresh(page, `/songs/${songId}`);
    await fre.waitForVisible();

    // 3 new player-gated slides
    await fre.assertSlideCount(3);
  });

  test('direct URL entry with no prior state', async ({ page, fre }) => {
    await goto(page, `/songs/${songId}`);
    await fre.waitForVisible();

    // Should show ungated slides
    const count = await fre.slideCount();
    expect(count).toBeGreaterThanOrEqual(5);
  });
});
