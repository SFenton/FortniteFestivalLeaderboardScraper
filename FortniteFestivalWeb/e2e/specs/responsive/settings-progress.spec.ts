import axe, { type AxeResults } from 'axe-core';
import type { ServiceInfoResponse } from '@festival/core/api';
import { expect, test } from '../../fixtures/test';
import { createPopulatedScenario } from '../../fixtures/scenarios';
import { gotoAppRoute } from '../../support/drivers/app';
import { WIDE_PROJECT } from '../../support/projects';

declare global {
  interface Window {
    axe: {
      run(context?: string): Promise<AxeResults>;
    };
  }
}

const idleServiceInfo = createPopulatedScenario().serviceInfo;

function runningServiceInfo(unitsTotalFinal: boolean): ServiceInfoResponse {
  return {
    ...idleServiceInfo,
    contractVersion: 2,
    activeScrapeId: 2,
    phasePlan: {
      version: 'fst.scrape-plan.v2',
      phases: [{
        id: 'post.compute_rankings',
        label: 'Computing rankings',
        legacyPhase: 'ComputeRankings',
        ordinal: 310,
        defaultUnitsKind: 'instruments',
      }],
    },
    currentUpdate: {
      status: 'updating',
      scrapeId: 2,
      startedAt: '2026-01-02T10:00:00.000Z',
      phase: 'ComputeRankings',
      subOperation: 'per_instrument_rankings',
      contractVersion: 2,
      operationId: 'scrape.update',
      phaseId: 'post.compute_rankings',
      phaseStatus: 'running',
      subphaseId: 'per_instrument_rankings',
      phasePlanVersion: 'fst.scrape-plan.v2',
      phaseOrdinal: 310,
      phaseAttempt: 1,
      unitsKind: 'instruments',
      unitsCompleted: 3,
      unitsTotal: 8,
      unitsTotalFinal,
      phasePercent: 37.5,
      overallPercentKind: unitsTotalFinal ? 'historical_phase_durations' : 'indeterminate',
      overallPercent: unitsTotalFinal ? 63.2 : null,
      overallModelVersion: unitsTotalFinal ? 'phase-timings.v1' : null,
      etaLowerSeconds: unitsTotalFinal ? 120 : null,
      etaUpperSeconds: unitsTotalFinal ? 240 : null,
      etaConfidence: unitsTotalFinal ? 'medium' : null,
      etaSampleCount: unitsTotalFinal ? 9 : null,
      heartbeatAt: '2026-01-02T10:05:05.000Z',
      lastProgressAt: '2026-01-02T10:05:00.000Z',
      branches: null,
    },
    workerStatus: {
      ...(idleServiceInfo.workerStatus ?? {}),
      workerKey: idleServiceInfo.workerStatus?.workerKey ?? 'e2e-worker',
      status: 'online',
      currentOperation: {
        operationKey: 'rankings.all',
        operationLabel: 'Computing rankings',
        status: 'running',
        startedAt: '2026-01-02T10:00:00.000Z',
        updatedAt: '2026-01-02T10:05:00.000Z',
      },
    },
  };
}

function failedServiceInfo(): ServiceInfoResponse {
  return {
    ...idleServiceInfo,
    lastCompletedUpdate: idleServiceInfo.lastCompletedUpdate
      ? {
        ...idleServiceInfo.lastCompletedUpdate,
        bestEffortFailureCount: 2,
      }
      : null,
    currentUpdate: {
      status: 'failed',
      startedAt: '2026-01-02T10:00:00.000Z',
      phase: 'Publishing',
      subOperation: 'failed',
      detail: 'Publication failed',
    },
    workerStatus: {
      ...(idleServiceInfo.workerStatus ?? {}),
      workerKey: idleServiceInfo.workerStatus?.workerKey ?? 'e2e-worker',
      status: 'offline',
    },
  };
}

test.use({ scenario: createPopulatedScenario() });
test.beforeEach(({}, testInfo) => {
  test.skip(testInfo.project.name !== WIDE_PROJECT, 'Settings viewport matrix runs once');
});

for (const width of [320, 375, 768, 1440]) {
  test(`idle Settings service groups fit without horizontal overflow at ${width}px`, async ({
    page,
    appState,
    api,
    scenario,
  }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    api.use({ ...scenario, serviceInfo: idleServiceInfo });
    await appState.reset();
    await gotoAppRoute(page, '/settings');

    const serviceInfo = page.getByTestId('settings-service-info-list');
    await expect(serviceInfo).toBeVisible();
    await expect(page.getByTestId('settings-service-technical-details')).not.toHaveAttribute('open', '');

    const geometry = await page.locator('#main-content').evaluate(element => ({
      clientWidth: element.clientWidth,
      scrollWidth: element.scrollWidth,
    }));
    expect(geometry.scrollWidth).toBeLessThanOrEqual(geometry.clientWidth + 1);

    for (const groupId of [
      'settings-service-health',
      'settings-service-progress',
      'settings-service-publication',
    ]) {
      const group = page.getByTestId(groupId);
      await expect(group).toBeVisible();
      const groupGeometry = await group.evaluate(element => ({
        clientWidth: element.clientWidth,
        scrollWidth: element.scrollWidth,
      }));
      expect(groupGeometry.scrollWidth).toBeLessThanOrEqual(groupGeometry.clientWidth + 1);
      await expect(group).not.toContainText('N/A');
    }

    await testInfo.attach(`settings-idle-${width}`, {
      body: await serviceInfo.screenshot(),
      contentType: 'image/png',
    });
  });
}

test('v2 determinate progress is accessible and technical details are keyboard operable', async ({
  page,
  appState,
  api,
  scenario,
}, testInfo) => {
  await page.setViewportSize({ width: 375, height: 900 });
  api.use({ ...scenario, serviceInfo: runningServiceInfo(true) });
  await appState.reset();
  await appState.selectPlayer();
  await gotoAppRoute(page, '/settings');

  const progress = page.getByRole('progressbar', { name: 'Current phase progress' });
  await expect(progress).toHaveAttribute('data-progress-kind', 'determinate');
  await expect(progress).toHaveAttribute('aria-valuemin', '0');
  await expect(progress).toHaveAttribute('aria-valuemax', '100');
  await expect(progress).toHaveAttribute('aria-valuenow', '37.5');
  await expect(progress).toHaveAttribute('aria-valuetext', /3 of 8 instruments completed/);
  await expect(page.getByText('Estimated overall progress: 63.2%')).toBeVisible();
  await expect(page.getByText(/Estimated 2m 0s–4m 0s remaining/)).toBeVisible();

  const details = page.getByTestId('settings-service-technical-details');
  const summary = details.locator('summary');
  await summary.focus();
  await expect(summary).toBeFocused();
  await summary.press('Enter');
  await expect(details).toHaveAttribute('open', '');
  await summary.press('Space');
  await expect(details).not.toHaveAttribute('open', '');

  const profileCard = page.getByTestId('settings-selected-profile-sync');
  await expect(profileCard).toBeVisible();
  expect(await page.getByTestId('settings-service-info-list').evaluate(
    service => service.contains(document.querySelector('[data-testid="settings-selected-profile-sync"]')),
  )).toBe(false);

  await page.addScriptTag({ content: axe.source });
  const results = await page.evaluate(() => window.axe.run('[data-testid="settings-service-info-list"]'));
  expect(results.violations.filter(
    violation => violation.impact === 'serious' || violation.impact === 'critical',
  )).toEqual([]);

  await testInfo.attach('settings-running-determinate', {
    body: await page.getByTestId('settings-service-info-list').screenshot(),
    contentType: 'image/png',
  });
});

test('v2 unknown totals stay indeterminate without numeric progress', async ({
  page,
  appState,
  api,
  scenario,
}, testInfo) => {
  await page.setViewportSize({ width: 375, height: 900 });
  api.use({ ...scenario, serviceInfo: runningServiceInfo(false) });
  await appState.reset();
  await gotoAppRoute(page, '/settings');

  const progress = page.getByRole('progressbar', { name: 'Current phase progress' });
  await expect(progress).toHaveAttribute('data-progress-kind', 'indeterminate');
  await expect(progress).not.toHaveAttribute('aria-valuemin');
  await expect(progress).not.toHaveAttribute('aria-valuemax');
  await expect(progress).not.toHaveAttribute('aria-valuenow');
  await expect(page.getByText('3 instruments completed; 8 discovered so far')).toBeVisible();
  await expect(page.getByText('37.5%')).toHaveCount(0);
  await expect(page.getByText(/Estimated overall progress/)).toHaveCount(0);

  await testInfo.attach('settings-running-indeterminate', {
    body: await page.getByTestId('settings-service-info-list').screenshot(),
    contentType: 'image/png',
  });
});

test('failed update keeps prior publication health visible', async ({
  page,
  appState,
  api,
  scenario,
}, testInfo) => {
  await page.setViewportSize({ width: 375, height: 900 });
  api.use({ ...scenario, serviceInfo: failedServiceInfo() });
  await appState.reset();
  await gotoAppRoute(page, '/settings');

  await expect(page.getByTestId('settings-service-info-row-update-status')).toContainText('Failed');
  await expect(page.getByTestId('settings-service-info-row-update-sub-status')).toContainText(
    'Published scrape 1 remains available',
  );
  await expect(page.getByText('The last completed update reported 2 non-critical warnings.')).toBeVisible();
  await expect(page.getByRole('progressbar')).toHaveCount(0);

  await testInfo.attach('settings-failed', {
    body: await page.getByTestId('settings-service-info-list').screenshot(),
    contentType: 'image/png',
  });
});
