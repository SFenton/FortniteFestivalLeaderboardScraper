#!/usr/bin/env node
/* global console, process */
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  copyFileSync,
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
const sourceScreenshotsDir = path.join(webRoot, 'manual-assets/source/screenshots');
const deployScreenshotsDir = path.join(webRoot, 'public/manual/screenshots');
const optimizedDir = path.join(deployScreenshotsDir, 'optimized');
const manifestPath = path.join(webRoot, 'manual-assets/generated/manifest.json');
const generatedModulePath = path.join(webRoot, 'src/pages/manual/manualScreenshotAssets.generated.ts');
const checkOnly = process.argv.includes('--check');

const MANIFEST_SCHEMA_VERSION = 2;
const WEBP_QUALITY = 90;
const WEBP_COMPRESSION_LEVEL = 6;
const WEBP_PRESET = 'picture';
const EXPECTED_CANONICAL_ASSET_COUNT = 141;
const MANUAL_PUBLIC_MAX_BYTES = 17_500_000;
const SCREENSHOT_ALIASES = {
  'song-detail-cards': 'song-detail-overview',
};
const LEGACY_PNG_SLUGS = new Set(['song-detail-overview']);
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
      const aliasPath = path.join(sourceScreenshotsDir, `${aliasSlug}-${viewport}.png`);
      const canonicalPath = path.join(sourceScreenshotsDir, `${canonicalSlug}-${viewport}.png`);
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
  const records = readdirSync(sourceScreenshotsDir)
    .filter(fileName => fileName.endsWith('.png'))
    .sort()
    .map((fileName) => {
      const match = /^(.*)-(mobile|compact|wide)\.png$/.exec(fileName);
      if (!match?.[1] || !match[2]) {
        throw new Error(`Unexpected Manual screenshot filename: ${fileName}`);
      }
      const viewport = VIEWPORTS[match[2]];
      const sourcePath = path.join(sourceScreenshotsDir, fileName);
      const metadata = readPngMetadata(readFileSync(sourcePath));
      if (metadata.width !== viewport.width || metadata.height !== viewport.height) {
        throw new Error(`${fileName} is ${metadata.width}x${metadata.height}; expected ${viewport.width}x${viewport.height}.`);
      }
      return {
        key: `${match[1]}-${match[2]}`,
        slug: match[1],
        viewport: match[2],
        sourcePath,
        sourceFileName: fileName,
        sourceBytes: statSync(sourcePath).size,
        sourceSha256: sha256(sourcePath),
        width: metadata.width,
        height: metadata.height,
        bitDepth: metadata.bitDepth,
        colorType: metadata.colorType,
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

  rmSync(deployScreenshotsDir, { recursive: true, force: true });
  mkdirSync(optimizedDir, { recursive: true });
  mkdirSync(path.dirname(manifestPath), { recursive: true });

  const manifestAssets = {};
  const legacyPngs = {};
  const sourceHashes = {};

  for (const source of sourceAssets) {
    const webp = source.variantWidths.map((variantWidth) => {
      const fileName = `${source.key}-${source.sourceSha256.slice(0, 12)}-${variantWidth}.webp`;
      const outputPath = path.join(optimizedDir, fileName);
      encodeWebp(source.sourcePath, outputPath, variantWidth, source.width);
      const dimensions = readWebpDimensions(readFileSync(outputPath));
      return {
        path: `optimized/${fileName}`,
        width: variantWidth,
        height: dimensions.height,
        bytes: statSync(outputPath).size,
        sha256: sha256(outputPath),
      };
    });
    const fallback = webp.find(variant => variant.width === source.width);
    if (!fallback) {
      throw new Error(`Missing full-resolution Manual fallback: ${source.key}`);
    }

    manifestAssets[source.key] = {
      slug: source.slug,
      viewport: source.viewport,
      source: {
        path: `source/screenshots/${source.sourceFileName}`,
        format: 'png',
        bitDepth: source.bitDepth,
        colorType: source.colorType,
        width: source.width,
        height: source.height,
        bytes: source.sourceBytes,
        sha256: source.sourceSha256,
      },
      fallback,
      sizes: source.sizes,
      webp,
    };
    sourceHashes[source.key] = source.sourceSha256;

    if (LEGACY_PNG_SLUGS.has(source.slug)) {
      const outputPath = path.join(deployScreenshotsDir, source.sourceFileName);
      copyFileSync(source.sourcePath, outputPath);
      legacyPngs[source.key] = {
        path: source.sourceFileName,
        bytes: statSync(outputPath).size,
        sha256: sha256(outputPath),
      };
    }
  }

  const deployBytes = totalBytes(deployScreenshotsDir);
  if (deployBytes > MANUAL_PUBLIC_MAX_BYTES) {
    throw new Error(`Manual deploy assets use ${deployBytes} bytes; maximum is ${MANUAL_PUBLIC_MAX_BYTES}.`);
  }
  const manifest = {
    schemaVersion: MANIFEST_SCHEMA_VERSION,
    encoder: {
      tool: 'ffmpeg/libwebp',
      version: ffmpeg.stdout.split(/\r?\n/, 1)[0],
      quality: WEBP_QUALITY,
      compressionLevel: WEBP_COMPRESSION_LEVEL,
      preset: WEBP_PRESET,
    },
    limits: {
      publicDeployMaxBytes: MANUAL_PUBLIC_MAX_BYTES,
    },
    aliases: SCREENSHOT_ALIASES,
    legacyPngs,
    summary: {
      canonicalSources: sourceAssets.length,
      sourceBytes: sourceAssets.reduce((sum, source) => sum + source.sourceBytes, 0),
      webpVariants: Object.values(manifestAssets).reduce((sum, asset) => sum + asset.webp.length, 0),
      deployBytes,
    },
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
  assertEqual(manifest.schemaVersion, MANIFEST_SCHEMA_VERSION, 'manifest schema version');
  assertEqual(manifest.encoder?.tool, 'ffmpeg/libwebp', 'manifest encoder');
  if (!manifest.encoder?.version) throw new Error('Manual manifest encoder version is missing.');
  assertEqual(manifest.encoder?.quality, WEBP_QUALITY, 'WebP quality');
  assertEqual(manifest.encoder?.compressionLevel, WEBP_COMPRESSION_LEVEL, 'WebP compression level');
  assertEqual(manifest.encoder?.preset, WEBP_PRESET, 'WebP preset');
  assertEqual(manifest.limits?.publicDeployMaxBytes, MANUAL_PUBLIC_MAX_BYTES, 'Manual deploy byte limit');
  assertEqual(JSON.stringify(manifest.aliases), JSON.stringify(SCREENSHOT_ALIASES), 'Manual screenshot aliases');

  const expectedOutputFiles = new Set();
  const expectedLegacyPngs = {};
  const sourceHashes = {};
  for (const source of sourceAssets) {
    const asset = manifest.assets?.[source.key];
    if (!asset) throw new Error(`Missing Manual manifest asset: ${source.key}`);
    assertEqual(asset.source?.path, `source/screenshots/${source.sourceFileName}`, `${source.key} source path`);
    assertEqual(asset.source?.format, 'png', `${source.key} source format`);
    assertEqual(asset.source?.bitDepth, source.bitDepth, `${source.key} source bit depth`);
    assertEqual(asset.source?.colorType, source.colorType, `${source.key} source color type`);
    assertEqual(asset.source?.width, source.width, `${source.key} source width`);
    assertEqual(asset.source?.height, source.height, `${source.key} source height`);
    assertEqual(asset.source?.bytes, source.sourceBytes, `${source.key} source bytes`);
    assertEqual(asset.source?.sha256, source.sourceSha256, `${source.key} source hash`);
    assertEqual(asset.sizes, source.sizes, `${source.key} sizes`);
    assertEqual(asset.webp?.length, source.variantWidths.length, `${source.key} WebP variant count`);

    source.variantWidths.forEach((variantWidth, index) => {
      const variant = asset.webp[index];
      const expectedName = `${source.key}-${source.sourceSha256.slice(0, 12)}-${variantWidth}.webp`;
      assertEqual(variant?.path, `optimized/${expectedName}`, `${source.key} ${variantWidth}w path`);
      assertEqual(variant?.width, variantWidth, `${source.key} ${variantWidth}w width`);
      const outputPath = path.join(optimizedDir, expectedName);
      if (!existsSync(outputPath)) throw new Error(`Missing Manual WebP variant: ${outputPath}`);
      const dimensions = readWebpDimensions(readFileSync(outputPath));
      assertEqual(variant?.height, dimensions.height, `${source.key} ${variantWidth}w height`);
      assertEqual(dimensions.width, variantWidth, `${source.key} ${variantWidth}w encoded width`);
      assertEqual(dimensions.height, expectedVariantHeight(source, variantWidth), `${source.key} ${variantWidth}w encoded height`);
      assertEqual(variant.bytes, statSync(outputPath).size, `${source.key} ${variantWidth}w bytes`);
      assertEqual(variant.sha256, sha256(outputPath), `${source.key} ${variantWidth}w hash`);
      expectedOutputFiles.add(`optimized/${expectedName}`);
    });
    const fallback = asset.webp.find(variant => variant.width === source.width);
    assertEqual(JSON.stringify(asset.fallback), JSON.stringify(fallback), `${source.key} full-resolution fallback`);

    if (LEGACY_PNG_SLUGS.has(source.slug)) {
      const deployPath = path.join(deployScreenshotsDir, source.sourceFileName);
      if (!existsSync(deployPath)) throw new Error(`Missing legacy Manual PNG: ${deployPath}`);
      const legacyPng = {
        path: source.sourceFileName,
        bytes: statSync(deployPath).size,
        sha256: sha256(deployPath),
      };
      assertEqual(legacyPng.sha256, source.sourceSha256, `${source.key} legacy PNG hash`);
      expectedLegacyPngs[source.key] = legacyPng;
      expectedOutputFiles.add(source.sourceFileName);
    }
    sourceHashes[source.key] = source.sourceSha256;
  }

  assertEqual(Object.keys(manifest.assets ?? {}).length, sourceAssets.length, 'manifest asset count');
  assertEqual(JSON.stringify(manifest.legacyPngs), JSON.stringify(expectedLegacyPngs), 'legacy Manual PNGs');
  const actualOutputFiles = new Set(
    listFiles(deployScreenshotsDir)
      .map(fileName => path.relative(deployScreenshotsDir, fileName).split(path.sep).join('/')),
  );
  assertEqual(JSON.stringify([...actualOutputFiles].sort()), JSON.stringify([...expectedOutputFiles].sort()), 'optimized Manual asset file list');
  const deployBytes = totalBytes(deployScreenshotsDir);
  if (deployBytes > MANUAL_PUBLIC_MAX_BYTES) {
    throw new Error(`Manual deploy assets use ${deployBytes} bytes; maximum is ${MANUAL_PUBLIC_MAX_BYTES}.`);
  }
  assertEqual(manifest.summary?.canonicalSources, sourceAssets.length, 'Manual source count');
  assertEqual(manifest.summary?.sourceBytes, sourceAssets.reduce((sum, source) => sum + source.sourceBytes, 0), 'Manual source bytes');
  assertEqual(manifest.summary?.webpVariants, sourceAssets.reduce((sum, source) => sum + source.variantWidths.length, 0), 'Manual WebP count');
  assertEqual(manifest.summary?.deployBytes, deployBytes, 'Manual deploy bytes');

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
  const deployBytes = totalBytes(deployScreenshotsDir);
  console.log(JSON.stringify({
    result: verb,
    canonicalSources: records.length,
    aliases: SCREENSHOT_ALIASES,
    legacyPngs: Object.keys(assets).filter(key => LEGACY_PNG_SLUGS.has(assets[key].slug)).length,
    webpVariants: webp.length,
    webpBytes: webp.reduce((sum, variant) => sum + variant.bytes, 0),
    deployBytes,
    deployMaxBytes: MANUAL_PUBLIC_MAX_BYTES,
    manifestPath,
    generatedModulePath,
  }, null, 2));
}

function readPngMetadata(buffer) {
  if (buffer.toString('ascii', 1, 4) !== 'PNG') {
    throw new Error('Manual screenshot is not a PNG.');
  }
  return {
    width: buffer.readUInt32BE(16),
    height: buffer.readUInt32BE(20),
    bitDepth: buffer[24],
    colorType: buffer[25],
  };
}

function readWebpDimensions(buffer) {
  if (buffer.toString('ascii', 0, 4) !== 'RIFF' || buffer.toString('ascii', 8, 12) !== 'WEBP') {
    throw new Error('Manual variant is not a WebP image.');
  }
  const chunk = buffer.toString('ascii', 12, 16);
  if (chunk === 'VP8 ') {
    if (buffer.toString('hex', 23, 26) !== '9d012a') {
      throw new Error('Manual VP8 frame header is invalid.');
    }
    return {
      width: buffer.readUInt16LE(26) & 0x3fff,
      height: buffer.readUInt16LE(28) & 0x3fff,
    };
  }
  if (chunk === 'VP8L') {
    if (buffer[20] !== 0x2f) throw new Error('Manual VP8L frame header is invalid.');
    const bits = buffer.readUInt32LE(21);
    return {
      width: (bits & 0x3fff) + 1,
      height: ((bits >>> 14) & 0x3fff) + 1,
    };
  }
  if (chunk === 'VP8X') {
    return {
      width: readUInt24LE(buffer, 24) + 1,
      height: readUInt24LE(buffer, 27) + 1,
    };
  }
  throw new Error(`Unsupported Manual WebP chunk: ${JSON.stringify(chunk)}.`);
}

function readUInt24LE(buffer, offset) {
  return buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
}

function expectedVariantHeight(source, variantWidth) {
  if (variantWidth === source.width) return source.height;
  return Math.round((source.height * variantWidth / source.width) / 2) * 2;
}

function sha256(filePath) {
  return createHash('sha256').update(readFileSync(filePath)).digest('hex');
}

function groupDuplicates(records, getHash) {
  const groups = new Map();
  for (const record of records) {
    const hash = getHash(record);
    groups.set(hash, [...(groups.get(hash) ?? []), record.sourceFileName]);
  }
  return [...groups.values()].filter(group => group.length > 1);
}

function listFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const fileName = path.join(directory, entry.name);
    return entry.isDirectory() ? listFiles(fileName) : [fileName];
  });
}

function totalBytes(directory) {
  return listFiles(directory).reduce((sum, fileName) => sum + statSync(fileName).size, 0);
}

function assertEqual(actual, expected, label) {
  if (actual !== expected) {
    throw new Error(`${label} is stale: expected ${JSON.stringify(expected)}, received ${JSON.stringify(actual)}.`);
  }
}
