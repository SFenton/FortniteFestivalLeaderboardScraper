import type { Page } from '@playwright/test';
import type { ServerSong } from '@festival/core/api';
import { test, expect } from '../../../fixtures/test';
import { createPopulatedScenario, E2E_NOW, type AppScenario } from '../../../fixtures/scenarios';

// Catalog changes that still wait for a publication are operational state:
// the Songs page lists incoming songs but never shows a catalog-lag banner.
// Titles sort into the first rows of the default title sort on every viewport.
const INCOMING_TITLE = 'Aardvark Incoming Song';
const REFRESHED_TITLE = 'Aardvark Refreshed Song';

function createIncomingSong(template: ServerSong, title: string): ServerSong {
  return {
    ...template,
    songId: 'e2e-song-incoming',
    title,
    maxScores: undefined,
    awaitingPublication: true,
  };
}

function createCatalogLagScenario(): AppScenario {
  const scenario = createPopulatedScenario();
  const template = scenario.songs.songs[1]!;
  return {
    ...scenario,
    name: 'catalog-lag',
    serviceInfo: {
      ...scenario.serviceInfo,
      catalog: {
        syncIntervalSeconds: 300,
        live: { version: 13, songCount: 13, capturedAt: E2E_NOW },
        published: { publicationId: 1, version: 12, songCount: 13, capturedAt: E2E_NOW },
        working: { publicationId: 2, version: 13, songCount: 13 },
        awaitingPublication: 4,
        addedAwaitingPublication: 1,
        changedAwaitingPublication: 2,
        removedAwaitingPublication: 1,
        pathGenerationPending: 0,
        pathGenerationReviewRequired: 0,
      },
    },
    incomingSongs: {
      count: 1,
      publishedPublicationId: 1,
      songs: [createIncomingSong(template, INCOMING_TITLE)],
    },
  };
}

async function expectNoCatalogBanner(page: Page) {
  await expect(page.getByTestId('catalog-update-banner')).toHaveCount(0);
  await expect(page.getByText(/catalog updates? detected/i)).toHaveCount(0);
  await expect(page.getByText(/waiting for a leaderboard update to publish/i)).toHaveCount(0);
}

test.use({ scenario: createCatalogLagScenario() });

test('pending catalog changes list incoming songs without a banner', async ({
  page,
  appState,
  api,
}) => {
  await appState.reset();
  await page.goto('/#/songs', { waitUntil: 'load' });

  await expect(page.getByText('Deterministic Song 2', { exact: true })).toBeVisible();
  await expect.poll(() => api.count('/api/service-info')).toBeGreaterThanOrEqual(1);
  await expectNoCatalogBanner(page);

  await expect(page.getByText(INCOMING_TITLE, { exact: true })).toBeVisible();
  await expectNoCatalogBanner(page);

  // A catalog change refreshes the incoming list and still shows no banner.
  await expect.poll(() => api.socketConnections).toBeGreaterThanOrEqual(1);
  const incomingRequests = api.count('/api/songs/incoming');
  const incoming = api.current().incomingSongs;
  incoming.songs = [createIncomingSong(incoming.songs[0]!, REFRESHED_TITLE)];
  api.send({
    type: 'songs_changed',
    total: 13,
    added: 1,
    awaitingPublication: 4,
    at: E2E_NOW,
  });
  await expect.poll(() => api.count('/api/songs/incoming'))
    .toBeGreaterThan(incomingRequests);
  await expect(page.getByText(REFRESHED_TITLE, { exact: true })).toBeVisible();
  await expectNoCatalogBanner(page);
});
