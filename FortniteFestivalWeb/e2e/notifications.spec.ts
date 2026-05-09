import type { Locator, Page } from '@playwright/test';
import { test, expect } from './fixtures/fre';
import { changelogHash } from '../src/changelog';

const NOTIFICATION_SEEN_STORAGE_KEY = 'fst:notificationSeen:v1';
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
    await expectRowsNewestFirst(rows);
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
    await expect(page.getByRole('button', { name: 'Actions' })).toHaveCount(0);
  });

  test('desktop validation mode exercises the same modal seen-state lifecycle', async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'desktop', 'desktop validation is covered by the desktop project');

    await page.goto(`/?validation=${NOTIFICATIONS_VALIDATION_TOKEN}#/songs`, { waitUntil: 'load' });
    await dismissFirstRunIfVisible(page);

    const dialog = page.getByRole('dialog', { name: 'Notifications' });
    const rows = page.getByTestId('mock-notification-row');
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    await expect(rows).toHaveCount(EXPECTED_NOTIFICATION_COUNT);
    await expectRowsNewestFirst(rows);
    await expect(page.getByTestId('notification-unread-dot')).toHaveCount(EXPECTED_NOTIFICATION_COUNT);

    await revealEveryNotification(page);

    await expect.poll(() => readMockSeenCount(page), { timeout: 5_000 }).toBe(EXPECTED_NOTIFICATION_COUNT);
    await expect(page.getByTestId('notification-unread-dot')).toHaveCount(EXPECTED_NOTIFICATION_COUNT);

    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(dialog).toBeHidden({ timeout: 5_000 });
    await page.reload({ waitUntil: 'load' });
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('notification-unread-dot')).toHaveCount(0);
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
    await expect(page.getByTestId('mock-notification-row')).toHaveCount(EXPECTED_NOTIFICATION_COUNT);
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

      localStorage.setItem('fst:appSettings', JSON.stringify({ hideItemShop: true, disableShopHighlighting: true }));
      localStorage.setItem('fst:changelog', JSON.stringify({ version: 'e2e', hash }));
    },
    { hash: changelogHash() },
  );
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

    await route.fulfill({ json: {} });
  });
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
