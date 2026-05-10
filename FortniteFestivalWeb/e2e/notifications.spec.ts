import type { Locator, Page } from '@playwright/test';
import { test, expect } from './fixtures/fre';
import { changelogHash } from '../src/changelog';
import { contentHash, type FirstRunStorage } from '../src/firstRun/types';

const NOTIFICATION_SEEN_STORAGE_KEY = 'fst:notificationSeen:v1';
const NOTIFICATION_FRESHNESS_STORAGE_KEY = 'fst:notificationFreshness:v1';
const EXPECTED_NOTIFICATION_COUNT = 6;
const NOTIFICATIONS_VALIDATION_TOKEN = 'notifications-open';
const APPLE_SONG_ID = 'e90125a8-742a-4be9-baa0-4d93f5fba556';
const STAND_AND_FIGHT_REMIX_SONG_ID = '4e5b8da5-0891-4a5b-9386-85031fcdca08';
const GHOSTS_N_STUFF_SONG_ID = 'e60b07e6-065a-4059-a7a4-4a88fe268108';

test.describe('Notification seen state', () => {
  test.beforeEach(async ({ page }) => {
    await installApiMocks(page);
    await seedAppState(page);
  });

  test('mobile badge and unread dots follow visibility-based seen state', async ({ page }, testInfo) => {
    test.skip(!testInfo.project.name.startsWith('mobile'), 'mobile-only notification shell behavior');

    await page.goto('/#/songs', { waitUntil: 'load' });
    await dismissFirstRunIfVisible(page);

    const notificationsButton = page.getByTestId('mobile-header-notifications');
    const notificationBadge = notificationsButton.locator('span');
    await expect(notificationsButton).toBeVisible({ timeout: 10_000 });
    await expect(notificationBadge).toHaveText(String(EXPECTED_NOTIFICATION_COUNT));

    await notificationsButton.click();

    const dialog = page.getByRole('dialog', { name: 'Notifications' });
    const rows = page.getByTestId('mock-notification-row');
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    await expect(rows).toHaveCount(EXPECTED_NOTIFICATION_COUNT);
    await expectNotificationFreshnessSections(page, EXPECTED_NOTIFICATION_COUNT, 0);
    await expectRowsNewestFirst(rows);
    await expectSoloInstrumentNotificationCopy(page);
    await expect(page.getByRole('button', { name: 'Actions' })).toHaveCount(0);

    await expect.poll(() => readBadgeCount(notificationBadge), { timeout: 5_000 }).toBeLessThan(EXPECTED_NOTIFICATION_COUNT);
    const unreadAfterOpen = await readBadgeCount(notificationBadge);
    expect(unreadAfterOpen).toBeGreaterThan(0);
    await expect(page.getByTestId('notification-unread-dot')).toHaveCount(EXPECTED_NOTIFICATION_COUNT);
    await expect.poll(() => readMockSeenCount(page), { timeout: 5_000 }).toBe(EXPECTED_NOTIFICATION_COUNT - unreadAfterOpen);

    await revealEveryNotification(page);

    await expect(notificationBadge).toHaveCount(0, { timeout: 5_000 });
    await expect(page.getByTestId('notification-unread-dot')).toHaveCount(EXPECTED_NOTIFICATION_COUNT);
    await expect.poll(() => readMockSeenCount(page), { timeout: 5_000 }).toBe(EXPECTED_NOTIFICATION_COUNT);

    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(dialog).toBeHidden({ timeout: 5_000 });
    await notificationsButton.click();
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('notification-unread-dot')).toHaveCount(0);
    await expectNotificationFreshnessSections(page, EXPECTED_NOTIFICATION_COUNT, 0);
    await expect(page.getByRole('button', { name: 'Actions' })).toHaveCount(0);
  });

  test('desktop validation mode exercises the same modal seen-state lifecycle', async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'desktop', 'desktop validation is covered by the desktop project');

    await page.goto(`/?validation=${NOTIFICATIONS_VALIDATION_TOKEN}#/songs`, { waitUntil: 'load' });
    await dismissFirstRunIfVisible(page);

    const dialog = page.getByRole('dialog', { name: 'Notifications' });
    const rows = page.getByTestId('mock-notification-row');
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    await expectDesktopNotificationDrawer(page);
    await expect(rows).toHaveCount(EXPECTED_NOTIFICATION_COUNT);
    await expectNotificationFreshnessSections(page, EXPECTED_NOTIFICATION_COUNT, 0);
    await expectRowsNewestFirst(rows);
    await expectSoloInstrumentNotificationCopy(page);
    await expect(page.getByTestId('notification-unread-dot')).toHaveCount(EXPECTED_NOTIFICATION_COUNT);

    await revealEveryNotification(page);

    await expect.poll(() => readMockSeenCount(page), { timeout: 5_000 }).toBe(EXPECTED_NOTIFICATION_COUNT);
    await expect(page.getByTestId('notification-unread-dot')).toHaveCount(EXPECTED_NOTIFICATION_COUNT);

    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(dialog).toBeHidden({ timeout: 5_000 });
    await page.reload({ waitUntil: 'load' });
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    await expectDesktopNotificationDrawer(page);
    await expect(page.getByTestId('notification-unread-dot')).toHaveCount(0);
    await expectNotificationFreshnessSections(page, EXPECTED_NOTIFICATION_COUNT, 0);
  });

  test('desktop header notifications button opens the notifications modal', async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'desktop', 'desktop header behavior is covered by the desktop project');

    await page.goto('/#/songs', { waitUntil: 'load' });
    await dismissFirstRunIfVisible(page);

    const notificationsButton = page.getByTestId('desktop-header-notifications');
    const notificationBadge = notificationsButton.locator('span');
    await expect(notificationsButton).toBeVisible({ timeout: 10_000 });
    await expect(notificationBadge).toHaveText(String(EXPECTED_NOTIFICATION_COUNT));

    await notificationsButton.click();

    const dialog = page.getByRole('dialog', { name: 'Notifications' });
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    await expectDesktopNotificationDrawer(page);
    await expect(page.getByTestId('mock-notification-row')).toHaveCount(EXPECTED_NOTIFICATION_COUNT);
    await expectNotificationFreshnessSections(page, EXPECTED_NOTIFICATION_COUNT, 0);
    await expectSoloInstrumentNotificationCopy(page);
  });

  test('player notification badge and modal rows respect hidden instruments', async ({ page }, testInfo) => {
    await seedPlayerNotificationFilterState(page, testInfo.project.name.startsWith('mobile'));
    await page.goto('/#/songs', { waitUntil: 'load' });
    await dismissFirstRunIfVisible(page);

    const headerPrefix = testInfo.project.name.startsWith('mobile') ? 'mobile-header' : 'desktop-header';
    const notificationsButton = page.getByTestId(`${headerPrefix}-notifications`);
    await expect(notificationsButton).toBeVisible({ timeout: 10_000 });
    await expect(notificationsButton.getByText('2', { exact: true })).toBeVisible();

    await notificationsButton.click();

    const dialog = page.getByRole('dialog', { name: 'Notifications' });
    await expect(dialog).toBeVisible({ timeout: 10_000 });

    const rows = page.getByTestId('mock-notification-row');
    await expect(rows).toHaveCount(2);
    await expect(rows.nth(0)).toHaveAttribute('data-notification-guid', 'visible-band-notification');
    await expect(rows.nth(1)).toHaveAttribute('data-notification-guid', 'visible-guitar-notification');
    await expect(page.locator('[data-testid="mock-notification-row"][data-notification-guid="hidden-drums-notification"]')).toHaveCount(0);
    await expect(page.getByTestId('notification-unread-dot')).toHaveCount(2);
  });

  test('mobile notification card opens a solo song with the instrument selected', async ({ page }, testInfo) => {
    test.skip(!testInfo.project.name.startsWith('mobile'), 'mobile-only notification shell behavior');

    await page.goto('/#/songs', { waitUntil: 'load' });
    await dismissFirstRunIfVisible(page);

    await page.getByTestId('mobile-header-notifications').click();
    const firstNotification = page.getByTestId('mock-notification-row').first();
    await expect(firstNotification).toHaveAttribute('data-actionable', 'true');
    await expect(firstNotification.getByTestId('notification-chevron')).toBeVisible();

    await firstNotification.click();

    await expect(page).toHaveURL(new RegExp(`#\\/songs\\/${APPLE_SONG_ID}\\?instrument=Solo_Drums`));
  });

  test('desktop validation mode opens a rank notification destination', async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'desktop', 'desktop validation is covered by the desktop project');

    await page.goto(`/?validation=${NOTIFICATIONS_VALIDATION_TOKEN}#/songs`, { waitUntil: 'load' });
    await dismissFirstRunIfVisible(page);

    await expectDesktopNotificationDrawer(page);

    const rankNotification = page.locator('[data-testid="mock-notification-row"][data-event-kind="player_weighted_rank_improved"]');
    await expect(rankNotification.getByTestId('notification-chevron')).toBeVisible();
    await rankNotification.click();

    await expect(page).toHaveURL(/#\/leaderboards\?rankBy=weighted/);
    await expect.poll(() => readLeaderboardRankBy(page)).toBe('weighted');
  });

  test('desktop validation mode opens a band song notification with played-instrument filters', async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'desktop', 'desktop validation is covered by the desktop project');

    await page.goto(`/?validation=${NOTIFICATIONS_VALIDATION_TOKEN}#/songs`, { waitUntil: 'load' });
    await dismissFirstRunIfVisible(page);

    await expectDesktopNotificationDrawer(page);

    const bandNotification = page.locator('[data-testid="mock-notification-row"][data-event-kind="band_score_pb"]');
    await expect(bandNotification).toBeVisible({ timeout: 10_000 });
    await page.evaluate(() => {
      document.querySelector('[data-testid="mock-notification-row"][data-event-kind="band_score_pb"]')?.scrollIntoView({ block: 'center' });
    });
    await page.waitForTimeout(250);
    await page.evaluate(() => {
      const row = document.querySelector('[data-testid="mock-notification-row"][data-event-kind="band_score_pb"]');
      if (!(row instanceof HTMLElement)) throw new Error('Band notification row was not available for click');
      row.click();
    });

    await expect(page).toHaveURL(new RegExp(`#\\/songs\\/${APPLE_SONG_ID}`));
    await expect.poll(() => readSelectedBandFilterCombo(page)).toBe('Solo_Bass+Solo_Bass+Solo_Drums');
  });
});

async function expectRowsNewestFirst(rows: Locator) {
  const detectedAtValues = await rows.evaluateAll((elements) => elements.map(element => element.getAttribute('data-detected-at') ?? ''));
  const sortedValues = [...detectedAtValues].sort((left, right) => Date.parse(right) - Date.parse(left));
  expect(detectedAtValues).toEqual(sortedValues);
}

async function expectNotificationFreshnessSections(page: Page, expectedNewCount: number, expectedOlderCount: number) {
  const newSection = page.locator('[data-testid="notification-section"][data-notification-section="new"]');
  const olderSection = page.locator('[data-testid="notification-section"][data-notification-section="older"]');
  await expect(newSection.getByTestId('notification-section-heading')).toHaveText('New');
  await expect(newSection.getByTestId('mock-notification-row')).toHaveCount(expectedNewCount);
  if (expectedOlderCount > 0) {
    await expect(olderSection.getByTestId('notification-section-heading')).toHaveText('Older');
    await expect(olderSection.getByTestId('mock-notification-row')).toHaveCount(expectedOlderCount);
  } else {
    await expect(olderSection).toHaveCount(0);
  }

  await expect.poll(() => readMockFreshnessNewCount(page), { timeout: 5_000 }).toBe(expectedNewCount);
}

async function expectDesktopNotificationDrawer(page: Page) {
  const dialog = page.getByRole('dialog', { name: 'Notifications' });
  await expect(dialog).toHaveAttribute('data-modal-placement', 'rightDrawer');
  await expect(page.getByTestId('desktop-notifications-drawer')).toBeVisible();

  await expect.poll(async () => {
    const box = await dialog.boundingBox();
    const viewport = page.viewportSize();
    if (!box || !viewport) return Number.POSITIVE_INFINITY;
    return Math.abs(box.x + box.width - viewport.width);
  }, { timeout: 5_000 }).toBeLessThanOrEqual(2);

  const box = await dialog.boundingBox();
  const viewport = page.viewportSize();
  if (!box || !viewport) throw new Error('Notification drawer geometry was unavailable');

  expect(Math.abs(box.x + box.width - viewport.width)).toBeLessThanOrEqual(2);
  expect(box.x).toBeGreaterThan(viewport.width / 2);
  expect(box.width).toBeGreaterThanOrEqual(420);
  expect(box.width).toBeLessThanOrEqual(470);
  expect(Math.abs(box.y)).toBeLessThanOrEqual(2);
  expect(Math.abs(box.height - viewport.height)).toBeLessThanOrEqual(2);

  const radii = await dialog.evaluate((element) => {
    const style = getComputedStyle(element);
    return {
      topLeft: style.borderTopLeftRadius,
      bottomLeft: style.borderBottomLeftRadius,
    };
  });
  expect(radii.topLeft).toBe('0px');
  expect(radii.bottomLeft).toBe('0px');
}

async function expectSoloInstrumentNotificationCopy(page: Page) {
  const playerPb = page.locator('[data-testid="mock-notification-row"][data-event-kind="player_score_pb"]').first();
  await expectNotificationTitle(playerPb, 'Apple · Pro Drums');
  await expect(playerPb.getByTestId('notification-summary')).toContainText('You set a new personal best on Pro Drums for Apple');

  const firstPlay = page.locator('[data-testid="mock-notification-row"][data-event-kind="player_first_score"]');
  await expectNotificationTitle(firstPlay, "Ghosts 'n' Stuff · Pro Drums");
  await expect(firstPlay.getByTestId('notification-summary')).toContainText("Your first Pro Drums play on Ghosts 'n' Stuff");

  await expectNotificationTitle(page.locator('[data-testid="mock-notification-row"][data-event-kind="player_weighted_rank_improved"]'), 'Weighted Rank Improved');
  await expectNotificationTitle(page.locator('[data-testid="mock-notification-row"][data-event-kind="band_weighted_rank_improved"]'), 'Weighted Rank Improved');
  await expectNotificationTitle(page.locator('[data-testid="mock-notification-row"][data-event-kind="band_score_pb"]'), 'Apple · Band Trios');

  const marqueeMarkers = await page.getByTestId('notification-title').evaluateAll((elements) => elements.map(element => element.getAttribute('data-marquee-title')));
  expect(marqueeMarkers.every(marker => marker === 'true')).toBe(true);
  await expectBandSongMediaCycle(page);

  const overflowingText = await page.locator('[data-testid="notification-summary"]').evaluateAll((elements) => elements
    .filter((element) => element.scrollWidth > element.clientWidth + 1)
    .map((element) => element.textContent ?? ''));
  expect(overflowingText).toEqual([]);
}

async function expectNotificationTitle(row: Locator, expectedTitle: string) {
  const title = row.getByTestId('notification-title');
  await expect(title).toHaveAttribute('data-marquee-title', 'true');
  await expect(title.locator('span').first()).toHaveText(expectedTitle);
}

async function expectBandSongMediaCycle(page: Page) {
  const bandRow = page.locator('[data-testid="mock-notification-row"][data-event-kind="band_score_pb"]');
  const cycle = bandRow.getByTestId('notification-media-cycle');
  await expect(cycle).toHaveAttribute('data-media-cycle', 'freRowSwap');
  await expect(cycle).toHaveAttribute('data-media-cycle-style', 'rowReplace');
  await expect(cycle).toHaveAttribute('data-media-cycle-epoch', '1700000000000');
  await expect(cycle).toHaveAttribute('data-media-cycle-duration', '10');
  await expect(cycle).toHaveAttribute('data-media-cycle-swap-interval', '5000');
  await expect(cycle).toHaveAttribute('data-media-cycle-fade-ms', '400');
  await expect.poll(async () => cycle.getAttribute('data-media-cycle-fading'), { timeout: 2_000 }).toBe('false');
  await expect(cycle.getByTestId('notification-media-cycle-art')).toBeAttached();
  await expect(cycle.getByAltText('Apple band notification album art')).toBeAttached();

  const cycleDetails = await cycle.evaluate((element) => {
    const icons = element.querySelector('[data-testid="notification-media-cycle-icons"]');
    const art = element.querySelector('[data-testid="notification-media-cycle-art"]');
    if (!(icons instanceof HTMLElement) || !(art instanceof HTMLElement)) return null;
    const cycleRect = element.getBoundingClientRect();
    const iconsRect = icons.getBoundingClientRect();
    const artRect = art.getBoundingClientRect();
    const iconsStyle = getComputedStyle(icons);
    const artStyle = getComputedStyle(art);
    return {
      activeLayer: element.getAttribute('data-media-cycle-active-layer'),
      cycle: { width: cycleRect.width, height: cycleRect.height },
      icons: { width: iconsRect.width, height: iconsRect.height, opacity: iconsStyle.opacity, transform: iconsStyle.transform, transitionDuration: iconsStyle.transitionDuration, transitionProperty: iconsStyle.transitionProperty },
      art: { width: artRect.width, height: artRect.height, opacity: artStyle.opacity, transform: artStyle.transform, transitionDuration: artStyle.transitionDuration, transitionProperty: artStyle.transitionProperty },
    };
  });

  expect(cycleDetails).not.toBeNull();
  expect(cycleDetails!.cycle).toEqual({ width: 64, height: 64 });
  expect(cycleDetails!.icons.width).toBe(64);
  expect(cycleDetails!.icons.height).toBe(64);
  expect(cycleDetails!.art.width).toBe(64);
  expect(cycleDetails!.art.height).toBe(64);
  expect(cycleDetails!.activeLayer).toMatch(/^(icons|art)$/);
  expect(cycleDetails!.icons.transitionProperty).toContain('opacity');
  expect(cycleDetails!.icons.transitionProperty).toContain('transform');
  expect(cycleDetails!.art.transitionProperty).toContain('opacity');
  expect(cycleDetails!.art.transitionProperty).toContain('transform');
  expect(cycleDetails!.icons.transitionDuration).toBe('0.4s, 0.4s');
  expect(cycleDetails!.art.transitionDuration).toBe('0.4s, 0.4s');
  const visibleOpacity = cycleDetails!.activeLayer === 'icons' ? cycleDetails!.icons.opacity : cycleDetails!.art.opacity;
  expect(visibleOpacity).toBe('1');
}

async function readBadgeCount(notificationBadge: Locator): Promise<number> {
  const text = await notificationBadge.textContent();
  const value = Number(text);
  return Number.isFinite(value) ? value : 0;
}

async function revealEveryNotification(page: Page) {
  const list = page.getByTestId('notification-list');
  const rows = page.getByTestId('mock-notification-row');
  const count = await rows.count();
  for (let index = 0; index < count; index += 1) {
    await rows.nth(index).evaluate((element) => element.scrollIntoView({ block: 'center' }));
    await list.evaluate((element) => element.dispatchEvent(new Event('scroll', { bubbles: true })));
    await page.waitForTimeout(100);
  }

  await list.evaluate((element) => {
    element.scrollTop = element.scrollHeight;
    element.dispatchEvent(new Event('scroll', { bubbles: true }));
  });
}

async function dismissFirstRunIfVisible(page: Page) {
  const overlay = page.getByTestId('fre-overlay');
  if ((await overlay.count()) === 0) return;
  if (!await overlay.isVisible().catch(() => false)) return;
  await page.getByTestId('fre-close').click();
  await expect(overlay).toBeHidden({ timeout: 5_000 });
}

async function readMockSeenCount(page: Page): Promise<number> {
  return page.evaluate((storageKey) => {
    const raw = localStorage.getItem(storageKey);
    if (!raw) return 0;
    const parsed = JSON.parse(raw) as Record<string, unknown>;
    const mockSeen = parsed.mock;
    return Array.isArray(mockSeen) ? mockSeen.length : 0;
  }, NOTIFICATION_SEEN_STORAGE_KEY);
}

async function readMockFreshnessNewCount(page: Page): Promise<number> {
  return page.evaluate((storageKey) => {
    const raw = localStorage.getItem(storageKey);
    if (!raw) return 0;
    const parsed = JSON.parse(raw) as Record<string, unknown>;
    const mockFreshness = parsed.mock;
    if (!mockFreshness || typeof mockFreshness !== 'object' || Array.isArray(mockFreshness)) return 0;
    const ids = (mockFreshness as { newNotificationIds?: unknown }).newNotificationIds;
    return Array.isArray(ids) ? ids.length : 0;
  }, NOTIFICATION_FRESHNESS_STORAGE_KEY);
}

async function readLeaderboardRankBy(page: Page): Promise<string | null> {
  return page.evaluate(() => {
    const raw = localStorage.getItem('fst:leaderboardSettings');
    if (!raw) return null;
    return (JSON.parse(raw) as { rankBy?: string }).rankBy ?? null;
  });
}

async function readSelectedBandFilterCombo(page: Page): Promise<string | null> {
  return page.evaluate(() => {
    const profileRaw = localStorage.getItem('fst:selectedProfile');
    const filterRaw = localStorage.getItem('fst:bandFilter');
    if (!profileRaw || !filterRaw) return null;
    const profile = JSON.parse(profileRaw) as { type?: string; bandType?: string };
    const filter = JSON.parse(filterRaw) as { bandType?: string; comboId?: string };
    return profile.type === 'band' && profile.bandType === filter.bandType ? filter.comboId ?? null : null;
  });
}

async function seedAppState(page: Page) {
  await page.goto('/', { waitUntil: 'commit' });
  await page.evaluate(
    ({ hash }) => {
      for (const key of Object.keys(localStorage).filter(key => key.startsWith('fst:'))) {
        localStorage.removeItem(key);
      }

      localStorage.setItem('fst:appSettings', JSON.stringify({ hideItemShop: true, disableShopHighlighting: true, enableExperimentalRanks: true }));
      localStorage.setItem('fst:changelog', JSON.stringify({ version: 'e2e', hash }));
    },
    { hash: changelogHash() },
  );
}

async function seedPlayerNotificationFilterState(page: Page, isMobile: boolean) {
  const seenSlides = seenRecordsForSlides(isMobile);
  await page.goto('/', { waitUntil: 'commit' });
  await page.evaluate(
    ({ hash, seenSlides }) => {
      for (const key of Object.keys(localStorage).filter(key => key.startsWith('fst:'))) {
        localStorage.removeItem(key);
      }

      const selectedProfile = { type: 'player', accountId: 'notification-filter-player', displayName: 'Notification Filter Player' };
      localStorage.setItem('fst:appSettings', JSON.stringify({ hideItemShop: true, disableShopHighlighting: true, showDrums: false }));
      localStorage.setItem('fst:firstRun', JSON.stringify(seenSlides));
      localStorage.setItem('fst:selectedProfile', JSON.stringify(selectedProfile));
      localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: selectedProfile.accountId, displayName: selectedProfile.displayName }));
      localStorage.setItem('fst:changelog', JSON.stringify({ version: 'e2e', hash }));
    },
    { hash: changelogHash(), seenSlides },
  );
}

type FirstRunSeenSlide = {
  id: string;
  version: number;
  title: string;
  description: string;
  contentKey?: string;
};

function seenRecordsForSlides(isMobile: boolean): FirstRunStorage {
  const navigationDescription = isMobile ? 'firstRun.songs.navigation.descriptionMobile' : 'firstRun.songs.navigation.descriptionDesktop';
  const slides: FirstRunSeenSlide[] = [
    { id: 'songs-song-list', version: 3, title: 'firstRun.songs.songList.title', description: 'firstRun.songs.songList.description' },
    { id: 'songs-sort', version: 5, title: 'firstRun.songs.sort.title', description: 'firstRun.songs.sort.description' },
    { id: 'songs-navigation', version: 5, title: 'firstRun.songs.navigation.title', description: navigationDescription, contentKey: 'songs-navigation' },
    { id: 'songs-filter', version: 4, title: 'firstRun.songs.filter.title', description: 'firstRun.songs.filter.description' },
    { id: 'songs-icons', version: 3, title: 'firstRun.songs.songIcons.title', description: 'firstRun.songs.songIcons.description' },
    { id: 'songs-metadata', version: 3, title: 'firstRun.songs.metadata.title', description: 'firstRun.songs.metadata.description' },
    { id: 'songs-shop-highlight', version: 1, title: 'firstRun.songs.shop.title', description: 'firstRun.songs.shop.description' },
    { id: 'songs-leaving-tomorrow', version: 1, title: 'firstRun.songs.leaving.title', description: 'firstRun.songs.leaving.description' },
  ];

  return Object.fromEntries(slides.map(slide => [
    slide.id,
    {
      version: slide.version,
      hash: contentHash(slide.contentKey ?? (slide.title + slide.description)),
      seenAt: '2026-05-09T00:00:00.000Z',
    },
  ]));
}

async function installApiMocks(page: Page) {
  await page.route(/^https?:\/\/[^/]+\/api\//, async (route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;

    if (path === '/api/songs') {
      await route.fulfill({ json: songsResponse() });
      return;
    }

    if (path === '/api/shop') {
      await route.fulfill({ json: { songs: [], lastUpdated: new Date(0).toISOString() } });
      return;
    }

    if (path === '/api/service-info') {
      await route.fulfill({ json: { lastCompletedUpdate: null, currentUpdate: { status: 'idle', startedAt: null, phase: null, subOperation: null }, nextScheduledUpdateAt: null } });
      return;
    }

    if (/^\/api\/player\/[^/]+\/notifications$/.test(path)) {
      await route.fulfill({ json: notificationResponse() });
      return;
    }

    await route.fulfill({ json: {} });
  });
}

function notificationResponse() {
  return {
    generatedAt: '2026-05-09T16:05:00Z',
    expiresAfterHours: 72,
    sourceRunId: 20260509,
    sourceCompletedAt: '2026-05-09T16:04:00Z',
    notificationsGenerated: true,
    items: [
      notificationItem({ eventId: 201, notificationGuid: 'hidden-drums-notification', eventKind: 'player_score_pb', instrument: 'Solo_Drums', detectedAt: '2026-05-09T16:00:00Z' }),
      notificationItem({ eventId: 202, notificationGuid: 'visible-guitar-notification', eventKind: 'player_score_pb', instrument: 'Solo_Guitar', detectedAt: '2026-05-09T16:01:00Z' }),
      notificationItem({ eventId: 203, notificationGuid: 'visible-band-notification', eventKind: 'band_score_pb', rankingScope: 'combo', comboId: 'Solo_Guitar+Solo_Drums', detectedAt: '2026-05-09T16:02:00Z' }),
    ],
  };
}

function notificationItem(overrides: Record<string, unknown>) {
  return {
    eventId: 0,
    notificationGuid: '',
    eventKind: 'player_score_pb',
    songId: APPLE_SONG_ID,
    metric: 'score',
    oldNumeric: 100000,
    newNumeric: 110000,
    oldRank: 1200,
    newRank: 900,
    detectedAt: '2026-05-09T16:00:00Z',
    expiresAt: '2026-05-12T16:00:00Z',
    ...overrides,
  };
}

function songsResponse() {
  return {
    count: 3,
    currentSeason: 9,
    songs: [
      {
        songId: APPLE_SONG_ID,
        title: 'Apple',
        artist: 'Charli xcx',
        year: 2024,
        durationSeconds: 151,
        difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 2 },
        maxScores: { Solo_Guitar: 100000, Solo_Bass: 90000, Solo_Drums: 110000, Solo_Vocals: 80000 },
      },
      {
        songId: STAND_AND_FIGHT_REMIX_SONG_ID,
        title: 'Stand and Fight (Remix)',
        artist: 'Epic Games',
        year: 2020,
        durationSeconds: 234,
        difficulty: { guitar: 4, bass: 3, drums: 5, vocals: 3 },
        maxScores: { Solo_Guitar: 120000, Solo_Bass: 95000, Solo_Drums: 126978, Solo_Vocals: 90000 },
      },
      {
        songId: GHOSTS_N_STUFF_SONG_ID,
        title: "Ghosts 'n' Stuff",
        artist: 'deadmau5, Rob Swire',
        year: 2008,
        durationSeconds: 328,
        difficulty: { guitar: 4, bass: 4, drums: 6, vocals: 3 },
        maxScores: { Solo_Guitar: 130000, Solo_Bass: 120000, Solo_Drums: 180005, Solo_Vocals: 100000 },
      },
    ],
  };
}
