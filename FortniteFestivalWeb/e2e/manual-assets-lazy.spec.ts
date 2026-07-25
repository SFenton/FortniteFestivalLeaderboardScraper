import { expect, test, type Page, type Route } from '@playwright/test';

test.beforeEach(async ({ page }) => {
  await installApiMocks(page);
  await seedState(page);
});

test('desktop Manual loads only near responsive images and preserves carousel state while scrolling', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop-wide', 'desktop Manual waterfall is covered at its production capture width');
  const manualRequests: string[] = [];
  const consoleErrors: string[] = [];
  const pageErrors: string[] = [];
  page.on('request', (request) => {
    if (request.url().includes('/manual/screenshots/')) manualRequests.push(request.url());
  });
  page.on('console', (message) => {
    if (message.type() === 'error') consoleErrors.push(message.text());
  });
  page.on('pageerror', error => pageErrors.push(error.message));

  await page.route('**/manual/screenshots/optimized/navigation-overview-mobile-*.webp', async (route) => {
    await new Promise(resolve => setTimeout(resolve, 750));
    await route.continue();
  }, { times: 1 });

  await page.goto('/#/manual', { waitUntil: 'load' });
  await dismissOverlays(page);
  await expect(page.getByRole('heading', { name: 'Navigation Basics' })).toBeVisible();

  const firstCarousel = page.getByTestId('manual-carousel-navigation-overview');
  const firstImage = firstCarousel.getByRole('img', { name: 'Navigation overview screenshot for Mobile' });
  const pendingBox = await firstCarousel.boundingBox();
  await expect.poll(() => firstImage.evaluate(image => image.complete && image.naturalWidth > 0)).toBe(true);
  const loadedBox = await firstCarousel.boundingBox();
  await waitForRequestSettle(page);

  expect(pendingBox).not.toBeNull();
  expect(loadedBox).not.toBeNull();
  expect(Math.abs(loadedBox!.height - pendingBox!.height)).toBeLessThanOrEqual(1);

  const initialRequests = [...manualRequests];
  expect(initialRequests.length).toBeGreaterThan(0);
  expect(initialRequests.length).toBeLessThanOrEqual(2);
  expect(new Set(initialRequests).size).toBe(initialRequests.length);
  expect(initialRequests.every(url => url.includes('/optimized/') && url.includes('-mobile-'))).toBe(true);
  expect(initialRequests.some(url => url.includes('settings-'))).toBe(false);
  expect(await firstImage.evaluate(image => image.currentSrc)).toMatch(/navigation-overview-mobile-[a-f0-9]{12}-390\.webp$/);
  await expect(firstImage).toHaveAttribute('width', '390');
  await expect(firstImage).toHaveAttribute('height', '844');

  const nextButton = firstCarousel.getByRole('button', { name: 'Next screenshot' });
  await nextButton.focus();
  await nextButton.press('Enter');
  const compactImage = firstCarousel.getByRole('img', { name: 'Navigation overview screenshot for Compact Web' });
  await expect.poll(() => compactImage.evaluate(image => image.currentSrc)).toMatch(/navigation-overview-compact-[a-f0-9]{12}-768\.webp$/);
  await expect(compactImage).toHaveAttribute('width', '1024');
  await expect(compactImage).toHaveAttribute('height', '768');

  const settingsLink = page.getByTestId('manual-quick-link-settings');
  await settingsLink.focus();
  await settingsLink.press('Enter');
  await expect(page.getByTestId('manual-carousel-settings-overview')).toHaveAttribute('data-mounted', 'true');
  await expect.poll(() => manualRequests.some(url => url.includes('settings-overview-mobile-'))).toBe(true);
  await waitForRequestSettle(page);
  expect(new Set(manualRequests).size).toBeLessThanOrEqual(5);
  expect(manualRequests.some(url => url.includes('/optimized/songs-'))).toBe(false);
  expect(manualRequests.some(url => url.includes('/optimized/profiles-'))).toBe(false);

  await firstCarousel.scrollIntoViewIfNeeded();
  await expect(firstCarousel.getByText('Compact Web', { exact: true }).first()).toBeVisible();
  expect(consoleErrors).toEqual([]);
  expect(pageErrors).toEqual([]);
});

test('mobile Manual selects the small source and keeps swipe/button behavior without eager far images', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mobile', 'mobile Manual waterfall is covered once');
  const manualRequests: string[] = [];
  const consoleErrors: string[] = [];
  const pageErrors: string[] = [];
  page.on('request', (request) => {
    if (request.url().includes('/manual/screenshots/')) manualRequests.push(request.url());
  });
  page.on('console', (message) => {
    if (message.type() === 'error') consoleErrors.push(message.text());
  });
  page.on('pageerror', error => pageErrors.push(error.message));

  await page.goto('/#/manual', { waitUntil: 'load' });
  await dismissOverlays(page);

  const carousel = page.getByTestId('manual-carousel-navigation-overview');
  const mobileImage = carousel.getByRole('img', { name: 'Navigation overview screenshot for Mobile' });
  await expect.poll(() => mobileImage.evaluate(image => image.currentSrc)).toMatch(/navigation-overview-mobile-[a-f0-9]{12}-240\.webp$/);
  await waitForRequestSettle(page);

  expect(manualRequests.length).toBeGreaterThan(0);
  expect(manualRequests.length).toBeLessThanOrEqual(3);
  const requestCounts = new Map<string, number>();
  for (const request of manualRequests) requestCounts.set(request, (requestCounts.get(request) ?? 0) + 1);
  expect(Math.max(...requestCounts.values())).toBeLessThanOrEqual(2);
  expect(manualRequests.some(url => url.includes('songs-'))).toBe(false);

  const frame = page.getByTestId('manual-carousel-frame-navigation-overview');
  await frame.dispatchEvent('touchstart', { touches: [{ identifier: 1, clientX: 220, clientY: 200 }] });
  await frame.dispatchEvent('touchend', { changedTouches: [{ identifier: 1, clientX: 90, clientY: 200 }] });

  const compactImage = carousel.getByRole('img', { name: 'Navigation overview screenshot for Compact Web' });
  await expect.poll(() => compactImage.evaluate(image => image.currentSrc)).toMatch(/navigation-overview-compact-[a-f0-9]{12}-480\.webp$/);

  const previousButton = carousel.getByRole('button', { name: 'Previous screenshot' });
  await previousButton.focus();
  await previousButton.press('Enter');
  await expect(carousel.getByRole('img', { name: 'Navigation overview screenshot for Mobile' })).toBeVisible();
  expect(consoleErrors).toEqual([]);
  expect(pageErrors).toEqual([]);
});

async function seedState(page: Page) {
  await page.addInitScript(() => {
    localStorage.clear();
    localStorage.setItem('fst:changelog', JSON.stringify({ version: 'web33', hash: 'web33' }));
    localStorage.setItem('fst:firstRun', JSON.stringify({}));
    localStorage.setItem('fst:appSettings', JSON.stringify({ disableLightTrails: true }));
  });
}

async function dismissOverlays(page: Page) {
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

async function waitForRequestSettle(page: Page) {
  let previousCount = -1;
  let stableSamples = 0;
  for (let attempt = 0; attempt < 20 && stableSamples < 4; attempt += 1) {
    await page.waitForTimeout(150);
    const count = await page.evaluate(() => performance.getEntriesByType('resource').length);
    stableSamples = count === previousCount ? stableSamples + 1 : 0;
    previousCount = count;
  }
}

async function installApiMocks(page: Page) {
  await page.routeWebSocket('**/api/ws', () => {});
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    if (!path.startsWith('/api/')) return route.continue();
    if (path === '/api/features') {
      return json(route, {
        compete: true,
        leaderboards: true,
        difficulty: true,
        playerBands: true,
        experimentalRanks: true,
        appManual: true,
      });
    }
    if (path === '/api/service-info') {
      return json(route, {
        publishedScrapeId: 1236,
        activeScrapeId: null,
        publication: { publishedScrapeId: 1236, publicReadsFrozen: false },
        workerStatus: { status: 'offline', rawStatus: 'offline' },
      });
    }
    if (path === '/api/songs') {
      return json(route, {
        count: 1,
        currentSeason: 14,
        songs: [{ songId: 'web33-song', title: 'WEB-3.3 Song', artist: 'WEB-3.3 Artist', year: 2026 }],
      });
    }
    if (path === '/api/shop') return json(route, { count: 0, songs: [], newSongs: [], lastUpdated: null });
    if (path === '/api/version') return json(route, { version: 'web33' });
    return json(route, {});
  });
}

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}
