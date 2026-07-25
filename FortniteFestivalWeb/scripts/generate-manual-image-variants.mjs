#!/usr/bin/env node
/* global console, process */
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  statSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const webRoot = path.resolve(scriptDir, '..');
const screenshotsDir = path.join(webRoot, 'public/manual/screenshots');
const optimizedDir = path.join(screenshotsDir, 'optimized');
const manifestPath = path.join(optimizedDir, 'manifest.json');
const generatedModulePath = path.join(webRoot, 'src/pages/manual/manualScreenshotAssets.generated.ts');
const checkOnly = process.argv.includes('--check');

const WEBP_QUALITY = 90;
const WEBP_COMPRESSION_LEVEL = 6;
const WEBP_PRESET = 'picture';
const EXPECTED_CANONICAL_ASSET_COUNT = 141;
const SCREENSHOT_ALIASES = {
  'song-detail-cards': 'song-detail-overview',
};
const VIEWPORTS = {
  mobile: {
    width: 390,
    height: 844,
    variants: [240, 390],
    sizes: '(max-width: 600px) 100px, 250px',
  },
  compact: {
    width: 1024,
    height: 768,
    variants: [480, 768, 1024],
    sizes: '(max-width: 600px) calc(100vw - 32px), 700px',
  },
  wide: {
    width: 1440,
    height: 900,
    variants: [480, 800, 1440],
    sizes: '(max-width: 600px) calc(100vw - 32px), 780px',
  },
};

deduplicateAliasPngs();
const sourceAssets = readSourceAssets();

if (checkOnly) {
  checkGeneratedAssets(sourceAssets);
} else {
  generateAssets(sourceAssets);
}

function deduplicateAliasPngs() {
  for (const [aliasSlug, canonicalSlug] of Object.entries(SCREENSHOT_ALIASES)) {
    for (const viewport of Object.keys(VIEWPORTS)) {
      const aliasPath = path.join(screenshotsDir, `${aliasSlug}-${viewport}.png`);
      const canonicalPath = path.join(screenshotsDir, `${canonicalSlug}-${viewport}.png`);
      if (!existsSync(canonicalPath)) {
        throw new Error(`Missing canonical Manual screenshot: ${canonicalPath}`);
      }
      if (!existsSync(aliasPath)) continue;
      if (sha256(aliasPath) !== sha256(canonicalPath)) {
        throw new Error(`${path.basename(aliasPath)} is no longer byte-identical to ${path.basename(canonicalPath)}; remove the alias before regenerating.`);
      }
      if (checkOnly) {
        throw new Error(`Duplicate Manual screenshot still exists: ${aliasPath}`);
      }
      unlinkSync(aliasPath);
    }
  }
}

function readSourceAssets() {
  const records = readdirSync(screenshotsDir)
    .filter(fileName => fileName.endsWith('.png'))
    .sort()
    .map((fileName) => {
      const match = /^(.*)-(mobile|compact|wide)\.png$/.exec(fileName);
      if (!match?.[1] || !match[2]) {
        throw new Error(`Unexpected Manual screenshot filename: ${fileName}`);
      }
      const viewport = VIEWPORTS[match[2]];
      const sourcePath = path.join(screenshotsDir, fileName);
      const dimensions = readPngDimensions(readFileSync(sourcePath));
      if (dimensions.width !== viewport.width || dimensions.height !== viewport.height) {
        throw new Error(`${fileName} is ${dimensions.width}x${dimensions.height}; expected ${viewport.width}x${viewport.height}.`);
      }
      return {
        key: `${match[1]}-${match[2]}`,
        slug: match[1],
        viewport: match[2],
        sourcePath,
        fallback: fileName,
        sourceSha256: sha256(sourcePath),
        width: dimensions.width,
        height: dimensions.height,
        sizes: viewport.sizes,
        variantWidths: viewport.variants,
      };
    });

  if (records.length !== EXPECTED_CANONICAL_ASSET_COUNT) {
    throw new Error(`Expected ${EXPECTED_CANONICAL_ASSET_COUNT} canonical Manual screenshots, found ${records.length}.`);
  }

  const duplicateHashes = groupDuplicates(records, record => record.sourceSha256);
  if (duplicateHashes.length > 0) {
    throw new Error(`Unmapped byte-identical Manual screenshots remain: ${JSON.stringify(duplicateHashes)}`);
  }

  const slugs = new Map();
  for (const record of records) {
    const viewports = slugs.get(record.slug) ?? new Set();
    viewports.add(record.viewport);
    slugs.set(record.slug, viewports);
  }
  for (const [slug, viewports] of slugs) {
    if (viewports.size !== Object.keys(VIEWPORTS).length) {
      throw new Error(`${slug} does not have all Manual viewport captures.`);
    }
  }

  return records;
}

function generateAssets(sourceAssets) {
  const ffmpeg = spawnSync('ffmpeg', ['-version'], { encoding: 'utf8' });
  if (ffmpeg.status !== 0) {
    throw new Error(`ffmpeg is required to generate Manual WebP variants: ${ffmpeg.stderr || ffmpeg.error?.message || 'not found'}`);
  }

  rmSync(optimizedDir, { recursive: true, force: true });
  mkdirSync(optimizedDir, { recursive: true });

  const manifestAssets = {};
  const sourceHashes = {};

  for (const source of sourceAssets) {
    const webp = source.variantWidths.map((variantWidth) => {
      const fileName = `${source.key}-${source.sourceSha256.slice(0, 12)}-${variantWidth}.webp`;
      const outputPath = path.join(optimizedDir, fileName);
      encodeWebp(source.sourcePath, outputPath, variantWidth, source.width);
      return {
        path: `optimized/${fileName}`,
        width: variantWidth,
        bytes: statSync(outputPath).size,
        sha256: sha256(outputPath),
      };
    });

    manifestAssets[source.key] = {
      slug: source.slug,
      viewport: source.viewport,
      fallback: source.fallback,
      sourceSha256: source.sourceSha256,
      width: source.width,
      height: source.height,
      sizes: source.sizes,
      webp,
    };
    sourceHashes[source.key] = source.sourceSha256;
  }

  const manifest = {
    schemaVersion: 1,
    encoder: {
      tool: 'ffmpeg/libwebp',
      quality: WEBP_QUALITY,
      compressionLevel: WEBP_COMPRESSION_LEVEL,
      preset: WEBP_PRESET,
    },
    aliases: SCREENSHOT_ALIASES,
    assets: manifestAssets,
  };

  writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
  writeFileSync(generatedModulePath, renderGeneratedModule(sourceHashes));
  printSummary(manifestAssets, 'generated');
}

function checkGeneratedAssets(sourceAssets) {
  if (!existsSync(manifestPath)) {
    throw new Error(`Manual asset manifest is missing: ${manifestPath}`);
  }
  if (!existsSync(generatedModulePath)) {
    throw new Error(`Generated Manual asset module is missing: ${generatedModulePath}`);
  }

  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  assertEqual(manifest.schemaVersion, 1, 'manifest schema version');
  assertEqual(manifest.encoder?.tool, 'ffmpeg/libwebp', 'manifest encoder');
  assertEqual(manifest.encoder?.quality, WEBP_QUALITY, 'WebP quality');
  assertEqual(manifest.encoder?.compressionLevel, WEBP_COMPRESSION_LEVEL, 'WebP compression level');
  assertEqual(manifest.encoder?.preset, WEBP_PRESET, 'WebP preset');
  assertEqual(JSON.stringify(manifest.aliases), JSON.stringify(SCREENSHOT_ALIASES), 'Manual screenshot aliases');

  const expectedOutputFiles = new Set(['manifest.json']);
  const sourceHashes = {};
  for (const source of sourceAssets) {
    const asset = manifest.assets?.[source.key];
    if (!asset) throw new Error(`Missing Manual manifest asset: ${source.key}`);
    assertEqual(asset.fallback, source.fallback, `${source.key} fallback`);
    assertEqual(asset.sourceSha256, source.sourceSha256, `${source.key} source hash`);
    assertEqual(asset.width, source.width, `${source.key} width`);
    assertEqual(asset.height, source.height, `${source.key} height`);
    assertEqual(asset.sizes, source.sizes, `${source.key} sizes`);
    assertEqual(asset.webp?.length, source.variantWidths.length, `${source.key} WebP variant count`);

    source.variantWidths.forEach((variantWidth, index) => {
      const variant = asset.webp[index];
      const expectedName = `${source.key}-${source.sourceSha256.slice(0, 12)}-${variantWidth}.webp`;
      assertEqual(variant?.path, `optimized/${expectedName}`, `${source.key} ${variantWidth}w path`);
      assertEqual(variant?.width, variantWidth, `${source.key} ${variantWidth}w width`);
      const outputPath = path.join(optimizedDir, expectedName);
      if (!existsSync(outputPath)) throw new Error(`Missing Manual WebP variant: ${outputPath}`);
      assertEqual(variant.bytes, statSync(outputPath).size, `${source.key} ${variantWidth}w bytes`);
      assertEqual(variant.sha256, sha256(outputPath), `${source.key} ${variantWidth}w hash`);
      expectedOutputFiles.add(expectedName);
    });
    sourceHashes[source.key] = source.sourceSha256;
  }

  assertEqual(Object.keys(manifest.assets ?? {}).length, sourceAssets.length, 'manifest asset count');
  const actualOutputFiles = new Set(readdirSync(optimizedDir));
  assertEqual(JSON.stringify([...actualOutputFiles].sort()), JSON.stringify([...expectedOutputFiles].sort()), 'optimized Manual asset file list');

  const expectedModule = renderGeneratedModule(sourceHashes);
  assertEqual(readFileSync(generatedModulePath, 'utf8'), expectedModule, 'generated Manual asset module');
  printSummary(manifest.assets, 'checked');
}

function encodeWebp(inputPath, outputPath, variantWidth, sourceWidth) {
  const args = ['-hide_banner', '-loglevel', 'error', '-y', '-i', inputPath];
  if (variantWidth !== sourceWidth) {
    args.push('-vf', `scale=${variantWidth}:-2:flags=lanczos`);
  }
  args.push(
    '-frames:v', '1',
    '-c:v', 'libwebp',
    '-quality', String(WEBP_QUALITY),
    '-compression_level', String(WEBP_COMPRESSION_LEVEL),
    '-preset', WEBP_PRESET,
    outputPath,
  );
  const result = spawnSync('ffmpeg', args, { encoding: 'utf8' });
  if (result.status !== 0) {
    throw new Error(`Failed to encode ${path.basename(inputPath)} at ${variantWidth}w: ${result.stderr || result.error?.message || 'ffmpeg failed'}`);
  }
}

function renderGeneratedModule(sourceHashes) {
  return `/* This file is generated by scripts/generate-manual-image-variants.mjs. */\n`
    + `/* eslint-disable no-magic-numbers */\n`
    + `export const MANUAL_SCREENSHOT_ALIASES = ${JSON.stringify(SCREENSHOT_ALIASES, null, 2)} as const;\n\n`
    + `export const MANUAL_SCREENSHOT_VIEWPORTS = ${JSON.stringify(VIEWPORTS, null, 2)} as const;\n\n`
    + `export const MANUAL_SCREENSHOT_SOURCE_HASHES = ${JSON.stringify(sourceHashes, null, 2)} as const;\n`;
}

function printSummary(assets, verb) {
  const records = Object.values(assets);
  const webp = records.flatMap(asset => asset.webp);
  console.log(JSON.stringify({
    result: verb,
    canonicalPngs: records.length,
    aliases: SCREENSHOT_ALIASES,
    webpVariants: webp.length,
    webpBytes: webp.reduce((sum, variant) => sum + variant.bytes, 0),
    manifestPath,
    generatedModulePath,
  }, null, 2));
}

function readPngDimensions(buffer) {
  if (buffer.toString('ascii', 1, 4) !== 'PNG') {
    throw new Error('Manual screenshot is not a PNG.');
  }
  return {
    width: buffer.readUInt32BE(16),
    height: buffer.readUInt32BE(20),
  };
}

function sha256(filePath) {
  return createHash('sha256').update(readFileSync(filePath)).digest('hex');
}

function groupDuplicates(records, getHash) {
  const groups = new Map();
  for (const record of records) {
    const hash = getHash(record);
    groups.set(hash, [...(groups.get(hash) ?? []), record.fallback]);
  }
  return [...groups.values()].filter(group => group.length > 1);
}

function assertEqual(actual, expected, label) {
  if (actual !== expected) {
    throw new Error(`${label} is stale: expected ${JSON.stringify(expected)}, received ${JSON.stringify(actual)}.`);
  }
}
