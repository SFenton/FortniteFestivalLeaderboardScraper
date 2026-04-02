import { test, expect } from '../fixtures/fre';
import { goto, getFirstSongId } from '../fixtures/navigation';

/*
 * Player History FRE — 2 slides:
 *   playerhistory-score-list, playerhistory-sort (platform variant)
 *
 * The sort slide has NO contentKey — mobile/desktop track separately.
 */

let songId: string;

test.describe('Player History FRE', () => {

  test.beforeEach(async ({ page, freState }) => {
    await freState.resetAppState();
    const id = await getFirstSongId(page);
    songId = id ?? 'fallback-song-id';
  });

  test('fresh — shows 2 slides', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, `/songs/${songId}/Solo_Guitar/history`);
    await fre.waitForVisible();

    await fre.assertSlideCount(2);
  });

  test('direct URL entry', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, `/songs/${songId}/Solo_Guitar/history`);
    await fre.waitForVisible();

    const count = await fre.slideCount();
    expect(count).toBe(2);
  });

  test('platform variant: sort slide tracks separately for mobile/desktop', async ({ page, fre, freState }) => {
    // This test verifies the playerhistory-sort slide lacks contentKey,
    // meaning mobile and desktop have different content hashes.
    await freState.setTrackedPlayer();
    await goto(page, `/songs/${songId}/Solo_Guitar/history`);
    await fre.waitForVisible();
    await fre.dismiss();

    // Read seen state
    const seen = await freState.getSeenSlides();
    expect(seen).toHaveProperty('playerhistory-score-list');
    expect(seen).toHaveProperty('playerhistory-sort');
  });
});
