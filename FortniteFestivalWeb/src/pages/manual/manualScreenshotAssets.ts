import {
  MANUAL_SCREENSHOT_ALIASES,
  MANUAL_SCREENSHOT_SOURCE_HASHES,
  MANUAL_SCREENSHOT_VIEWPORTS,
} from './manualScreenshotAssets.generated';

export type ManualScreenshotViewport = keyof typeof MANUAL_SCREENSHOT_VIEWPORTS;

export type ManualScreenshotAsset = {
  fallbackSrc: string;
  height: number;
  sizes: string;
  sourceHash: string;
  webpSrcSet: string;
  width: number;
};

const aliases = MANUAL_SCREENSHOT_ALIASES as Readonly<Record<string, string>>;
const sourceHashes = MANUAL_SCREENSHOT_SOURCE_HASHES as Readonly<Record<string, string>>;

export function getManualScreenshotAsset(slug: string, viewport: ManualScreenshotViewport): ManualScreenshotAsset {
  const canonicalSlug = aliases[slug] ?? slug;
  const key = `${canonicalSlug}-${viewport}`;
  const sourceHash = sourceHashes[key];
  if (!sourceHash) {
    throw new Error(`Missing Manual screenshot asset: ${key}`);
  }

  const config = MANUAL_SCREENSHOT_VIEWPORTS[viewport];
  const baseUrl = `${import.meta.env.BASE_URL}manual/screenshots/`;
  const fallbackWidth = config.variants[config.variants.length - 1];
  const webpSrcSet = config.variants
    .map(width => `${baseUrl}optimized/${key}-${sourceHash.slice(0, 12)}-${width}.webp ${width}w`)
    .join(', ');

  return {
    fallbackSrc: `${baseUrl}optimized/${key}-${sourceHash.slice(0, 12)}-${fallbackWidth}.webp?fallback=1`,
    height: config.height,
    sizes: config.sizes,
    sourceHash,
    webpSrcSet,
    width: config.width,
  };
}

export {
  MANUAL_SCREENSHOT_ALIASES,
  MANUAL_SCREENSHOT_SOURCE_HASHES,
  MANUAL_SCREENSHOT_VIEWPORTS,
};
