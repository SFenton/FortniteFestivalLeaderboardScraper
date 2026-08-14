import { writeFileSync } from 'node:fs';
import type { Page } from '@playwright/test';
import { expect, test } from '../../fixtures/test';
import { createPopulatedScenario, E2E_PLAYER } from '../../fixtures/scenarios';
import { dismissObstructions, gotoAppRoute } from '../../support/drivers/app';
import { isPrimaryDesktopProject } from '../../support/projects';

const DEFAULT_TRIGGER_TARGET = 100;
const BENCHMARK_TIMEOUT_MS = 120_000;
const RESTORE_TOLERANCE_PX = 4;
const SUGGESTION_TYPE_IDS = [
  'NearFC',
  'StarProgress',
  'Unplayed',
  'VarietyPack',
  'ArtistEssentials',
  'ArtistDiscover',
  'SameName',
  'AlmostElite',
  'PercentilePush',
  'Stale',
  'PctImprove',
  'NearMaxScore',
  'SongRivals',
  'LeaderboardRivals',
] as const;

type SuggestionsMetrics = {
  triggerTarget: number;
  loadTriggerCount: number;
  generatedCategoryCount: number;
  visibleCategoryCount: number;
  renderedCategoryCount: number;
  totalDomNodes: number;
  frostedMarkerCount: number;
  scrollHeight: number;
  initialHeapBytes: number;
  finalHeapBytes: number;
  heapGrowthBytes: number;
  longTaskCount: number;
  maxLongTaskMs: number;
  geometryReadsOnMouseMove: number;
  restoredScrollTop: number;
  expectedScrollTop: number;
};

test.use({ scenario: createPopulatedScenario() });
test.setTimeout(BENCHMARK_TIMEOUT_MS);

test.beforeEach(async ({ page, appState }) => {
  await appState.reset();
  await appState.selectPlayer();
  await appState.setSettings({
    disableLightTrails: false,
    showLead: true,
    showBass: true,
    showDrums: true,
    showVocals: true,
    showProLead: true,
    showProBass: true,
  });
  await page.clock.setFixedTime(new Date('2026-08-13T00:00:00Z'));
  await installPerformanceInstrumentation(page);
});

test('Suggestions sentinel grows the list in desktop and mobile scroll containers', async ({ page }) => {
  await gotoAppRoute(page, '/suggestions');
  await settleSuggestionsRoute(page);

  const scrollContainer = page.getByTestId('app-scroll-container');
  const list = page.getByTestId('suggestions-list');
  const sentinel = page.getByTestId('suggestions-load-sentinel');
  await expect(list).toBeVisible({ timeout: 15_000 });
  await expect(sentinel).toHaveAttribute('data-observer-ready', 'true');
  const initialGeneratedCount = Number(await list.getAttribute('data-generated-category-count') ?? 0);

  await releasePendingScrollRestoration(page);
  await scrollContainer.evaluate((element) => {
    element.scrollTo(0, element.scrollHeight);
  });
  await expect.poll(async () => (
    Number(await list.getAttribute('data-generated-category-count') ?? 0)
  )).toBeGreaterThan(initialGeneratedCount);
  await expect(sentinel).toHaveCount(1);

  const scrollState = await scrollContainer.evaluate((element) => {
    const nextTop = Math.min(element.scrollHeight - element.clientHeight, element.scrollTop + 500);
    element.scrollTop = nextTop;
    return new Promise<{ expected: number; actual: number }>(resolve => {
      window.setTimeout(() => resolve({ expected: nextTop, actual: element.scrollTop }), 350);
    });
  });
  expect(scrollState.actual).toBe(scrollState.expected);
});

test('filtered empty state keeps loading until a later matching category appears', async ({ page }) => {
  const filter: Record<string, boolean> = {};
  for (const id of SUGGESTION_TYPE_IDS) {
    filter[`suggestionsShow${id}`] = id === 'NearMaxScore';
  }
  await page.localStorage.setItem('fst-suggestions-filter', JSON.stringify({
    version: 1,
    data: filter,
  }));

  await gotoAppRoute(page, '/suggestions');
  await settleSuggestionsRoute(page);
  await expect(page.getByTestId('suggestions-load-sentinel')).toHaveCount(1);
  await expect(page.getByText(/(?:Almost Perfect|Close to Max|Approaching Max)/).first())
    .toBeVisible({ timeout: 10_000 });
});

test('Suggestions scroll cache is profile-scoped and preserves an intentional top position', async ({ page }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'profile-scoped restoration is covered once');
  await gotoAppRoute(page, '/suggestions');
  await settleSuggestionsRoute(page);

  const scrollContainer = page.getByTestId('app-scroll-container');
  await expect(page.getByTestId('suggestions-list')).toBeVisible();
  await releasePendingScrollRestoration(page);
  await scrollContainer.evaluate((element) => {
    element.scrollTop = Math.min(1_000, element.scrollHeight - element.clientHeight);
    element.dispatchEvent(new Event('scroll', { bubbles: true }));
    element.scrollTop = 0;
    element.dispatchEvent(new Event('scroll', { bubbles: true }));
  });
  await navigateByHash(page, '/settings');
  await expect(page.getByText('App Settings', { exact: true })).toBeVisible();
  await scrollContainer.evaluate((element) => {
    element.scrollTop = Math.min(600, element.scrollHeight - element.clientHeight);
  });
  await expect.poll(() => scrollContainer.evaluate(element => element.scrollTop))
    .toBeGreaterThan(0);
  await page.goBack();
  await expect(page).toHaveURL(/#\/suggestions$/);
  await expect.poll(() => scrollContainer.evaluate(element => element.scrollTop)).toBe(0);
  await expect.poll(() => scrollContainer.evaluate(element => (
    element.scrollHeight - element.clientHeight
  ))).toBeGreaterThan(0);
  await scrollContainer.evaluate((element) => {
    window.dispatchEvent(new WheelEvent('wheel', { bubbles: true, deltaY: 400 }));
    element.scrollTop = Math.min(400, element.scrollHeight - element.clientHeight);
    element.dispatchEvent(new Event('scroll', { bubbles: true }));
  });
  await expect.poll(() => scrollContainer.evaluate(element => element.scrollTop))
    .toBeGreaterThan(0);

  await scrollContainer.evaluate((element) => {
    element.scrollTop = Math.min(1_000, element.scrollHeight - element.clientHeight);
    element.dispatchEvent(new Event('scroll', { bubbles: true }));
  });
  await navigateByHash(page, '/settings');
  await page.evaluate(() => {
    const profile = { accountId: 'suggestions-profile-b', displayName: 'Suggestions Profile B' };
    localStorage.setItem('fst:selectedProfile', JSON.stringify({ type: 'player', ...profile }));
    localStorage.setItem('fst:trackedPlayer', JSON.stringify(profile));
    window.dispatchEvent(new Event('fst:selectedProfileChanged'));
    window.dispatchEvent(new Event('fst:trackedPlayerChanged'));
  });
  await navigateByHash(page, '/suggestions');
  await expect(page.getByTestId('suggestions-list')).toBeVisible();
  await expect.poll(() => scrollContainer.evaluate(element => element.scrollTop)).toBe(0);

  await navigateByHash(page, '/settings');
  await page.evaluate(profile => {
    localStorage.setItem('fst:selectedProfile', JSON.stringify({ type: 'player', ...profile }));
    localStorage.setItem('fst:trackedPlayer', JSON.stringify(profile));
    window.dispatchEvent(new Event('fst:selectedProfileChanged'));
    window.dispatchEvent(new Event('fst:trackedPlayerChanged'));
  }, E2E_PLAYER);
  await navigateByHash(page, '/suggestions');
  await expect(page.getByTestId('suggestions-list')).toBeVisible();
  await expect.poll(() => scrollContainer.evaluate(element => element.scrollTop)).toBe(0);
});

test('Suggestions preserves scroll when the shell crosses the wide breakpoint', async ({ page }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'wide-shell restoration is covered once');
  await gotoAppRoute(page, '/suggestions');
  await settleSuggestionsRoute(page);

  const scrollContainer = page.getByTestId('app-scroll-container');
  await expect(page.getByTestId('suggestions-list')).toBeVisible();
  await releasePendingScrollRestoration(page);
  const expectedScrollTop = await scrollContainer.evaluate((element) => {
    const nextTop = Math.min(1_000, element.scrollHeight - element.clientHeight);
    element.scrollTop = nextTop;
    element.dispatchEvent(new Event('scroll', { bubbles: true }));
    return element.scrollTop;
  });
  expect(expectedScrollTop).toBeGreaterThan(0);

  await page.setViewportSize({ width: 1_600, height: 900 });
  await expect.poll(() => scrollContainer.evaluate(element => element.scrollTop))
    .toBe(expectedScrollTop);
  await page.setViewportSize({ width: 1_280, height: 800 });
  await expect.poll(() => scrollContainer.evaluate(element => element.scrollTop))
    .toBe(expectedScrollTop);
});

test('Suggestions restores after visiting one of its song links', async ({ page }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'song-detail return restoration is covered once');
  await gotoAppRoute(page, '/suggestions');
  await settleSuggestionsRoute(page);
  await waitForAnimationFrames(page);

  const scrollContainer = page.getByTestId('app-scroll-container');
  await expect(page.getByTestId('suggestions-list')).toBeVisible({ timeout: 15_000 });
  const targetSong = page.getByTestId('suggestions-list').getByRole('link').nth(6);
  await targetSong.scrollIntoViewIfNeeded();
  await expect(targetSong).toBeVisible({ timeout: 15_000 });
  await releasePendingScrollRestoration(page);
  await waitForAnimationFrames(page);
  const suggestionsScrollTop = await scrollContainer.evaluate(
    element => element.scrollTop,
  );
  expect(suggestionsScrollTop).toBeGreaterThan(0);

  await targetSong.click();
  await expect(page).toHaveURL(/#\/songs\/[^?]+(?:\?.*)?$/);
  await expect(page.getByText('Score Player').first()).toBeVisible({ timeout: 15_000 });
  await scrollContainer.evaluate((element) => {
    element.scrollTop = Math.min(200, element.scrollHeight - element.clientHeight);
    element.dispatchEvent(new Event('scroll', { bubbles: true }));
  });
  await page.goBack();
  await expect(page).toHaveURL(/#\/suggestions$/);
  await expect(page.getByTestId('suggestions-list')).toBeVisible({ timeout: 15_000 });
  await expect.poll(() => scrollContainer.evaluate(element => element.scrollTop))
    .toBe(suggestionsScrollTop);
});

test('100-trigger Suggestions benchmark records growth and restores scroll', {
  tag: '@suggestions-benchmark',
}, async ({ page }, testInfo) => {
  test.skip(process.env.SUGGESTIONS_BENCHMARK !== '1', 'Suggestions benchmark runs in its isolated CI pass');
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'Suggestions benchmark is owned by primary desktop');
  const triggerTarget = readTriggerTarget();
  const rivalsReady = page.waitForResponse(response => (
    /\/api\/player\/[^/]+\/rivals\/all$/.test(new URL(response.url()).pathname)
  ));

  await gotoAppRoute(page, '/suggestions');
  await rivalsReady;
  await settleSuggestionsRoute(page);
  await waitForAnimationFrames(page);

  const scrollContainer = page.getByTestId('app-scroll-container');
  const list = page.getByTestId('suggestions-list');
  const sentinel = page.getByTestId('suggestions-load-sentinel');
  await expect(list).toBeVisible();
  await expect(page.getByTestId('suggestion-category-card').first()).toBeVisible();
  await expect(sentinel).toHaveAttribute('data-observer-ready', 'true');
  const initialHeapBytes = await readHeapBytes(page);
  await releasePendingScrollRestoration(page);

  for (let iteration = 0; iteration < triggerTarget + 10; iteration += 1) {
    const currentCount = await readLoadTriggerCount(list);
    if (currentCount >= triggerTarget) break;

    await expect(sentinel).toHaveAttribute('data-observer-ready', 'true');
    await scrollContainer.evaluate((element) => {
      element.scrollTo(0, element.scrollHeight);
    });
    await expect.poll(
      () => readLoadTriggerCount(list),
      { timeout: 5_000, message: `Suggestions load trigger ${currentCount + 1} did not commit` },
    ).toBeGreaterThan(currentCount);
    await waitForAnimationFrames(page);
  }

  await expect.poll(() => readLoadTriggerCount(list), { timeout: 10_000 })
    .toBeGreaterThanOrEqual(triggerTarget);

  const expectedScrollTop = await scrollContainer.evaluate((element) => {
    const nextTop = Math.floor(element.scrollHeight * 0.6);
    element.scrollTop = nextTop;
    element.dispatchEvent(new Event('scroll', { bubbles: true }));
    return element.scrollTop;
  });
  await page.evaluate(() => {
    window.location.hash = '#/settings';
  });
  await expect(page).toHaveURL(/#\/settings$/);
  await expect(page.getByText('App Settings', { exact: true })).toBeVisible();
  await page.goBack();
  await expect(page).toHaveURL(/#\/suggestions$/);
  await expect(page.getByTestId('suggestions-list')).toBeVisible();
  await expect.poll(async () => (
    Math.abs((await scrollContainer.evaluate(element => element.scrollTop)) - expectedScrollTop)
  ), { timeout: 5_000 }).toBeLessThanOrEqual(RESTORE_TOLERANCE_PX).catch(() => {});
  const restoredScrollTop = await scrollContainer.evaluate(element => element.scrollTop);
  await expect(page.locator('[data-modal-root]')).toHaveCount(0);
  await waitForStableDom(page);

  const scrollBox = await scrollContainer.boundingBox();
  if (!scrollBox) throw new Error('Suggestions scroll container has no bounding box');
  await page.mouse.move(scrollBox.x + 2, scrollBox.y + 2);
  await waitForAnimationFrames(page);
  await page.evaluate(() => {
    (window as Window & { __suggestionsGeometryReads?: number }).__suggestionsGeometryReads = 0;
  });
  await page.mouse.move(
    scrollBox.x + scrollBox.width - 2,
    scrollBox.y + scrollBox.height / 2,
  );
  await waitForAnimationFrames(page);

  const finalHeapBytes = await readHeapBytes(page);
  const metrics = await readMetrics(
    page,
    triggerTarget,
    initialHeapBytes,
    finalHeapBytes,
    expectedScrollTop,
    restoredScrollTop,
  );
  await testInfo.attach('suggestions-long-scroll-metrics', {
    body: JSON.stringify(metrics, null, 2),
    contentType: 'application/json',
  });
  if (process.env.SUGGESTIONS_METRICS_PATH) {
    writeFileSync(process.env.SUGGESTIONS_METRICS_PATH, `${JSON.stringify(metrics, null, 2)}\n`);
  }

  expect(metrics.loadTriggerCount).toBeGreaterThanOrEqual(triggerTarget);
  expect(metrics.generatedCategoryCount).toBeGreaterThan(0);
  expect(metrics.visibleCategoryCount).toBeLessThanOrEqual(metrics.generatedCategoryCount);
  expect(metrics.renderedCategoryCount).toBeLessThanOrEqual(metrics.visibleCategoryCount);
  expect(Math.abs(metrics.restoredScrollTop - metrics.expectedScrollTop)).toBeLessThanOrEqual(RESTORE_TOLERANCE_PX);
});

async function waitForAnimationFrames(page: Page): Promise<void> {
  await page.evaluate(() => new Promise<void>(resolve => {
    requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
  }));
}

async function settleSuggestionsRoute(page: Page): Promise<void> {
  await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  await dismissObstructions(page);
  await expect(page.locator('[data-modal-root]')).toHaveCount(0);
}

async function releasePendingScrollRestoration(page: Page): Promise<void> {
  await page.evaluate(() => {
    window.dispatchEvent(new WheelEvent('wheel', { bubbles: true, deltaY: 1 }));
  });
}

async function waitForStableDom(page: Page): Promise<void> {
  let previousCount = -1;
  let stableReads = 0;
  for (let attempt = 0; attempt < 20 && stableReads < 3; attempt += 1) {
    await waitForAnimationFrames(page);
    const nextCount = await page.locator('*').count();
    stableReads = nextCount === previousCount ? stableReads + 1 : 0;
    previousCount = nextCount;
  }
}

async function navigateByHash(page: Page, path: string): Promise<void> {
  await page.evaluate(nextPath => {
    window.location.hash = `#${nextPath}`;
  }, path);
  await expect(page).toHaveURL(new RegExp(`#${path.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`));
}

function readTriggerTarget(): number {
  const parsed = Number(process.env.SUGGESTIONS_TRIGGER_TARGET ?? DEFAULT_TRIGGER_TARGET);
  return Number.isFinite(parsed) && parsed >= DEFAULT_TRIGGER_TARGET
    ? Math.floor(parsed)
    : DEFAULT_TRIGGER_TARGET;
}

async function readLoadTriggerCount(list: ReturnType<Page['getByTestId']>): Promise<number> {
  return Number(await list.getAttribute('data-load-trigger-count') ?? 0);
}

async function readHeapBytes(page: Page): Promise<number> {
  const session = await page.context().newCDPSession(page);
  await session.send('HeapProfiler.collectGarbage');
  await session.send('Performance.enable');
  const result = await session.send('Performance.getMetrics');
  await session.detach();
  const heapMetric = result.metrics.find(metric => metric.name === 'JSHeapUsedSize');
  if (!heapMetric) throw new Error('Chromium did not expose JSHeapUsedSize');
  return heapMetric.value;
}

async function readMetrics(
  page: Page,
  triggerTarget: number,
  initialHeapBytes: number,
  finalHeapBytes: number,
  expectedScrollTop: number,
  restoredScrollTop: number,
): Promise<SuggestionsMetrics> {
  return page.evaluate(({ target, initialHeap, finalHeap, expectedTop, restoredTop }) => {
    const list = document.querySelector<HTMLElement>('[data-testid="suggestions-list"]');
    if (!list) throw new Error('Suggestions list metrics target was not found');
    const longTasks = (
      window as Window & { __suggestionsLongTasks?: Array<{ duration: number }> }
    ).__suggestionsLongTasks ?? [];
    return {
      triggerTarget: target,
      loadTriggerCount: Number(list.dataset.loadTriggerCount ?? 0),
      generatedCategoryCount: Number(list.dataset.generatedCategoryCount ?? 0),
      visibleCategoryCount: Number(list.dataset.visibleCategoryCount ?? 0),
      renderedCategoryCount: list.querySelectorAll('[data-testid="suggestion-category-card"]').length,
      totalDomNodes: document.querySelectorAll('*').length,
      frostedMarkerCount: document.querySelectorAll('[style*="--frosted-card"]').length,
      scrollHeight: document.querySelector<HTMLElement>('[data-testid="app-scroll-container"]')?.scrollHeight ?? 0,
      initialHeapBytes: initialHeap,
      finalHeapBytes: finalHeap,
      heapGrowthBytes: finalHeap - initialHeap,
      longTaskCount: longTasks.length,
      maxLongTaskMs: longTasks.reduce((maximum, entry) => Math.max(maximum, entry.duration), 0),
      geometryReadsOnMouseMove: (
        window as Window & { __suggestionsGeometryReads?: number }
      ).__suggestionsGeometryReads ?? 0,
      restoredScrollTop: restoredTop,
      expectedScrollTop: expectedTop,
    };
  }, {
    target: triggerTarget,
    initialHeap: initialHeapBytes,
    finalHeap: finalHeapBytes,
    expectedTop: expectedScrollTop,
    restoredTop: restoredScrollTop,
  });
}

async function installPerformanceInstrumentation(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const state = window as Window & {
      __suggestionsGeometryReads?: number;
      __suggestionsLongTasks?: Array<{ duration: number }>;
    };
    state.__suggestionsGeometryReads = 0;
    state.__suggestionsLongTasks = [];

    const original = Element.prototype.getBoundingClientRect;
    Element.prototype.getBoundingClientRect = function instrumentedBoundingRect() {
      if (this instanceof HTMLElement && this.style.getPropertyValue('--frosted-card')) {
        state.__suggestionsGeometryReads = (state.__suggestionsGeometryReads ?? 0) + 1;
      }
      return original.call(this);
    };

    if ('PerformanceObserver' in window) {
      try {
        const observer = new PerformanceObserver((entries) => {
          for (const entry of entries.getEntries()) {
            if (entry.duration > 50) state.__suggestionsLongTasks?.push({ duration: entry.duration });
          }
        });
        observer.observe({ type: 'longtask', buffered: true });
      } catch {
        // Long-task entries are unavailable in some browser configurations.
      }
    }
  });
}
