#!/usr/bin/env node
import { brotliCompressSync, gzipSync } from 'node:zlib';
import { mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const args = parseArgs(process.argv.slice(2));
const distDir = path.resolve(webRoot, args.dist ?? '../FSTService/wwwroot');
const outputPath = path.resolve(webRoot, args.out ?? 'performance-artifacts/bundle.json');
const budgets = JSON.parse(readFileSync(path.join(webRoot, 'performance-budgets.json'), 'utf8')).bundle;
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
check('entryJsRawMaxBytes', entry.rawBytes);
check('entryJsGzipMaxBytes', entry.gzipBytes);
check('entryJsBrotliMaxBytes', entry.brotliBytes);
check('largestChunkGzipMaxBytes', largestChunkGzipBytes);

const report = {
  capturedAtUtc: new Date().toISOString(),
  distDir,
  entry,
  largestChunkGzipBytes,
  budgets,
  failures,
  files,
};
mkdirSync(path.dirname(outputPath), { recursive: true });
writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);

console.log(JSON.stringify({
  entry: report.entry,
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
