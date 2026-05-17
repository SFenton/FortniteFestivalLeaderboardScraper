const { chromium } = require('@playwright/test');
const fs = require('fs/promises');
const path = require('path');

const BASE_URL = process.env.MANUAL_SCREENSHOT_BASE_URL ?? 'http://127.0.0.1:5173';
const OUT_DIR = path.resolve(__dirname, '../public/manual/screenshots');
const CHROMIUM_FALLBACK = '/home/sfenton/.cache/ms-playwright/chromium-1208/chrome-linux64/chrome';

const PLAYER = {
  type: 'player',
  accountId: '195e93ef108143b2975ee46662d4d0e1',
  displayName: 'SFentonX',
};

const BAND = {
  type: 'band',
  bandId: 'cf59dbdb-7f58-5309-a584-6aa01f314b8b',
  bandType: 'Band_Trios',
  teamKey: '195e93ef108143b2975ee46662d4d0e1:4c2a1300df4c49a9b9d2b352d704bdf0:db9342c9dd874c799b58f177ec899f5e',
  displayName: 'SFentonX + kahnyri + Phankie.ToT',
  members: [
    { accountId: '195e93ef108143b2975ee46662d4d0e1', displayName: 'SFentonX' },
    { accountId: '4c2a1300df4c49a9b9d2b352d704bdf0', displayName: 'kahnyri' },
    { accountId: 'db9342c9dd874c799b58f177ec899f5e', displayName: 'Phankie.ToT' },
  ],
};

const SONG_ID = '009f0d51-642b-4a86-b05e-486dbbfa4ace';
const INSTRUMENT = 'Solo_Guitar';

const VIEWPORTS = [
  { id: 'mobile', width: 390, height: 844, isMobile: true },
  { id: 'compact', width: 1024, height: 768, isMobile: false },
  { id: 'wide', width: 1440, height: 900, isMobile: false },
];

const defaultSongSettings = {
  sortMode: 'title',
  sortAscending: true,
  metadataOrder: ['score', 'percentage', 'percentile', 'stars', 'seasonachieved', 'intensity', 'difficulty', 'lastplayed'],
  instrumentOrder: ['Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals', 'Solo_PeripheralGuitar', 'Solo_PeripheralBass', 'Solo_PeripheralVocals', 'Solo_PeripheralCymbals', 'Solo_PeripheralDrums'],
  filters: {
    missingScores: {},
    missingFCs: {},
    hasScores: {},
    hasFCs: {},
    overThreshold: {},
    selectedBandHasScore: false,
    selectedBandMissingScore: false,
    individualBandMemberScoreFilters: {},
    seasonFilter: {},
    percentileFilter: {},
    starsFilter: {},
    difficultyFilter: {},
    shopInShop: false,
    shopLeavingTomorrow: false,
  },
  instrument: null,
};

const PLAN = [
  { slug: 'navigation-overview', route: '/songs', profile: null, expected: ['Blue Öyster Cult'] },
  { slug: 'navigation-sidebar', route: '/songs', profile: PLAYER, expected: ['Blue Öyster Cult'] },
  { slug: 'navigation-quick-links', route: '/songs', profile: PLAYER, action: 'quickLinks', expected: ['Blue Öyster Cult'] },
  { slug: 'navigation-mobile-actions', route: '/songs', profile: PLAYER, action: 'quickLinks', expected: ['Blue Öyster Cult'] },

  { slug: 'songs-overview', route: '/songs', profile: PLAYER, expected: ['Blue Öyster Cult'] },
  { slug: 'songs-search-sort-filter', route: '/songs', profile: PLAYER, action: 'songsControls', expected: ['Blue Öyster Cult'] },
  { slug: 'songs-rows', route: '/songs', profile: PLAYER, scroll: 420, expected: ['Blue Öyster Cult'] },
  { slug: 'songs-profile-sorts', route: '/songs', profile: PLAYER, songSettings: { instrument: INSTRUMENT, sortMode: 'score', sortAscending: false }, expected: ['Through the Fire and Flames'] },

  { slug: 'profiles-overview', route: '/songs', profile: null, action: 'profileSearch', expected: ['Blue Öyster Cult'] },
  { slug: 'profiles-player', route: `/player/${PLAYER.accountId}`, profile: PLAYER, expected: ['SFentonX'] },
  { slug: 'profiles-band', route: `/bands/${BAND.bandId}?accountId=${PLAYER.accountId}&bandType=${BAND.bandType}&teamKey=${encodeURIComponent(BAND.teamKey)}`, profile: BAND, expected: ['SFentonX', 'kahnyri'] },
  { slug: 'profiles-context', route: '/songs', profile: BAND, songSettings: { instrument: INSTRUMENT, sortMode: 'bandIntensity:Solo_Guitar' }, expected: ['Blue Öyster Cult'] },

  { slug: 'player-details-overview', route: `/player/${PLAYER.accountId}`, profile: PLAYER, expected: ['SFentonX'] },
  { slug: 'player-summary', route: `/player/${PLAYER.accountId}`, profile: PLAYER, scroll: 260, expected: ['SFentonX'] },
  { slug: 'player-instruments', route: '/statistics', profile: PLAYER, expected: ['SFentonX'] },
  { slug: 'player-navigation', route: `/player/${PLAYER.accountId}`, profile: PLAYER, action: 'quickLinks', expected: ['SFentonX'] },

  { slug: 'band-details-overview', route: `/bands/${BAND.bandId}?accountId=${PLAYER.accountId}&bandType=${BAND.bandType}&teamKey=${encodeURIComponent(BAND.teamKey)}`, profile: BAND, expected: ['SFentonX'] },
  { slug: 'band-members', route: `/bands/${BAND.bandId}?accountId=${PLAYER.accountId}&bandType=${BAND.bandType}&teamKey=${encodeURIComponent(BAND.teamKey)}`, profile: BAND, scroll: 260, expected: ['kahnyri'] },
  { slug: 'band-songs', route: `/bands/${BAND.bandId}?accountId=${PLAYER.accountId}&bandType=${BAND.bandType}&teamKey=${encodeURIComponent(BAND.teamKey)}`, profile: BAND, scroll: 620, expected: ['Songs'] },
  { slug: 'band-rank-history', route: `/bands/${BAND.bandId}?accountId=${PLAYER.accountId}&bandType=${BAND.bandType}&teamKey=${encodeURIComponent(BAND.teamKey)}`, profile: BAND, scroll: 430, expected: ['Rank'] },

  { slug: 'song-detail-overview', route: `/songs/${SONG_ID}`, profile: PLAYER, expected: ['Cake By The Ocean'] },
  { slug: 'song-detail-cards', route: `/songs/${SONG_ID}`, profile: PLAYER, scroll: 260, expected: ['Cake By The Ocean'] },
  { slug: 'song-detail-leaderboards', route: `/songs/${SONG_ID}/${INSTRUMENT}`, profile: PLAYER, expected: ['Cake By The Ocean'] },
  { slug: 'song-detail-paths-history', route: `/songs/${SONG_ID}/${INSTRUMENT}/history`, profile: PLAYER, expected: ['Cake By The Ocean'] },

  { slug: 'sync-overview', route: `/player/${PLAYER.accountId}`, profile: PLAYER, expected: ['SFentonX'] },
  { slug: 'sync-card', route: `/player/${PLAYER.accountId}`, profile: PLAYER, scroll: 120, syncStatusMock: 'history', expected: ['Building history'] },
  { slug: 'sync-after', route: `/player/${PLAYER.accountId}`, profile: PLAYER, scroll: 360, expected: ['History'] },
  { slug: 'sync-graphs', route: '/statistics', profile: PLAYER, scroll: 360, expected: ['Rank'] },

  { slug: 'suggestions-overview', route: '/suggestions', profile: PLAYER, expected: ['Suggestions'] },
  { slug: 'suggestions-solo', route: '/suggestions', profile: PLAYER, songSettings: { instrument: INSTRUMENT }, expected: ['Suggestions'] },
  { slug: 'suggestions-band', route: '/suggestions', profile: BAND, expected: ['Suggestions'] },
  { slug: 'suggestions-filters', route: '/suggestions', profile: PLAYER, action: 'suggestionsFilter', expected: ['Suggestions'] },

  { slug: 'compete-overview', route: '/compete', profile: PLAYER, expected: ['Compete'] },
  { slug: 'compete-mobile-hub', route: '/compete', profile: PLAYER, expected: ['Compete'] },
  { slug: 'compete-leaderboards', route: '/leaderboards', profile: PLAYER, expected: ['Leaderboards'] },
  { slug: 'compete-rivals', route: '/rivals', profile: PLAYER, expected: ['Rivals'] },

  { slug: 'leaderboards-rivals-overview', route: '/leaderboards', profile: PLAYER, expected: ['Leaderboards'] },
  { slug: 'leaderboards-full', route: `/leaderboards/all?instrument=${INSTRUMENT}&rankBy=rank`, profile: PLAYER, expected: ['Lead'] },
  { slug: 'leaderboards-metrics', route: `/leaderboards/all?instrument=${INSTRUMENT}&rankBy=adjusted`, profile: PLAYER, action: 'leaderboardMetric', expected: ['Leaderboards'] },
  { slug: 'rivals-solo', route: '/rivals', profile: PLAYER, expected: ['Rivals'] },

  { slug: 'shop-overview', route: '/shop', profile: PLAYER, expected: ['Shop'] },
  { slug: 'shop-grid-list', route: '/shop', profile: PLAYER, action: 'viewToggle', expected: ['Shop'] },
  { slug: 'shop-badges', route: '/shop', profile: PLAYER, scroll: 360, expected: ['Shop'] },
  { slug: 'shop-settings', route: '/settings', profile: PLAYER, scroll: 820, expected: ['Shop'] },

  { slug: 'settings-overview', route: '/settings', profile: PLAYER, expected: ['Settings'] },
  { slug: 'settings-preferences', route: '/settings', profile: PLAYER, scroll: 240, expected: ['General'] },
  { slug: 'settings-instruments', route: '/settings', profile: PLAYER, scroll: 520, expected: ['Instrument'] },
  { slug: 'settings-paths', route: '/settings', profile: PLAYER, scroll: 980, expected: ['Paths'] },
];

function urlFor(route) {
  return `${BASE_URL}/#${route}`;
}

function mergedSongSettings(overrides = {}) {
  return {
    ...defaultSongSettings,
    ...overrides,
    filters: { ...defaultSongSettings.filters, ...(overrides.filters ?? {}) },
  };
}

async function resetStorage(page, profile, songSettings) {
  await page.addInitScript(({ profile: selectedProfile, songSettings: settings }) => {
    localStorage.clear();
    localStorage.setItem('fst:changelog', JSON.stringify({ version: 'manual-screenshot', hash: 'manual-screenshot' }));
    localStorage.setItem('fst:firstRun', JSON.stringify({}));
    if (selectedProfile) {
      localStorage.setItem('fst:selectedProfile', JSON.stringify(selectedProfile));
      if (selectedProfile.type === 'player') {
        localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: selectedProfile.accountId, displayName: selectedProfile.displayName }));
      }
    }
    localStorage.setItem('fst:songSettings', JSON.stringify(settings));
  }, { profile, songSettings: mergedSongSettings(songSettings) });
}

async function installCaptureGuards(page) {
  await page.addInitScript(() => {
    const css = '[data-testid="fre-overlay"] { display: none !important; pointer-events: none !important; }';
    const injectStyle = () => {
      if (document.getElementById('manual-screenshot-capture-guards')) return;
      const style = document.createElement('style');
      style.id = 'manual-screenshot-capture-guards';
      style.textContent = css;
      (document.head ?? document.documentElement).appendChild(style);
    };
    injectStyle();
    document.addEventListener('DOMContentLoaded', injectStyle, { once: true });
  });
}

async function suppressFirstRunOverlay(page) {
  await page.addStyleTag({
    content: '[data-testid="fre-overlay"] { display: none !important; pointer-events: none !important; }',
  }).catch(() => {});
}

async function dismissOverlays(page) {
  for (let attempt = 0; attempt < 3; attempt += 1) {
    const close = page.getByRole('button', { name: /close|dismiss|don't show again/i }).first();
    if (await close.isVisible().catch(() => false)) {
      await close.click().catch(() => {});
      await page.waitForTimeout(250);
    }
  }
}

async function readBodyText(page, minLength = 80, timeout = 8000) {
  const deadline = Date.now() + timeout;
  let bodyText = '';
  do {
    bodyText = await page.locator('body').innerText({ timeout: 5000 }).catch(() => '');
    if (bodyText.length >= minLength) return bodyText;
    await page.waitForTimeout(400);
  } while (Date.now() < deadline);
  return bodyText;
}

function buildSyncStatusMock(phase) {
  const now = new Date().toISOString();
  const completeBackfill = {
    status: 'complete',
    songsChecked: 659,
    totalSongsToCheck: 659,
    entriesFound: 2754,
    currentSongName: null,
    startedAt: now,
    completedAt: now,
    rankingsPending: false,
    deferredReason: null,
  };

  const activeHistory = {
    status: 'in_progress',
    songsProcessed: 184,
    totalSongsToProcess: 659,
    seasonsQueried: 8,
    historyEntriesFound: 1426,
    currentSongName: 'Cake By The Ocean',
    startedAt: now,
    completedAt: null,
  };

  return {
    accountId: PLAYER.accountId,
    isTracked: true,
    pendingRankUpdate: false,
    backfill: completeBackfill,
    historyRecon: phase === 'history' ? activeHistory : null,
    rivals: null,
    postScrape: null,
  };
}

async function installNetworkMocks(page, entry) {
  if (!entry.syncStatusMock) return;

  await page.route(`**/api/player/${PLAYER.accountId}/track`, async route => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        accountId: PLAYER.accountId,
        displayName: PLAYER.displayName,
        trackingStarted: false,
        backfillStatus: 'complete',
        backfillKicked: false,
        syncDeferred: false,
        deferredReason: null,
        pendingRankUpdate: false,
      }),
    });
  });

  await page.route(`**/api/player/${PLAYER.accountId}/sync-status`, async route => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(buildSyncStatusMock(entry.syncStatusMock)),
    });
  });
}

async function waitForPageReady(page, expected) {
  await page.waitForLoadState('domcontentloaded');
  await page.waitForLoadState('networkidle', { timeout: 12000 }).catch(() => {});
  await dismissOverlays(page);
  await page.waitForTimeout(350);
  const bodyText = await readBodyText(page);
  if (bodyText.length < 80) throw new Error(`body text too short (${bodyText.length})`);
  if (/Reference image pending|Something went wrong|Service Unavailable|temporarily unavailable|API \d{3}/i.test(bodyText)) {
    throw new Error(`unexpected page text: ${bodyText.slice(0, 160).replace(/\s+/g, ' ')}`);
  }
  const textLower = bodyText.toLowerCase();
  const missing = expected.filter(term => !term.split('|').some(option => textLower.includes(option.toLowerCase())));
  if (missing.length > 0) {
    throw new Error(`missing expected text: ${missing.join(', ')} in ${bodyText.slice(0, 240).replace(/\s+/g, ' ')}`);
  }
}

async function scrollPage(page, y) {
  if (!y) return;
  await page.evaluate((nextY) => {
    const scrollArea = document.querySelector('[data-testid="page-root"]') ?? document.scrollingElement ?? document.documentElement;
    scrollArea.scrollTo({ top: nextY, behavior: 'instant' });
  }, y);
  await page.waitForTimeout(250);
}

async function clickVisible(locator, timeout = 6000) {
  if (!await locator.isVisible().catch(() => false)) return false;
  try {
    await locator.click({ timeout });
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    if (!/intercepts pointer events|Timeout/i.test(message)) throw error;
    await locator.evaluate(element => element.click());
  }
  return true;
}

async function runAction(page, action, viewportId) {
  if (!action) return;
  if (action === 'quickLinks') {
    if (viewportId !== 'wide') {
      const trigger = page.getByRole('button', { name: /Manual Sections|Quick Links|Actions/i }).first();
      if (await trigger.isVisible().catch(() => false)) await trigger.click({ timeout: 6000 });
    }
    await page.waitForTimeout(250);
    return;
  }
  if (action === 'profileSearch') {
    const triggers = [
      page.getByTestId('mobile-header-profile'),
      page.getByTestId('desktop-header-profile'),
      page.getByRole('button', { name: /Select Profile|Profile/i }).first(),
    ];
    for (const trigger of triggers) {
      if (await trigger.isVisible().catch(() => false)) {
        await trigger.click({ timeout: 6000 });
        break;
      }
    }
    await page.waitForTimeout(350);
    return;
  }
  if (action === 'songsControls') {
    const trigger = page.getByRole('button', { name: /Filter|Sort|Actions/i }).first();
    if (await trigger.isVisible().catch(() => false)) await trigger.click({ timeout: 6000 });
    await page.waitForTimeout(350);
    return;
  }
  if (action === 'suggestionsFilter') {
    const trigger = page.getByRole('button', { name: /Filter Suggestions|Filter/i }).first();
    if (await trigger.isVisible().catch(() => false)) await trigger.click({ timeout: 6000 });
    await page.waitForTimeout(350);
    return;
  }
  if (action === 'leaderboardMetric') {
    const trigger = page.getByRole('button', { name: /Change Leaderboard Ranking|Rank By|Total Score|Adjusted Percentile|Popularity-Weighted Percentile|FC Rate|Max Score/i }).first();
    if (!await clickVisible(trigger)) {
      const actions = page.getByRole('button', { name: /Actions/i }).first();
      if (await clickVisible(actions)) {
        await page.waitForTimeout(250);
        const metricAction = page.getByRole('button', { name: /Change Leaderboard Ranking/i }).first();
        await clickVisible(metricAction);
      }
    }
    await page.waitForTimeout(350);
    return;
  }
  if (action === 'viewToggle') {
    const trigger = page.getByRole('button', { name: /Grid|List|View/i }).first();
    if (await trigger.isVisible().catch(() => false)) await trigger.click({ timeout: 6000 });
    await page.waitForTimeout(250);
  }
}

function pngDimensions(buffer) {
  return { width: buffer.readUInt32BE(16), height: buffer.readUInt32BE(20) };
}

async function captureOne(browser, entry, viewport) {
  const page = await browser.newPage({
    colorScheme: 'dark',
    viewport: { width: viewport.width, height: viewport.height },
  });
  page.setDefaultTimeout(10000);
  page.setDefaultNavigationTimeout(30000);
  await installCaptureGuards(page);
  try {
    await installNetworkMocks(page, entry);
    await resetStorage(page, entry.profile, entry.songSettings);
    await page.goto(urlFor(entry.route), { waitUntil: 'domcontentloaded' });
    await suppressFirstRunOverlay(page);
    await waitForPageReady(page, entry.expected);
    await scrollPage(page, entry.scroll);
    await suppressFirstRunOverlay(page);
    await runAction(page, entry.action, viewport.id);
    await waitForCaptureState(page, entry, viewport.id);
    const outputPath = path.join(OUT_DIR, `${entry.slug}-${viewport.id}.png`);
    const buffer = await page.screenshot({ path: outputPath, fullPage: false, timeout: 15000 });
    const dims = pngDimensions(buffer);
    if (dims.width !== viewport.width || dims.height !== viewport.height) {
      throw new Error(`wrong PNG dimensions ${dims.width}x${dims.height}, expected ${viewport.width}x${viewport.height}`);
    }
    if (buffer.length < 25_000) throw new Error(`screenshot too small (${buffer.length} bytes)`);
    return { outputPath, bytes: buffer.length };
  } finally {
    await page.close().catch(() => {});
  }
}

async function waitForCaptureState(page, entry, viewportId) {
  await page.waitForTimeout(250);
  const bodyText = await page.locator('body').innerText({ timeout: 5000 });
  if (/Reference image pending|Loading\.\.\.|Something went wrong|Service Unavailable|temporarily unavailable/i.test(bodyText)) {
    throw new Error(`bad capture state for ${entry.slug}-${viewportId}: ${bodyText.slice(0, 220).replace(/\s+/g, ' ')}`);
  }
  if (entry.action === 'quickLinks' && viewportId !== 'wide' && !/Quick Links|Manual Sections/i.test(bodyText)) {
    throw new Error(`quick links action did not open modal for ${entry.slug}-${viewportId}`);
  }
  if (entry.action === 'profileSearch' && !/Search\s+Players|Search players|Players\s+Bands|Select Profile|Profile/i.test(bodyText)) {
    throw new Error(`profile search action did not expose profile controls for ${entry.slug}-${viewportId}`);
  }
  if (entry.action === 'suggestionsFilter' && !/Filter Suggestions|Instruments|General|Apply/i.test(bodyText)) {
    throw new Error(`suggestions filter action did not open filter modal for ${entry.slug}-${viewportId}`);
  }
  if (entry.action === 'leaderboardMetric' && !/Rank By|Adjusted Percentile|Popularity-Weighted Percentile|FC Rate|Max Score|Total Score/i.test(bodyText)) {
    throw new Error(`leaderboard metric action did not expose metric controls for ${entry.slug}-${viewportId}`);
  }
}

async function captureWithRetries(browser, entry, viewport) {
  let lastError;
  for (let attempt = 1; attempt <= 3; attempt += 1) {
    try {
      return await captureOne(browser, entry, viewport);
    } catch (error) {
      lastError = error;
      if (attempt === 3) break;
      const message = error instanceof Error ? error.message : String(error);
      console.warn(`retrying ${entry.slug}-${viewport.id} after capture failure (${attempt}/3): ${message}`);
      await new Promise(resolve => setTimeout(resolve, attempt * 1000));
    }
  }
  throw lastError;
}

async function main() {
  await fs.rm(OUT_DIR, { recursive: true, force: true });
  await fs.mkdir(OUT_DIR, { recursive: true });
  const browser = await chromium.launch({
    executablePath: process.env.MANUAL_SCREENSHOT_CHROMIUM ?? CHROMIUM_FALLBACK,
    headless: true,
    args: ['--no-sandbox', '--disable-setuid-sandbox', '--disable-gpu', '--disable-dev-shm-usage'],
  });
  const results = [];
  try {
    for (const entry of PLAN) {
      for (const viewport of VIEWPORTS) {
        const result = await captureWithRetries(browser, entry, viewport);
        results.push(result);
        console.log(`captured ${path.basename(result.outputPath)} ${result.bytes} bytes`);
      }
    }
  } finally {
    await browser.close();
  }
  console.log(`validated ${results.length} manual screenshots from ${PLAN.length} routes across ${VIEWPORTS.length} viewport configurations`);
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});