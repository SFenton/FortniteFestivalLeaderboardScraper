import { test, expect } from './fixtures/fre';
import type { Page } from '@playwright/test';
import { changelogHash } from '../src/changelog';

// Scroll tests are designed for desktop viewports only
test.beforeEach(async ({ page, freState }, testInfo) => {
  if (testInfo.project.name !== 'desktop') {
    test.skip();
  }
  await freState.resetAppState();
  await page.evaluate(hash => {
    localStorage.setItem('fst:changelog', JSON.stringify({ version: 'e2e', hash }));
  }, changelogHash());
  await page.route(/^https?:\/\/[^/]+\/api\/songs(?:\?.*)?$/, route => route.fulfill({
    headers: { 'X-FST-Publication-Id': '1' },
    json: songsResponse(),
  }));
});

async function dismissFirstRun(page: Page) {
  const overlay = page.getByTestId('fre-overlay');
  if (!await overlay.isVisible().catch(() => false)) return;
  await page.getByTestId('fre-close').click();
  await expect(overlay).toBeHidden({ timeout: 5_000 });
}

test('scroll works at 1280px (narrow desktop)', async ({ page }) => {
  await page.goto('/#/songs');
  await page.getByText('Scroll Test Song 1', { exact: true }).waitFor({ state: 'visible' });
  await dismissFirstRun(page);

  const state = await readShellScrollState(page);
  console.log('shell scroll state:', JSON.stringify(state));
  expect(state.canScroll).toBe(true);
  expect(state.overflowY).toBe('auto');

  // Mouse wheel on the page header area (not over content)
  if (state.box) {
    // Wheel near the top (over header area)
    await page.mouse.move(state.box.x + state.box.width / 2, state.box.y + 30);
    await page.mouse.wheel(0, 500);
    await page.waitForTimeout(300);
  }
  const afterWheel = await readShellScrollTop(page);
  console.log(`Mouse wheel over header area: scrollTop=${afterWheel}`);
  expect(afterWheel).toBeGreaterThan(0);
});

test('scroll works at 1920px (wide desktop)', async ({ page }) => {
  await page.setViewportSize({ width: 1920, height: 900 });
  await page.goto('/#/songs');
  await page.getByText('Scroll Test Song 1', { exact: true }).waitFor({ state: 'visible' });
  await dismissFirstRun(page);

  const state = await readShellScrollState(page);
  console.log('Wide shell scroll state:', JSON.stringify(state));
  expect(state.canScroll).toBe(true);

  // Mouse wheel over the sidebar area (left of content)
  if (state.box) {
    await page.mouse.move(state.box.x + 50, state.box.y + state.box.height / 2);
    await page.mouse.wheel(0, 500);
    await page.waitForTimeout(300);
  }
  const afterWheel = await readShellScrollTop(page);
  console.log(`Wide desktop wheel over sidebar: scrollTop=${afterWheel}`);
  expect(afterWheel).toBeGreaterThan(0);
});

function songsResponse() {
  return {
    count: 80,
    currentSeason: 1,
    songs: Array.from({ length: 80 }, (_, index) => ({
      songId: `scroll-song-${index + 1}`,
      title: `Scroll Test Song ${index + 1}`,
      artist: 'Festival QA',
      year: 2026,
      durationSeconds: 180,
      difficulty: { guitar: 3 },
      maxScores: { Solo_Guitar: 100_000 },
    })),
  };
}

async function readShellScrollState(page: Page) {
  return page.evaluate(() => {
    let element = document.getElementById('main-content')?.parentElement ?? null;
    while (element) {
      const overflowY = getComputedStyle(element).overflowY;
      if (overflowY === 'auto' || overflowY === 'scroll') break;
      element = element.parentElement;
    }
    if (!element) throw new Error('Shell scroll container was not found');
    const style = getComputedStyle(element);
    const rect = element.getBoundingClientRect();
    return {
      scrollHeight: element.scrollHeight,
      clientHeight: element.clientHeight,
      canScroll: element.scrollHeight > element.clientHeight,
      overflowY: style.overflowY,
      box: { x: rect.x, y: rect.y, width: rect.width, height: rect.height },
    };
  });
}

async function readShellScrollTop(page: Page) {
  return page.evaluate(() => {
    let element = document.getElementById('main-content')?.parentElement ?? null;
    while (element) {
      const overflowY = getComputedStyle(element).overflowY;
      if (overflowY === 'auto' || overflowY === 'scroll') return element.scrollTop;
      element = element.parentElement;
    }
    throw new Error('Shell scroll container was not found');
  });
}
