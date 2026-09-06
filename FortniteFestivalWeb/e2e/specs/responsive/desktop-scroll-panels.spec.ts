import type { Page } from '@playwright/test';
import axe from 'axe-core';
import { test, expect } from '../../fixtures/test';
import { createDesktopScrollScenario, E2E_PLAYER, E2E_QUAD_BAND } from '../../fixtures/scenarios';
import { dismissObstructions, gotoAppRoute } from '../../support/drivers/app';
import { isMobileProject, WIDE_PROJECT } from '../../support/projects';

const MAIN = '[data-testid="app-scroll-container"]';
const NAVIGATION = '[data-testid="pinned-navigation"]';
const UTILITIES = '[data-testid="pinned-utilities"]';
const SETTINGS_LINKS = '[data-testid="settings-quick-links-rail"] nav';
const SONG_LINKS = '[data-testid="songs-quick-links-rail"] nav';
const desktopOwners = new Set([WIDE_PROJECT, 'webkit-desktop', 'firefox-desktop']);

type WheelProbe = {
  element: HTMLElement;
  original: HTMLElement['scrollBy'];
  descriptor: PropertyDescriptor | undefined;
  calls: number;
  events: number;
  trusted: boolean;
  listener: (event: WheelEvent) => void;
};
type RevealSample = { pointer: string; childPointer: string; opacity: number };

declare global {
  interface Window {
    __fstDesktopWheelProbe?: WheelProbe;
    __fstDesktopRevealSamples?: RevealSample[];
  }
}

test.use({ scenario: createDesktopScrollScenario() });

async function ready(page: Page, route: string, rail?: string) {
  await gotoAppRoute(page, route);
  await expect(page.locator(MAIN)).toBeVisible();
  await expect(page.getByTestId('page-root')).toBeVisible();
  if (rail) {
    await expect(page.getByTestId(`${rail}-quick-links-rail`)).toHaveCSS('opacity', '1');
    await expect(page.getByTestId(`${rail}-quick-links-rail`)).toHaveCSS('pointer-events', 'auto');
  }
  await dismissObstructions(page);
  await stableScroll(page);
}

async function geometry(page: Page, selector: string) {
  return page.locator(selector).evaluate(element => {
    const rect = element.getBoundingClientRect();
    return {
      x: rect.x, y: rect.y, width: rect.width, height: rect.height,
      top: rect.top, bottom: rect.bottom,
      viewportTop: rect.top + element.clientTop,
      viewportBottom: rect.top + element.clientTop + element.clientHeight,
      clientTop: element.clientTop,
      clientHeight: element.clientHeight,
      scrollHeight: element.scrollHeight,
      scrollTop: element.scrollTop,
      pointerEvents: getComputedStyle(element).pointerEvents,
    };
  });
}

async function stableScroll(page: Page) {
  await page.locator(MAIN).evaluate(async element => {
    await new Promise<void>((resolve, reject) => {
      let previous = '';
      let frames = 0;
      const started = performance.now();
      const tick = () => {
        const value = `${element.scrollTop}|${element.clientTop}|${element.clientHeight}`;
        frames = value === previous ? frames + 1 : 0;
        previous = value;
        if (frames >= 18) return resolve();
        if (performance.now() - started > 6000) return reject(new Error('Scroll geometry did not settle'));
        requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    });
  });
}

async function resetMain(page: Page) {
  await page.locator(MAIN).evaluate(element => {
    const maximum = element.scrollHeight - element.clientHeight;
    if (maximum < 600) throw new Error('Expected a genuinely scrollable page');
    element.scrollTo({ top: Math.min(700, maximum / 2), behavior: 'instant' });
  });
  await stableScroll(page);
}

async function wheel(page: Page, point: { x: number; y: number }, delta: number, panel?: string) {
  await stableScroll(page);
  await page.mouse.move(point.x, point.y);
  const before = await geometry(page, MAIN);
  const panelBefore = panel ? await geometry(page, panel) : null;
  const hit = await page.evaluate(({ point, main }) => {
    const target = document.elementFromPoint(point.x, point.y);
    const scroller = document.querySelector(main)!;
    return {
      main: target === scroller || scroller.contains(target),
      panel: target?.closest('[data-testid="pinned-navigation"],[data-testid="pinned-utilities"],[data-testid$="-quick-links-rail"]')?.getAttribute('data-testid'),
    };
  }, { point, main: MAIN });
  await page.evaluate(selector => {
    const element = document.querySelector<HTMLElement>(selector)!;
    const probe: WheelProbe = {
      element,
      original: element.scrollBy,
      descriptor: Object.getOwnPropertyDescriptor(element, 'scrollBy'),
      calls: 0,
      events: 0,
      trusted: true,
      listener: event => {
        probe.events += 1;
        probe.trusted &&= event.isTrusted;
      },
    };
    element.scrollBy = function (...args: unknown[]) {
      probe.calls += 1;
      Reflect.apply(probe.original, this, args);
    };
    window.__fstDesktopWheelProbe = probe;
    window.addEventListener('wheel', probe.listener, { capture: true, passive: true });
  }, MAIN);
  try {
    await page.mouse.wheel(0, delta);
    await page.waitForFunction(() => (window.__fstDesktopWheelProbe?.events ?? 0) > 0);
    await stableScroll(page);
    const after = await geometry(page, MAIN);
    const panelAfter = panel ? await geometry(page, panel) : null;
    const input = await page.evaluate(() => {
      const { calls, events, trusted } = window.__fstDesktopWheelProbe!;
      return { calls, events, trusted };
    });
    expect(input.events).toBeGreaterThan(0);
    expect(input.trusted).toBe(true);
    expect(input.calls).toBe(0);
    return {
      hit,
      mainDelta: after.scrollTop - before.scrollTop,
      mainPointerBefore: before.pointerEvents,
      panelDelta: panelAfter && panelBefore ? panelAfter.scrollTop - panelBefore.scrollTop : null,
    };
  } finally {
    await page.evaluate(() => {
      const probe = window.__fstDesktopWheelProbe;
      if (!probe) return;
      window.removeEventListener('wheel', probe.listener, true);
      if (probe.descriptor) Object.defineProperty(probe.element, 'scrollBy', probe.descriptor);
      else Reflect.deleteProperty(probe.element, 'scrollBy');
      delete window.__fstDesktopWheelProbe;
    });
  }
}

test.describe('wide desktop native scroll panels', () => {
  test.beforeEach(async ({ page, appState }, testInfo) => {
    test.skip(!desktopOwners.has(testInfo.project.name), 'wide Chromium and desktop cross-engine ownership');
    await page.setViewportSize({ width: 1440, height: 900 });
    await appState.reset();
    await appState.setSettings({ disableLightTrails: false });
  });

  test('content-height panels leave native gaps and isolate controls and padding', async ({ page }) => {
    await ready(page, '/settings', 'settings');
    const navigation = await geometry(page, NAVIGATION);
    const utilities = await geometry(page, UTILITIES);
    const frame = await geometry(page, '[data-testid="pinned-sidebar"]');
    const links = await geometry(page, SETTINGS_LINKS);
    const portal = await geometry(page, '[data-testid="shell-quick-links-portal"]');
    expect(navigation.top).toBe(frame.top);
    expect(navigation.width).toBe(frame.width);
    expect(utilities.bottom).toBe(frame.bottom);
    expect(utilities.top - navigation.bottom).toBeGreaterThan(100);
    expect(links.height).toBeLessThan(portal.height);
    expect(links.width).toBe(portal.width);
    expect(links.scrollHeight).toBe(links.clientHeight);

    for (const [selector, point] of [
      [NAVIGATION, { x: navigation.x + 100, y: navigation.top + 82 }],
      [NAVIGATION, { x: navigation.x + 2, y: navigation.bottom - 2 }],
      [UTILITIES, { x: utilities.x + 100, y: utilities.bottom - 32 }],
      [UTILITIES, { x: utilities.x + 2, y: utilities.top + 2 }],
      [SETTINGS_LINKS, { x: links.x + 100, y: links.top + 200 }],
      [SETTINGS_LINKS, { x: links.x + 2, y: links.top + 2 }],
    ] as const) {
      for (const delta of [360, -360]) {
        await resetMain(page);
        const sample = await wheel(page, point, delta, selector);
        expect(sample.hit.panel).toBeTruthy();
        expect(sample.mainDelta).toBe(0);
      }
    }

    for (const point of [
      { x: navigation.x + 100, y: (navigation.bottom + utilities.top) / 2 },
      { x: links.x + 100, y: (links.bottom + portal.bottom) / 2 },
    ]) {
      for (const delta of [360, -360]) {
        await resetMain(page);
        const sample = await wheel(page, point, delta);
        expect(sample.hit.main).toBe(true);
        expect(Math.sign(sample.mainDelta)).toBe(Math.sign(delta));
      }
    }
    expect((await geometry(page, NAVIGATION)).top).toBe(navigation.top);
    expect((await geometry(page, UTILITIES)).bottom).toBe(utilities.bottom);
  });

  test('hands continuing gutter wheel input to panels without disabling native keyboard scrolling', async ({ page }, testInfo) => {
    await ready(page, '/settings', 'settings');
    const navigation = await geometry(page, NAVIGATION);
    const utilities = await geometry(page, UTILITIES);
    const links = await geometry(page, SETTINGS_LINKS);
    const gap = { x: navigation.x + 100, y: (navigation.bottom + utilities.top) / 2 };
    const main = await geometry(page, MAIN);
    for (const [selector, point] of [
      [NAVIGATION, { x: navigation.x + 100, y: navigation.top + 82 }],
      [UTILITIES, { x: utilities.x + 100, y: utilities.bottom - 32 }],
      [SETTINGS_LINKS, { x: links.x + 100, y: links.top + 200 }],
      ['[role="region"][aria-label="Page header"]', { x: 720, y: (main.top + main.viewportTop) / 2 }],
    ] as const) {
      await resetMain(page);
      expect((await wheel(page, gap, 360)).mainDelta).toBeGreaterThan(0);
      const handoff = await wheel(page, point, 360, selector);
      expect(handoff.mainDelta, JSON.stringify(handoff)).toBe(0);
      await expect(page.locator(MAIN)).toHaveCSS('pointer-events', 'auto');
    }
    const beforeKey = (await geometry(page, MAIN)).scrollTop;
    await page.locator('main#main-content').evaluate(element => (element as HTMLElement).focus({ preventScroll: true }));
    await page.keyboard.press('PageDown');
    await expect.poll(async () => (await geometry(page, MAIN)).scrollTop).toBeGreaterThan(beforeKey);

    await ready(page, '/songs', 'songs');
    await resetMain(page);
    await page.locator(SONG_LINKS).evaluate(element => { element.scrollTop = 0; });
    expect((await wheel(page, gap, 360)).mainDelta).toBeGreaterThan(0);
    const longLinks = await geometry(page, SONG_LINKS);
    const sample = await wheel(page, { x: longLinks.x + 100, y: longLinks.top + longLinks.height / 2 }, 360, SONG_LINKS);
    expect(sample.mainDelta).toBe(0);
    if (testInfo.project.name === 'firefox-desktop') {
      expect(sample.panelDelta).toBe(0);
      const native = await wheel(page, { x: longLinks.x + 100, y: longLinks.top + longLinks.height / 2 }, 360, SONG_LINKS);
      expect(native.mainDelta).toBe(0);
      expect(native.panelDelta).toBeGreaterThan(0);
    } else {
      expect(sample.panelDelta).toBeGreaterThan(0);
    }
  });

  test('long alphabet links scroll internally at boundaries and preserve M/Z placement and POP', async ({ page }) => {
    await ready(page, '/songs', 'songs');
    const links = await geometry(page, SONG_LINKS);
    expect(links.scrollHeight).toBeGreaterThan(links.clientHeight);
    const point = { x: links.x + 100, y: links.top + links.height / 2 };
    for (const [position, delta, moves] of [
      ['top', -360, false], ['top', 360, true],
      ['bottom', 360, false], ['bottom', -360, true],
    ] as const) {
      await resetMain(page);
      await page.locator(SONG_LINKS).evaluate((element, position) => {
        element.scrollTop = position === 'bottom' ? element.scrollHeight : 0;
      }, position);
      const sample = await wheel(page, point, delta, SONG_LINKS);
      expect(sample.mainDelta).toBe(0);
      if (moves) expect(Math.sign(sample.panelDelta!)).toBe(Math.sign(delta));
      else expect(sample.panelDelta).toBe(0);
    }
    for (const letter of ['m', 'z']) {
      const link = page.getByTestId(`songs-quick-link-title-${letter}`);
      await link.click();
      await expect(link).toHaveAttribute('aria-current', 'location');
      await stableScroll(page);
      const main = await geometry(page, MAIN);
      const section = await geometry(page, `[data-testid="songs-section-title-${letter}"]`);
      expect(section.bottom).toBeGreaterThan(main.viewportTop);
      expect(section.top).toBeLessThan(main.viewportBottom);
      if (letter === 'm') expect(Math.abs(section.top - main.viewportTop - 16)).toBeLessThanOrEqual(2);
    }
    const saved = (await geometry(page, MAIN)).scrollTop;
    await page.getByTestId('pinned-utilities').getByRole('link', { name: 'Settings', exact: true }).click();
    await expect(page).toHaveURL(/#\/settings$/);
    await page.goBack();
    await expect(page.getByTestId('songs-quick-links-rail')).toHaveCSS('opacity', '1');
    await stableScroll(page);
    expect(Math.abs((await geometry(page, MAIN)).scrollTop - saved)).toBeLessThanOrEqual(4);
  });

  test('an absent right rail exposes the header-side native hit area without scrolling the center header', async ({ page }) => {
    await ready(page, '/settings/licenses');
    await expect(page.locator('[data-testid$="-quick-links-rail"]')).toHaveCount(0);
    const main = await geometry(page, MAIN);
    const portal = await geometry(page, '[data-testid="shell-quick-links-portal"]');
    expect(main.clientTop).toBeGreaterThan(0);
    const header = await page.getByRole('region', { name: 'Page header' }).boundingBox();
    expect(Math.abs(header!.y + header!.height - main.viewportTop)).toBeLessThanOrEqual(1);
    for (const point of [
      { x: portal.x + 100, y: (main.top + main.viewportTop) / 2 },
      { x: portal.x + 100, y: (main.viewportTop + main.viewportBottom) / 2 },
    ]) {
      for (const delta of [360, -360]) {
        await resetMain(page);
        const sample = await wheel(page, point, delta);
        expect(sample.hit.main).toBe(true);
        expect(Math.sign(sample.mainDelta)).toBe(Math.sign(delta));
      }
    }
    await resetMain(page);
    const sample = await wheel(page, { x: 720, y: (main.top + main.viewportTop) / 2 }, 360);
    expect(sample.mainDelta).toBe(0);
  });

  test('short quad-band panels scroll internally and keep keyboard controls reachable', async ({ page, appState }) => {
    await appState.selectBand(E2E_QUAD_BAND);
    await page.setViewportSize({ width: 1440, height: 420 });
    await ready(page, '/settings', 'settings');
    await expect(page.getByTestId('pinned-sidebar-band-profile')).toBeVisible();
    for (const selector of [NAVIGATION, UTILITIES]) {
      const panel = await geometry(page, selector);
      expect(panel.scrollHeight).toBeGreaterThan(panel.clientHeight);
      expect(panel.top).toBeGreaterThanOrEqual(64);
      expect(panel.bottom).toBeLessThanOrEqual(420);
      for (const [position, delta, moves] of [
        ['top', -200, false], ['top', 200, true],
        ['bottom', 200, false], ['bottom', -200, true],
      ] as const) {
        await resetMain(page);
        await page.locator(selector).evaluate((element, position) => {
          element.scrollTop = position === 'bottom' ? element.scrollHeight : 0;
        }, position);
        const sample = await wheel(page, { x: panel.x + 120, y: panel.top + panel.height / 2 }, delta, selector);
        expect(sample.mainDelta).toBe(0);
        if (moves) expect(Math.abs(sample.panelDelta!)).toBeGreaterThan(0);
        else expect(sample.panelDelta).toBe(0);
      }
    }
    await resetMain(page);
    const saved = (await geometry(page, MAIN)).scrollTop;
    const utility = page.getByTestId('pinned-utilities');
    await utility.getByRole('link', { name: 'Settings', exact: true }).focus();
    for (const name of ['App Manual', 'Deselect Band', 'E2E Player D']) {
      await page.keyboard.press('Shift+Tab');
      const control = utility.getByRole(name === 'Deselect Band' ? 'button' : 'link', { name, exact: true });
      await expect(control).toBeFocused();
      const rect = await control.boundingBox();
      const viewport = await geometry(page, UTILITIES);
      expect(rect!.y).toBeGreaterThanOrEqual(viewport.top);
      expect(rect!.y + rect!.height).toBeLessThanOrEqual(viewport.bottom + 1);
    }
    await page.getByTestId('pinned-navigation').getByRole('link', { name: 'Songs', exact: true }).focus();
    expect((await geometry(page, MAIN)).scrollTop).toBe(saved);
  });

  test('selected-player utility controls remain separate and isolated', async ({ page, appState }) => {
    await appState.selectPlayer();
    await ready(page, '/settings', 'settings');
    const utility = page.getByTestId('pinned-utilities');
    await expect(utility.getByRole('link', { name: E2E_PLAYER.displayName, exact: true })).toBeVisible();
    await expect(utility.getByRole('button', { name: 'Deselect', exact: true })).toBeVisible();
    await expect(page.getByTestId('pinned-navigation').getByRole('link', { name: 'Rivals', exact: true })).toBeVisible();
    const rect = await geometry(page, UTILITIES);
    await resetMain(page);
    expect((await wheel(page, { x: rect.x + 100, y: rect.top + 32 }, 360, UTILITIES)).mainDelta).toBe(0);
  });

  test('actual rail reveal keeps descendants disabled until the animation completes', async ({ page, appState }) => {
    await ready(page, '/songs', 'songs');
    await page.addInitScript(() => {
      window.__fstDesktopRevealSamples = [];
      const observe = () => {
        const capture = () => {
          const rail = document.querySelector<HTMLElement>('[data-testid="songs-quick-links-rail"]');
          const nav = rail?.querySelector('nav');
          if (!rail || !nav) return;
          const style = getComputedStyle(rail);
          window.__fstDesktopRevealSamples!.push({
            pointer: style.pointerEvents,
            childPointer: getComputedStyle(nav).pointerEvents,
            opacity: Number(style.opacity),
          });
        };
        new MutationObserver(capture).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['style'] });
        capture();
      };
      if (document.body) observe();
      else document.addEventListener('DOMContentLoaded', observe, { once: true });
    });
    await appState.reset({ preserveFirstRun: true });
    await page.goto('/#/songs');
    const rail = page.getByTestId('songs-quick-links-rail');
    await expect(rail).toHaveCSS('pointer-events', 'none');
    await expect(page.getByTestId('fre-overlay')).toHaveCount(0);
    const rect = await geometry(page, '[data-testid="songs-quick-links-rail"]');
    const point = { x: rect.x + 100, y: rect.y + 200 };
    const hiddenHit = await page.evaluate(point => {
      const hit = document.elementFromPoint(point.x, point.y);
      return !!hit?.closest('[data-testid="songs-quick-links-rail"]');
    }, point);
    expect(hiddenHit).toBe(false);
    await expect(rail).toHaveCSS('opacity', '1');
    await expect(rail).toHaveCSS('pointer-events', 'auto');
    const samples = await page.evaluate(() => window.__fstDesktopRevealSamples!);
    expect(samples.some(sample => sample.pointer === 'none' && sample.childPointer === 'none' && sample.opacity < 1)).toBe(true);
    await resetMain(page);
    expect((await wheel(page, point, 360, SONG_LINKS)).mainDelta).toBe(0);
  });

  test('skip navigation, utility modal focus, and control-only glow stay intact', async ({ page }) => {
    await ready(page, '/settings', 'settings');
    const url = page.url();
    await page.keyboard.press('Tab');
    await expect(page.getByRole('link', { name: 'Skip to main content' })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.locator('main#main-content')).toBeFocused();
    expect(page.url()).toBe(url);
    await resetMain(page);
    const saved = (await geometry(page, MAIN)).scrollTop;
    const trigger = page.getByTestId('pinned-utilities').getByRole('button', { name: 'Select Profile', exact: true });
    await trigger.focus();
    await page.keyboard.press('Enter');
    await expect(page.locator('[role="dialog"]:visible')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.locator('[role="dialog"]:visible')).toHaveCount(0);
    await expect(trigger).toBeFocused();
    expect((await geometry(page, MAIN)).scrollTop).toBe(saved);
    const link = page.getByTestId('pinned-navigation').getByRole('link', { name: 'Leaderboards', exact: true });
    await link.hover();
    await expect.poll(() => link.evaluate(element => (element as HTMLElement).style.getPropertyValue('--glow-hover'))).toBe('1');
    const nav = await geometry(page, NAVIGATION);
    const utilities = await geometry(page, UTILITIES);
    await page.mouse.move(nav.x + 100, (nav.bottom + utilities.top) / 2);
    await expect.poll(() => link.evaluate(element => (element as HTMLElement).style.getPropertyValue('--glow-hover'))).toBe('0');
    expect((await page.getByTestId('pinned-sidebar').getAttribute('style')) ?? '').not.toContain('--frosted-card');
    await page.addScriptTag({ content: axe.source });
    const violations = await page.evaluate(async () => (await window.axe.run()).violations);
    expect(violations.filter(violation => ['moderate', 'serious', 'critical'].includes(violation.impact ?? ''))).toEqual([]);
  });
});

test.describe('compact shell scroll boundaries', () => {
  for (const width of [390, 768, 769, 1280, 1439, 1440]) {
    test(`retains compact controls and scrolling at ${width}px`, async ({ page, appState }, testInfo) => {
      test.skip(testInfo.project.name !== WIDE_PROJECT && !isMobileProject(testInfo.project.name), 'wide boundary and mobile ownership');
      test.skip(width === 1440 && !isMobileProject(testInfo.project.name), 'desktop wide mode is covered by the panel cases');
      await page.setViewportSize({ width, height: 800 });
      await appState.reset();
      await ready(page, '/settings');
      await expect(page.locator('[data-testid="page-root"]').getByText('App Settings', { exact: true })).toBeVisible();
      await expect(page.getByTestId('pinned-sidebar')).toHaveCount(0);
      await expect(page.locator('[data-testid$="-quick-links-rail"]')).toHaveCount(0);
      await expect(page.locator('main#main-content')).toHaveCount(1);
      const main = await geometry(page, MAIN);
      expect(main.clientTop).toBe(0);
      await resetMain(page);
      if (testInfo.project.name === 'webkit-mobile') {
        const before = (await geometry(page, MAIN)).scrollTop;
        await page.locator('main#main-content').focus();
        await page.keyboard.press('PageDown');
        await expect.poll(async () => (await geometry(page, MAIN)).scrollTop).toBeGreaterThan(before);
      } else {
        expect((await wheel(page, { x: width / 2, y: main.viewportTop + 150 }, 360)).mainDelta).toBeGreaterThan(0);
      }
      if (isMobileProject(testInfo.project.name) || width <= 768) {
        await expect(page.getByTestId('bottom-nav-settings')).toBeVisible();
      } else {
        await expect(page.getByRole('button', { name: 'Quick Links', exact: true })).toBeVisible();
      }
      const trigger = page.getByRole('button', { name: 'Quick Links', exact: true });
      if (isMobileProject(testInfo.project.name)) await trigger.tap();
      else await trigger.click();
      const dialog = page.getByRole('dialog', { name: 'Quick Links', exact: true });
      await expect(dialog).toBeVisible();
      const sectionLink = dialog.getByTestId('settings-quick-link-show-instruments');
      if (isMobileProject(testInfo.project.name)) await sectionLink.tap();
      else await sectionLink.click();
      await expect(dialog).toBeHidden();
      await expect(page.locator('[data-testid="page-root"]').getByText('Show Instruments', { exact: true })).toBeVisible();
    });
  }
});
