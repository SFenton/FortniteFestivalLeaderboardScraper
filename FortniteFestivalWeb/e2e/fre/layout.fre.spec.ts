import { test, expect } from '../fixtures/fre';
import { goto } from '../fixtures/navigation';

/*
 * Layout integrity tests — verify no clipping, overflow, or missing elements
 * at various viewport sizes including vertical shrinking.
 */

test.describe('Layout FRE', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('desktop 1280×800 — carousel card fully visible', async ({ page, fre }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await goto(page, '/songs');
    await fre.waitForVisible();

    const box = await fre.card.boundingBox();
    expect(box).not.toBeNull();
    // Card should be fully within viewport
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.y).toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width).toBeLessThanOrEqual(1280);
    expect(box!.y + box!.height).toBeLessThanOrEqual(800);
  });

  test('desktop narrow height 1280×400 — carousel adapts, no clipping', async ({ page, fre }) => {
    await page.setViewportSize({ width: 1280, height: 400 });
    await goto(page, '/songs');
    await fre.waitForVisible();

    const box = await fre.card.boundingBox();
    expect(box).not.toBeNull();
    // Card must fit vertically
    expect(box!.y).toBeGreaterThanOrEqual(0);
    expect(box!.y + box!.height).toBeLessThanOrEqual(400);

    // Close button and dots must be visible
    await expect(fre.closeButton).toBeVisible();
    await expect(fre.dots.first()).toBeVisible();
  });

  test('desktop very narrow 1280×300 — controls still accessible', async ({ page, fre }) => {
    await page.setViewportSize({ width: 1280, height: 300 });
    await goto(page, '/songs');
    await fre.waitForVisible();

    // At minimum, close and dots should be visible/clickable
    await expect(fre.closeButton).toBeVisible();
    const dotsVisible = await fre.dots.first().isVisible();
    expect(dotsVisible).toBe(true);
  });

  test('mobile 375×812 — no horizontal overflow', async ({ page, fre }) => {
    await page.setViewportSize({ width: 375, height: 812 });
    await goto(page, '/songs');
    await fre.waitForVisible();

    const box = await fre.card.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width).toBeLessThanOrEqual(375);
  });

  test('mobile narrow height 375×500 — carousel adapts', async ({ page, fre }) => {
    await page.setViewportSize({ width: 375, height: 500 });
    await goto(page, '/songs');
    await fre.waitForVisible();

    const box = await fre.card.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.y + box!.height).toBeLessThanOrEqual(500);
    await expect(fre.closeButton).toBeVisible();
  });

  test('mobile landscape 812×375 — carousel visible and functional', async ({ page, fre }) => {
    await page.setViewportSize({ width: 812, height: 375 });
    await goto(page, '/songs');
    await fre.waitForVisible();

    // Card should be visible and controls accessible even at minimal height
    await expect(fre.closeButton).toBeVisible();
    await expect(fre.dots.first()).toBeVisible();
    const box = await fre.card.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.x + box!.width).toBeLessThanOrEqual(812);
  });

  test('dynamic resize: 1280×800 → 1280×400 — carousel adjusts live', async ({ page, fre }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await goto(page, '/songs');
    await fre.waitForVisible();

    const bigBox = await fre.card.boundingBox();
    expect(bigBox).not.toBeNull();

    // Shrink viewport
    await page.setViewportSize({ width: 1280, height: 400 });
    await page.waitForTimeout(500); // ResizeObserver debounce

    const smallBox = await fre.card.boundingBox();
    expect(smallBox).not.toBeNull();
    expect(smallBox!.y + smallBox!.height).toBeLessThanOrEqual(400);
  });

  test('overlay covers full viewport — desktop', async ({ page, fre }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await goto(page, '/songs');
    await fre.waitForVisible();

    const overlayBox = await fre.overlay.boundingBox();
    expect(overlayBox).not.toBeNull();
    // Overlay should span the whole viewport
    expect(overlayBox!.width).toBeGreaterThanOrEqual(1280 - 1);
    expect(overlayBox!.height).toBeGreaterThanOrEqual(800 - 1);
  });

  test('overlay covers full viewport — mobile', async ({ page, fre }) => {
    await page.setViewportSize({ width: 375, height: 812 });
    await goto(page, '/songs');
    await fre.waitForVisible();

    const overlayBox = await fre.overlay.boundingBox();
    expect(overlayBox).not.toBeNull();
    expect(overlayBox!.width).toBeGreaterThanOrEqual(375 - 1);
    expect(overlayBox!.height).toBeGreaterThanOrEqual(812 - 1);
  });

  test('pagination dots stay in single row (songs page, 8 dots)', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await page.setViewportSize({ width: 375, height: 812 });
    await goto(page, '/songs');
    await fre.waitForVisible();

    // All dots should have the same Y coordinate (single row)
    const dotsWrap = page.locator('[data-testid="fre-dots"]');
    const wrapBox = await dotsWrap.boundingBox();
    expect(wrapBox).not.toBeNull();

    // The wrap height should be small (single row of dots, not wrapped)
    // Dots are typically ~8px, so wrap should be under 30px
    expect(wrapBox!.height).toBeLessThan(30);
  });
});
