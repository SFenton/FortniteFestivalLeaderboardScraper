import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { delimiter, dirname, resolve } from 'node:path';
import { performance } from 'node:perf_hooks';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const vitestPackage = require.resolve('vitest/package.json');
const vitestBin = resolve(dirname(vitestPackage), require(vitestPackage).bin.vitest);
const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = resolve(webRoot, '..');
const coverage = process.argv.includes('--coverage');
const startedAt = performance.now();

const result = spawnSync(process.execPath, [
  vitestBin,
  'run',
  '--config=FortniteFestivalWeb/vitest.shared.config.ts',
  '--maxWorkers=1',
  '--reporter=dot',
  '--silent=passed-only',
  ...(coverage ? ['--coverage', '--coverage.processingConcurrency=2'] : []),
], {
  cwd: repoRoot,
  env: {
    ...process.env,
    CI: process.env.CI ?? '1',
    NODE_PATH: [resolve(webRoot, 'node_modules'), process.env.NODE_PATH].filter(Boolean).join(delimiter),
    NODE_OPTIONS: withMaxOldSpace(process.env.NODE_OPTIONS, 2048),
  },
  stdio: 'inherit',
});

if (result.error) {
  console.error(`[shared] Vitest failed to start: ${result.error.message}`);
  process.exit(1);
}
if (result.status !== 0) {
  console.error(`[shared] ${coverage ? 'Coverage' : 'Test'} run failed after ${duration()}.`);
  process.exit(result.status ?? 1);
}

console.log(`[shared] ${coverage ? 'Coverage and thresholds' : 'Tests'} passed in ${duration()}.`);

function withMaxOldSpace(nodeOptions, maxOldSpaceMb) {
  const withoutExistingLimit = (nodeOptions ?? '')
    .split(/\s+/)
    .filter(Boolean)
    .filter(option => !option.startsWith('--max-old-space-size='));
  return [...withoutExistingLimit, `--max-old-space-size=${maxOldSpaceMb}`].join(' ');
}

function duration() {
  return `${((performance.now() - startedAt) / 1000).toFixed(1)}s`;
}
