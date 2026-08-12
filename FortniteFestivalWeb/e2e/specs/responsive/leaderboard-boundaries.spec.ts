import { test, expect } from '../../fixtures/test';
import { createPopulatedScenario, E2E_SONG_ID } from '../../fixtures/scenarios';
import { gotoAppRoute } from '../../support/drivers/app';
import { WIDE_PROJECT } from '../../support/projects';

test.use({ scenario: createPopulatedScenario() });
test.beforeEach(({}, testInfo) => {
  test.skip(testInfo.project.name !== WIDE_PROJECT, 'responsive boundary matrix runs once');
});

for (const width of [319, 320, 419, 420, 519, 520, 599, 600, 767, 768, 769, 1439, 1440]) {
  test(`leaderboard fits without horizontal overflow at ${width}px`, async ({ page, appState }) => {
    await page.setViewportSize({ width, height: 800 });
    await appState.reset();
    await appState.selectPlayer();
    await gotoAppRoute(page, `/songs/${E2E_SONG_ID}/Solo_Guitar`);

    await expect(page.getByText('Score Player 1', { exact: true })).toBeVisible();
    const geometry = await page.locator('#main-content').evaluate(element => ({
      clientWidth: element.clientWidth,
      scrollWidth: element.scrollWidth,
    }));
    expect(geometry.scrollWidth).toBeLessThanOrEqual(geometry.clientWidth + 1);
  });
}
