#!/usr/bin/env node
import { brotliCompressSync, gzipSync } from 'node:zlib';
import { mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = path.resolve(webRoot, '..');
const require = createRequire(import.meta.url);
const args = parseArgs(process.argv.slice(2));
const distDir = path.resolve(webRoot, args.dist ?? '../FSTService/wwwroot');
const outputPath = path.resolve(webRoot, args.out ?? 'performance-artifacts/bundle.json');
const budgets = JSON.parse(readFileSync(path.join(webRoot, 'performance-budgets.json'), 'utf8')).bundle;
const requiredNodeVersion = readFileSync(path.join(webRoot, '.node-version'), 'utf8').trim();
const webPackage = readJson(path.join(webRoot, 'package.json'));
const corePackage = readJson(path.join(repositoryRoot, 'packages/core/package.json'));
const themePackage = readJson(path.join(repositoryRoot, 'packages/theme/package.json'));
const vitePackage = readJson(require.resolve('vite/package.json'));
const dockerfile = readFileSync(path.join(webRoot, 'Dockerfile'), 'utf8');
const dockerNodeVersion = /^FROM\s+node:([^\s]+)-slim\s+AS\s+webapp\s*$/im.exec(dockerfile)?.[1] ?? null;
const indexHtml = readFileSync(path.join(distDir, 'index.html'), 'utf8');
const entryMatch = /<script[^>]+src="([^"]+\.js)"/.exec(indexHtml);
if (!entryMatch) throw new Error(`Unable to identify entry script in ${distDir}/index.html`);

const files = listFiles(path.join(distDir, 'assets'))
  .filter((file) => /\.(?:js|css)$/.test(file))
  .map((file) => {
    const content = readFileSync(file);
    return {
      file: path.relative(distDir, file).split(path.sep).join('/'),
      rawBytes: content.length,
      gzipBytes: gzipSync(content, { level: 9 }).length,
      brotliBytes: brotliCompressSync(content).length,
    };
  })
  .sort((left, right) => right.rawBytes - left.rawBytes);

const entryFile = entryMatch[1].replace(/^\//, '');
const entry = files.find((file) => file.file === entryFile);
if (!entry) throw new Error(`Entry script ${entryFile} was not found under ${distDir}`);

const largestChunkGzipBytes = Math.max(
  ...files
    .filter((file) => file.file.endsWith('.js') && file.file !== entryFile)
    .map((file) => file.gzipBytes),
);
const failures = [];
if (process.versions.node !== requiredNodeVersion) {
  failures.push({
    metric: 'nodeVersion',
    actual: process.versions.node,
    expected: requiredNodeVersion,
  });
}
if (dockerNodeVersion !== requiredNodeVersion) {
  failures.push({
    metric: 'dockerNodeVersion',
    actual: dockerNodeVersion,
    expected: requiredNodeVersion,
  });
}
check('entryJsRawMaxBytes', entry.rawBytes);
check('entryJsGzipMaxBytes', entry.gzipBytes);
check('entryJsBrotliMaxBytes', entry.brotliBytes);
check('largestChunkGzipMaxBytes', largestChunkGzipBytes);
if (!Number.isFinite(budgets.entryJsGzipMinHeadroomBytes) || budgets.entryJsGzipMinHeadroomBytes < 0) {
  throw new Error('entryJsGzipMinHeadroomBytes must be a non-negative number.');
}
const entryJsGzipHeadroomBytes = budgets.entryJsGzipMaxBytes - entry.gzipBytes;
if (entryJsGzipHeadroomBytes < budgets.entryJsGzipMinHeadroomBytes) {
  failures.push({
    metric: 'entryJsGzipMinHeadroomBytes',
    actual: entryJsGzipHeadroomBytes,
    minimum: budgets.entryJsGzipMinHeadroomBytes,
  });
}

const report = {
  capturedAtUtc: new Date().toISOString(),
  distDir,
  runtime: {
    requiredNodeVersion,
    nodeVersion: process.versions.node,
    dockerNodeVersion,
    zlibVersion: process.versions.zlib,
    viteVersion: vitePackage.version,
    appVersion: webPackage.version,
    coreVersion: corePackage.version,
    themeVersion: themePackage.version,
  },
  entry,
  entryJsGzipHeadroomBytes,
  largestChunkGzipBytes,
  budgets,
  failures,
  files,
};
mkdirSync(path.dirname(outputPath), { recursive: true });
writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);

console.log(JSON.stringify({
  runtime: report.runtime,
  entry: report.entry,
  entryJsGzipHeadroomBytes,
  largestChunkGzipBytes,
  files: files.length,
  failures,
  outputPath,
}, null, 2));
if (failures.length) process.exitCode = 1;

function check(name, actual) {
  const limit = budgets[name];
  if (typeof limit === 'number' && actual > limit) failures.push({ metric: name, actual, limit });
}

function listFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const fullPath = path.join(directory, entry.name);
    return entry.isDirectory() ? listFiles(fullPath) : statSync(fullPath).isFile() ? [fullPath] : [];
  });
}

function readJson(fileName) {
  return JSON.parse(readFileSync(fileName, 'utf8'));
}

function parseArgs(argv) {
  const values = {};
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith('--')) continue;
    const value = argv[index + 1];
    if (!value || value.startsWith('--')) throw new Error(`Missing value for ${token}`);
    values[token.slice(2)] = value;
    index += 1;
  }
  return values;
}
