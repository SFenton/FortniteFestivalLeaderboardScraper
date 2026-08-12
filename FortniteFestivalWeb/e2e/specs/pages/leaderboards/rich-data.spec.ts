import { test, expect } from '../../../fixtures/test';
import { createEmptyScenario, createPopulatedScenario, E2E_SONG_ID } from '../../../fixtures/scenarios';
import { dismissObstructions, gotoAppRoute } from '../../../support/drivers/app';

test.describe('populated leaderboard presentation', () => {
  test.use({ scenario: createPopulatedScenario() });

  test.beforeEach(async ({ appState }) => {
    await appState.reset();
    await appState.selectPlayer();
  });

  test('tracked player row is purple and pagination requests the next page', async ({ page, api }) => {
    await gotoAppRoute(page, `/songs/${E2E_SONG_ID}/Solo_Guitar`);

    const playerRow = page.getByText('SFentonX', { exact: true }).locator('xpath=ancestor::a[1]');
    await expect(playerRow).toHaveCSS('background-color', 'rgba(75, 15, 99, 0.75)');
    await expect(page.getByTestId('leaderboard-fixed-pagination')).toBeVisible();
    const pageInfo = page.getByTestId('leaderboard-page-info');
    await expect(pageInfo).toContainText('1');
    await page.getByRole('button', { name: 'Next' }).click();
    await expect(pageInfo).toContainText('2');
    await expect.poll(() => api.last(/^\/api\/leaderboard\//)?.search ?? '').toContain('offset=25');
  });

  test('selected band row uses the purple selected treatment', async ({ page }) => {
    await gotoAppRoute(page, `/songs/${E2E_SONG_ID}/bands/Band_Duets`);

    const selected = page.getByTestId('song-band-leaderboard-entry-2');
    await expect(selected).toContainText('E2E Player A');
    await expect(selected).toHaveCSS('background-color', 'rgba(75, 15, 99, 0.75)');
  });

  test('invalid score exposes its explanation dialog', async ({ page, appState }) => {
    await appState.setSettings({ filterInvalidScores: true, filterInvalidScoresLeeway: 1 });
    await gotoAppRoute(page, '/songs');
    await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
    await dismissObstructions(page);

    const invalidScore = page.getByRole('button', { name: /invalid score/i }).first();
    await expect(invalidScore).toBeVisible();
    await invalidScore.click();
    await expect(page.getByRole('alertdialog')).toContainText(/score/i);
  });
});

test.describe('empty leaderboard presentation', () => {
  test.use({ scenario: createEmptyScenario() });

  test('empty leaderboard explains that no scores are available', async ({ page, appState }) => {
    await appState.reset();
    await gotoAppRoute(page, `/songs/${E2E_SONG_ID}/Solo_Guitar`);
    await expect(page.locator('#main-content')).toContainText(/no entries on this page/i);
  });
});
