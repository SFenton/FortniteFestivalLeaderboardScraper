import { createHash } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  getManualScreenshotAsset,
  MANUAL_SCREENSHOT_ALIASES,
  MANUAL_SCREENSHOT_SOURCE_HASHES,
  MANUAL_SCREENSHOT_VIEWPORTS,
} from '../../../src/pages/manual/manualScreenshotAssets';

const screenshotsDir = resolve(process.cwd(), 'public/manual/screenshots');

describe('Manual screenshot asset manifest', () => {
  it('aliases byte-identical screenshots without retaining duplicate PNG files', () => {
    expect(MANUAL_SCREENSHOT_ALIASES).toEqual({
      'song-detail-cards': 'song-detail-overview',
    });

    for (const viewport of Object.keys(MANUAL_SCREENSHOT_VIEWPORTS)) {
      const cards = getManualScreenshotAsset('song-detail-cards', viewport as keyof typeof MANUAL_SCREENSHOT_VIEWPORTS);
      const overview = getManualScreenshotAsset('song-detail-overview', viewport as keyof typeof MANUAL_SCREENSHOT_VIEWPORTS);

      expect(cards).toEqual(overview);
      expect(cards.fallbackSrc).toContain(`/song-detail-overview-${viewport}.png`);
      expect(existsSync(resolve(screenshotsDir, `song-detail-cards-${viewport}.png`))).toBe(false);
    }
  });

  it('tracks every canonical fallback and responsive WebP variant without byte-identical PNG pairs', () => {
    const entries = Object.entries(MANUAL_SCREENSHOT_SOURCE_HASHES);
    expect(entries).toHaveLength(141);
    expect(new Set(entries.map(([, hash]) => hash)).size).toBe(entries.length);

    for (const [key, sourceHash] of entries) {
      const viewport = key.endsWith('-mobile')
        ? 'mobile'
        : key.endsWith('-compact')
          ? 'compact'
          : 'wide';
      const config = MANUAL_SCREENSHOT_VIEWPORTS[viewport];
      const fallback = resolve(screenshotsDir, `${key}.png`);
      expect(existsSync(fallback), `${key} fallback`).toBe(true);
      expect(sha256(fallback), `${key} source hash`).toBe(sourceHash);

      for (const width of config.variants) {
        const webp = resolve(screenshotsDir, `optimized/${key}-${sourceHash.slice(0, 12)}-${width}.webp`);
        expect(existsSync(webp), `${key} ${width}w WebP`).toBe(true);
      }
    }
  });

  it('deduplicates the maskable icon through the web manifest while retaining its legacy HTTP alias', () => {
    const manifest = JSON.parse(readFileSync(resolve(process.cwd(), 'public/manifest.json'), 'utf8'));
    const icon512 = manifest.icons.filter((icon: { sizes: string }) => icon.sizes === '512x512');
    const nginx = readFileSync(resolve(process.cwd(), 'nginx.conf'), 'utf8');

    expect(icon512).toHaveLength(2);
    expect(new Set(icon512.map((icon: { src: string }) => icon.src))).toEqual(new Set(['/icons/fst-icon-512.png']));
    expect(existsSync(resolve(process.cwd(), 'public/icons/fst-icon-maskable-512.png'))).toBe(false);
    expect(nginx).toContain('location = /icons/fst-icon-maskable-512.png');
    expect(nginx).toContain('song-detail-cards-(mobile|compact|wide)');
  });

  it('falls back to the SPA before matching static directories', () => {
    const nginx = readFileSync(resolve(process.cwd(), 'nginx.conf'), 'utf8');

    expect(nginx).toContain('try_files $uri /index.html;');
    expect(nginx).not.toContain('try_files $uri $uri/ /index.html;');
  });
});

function sha256(filePath: string): string {
  return createHash('sha256').update(readFileSync(filePath)).digest('hex');
}
