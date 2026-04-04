const { chromium } = require('playwright');
const fs = require('fs');

const VIEWPORTS = [
  { name: '1920x1080', width: 1920, height: 1080 },
  { name: '1440x900', width: 1440, height: 900 },
  { name: '1280x800', width: 1280, height: 800 },
  { name: '800x800', width: 800, height: 800 },
  { name: '375x812', width: 375, height: 812 },
  { name: '320x568', width: 320, height: 568 },
];

(async () => {
  // Fetch real songs to build mock scores
  const songsFetch = await fetch('http://localhost:3000/api/songs');
  const songsData = await songsFetch.json();
  const songs = songsData.songs;
  console.log('Songs loaded:', songs.length);

  // Generate mock wire-format scores for Solo_Guitar (hex '01')
  // Wire format: ins is hex instrument code, acc is accuracy/1000
  const mockScores = songs.map((song, i) => {
    const maxScore = song.maxScores?.Solo_Guitar ?? 200000;
    if (!maxScore || maxScore <= 0) return null;
    const pct = 0.80 + Math.random() * 0.20;
    const score = Math.round(maxScore * pct);
    const accPct = 95 + Math.random() * 5; // 95-100%
    return {
      si: song.songId,       // songId
      ins: '01',              // Solo_Guitar = bit 0 = hex 0x01
      sc: score,              // score
      acc: accPct / 1000,     // accuracy in wire format (divided by 1000)
      fc: accPct > 99.5,      // full combo
      st: accPct > 99 ? 6 : accPct > 97 ? 5 : 4, // stars
      dif: song.difficulty?.guitar ?? 3,
      sn: 12,                 // season
      pct: Math.max(0.1, Math.round((100 - i * 0.15) * 10) / 10), // percentile
      rk: Math.round(1 + i * 5 + Math.random() * 10), // rank
      te: 5000 + Math.round(Math.random() * 5000),     // total entries
      ml: Math.round((1 - pct) * 10000) / 100,         // minLeeway (% distance)
    };
  }).filter(Boolean);

  const mockPlayerResponse = {
    accountId: '195e93ef108143b2975ee46662d4d0e1',
    displayName: 'SFentonX',
    totalScores: mockScores.length,
    scores: mockScores,
  };
  console.log('Mock scores:', mockScores.length);

  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({ viewport: VIEWPORTS[0] });
  const page = await context.newPage();

  // Intercept ALL player API calls
  await page.route('**/api/player/**', (route, request) => {
    const url = request.url();
    if (url.includes('/sync-status')) {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ isCompleted: true, queuePosition: 0 }) });
    }
    if (url.includes('/track')) {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ accountId: '195e93ef108143b2975ee46662d4d0e1', displayName: 'SFentonX' }) });
    }
    if (url.includes('/stats')) {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({}) });
    }
    // Default: return player with scores
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(mockPlayerResponse) });
  });

  // Navigate first to set localStorage
  await page.goto('http://localhost:3000', { waitUntil: 'networkidle', timeout: 30000 });
  await page.waitForTimeout(1000);

  await page.evaluate(() => {
    // Dismiss changelog 
    localStorage.setItem('fst:changelog', JSON.stringify({ version: '99.99.99', hash: 'always-seen' }));
    
    // Set tracked player
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({
      accountId: '195e93ef108143b2975ee46662d4d0e1',
      displayName: 'SFentonX'
    }));
    
    // Song settings: Solo_Guitar instrument (not just 'Guitar')!
    localStorage.setItem('fst:songSettings', JSON.stringify({
      sortMode: 'maxdistance',
      sortAscending: false,
      metadataOrder: ['score', 'percentage', 'percentile', 'stars', 'seasonachieved', 'intensity', 'maxdistance'],
      instrumentOrder: ['Solo_Guitar', 'Solo_Bass', 'Solo_Vocals', 'Solo_Drums', 'Solo_PeripheralGuitar', 'Solo_PeripheralBass'],
      filters: {
        missingScores: {}, missingFCs: {}, hasScores: {}, hasFCs: {},
        overThreshold: {}, seasonFilter: {}, percentileFilter: {},
        starsFilter: {}, difficultyFilter: {},
      },
      instrument: 'Solo_Guitar',
    }));
  });

  // Reload
  await page.reload({ waitUntil: 'networkidle', timeout: 30000 });
  await page.waitForTimeout(5000);

  // Dismiss FRE overlays
  for (let i = 0; i < 20; i++) {
    const fre = page.locator('[data-testid="fre-overlay"]');
    if (await fre.isVisible({ timeout: 500 }).catch(() => false)) {
      const close = page.locator('[data-testid="fre-close"]');
      if (await close.isVisible({ timeout: 300 }).catch(() => false)) {
        await close.click({ force: true });
        await page.waitForTimeout(600);
      } else {
        await fre.click({ position: { x: 5, y: 5 }, force: true });
        await page.waitForTimeout(600);
      }
    } else break;
  }
  for (let i = 0; i < 5; i++) {
    const dismiss = page.locator('button:has-text("Dismiss")');
    if (await dismiss.isVisible({ timeout: 500 }).catch(() => false)) {
      await dismiss.click({ force: true });
      await page.waitForTimeout(600);
    } else break;
  }

  await page.waitForTimeout(3000);
  console.log('URL:', page.url());
  
  // Debug: check first song row content
  const firstRowText = await page.evaluate(() => {
    const firstRow = document.querySelector('a[href*="/songs/"]');
    if (!firstRow) return 'NO ROWS';
    return {
      innerText: firstRow.innerText.substring(0, 200),
      childCount: firstRow.children.length,
      innerHTML: firstRow.innerHTML.substring(0, 500),
    };
  });
  console.log('First row:', JSON.stringify(firstRowText, null, 2));
  await page.screenshot({ path: 'test-results/m-final2-setup.png', fullPage: false });

  // MEASUREMENTS
  const results = { viewports: [] };
  for (const vp of VIEWPORTS) {
    console.log('\\n=== Viewport:', vp.name, '===');
    await page.setViewportSize({ width: vp.width, height: vp.height });
    await page.waitForTimeout(2000);
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.waitForTimeout(500);

    const m = await page.evaluate(() => {
      const r = {};
      r.horizontalOverflow = document.documentElement.scrollWidth > window.innerWidth;
      r.documentScrollWidth = document.documentElement.scrollWidth;
      r.windowInnerWidth = window.innerWidth;

      let container = null;
      for (const div of document.querySelectorAll('div')) {
        if (getComputedStyle(div).maxWidth === '1400px') { container = div; break; }
      }
      if (container) {
        const cr = container.getBoundingClientRect();
        r.containerWidth = Math.round(cr.width * 100) / 100;
        r.containerPaddingLeft = parseFloat(getComputedStyle(container).paddingLeft);
        r.containerPaddingRight = parseFloat(getComputedStyle(container).paddingRight);
      } else r.containerWidth = null;

      const rows = Array.from(document.querySelectorAll('a[href*="/songs/"]'));
      r.songRowCount = rows.length;
      if (!rows.length) { r.note = 'no rows'; return r; }

      let row = rows.find(el => el.getBoundingClientRect().top >= -20 && el.getBoundingClientRect().height > 0) || rows[0];
      const rr = row.getBoundingClientRect();
      const rcs = getComputedStyle(row);
      r.rowFlexDirection = rcs.flexDirection;
      r.rowHeight = Math.round(rr.height * 100) / 100;
      r.rowWidth = Math.round(rr.width * 100) / 100;
      r.rowLeft = Math.round(rr.left * 100) / 100;
      r.rowRight = Math.round(rr.right * 100) / 100;
      r.rowOverflow = rcs.overflow;
      r.isCompactMode = rcs.flexDirection === 'column';
      r.rowInnerText = row.innerText.substring(0, 200).replace(/\n/g, ' | ');

      const dc = Array.from(row.children);
      r.rowDirectChildren = dc.map(c => {
        const cr2 = c.getBoundingClientRect();
        const cs2 = getComputedStyle(c);
        return {
          tag: c.tagName, display: cs2.display, flexDir: cs2.flexDirection,
          flexShrink: cs2.flexShrink, flexGrow: cs2.flexGrow, minWidth: cs2.minWidth,
          overflow: cs2.overflow,
          w: Math.round(cr2.width * 100) / 100, h: Math.round(cr2.height * 100) / 100,
          l: Math.round(cr2.left * 100) / 100, r: Math.round(cr2.right * 100) / 100,
          text: c.innerText.substring(0, 100).replace(/\n/g, ' '),
          childCount: c.children.length,
        };
      });

      // Find scoreMeta - use direct children first, then look deeper
      let scoreMeta = null;
      // In desktop mode: scoreMeta is a direct child with display:flex, flexDirection:row, multiple children, numbers
      // In compact mode: scoreMeta is the 2nd direct child (metadata bottom row)
      for (const child of dc) {
        const ccs = getComputedStyle(child);
        if (ccs.display === 'flex' && ccs.flexDirection === 'row' && child.children.length >= 2 && child.innerText.match(/\d/)) {
          scoreMeta = child;
          // In compact mode, prefer the LAST matching child (bottom metadata row)
        }
      }

      if (scoreMeta) {
        const smr = scoreMeta.getBoundingClientRect();
        const scs = getComputedStyle(scoreMeta);
        r.scoreMetaFound = true;
        r.scoreMetaWidth = Math.round(smr.width * 100) / 100;
        r.scoreMetaLeft = Math.round(smr.left * 100) / 100;
        r.scoreMetaRight = Math.round(smr.right * 100) / 100;
        r.scoreMetaOverflow = scs.overflow;
        r.scoreMetaFlexWrap = scs.flexWrap;
        r.scoreMetaFlexShrink = scs.flexShrink;
        r.scoreMetaGap = scs.gap;
        r.scoreMetaScrollWidth = scoreMeta.scrollWidth;
        r.scoreMetaClientWidth = Math.round(scoreMeta.clientWidth * 100) / 100;
        r.scoreMetaScrollWidthExceedsClient = scoreMeta.scrollWidth > scoreMeta.clientWidth;
        r.scoreMetaMinWidth = scs.minWidth;

        const smc = Array.from(scoreMeta.children);
        let rightmost = 0;
        r.scoreMetaChildren = smc.map(c => {
          const cr3 = c.getBoundingClientRect();
          const cs3 = getComputedStyle(c);
          if (cr3.right > rightmost) rightmost = cr3.right;
          return {
            tag: c.tagName, text: (c.innerText || c.textContent || '').substring(0, 50).replace(/\n/g, ' '),
            w: Math.round(cr3.width * 100) / 100, l: Math.round(cr3.left * 100) / 100,
            r: Math.round(cr3.right * 100) / 100,
            minWidth: cs3.minWidth, flexShrink: cs3.flexShrink,
          };
        });
        r.rightmostChildRight = Math.round(rightmost * 100) / 100;
        r.childOverflowsRow = rightmost > rr.right + 1;

        const deepEls = scoreMeta.querySelectorAll('span, div');
        const pills = []; let sep = null;
        for (const el of deepEls) {
          const ecs = getComputedStyle(el); const er = el.getBoundingClientRect();
          const t = el.innerText.trim();
          if (parseInt(ecs.fontWeight) >= 600 && t.match(/^[\d,]+$/) && er.width > 3) pills.push({ text: t, w: Math.round(er.width * 100) / 100 });
          if (t === '/') sep = { w: Math.round(er.width * 100) / 100 };
        }
        r.scorePills = pills;
        r.scorePillWidths = pills.map(p => p.w);
        r.separatorWidth = sep ? sep.w : null;
      } else {
        r.scoreMetaFound = false;
      }
      return r;
    });

    m.viewport = vp.name;
    await page.screenshot({ path: 'test-results/measure-vp-' + vp.name + '.png' });
    m.screenshot = 'test-results/measure-vp-' + vp.name + '.png';
    results.viewports.push(m);
    console.log(JSON.stringify(m, null, 2));
  }

  fs.writeFileSync('test-results/song-measurements.json', JSON.stringify(results, null, 2));
  console.log('\\n=== ALL MEASUREMENTS COMPLETE ===');
  await browser.close();
})().catch(err => { console.error('FATAL:', err.message, err.stack); process.exit(1); });



