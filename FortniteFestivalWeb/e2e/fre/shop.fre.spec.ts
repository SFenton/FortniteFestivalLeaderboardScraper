import { test, expect } from '../fixtures/fre';
import { goto } from '../fixtures/navigation';

const NARROW_BREAKPOINT = 420;

/*
 * Shop FRE — 5 slides:
 *   Always:   shop-overview, shop-views
 *   Gated (shopHighlightEnabled):   shop-highlighting, shop-new-items, shop-leaving-tomorrow
 *
 * NOTE: ShopPage hardcodes shopHighlightEnabled: true in its gate context,
 * so all 4 slides always show regardless of the disableShopHighlighting setting.
 * The highlighting setting only affects songs/songinfo page FRE slides.
 */

test.describe('Shop FRE', () => {

  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('fresh — shows all available slides', async ({ page, fre }) => {
    await goto(page, '/shop');
    await fre.waitForVisible();

    await fre.assertSlideCount(expectedSlideCount(page));
  });

  test('shop hardcodes shopHighlightEnabled — disableShopHighlighting has no effect', async ({ page, fre, freState }) => {
    await freState.setSettings({ disableShopHighlighting: true });
    await goto(page, '/shop');
    await fre.waitForVisible();

    await fre.assertSlideCount(expectedSlideCount(page));
  });
});

function expectedSlideCount(page: Parameters<typeof goto>[0]) {
  return (page.viewportSize()?.width ?? NARROW_BREAKPOINT) < NARROW_BREAKPOINT ? 4 : 5;
}
