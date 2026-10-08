import { test, expect, type Locator, type Page } from '@playwright/test';

const STORY = 'pages/suggestions/components/SuggestionsMixLimit/AtLimit';
const LIMIT_MESSAGE = "You've reached 1,000 suggestions in this mix.";
const WCAG_MIN_TARGET_PX = 24;

test('fresh-mix control is a described button operable from the keyboard with visible focus', async ({ mount, page }) => {
  const component = await mount(STORY);
  const button = component.getByRole('button', { name: 'Start a new mix', exact: true });
  const started = component.getByTestId('started-mixes');

  await expect(button).toHaveAccessibleDescription(LIMIT_MESSAGE);
  await expect(button).toHaveAttribute('type', 'button');

  await page.keyboard.press('Tab');
  await expect(button).toBeFocused();
  expect(await button.evaluate(element => element.matches(':focus-visible'))).toBe(true);
  const focusRing = await button.evaluate((element) => {
    const style = getComputedStyle(element);
    return { outlineStyle: style.outlineStyle, outlineWidth: parseFloat(style.outlineWidth), boxShadow: style.boxShadow };
  });
  expect(
    (focusRing.outlineStyle !== 'none' && focusRing.outlineWidth > 0) || focusRing.boxShadow !== 'none',
  ).toBe(true);

  await page.keyboard.press('Enter');
  await expect(started).toHaveText('1');
  await page.keyboard.press('Space');
  await expect(started).toHaveText('2');
  await expect(button).toBeFocused();
});

test('fresh-mix control keeps a WCAG target size and reflows at 320px without clipping', async ({ mount, page }) => {
  const component = await mount(STORY);
  const button = component.getByRole('button', { name: 'Start a new mix', exact: true });
  const message = component.getByText(LIMIT_MESSAGE, { exact: true });

  await expectTargetSize(button);

  await page.setViewportSize({ width: 320, height: 640 });
  await expectTargetSize(button);
  await expectInsideViewport(page, button);
  await expectNotClipped(button);
  await expectNotClipped(message);
  await expectNoHorizontalOverflow(page);
});

test('fresh-mix control survives 200% zoom and WCAG text spacing', async ({ mount, page }) => {
  await page.setViewportSize({ width: 640, height: 800 });
  const component = await mount(STORY);
  const button = component.getByRole('button', { name: 'Start a new mix', exact: true });
  const message = component.getByText(LIMIT_MESSAGE, { exact: true });
  const baseHeight = (await button.boundingBox())!.height;

  await page.addStyleTag({
    content: `
      html { zoom: 2; }
      * {
        line-height: 1.5 !important;
        letter-spacing: 0.12em !important;
        word-spacing: 0.16em !important;
      }
      p, div { margin-bottom: 2em; }
    `,
  });

  await expect.poll(async () => (await button.boundingBox())!.height).toBeGreaterThan(baseHeight);
  await expectTargetSize(button);
  await expectInsideViewport(page, button);
  await expectNotClipped(button);
  await expectNotClipped(message);
  await expectNoHorizontalOverflow(page);
  await button.focus();
  await page.keyboard.press('Enter');
  await expect(component.getByTestId('started-mixes')).toHaveText('1');
});

async function expectTargetSize(target: Locator) {
  const box = await target.boundingBox();
  expect(box).not.toBeNull();
  expect(box!.width).toBeGreaterThanOrEqual(WCAG_MIN_TARGET_PX);
  expect(box!.height).toBeGreaterThanOrEqual(WCAG_MIN_TARGET_PX);
}

async function expectInsideViewport(page: Page, target: Locator) {
  const box = (await target.boundingBox())!;
  const viewport = await page.evaluate(() => ({ width: window.innerWidth, height: document.documentElement.scrollHeight }));
  expect(box.x).toBeGreaterThanOrEqual(0);
  expect(box.x + box.width).toBeLessThanOrEqual(viewport.width + 0.5);
}

async function expectNotClipped(target: Locator) {
  const overflow = await target.evaluate(element => ({
    horizontal: element.scrollWidth - element.clientWidth,
    vertical: element.scrollHeight - element.clientHeight,
  }));
  expect(overflow.horizontal).toBeLessThanOrEqual(1);
  expect(overflow.vertical).toBeLessThanOrEqual(1);
}

async function expectNoHorizontalOverflow(page: Page) {
  const overflow = await page.evaluate(() => (
    document.documentElement.scrollWidth - document.documentElement.clientWidth
  ));
  expect(overflow).toBeLessThanOrEqual(0);
}
