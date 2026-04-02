import { test, expect } from '@playwright/test';

// Scroll tests are designed for desktop viewports only
test.beforeEach(async ({ page }, testInfo) => {
  if (testInfo.project.name !== 'desktop') {
    test.skip();
  }
  // Disable FRE so the carousel doesn't interfere with scroll tests
  await page.goto('/');
  await page.evaluate(() => localStorage.setItem('fst:featureFlagOverrides', JSON.stringify({ firstRun: false })));
});

async function dismissOverlays(page: any) {
  for (let attempt = 0; attempt < 5; attempt++) {
    const xBtn = page.locator('button svg, [role="button"] svg').first();
    if (await xBtn.count() > 0) {
      const box = await xBtn.boundingBox();
      if (box) {
        await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
        await page.waitForTimeout(800);
      }
    } else break;
    const hasFixed = await page.evaluate(() => {
      for (const el of document.querySelectorAll('*')) {
        const cs = getComputedStyle(el);
        if (cs.position === 'fixed' && cs.pointerEvents !== 'none' &&
            (el as HTMLElement).offsetHeight > 400 && cs.zIndex !== 'auto' &&
            parseInt(cs.zIndex) > 0) return true;
      }
      return false;
    });
    if (!hasFixed) break;
  }
}

test('scroll works at 1280px (narrow desktop)', async ({ page }) => {
  await page.goto('/#/songs');
  await page.waitForTimeout(3000);
  await dismissOverlays(page);

  const pageRoot = page.locator('[data-testid="page-root"]');
  const state = await pageRoot.evaluate((el: HTMLElement) => ({
    scrollHeight: el.scrollHeight,
    clientHeight: el.clientHeight,
    canScroll: el.scrollHeight > el.clientHeight,
    overflowY: getComputedStyle(el).overflowY,
  }));
  console.log('page-root state:', JSON.stringify(state));
  expect(state.canScroll).toBe(true);
  expect(state.overflowY).toBe('auto');

  // Mouse wheel on the page header area (not over content)
  const box = await pageRoot.boundingBox();
  if (box) {
    // Wheel near the top (over header area)
    await page.mouse.move(box.x + box.width / 2, box.y + 30);
    await page.mouse.wheel(0, 500);
    await page.waitForTimeout(300);
  }
  const afterWheel = await pageRoot.evaluate((el: HTMLElement) => el.scrollTop);
  console.log(`Mouse wheel over header area: scrollTop=${afterWheel}`);
  expect(afterWheel).toBeGreaterThan(0);
});

test('scroll works at 1920px (wide desktop)', async ({ page }) => {
  await page.setViewportSize({ width: 1920, height: 900 });
  await page.goto('/#/songs');
  await page.waitForTimeout(3000);
  await dismissOverlays(page);

  const pageRoot = page.locator('[data-testid="page-root"]');
  const state = await pageRoot.evaluate((el: HTMLElement) => ({
    scrollHeight: el.scrollHeight,
    clientHeight: el.clientHeight,
    canScroll: el.scrollHeight > el.clientHeight,
    overflowY: getComputedStyle(el).overflowY,
  }));
  console.log('Wide page-root state:', JSON.stringify(state));
  expect(state.canScroll).toBe(true);

  // Mouse wheel over the sidebar area (left of content)
  const box = await pageRoot.boundingBox();
  if (box) {
    await page.mouse.move(box.x + 50, box.y + box.height / 2);
    await page.mouse.wheel(0, 500);
    await page.waitForTimeout(300);
  }
  const afterWheel = await pageRoot.evaluate((el: HTMLElement) => el.scrollTop);
  console.log(`Wide desktop wheel over sidebar: scrollTop=${afterWheel}`);
  expect(afterWheel).toBeGreaterThan(0);
});
