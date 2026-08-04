import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';
import { performance } from 'node:perf_hooks';

const require = createRequire(import.meta.url);
const vitestPackage = require.resolve('vitest/package.json');
const vitestBin = resolve(dirname(vitestPackage), require(vitestPackage).bin.vitest);
const maxWorkers = readPositiveInteger('VITEST_COVERAGE_MAX_WORKERS', 1);
const maxOldSpaceMb = readPositiveInteger('VITEST_COVERAGE_MAX_OLD_SPACE_MB', 4096);
const processingConcurrency = readPositiveInteger('VITEST_COVERAGE_PROCESSING_CONCURRENCY', 2);
const startedAt = performance.now();

console.log(
  `[coverage] Running full suite with ${maxWorkers} worker, `
  + `${maxOldSpaceMb} MB heap cap, and ${processingConcurrency} coverage processors.`,
);

const result = spawnSync(process.execPath, [
  vitestBin,
  'run',
  '--coverage',
  `--maxWorkers=${maxWorkers}`,
  `--coverage.processingConcurrency=${processingConcurrency}`,
  '--reporter=dot',
  '--silent=passed-only',
], {
  cwd: process.cwd(),
  env: {
    ...process.env,
    CI: process.env.CI ?? '1',
    NODE_OPTIONS: withMaxOldSpace(process.env.NODE_OPTIONS, maxOldSpaceMb),
  },
  stdio: 'inherit',
});

if (result.error) {
  console.error(`[coverage] Vitest failed to start: ${result.error.message}`);
  process.exit(1);
}
if (result.status !== 0) {
  console.error(`[coverage] Full coverage run failed after ${formatDuration(startedAt)}.`);
  process.exit(result.status ?? 1);
}

console.log(`[coverage] Full suite and configured thresholds passed in ${formatDuration(startedAt)}.`);

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
