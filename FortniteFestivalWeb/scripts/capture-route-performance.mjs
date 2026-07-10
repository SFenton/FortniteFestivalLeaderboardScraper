#!/usr/bin/env node
import { chromium } from '@playwright/test';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const args = parseArgs(process.argv.slice(2));
const baseUrl = (args['base-url'] ?? 'http://127.0.0.1:3001').replace(/\/$/, '');
const outputPath = path.resolve(webRoot, args.out ?? 'performance-artifacts/routes.json');
const accountId = args['account-id'] ?? '195e93ef108143b2975ee46662d4d0e1';
const displayName = args['display-name'] ?? 'SFenton';
const songId = args['song-id'] ?? '1fcee9b4-dc49-41b1-8e7d-add244f556a2';
const routeBudgets = JSON.parse(readFileSync(path.join(webRoot, 'performance-budgets.json'), 'utf8')).routes;
const routes = [
  ['Songs', '/songs'],
  ['Song Details', `/songs/${songId}`],
  ['Leaderboards', '/leaderboards'],
  ['Rivals', '/rivals'],
  ['Suggestions', '/suggestions'],
  ['Settings', '/settings'],
  ['Manual', '/manual'],
];
const viewports = [
  ['desktop', { width: 1440, height: 900 }],
  ['mobile', { width: 375, height: 812 }],
];

const browser = await chromium.launch({ headless: true });
const results = [];
try {
  for (const [viewportName, viewport] of viewports) {
    for (const [routeName, route] of routes) {
      results.push(await captureRoute({ viewportName, viewport, routeName, route }));
    }
  }
} finally {
  await browser.close();
}

const failures = results.flatMap((result) => compareBudget(result, routeBudgets[result.routeName]));
const report = {
  capturedAtUtc: new Date().toISOString(),
  baseUrl,
  accountId,
  songId,
  results,
  failures,
};
mkdirSync(path.dirname(outputPath), { recursive: true });
writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
console.log(JSON.stringify({ captures: results.length, failures, outputPath }, null, 2));
if (failures.length) process.exitCode = 1;

async function captureRoute({ viewportName, viewport, routeName, route }) {
  const context = await browser.newContext({ viewport, hasTouch: viewportName === 'mobile' });
  await context.addInitScript(({ accountId: selectedAccountId, displayName: selectedDisplayName }) => {
    const profile = { type: 'player', accountId: selectedAccountId, displayName: selectedDisplayName };
    localStorage.setItem('fst:selectedProfile', JSON.stringify(profile));
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: selectedAccountId, displayName: selectedDisplayName }));
    window.__fstLongTasks = [];
    if ('PerformanceObserver' in window) {
      try {
        const observer = new PerformanceObserver((list) => {
          window.__fstLongTasks.push(...list.getEntries().map((entry) => ({ startTime: entry.startTime, duration: entry.duration })));
        });
        observer.observe({ type: 'longtask', buffered: true });
      } catch {
        // Long-task observation is optional in older browsers.
      }
    }
  }, { accountId, displayName });

  const page = await context.newPage();
  const consoleErrors = [];
  const serverErrors = [];
  await page.route('**/api/features', async (route) => {
    const response = await route.fetch();
    const features = await response.json();
    await route.fulfill({
      response,
      json: {
        ...features,
        appManual: true,
        leaderboards: true,
        playerBands: true,
        experimentalRanks: true,
      },
    });
  });
  page.on('console', (message) => {
    if (message.type() === 'error') consoleErrors.push(message.text());
  });
  page.on('response', (response) => {
    if (response.status() >= 500) serverErrors.push({ status: response.status(), url: response.url() });
  });

  const cacheBust = `${Date.now()}-${viewportName}-${routeName.replace(/\s+/g, '-').toLowerCase()}`;
  await page.goto(`${baseUrl}/?perf=${cacheBust}#${route}`, { waitUntil: 'load', timeout: 45_000 });
  await page.waitForTimeout(2_000);
  await dismissOverlays(page);
  await waitForResourceSettle(page);

  const metrics = await page.evaluate(() => {
    const viewportWidth = window.innerWidth;
    const viewportHeight = window.innerHeight;
    const images = Array.from(document.images);
    const loadedImages = images.filter((image) => image.complete && image.naturalWidth > 0);
    const hiddenLoadedImages = loadedImages.filter((image) => {
      const rect = image.getBoundingClientRect();
      return rect.bottom <= 0 || rect.right <= 0 || rect.top >= viewportHeight || rect.left >= viewportWidth;
    });
    const resources = performance.getEntriesByType('resource');
    const navigation = performance.getEntriesByType('navigation')[0];
    const memory = performance.memory;
    return {
      finalUrl: location.href,
      domElements: document.querySelectorAll('*').length,
      imageElements: images.length,
      loadedImages: loadedImages.length,
      hiddenLoadedImages: hiddenLoadedImages.length,
      requestCount: resources.length,
      transferBytes: resources.reduce((sum, entry) => sum + (entry.transferSize || 0), 0),
      encodedBodyBytes: resources.reduce((sum, entry) => sum + (entry.encodedBodySize || 0), 0),
      decodedBodyBytes: resources.reduce((sum, entry) => sum + (entry.decodedBodySize || 0), 0),
      usedJsHeapBytes: memory?.usedJSHeapSize ?? null,
      longTaskCount: window.__fstLongTasks?.length ?? 0,
      longTaskDurationMs: window.__fstLongTasks?.reduce((sum, entry) => sum + entry.duration, 0) ?? 0,
      domContentLoadedMs: navigation?.domContentLoadedEventEnd ?? null,
      loadEventMs: navigation?.loadEventEnd ?? null,
    };
  });
  await context.close();

  return {
    routeName,
    route,
    viewport: viewportName,
    ...metrics,
    consoleErrorCount: consoleErrors.length,
    consoleErrors,
    serverErrorCount: serverErrors.length,
    serverErrors,
  };
}

async function dismissOverlays(page) {
  for (let attempt = 0; attempt < 8; attempt += 1) {
    const dialog = page.getByRole('dialog').last();
    if (!(await dialog.isVisible().catch(() => false))) return;
    const button = dialog.getByRole('button', { name: /close|skip|got it|continue|done|later/i }).last();
    if (!(await button.isVisible().catch(() => false))) return;
    await button.click();
    await page.waitForTimeout(150);
  }
}

async function waitForResourceSettle(page) {
  let previousCount = -1;
  let stableSamples = 0;
  for (let attempt = 0; attempt < 20 && stableSamples < 4; attempt += 1) {
    await page.waitForTimeout(250);
    const count = await page.evaluate(() => performance.getEntriesByType('resource').length);
    if (count === previousCount) stableSamples += 1;
    else stableSamples = 0;
    previousCount = count;
  }
}

function compareBudget(result, budget) {
  if (!budget) return [{ routeName: result.routeName, viewport: result.viewport, metric: 'budget', actual: null, limit: null }];
  const pairs = [
    ['domElements', 'domElementsMax'],
    ['loadedImages', 'loadedImagesMax'],
    ['hiddenLoadedImages', 'hiddenLoadedImagesMax'],
    ['requestCount', 'requestCountMax'],
    ['transferBytes', 'transferBytesMax'],
    ['usedJsHeapBytes', 'usedJsHeapBytesMax'],
    ['longTaskCount', 'longTaskCountMax'],
    ['consoleErrorCount', 'consoleErrorCountMax'],
    ['serverErrorCount', 'serverErrorCountMax'],
  ];
  return pairs.flatMap(([metric, limitName]) => {
    const actual = result[metric];
    const limit = budget[limitName];
    return typeof actual === 'number' && typeof limit === 'number' && actual > limit
      ? [{ routeName: result.routeName, viewport: result.viewport, metric, actual, limit }]
      : [];
  });
}

function parseArgs(argv) {
  const values = {};
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith('--')) continue;
    const value = argv[index + 1];
    if (!value || value.startsWith('--')) throw new Error(`Missing value for ${token}`);
    values[token.slice(2)] = value;
    index += 1;
  }
  return values;
}
