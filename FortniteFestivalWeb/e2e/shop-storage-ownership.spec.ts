import { expect, test, type Page, type Route } from '@playwright/test';
import { writeFileSync } from 'node:fs';

const PROFILE_A = { accountId: 'web23-profile-a', displayName: 'WEB23 Profile A' };
const PROFILE_B = { accountId: 'web23-profile-b', displayName: 'WEB23 Profile B' };
const SONG_ID = 'web23-song';

type RequestMetric = {
  path: string;
  failed: boolean;
  status?: number;
};

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

function playerResponse(profile: typeof PROFILE_A) {
  return {
    accountId: profile.accountId,
    displayName: profile.displayName,
    totalScores: 0,
    scores: [],
  };
}

async function installApi(page: Page, requests: RequestMetric[]) {
  await page.routeWebSocket('**/api/ws', () => {});
  page.on('request', request => {
    const url = new URL(request.url());
    if (url.pathname === '/api/songs' || url.pathname === '/api/shop') {
      requests.push({ path: url.pathname, failed: false });
    }
  });
  page.on('requestfailed', request => {
    const url = new URL(request.url());
    const metric = requests.findLast(item => item.path === url.pathname && !item.failed);
    if (metric) metric.failed = true;
  });
  page.on('response', response => {
    const url = new URL(response.url());
    const metric = requests.findLast(item => item.path === url.pathname && item.status === undefined);
    if (metric) metric.status = response.status();
  });

  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    if (!url.pathname.startsWith('/api/')) return route.continue();
    if (url.pathname === '/api/service-info') {
      return json(route, {
        lastCompletedUpdate: null,
        currentUpdate: null,
        activeScrapeId: null,
        publishedScrapeId: 1236,
        publication: {
          publishedScrapeId: 1236,
          publishedAt: '2026-07-25T00:00:00Z',
          publicReadsFrozen: false,
        },
        workerStatus: { status: 'offline', rawStatus: 'offline' },
      });
    }
    if (url.pathname === '/api/songs') {
      await new Promise(resolve => setTimeout(resolve, 40));
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        headers: { ETag: '"web23-songs"' },
        body: JSON.stringify({
          count: 2,
          currentSeason: 14,
          songs: [{
            songId: SONG_ID,
            title: 'WEB23 Cached Song',
            artist: 'WEB23 Artist',
          }, {
            songId: `${SONG_ID}-new`,
            title: 'WEB23 New Song',
            artist: 'WEB23 Artist',
          }],
        }),
      });
    }
    if (url.pathname === '/api/shop') {
      await new Promise(resolve => setTimeout(resolve, 1_200));
      return json(route, {
        count: 2,
        songs: [{
          songId: SONG_ID,
          title: 'WEB23 Leaving Song',
          artist: 'WEB23 Artist',
          shopUrl: 'https://example.com/shop/web23',
          leavingTomorrow: true,
        }, {
          songId: `${SONG_ID}-new`,
          title: 'WEB23 New Song',
          artist: 'WEB23 Artist',
          shopUrl: 'https://example.com/shop/web23-new',
          isNew: true,
        }],
        newSongs: [`${SONG_ID}-new`],
        lastUpdated: '2026-07-25T00:00:00Z',
      });
    }
    if (url.pathname === '/api/version') return json(route, { version: 'web23' });
    if (url.pathname === '/api/account/name-refresh') {
      return json(route, {
        changed: 0,
        unchanged: 1,
        failed: 0,
        missing: 0,
        names: {},
        changedAccountIds: [],
      });
    }

    const playerMatch = /^\/api\/player\/([^/]+)$/.exec(url.pathname);
    if (playerMatch) {
      await new Promise(resolve => setTimeout(resolve, 100));
      return json(
        route,
        playerResponse(playerMatch[1] === PROFILE_B.accountId ? PROFILE_B : PROFILE_A),
      );
    }
    const syncMatch = /^\/api\/player\/([^/]+)\/sync-status$/.exec(url.pathname);
    if (syncMatch) {
      return json(route, {
        accountId: syncMatch[1],
        isTracked: true,
        backfill: null,
        historyRecon: null,
      });
    }
    const notificationsMatch = /^\/api\/player\/([^/]+)\/notifications$/.exec(url.pathname);
    if (notificationsMatch) {
      return json(route, {
        generatedAt: '2026-07-25T00:00:00Z',
        expiresAfterHours: 24,
        items: [],
      });
    }
    const bandsMatch = /^\/api\/player\/([^/]+)\/bands$/.exec(url.pathname);
    if (bandsMatch) {
      return json(route, {
        accountId: bandsMatch[1],
        group: url.searchParams.get('group') ?? 'all',
        totalCount: 0,
        entries: [],
      });
    }

    return json(route, {});
  });
}

async function dismissDialogs(page: Page) {
  for (let attempt = 0; attempt < 8; attempt += 1) {
    const dialog = page.getByRole('dialog').last();
    if (!(await dialog.isVisible().catch(() => false))) return;
    const button = dialog.getByRole('button', { name: /close|skip|got it|continue|done|later/i }).last();
    if (!(await button.isVisible().catch(() => false))) return;
    await button.click();
  }
}

async function navigate(page: Page, path: string) {
  await page.evaluate(nextPath => {
    window.location.hash = `#${nextPath}`;
  }, path);
  await page.waitForFunction(nextPath => window.location.hash === `#${nextPath}`, path);
}

test('Songs and Shop share one cache/request owner across profile switches', async ({ page }, testInfo) => {
  const requests: RequestMetric[] = [];
  const consoleErrors: string[] = [];
  const pageErrors: string[] = [];
  page.on('console', message => {
    if (message.type() === 'error') consoleErrors.push(message.text());
  });
  page.on('pageerror', error => pageErrors.push(error.message));
  await page.addInitScript(({ profile, songId }) => {
    const parse = JSON.parse;
    Object.defineProperty(window, '__web23SongsParseCount', { value: 0, writable: true });
    JSON.parse = function parseWithSongsCount(text: string, reviver?: (this: unknown, key: string, value: unknown) => unknown) {
      if (typeof text === 'string' && text.includes('"web23Marker":true')) {
        (window as Window & { __web23SongsParseCount: number }).__web23SongsParseCount += 1;
      }
      return parse.call(this, text, reviver);
    };
    Object.assign(window, { __web23LongTasks: [], __web23UnhandledRejections: [] });
    window.addEventListener('unhandledrejection', event => {
      (window as Window & { __web23UnhandledRejections: string[] }).__web23UnhandledRejections.push(String(event.reason));
    });
    if ('PerformanceObserver' in window) {
      try {
        const observer = new PerformanceObserver(list => {
          (window as Window & { __web23LongTasks: PerformanceEntry[] }).__web23LongTasks.push(...list.getEntries());
        });
        observer.observe({ type: 'longtask', buffered: true });
      } catch {
        // Long-task observation is optional.
      }
    }

    localStorage.clear();
    localStorage.setItem('fst:selectedProfile', JSON.stringify({ type: 'player', ...profile }));
    localStorage.setItem('fst:trackedPlayer', JSON.stringify(profile));
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: true,
      showDrums: true,
      showVocals: true,
      showProLead: true,
      showProBass: true,
      showPeripheralVocals: true,
      showPeripheralCymbals: true,
      showPeripheralDrums: true,
    }));
    localStorage.setItem('fst_songs_cache', JSON.stringify({
      web23Marker: true,
      v: 2,
      etag: '"web23-songs"',
      data: {
        count: 2,
        currentSeason: 14,
        songs: [{
          songId,
          title: 'WEB23 Cached Song',
          artist: 'WEB23 Artist',
        }, {
          songId: `${songId}-new`,
          title: 'WEB23 New Song',
          artist: 'WEB23 Artist',
        }],
      },
    }));
  }, { profile: PROFILE_A, songId: SONG_ID });
  await installApi(page, requests);

  const songsStartedAt = performance.now();
  await page.goto('/#/songs', { waitUntil: 'domcontentloaded' });
  if (process.env.WEB23_DIAGNOSTIC === '1') {
    await page.waitForTimeout(500);
    console.log(await page.evaluate(() => ({
      url: location.href,
      html: document.documentElement.outerHTML.slice(0, 1_000),
      body: document.body.innerText.slice(0, 1_000),
      cache: localStorage.getItem('fst_songs_cache'),
      parseCount: (window as Window & { __web23SongsParseCount?: number }).__web23SongsParseCount,
    })), { consoleErrors, pageErrors });
  }
  await expect(page.getByText('WEB23 Cached Song').first()).toBeVisible({ timeout: 10_000 });
  await dismissDialogs(page);
  const songsReadyMs = performance.now() - songsStartedAt;

  const shopStartedAt = performance.now();
  await navigate(page, '/shop');
  await expect(page.getByText('WEB23 Leaving Song').first()).toBeVisible({ timeout: 10_000 });
  await expect(page.getByText('WEB23 New Song').first()).toBeVisible();
  await expect(page.getByText(/Leaving Tomorrow/i)).toHaveCount(1);
  await dismissDialogs(page);
  const shopReadyMs = performance.now() - shopStartedAt;

  const catalogRequestsBeforeProfileSwitch = requests.length;
  const profileStartedAt = performance.now();
  const switchedProfileResponse = page.waitForResponse(response => (
    new URL(response.url()).pathname === `/api/player/${PROFILE_B.accountId}`
  ));
  await page.evaluate(profile => {
    localStorage.setItem('fst:selectedProfile', JSON.stringify({ type: 'player', ...profile }));
    localStorage.setItem('fst:trackedPlayer', JSON.stringify(profile));
    window.dispatchEvent(new Event('fst:selectedProfileChanged'));
    window.dispatchEvent(new Event('fst:trackedPlayerChanged'));
  }, PROFILE_B);
  await switchedProfileResponse;
  const profileReadyMs = performance.now() - profileStartedAt;
  await page.waitForTimeout(100);

  const browserMetrics = await page.evaluate(() => {
    const memory = performance.memory;
    const longTasks = (window as Window & { __web23LongTasks?: PerformanceEntry[] }).__web23LongTasks ?? [];
    return {
      songsParseCount: (window as Window & { __web23SongsParseCount?: number }).__web23SongsParseCount ?? 0,
      usedJsHeapBytes: memory?.usedJSHeapSize ?? null,
      longTaskCount: longTasks.length,
      longTaskDurationMs: longTasks.reduce((sum, entry) => sum + entry.duration, 0),
      unhandledRejections: (window as Window & { __web23UnhandledRejections?: string[] }).__web23UnhandledRejections ?? [],
    };
  });
  const metrics = {
    project: testInfo.project.name,
    expectation: process.env.WEB23_EXPECT_SHARED_OWNERS === '0' ? 'baseline' : 'candidate',
    routeReadyMs: {
      songs: songsReadyMs,
      shop: shopReadyMs,
      profile: profileReadyMs,
    },
    requests,
    catalogRequestsAfterProfileSwitch: requests.length - catalogRequestsBeforeProfileSwitch,
    consoleErrors,
    pageErrors,
    ...browserMetrics,
  };
  if (process.env.WEB23_METRICS_PATH) {
    const metricsPath = process.env.WEB23_METRICS_PATH
      .replace('{project}', testInfo.project.name)
      .replace('{repeat}', String(testInfo.repeatEachIndex));
    writeFileSync(metricsPath, `${JSON.stringify(metrics, null, 2)}\n`);
  }

  expect(requests.filter(request => request.path === '/api/songs' && request.status === 200)).toHaveLength(1);
  expect(requests.filter(request => request.path === '/api/shop' && request.status === 200)).toHaveLength(1);
  expect(metrics.catalogRequestsAfterProfileSwitch).toBe(0);
  expect(consoleErrors).toEqual([]);
  expect(pageErrors).toEqual([]);
  expect(browserMetrics.unhandledRejections).toEqual([]);
  if (process.env.WEB23_EXPECT_SHARED_OWNERS === '0') {
    expect(browserMetrics.songsParseCount).toBeGreaterThan(1);
  } else {
    expect(browserMetrics.songsParseCount).toBe(1);
  }
});
