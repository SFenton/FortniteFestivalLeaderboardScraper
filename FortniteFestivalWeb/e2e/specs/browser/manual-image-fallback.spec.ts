import { expect, test } from '../../fixtures/test';
import { createPopulatedScenario } from '../../fixtures/scenarios';
import { gotoAppRoute } from '../../support/drivers/app';

test.use({ scenario: createPopulatedScenario() });

test('Manual retries a failed responsive candidate with the full-resolution WebP fallback', async ({ page, appState }) => {
  await appState.reset();
  await appState.setSettings({ disableLightTrails: true });

  const compactRequests: string[] = [];
  const consoleErrors: string[] = [];
  const pageErrors: string[] = [];
  const failedResponses: string[] = [];
  let failedCandidateCount = 0;

  page.on('request', (request) => {
    if (request.url().includes('/manual/screenshots/optimized/navigation-overview-compact-')) {
      compactRequests.push(request.url());
    }
  });
  page.on('console', (message) => {
    if (message.type() === 'error') consoleErrors.push(message.text());
  });
  page.on('pageerror', error => pageErrors.push(error.message));
  page.on('response', (response) => {
    if (response.status() >= 400) {
      failedResponses.push(`${response.status()} ${response.url()}`);
    }
  });
  await page.route('**/icons/fst-icon.svg', route => route.fulfill({
    status: 200,
    contentType: 'image/svg+xml',
    body: '<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16"/>',
  }));

  await page.route('**/manual/screenshots/optimized/navigation-overview-compact-*.webp*', async (route) => {
    const url = new URL(route.request().url());
    if (url.searchParams.get('fallback') === '1') {
      await route.continue();
      return;
    }
    failedCandidateCount += 1;
    await route.fulfill({
      status: 200,
      contentType: 'image/webp',
      headers: { 'cache-control': 'no-store' },
      body: 'invalid-webp',
    });
  });

  await gotoAppRoute(page, '/manual');
  const carousel = page.getByTestId('manual-carousel-navigation-overview');
  await carousel.getByRole('button', { name: 'Next screenshot' }).click();

  const image = carousel.getByRole('img', {
    name: 'Navigation overview screenshot for Compact Web',
  });
  await expect(image).toHaveAttribute(
    'src',
    /navigation-overview-compact-[a-f0-9]{12}-1024\.webp\?fallback=1$/,
  );
  await expect.poll(() => image.evaluate(element => {
    const candidate = element as HTMLImageElement;
    return candidate.complete && candidate.naturalWidth > 0;
  })).toBe(true);
  await expect(image).toHaveAttribute('width', '1024');
  await expect(carousel.locator('source[type="image/webp"]')).toHaveCount(0);

  expect(failedCandidateCount).toBeGreaterThanOrEqual(1);
  expect(compactRequests.filter(url => new URL(url).searchParams.get('fallback') === '1')).toHaveLength(1);
  expect(compactRequests.every(url => new URL(url).pathname.endsWith('.webp'))).toBe(true);
  expect(consoleErrors, failedResponses.join('\n')).toEqual([]);
  expect(failedResponses).toEqual([]);
  expect(pageErrors).toEqual([]);
});
