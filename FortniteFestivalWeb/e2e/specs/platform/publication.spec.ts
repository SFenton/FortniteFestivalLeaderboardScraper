import { test, expect } from '../../fixtures/test';
import { createPopulatedScenario } from '../../fixtures/scenarios';
import { gotoAppRoute } from '../../support/drivers/app';

test.use({ scenario: createPopulatedScenario() });

test('publication bootstrap uses an accessible purple splash without visible status copy', async ({
  page,
  appState,
  api,
}) => {
  await appState.reset();
  let releasePublication!: () => void;
  const publicationReady = new Promise<void>(resolve => {
    releasePublication = resolve;
  });
  await page.route('**/api/publication', async route => {
    await publicationReady;
    await route.fulfill({ json: api.current().publication });
  });

  await page.goto('/#/songs', { waitUntil: 'domcontentloaded' });

  const splash = page.getByTestId('startup-splash');
  await expect(splash).toBeVisible();
  await expect(splash).toHaveAttribute('role', 'status');
  await expect(splash).toHaveAttribute('aria-busy', 'true');
  await expect(splash).toHaveCSS('background-color', 'rgb(26, 8, 48)');
  await expect(splash.locator('img')).toHaveCount(0);
  const hiddenStatus = page.getByTestId('startup-splash-status');
  await expect(hiddenStatus).toHaveCSS('clip-path', 'inset(50%)');
  expect(await hiddenStatus.boundingBox()).toMatchObject({ width: 1, height: 1 });
  await expect(page.getByText('Loading published data...')).toHaveCount(0);

  releasePublication();

  await expect(splash).toHaveCount(0);
  await expect(page.getByText('Deterministic Song 2', { exact: true })).toBeVisible();
});

test('publication change refreshes pinned requests, caches, and WebSocket ownership', async ({
  page,
  appState,
  api,
}) => {
  await appState.reset();
  await appState.selectPlayer();
  await gotoAppRoute(page, '/songs');

  await expect.poll(() => api.count('/api/publication')).toBe(1);
  await expect.poll(() => api.count('/api/songs')).toBeGreaterThanOrEqual(1);
  await expect.poll(() => api.socketConnections).toBeGreaterThanOrEqual(1);
  expect(api.last('/api/songs')?.search).toContain('publicationId=1');

  const current = api.current();
  current.publication = {
    ...current.publication,
    publicationId: 2,
    previousPublicationId: 1,
    publishedScrapeId: 2,
  };
  api.override({
    path: '/api/rankings/Solo_Guitar',
    status: 409,
    body: { status: 'publication_changed' },
    remaining: 1,
  });

  await gotoAppRoute(page, '/leaderboards');

  await expect.poll(() => api.count('/api/publication')).toBeGreaterThanOrEqual(2);
  await expect.poll(() => api.count('/api/songs')).toBeGreaterThanOrEqual(2);
  await expect.poll(() => api.socketConnections).toBeGreaterThanOrEqual(2);
  await expect.poll(() => api.last('/api/rankings/Solo_Guitar')?.search ?? '')
    .toContain('publicationId=2');
  await expect(page.localStorage.getItem('fst_publication_id')).resolves.toBe('2');
});
