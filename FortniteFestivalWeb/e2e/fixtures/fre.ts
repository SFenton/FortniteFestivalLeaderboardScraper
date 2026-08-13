import { errors, type Page, type Locator } from '@playwright/test';
import { test as base, expect } from './test';
import { AppState } from './appState';
import { E2E_PLAYER } from './scenarios';

/* ── Constants ── */

const TRANSITION_MS = 500;
const TEST_PLAYER = E2E_PLAYER;

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
    try {
      await this.card.waitFor({ state: 'visible', timeout: 10_000 });
    } catch (error) {
      if (!(error instanceof errors.TimeoutError)) throw error;
      await this.page.reload({ waitUntil: 'load' });
      await this.card.waitFor({ state: 'visible', timeout: 35_000 });
    }
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

export class FreState extends AppState {

  /**
   * Navigate to a same-origin static resource so localStorage is accessible
   * without mounting React, then clear all fst:* state. After this call, set
   * desired state and call goto() so the app reads it on its first mount.
   */
  async resetAppState() {
    await this.reset();
  }

  /** Clear all FRE seen-state from localStorage. */
  async clearFirstRunState() {
    await this.clearFirstRun();
  }

  /** Clear the tracked player from localStorage. */
  async clearTrackedPlayer() {
    await this.page.localStorage.removeItem('fst:trackedPlayer');
  }

  /** Clear application state from both browser storage areas. */
  async clearAllAppState() {
    await Promise.all([
      this.page.localStorage.clear(),
      this.page.sessionStorage.clear(),
    ]);
  }

  /** Set a tracked player in localStorage. */
  async setTrackedPlayer(accountId = TEST_PLAYER.accountId, displayName = TEST_PLAYER.displayName) {
    await this.selectPlayer(accountId, displayName);
  }

  /** Merge partial settings into fst:appSettings in localStorage. */
  async setSettings(partial: Record<string, unknown>) {
    await super.setSettings(partial);
  }

  /** Set legacy feature flag overrides. The app should now ignore these. */
  async setLegacyFeatureFlagOverrides(overrides: Record<string, boolean>) {
    await super.setLegacyFeatureFlagOverrides(overrides);
  }

  /** Clear legacy feature flag overrides. */
  async clearLegacyFeatureFlagOverrides() {
    await super.clearLegacyFeatureFlagOverrides();
  }

  /** Read the current fst:firstRun seen state. */
  async getSeenSlides(): Promise<Record<string, unknown>> {
    return this.firstRunState();
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
