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
      subphaseProgress: {
        schemaVersion: 1,
        id: 'per_instrument_rankings',
        epoch: 1,
        sequence: 1,
        kind: unitsTotalFinal ? 'exact' : 'indeterminate',
        unitsKind: unitsTotalFinal ? 'instruments' : null,
        unitsCompleted: unitsTotalFinal ? 3 : null,
        unitsTotal: unitsTotalFinal ? 8 : null,
        unitsTotalFinal,
        percent: unitsTotalFinal ? 37.5 : null,
        startedAt: '2026-01-02T10:00:00.000Z',
        lastProgressAt: '2026-01-02T10:05:00.000Z',
      },
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

function notApplicableServiceInfo(): ServiceInfoResponse {
  const running = runningServiceInfo(false);
  return {
    ...running,
    currentUpdate: {
      ...running.currentUpdate,
      subOperation: 'skipping_band_after_timeout',
      subphaseId: 'skipping_band_after_timeout',
      subphaseProgress: {
        schemaVersion: 1,
        id: 'skipping_band_after_timeout',
        epoch: 2,
        sequence: 3,
        kind: 'not_applicable',
        unitsTotalFinal: false,
      },
    },
  };
}

function exactProgressServiceInfo(
  percent: number,
  epoch: number,
  sequence: number,
): ServiceInfoResponse {
  const running = runningServiceInfo(true);
  return {
    ...running,
    currentUpdate: {
      ...running.currentUpdate,
      subphaseProgress: {
        schemaVersion: 1,
        id: 'per_instrument_rankings',
        epoch,
        sequence,
        kind: 'exact',
        unitsKind: 'instruments',
        unitsCompleted: percent,
        unitsTotal: 100,
        unitsTotalFinal: true,
        percent,
        startedAt: '2026-01-02T10:00:00.000Z',
        lastProgressAt: `2026-01-02T10:05:0${sequence}.000Z`,
      },
    },
  };
}

test.use({ scenario: createPopulatedScenario() });
test.beforeEach(({}, testInfo) => {
  test.skip(testInfo.project.name !== WIDE_PROJECT, 'Settings viewport matrix runs once');
});

for (const width of [320, 375, 768, 1440]) {
  test(`idle Settings service card fits without horizontal overflow at ${width}px`, async ({
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
    await expect(serviceInfo.getByText('Leaderboard Service State')).toBeVisible();
    await expect(page.getByTestId('settings-service-info-row-update-sub-status')).toContainText(
      'Waiting for the Next Update',
    );
    await expect(page.getByTestId('settings-service-info-row-update-status')).toContainText('Idle');
    await expect(page.getByTestId('settings-service-info-row-update-step-position')).toHaveCount(0);
    const publication = page.getByTestId('settings-service-info-row-last-published-at');
    await expect(publication).toContainText('Last Successful Publication');
    await expect(publication).toContainText(/\b(?:PST|PDT|UTC|GMT(?:[+-]\d+)?)\b/);
    await expect(page.getByTestId('settings-service-technical-details')).toHaveCount(0);
    await expect(page.getByTestId('settings-selected-profile-sync')).toHaveCount(0);

    const geometry = await page.locator('#main-content').evaluate(element => ({
      clientWidth: element.clientWidth,
      scrollWidth: element.scrollWidth,
    }));
    expect(geometry.scrollWidth).toBeLessThanOrEqual(geometry.clientWidth + 1);

    const card = serviceInfo.locator(':scope > div');
    await expect(card).toHaveCount(1);
    const cardGeometry = await card.evaluate(element => ({
      clientWidth: element.clientWidth,
      scrollWidth: element.scrollWidth,
    }));
    expect(cardGeometry.scrollWidth).toBeLessThanOrEqual(cardGeometry.clientWidth + 1);
    await expect(card).not.toContainText('N/A');
    await expect(page.getByTestId('settings-service-health')).toHaveCount(0);
    await expect(page.getByTestId('settings-service-progress')).toHaveCount(0);
    await expect(page.getByTestId('settings-service-publication')).toHaveCount(0);

    await testInfo.attach(`settings-idle-${width}`, {
      body: await serviceInfo.screenshot(),
      contentType: 'image/png',
    });
  });
}

for (const width of [375, 1440]) {
  test(`v2 determinate progress uses state and phase rows at ${width}px`, async ({
    page,
    appState,
    api,
    scenario,
  }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
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
    await expect(page.getByText('37.5%', { exact: true })).toHaveCount(0);
    await expect(page.getByText('3 of 8 instruments completed', { exact: true })).toHaveCount(0);
    await expect(page.getByTestId('settings-service-info-row-update-status')).toContainText('Updating');
    await expect(page.getByTestId('settings-service-info-row-update-status').getByTestId('arc-spinner')).toBeVisible();
    await expect(page.getByTestId('settings-service-info-row-update-sub-status')).toContainText(
      'Computing Rankings',
    );
    await expect(page.getByTestId('settings-service-info-row-update-step-position')).toContainText(
      'Computing Rankings · Calculating Instrument Rankings',
    );
    await expect(page.getByTestId('settings-service-info-row-update-overall-progress')).toHaveCount(0);
    await expect(page.getByTestId('settings-service-info-row-update-eta')).toHaveCount(0);
    await expect(page.getByTestId('settings-service-technical-details')).toHaveCount(0);
    await expect(page.getByTestId('settings-selected-profile-sync')).toHaveCount(0);
    await expect(page.getByTestId('settings-quick-link-profile-sync')).toHaveCount(0);

    const rowOrderIsCorrect = await page.getByTestId('settings-service-info-list').evaluate(service => {
      const state = service.querySelector('[data-testid="settings-service-info-row-update-sub-status"]');
      const phase = service.querySelector('[data-testid="settings-service-info-row-update-step-position"]');
      const bar = service.querySelector('[data-testid="settings-service-phase-progress"]');
      return Boolean(
        state
        && phase
        && (state.compareDocumentPosition(phase) & Node.DOCUMENT_POSITION_FOLLOWING)
        && bar
        && (phase.compareDocumentPosition(bar) & Node.DOCUMENT_POSITION_FOLLOWING),
      );
    });
    expect(rowOrderIsCorrect).toBe(true);

    await page.addScriptTag({ content: axe.source });
    const results = await page.evaluate(() => window.axe.run('[data-testid="settings-service-info-list"]'));
    expect(results.violations.filter(
      violation => violation.impact === 'moderate'
        || violation.impact === 'serious'
        || violation.impact === 'critical',
    )).toEqual([]);

    await testInfo.attach(`settings-running-determinate-${width}`, {
      body: await page.getByTestId('settings-service-info-list').screenshot(),
      contentType: 'image/png',
    });
  });
}

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
  await expect(page.getByText('3 instruments completed; 8 discovered so far')).toHaveCount(0);
  await expect(page.getByText('37.5%')).toHaveCount(0);
  await expect(page.getByText(/Estimated overall progress/)).toHaveCount(0);

  await testInfo.attach('settings-running-indeterminate', {
    body: await page.getByTestId('settings-service-info-list').screenshot(),
    contentType: 'image/png',
  });
});

test('not-applicable substate renders no progress bar', async ({
  page,
  appState,
  api,
  scenario,
}) => {
  await page.setViewportSize({ width: 375, height: 900 });
  api.use({ ...scenario, serviceInfo: notApplicableServiceInfo() });
  await appState.reset();
  await gotoAppRoute(page, '/settings');

  await expect(page.getByTestId('settings-service-info-row-update-step-position')).toContainText(
    'Continuing Without Band Leaderboards',
  );
  await expect(page.getByRole('progressbar')).toHaveCount(0);
});

test('a new subphase epoch resets the bar from 90% to 5%', async ({
  page,
  appState,
  api,
  scenario,
}) => {
  await page.setViewportSize({ width: 375, height: 900 });
  api.use({ ...scenario, serviceInfo: exactProgressServiceInfo(90, 1, 5) });
  await appState.reset();
  await gotoAppRoute(page, '/settings');

  const progress = page.getByRole('progressbar', { name: 'Current phase progress' });
  await expect(progress).toHaveAttribute('aria-valuenow', '90');

  api.use({ ...scenario, serviceInfo: exactProgressServiceInfo(5, 2, 1) });
  await expect(progress).toHaveAttribute('aria-valuenow', '5', { timeout: 8_000 });
  await expect(page.getByText('90.0%', { exact: true })).toHaveCount(0);
  await expect(page.getByText('5.0%', { exact: true })).toHaveCount(0);
});

test('failed update shows stopped process and failed service state', async ({
  page,
  appState,
  api,
  scenario,
}, testInfo) => {
  await page.setViewportSize({ width: 375, height: 900 });
  api.use({ ...scenario, serviceInfo: failedServiceInfo() });
  await appState.reset();
  await gotoAppRoute(page, '/settings');

  await expect(page.getByTestId('settings-service-info-row-update-status')).toContainText('Stopped');
  await expect(page.getByTestId('settings-service-info-row-update-sub-status')).toContainText(
    'Last Leaderboard Update Failed',
  );
  await expect(page.getByText(/non-critical warnings?/i)).toHaveCount(0);
  await expect(page.getByRole('progressbar')).toHaveCount(0);

  await testInfo.attach('settings-failed', {
    body: await page.getByTestId('settings-service-info-list').screenshot(),
    contentType: 'image/png',
  });
});
