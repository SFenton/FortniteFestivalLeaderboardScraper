import type { Page } from '@playwright/test';

type SplashProbeWindow = Window & {
  __startupSplashNodes?: Element[];
  __startupSpinnerNodes?: Element[];
};

export type StartupSplashNodeCounts = {
  splashes: number;
  spinners: number;
  currentIsFirst: boolean;
};

/**
 * Records every startup splash and splash spinner element inserted from the
 * first document mutation, so specs can prove bootstrap stages reuse one node
 * instead of remounting it (a remount restarts the spinner animation).
 */
export async function recordStartupSplashNodes(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const state = window as SplashProbeWindow;
    const splashes: Element[] = [];
    const spinners: Element[] = [];
    state.__startupSplashNodes = splashes;
    state.__startupSpinnerNodes = spinners;
    const remember = (list: Element[], element: Element) => {
      if (!list.includes(element)) list.push(element);
    };
    const collect = (root: Element, selector: string) => [
      ...(root.matches(selector) ? [root] : []),
      ...root.querySelectorAll(selector),
    ];
    new MutationObserver(records => {
      for (const record of records) {
        for (const node of record.addedNodes) {
          if (!(node instanceof Element)) continue;
          for (const splash of collect(node, '[data-testid="startup-splash"]')) remember(splashes, splash);
          for (const spinner of collect(node, '[data-testid="arc-spinner"]')) {
            if (spinner.closest('[data-testid="startup-splash"]')) remember(spinners, spinner);
          }
        }
      }
    }).observe(document, { childList: true, subtree: true });
  });
}

export async function readStartupSplashNodes(page: Page): Promise<StartupSplashNodeCounts> {
  return page.evaluate(() => {
    const state = window as SplashProbeWindow;
    const splashes = state.__startupSplashNodes ?? [];
    const current = document.querySelector('[data-testid="startup-splash"]');
    return {
      splashes: splashes.length,
      spinners: state.__startupSpinnerNodes?.length ?? 0,
      currentIsFirst: current !== null && current === splashes[0],
    };
  });
}
