import { test as base, expect, type Page, type Locator } from '@playwright/test';

/* ── Constants ── */

const TRANSITION_MS = 500;
const TEST_PLAYER = { accountId: '195e93ef108143b2975ee46662d4d0e1', displayName: 'SFentonX' };

/* ── Carousel page-object ── */

export class FreCarousel {
  readonly overlay: Locator;
  readonly card: Locator;
  readonly closeButton: Locator;
  readonly nextButton: Locator;
  readonly prevButton: Locator;
  readonly dots: Locator;
  readonly title: Locator;
  readonly description: Locator;
  readonly slideArea: Locator;

  constructor(private page: Page) {
    this.overlay = page.locator('[data-testid="fre-overlay"]');
    this.card = page.locator('[data-testid="fre-card"]');
    this.closeButton = page.locator('[data-testid="fre-close"]');
    this.nextButton = page.locator('[data-testid="fre-next"]');
    this.prevButton = page.locator('[data-testid="fre-prev"]');
    this.dots = page.locator('[data-testid="fre-dots"] button');
    this.title = page.locator('[data-testid="fre-title"]');
    this.description = page.locator('[data-testid="fre-description"]');
    this.slideArea = page.locator('[data-testid="fre-slide-area"]');
  }

  /** Wait for the carousel entrance animation to finish. */
  async waitForVisible() {
    await this.card.waitFor({ state: 'visible', timeout: 10_000 });
    // Allow entrance animation to settle
    await this.page.waitForTimeout(TRANSITION_MS + 100);
  }

  /** Returns true when the carousel overlay is present and visible. */
  async isVisible(): Promise<boolean> {
    return (await this.overlay.count()) > 0 && (await this.overlay.isVisible());
  }

  /** Click the close button and wait for exit animation. */
  async dismiss() {
    await this.closeButton.click();
    await this.page.waitForTimeout(TRANSITION_MS + 100);
  }

  /** Returns the number of pagination dots (= number of slides). */
  async slideCount(): Promise<number> {
    return this.dots.count();
  }

  /** Assert exactly `n` slides are present. */
  async assertSlideCount(n: number) {
    await expect(this.dots).toHaveCount(n);
  }

  /** Navigate forward through all slides, collecting each title text. */
  async collectAllTitles(): Promise<string[]> {
    const titles: string[] = [];
    const count = await this.slideCount();
    for (let i = 0; i < count; i++) {
      // Wait for title to become visible on each slide
      await expect(this.title).toBeVisible({ timeout: 5_000 });
      titles.push((await this.title.textContent()) ?? '');
      if (i < count - 1) {
        await this.nextButton.click();
        // Wait for cross-fade
        await this.page.waitForTimeout(300);
      }
    }
    return titles;
  }

  /** Navigate to a specific slide by clicking its dot (0-indexed). */
  async goToSlide(index: number) {
    await this.dots.nth(index).click();
    await this.page.waitForTimeout(300);
  }
}

/* ── localStorage helpers ── */

export class FreState {
  constructor(private page: Page) {}

  /**
   * Navigate to the app origin so localStorage is accessible,
   * then clear all fst:* state. Call this in beforeEach before
   * setting up any state. After this call, set your desired
   * state (setSettings, setTrackedPlayer, etc.) then call goto()
   * to navigate — the app will pick up the pre-set state on mount.
   */
  async resetAppState() {
    // Navigate to index to establish the correct origin for localStorage
    await this.page.goto('/', { waitUntil: 'commit' });
    await this.clearAllAppState();
    // We don't wait for full load — just need origin established
  }

  /** Clear all FRE seen-state from localStorage. */
  async clearFirstRunState() {
    await this.page.evaluate(() => localStorage.removeItem('fst:firstRun'));
  }

  /** Clear the tracked player from localStorage. */
  async clearTrackedPlayer() {
    await this.page.evaluate(() => localStorage.removeItem('fst:trackedPlayer'));
  }

  /** Clear all fst:* keys from localStorage. */
  async clearAllAppState() {
    await this.page.evaluate(() => {
      const keys = Object.keys(localStorage).filter(k => k.startsWith('fst:'));
      keys.forEach(k => localStorage.removeItem(k));
    });
  }

  /** Set a tracked player in localStorage. */
  async setTrackedPlayer(accountId = TEST_PLAYER.accountId, displayName = TEST_PLAYER.displayName) {
    await this.page.evaluate(
      ({ id, name }) => localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: id, displayName: name })),
      { id: accountId, name: displayName },
    );
  }

  /** Merge partial settings into fst:appSettings in localStorage. */
  async setSettings(partial: Record<string, unknown>) {
    await this.page.evaluate(
      (p) => {
        const raw = localStorage.getItem('fst:appSettings');
        const current = raw ? JSON.parse(raw) : {};
        localStorage.setItem('fst:appSettings', JSON.stringify({ ...current, ...p }));
      },
      partial,
    );
  }

  /** Set feature flag overrides (dev-mode only). */
  async setFeatureFlags(overrides: Record<string, boolean>) {
    await this.page.evaluate(
      (o) => localStorage.setItem('fst:featureFlagOverrides', JSON.stringify(o)),
      overrides,
    );
  }

  /** Clear feature flag overrides. */
  async clearFeatureFlags() {
    await this.page.evaluate(() => localStorage.removeItem('fst:featureFlagOverrides'));
  }

  /** Write seen records for the given slide IDs so they won't appear again. */
  async markSlidesSeen(slideIds: string[]) {
    await this.page.evaluate(
      (ids) => {
        const raw = localStorage.getItem('fst:firstRun');
        const state: Record<string, unknown> = raw ? JSON.parse(raw) : {};
        for (const id of ids) {
          state[id] = { version: 999, hash: 'e2e', seenAt: new Date().toISOString() };
        }
        localStorage.setItem('fst:firstRun', JSON.stringify(state));
      },
      slideIds,
    );
  }

  /** Read the current fst:firstRun seen state. */
  async getSeenSlides(): Promise<Record<string, unknown>> {
    return this.page.evaluate(() => {
      const raw = localStorage.getItem('fst:firstRun');
      return raw ? JSON.parse(raw) : {};
    });
  }
}

/* ── Extended test fixture ── */

type FreFixtures = {
  fre: FreCarousel;
  freState: FreState;
};

export const test = base.extend<FreFixtures>({
  fre: async ({ page }, use) => {
    await use(new FreCarousel(page));
  },
  freState: async ({ page }, use) => {
    await use(new FreState(page));
  },
});

export { expect };
