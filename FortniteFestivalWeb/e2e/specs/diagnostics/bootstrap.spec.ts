import { expect, test } from '../../fixtures/test';
import { isPrimaryDesktopProject } from '../../support/projects';

test('scroll-fade diagnostics load only when explicitly requested', async ({ page, appState }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'diagnostic entry loading is covered once');
  const moduleRequests: string[] = [];
  page.on('request', request => {
    if (request.url().includes('/src/diagnostics/scrollFadeTestMode.ts')) {
      moduleRequests.push(request.url());
    }
  });

  await appState.reset();
  await page.goto('/#/songs', { waitUntil: 'load' });
  await expect(page.locator('html')).not.toHaveClass(/fst-disable-scroll-fade/);
  expect(moduleRequests).toHaveLength(0);

  await page.goto('/?disableScrollFade=1#/songs', { waitUntil: 'load' });
  await expect(page.locator('html')).toHaveClass(/fst-disable-scroll-fade/);
  await expect.poll(() => page.localStorage.getItem('fst.disableScrollFade')).toBe('1');
  expect(moduleRequests).toHaveLength(1);

  await page.goto('/#/songs', { waitUntil: 'load' });
  await expect(page.locator('html')).toHaveClass(/fst-disable-scroll-fade/);
  expect(moduleRequests).toHaveLength(2);
});

test('PWA capture renders without publication or backend requests', async ({ page }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'diagnostic root independence is covered once');
  const apiRequests: string[] = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname.startsWith('/api/')) apiRequests.push(request.url());
  });

  await page.goto('/?pwaIconCapture=1&pwaIconSize=192', { waitUntil: 'load' });
  await expect(page.getByTestId('pwa-icon-capture')).toBeVisible();
  expect(apiRequests).toHaveLength(0);
});

test('the app remains usable when scroll-fade diagnostics cannot load', async ({ page }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'diagnostic failure recovery is covered once');
  const consoleErrors: string[] = [];
  page.on('console', message => {
    if (message.type() === 'error') consoleErrors.push(message.text());
  });
  await page.route('**/src/diagnostics/scrollFadeTestMode.ts*', route => route.abort('failed'));

  await page.goto('/?disableScrollFade=1#/songs', { waitUntil: 'load' });
  await expect(page.getByTestId('page-root')).toBeVisible({ timeout: 15_000 });
  await expect(page.locator('html')).not.toHaveClass(/fst-disable-scroll-fade/);
  expect(consoleErrors.some(message => message.includes('Unable to load scroll-fade diagnostic mode.'))).toBe(true);
});
