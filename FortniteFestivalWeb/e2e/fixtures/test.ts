import { test as base, expect } from '@playwright/test';
import { ApiScenarioController, installScenarioApi } from './apiRouter';
import { AppState } from './appState';
import { createEmptyScenario, type AppScenario } from './scenarios';

type ScenarioFixtures = {
  scenario: AppScenario;
  api: ApiScenarioController;
  appState: AppState;
};

export const test = base.extend<ScenarioFixtures>({
  scenario: [createEmptyScenario(), { option: true }],
  api: [async ({ page, scenario }, use) => {
    const api = await installScenarioApi(page, scenario);
    await use(api);
  }, { auto: true }],
  appState: async ({ page }, use) => {
    await use(new AppState(page));
  },
});

export { expect };
