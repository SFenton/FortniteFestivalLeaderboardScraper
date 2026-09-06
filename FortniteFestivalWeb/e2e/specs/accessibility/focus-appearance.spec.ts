import { readFile } from 'node:fs/promises';
import { test, expect } from '../../fixtures/test';
import { createPopulatedScenario } from '../../fixtures/scenarios';
import {
  activate,
  captureAppearance,
  expectSilentFocus,
  expectVisibleFocus,
  holdModule,
  recordFocusEvents,
  tabTo,
} from '../../support/focusAppearance';

test.use({ scenario: createPopulatedScenario() });

test.beforeEach(async ({ page, appState }) => {
  await recordFocusEvents(page);
  await appState.reset();
});

test.afterEach(async ({ page, browser, isMobile }, testInfo) => {
  const events = await page.evaluate(() => (
    (window as Window & { __focusAppearanceEvents?: unknown[] }).__focusAppearanceEvents ?? []
  )).catch(() => []);
  await testInfo.attach('focus-events.json', {
    body: JSON.stringify({ browser: browser.version(), project: testInfo.project.name, isMobile, url: page.url(), events }, null, 2),
    contentType: 'application/json',
  });
  await testInfo.attach('focus-appearance.png', {
    body: await page.screenshot(),
    contentType: 'image/png',
  });
});

test('returning-user startup leaves focus alone and real Tab reveals the skip link', async ({ page }, testInfo) => {
  await page.goto('/#/settings');
  const main = page.locator('main#main-content');
  await expect(main).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(main).not.toBeFocused();
  await expectSilentFocus(main, 'returning-main', testInfo);

  await page.keyboard.press('Tab');
  const skip = page.getByRole('link', { name: 'Skip to main content' });
  await expectVisibleFocus(skip);
  const url = page.url();
  await page.keyboard.press('Enter');
  await expectVisibleFocus(main);
  expect(page.url()).toBe(url);
  await captureAppearance(main, 'keyboard-skip-target', testInfo);
});

test('automatic First Run keeps focus inside without a startup outline', async ({ page, appState }, testInfo) => {
  await appState.setSettings({ hideItemShop: true });
  await page.goto('/#/songs');
  const card = page.getByTestId('fre-card');
  const close = page.getByTestId('fre-close');
  await expect(card).toBeVisible();
  await expect(card).toHaveCSS('opacity', '1');
  await expect(close).toBeFocused();
  await expectSilentFocus(close, 'first-run-startup-close', testInfo);
  await testInfo.attach('first-run-startup.png', { body: await page.screenshot(), contentType: 'image/png' });
  await page.keyboard.press('ArrowRight');
  await expectVisibleFocus(close);
  await page.keyboard.press('Shift+Tab');
  await expectVisibleFocus(page.getByTestId('fre-next'));
  await page.keyboard.press('Tab');
  await expectVisibleFocus(close);
  await captureAppearance(close, 'first-run-keyboard-close', testInfo);
});

test('changed changelog keeps focus inside without a startup outline', async ({ page }, testInfo) => {
  await page.localStorage.removeItem('fst:changelog');
  await page.goto('/#/settings');
  const dialog = page.getByRole('dialog', { name: /What's New/ });
  const close = dialog.getByRole('button', { name: 'Close' });
  await expect(dialog).toBeVisible();
  await expect(close).toBeFocused();
  await expectSilentFocus(close, 'changelog-startup-close', testInfo);
  await testInfo.attach('changelog-startup.png', { body: await page.screenshot(), contentType: 'image/png' });
  const dismiss = dialog.getByRole('button', { name: 'Dismiss', exact: true });
  await tabTo(page, dismiss);
  await expectVisibleFocus(dismiss);
});

test('pointer Search entry, text editing and warm return preserve silent launcher focus', async ({ page, isMobile }, testInfo) => {
  await page.goto('/#/settings');
  const launcher = page.getByTestId(isMobile ? 'mobile-header-search' : 'desktop-header-search');
  for (const dismissKeyboard of [false, true]) {
    const release = !dismissKeyboard
      ? await holdModule(page, /\/(?:SearchModal\.tsx|SearchModal-[^/?]+\.js)(?:\?.*)?$/)
      : undefined;
    try {
      await activate(launcher, isMobile);
      if (release) {
        const loading = page.getByTestId('search-modal-lazy-loading');
        await expect(loading).toBeVisible();
        await expect(loading).toBeFocused();
        await expectSilentFocus(loading, 'search-cold-loading', testInfo);
      }
    } finally {
      release?.();
    }
    const dialog = page.getByRole('dialog', { name: 'Search', exact: true })
      .filter({ has: page.getByRole('textbox') });
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveCSS('opacity', '1');
    if (isMobile) {
      await expect(dialog).toBeFocused();
      await expectSilentFocus(dialog, `search-panel-${dismissKeyboard}`, testInfo);
    }
    const input = dialog.getByRole('textbox');
    await activate(input, isMobile);
    await input.pressSequentially('fixture');
    await expect(input).toBeFocused();
    const editing = await captureAppearance(input, `editing-${dismissKeyboard}`, testInfo);
    expect(editing.tapHighlight).toBe('rgba(0, 0, 0, 0)');
    await expect(page.locator('html')).toHaveAttribute('data-fst-quiet-focus', '');
    if (dismissKeyboard) {
      await input.press('Enter');
      if (isMobile) await expect(input).not.toBeFocused();
    }
    await activate(dialog.getByRole('button', { name: 'Close' }), isMobile);
    await expect(dialog).toHaveCount(0);
    await expect(launcher).toBeFocused();
    await expectSilentFocus(launcher, `search-return-${dismissKeyboard}`, testInfo);
  }
});

test('pointer route navigation keeps main focus without a container outline', async ({ page, api, isMobile }, testInfo) => {
  await page.goto('/#/settings');
  const licenses = page.getByRole('link', { name: 'Licenses', exact: true });
  await activate(licenses, isMobile);
  await expect(page).toHaveURL(/#\/settings\/licenses$/);
  const main = page.locator('main#main-content');
  await expect(main).toBeFocused();
  await expectSilentFocus(main, 'pointer-route-main', testInfo);

  const launcher = page.getByTestId(isMobile ? 'mobile-header-search' : 'desktop-header-search');
  await activate(launcher, isMobile);
  const search = page.getByRole('dialog', { name: 'Search', exact: true });
  await activate(search.getByRole('button', { name: 'Close' }), isMobile);
  await expect(search).toHaveCount(0);
  await expect(launcher).toBeFocused();
  await page.goBack();
  await expect(page).toHaveURL(/#\/settings$/);
  await expect(main).not.toBeFocused();
  const popOwner = await page.evaluate(() => ({
    body: document.activeElement === document.body,
    testId: document.activeElement?.getAttribute('data-testid'),
  }));
  expect(popOwner.body || popOwner.testId === (isMobile ? 'mobile-header-search' : 'desktop-header-search')).toBe(true);
  await expectSilentFocus(page.locator(popOwner.body ? 'body' : ':focus'), 'pop-focus-owner', testInfo);
  await page.goForward();
  await expect(page).toHaveURL(/#\/settings\/licenses$/);
  await expect(main).not.toBeFocused();
  // WebKit route.fulfill rejects 304; this case tests focus, not conditional HTTP caching.
  api.override({ path: '/api/songs', status: 200, body: api.current().songs, remaining: 1 });
  await page.reload();
  await expect(main).toBeVisible();
  const bodyOwnsFocus = await page.evaluate(() => document.activeElement === document.body);
  await expectSilentFocus(page.locator(bodyOwnsFocus ? 'body' : ':focus'), 'reload-active-element', testInfo);
});

test('shared settings control stays silent after pointer activation and switches back to keyboard', async ({ page, isMobile }, testInfo) => {
  await page.goto('/#/settings');
  const toggle = page.getByRole('button', { name: /^Light Trails/ });
  await expect(toggle).toHaveCSS('touch-action', 'manipulation');
  const track = toggle.locator(':scope > div').last();
  const enabledColor = await track.evaluate(element => getComputedStyle(element).backgroundColor);
  await activate(toggle, isMobile);
  await expect(toggle).toBeFocused();
  await expectSilentFocus(toggle, 'settings-pointer-toggle', testInfo);
  await expect(track).not.toHaveCSS('background-color', enabledColor);
  await page.keyboard.press('Tab');
  await expectVisibleFocus(page.getByRole('button', { name: /^Show Buttons In Header/ }));
  await activate(toggle, isMobile);
  await expect(toggle).toBeFocused();
  await expectSilentFocus(toggle, 'settings-hybrid-toggle', testInfo);
  await expect(track).toHaveCSS('background-color', enabledColor);
  await page.keyboard.press('ArrowRight');
  await expectVisibleFocus(toggle);
});

test('custom song-title link has transparent tap feedback and remains keyboard visible', async ({ page, isMobile }, testInfo) => {
  await page.goto('/#/songs/e2e-song-01/Solo_Guitar');
  const title = page.getByRole('link').filter({ has: page.getByRole('heading', { level: 1 }) });
  await expect(title).toBeVisible();
  const appearance = await captureAppearance(title, 'custom-title-link', testInfo);
  expect(appearance.tapHighlight).toBe('rgba(0, 0, 0, 0)');
  await tabTo(page, title);
  await expectVisibleFocus(title);
  await activate(title, isMobile);
  await expect(page).toHaveURL(/#\/songs\/e2e-song-01$/);
});

test('keyboard-opened Search preserves visible focus and exact Escape return', async ({ page, isMobile }, testInfo) => {
  await page.goto('/#/settings');
  const launcher = page.getByTestId(isMobile ? 'mobile-header-search' : 'desktop-header-search');
  await tabTo(page, launcher);
  await expectVisibleFocus(launcher);
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name: 'Search', exact: true })
    .filter({ has: page.getByRole('textbox') });
  await expect(dialog).toBeVisible();
  // This is a trusted keyboard sequence, not an isolated assistive-click probe:
  // its preceding keydown already restores native appearance.
  const trustedKeyboardClick = await page.evaluate(testId => {
    const events = (window as Window & {
      __focusAppearanceEvents?: Array<{
        type: string; trusted?: boolean; detail?: number; pointerType?: string;
        target?: { testId?: string };
      }>;
    }).__focusAppearanceEvents ?? [];
    return events.some(event => event.type === 'click' && event.trusted
      && event.detail === 0 && !event.pointerType && event.target?.testId === testId);
  }, isMobile ? 'mobile-header-search' : 'desktop-header-search');
  expect(trustedKeyboardClick).toBe(true);
  if (isMobile) await expectVisibleFocus(dialog);
  else await expect(dialog.getByRole('textbox')).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expectVisibleFocus(launcher);
  await captureAppearance(launcher, 'keyboard-search-return', testInfo);
});

test('keyboard route activation keeps a visible main focus target', async ({ page }) => {
  await page.goto('/#/settings');
  const licenses = page.getByRole('link', { name: 'Licenses', exact: true });
  await tabTo(page, licenses);
  await expectVisibleFocus(licenses);
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/#\/settings\/licenses$/);
  await expectVisibleFocus(page.locator('main#main-content'));
});

test('nested metric help retains silent pointer entry, keyboard trapping and both return targets', async ({ page, appState, isMobile }, testInfo) => {
  await appState.selectPlayer();
  await appState.setSettings({ enableExperimentalRanks: true });
  await page.goto('/#/leaderboards');
  const firstRun = page.getByTestId('fre-card');
  await expect(firstRun).toBeVisible();
  await activate(page.getByTestId('fre-close'), isMobile);
  await expect(firstRun).toHaveCount(0);
  const launcher = page.getByRole('button', {
    name: isMobile ? 'Change Leaderboard Ranking' : 'Total Score',
    exact: true,
  }).first();
  await expect(launcher).toBeVisible();
  await activate(launcher, isMobile);
  const rankBy = page.locator('[role="dialog"][aria-label="Rank By"]');
  const info = rankBy.getByRole('button', { name: 'Learn how FC Rate works' });
  await expect(info).toBeVisible();
  const rankByClose = rankBy.getByRole('button', { name: 'Close' });
  await expect(rankByClose).toBeFocused();
  await expectSilentFocus(rankByClose, 'rank-by-pointer-entry', testInfo);
  const release = await holdModule(page, /\/(?:MetricInfoCarousel\.tsx|MetricInfoCarousel-[^/?]+\.js)(?:\?.*)?$/);
  try {
    await activate(info, isMobile);
    const loading = page.getByTestId('rank-metric-info-lazy-loading');
    await expect(loading).toBeVisible();
    await expect(loading).toBeFocused();
    await expectSilentFocus(loading, 'metric-help-loading', testInfo);
  } finally {
    release();
  }
  const help = page.getByRole('dialog', { name: 'FC Rate details', exact: true })
    .filter({ has: page.getByRole('button', { name: 'Forward one entry' }) });
  const close = help.getByRole('button', { name: 'Close' });
  await expect(close).toBeFocused();
  await expect(rankBy).toHaveAttribute('inert', '');
  await expectSilentFocus(close, 'metric-help-pointer-entry', testInfo);
  await page.keyboard.press('Shift+Tab');
  await expectVisibleFocus(help.getByRole('button', { name: 'Forward one entry' }));
  await page.keyboard.press('Tab');
  await expectVisibleFocus(close);
  await page.keyboard.press('Escape');
  await expect(help).toHaveCount(0);
  await expectVisibleFocus(info);
  await activate(rankBy.getByRole('button', { name: 'Close' }), isMobile);
  await expect(rankBy).toHaveCount(0);
  await expect(launcher).toBeFocused();
  await expectSilentFocus(launcher, 'rank-by-pointer-return', testInfo);
});

test('application-generated activation retains pointer appearance without losing modal semantics', async ({ page, isMobile }, testInfo) => {
  await page.goto('/#/settings');
  const previous = page.getByRole('button', { name: /^Light Trails/ });
  await activate(previous, isMobile);
  const launcher = page.getByTestId(isMobile ? 'mobile-header-search' : 'desktop-header-search');
  const release = await holdModule(page, /\/(?:SearchModal\.tsx|SearchModal-[^/?]+\.js)(?:\?.*)?$/);
  const loading = page.getByTestId('search-modal-lazy-loading');
  try {
    await launcher.evaluate(element => {
      if (element instanceof HTMLElement) element.click();
    });
    await expect(loading).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('data-fst-quiet-focus', '');
    if (isMobile) {
      await expect(loading).toBeFocused();
      await expectSilentFocus(loading, 'application-click-loading-focus', testInfo);
    }
  } finally {
    release();
  }
  const dialog = page.getByRole('dialog', { name: 'Search', exact: true })
    .filter({ has: page.getByRole('textbox') });
  await expect(dialog).toBeVisible();
  await expect(loading).toHaveCount(0);
  await expect(page.locator('html')).toHaveAttribute('data-fst-quiet-focus', '');
  if (isMobile) {
    await expect(dialog).toBeFocused();
    await expectSilentFocus(dialog, 'application-click-quiet-focus', testInfo);
  }
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expectVisibleFocus(previous);
});

test('pointer Export Data keeps quiet provenance through its synthetic download click', async ({ page, appState, isMobile }, testInfo) => {
  await appState.selectPlayer();
  await page.goto('/#/settings');
  const button = page.getByRole('button', { name: /^(Export Data|Preparing\.\.\.)$/ });
  await expect(button).toBeEnabled();

  let release!: () => void;
  let requestStarted!: () => void;
  const pending = new Promise<void>(resolve => { release = resolve; });
  const started = new Promise<void>(resolve => { requestStarted = resolve; });
  await page.route(/\/api\/player\/[^/]+\/export(?:\?.*)?$/, async route => {
    requestStarted();
    await pending;
    await route.fallback();
  }, { times: 1 });

  const downloading = page.waitForEvent('download');
  let busyOwner: string | undefined;
  try {
    await activate(button, isMobile);
    await started;
    await expect(button).toBeDisabled();
    await expect(button).not.toBeFocused();
    await expect(page.locator('html')).toHaveAttribute('data-fst-quiet-focus', '');
    busyOwner = await page.evaluate(() => document.activeElement?.tagName);
    expect(['BODY', 'MAIN']).toContain(busyOwner);
    await captureAppearance(button, 'export-busy-button', testInfo);
  } finally {
    release();
  }

  const download = await downloading;
  const path = testInfo.outputPath('fixture-export.zip');
  await download.saveAs(path);
  expect(await download.failure()).toBeNull();
  expect((await readFile(path)).toString()).toBe('e2e-export');
  await expect(button).toBeEnabled();
  await expect(button).not.toBeFocused();
  expect(await page.evaluate(() => document.activeElement?.tagName)).toBe(busyOwner);
  await captureAppearance(button, 'export-completed-button', testInfo);
  const syntheticClicks = await page.evaluate(() => {
    const events = (window as Window & {
      __focusAppearanceEvents?: Array<{
        type: string; trusted?: boolean; detail?: number; pointerType?: string;
        target?: { download?: string };
      }>;
    }).__focusAppearanceEvents ?? [];
    return events.filter(event => event.type === 'click' && event.target?.download);
  });
  expect(syntheticClicks.some(event => event.trusted === false && event.detail === 0 && !event.pointerType)).toBe(true);
  await testInfo.attach('export-provenance.json', {
    body: JSON.stringify({ busyOwner, syntheticClicks, suggestedFilename: download.suggestedFilename() }, null, 2),
    contentType: 'application/json',
  });
  await expect(page.locator('html')).toHaveAttribute('data-fst-quiet-focus', '');
});

test('forced colors keeps native startup focus indication available', async ({ page, appState }, testInfo) => {
  await page.emulateMedia({ forcedColors: 'active' });
  await appState.setSettings({ hideItemShop: true });
  await page.goto('/#/songs');
  expect(await page.evaluate(() => matchMedia('(forced-colors: active)').matches)).toBe(true);
  const close = page.getByTestId('fre-close');
  await expectVisibleFocus(close);
  await captureAppearance(close, 'forced-colors-startup', testInfo);
});
