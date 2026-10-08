import axe, { type AxeResults } from 'axe-core';
import type { Locator, Page } from '@playwright/test';
import { test, expect } from '../../fixtures/test';
import { createPopulatedScenario } from '../../fixtures/scenarios';
import { gotoAppRoute } from '../../support/drivers/app';
import { isPrimaryDesktopProject } from '../../support/projects';

declare global {
  interface Window {
    axe: {
      run(context?: string): Promise<AxeResults>;
    };
  }
}

// Section-header "View All" links and card "View all" CTAs on Rivals, Rival Detail and Compete (#321, #468).

test.use({ scenario: createPopulatedScenario() });

const MIN_TARGET_PX = 44;
const REFLOW_VIEWPORT = { width: 320, height: 640 };
const blockingImpacts = new Set(['moderate', 'serious', 'critical']);
const RIVALS_DETAIL_ROUTE = '/rivals/e2e-rival?name=Rival%20Player';

async function openRoute(page: Page, appState: { reset(): Promise<void>; selectPlayer(): Promise<void> }, route: string) {
  await appState.reset();
  await appState.selectPlayer();
  await gotoAppRoute(page, route);
}

function sectionOf(header: Locator): Locator {
  return header.locator('xpath=..');
}

async function expectMinTarget(locator: Locator) {
  await expect(locator).toBeVisible();
  const box = await locator.boundingBox();
  expect(box, 'target has a layout box').not.toBeNull();
  expect(box!.height).toBeGreaterThanOrEqual(MIN_TARGET_PX);
  expect(box!.width).toBeGreaterThanOrEqual(MIN_TARGET_PX);
}

async function expectNoBlockingAxeViolations(page: Page) {
  await page.addScriptTag({ content: axe.source });
  const results = await page.evaluate(() => window.axe.run('main'));
  expect(results.violations.filter(violation => (
    violation.impact && blockingImpacts.has(violation.impact)
  ))).toEqual([]);
}

/** The header's "View All" label and chevron stay inside the header and are not clipped. */
async function expectViewAllUnclipped(header: Locator) {
  const label = header.getByText('View All', { exact: true });
  await expect(label).toBeVisible();
  const metrics = await header.evaluate((element) => {
    const headerRect = element.getBoundingClientRect();
    const labelElement = Array.from(element.querySelectorAll('span'))
      .find(span => span.textContent === 'View All')!;
    const labelRect = labelElement.getBoundingClientRect();
    return {
      headerLeft: headerRect.left,
      headerRight: headerRect.right,
      labelLeft: labelRect.left,
      labelRight: labelRect.right,
      labelScrollWidth: labelElement.scrollWidth,
      labelClientWidth: labelElement.clientWidth || Math.ceil(labelRect.width),
      viewportWidth: document.documentElement.clientWidth,
    };
  });
  expect(metrics.labelLeft).toBeGreaterThanOrEqual(metrics.headerLeft - 0.5);
  expect(metrics.labelRight).toBeLessThanOrEqual(metrics.headerRight + 0.5);
  expect(metrics.headerRight).toBeLessThanOrEqual(metrics.viewportWidth + 0.5);
  expect(metrics.labelScrollWidth).toBeLessThanOrEqual(metrics.labelClientWidth + 1);
}

test.describe('Rivals View All headers and CTAs', () => {
  test('names, roles and reading order match the visible card', async ({ page, appState }) => {
    await openRoute(page, appState, '/rivals');
    const main = page.locator('main');

    const commonHeader = main.getByRole('button', { name: /^Common Rivals\s*View All$/ });
    const leadHeader = main.getByRole('button', { name: /^Lead Rivals\s*View All$/ });
    await expect(commonHeader).toBeVisible();
    await expect(leadHeader).toBeVisible();
    await expect(leadHeader).toHaveAttribute('tabindex', '0');
    await expect(main.getByText(/see all/i)).toHaveCount(0);

    const commonCta = main.getByRole('button', { name: 'View all rivals, Common Rivals', exact: true });
    const leadCta = main.getByRole('button', { name: 'View all rivals, Lead Rivals', exact: true });
    await expect(commonCta).toHaveText('View all rivals');
    await expect(leadCta).toHaveText('View all rivals');

    const ctaNames = await main.getByRole('button', { name: /^View all rivals/ }).evaluateAll(
      elements => elements.map(element => element.getAttribute('aria-label')),
    );
    expect(ctaNames.length).toBeGreaterThan(1);
    expect(new Set(ctaNames).size).toBe(ctaNames.length);

    const leadSectionButtons = sectionOf(leadHeader).getByRole('button');
    await expect(leadSectionButtons).toHaveCount(4);
    await expect(leadSectionButtons.nth(0)).toHaveAccessibleName(/^Lead Rivals\s*View All$/);
    await expect(leadSectionButtons.nth(1)).toHaveAccessibleName(/^Rival Player\b/);
    await expect(leadSectionButtons.nth(2)).toHaveAccessibleName(/^Chasing Player\b/);
    await expect(leadSectionButtons.nth(3)).toHaveAccessibleName('View all rivals, Lead Rivals');

    await expectMinTarget(commonHeader);
    await expectMinTarget(leadHeader);
    await expectMinTarget(leadCta);
    await expectNoBlockingAxeViolations(page);
  });

  test('keyboard reaches the header, rows and CTA in order and Enter opens the list', async ({ page, appState }, testInfo) => {
    test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'keyboard order is covered once');
    await openRoute(page, appState, '/rivals');
    const main = page.locator('main');
    const leadHeader = main.getByRole('button', { name: /^Lead Rivals\s*View All$/ });
    await expect(leadHeader).toBeVisible();

    await leadHeader.focus();
    await expect(leadHeader).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(main.locator(':focus')).toHaveAccessibleName(/^Rival Player\b/);
    await page.keyboard.press('Tab');
    await expect(main.locator(':focus')).toHaveAccessibleName(/^Chasing Player\b/);
    await page.keyboard.press('Tab');
    await expect(main.locator(':focus')).toHaveAccessibleName('View all rivals, Lead Rivals');

    await leadHeader.focus();
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/#\/rivals\/all\?category=Solo_Guitar$/);
  });

  test('headers reflow at 320 px without clipping View All', async ({ page, appState }) => {
    await page.setViewportSize(REFLOW_VIEWPORT);
    await openRoute(page, appState, '/rivals');
    const main = page.locator('main');
    for (const name of [/^Common Rivals\s*View All$/, /^Lead Rivals\s*View All$/, /^Pro Drums \+ Cymbals Rivals\s*View All$/]) {
      const header = main.getByRole('button', { name });
      await header.scrollIntoViewIfNeeded();
      await expectViewAllUnclipped(header);
      await expectMinTarget(header);
    }
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(0);
  });
});

test.describe('Rival Detail View All category headers', () => {
  const categoryHeader = /^Closest Battles\s*Songs where you and your rival are neck and neck\.\s*View All$/;

  test('category headers are named buttons that meet the target size', async ({ page, appState }) => {
    await openRoute(page, appState, RIVALS_DETAIL_ROUTE);
    const main = page.locator('main');
    const header = main.getByRole('button', { name: categoryHeader });
    await expect(header).toBeVisible();
    await expect(main.getByRole('button', { name: /View All$/ })).toHaveCount(4);
    await expect(main.getByText(/see all/i)).toHaveCount(0);

    const sectionButtons = sectionOf(header).getByRole('button');
    await expect(sectionButtons.first()).toHaveAccessibleName(categoryHeader);
    await expect(sectionButtons.nth(1)).toHaveAccessibleName(/^A Very Long Deterministic Festival Song Title\b/);

    for (const button of await main.getByRole('button', { name: /View All$/ }).all()) {
      await expectMinTarget(button);
    }
    await expectNoBlockingAxeViolations(page);
  });

  test('Enter on a category header opens that rivalry', async ({ page, appState }, testInfo) => {
    test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'keyboard activation is covered once');
    await openRoute(page, appState, RIVALS_DETAIL_ROUTE);
    const header = page.locator('main').getByRole('button', { name: categoryHeader });
    await header.focus();
    await expect(header).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/#\/rivals\/e2e-rival\/rivalry\?mode=closest_battles/);
  });

  test('category headers reflow at 320 px without clipping View All', async ({ page, appState }) => {
    await page.setViewportSize(REFLOW_VIEWPORT);
    await openRoute(page, appState, RIVALS_DETAIL_ROUTE);
    const header = page.locator('main').getByRole('button', { name: categoryHeader });
    await expectViewAllUnclipped(header);
    await expectMinTarget(header);
  });
});

test.describe('Compete View All headers and CTAs', () => {
  test('instrument headers name the instrument once and CTAs name their card', async ({ page, appState }) => {
    await openRoute(page, appState, '/compete');
    const main = page.locator('main');
    const leadHeaders = main.getByRole('button', { name: /^Lead\s*View All$/ });
    await expect(leadHeaders).toHaveCount(2);
    await expect(main.getByRole('button', { name: /^Lead Lead\b/ })).toHaveCount(0);

    const leaderboardsCta = main.getByRole('button', { name: 'View full leaderboards, Lead', exact: true });
    const rivalsCta = main.getByRole('button', { name: 'View all rivals, Lead', exact: true });
    await expect(leaderboardsCta).toHaveText('View full leaderboards');
    await expect(rivalsCta).toHaveText('View all rivals');

    const ctaNames = await main.getByRole('button', { name: /^View (full leaderboards|all rivals)/ }).evaluateAll(
      elements => elements.map(element => element.getAttribute('aria-label')),
    );
    expect(new Set(ctaNames).size).toBe(ctaNames.length);

    await expectMinTarget(leadHeaders.first());
    await expectMinTarget(leaderboardsCta);
    await expectMinTarget(rivalsCta);
  });
});
