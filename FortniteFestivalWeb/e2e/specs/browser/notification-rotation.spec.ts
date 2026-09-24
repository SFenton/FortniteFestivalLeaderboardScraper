import type { Locator, Page } from '@playwright/test';
import { test, expect } from '../../fixtures/test';
import { createPopulatedScenario } from '../../fixtures/scenarios';
import { isMobileProject } from '../../support/projects';

const PORTRAIT = { width: 390, height: 844 };
const LANDSCAPES = [
  { width: 844, height: 390, placement: 'center' },
  { width: 667, height: 375, placement: 'mobileSheet' },
] as const;
const MAX_RETURNED_GLYPH_DELTA_PX = 1;
const MAX_RETURNED_PANEL_DELTA_PX = 2;
const ART_FIXTURE = '<svg xmlns="http://www.w3.org/2000/svg" width="54" height="54"/>';

type TextMetrics = {
  fontSize: number;
  lineHeight: number;
  glyphHeight: number;
  textSizeAdjust: string;
  webkitTextSizeAdjust: string;
};

type NotificationMetrics = {
  header: TextMetrics;
  section: TextMetrics;
  title: TextMetrics;
  summary: TextMetrics;
  supportsTextAdjustment: { standard: boolean; webkit: boolean };
  panel: { top: number; left: number; right: number; bottom: number; width: number; height: number; scrollWidth: number; clientWidth: number };
  list: { scrollWidth: number; clientWidth: number; scrollHeight: number; clientHeight: number; scrollTop: number };
  visualScale: number;
  focusInside: boolean;
};

test.use({ scenario: createPopulatedScenario() });

test('notifications retain authored typography and usable scrolling across both mobile rotation layouts', async ({ page, appState }, testInfo) => {
  test.skip(!isMobileProject(testInfo.project.name), 'mobile browser rotation only');

  await page.setViewportSize(PORTRAIT);
  await page.route('https://cdn2.unrealengine.com/**', route => route.fulfill({
    status: 200,
    contentType: 'image/svg+xml',
    body: ART_FIXTURE,
  }));
  await appState.reset();
  await appState.selectPlayer();
  await appState.setSettings({ enableExperimentalRanks: true });
  await page.goto('/?validation=notifications-open#/songs', { waitUntil: 'load' });

  const firstRun = page.getByTestId('fre-card');
  await expect(firstRun).toBeVisible({ timeout: 15_000 });
  await expectTextAdjustment(firstRun);
  await page.getByTestId('fre-close').click();
  await expect(page.getByTestId('fre-overlay')).toBeHidden();

  const dialog = page.getByRole('dialog', { name: 'Notifications' });
  await expect(dialog).toBeVisible({ timeout: 15_000 });
  await dialog.getByRole('button', { name: 'Close' }).click();
  await expect(dialog).toBeHidden();
  const launcher = page.getByTestId('mobile-header-notifications');
  await expect(launcher).toBeVisible();
  await launcher.click();
  await expect(dialog).toBeVisible();
  await expect(dialog).toHaveAttribute('data-modal-placement', 'mobileSheet');
  await expect(dialog.getByTestId('mock-notification-row')).toHaveCount(6);
  await expectTextAdjustment(dialog);

  const list = dialog.getByTestId('notification-list');
  await expect.poll(() => list.evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true);
  await expect.poll(() => dialog.evaluate(element => element.contains(document.activeElement))).toBe(true);
  await page.evaluate(async () => { await document.fonts.ready; });
  await settleLayout(page, dialog);
  const baseline = await measureNotifications(dialog);
  await testInfo.attach('text-size-adjust-support.json', {
    body: JSON.stringify({ project: testInfo.project.name, ...baseline.supportsTextAdjustment }),
    contentType: 'application/json',
  });
  if (testInfo.project.name === 'chromium-mobile') {
    expect(baseline.supportsTextAdjustment).toEqual({ standard: true, webkit: true });
  }
  expect(baseline.header.fontSize).toBe(20);
  expect(baseline.section.fontSize).toBe(11);
  expect(baseline.title.fontSize).toBe(16);
  expect(baseline.summary.fontSize).toBe(12);
  expectUsableDialog(baseline, PORTRAIT);

  const originalDialog = await dialog.elementHandle();
  if (!originalDialog) throw new Error('Notification dialog detached before rotation');

  for (const landscape of LANDSCAPES) {
    await page.setViewportSize(landscape);
    await expect(dialog).toHaveAttribute('data-modal-placement', landscape.placement);
    await settleLayout(page, dialog);
    expect(await dialog.evaluate((element, original) => element === original, originalDialog)).toBe(true);
    await expectTextAdjustment(dialog);
    const rotated = await measureNotifications(dialog);
    expectUsableDialog(rotated, landscape);

    await list.evaluate(element => { element.scrollTop = element.scrollHeight; });
    await expect.poll(() => list.evaluate(element => element.scrollTop)).toBeGreaterThan(0);

    await page.setViewportSize(PORTRAIT);
    await expect(dialog).toHaveAttribute('data-modal-placement', 'mobileSheet');
    await settleLayout(page, dialog);
    expect(await dialog.evaluate((element, original) => element === original, originalDialog)).toBe(true);
    await expectTextAdjustment(dialog);
    const returned = await measureNotifications(dialog);
    expectUsableDialog(returned, PORTRAIT);
    expect(returned.list.scrollTop).toBeGreaterThan(0);
    expectReturnedTypography(baseline, returned);
  }

  await dialog.getByRole('button', { name: 'Close' }).click();
  await expect(dialog).toBeHidden();
  await expect(launcher).toBeFocused();
});

async function expectTextAdjustment(element: Locator): Promise<void> {
  const values = await element.evaluate(node => {
    const style = getComputedStyle(node);
    return {
      supportsStandard: CSS.supports('text-size-adjust', '100%'),
      supportsWebkit: CSS.supports('-webkit-text-size-adjust', '100%'),
      standard: style.getPropertyValue('text-size-adjust'),
      webkit: style.getPropertyValue('-webkit-text-size-adjust'),
    };
  });
  expect(values.standard).toBe(values.supportsStandard ? '100%' : '');
  expect(values.webkit).toBe(values.supportsWebkit ? '100%' : '');
}

async function settleLayout(page: Page, dialog: Locator): Promise<void> {
  await page.evaluate(() => new Promise<void>(resolve => {
    requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
  }));
  await expect.poll(() => dialog.evaluate(element => element.getAnimations().length), { timeout: 5_000 }).toBe(0);
}

async function measureNotifications(dialog: Locator): Promise<NotificationMetrics> {
  return dialog.evaluate(panel => {
    const list = panel.querySelector<HTMLElement>('[data-testid="notification-list"]');
    const header = panel.querySelector<HTMLElement>('h2');
    const section = panel.querySelector<HTMLElement>('[data-testid="notification-section-heading"]');
    const title = panel.querySelector<HTMLElement>('[data-testid="notification-title"] span');
    const summary = panel.querySelector<HTMLElement>('[data-testid="notification-summary"]');
    if (!list || !header || !section || !title || !summary) {
      throw new Error('Notification typography or scroll anchors are missing');
    }

    const readText = (element: HTMLElement): TextMetrics => {
      const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
      let textNode = walker.nextNode();
      while (textNode && !textNode.textContent?.trim()) textNode = walker.nextNode();
      if (!textNode) throw new Error('Notification text was not measurable');
      const range = document.createRange();
      range.selectNodeContents(textNode);
      const style = getComputedStyle(element);
      return {
        fontSize: Number.parseFloat(style.fontSize),
        lineHeight: Number.parseFloat(style.lineHeight),
        glyphHeight: range.getBoundingClientRect().height,
        textSizeAdjust: style.getPropertyValue('text-size-adjust'),
        webkitTextSizeAdjust: style.getPropertyValue('-webkit-text-size-adjust'),
      };
    };

    const rect = panel.getBoundingClientRect();
    return {
      header: readText(header),
      section: readText(section),
      title: readText(title),
      summary: readText(summary),
      supportsTextAdjustment: {
        standard: CSS.supports('text-size-adjust', '100%'),
        webkit: CSS.supports('-webkit-text-size-adjust', '100%'),
      },
      panel: {
        top: rect.top,
        left: rect.left,
        right: rect.right,
        bottom: rect.bottom,
        width: rect.width,
        height: rect.height,
        scrollWidth: panel.scrollWidth,
        clientWidth: panel.clientWidth,
      },
      list: {
        scrollWidth: list.scrollWidth,
        clientWidth: list.clientWidth,
        scrollHeight: list.scrollHeight,
        clientHeight: list.clientHeight,
        scrollTop: list.scrollTop,
      },
      visualScale: window.visualViewport?.scale ?? 1,
      focusInside: panel.contains(document.activeElement),
    };
  });
}

function expectUsableDialog(metrics: NotificationMetrics, viewport: { width: number; height: number }): void {
  expect(metrics.panel.left).toBeGreaterThanOrEqual(-2);
  expect(metrics.panel.top).toBeGreaterThanOrEqual(-2);
  expect(metrics.panel.right).toBeLessThanOrEqual(viewport.width + 2);
  expect(metrics.panel.bottom).toBeLessThanOrEqual(viewport.height + 2);
  expect(metrics.panel.scrollWidth).toBeLessThanOrEqual(metrics.panel.clientWidth + 1);
  expect(metrics.list.scrollWidth).toBeLessThanOrEqual(metrics.list.clientWidth + 1);
  expect(metrics.list.scrollHeight).toBeGreaterThan(metrics.list.clientHeight);
  expect(metrics.focusInside).toBe(true);
  expect(metrics.visualScale).toBeCloseTo(1, 2);
  for (const text of [metrics.header, metrics.section, metrics.title, metrics.summary]) {
    expect(text.textSizeAdjust).toBe(metrics.supportsTextAdjustment.standard ? '100%' : '');
    expect(text.webkitTextSizeAdjust).toBe(metrics.supportsTextAdjustment.webkit ? '100%' : '');
    expect(text.glyphHeight).toBeGreaterThan(0);
  }
}

function expectReturnedTypography(baseline: NotificationMetrics, returned: NotificationMetrics): void {
  for (const key of ['header', 'section', 'title', 'summary'] as const) {
    expect(returned[key].fontSize).toBe(baseline[key].fontSize);
    expect(returned[key].lineHeight).toBe(baseline[key].lineHeight);
    expect(Math.abs(returned[key].glyphHeight - baseline[key].glyphHeight)).toBeLessThanOrEqual(MAX_RETURNED_GLYPH_DELTA_PX);
  }
  for (const key of ['top', 'left', 'width', 'height'] as const) {
    expect(Math.abs(returned.panel[key] - baseline.panel[key])).toBeLessThanOrEqual(MAX_RETURNED_PANEL_DELTA_PX);
  }
  expect(returned.visualScale).toBeCloseTo(baseline.visualScale, 2);
}
