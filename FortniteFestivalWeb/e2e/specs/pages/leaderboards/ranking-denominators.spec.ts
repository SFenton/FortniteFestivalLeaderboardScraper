import { test, expect } from '../../../fixtures/test';
import { createPopulatedScenario } from '../../../fixtures/scenarios';
import { gotoAppRoute } from '../../../support/drivers/app';

test.describe('instrument ranking denominators', () => {
  test.use({ scenario: createPopulatedScenario() });

  test.beforeEach(async ({ appState }) => {
    await appState.reset();
    await appState.setSettings({ enableExperimentalRanks: true });
  });

  test('Lead and Pro Lead keep one API denominator across ranking methods', async ({ page, api }) => {
    const cases = [
      ['Solo_Guitar', 'totalscore'],
      ['Solo_Guitar', 'adjusted'],
      ['Solo_PeripheralGuitar', 'totalscore'],
      ['Solo_PeripheralGuitar', 'adjusted'],
    ] as const;

    for (const [instrument, rankBy] of cases) {
      await gotoAppRoute(
        page,
        `/leaderboards/all?instrument=${instrument}&rankBy=${rankBy}&page=1`,
      );

      await expect.poll(
        () => api.last(`/api/rankings/${instrument}`)?.search ?? '',
      ).toBe(`?rankBy=${rankBy}&page=1&pageSize=25`);

      const fractions = page.locator('#main-content').getByText(/^\d+ \/ \d+$/);
      await expect(fractions).toHaveCount(12);
      await expect.poll(async () => [
        ...new Set(await fractions.allTextContents()),
      ]).toEqual(['10 / 12']);
    }
  });
});
