import { type Page } from '@playwright/test';
import { test, expect } from '../../fixtures/test';
import { createScrollableShopScenario } from '../../fixtures/scenarios';
import { gotoAppRoute } from '../../support/drivers/app';
import { isMobileProject, isPrimaryDesktopProject } from '../../support/projects';

const SHOP_ITEM_SELECTOR = '[data-testid="scroll-area"] a[href^="https://example.invalid/shop/"]';
const EXPECTED_DESKTOP_FAB_CLEARANCE_PX = 96;

test.use({ scenario: createScrollableShopScenario() });

test('narrow Item Shop omits clearance when no mobile FAB is rendered', async ({ page, appState }, testInfo) => {
  test.skip(!isMobileProject(testInfo.project.name), 'mobile handset geometry only');
  await page.setViewportSize({ width: 390, height: 844 });
  await appState.reset();
  await appState.clearProfile();
  await gotoAppRoute(page, '/shop');

  await expect(page.getByTestId('mobile-fab')).toHaveCount(0);
  const geometry = await scrollToLastShopItem(page);

  expect(geometry.maxScroll).toBeGreaterThan(0);
  expect(geometry.distanceFromBottom).toBeLessThanOrEqual(1);
  expect(Math.abs(geometry.trailingGap - geometry.listPaddingBottom)).toBeLessThanOrEqual(2);
});

test('narrow Item Shop keeps clearance for the selected-band FAB', async ({ page, appState }, testInfo) => {
  test.skip(!isMobileProject(testInfo.project.name), 'mobile handset geometry only');
  await page.setViewportSize({ width: 390, height: 844 });
  await appState.reset();
  await appState.selectBand();
  await gotoAppRoute(page, '/shop');

  await expect(page.getByTestId('mobile-fab').locator('button').first()).toBeVisible();
  const geometry = await scrollToLastShopItem(page);

  expect(geometry.maxScroll).toBeGreaterThan(0);
  if (geometry.fabTop == null) throw new Error('Selected-band FAB button was not measurable');
  expect(geometry.lastItemBottom).toBeLessThanOrEqual(geometry.fabTop - 1);
});

test('Item Shop keeps clearance at the view-toggle breakpoint', async ({ page, appState }, testInfo) => {
  test.skip(!isMobileProject(testInfo.project.name), 'mobile handset geometry only');
  await page.setViewportSize({ width: 430, height: 844 });
  await appState.reset();
  await appState.clearProfile();
  await gotoAppRoute(page, '/shop');

  await expect(page.getByTestId('mobile-fab').locator('button').first()).toBeVisible();
  const geometry = await scrollToLastShopItem(page);

  expect(geometry.maxScroll).toBeGreaterThan(0);
  if (geometry.fabTop == null) throw new Error('View-toggle FAB button was not measurable');
  expect(geometry.lastItemBottom).toBeLessThanOrEqual(geometry.fabTop - 1);
});

test('desktop Item Shop retains its existing trailing FAB clearance', async ({ page, appState }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'desktop preservation check runs once');
  await page.setViewportSize({ width: 1280, height: 800 });
  await appState.reset();
  await appState.clearProfile();
  await gotoAppRoute(page, '/shop');

  await expect(page.getByTestId('bottom-nav-songs')).toHaveCount(0);
  const spacerHeight = await page.locator('[data-testid="scroll-area"] > :last-child').evaluate(
    element => Number.parseFloat(getComputedStyle(element).height),
  );
  expect(spacerHeight).toBe(EXPECTED_DESKTOP_FAB_CLEARANCE_PX);
});

async function scrollToLastShopItem(page: Page) {
  const items = page.locator(SHOP_ITEM_SELECTOR);
  await expect(items).toHaveCount(12);
  const lastItem = items.last();
  await expect(lastItem).toBeVisible();
  await lastItem.evaluate(async element => {
    await Promise.all(element.getAnimations().map(animation => animation.finished.catch(() => undefined)));
  });

  await page.locator('#main-content').evaluate(mainContent => {
    let scrollContainer = mainContent.parentElement;
    while (scrollContainer) {
      const overflowY = getComputedStyle(scrollContainer).overflowY;
      if (overflowY === 'auto' || overflowY === 'scroll') break;
      scrollContainer = scrollContainer.parentElement;
    }
    if (!scrollContainer) throw new Error('Scrollable shell container was not found');
    scrollContainer.scrollTop = scrollContainer.scrollHeight;
  });
  await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => resolve())));

  return lastItem.evaluate(element => {
    const list = element.parentElement;
    const bottomNavButton = document.querySelector<HTMLElement>('[data-testid="bottom-nav-songs"]');
    const bottomNav = bottomNavButton?.closest('nav');
    const mainContent = document.querySelector<HTMLElement>('#main-content');
    if (!list || !bottomNav || !mainContent) throw new Error('Item Shop geometry anchors are missing');

    let scrollContainer = mainContent.parentElement;
    while (scrollContainer) {
      const overflowY = getComputedStyle(scrollContainer).overflowY;
      if (overflowY === 'auto' || overflowY === 'scroll') break;
      scrollContainer = scrollContainer.parentElement;
    }
    if (!scrollContainer) throw new Error('Scrollable shell container was not found');
    const listStyle = getComputedStyle(list);
    const lastItemRect = element.getBoundingClientRect();
    const fabButton = document.querySelector<HTMLElement>('[data-testid="mobile-fab"] button');

    return {
      maxScroll: scrollContainer.scrollHeight - scrollContainer.clientHeight,
      distanceFromBottom: scrollContainer.scrollHeight - scrollContainer.clientHeight - scrollContainer.scrollTop,
      trailingGap: bottomNav.getBoundingClientRect().top - lastItemRect.bottom,
      listPaddingBottom: Number.parseFloat(listStyle.paddingBottom),
      lastItemBottom: lastItemRect.bottom,
      fabTop: fabButton?.getBoundingClientRect().top ?? null,
    };
  });
}
