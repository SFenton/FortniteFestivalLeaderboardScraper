import axe, { type AxeResults } from 'axe-core';
import { test, expect } from '../../fixtures/test';
import { createPopulatedScenario } from '../../fixtures/scenarios';
import { dismissObstructions, gotoAppRoute } from '../../support/drivers/app';
import { isMobileProject, isPrimaryDesktopProject, isPrimaryMobileProject } from '../../support/projects';

declare global {
  interface Window {
    axe: {
      run(context?: string): Promise<AxeResults>;
    };
  }
}

test.use({ scenario: createPopulatedScenario() });

const blockingImpacts = new Set(['moderate', 'serious', 'critical']);

for (const route of ['/songs', '/suggestions', '/leaderboards', '/settings', '/manual', '/missing/deep-link']) {
  test(`${route} has no moderate, serious, or critical axe violations`, async ({ page, appState }) => {
    await appState.reset();
    await appState.selectPlayer();
    await gotoAppRoute(page, route);
    await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
    await dismissObstructions(page);
    await expect(page.getByRole('heading', { level: 1 }).first()).toBeAttached();
    await expect(page.getByRole('heading', { level: 1 })).toHaveCount(1);
    await page.addScriptTag({ content: axe.source });

    const results = await page.evaluate(() => window.axe.run());
    expect(results.violations.filter(violation => (
      violation.impact && blockingImpacts.has(violation.impact)
    ))).toEqual([]);
  });
}

test('skip navigation focuses main without replacing the HashRouter URL', async ({ page, appState }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'skip navigation is covered once');
  await appState.reset();
  await gotoAppRoute(page, '/songs');
  await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  await dismissObstructions(page);
  const originalUrl = page.url();

  await page.keyboard.press('Tab');
  const skipLink = page.getByRole('link', { name: 'Skip to main content' });
  await expect(skipLink).toBeFocused();
  await page.keyboard.press('Enter');

  await expect(page.locator('main#main-content')).toBeFocused();
  expect(page.url()).toBe(originalUrl);
});

test('route metadata updates title, announcement, and PUSH/POP focus policy', async ({ page, appState }, testInfo) => {
  test.skip(!isPrimaryMobileProject(testInfo.project.name), 'mobile shell route focus is covered once');
  await appState.reset();
  await appState.selectPlayer();
  await gotoAppRoute(page, '/settings');
  await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  await dismissObstructions(page);
  await expect(page).toHaveTitle('Settings | Festival Score Tracker');

  await page.getByText('Licenses', { exact: true }).click();
  await expect(page).toHaveURL(/#\/settings\/licenses$/);
  await expect(page).toHaveTitle('Licenses | Festival Score Tracker');
  await expect(page.getByText('Navigated to Licenses')).toBeAttached();
  await expect(page.locator('main#main-content')).toBeFocused();

  const settingsTab = page.getByTestId('bottom-nav-settings');
  await settingsTab.focus();
  await page.goBack();
  await expect(page).toHaveURL(/#\/settings$/);
  await expect(page).toHaveTitle('Settings | Festival Score Tracker');
  await expect(settingsTab).toBeFocused();
});

test('shell replacement preserves one main landmark without a route announcement', async ({ page, appState }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'layout replacement is covered once');
  await appState.reset();
  await gotoAppRoute(page, '/songs');
  await expect(page.locator('main#main-content')).toHaveCount(1);
  await expect(page.getByText('Navigated to Songs')).toHaveCount(0);

  await page.setViewportSize({ width: 1_600, height: 900 });
  await expect(page.locator('main#main-content')).toHaveCount(1);
  await expect(page.getByText('Navigated to Songs')).toHaveCount(0);
});

test('decorative background stops animating for reduced motion', async ({ page, appState }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await appState.reset();
  await gotoAppRoute(page, '/songs');
  await expect(page.getByText('Deterministic Song 2', { exact: true })).toBeVisible();

  const backgroundAnimations = await page.evaluate(() => (
    Array.from(document.querySelectorAll<HTMLElement>('div'))
      .filter(element => getComputedStyle(element).backgroundImage !== 'none')
      .reduce((count, element) => count + element.getAnimations().length, 0)
  ));
  expect(backgroundAnimations).toBe(0);
  const infiniteDecorativeAnimations = await page.evaluate(() => (
    document.getAnimations().filter((animation) => {
      const timing = animation.effect?.getComputedTiming();
      const target = animation.effect instanceof KeyframeEffect
        ? animation.effect.target
        : null;
      const animationName = target instanceof Element
        ? getComputedStyle(target).animationName
        : '';
      return timing?.iterations === Infinity
        && !animationName.toLowerCase().includes('spin')
        && !animationName.toLowerCase().includes('indeterminate');
    }).length
  ));
  expect(infiniteDecorativeAnimations).toBe(0);
});

test('Save-Data omits decorative remote background art', async ({ page, appState }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'Save-Data policy is covered once');
  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'connection', {
      configurable: true,
      value: {
        saveData: true,
        addEventListener() {},
        removeEventListener() {},
      },
    });
  });
  await appState.reset();
  await gotoAppRoute(page, '/songs');
  await expect(page.getByText('Deterministic Song 2', { exact: true })).toBeVisible();

  const remoteBackgrounds = await page.evaluate(() => (
    Array.from(document.querySelectorAll<HTMLElement>('div'))
      .filter((element) => {
        const backgroundImage = getComputedStyle(element).backgroundImage;
        return backgroundImage.includes('url(') && !backgroundImage.includes('data:image');
      })
      .map(element => ({
        testId: element.dataset.testid ?? null,
        className: element.className,
        backgroundImage: getComputedStyle(element).backgroundImage,
      }))
  ));
  expect(remoteBackgrounds).toEqual([]);
});

test('instrument images expose friendly labels instead of wire keys', async ({ page, appState }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'instrument image semantics are covered once');
  await appState.reset();
  await appState.selectPlayer();
  await gotoAppRoute(page, '/songs/e2e-song-01/Solo_Guitar');

  const leadIcon = page.locator('img[data-instrument="Solo_Guitar"]').first();
  await expect(leadIcon).toHaveAttribute('alt', 'Lead');
  await expect(page.locator('img[alt^="Solo_"]')).toHaveCount(0);
});

test('real Paths overlay has no moderate, serious, or critical axe violations', async ({ page, appState }) => {
  await appState.reset();
  await appState.selectPlayer();
  await appState.setSettings({
    pathDefaultView: 'text',
    pathUnavailableWarningDismissed: true,
    showPeripheralCymbals: true,
    showPeripheralDrums: true,
  });
  await gotoAppRoute(page, '/songs/e2e-song-01');
  await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  await dismissObstructions(page);
  await page.getByRole('button', { name: 'View Paths', exact: true }).first().click();
  const dialog = page.getByRole('dialog', { name: 'Paths' });
  await expect(dialog).toBeVisible();
  await expect(dialog).toHaveCSS('opacity', '1');
  await page.addScriptTag({ content: axe.source });

  const results = await page.evaluate(() => window.axe.run('[role="dialog"]'));
  expect(results.violations.filter(violation => (
    violation.impact && blockingImpacts.has(violation.impact)
  ))).toEqual([]);
});

test('real Notifications overlay has no moderate, serious, or critical axe violations', async ({ page, appState }, testInfo) => {
  await appState.reset();
  await appState.selectPlayer();
  await gotoAppRoute(page, '/songs');
  await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  await dismissObstructions(page);
  const notificationsButton = page.getByTestId(
    isMobileProject(testInfo.project.name)
      ? 'mobile-header-notifications'
      : 'desktop-header-notifications',
  );
  await expect(notificationsButton).toBeVisible({ timeout: 10_000 });
  await notificationsButton.click();
  const dialog = page.getByRole('dialog', { name: 'Notifications' });
  await expect(dialog).toBeVisible();
  await page.addScriptTag({ content: axe.source });

  const results = await page.evaluate(() => window.axe.run('[role="dialog"]'));
  expect(results.violations.filter(violation => (
    violation.impact && blockingImpacts.has(violation.impact)
  ))).toEqual([]);
});
