import type { Page } from '@playwright/test';
import { installScenarioApi } from './apiRouter';
import { createEmptyScenario, E2E_BAND, E2E_PLAYER, E2E_SONG_ID } from './scenarios';

export { E2E_BAND, E2E_PLAYER, E2E_SONG_ID };

export async function installDeterministicApiMocks(page: Page): Promise<void> {
  await installScenarioApi(page, createEmptyScenario());
}
