import type { Locator, Page, TestInfo } from '@playwright/test';
import { expect } from '@playwright/test';

export async function recordFocusEvents(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const state = window as Window & { __focusAppearanceEvents?: unknown[] };
    const events: unknown[] = [];
    state.__focusAppearanceEvents = events;
    const describe = (element: EventTarget | null) => {
      if (!(element instanceof Element)) return null;
      const style = getComputedStyle(element);
      return {
        tag: element.tagName,
        id: element.id,
        role: element.getAttribute('role'),
        label: element.getAttribute('aria-label'),
        testId: element.getAttribute('data-testid'),
        download: element instanceof HTMLAnchorElement ? element.download : undefined,
        disabled: element instanceof HTMLButtonElement ? element.disabled : undefined,
        connected: element.isConnected,
        focused: element === document.activeElement,
        focusVisible: element.matches(':focus-visible'),
        outlineStyle: style.outlineStyle,
        outlineWidth: style.outlineWidth,
        outlineColor: style.outlineColor,
        outlineOffset: style.outlineOffset,
        boxShadow: style.boxShadow,
        tapHighlight: style.getPropertyValue('-webkit-tap-highlight-color'),
      };
    };
    for (const type of ['pointerdown', 'pointerup', 'mousedown', 'mouseup', 'click', 'keydown', 'focusin', 'focusout']) {
      document.addEventListener(type, event => {
        events.push({
          type,
          time: performance.now(),
          quietFocus: document.documentElement.hasAttribute('data-fst-quiet-focus'),
          trusted: event.isTrusted,
          pointerType: event instanceof PointerEvent ? event.pointerType : undefined,
          key: event instanceof KeyboardEvent ? event.key : undefined,
          composing: event instanceof KeyboardEvent ? event.isComposing : undefined,
          detail: event instanceof MouseEvent ? event.detail : undefined,
          target: describe(event.target),
          active: describe(document.activeElement),
        });
        if (events.length > 300) events.shift();
      }, true);
    }
    window.addEventListener('pageshow', event => {
      events.push({ type: 'pageshow', persisted: event.persisted, active: describe(document.activeElement) });
    });
  });
}

export async function captureAppearance(locator: Locator, label: string, testInfo: TestInfo) {
  const appearance = await locator.evaluate(element => {
    const style = getComputedStyle(element);
    return {
      tag: element.tagName,
      id: element.id,
      role: element.getAttribute('role'),
      label: element.getAttribute('aria-label'),
      testId: element.getAttribute('data-testid'),
      disabled: element instanceof HTMLButtonElement ? element.disabled : undefined,
      connected: element.isConnected,
      focused: element === document.activeElement,
      focusVisible: element.matches(':focus-visible'),
      outlineStyle: style.outlineStyle,
      outlineWidth: style.outlineWidth,
      outlineColor: style.outlineColor,
      outlineOffset: style.outlineOffset,
      boxShadow: style.boxShadow,
      borderColor: style.borderColor,
      backgroundColor: style.backgroundColor,
      tapHighlight: style.getPropertyValue('-webkit-tap-highlight-color'),
      appearance: style.appearance,
    };
  });
  await testInfo.attach(`${label}.json`, {
    body: JSON.stringify(appearance, null, 2),
    contentType: 'application/json',
  });
  return appearance;
}

export async function expectSilentFocus(locator: Locator, label: string, testInfo: TestInfo): Promise<void> {
  const appearance = await captureAppearance(locator, label, testInfo);
  expect(appearance.connected, `${label}: cannot measure focus paint on a detached target`).toBe(true);
  const width = appearance.outlineStyle === 'none' ? 0 : Number.parseFloat(appearance.outlineWidth);
  expect.soft(width, `${label}: unexpected ${appearance.outlineStyle} ${appearance.outlineColor} focus outline`).toBe(0);
}

export async function expectVisibleFocus(locator: Locator): Promise<void> {
  await expect(locator).toBeFocused();
  await expect.poll(() => locator.evaluate(element => {
    const style = getComputedStyle(element);
    return element.matches(':focus-visible')
      && style.outlineStyle !== 'none'
      && Number.parseFloat(style.outlineWidth) > 0;
  })).toBe(true);
}

export async function activate(locator: Locator, touch: boolean): Promise<void> {
  if (touch) await locator.tap();
  else await locator.click();
}

export async function tabTo(page: Page, locator: Locator): Promise<void> {
  await expect(locator).toBeAttached();
  for (let attempt = 0; attempt < 80; attempt += 1) {
    if (await locator.evaluate(element => element === document.activeElement)) return;
    await page.keyboard.press('Tab');
  }
  await expect(locator).toBeFocused();
}

export async function holdModule(page: Page, pattern: string | RegExp): Promise<() => void> {
  let release!: () => void;
  const pending = new Promise<void>(resolve => { release = resolve; });
  await page.route(pattern, async route => {
    await pending;
    await route.continue();
  }, { times: 1 });
  return release;
}
