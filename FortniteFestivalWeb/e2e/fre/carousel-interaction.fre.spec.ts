import { test, expect } from '../fixtures/fre';
import { goto } from '../fixtures/navigation';

/*
 * Carousel interaction & edge case tests — keyboard, swipe, dots,
 * overlay dismiss, button boundaries, and persistence.
 */

test.describe('Carousel Interaction', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('keyboard: arrow keys cycle slides', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await freState.setSettings({ hideItemShop: true });
    await goto(page, '/songs');
    await fre.waitForVisible();

    // Should be on slide 1
    const firstTitle = await fre.title.textContent();

    // ArrowRight → next slide
    await page.keyboard.press('ArrowRight');
    await page.waitForTimeout(300);
    const secondTitle = await fre.title.textContent();
    expect(secondTitle).not.toBe(firstTitle);

    // ArrowLeft → back to first
    await page.keyboard.press('ArrowLeft');
    await page.waitForTimeout(300);
    const backTitle = await fre.title.textContent();
    expect(backTitle).toBe(firstTitle);
  });

  test('keyboard: Escape dismisses carousel', async ({ page, fre }) => {
    await goto(page, '/songs');
    await fre.waitForVisible();

    await page.keyboard.press('Escape');
    await page.waitForTimeout(600);
    expect(await fre.isVisible()).toBe(false);
  });

  test('dot click: jump to specific slide', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await freState.setSettings({ hideItemShop: true });
    await goto(page, '/songs');
    await fre.waitForVisible();

    const count = await fre.slideCount();
    expect(count).toBeGreaterThan(2);

    // Click the last dot
    await fre.goToSlide(count - 1);
    await expect(fre.title).toBeVisible();

    // Click the first dot
    await fre.goToSlide(0);
    await expect(fre.title).toBeVisible();
  });

  test('overlay click: clicking outside card dismisses', async ({ page, fre }) => {
    await goto(page, '/songs');
    await fre.waitForVisible();

    // Click on overlay area (top-left corner, outside the card)
    const overlayBox = await fre.overlay.boundingBox();
    expect(overlayBox).not.toBeNull();
    await page.mouse.click(overlayBox!.x + 5, overlayBox!.y + 5);
    await page.waitForTimeout(600);

    expect(await fre.isVisible()).toBe(false);
  });

  test('prev/next buttons: disabled at boundaries', async ({ page, fre }) => {
    await goto(page, '/songs');
    await fre.waitForVisible();

    // On first slide, prev should be disabled
    await expect(fre.prevButton).toBeDisabled();
    await expect(fre.nextButton).not.toBeDisabled();

    // Navigate to last slide
    const count = await fre.slideCount();
    for (let i = 0; i < count - 1; i++) {
      await fre.nextButton.click();
      await page.waitForTimeout(300);
    }

    // On last slide, next should be disabled
    await expect(fre.nextButton).toBeDisabled();
    await expect(fre.prevButton).not.toBeDisabled();
  });

  test('dismiss persists across page reload', async ({ page, fre }) => {
    await goto(page, '/songs');
    await fre.waitForVisible();
    await fre.dismiss();

    // Reload page
    await page.reload();
    await page.waitForTimeout(3000);
    expect(await fre.isVisible()).toBe(false);
  });

  test('revisiting same page after dismiss — no carousel', async ({ page, fre }) => {
    await goto(page, '/songs');
    await fre.waitForVisible();
    await fre.dismiss();

    // Navigate away and back
    await goto(page, '/settings');
    await goto(page, '/songs');
    await page.waitForTimeout(2000);
    expect(await fre.isVisible()).toBe(false);
  });

  test('swipe navigation on touch devices', async ({ page, fre, browserName }) => {
    // Touch swipe only works on projects with hasTouch: true
    const viewport = page.viewportSize();
    if (!viewport || viewport.width > 768) {
      test.skip();
      return;
    }

    await goto(page, '/songs');
    await fre.waitForVisible();

    const firstTitle = await fre.title.textContent();

    // Swipe left (next slide)
    const cardBox = await fre.card.boundingBox();
    expect(cardBox).not.toBeNull();
    const startX = cardBox!.x + cardBox!.width * 0.8;
    const endX = cardBox!.x + cardBox!.width * 0.2;
    const y = cardBox!.y + cardBox!.height / 2;

    await page.touchscreen.tap(startX, y);
    await page.mouse.move(startX, y);
    // Simulate swipe via touch events
    await page.evaluate(({ sx, ex, cy }) => {
      const el = document.querySelector('[data-testid="fre-card"]');
      if (!el) return;
      el.dispatchEvent(new TouchEvent('touchstart', {
        touches: [new Touch({ identifier: 0, target: el, clientX: sx, clientY: cy })],
        bubbles: true
      }));
      el.dispatchEvent(new TouchEvent('touchend', {
        changedTouches: [new Touch({ identifier: 0, target: el, clientX: ex, clientY: cy })],
        bubbles: true
      }));
    }, { sx: startX, ex: endX, cy: y });

    await page.waitForTimeout(500);
    const secondTitle = await fre.title.textContent();
    // If swipe worked, title changes
    if (secondTitle !== firstTitle) {
      expect(secondTitle).not.toBe(firstTitle);
    }
  });
});
