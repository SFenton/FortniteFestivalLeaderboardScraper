import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { mkdirSync, rmSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { performance } from 'node:perf_hooks';

const require = createRequire(import.meta.url);
const vitestPackage = require.resolve('vitest/package.json');
const vitestBin = resolve(dirname(vitestPackage), require(vitestPackage).bin.vitest);
const shardCount = readPositiveInteger('VITEST_COVERAGE_SHARDS', 16);
const maxWorkers = readPositiveInteger('VITEST_COVERAGE_MAX_WORKERS', 1);
const maxOldSpaceMb = readPositiveInteger('VITEST_COVERAGE_MAX_OLD_SPACE_MB', 4096);
const processingConcurrency = readPositiveInteger('VITEST_COVERAGE_PROCESSING_CONCURRENCY', 2);
const reportsDirectory = resolve(process.cwd(), '.vitest-coverage-reports');
const blobsDirectory = resolve(reportsDirectory, 'blobs');
const finalCoverageDirectory = resolve(process.cwd(), 'coverage');
const startedAt = performance.now();

const ignoreCheck = spawnSync(process.execPath, [resolve(process.cwd(), 'scripts/check-coverage-ignores.mjs')], {
  cwd: process.cwd(),
  stdio: 'inherit',
});
if (ignoreCheck.error || ignoreCheck.status !== 0) {
  console.error(`[coverage] Coverage-ignore validation failed${ignoreCheck.error ? `: ${ignoreCheck.error.message}` : '.'}`);
  process.exit(ignoreCheck.status ?? 1);
}

rmSync(reportsDirectory, { recursive: true, force: true });
rmSync(finalCoverageDirectory, { recursive: true, force: true });
mkdirSync(blobsDirectory, { recursive: true });

console.log(
  `[coverage] Running ${shardCount} sequential shards with ${maxWorkers} worker, `
  + `${maxOldSpaceMb} MB heap cap, and ${processingConcurrency} coverage processors.`,
);

let exitCode = 0;
try {
  for (let shard = 1; shard <= shardCount; shard += 1) {
    const shardStartedAt = performance.now();
    console.log(`\n[coverage] Running shard ${shard}/${shardCount}`);
    const result = runVitest([
      'run',
      '--coverage',
      `--shard=${shard}/${shardCount}`,
      `--maxWorkers=${maxWorkers}`,
      `--coverage.processingConcurrency=${processingConcurrency}`,
      '--coverage.thresholds.lines=0',
      '--coverage.thresholds.branches=0',
      '--coverage.thresholds.statements=0',
      '--coverage.thresholds.functions=0',
      '--coverage.reporter=json',
      `--coverage.reportsDirectory=${resolve(reportsDirectory, `coverage-${shard}`)}`,
      '--reporter=dot',
      '--reporter=blob',
      `--outputFile.blob=${resolve(blobsDirectory, `shard-${shard}.json`)}`,
      '--silent=passed-only',
    ]);
    if (!handleResult(result, `Shard ${shard}/${shardCount}`, shardStartedAt)) {
      exitCode = result.status ?? 1;
      break;
    }
    console.log(`[coverage] Shard ${shard}/${shardCount} passed in ${formatDuration(shardStartedAt)}.`);
  }

  if (exitCode === 0) {
    const mergeStartedAt = performance.now();
    console.log(`\n[coverage] Merging ${shardCount} shard reports and enforcing configured thresholds.`);
    const result = runVitest([
      `--merge-reports=${blobsDirectory}`,
      '--coverage',
      `--coverage.processingConcurrency=${processingConcurrency}`,
      '--reporter=dot',
      '--silent=passed-only',
    ]);
    if (!handleResult(result, 'Coverage merge', mergeStartedAt)) {
      exitCode = result.status ?? 1;
    }
  }
} finally {
  rmSync(reportsDirectory, { recursive: true, force: true });
}

if (exitCode !== 0) {
  process.exit(exitCode);
}

console.log(`[coverage] Full suite and configured thresholds passed in ${formatDuration(startedAt)}.`);

function runVitest(args) {
  return spawnSync(process.execPath, [
    vitestBin,
    ...args,
  ], {
    cwd: process.cwd(),
    env: {
      ...process.env,
      CI: process.env.CI ?? '1',
      NODE_OPTIONS: withMaxOldSpace(process.env.NODE_OPTIONS, maxOldSpaceMb),
    },
    stdio: 'inherit',
  });
}

function handleResult(result, label, runStartedAt) {
  if (result.error) {
    console.error(`[coverage] ${label} failed to start: ${result.error.message}`);
    return false;
  }
  if (result.status !== 0) {
    console.error(`[coverage] ${label} failed after ${formatDuration(runStartedAt)}.`);
    return false;
  }
  return true;
}

function readPositiveInteger(name, fallback) {
  const raw = process.env[name];
  if (raw == null || raw === '') return fallback;

  const value = Number(raw);
  if (!Number.isSafeInteger(value) || value <= 0) {
    console.error(`[coverage] ${name} must be a positive integer; received "${raw}".`);
    process.exit(2);
  }
  return value;
}

function withMaxOldSpace(nodeOptions, maxOldSpaceMb) {
  const withoutExistingLimit = (nodeOptions ?? '')
    .split(/\s+/)
    .filter(Boolean)
    .filter(option => !option.startsWith('--max-old-space-size='));
  return [...withoutExistingLimit, `--max-old-space-size=${maxOldSpaceMb}`].join(' ');
}

function formatDuration(startedAt) {
  return `${((performance.now() - startedAt) / 1000).toFixed(1)}s`;
}
