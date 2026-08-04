#!/usr/bin/env node
/* global PerformanceObserver, console, document, localStorage, performance, process, window */
import { chromium } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const args = parseArgs(process.argv.slice(2));
const baseUrl = (args['base-url'] ?? 'http://127.0.0.1:3001').replace(/\/$/, '');
const outputPath = path.resolve(webRoot, args.out ?? 'performance-artifacts/manual.json');
const outputDir = path.dirname(outputPath);
const label = args.label ?? 'manual';
const iterations = Number(args.iterations ?? 1);
const network = args.network ?? 'none';
const chromiumExecutablePath = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH;
const viewportDefinitions = [
  ['desktop', { width: 1440, height: 900 }, false],
  ['mobile', { width: 375, height: 812 }, true],
];

if (!Number.isInteger(iterations) || iterations < 1 || iterations > 20) {
  throw new Error(`--iterations must be an integer from 1 to 20; received ${args.iterations}.`);
}
if (!['none', 'slow4g'].includes(network)) {
  throw new Error(`--network must be "none" or "slow4g"; received ${network}.`);
}

mkdirSync(outputDir, { recursive: true });
const browser = await chromium.launch({
  headless: true,
  ...(chromiumExecutablePath
    ? { executablePath: chromiumExecutablePath, args: ['--no-sandbox'] }
    : {}),
});

const captures = [];
try {
  for (let iteration = 1; iteration <= iterations; iteration += 1) {
    for (const [viewportName, viewport, hasTouch] of viewportDefinitions) {
      captures.push(await capture({ iteration, viewportName, viewport, hasTouch }));
    }
  }
} finally {
  await browser.close();
}

const report = {
  capturedAtUtc: new Date().toISOString(),
  label,
  baseUrl,
  network,
  iterations,
  captures,
  summary: summarize(captures),
};
writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
console.log(JSON.stringify({ outputPath, summary: report.summary }, null, 2));
if (captures.some(capture => capture.consoleErrors.length > 0 || capture.pageErrors.length > 0)) {
  process.exitCode = 1;
}

async function capture({ iteration, viewportName, viewport, hasTouch }) {
  const context = await browser.newContext({ viewport, hasTouch, serviceWorkers: 'block' });
  await context.addInitScript(() => {
    localStorage.clear();
    localStorage.setItem('fst:changelog', JSON.stringify({ version: 'web33', hash: 'web33' }));
    localStorage.setItem('fst:firstRun', JSON.stringify({}));
    localStorage.setItem('fst:appSettings', JSON.stringify({ disableLightTrails: true }));
    window.__web33LongTasks = [];
    window.__web33LayoutShifts = [];
    try {
      new PerformanceObserver((list) => {
        window.__web33LongTasks.push(...list.getEntries().map(entry => ({
          startTime: entry.startTime,
          duration: entry.duration,
        })));
      }).observe({ type: 'longtask', buffered: true });
    } catch {
      // Long-task observation is optional.
    }
    try {
      new PerformanceObserver((list) => {
        window.__web33LayoutShifts.push(...list.getEntries()
          .filter(entry => !entry.hadRecentInput)
          .map(entry => ({ startTime: entry.startTime, value: entry.value })));
      }).observe({ type: 'layout-shift', buffered: true });
    } catch {
      // Layout-shift observation is optional.
    }
  });

  const page = await context.newPage();
  const consoleErrors = [];
  const pageErrors = [];
  page.on('console', (message) => {
    if (message.type() === 'error') consoleErrors.push(message.text());
  });
  page.on('pageerror', error => pageErrors.push(error.message));

  if (network === 'slow4g') {
    const session = await context.newCDPSession(page);
    await session.send('Network.enable');
    await session.send('Network.emulateNetworkConditions', {
      offline: false,
      latency: 150,
      downloadThroughput: 1_600_000 / 8,
      uploadThroughput: 750_000 / 8,
      connectionType: 'cellular4g',
    });
  }

  const cacheBust = `${Date.now()}-${label}-${viewportName}-${iteration}`;
  await page.goto(`${baseUrl}/?web33=${cacheBust}#/manual`, { waitUntil: 'load', timeout: 60_000 });
  await page.getByRole('heading', { name: 'Navigation Basics' }).waitFor({ state: 'visible' });
  const headingReadyMs = await page.evaluate(() => performance.now());

  const firstCarousel = page.getByTestId('manual-carousel-navigation-overview');
  const firstImage = firstCarousel.getByRole('img', { name: 'Navigation overview screenshot for Mobile' });
  await firstImage.evaluate(image => image.complete && image.naturalWidth > 0
    ? undefined
    : new Promise((resolve, reject) => {
        image.addEventListener('load', () => resolve(undefined), { once: true });
        image.addEventListener('error', () => reject(new Error('initial Manual image failed')), { once: true });
      }));
  const firstImageReadyMs = await page.evaluate(() => performance.now());
  await dismissOverlays(page);
  await waitForResourceSettle(page);
  const initial = await collectMetrics(page);
  await page.screenshot({
    path: path.join(outputDir, `${label}-${network}-${viewportName}-${iteration}-initial.png`),
    fullPage: false,
  });

  const interactionStartedAt = performance.now();
  await firstCarousel.getByRole('button', { name: 'Next screenshot' }).evaluate(button => button.click());
  const compactImage = firstCarousel.getByRole('img', { name: 'Navigation overview screenshot for Compact Web' });
  await compactImage.evaluate(image => image.complete && image.naturalWidth > 0
    ? undefined
    : new Promise((resolve, reject) => {
        image.addEventListener('load', () => resolve(undefined), { once: true });
        image.addEventListener('error', () => reject(new Error('carousel Manual image failed')), { once: true });
      }));
  const carouselInteractionReadyMs = performance.now() - interactionStartedAt;

  const scrollStartedAt = performance.now();
  const settingsSection = page.getByTestId('manual-section-settings');
  await settingsSection.scrollIntoViewIfNeeded();
  const settingsCarousel = page.getByTestId('manual-carousel-settings-overview');
  await settingsCarousel.waitFor({ state: 'visible' });
  await page.waitForFunction(() => (
    document.querySelector('[data-testid="manual-carousel-settings-overview"]')?.getAttribute('data-mounted') !== 'false'
  ));
  const settingsImage = settingsCarousel.locator('img').first();
  await settingsImage.evaluate(image => image.complete && image.naturalWidth > 0
    ? undefined
    : new Promise((resolve, reject) => {
        image.addEventListener('load', () => resolve(undefined), { once: true });
        image.addEventListener('error', () => reject(new Error('scrolled Manual image failed')), { once: true });
      }));
  const settingsScrollReadyMs = performance.now() - scrollStartedAt;
  await waitForResourceSettle(page);
  const afterScroll = await collectMetrics(page);

  await context.close();
  return {
    iteration,
    viewport: viewportName,
    headingReadyMs,
    firstImageReadyMs,
    carouselInteractionReadyMs,
    settingsScrollReadyMs,
    initial,
    afterScroll,
    consoleErrors,
    pageErrors,
  };
}

async function collectMetrics(page) {
  return page.evaluate(() => {
    const viewportWidth = window.innerWidth;
    const viewportHeight = window.innerHeight;
    const images = Array.from(document.images);
    const manualImages = images.filter(image => image.src.includes('/manual/screenshots/'));
    const loadedManualImages = manualImages.filter(image => image.complete && image.naturalWidth > 0);
    const hiddenLoadedManualImages = loadedManualImages.filter((image) => {
      const rect = image.getBoundingClientRect();
      return rect.bottom <= 0 || rect.right <= 0 || rect.top >= viewportHeight || rect.left >= viewportWidth;
    });
    const resources = performance.getEntriesByType('resource').map(entry => ({
      name: entry.name,
      transferSize: entry.transferSize || 0,
      encodedBodySize: entry.encodedBodySize || 0,
      decodedBodySize: entry.decodedBodySize || 0,
      duration: entry.duration,
    }));
    const manualResources = resources.filter(entry => entry.name.includes('/manual/screenshots/'));
    const memory = performance.memory;
    return {
      domElements: document.querySelectorAll('*').length,
      imageElements: images.length,
      manualImageElements: manualImages.length,
      loadedManualImages: loadedManualImages.length,
      hiddenLoadedManualImages: hiddenLoadedManualImages.length,
      manualImageRequests: manualResources.length,
      uniqueManualImageRequests: new Set(manualResources.map(entry => entry.name)).size,
      manualImageTransferBytes: manualResources.reduce((sum, entry) => sum + entry.transferSize, 0),
      manualImageEncodedBytes: manualResources.reduce((sum, entry) => sum + entry.encodedBodySize, 0),
      manualImageDecodedBodyBytes: manualResources.reduce((sum, entry) => sum + entry.decodedBodySize, 0),
      decodedImagePixelBytes: loadedManualImages.reduce(
        (sum, image) => sum + image.naturalWidth * image.naturalHeight * 4,
        0,
      ),
      totalRequests: resources.length,
      totalTransferBytes: resources.reduce((sum, entry) => sum + entry.transferSize, 0),
      usedJsHeapBytes: memory?.usedJSHeapSize ?? null,
      longTaskCount: window.__web33LongTasks?.length ?? 0,
      longTaskDurationMs: window.__web33LongTasks?.reduce((sum, entry) => sum + entry.duration, 0) ?? 0,
      cls: window.__web33LayoutShifts?.reduce((sum, entry) => sum + entry.value, 0) ?? 0,
      currentSources: loadedManualImages.map(image => image.currentSrc),
    };
  });
}

async function dismissOverlays(page) {
  await page.waitForTimeout(750);
  let quietChecks = 0;
  for (let attempt = 0; attempt < 20; attempt += 1) {
    const dismiss = page.getByRole('button', { name: 'Dismiss', exact: true }).last();
    if (await dismiss.isVisible().catch(() => false)) {
      await dismiss.evaluate(element => element.click());
      await page.waitForTimeout(600);
      quietChecks = 0;
      continue;
    }
    const firstRunClose = page.getByTestId('fre-close');
    if (await firstRunClose.isVisible().catch(() => false)) {
      await firstRunClose.evaluate(element => element.click());
      await page.waitForTimeout(600);
      quietChecks = 0;
      continue;
    }
    const dialog = page.getByRole('dialog').last();
    if (await dialog.isVisible().catch(() => false)) {
      const button = dialog.getByRole('button', { name: /close|skip|got it|continue|done|later|dismiss/i }).last();
      if (await button.isVisible().catch(() => false)) {
        await button.evaluate(element => element.click());
        await page.waitForTimeout(600);
        quietChecks = 0;
        continue;
      }
    }
    quietChecks += 1;
    if (quietChecks >= 3) return;
    await page.waitForTimeout(200);
  }
}

async function waitForResourceSettle(page) {
  let previousCount = -1;
  let stableSamples = 0;
  for (let attempt = 0; attempt < 30 && stableSamples < 5; attempt += 1) {
    await page.waitForTimeout(200);
    const count = await page.evaluate(() => performance.getEntriesByType('resource').length);
    stableSamples = count === previousCount ? stableSamples + 1 : 0;
    previousCount = count;
  }
}

function summarize(captures) {
  return Object.fromEntries(viewportDefinitions.map(([viewportName]) => {
    const matching = captures.filter(capture => capture.viewport === viewportName);
    return [viewportName, {
      headingReadyMs: summarizeMetric(matching, capture => capture.headingReadyMs),
      firstImageReadyMs: summarizeMetric(matching, capture => capture.firstImageReadyMs),
      carouselInteractionReadyMs: summarizeMetric(matching, capture => capture.carouselInteractionReadyMs),
      settingsScrollReadyMs: summarizeMetric(matching, capture => capture.settingsScrollReadyMs),
      initialDomElements: summarizeMetric(matching, capture => capture.initial.domElements),
      initialManualImageRequests: summarizeMetric(matching, capture => capture.initial.manualImageRequests),
      initialManualImageTransferBytes: summarizeMetric(matching, capture => capture.initial.manualImageTransferBytes),
      initialDecodedImagePixelBytes: summarizeMetric(matching, capture => capture.initial.decodedImagePixelBytes),
      initialUsedJsHeapBytes: summarizeMetric(matching, capture => capture.initial.usedJsHeapBytes),
      initialCls: summarizeMetric(matching, capture => capture.initial.cls),
    }];
  }));
}

function summarizeMetric(captures, select) {
  const values = captures.map(select).filter(value => typeof value === 'number').sort((a, b) => a - b);
  if (values.length === 0) return null;
  return {
    min: values[0],
    median: percentile(values, 0.5),
    p95: percentile(values, 0.95),
    max: values[values.length - 1],
  };
}

function percentile(sortedValues, percentileValue) {
  const index = Math.min(sortedValues.length - 1, Math.ceil(sortedValues.length * percentileValue) - 1);
  return sortedValues[index];
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
