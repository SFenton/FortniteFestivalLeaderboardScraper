import type { Page } from '@playwright/test';
import { changelogHash } from '../../src/changelogHash';
import { E2E_BAND, E2E_PLAYER } from './scenarios';
import type { SelectedBandProfile } from '../../src/state/selectedProfile';

export class AppState {
  constructor(protected readonly page: Page) {}

  async reset({ preserveFirstRun = false }: { preserveFirstRun?: boolean } = {}): Promise<void> {
    const firstRun = preserveFirstRun ? await this.page.localStorage.getItem('fst:firstRun') : null;
    await this.page.goto('/e2e/fixtures/reset.html', { waitUntil: 'load' });
    await Promise.all([
      this.page.localStorage.clear(),
      this.page.sessionStorage.clear(),
    ]);
    await this.page.localStorage.setItem(
      'fst:changelog',
      JSON.stringify({ version: 'e2e', hash: changelogHash() }),
    );
    if (firstRun) await this.page.localStorage.setItem('fst:firstRun', firstRun);
  }

  async selectPlayer(
    accountId = E2E_PLAYER.accountId,
    displayName = E2E_PLAYER.displayName,
  ): Promise<void> {
    const profile = { accountId, displayName };
    await Promise.all([
      this.page.localStorage.setItem('fst:trackedPlayer', JSON.stringify(profile)),
      this.page.localStorage.setItem(
        'fst:selectedProfile',
        JSON.stringify({ type: 'player', ...profile }),
      ),
    ]);
  }

  async selectBand(profile: Omit<SelectedBandProfile, 'type'> = { ...E2E_BAND, members: [...E2E_BAND.members] }): Promise<void> {
    await Promise.all([
      this.page.localStorage.removeItem('fst:trackedPlayer'),
      this.page.localStorage.setItem(
        'fst:selectedProfile',
        JSON.stringify({ type: 'band', ...profile }),
      ),
    ]);
  }

  async clearProfile(): Promise<void> {
    await Promise.all([
      this.page.localStorage.removeItem('fst:trackedPlayer'),
      this.page.localStorage.removeItem('fst:selectedProfile'),
    ]);
  }

  async setSettings(partial: Record<string, unknown>): Promise<void> {
    const raw = await this.page.localStorage.getItem('fst:appSettings');
    const current = raw ? JSON.parse(raw) as Record<string, unknown> : {};
    await this.page.localStorage.setItem(
      'fst:appSettings',
      JSON.stringify({ ...current, ...partial }),
    );
  }

  async setLegacyFeatureFlagOverrides(
    overrides: Record<string, boolean>,
  ): Promise<void> {
    await this.page.localStorage.setItem(
      'fst:featureFlagOverrides',
      JSON.stringify(overrides),
    );
  }

  async clearLegacyFeatureFlagOverrides(): Promise<void> {
    await this.page.localStorage.removeItem('fst:featureFlagOverrides');
  }

  async clearFirstRun(): Promise<void> {
    await this.page.localStorage.removeItem('fst:firstRun');
  }

  async firstRunState(): Promise<Record<string, unknown>> {
    const raw = await this.page.localStorage.getItem('fst:firstRun');
    return raw ? JSON.parse(raw) as Record<string, unknown> : {};
  }
}
