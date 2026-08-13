import type { Page } from '@playwright/test';
import { expect, test } from '../../../fixtures/test';
import { changelogHash } from '../../../../src/changelogHash';
import { createPopulatedScenario, E2E_BAND } from '../../../fixtures/scenarios';

test.use({ scenario: createPopulatedScenario() });

test('Band details use total-score rank semantics when experimental ranks are disabled', async ({ page }) => {
  await openBand(page, false);

  const statistics = page.getByTestId('band-section-statistics');
  await expect(statistics).toBeVisible();
  await expect(statistics.getByText('Adjusted Percentile Rank')).toHaveCount(0);
  await expect(statistics.getByText('Weighted Percentile Rank')).toHaveCount(0);
  await expect(statistics.getByText('FC Rate Rank')).toHaveCount(0);
  await expect(statistics.getByText('Total Score Rank')).toBeVisible();

  const history = page.getByTestId('band-section-rank-history');
  await expect(history.getByText('Total Score').first()).toBeVisible();
  await expect(history.getByText('#10').first()).toBeVisible();

  await statistics.getByText('Total Score Rank').click();
  await expect(page).toHaveURL(/#\/leaderboards\/bands\/Band_Duets\?rankBy=totalscore&page=1$/);
});

test('Band details retain experimental rank cards and navigation when enabled', async ({ page }) => {
  await openBand(page, true);

  const statistics = page.getByTestId('band-section-statistics');
  await expect(statistics.getByText('Adjusted Percentile Rank')).toBeVisible();
  await expect(statistics.getByText('Weighted Percentile Rank')).toBeVisible();
  await expect(statistics.getByText('FC Rate Rank')).toBeVisible();
  await expect(statistics.getByText('Total Score Rank')).toBeVisible();

  const history = page.getByTestId('band-section-rank-history');
  await expect(history.getByText('Adjusted Percentile').first()).toBeVisible();
  await expect(history.getByText('#7').first()).toBeVisible();

  await statistics.getByText('Adjusted Percentile Rank').click();
  await expect(page).toHaveURL(/#\/leaderboards\/bands\/Band_Duets\?rankBy=adjusted&page=1$/);
});

async function openBand(page: Page, enableExperimentalRanks: boolean) {
  await page.addInitScript(({ enabled, hash }) => {
    localStorage.clear();
    localStorage.setItem('fst:changelog', JSON.stringify({ version: 'e2e', hash }));
    localStorage.setItem('fst:appSettings', JSON.stringify({
      disableLightTrails: true,
      enableExperimentalRanks: enabled,
    }));
  }, { enabled: enableExperimentalRanks, hash: changelogHash() });

  await page.goto(`/#/bands/${E2E_BAND.bandId}`, { waitUntil: 'load' });
}
