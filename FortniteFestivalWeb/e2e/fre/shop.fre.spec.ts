import { test, expect } from '../fixtures/fre';
import { goto } from '../fixtures/navigation';

/*
 * Shop FRE — 4 slides:
 *   Always:   shop-overview, shop-views
 *   Gated (shopHighlightEnabled):   shop-highlighting, shop-leaving-tomorrow
 *
 * NOTE: ShopPage hardcodes shopHighlightEnabled: true in its gate context,
 * so all 4 slides always show regardless of the disableShopHighlighting setting.
 * The highlighting setting only affects songs/songinfo page FRE slides.
 */

test.describe('Shop FRE', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('fresh — shows all 4 slides', async ({ page, fre }) => {
    await goto(page, '/shop');
    await fre.waitForVisible();

    await fre.assertSlideCount(4);
  });

  test('shop hardcodes shopHighlightEnabled — disableShopHighlighting has no effect', async ({ page, fre, freState }) => {
    await freState.setSettings({ disableShopHighlighting: true });
    await goto(page, '/shop');
    await fre.waitForVisible();

    // Still shows all 4 because ShopPage gate is hardcoded
    await fre.assertSlideCount(4);
  });
});
