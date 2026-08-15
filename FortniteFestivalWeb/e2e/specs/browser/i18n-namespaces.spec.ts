import { expect, test } from '../../fixtures/test';
import { createPopulatedScenario } from '../../fixtures/scenarios';
import { dismissObstructions, gotoAppRoute } from '../../support/drivers/app';

test.use({ scenario: createPopulatedScenario() });

test('lazy translation namespaces load without key flashes', async ({ page, appState }) => {
  await appState.reset();
  await appState.setSettings({ disableLightTrails: true });

  await page.addInitScript(() => {
    const keyPattern = /\b(?:appManual|firstRun|settings)\.[A-Za-z][\w.-]*\b/g;
    const state = window as Window & {
      __i18nKeyFlashes?: string[];
      __i18nKeyObserver?: MutationObserver;
    };
    const observe = () => {
      state.__i18nKeyFlashes = [];
      const scan = () => {
        const matches = document.body.innerText.match(keyPattern) ?? [];
        state.__i18nKeyFlashes!.push(...matches);
      };
      state.__i18nKeyObserver = new MutationObserver(scan);
      state.__i18nKeyObserver.observe(document.body, {
        childList: true,
        characterData: true,
        subtree: true,
      });
      scan();
    };
    if (document.readyState === 'loading') {
      document.addEventListener('DOMContentLoaded', observe, { once: true });
    } else {
      observe();
    }
  });

  await gotoAppRoute(page, '/songs');

  await navigateByHash(page, '/manual');
  await expect(page.getByRole('heading', { name: 'Navigation Basics' })).toBeVisible({
    timeout: 15_000,
  });

  await navigateByHash(page, '/settings');
  await dismissObstructions(page);
  await expect(page.getByText('Show Instruments', { exact: true })).toBeVisible({
    timeout: 15_000,
  });

  await page.getByText('Show', { exact: true }).first().click();
  await expect(page.getByRole('dialog', { name: 'Feature tour' })).toBeVisible();

  const flashes = await page.evaluate(() => {
    const state = window as Window & {
      __i18nKeyFlashes?: string[];
      __i18nKeyObserver?: MutationObserver;
    };
    state.__i18nKeyObserver?.disconnect();
    return state.__i18nKeyFlashes ?? [];
  });
  expect(flashes).toEqual([]);
});

async function navigateByHash(page: Parameters<typeof gotoAppRoute>[0], path: string) {
  await page.evaluate(nextPath => {
    window.location.hash = `#${nextPath}`;
  }, path);
  await expect(page).toHaveURL(new RegExp(`#${path.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`));
}
