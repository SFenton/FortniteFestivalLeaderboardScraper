import axe, { type AxeResults } from 'axe-core';
import type { Page } from '@playwright/test';
import { test, expect } from '../../fixtures/test';
import { createPopulatedScenario } from '../../fixtures/scenarios';
import { dismissObstructions, gotoAppRoute } from '../../support/drivers/app';

declare global {
  interface Window {
    axe: {
      run(context?: string): Promise<AxeResults>;
    };
  }
}

test.use({ scenario: createPopulatedScenario() });

const blockingImpacts = new Set(['moderate', 'serious', 'critical']);
const VERSION_LIST = '[data-testid="settings-version-list"]';
const EXPECTED_TERMS = ['App Version', 'Service Version', '@festival/core Version', '@festival/theme Version'];

async function openVersionSection(page: Page) {
  await gotoAppRoute(page, '/settings');
  await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  await dismissObstructions(page);
  const list = page.getByTestId('settings-version-list');
  await list.scrollIntoViewIfNeeded();
  await expect(list).toBeVisible();
  return list;
}

test.beforeEach(async ({ appState }) => {
  await appState.reset();
  await appState.selectPlayer();
});

test('Settings version rows expose labelled term/definition pairs in reading order', async ({ page }) => {
  const list = await openVersionSection(page);

  const terms = list.getByRole('term');
  const definitions = list.getByRole('definition');
  await expect(terms).toHaveText(EXPECTED_TERMS);
  await expect(definitions).toHaveCount(EXPECTED_TERMS.length);
  await expect(definitions.first()).toHaveText(/^\d+\.\d+\.\d+(?: · [0-9a-f]{7})?$/);
  await expect(list.locator('a, button, input, select, textarea, [tabindex]')).toHaveCount(0);

  const order = await page.locator(VERSION_LIST).evaluate(dl => Array.from(dl.children).map(row => (
    Array.from(row.children).map(child => child.tagName)
  )));
  expect(order).toEqual(EXPECTED_TERMS.map(() => ['DT', 'DD']));

  await page.addScriptTag({ content: axe.source });
  const results = await page.evaluate(selector => window.axe.run(selector), VERSION_LIST);
  expect(results.violations.filter(violation => (
    violation.impact && blockingImpacts.has(violation.impact)
  ))).toEqual([]);
});

test('Settings version rows reflow at 320 CSS px with doubled text size', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 640 });
  await openVersionSection(page);
  await page.addStyleTag({
    content: `${VERSION_LIST} > div { font-size: calc(var(--font-md) * 2) !important; }`,
  });

  const geometry = await page.locator(VERSION_LIST).evaluate(dl => {
    const listRect = dl.getBoundingClientRect();
    const rows = Array.from(dl.children).map(row => {
      const [term, definition] = Array.from(row.children) as HTMLElement[];
      const termRect = term!.getBoundingClientRect();
      const definitionRect = definition!.getBoundingClientRect();
      return {
        fontSize: parseFloat(getComputedStyle(row).fontSize),
        rowOverflow: row.scrollWidth - row.clientWidth,
        termInside: termRect.left >= listRect.left - 0.5 && termRect.right <= listRect.right + 0.5,
        definitionInside: definitionRect.left >= listRect.left - 0.5 && definitionRect.right <= listRect.right + 0.5,
        definitionFollowsTerm: definitionRect.top >= termRect.top - 0.5
          && (definitionRect.top >= termRect.bottom - 0.5 || definitionRect.left >= termRect.right - 0.5),
      };
    });
    return { listOverflow: dl.scrollWidth - dl.clientWidth, rows };
  });

  expect(geometry.listOverflow).toBeLessThanOrEqual(0);
  for (const row of geometry.rows) {
    expect(row.fontSize).toBeGreaterThanOrEqual(28);
    expect(row.rowOverflow).toBeLessThanOrEqual(0);
    expect(row.termInside).toBe(true);
    expect(row.definitionInside).toBe(true);
    expect(row.definitionFollowsTerm).toBe(true);
  }
});
