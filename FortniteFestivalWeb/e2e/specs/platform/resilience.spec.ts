import { test, expect } from '../../fixtures/test';
import { createPopulatedScenario, E2E_NOW } from '../../fixtures/scenarios';
import { gotoAppRoute } from '../../support/drivers/app';

test.use({ scenario: createPopulatedScenario() });

test('backend availability gate recovers after the retry interval', async ({
  page,
  appState,
  api,
}) => {
  await appState.reset();
  api.override({
    path: '/api/service-info',
    status: 503,
    body: { error: 'temporarily unavailable' },
    remaining: 1,
  });

  await page.goto('/#/songs', { waitUntil: 'load' });
  await expect(page.getByRole('main', { name: 'Festival Score Tracker Status' })).toBeVisible();

  await expect.poll(
    () => api.count('/api/service-info'),
    { timeout: 7_000 },
  ).toBeGreaterThanOrEqual(2);
  await expect(page.getByText('Deterministic Song 2', { exact: true })).toBeVisible();
});

test('shop and notification WebSocket messages update browser state', async ({
  page,
  appState,
  api,
}) => {
  await appState.reset();
  await appState.selectPlayer();
  await gotoAppRoute(page, '/shop');
  await expect.poll(() => api.socketConnections).toBeGreaterThanOrEqual(1);

  api.send({
    type: 'shop_changed',
    added: [{
      songId: 'e2e-song-ws',
      title: 'WebSocket Shop Arrival',
      artist: 'Realtime Artist',
      year: 2026,
      shopUrl: 'https://example.invalid/shop/e2e-song-ws',
      isNew: true,
    }],
    removed: [],
    total: 3,
    leavingTomorrow: [],
    newSongs: ['e2e-song-ws'],
  });
  await expect(page.getByText('WebSocket Shop Arrival', { exact: true })).toBeVisible();

  const notificationRequests = api.count(/^\/api\/player\/[^/]+\/notifications$/);
  api.send({ type: 'notification_feed_changed', at: E2E_NOW });
  await expect.poll(() => api.count(/^\/api\/player\/[^/]+\/notifications$/))
    .toBeGreaterThan(notificationRequests);
});
