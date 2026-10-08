import axe, { type AxeResults } from 'axe-core';
import type { Locator, Page } from '@playwright/test';
import { test, expect } from '../../fixtures/test';
import { createScrollableShopScenario } from '../../fixtures/scenarios';
import { dismissObstructions, gotoAppRoute } from '../../support/drivers/app';
import { isMobileProject, isPrimaryDesktopProject } from '../../support/projects';

declare global {
  interface Window {
    axe: {
      run(context?: string): Promise<AxeResults>;
    };
  }
}

test.use({ scenario: createScrollableShopScenario() });

/* Touch-target floor shared with the native apps (Apple 44 pt). */
const MIN_TARGET_PX = 44;
/* The shared modal Close button predates the Item Shop sort; hold it to WCAG 2.5.8. */
const MIN_SHARED_CLOSE_PX = 24;
const MODE_NAMES = ['Title', 'Artist', 'Year', 'Duration'];
const DIRECTION_NAMES = ['Ascending', 'Descending'];
const DIALOG_READING_ORDER = ['Close', ...MODE_NAMES, ...DIRECTION_NAMES, 'Reset', 'Apply Sort Changes'];
const blockingImpacts = new Set(['moderate', 'serious', 'critical']);

async function openShop(page: Page): Promise<void> {
  await gotoAppRoute(page, '/shop');
  await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  await dismissObstructions(page);
  await expect(page.getByRole('link', { name: /^Deterministic Song 10 / })).toBeVisible();
}

function headerSortPill(page: Page): Locator {
  return page.getByRole('region', { name: 'Page header' }).getByRole('button', { name: 'Sort', exact: true });
}

function sortDialog(page: Page): Locator {
  return page.getByRole('dialog', { name: 'Sort Item Shop' });
}

async function openSortFromFab(page: Page): Promise<Locator> {
  const fab = page.getByRole('button', { name: 'Actions', exact: true });
  await fab.click();
  const action = page.getByTestId('fab-menu').getByRole('button', { name: 'Sort Item Shop', exact: true });
  await expect(action).toBeVisible();
  await action.click();
  await expect(sortDialog(page)).toBeVisible();
  return fab;
}

async function expectMinTarget(locator: Locator, min: number): Promise<void> {
  const box = await locator.boundingBox();
  if (!box) throw new Error('target was not measurable');
  expect(box.width, 'target width').toBeGreaterThanOrEqual(min);
  expect(box.height, 'target height').toBeGreaterThanOrEqual(min);
}

async function focusedName(page: Page): Promise<string> {
  return page.evaluate(() => {
    const el = document.activeElement as HTMLElement | null;
    return el?.getAttribute('aria-label') ?? el?.textContent?.trim() ?? '';
  });
}

async function dialogButtonNames(dialog: Locator): Promise<string[]> {
  return dialog.getByRole('button').evaluateAll(buttons => buttons.map(
    button => button.getAttribute('aria-label') ?? button.textContent?.trim() ?? '',
  ));
}

async function expectSelection(dialog: Locator, mode: string, direction: string): Promise<void> {
  for (const name of MODE_NAMES) {
    await expect(dialog.getByRole('button', { name, exact: true })).toHaveAttribute('aria-pressed', String(name === mode));
  }
  for (const name of DIRECTION_NAMES) {
    await expect(dialog.getByRole('button', { name, exact: true })).toHaveAttribute('aria-pressed', String(name === direction));
  }
}

async function shopLinkTitles(page: Page, count: number): Promise<string[]> {
  const names = await page.getByRole('main').getByRole('link').evaluateAll(links => links.map(
    link => link.getAttribute('aria-label') ?? link.textContent ?? '',
  ));
  return names.slice(0, count).map(name => name.match(/Deterministic Song \d+|A Very Long Deterministic/)?.[0] ?? name);
}

/** Text inside the dialog must not be clipped or pushed outside it when the page is zoomed. */
async function expectNoClippedText(dialog: Locator): Promise<void> {
  const problems = await dialog.evaluate((root) => {
    const rootRect = root.getBoundingClientRect();
    const issues: string[] = [];
    if (root.scrollWidth > root.clientWidth + 1) issues.push(`dialog scrolls horizontally (${root.scrollWidth} > ${root.clientWidth})`);
    for (const el of Array.from(root.querySelectorAll<HTMLElement>('*'))) {
      const hasText = Array.from(el.childNodes).some(node => node.nodeType === Node.TEXT_NODE && node.textContent?.trim());
      if (!hasText) continue;
      const rect = el.getBoundingClientRect();
      const label = el.textContent?.trim().slice(0, 40);
      if (el.scrollWidth > el.clientWidth + 1 && getComputedStyle(el).overflowX !== 'visible') {
        issues.push(`clipped: ${label}`);
      }
      if (rect.left < rootRect.left - 1 || rect.right > rootRect.right + 1) {
        issues.push(`outside dialog: ${label}`);
      }
    }
    return issues;
  });
  expect(problems).toEqual([]);
}

test('desktop Item Shop sort is named, ordered, keyboard operable and returns focus', async ({ page, appState }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'desktop header pill is covered once');
  await page.setViewportSize({ width: 1280, height: 800 });
  await appState.reset();
  await appState.clearProfile();
  await openShop(page);

  const header = page.getByRole('region', { name: 'Page header' });
  await expect(header.getByRole('heading', { level: 1, name: 'Item Shop' })).toBeVisible();
  expect(await header.getByRole('button').evaluateAll(buttons => buttons.map(b => b.getAttribute('aria-label')))).toEqual(['Sort', 'List']);
  const pill = headerSortPill(page);
  await expectMinTarget(pill, MIN_TARGET_PX);
  expect(await shopLinkTitles(page, 3)).toEqual(['A Very Long Deterministic', 'Deterministic Song 10', 'Deterministic Song 11']);

  await pill.focus();
  await page.keyboard.press('Enter');
  const dialog = sortDialog(page);
  await expect(dialog).toBeVisible();
  await expect(dialog).toHaveCSS('opacity', '1');
  await expect(dialog.getByRole('heading', { name: 'Sort Item Shop' })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Close' })).toBeFocused();
  expect(await dialogButtonNames(dialog)).toEqual(DIALOG_READING_ORDER);
  await expectSelection(dialog, 'Title', 'Ascending');
  await expect(dialog.getByRole('button', { name: 'Apply Sort Changes' })).toBeDisabled();

  await expectMinTarget(dialog.getByRole('button', { name: 'Close' }), MIN_SHARED_CLOSE_PX);
  for (const name of [...MODE_NAMES, ...DIRECTION_NAMES, 'Reset', 'Apply Sort Changes']) {
    await expectMinTarget(dialog.getByRole('button', { name, exact: true }), MIN_TARGET_PX);
  }

  // Focus order follows the reading order; the disabled Apply button is skipped until there is a change.
  const tabbed: string[] = [];
  for (let i = 0; i < 8; i += 1) {
    await page.keyboard.press('Tab');
    tabbed.push(await focusedName(page));
  }
  expect(tabbed).toEqual(['Title', 'Artist', 'Year', 'Duration', 'Ascending', 'Descending', 'Reset', 'Close']);

  await page.addScriptTag({ content: axe.source });
  const results = await page.evaluate(() => window.axe.run('[role="dialog"]'));
  expect(results.violations.filter(v => v.impact && blockingImpacts.has(v.impact))).toEqual([]);

  // Choose Duration, descending, and apply with the keyboard alone.
  const duration = dialog.getByRole('button', { name: 'Duration', exact: true });
  await duration.focus();
  await page.keyboard.press('Enter');
  const descending = dialog.getByRole('button', { name: 'Descending', exact: true });
  await descending.focus();
  await page.keyboard.press('Space');
  await expectSelection(dialog, 'Duration', 'Descending');
  const apply = dialog.getByRole('button', { name: 'Apply Sort Changes' });
  await expect(apply).toBeEnabled();
  await page.keyboard.press('Tab');
  await page.keyboard.press('Tab');
  await expect(apply).toBeFocused();
  await page.keyboard.press('Enter');

  await expect(dialog).toBeHidden();
  await expect(pill).toBeFocused();
  // Reading order of the grid follows the chosen sort (longest first).
  await expect.poll(() => shopLinkTitles(page, 3)).toEqual(['Deterministic Song 12', 'Deterministic Song 11', 'Deterministic Song 10']);

  // Reopening reflects the saved choice, and Escape closes back to the pill.
  await page.keyboard.press('Enter');
  await expect(dialog).toBeVisible();
  await expectSelection(dialog, 'Duration', 'Descending');
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(pill).toBeFocused();
});

test('phone Item Shop sort action is named, large enough and opens the labelled dialog', async ({ page, appState }, testInfo) => {
  test.skip(!isMobileProject(testInfo.project.name), 'phone FAB action only');
  await page.setViewportSize({ width: 390, height: 844 });
  await appState.reset();
  await appState.clearProfile();
  await openShop(page);

  await expect(headerSortPill(page)).toHaveCount(0);
  const fab = page.getByRole('button', { name: 'Actions', exact: true });
  await expectMinTarget(fab, MIN_TARGET_PX);
  await fab.click();
  const action = page.getByTestId('fab-menu').getByRole('button', { name: 'Sort Item Shop', exact: true });
  await expect(action).toBeVisible();
  await expect(action).toHaveText('Sort Item Shop');
  // The menu scales open; measure once it has settled at full size.
  await expect(page.getByTestId('fab-menu')).toHaveCSS('transform', 'matrix(1, 0, 0, 1, 0, 0)');
  await expect.poll(async () => (await action.boundingBox())?.height ?? 0).toBeGreaterThanOrEqual(MIN_TARGET_PX);
  await expectMinTarget(action, MIN_TARGET_PX);

  await action.click();
  const dialog = sortDialog(page);
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Close' })).toBeFocused();
  expect(await dialogButtonNames(dialog)).toEqual(DIALOG_READING_ORDER);
  await expectSelection(dialog, 'Title', 'Ascending');
  for (const name of [...MODE_NAMES, ...DIRECTION_NAMES]) {
    await expectMinTarget(dialog.getByRole('button', { name, exact: true }), MIN_TARGET_PX);
  }

  await dialog.getByRole('button', { name: 'Year', exact: true }).click();
  await dialog.getByRole('button', { name: 'Descending', exact: true }).click();
  await expectSelection(dialog, 'Year', 'Descending');
  await dialog.getByRole('button', { name: 'Apply Sort Changes' }).click();
  await expect(dialog).toBeHidden();
  await expect.poll(() => shopLinkTitles(page, 2)).toEqual(['Deterministic Song 12', 'Deterministic Song 11']);
});

for (const zoom of [
  { label: '200% zoom', width: 640, height: 400 },
  { label: '320px reflow (400% zoom)', width: 320, height: 512 },
]) {
  test(`Item Shop sort stays readable and reachable at ${zoom.label}`, async ({ page, appState }, testInfo) => {
    test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'zoom reflow is covered once');
    // A 1280x800 window at this zoom level lays out at this CSS viewport.
    await page.setViewportSize({ width: zoom.width, height: zoom.height });
    await appState.reset();
    await appState.clearProfile();
    await openShop(page);

    const pill = headerSortPill(page);
    if (await pill.isVisible()) await pill.click();
    else await openSortFromFab(page);
    const dialog = sortDialog(page);
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveCSS('opacity', '1');
    await expectNoClippedText(dialog);

    for (const name of DIALOG_READING_ORDER) {
      const control = dialog.getByRole('button', { name, exact: true });
      await control.scrollIntoViewIfNeeded();
      await expect(control).toBeInViewport();
    }

    await dialog.getByRole('button', { name: 'Artist', exact: true }).click();
    await expectSelection(dialog, 'Artist', 'Ascending');
    const apply = dialog.getByRole('button', { name: 'Apply Sort Changes' });
    await apply.scrollIntoViewIfNeeded();
    await expect(apply).toBeInViewport();
    await apply.click();
    await expect(dialog).toBeHidden();
  });
}
