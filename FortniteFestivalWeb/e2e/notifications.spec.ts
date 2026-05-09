import type { Locator, Page } from '@playwright/test';
import { test, expect } from './fixtures/fre';
import { changelogHash } from '../src/changelog';

const NOTIFICATION_SEEN_STORAGE_KEY = 'fst:notificationSeen:v1';
const EXPECTED_NOTIFICATION_COUNT = 6;
const NOTIFICATIONS_VALIDATION_TOKEN = 'notifications-open';

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
    count: 1,
    currentSeason: 9,
    songs: [{
      songId: 'song-a',
      title: 'Notification E2E Song',
      artist: 'Festival QA',
      year: 2026,
      durationSeconds: 180,
      difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1 },
      maxScores: { Solo_Guitar: 100000, Solo_Bass: 90000, Solo_Drums: 110000, Solo_Vocals: 80000 },
    }],
  };
}