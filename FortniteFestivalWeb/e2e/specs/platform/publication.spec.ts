import { test, expect } from '../../fixtures/test';
import { createPopulatedScenario } from '../../fixtures/scenarios';
import { gotoAppRoute } from '../../support/drivers/app';

test.use({ scenario: createPopulatedScenario() });

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
