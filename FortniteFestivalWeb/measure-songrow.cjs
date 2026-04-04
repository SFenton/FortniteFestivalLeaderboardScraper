const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({ headless: true });
  const viewports = [1100, 1280, 1440, 1920];

  for (const width of viewports) {
    const context = await browser.newContext({ viewport: { width, height: 900 } });
    const page = await context.newPage();

    try {
      // Inject localStorage BEFORE navigation
      await page.addInitScript(() => {
        localStorage.setItem('fst:trackedPlayer', JSON.stringify({
          accountId: '195e93ef108143b2975ee46662d4d0e1',
          displayName: 'SFentonX'
        }));
        // Also skip first-run carousel
        localStorage.setItem('fst:firstRunComplete', 'true');
      });

      await page.goto('http://localhost:3001/', { waitUntil: 'networkidle', timeout: 15000 });

      // Close any modal/overlay that may appear
      try {
        const closeBtn = page.locator('button:has-text("×"), button:has-text("Close"), [aria-label="Close"]');
        if (await closeBtn.count() > 0) {
          await closeBtn.first().click();
          await page.waitForTimeout(500);
        }
      } catch (_) {}

      // Wait for song rows to render (virtualizer needs scroll container)
      await page.waitForTimeout(3000);

      console.log('=== Viewport: ' + width + 'px ===');
      console.log('Page loaded: yes');
      console.log('Page title: ' + await page.title());
      console.log('URL: ' + page.url());

      // Find song rows: they are <a> tags with href containing /songs/ inside div[data-index] wrappers
      const songRowCount = await page.evaluate(() => {
        return document.querySelectorAll('div[data-index] a[href*="/songs/"]').length;
      });
      
      // Also try: direct <a> children of data-index divs
      const altCount = await page.evaluate(() => {
        return document.querySelectorAll('div[data-index] > a').length;
      });

      // Also check what is actually in the DOM
      const domDebug = await page.evaluate(() => {
        const dataIdxDivs = document.querySelectorAll('div[data-index]');
        const anchors = document.querySelectorAll('a[href*="songs"]');
        // Check for the container ref div (the one with maxWidth style)
        const allDivs = document.querySelectorAll('div');
        let containerInfo = null;
        for (const d of allDivs) {
          const s = d.style;
          if (s.maxWidth && s.maxWidth.includes('px') && s.margin === '0px auto') {
            containerInfo = {
              maxWidth: s.maxWidth,
              width: d.getBoundingClientRect().width,
              right: d.getBoundingClientRect().right,
              left: d.getBoundingClientRect().left,
              childCount: d.children.length
            };
            break;
          }
        }
        return {
          dataIndexDivs: dataIdxDivs.length,
          songsAnchors: anchors.length,
          containerInfo,
          bodyWidth: document.body.getBoundingClientRect().width,
          firstRunModal: !!document.querySelector('[class*="carousel"], [class*="Carousel"], [class*="modal"], [class*="Modal"]'),
        };
      });
      console.log('DOM debug: data-index divs=' + domDebug.dataIndexDivs +
        ', songs anchors=' + domDebug.songsAnchors +
        ', bodyWidth=' + domDebug.bodyWidth);
      console.log('Container info: ' + JSON.stringify(domDebug.containerInfo));
      console.log('First-run modal present: ' + domDebug.firstRunModal);

      let songRows;
      if (songRowCount > 0) {
        songRows = await page.$$('div[data-index] a[href*="/songs/"]');
        console.log('Selector: div[data-index] a[href*="/songs/"] (' + songRows.length + ')');
      } else if (altCount > 0) {
        songRows = await page.$$('div[data-index] > a');
        console.log('Selector: div[data-index] > a (' + songRows.length + ')');
      } else {
        // Fallback: find any <a> that looks like a song row
        songRows = await page.$$('a[href*="songs"]');
        if (songRows.length > 0) {
          console.log('Fallback selector: a[href*="songs"] (' + songRows.length + ')');
        }
      }

      if (songRows && songRows.length > 0) {
        console.log('Song rows found: yes (' + songRows.length + ' total)');
        const measureCount = Math.min(3, songRows.length);

        for (let i = 0; i < measureCount; i++) {
          const row = songRows[i];
          const data = await row.evaluate((el) => {
            const rect = el.getBoundingClientRect();
            const cs = getComputedStyle(el);
            const firstChildDiv = el.querySelector(':scope > div');
            const flexDir = firstChildDiv ? getComputedStyle(firstChildDiv).flexDirection : cs.flexDirection;
            return {
              height: rect.height,
              width: rect.width,
              right: rect.right,
              left: rect.left,
              top: rect.top,
              offsetHeight: el.offsetHeight,
              flexDirection: cs.flexDirection,
              firstChildFlexDir: flexDir,
              display: cs.display,
              tag: el.tagName,
              href: el.getAttribute('href') || ''
            };
          });
          console.log('--- Song Row #' + (i + 1) + ' ---');
          console.log('  tag: ' + data.tag + ' href: ' + data.href.slice(0, 40));
          console.log('  height: ' + data.height.toFixed(1) + 'px (offset: ' + data.offsetHeight + 'px)');
          console.log('  width: ' + data.width.toFixed(1) + 'px');
          console.log('  left: ' + data.left.toFixed(1) + 'px, right: ' + data.right.toFixed(1) + 'px');
          console.log('  top: ' + data.top.toFixed(1) + 'px');
          console.log('  display: ' + data.display + ', flexDirection: ' + data.flexDirection);
          console.log('  First child flexDirection: ' + data.firstChildFlexDir);
          console.log('  Layout type: ' + (data.flexDirection === 'row' ? 'DESKTOP (row)' : data.flexDirection === 'column' ? 'MOBILE (stacked)' : 'UNKNOWN (' + data.flexDirection + ')'));
        }

        // Find the container div (uses maxWidth + margin: auto)
        const containerData = await page.evaluate(() => {
          const allDivs = document.querySelectorAll('div');
          for (const d of allDivs) {
            const cs = getComputedStyle(d);
            if (cs.maxWidth && cs.maxWidth !== 'none' && cs.marginLeft === 'auto' && cs.marginRight === 'auto' && d.querySelector('div[data-index]')) {
              const r = d.getBoundingClientRect();
              return { right: r.right, width: r.width, left: r.left, maxWidth: cs.maxWidth };
            }
          }
          return null;
        });
        if (containerData) {
          console.log('Content container: right=' + containerData.right.toFixed(1) + 'px width=' + containerData.width.toFixed(1) + 'px left=' + containerData.left.toFixed(1) + 'px maxWidth=' + containerData.maxWidth);
        } else {
          console.log('Content container: NOT FOUND');
        }

        // Metadata elements in first row
        const firstRow = songRows[0];
        const metaData = await firstRow.evaluate((el) => {
          const children = el.querySelectorAll('*');
          const results = [];
          const seen = new Set();
          for (const child of children) {
            const text = child.textContent?.trim() || '';
            const r = child.getBoundingClientRect();
            if (r.width > 5 && r.width < 200 && text.length > 0 && text.length < 50) {
              const key = child.tagName + '|' + text.slice(0, 30);
              if (!seen.has(key)) {
                seen.add(key);
                results.push({
                  tag: child.tagName,
                  text: text.slice(0, 40),
                  right: r.right,
                  width: r.width,
                  left: r.left
                });
              }
            }
          }
          return results;
        });
        console.log('Metadata elements in first row: ' + metaData.length);
        const containerRight = containerData ? containerData.right : width;
        metaData.forEach((m, idx) => {
          const overflow = m.right > containerRight + 1 ? ' *** OVERFLOW ***' : '';
          console.log('  meta[' + idx + ']: <' + m.tag + '> "' + m.text + '" left=' + m.left.toFixed(1) + ' right=' + m.right.toFixed(1) + 'px w=' + m.width.toFixed(1) + 'px' + overflow);
        });

        const overflows = metaData.filter(m => m.right > containerRight + 1);
        console.log('Overflow detected: ' + (overflows.length > 0 ? 'YES (' + overflows.length + ' elements)' : 'no'));
      } else {
        console.log('Song rows found: NO');
        // Dump first 10 <a> tags for debugging
        const allAnchors = await page.evaluate(() => {
          return Array.from(document.querySelectorAll('a')).slice(0, 10).map(a => ({
            href: a.getAttribute('href') || '',
            text: (a.textContent || '').trim().slice(0, 50)
          }));
        });
        console.log('All anchors: ' + JSON.stringify(allAnchors));
      }

      // Sidebar detection
      const sidebarData = await page.evaluate(() => {
        const aside = document.querySelector('aside');
        if (aside) {
          const r = aside.getBoundingClientRect();
          return { width: r.width, left: r.left, right: r.right, tag: 'aside' };
        }
        const nav = document.querySelector('nav');
        if (nav) {
          const r = nav.getBoundingClientRect();
          return { width: r.width, left: r.left, right: r.right, tag: 'nav' };
        }
        return null;
      });
      if (sidebarData && sidebarData.width > 30) {
        console.log('Sidebar: <' + sidebarData.tag + '> width=' + sidebarData.width + 'px left=' + sidebarData.left + ' right=' + sidebarData.right);
        console.log('Content area width after sidebar: ~' + (width - sidebarData.width) + 'px');
      } else {
        console.log('Sidebar: not found or not visible');
      }

      // Screenshot
      const ssPath = 'C:/Users/sfent/source/repos/FortniteFestivalLeaderboardScraper/FortniteFestivalWeb/test-results/measurement-' + width + 'px.png';
      await page.screenshot({ path: ssPath, fullPage: false });
      console.log('Screenshot saved: measurement-' + width + 'px.png');
      console.log('');

    } catch (err) {
      console.log('=== Viewport: ' + width + 'px ===');
      console.log('ERROR: ' + err.message);
      console.log('');
    }

    await context.close();
  }

  await browser.close();
})();