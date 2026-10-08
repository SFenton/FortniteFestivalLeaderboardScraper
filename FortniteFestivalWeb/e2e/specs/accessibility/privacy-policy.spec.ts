import axe, { type AxeResults } from 'axe-core';
import type { Locator, Page } from '@playwright/test';
import { test, expect } from '../../fixtures/test';
import { createPopulatedScenario } from '../../fixtures/scenarios';
import { dismissObstructions, gotoAppRoute } from '../../support/drivers/app';
import { isMobileProject, isPrimaryDesktopProject } from '../../support/projects';

declare global {
  interface Window {
    axe: {
      run(context?: string): Promise<AxeResults>;
    };
  }
}

test.use({ scenario: createPopulatedScenario() });

const blockingImpacts = new Set(['moderate', 'serious', 'critical']);
// The new Settings row meets the app's 44 px touch convention (Apple 44 pt, WCAG 2.5.5).
const ROW_MIN_TARGET_PX = 44;
// The shared ModalShell Close button is held to WCAG 2.2 AA target size (2.5.8).
const SHARED_CONTROL_MIN_TARGET_PX = 24;
const POLICY_HEADINGS = [
  'Information we collect',
  'How we use information',
  'Third parties and data sources',
  'Data retention',
  'Your choices and rights',
  'Children',
  'Security',
  'Changes to this policy',
  'Contact us',
];

test.beforeEach(async ({ appState }) => {
  await appState.reset();
});

test('Settings Privacy Policy row is a labelled dialog launcher in reading order with a large target', async ({ page }) => {
  await openSettings(page);
  const licenses = page.getByRole('link', { name: 'Licenses', exact: true });
  const privacy = privacyRow(page);
  await privacy.scrollIntoViewIfNeeded();

  await expect(privacy).toBeVisible();
  await expect(privacy).toHaveAttribute('href', /#\/settings\/privacy$/);
  await expect(privacy).toHaveAttribute('aria-haspopup', 'dialog');
  await expect(privacy).toContainText('What data Festival Score Tracker uses and your choices.');
  await expect(privacy.locator('svg')).toHaveAttribute('aria-hidden', 'true');

  expect(await followsInDocument(licenses, privacy)).toBe(true);
  const licensesBox = await requireBox(licenses);
  const privacyBox = await requireBox(privacy);
  expect(privacyBox.y).toBeGreaterThanOrEqual(licensesBox.y + licensesBox.height - 1);
  expect(privacyBox.height).toBeGreaterThanOrEqual(ROW_MIN_TARGET_PX);
  expect(privacyBox.width).toBeGreaterThanOrEqual(ROW_MIN_TARGET_PX);

  await licenses.focus();
  await page.keyboard.press('Tab');
  await expect(privacy).toBeFocused();
});

test('keyboard opens the Privacy Policy dialog, keeps focus inside, and returns focus to the row', async ({ page }) => {
  await openSettings(page);
  const privacy = privacyRow(page);
  await privacy.scrollIntoViewIfNeeded();
  await privacy.focus();
  await page.keyboard.press('Enter');

  await expect(page).toHaveURL(/#\/settings\/privacy$/);
  const dialog = page.getByRole('dialog', { name: 'Privacy Policy' });
  await expect(dialog).toBeVisible();
  await expect(dialog).toHaveAttribute('aria-modal', 'true');
  await expect(dialog.getByTestId('privacy-policy-content')).toBeVisible();
  await expect(page).toHaveTitle('Privacy Policy | Festival Score Tracker');

  const close = dialog.getByRole('button', { name: 'Close' });
  await expect(close).toBeFocused();
  const closeBox = await requireBox(close);
  expect(closeBox.width).toBeGreaterThanOrEqual(SHARED_CONTROL_MIN_TARGET_PX);
  expect(closeBox.height).toBeGreaterThanOrEqual(SHARED_CONTROL_MIN_TARGET_PX);

  const article = dialog.getByRole('article', { name: 'Privacy Policy' });
  const headings = article.getByRole('heading', { level: 3 });
  await expect(headings).toHaveText(POLICY_HEADINGS);
  for (const heading of POLICY_HEADINGS) {
    const region = article.getByRole('region', { name: heading });
    await expect(region).toHaveCount(1);
    expect(await followsInDocument(region.getByRole('heading', { name: heading }), region.locator('p, li').first())).toBe(true);
  }

  const links = article.getByRole('link');
  expect(await links.count()).toBeGreaterThan(0);
  for (const link of await links.all()) {
    await expect(link).toHaveAccessibleName(/^https:\/\//);
    await expect(link).toHaveAttribute('target', '_blank');
    await expect(link).toHaveAttribute('rel', /noopener/);
  }

  await close.press('Shift+Tab');
  await expect(links.last()).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(close).toBeFocused();
  await expect(page.locator('#root')).toHaveAttribute('inert', '');

  await expect(dialog).toHaveCSS('opacity', '1');
  await page.addScriptTag({ content: axe.source });
  const results = await page.evaluate(() => window.axe.run('[role="dialog"]'));
  expect(results.violations.filter(violation => (
    violation.impact && blockingImpacts.has(violation.impact)
  ))).toEqual([]);

  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(page).toHaveURL(/#\/settings$/);
  await expect(privacy).toBeFocused();
});

test('Privacy Policy reflows at a 320 px viewport (400% zoom) without clipping or horizontal scroll', async ({ page }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'zoom reflow is covered once');
  await page.setViewportSize({ width: 320, height: 640 });
  await openSettings(page);

  const privacy = privacyRow(page);
  await privacy.scrollIntoViewIfNeeded();
  await expect(privacy).toBeVisible();
  await expectNotClipped(privacy);
  const rowBox = await requireBox(privacy);
  expect(rowBox.x + rowBox.width).toBeLessThanOrEqual(320);
  expect(rowBox.height).toBeGreaterThanOrEqual(ROW_MIN_TARGET_PX);

  await privacy.click();
  const dialog = page.getByRole('dialog', { name: 'Privacy Policy' });
  await expect(dialog).toBeVisible();
  await expect(dialog).toHaveCSS('opacity', '1');
  const content = dialog.getByTestId('privacy-policy-content');
  await expectNotClipped(content);
  const close = dialog.getByRole('button', { name: 'Close' });
  await expect(close).toBeInViewport();

  const contact = dialog.getByRole('heading', { level: 3, name: 'Contact us' });
  await contact.scrollIntoViewIfNeeded();
  await expect(contact).toBeInViewport();
  const lastLink = dialog.getByRole('link').last();
  await lastLink.scrollIntoViewIfNeeded();
  await expect(lastLink).toBeInViewport();
  const linkBox = await requireBox(lastLink);
  expect(linkBox.x + linkBox.width).toBeLessThanOrEqual(320);
  await expect(close).toBeInViewport();
});

test('Privacy Policy opens and closes without decorative motion when reduced motion is requested', async ({ page }, testInfo) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await openSettings(page);
  const privacy = privacyRow(page);
  await privacy.scrollIntoViewIfNeeded();
  await expect(privacy).toBeVisible();
  await privacy.click();

  const dialog = page.getByRole('dialog', { name: 'Privacy Policy' });
  await expect(dialog).toBeVisible();
  await expect(dialog).toHaveCSS('opacity', '1');
  await expect.poll(() => infiniteAnimationCount(page)).toBe(0);
  if (isMobileProject(testInfo.project.name)) {
    await expect(dialog).toBeInViewport();
  }

  await dialog.getByRole('button', { name: 'Close' }).click();
  await expect(dialog).toBeHidden();
  await expect(page).toHaveURL(/#\/settings$/);
  await expect(privacy).toBeVisible();
});

function privacyRow(page: Page): Locator {
  return page.getByRole('link', { name: 'Privacy Policy', exact: true });
}

async function openSettings(page: Page): Promise<void> {
  await gotoAppRoute(page, '/settings');
  await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  await dismissObstructions(page);
  await expect(page).toHaveTitle('Settings | Festival Score Tracker', { timeout: 15_000 });
}

async function requireBox(locator: Locator): Promise<{ x: number; y: number; width: number; height: number }> {
  const box = await locator.boundingBox();
  expect(box).not.toBeNull();
  return box!;
}

async function followsInDocument(first: Locator, second: Locator): Promise<boolean> {
  const secondHandle = await second.elementHandle();
  return first.evaluate(
    (element, other) => Boolean(other && (element.compareDocumentPosition(other) & Node.DOCUMENT_POSITION_FOLLOWING)),
    secondHandle,
  );
}

async function expectNotClipped(locator: Locator): Promise<void> {
  const overflow = await locator.evaluate((element) => {
    const clipped: string[] = [];
    for (const node of [element, ...Array.from(element.querySelectorAll<HTMLElement>('*'))]) {
      if (node.scrollWidth > node.clientWidth + 1 && getComputedStyle(node).overflowX !== 'visible') {
        clipped.push(`${node.tagName.toLowerCase()}${node.getAttribute('data-testid') ? `[${node.getAttribute('data-testid')}]` : ''}`);
      }
    }
    return clipped;
  });
  expect(overflow).toEqual([]);
}

function infiniteAnimationCount(page: Page): Promise<number> {
  return page.evaluate(() => (
    document.getAnimations().filter((animation) => {
      const timing = animation.effect?.getComputedTiming();
      const target = animation.effect instanceof KeyframeEffect ? animation.effect.target : null;
      const name = target instanceof Element ? getComputedStyle(target).animationName.toLowerCase() : '';
      return timing?.iterations === Infinity && !name.includes('spin') && !name.includes('indeterminate');
    }).length
  ));
}
