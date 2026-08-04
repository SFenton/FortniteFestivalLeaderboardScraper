import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync, unlinkSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';
import { performance } from 'node:perf_hooks';

const require = createRequire(import.meta.url);
const vitestPackage = require.resolve('vitest/package.json');
const vitestBin = resolve(dirname(vitestPackage), require(vitestPackage).bin.vitest);
const shardCount = readPositiveInteger('VITEST_SHARDS', 32);
const maxWorkers = readPositiveInteger('VITEST_MAX_WORKERS', 2);
const maxOldSpaceMb = readPositiveInteger('VITEST_MAX_OLD_SPACE_MB', 2048);
const forwardedArgs = process.argv.slice(2);
const startedAt = performance.now();

if (forwardedArgs.length > 0) {
  const targetedInvocation = resolveTargetedInvocation(forwardedArgs);
  validateExplicitTestFiles(targetedInvocation.args);
  console.log('\n[unit] Running targeted selection without sharding.');
  const resultFile = resolve(process.cwd(), `.unit-target-results-${process.pid}.json`);
  const result = runVitest(targetedInvocation.args, resultFile, targetedInvocation.command);
  const executedTestCount = readExecutedTargetTestCount(resultFile);
  removeIfPresent(resultFile);
  exitForResult(result, 'Targeted selection', startedAt);
  if (executedTestCount <= 0) {
    console.error('[unit] Targeted selection matched zero tests.');
    process.exit(1);
  }
  console.log(`[unit] Targeted selection passed (${executedTestCount} tests) in ${formatDuration(startedAt)}.`);
  process.exit(0);
}

for (let shard = 1; shard <= shardCount; shard += 1) {
  const shardStartedAt = performance.now();
  console.log(`\n[unit] Running shard ${shard}/${shardCount}`);

  const result = runVitest([`--shard=${shard}/${shardCount}`]);
  exitForResult(result, `Shard ${shard}/${shardCount}`, shardStartedAt);

  console.log(`[unit] Shard ${shard}/${shardCount} passed in ${formatDuration(shardStartedAt)}.`);
}

console.log(`\n[unit] All ${shardCount} shards passed in ${formatDuration(startedAt)}.`);

function runVitest(args, resultFile, command = 'run') {
  return spawnSync(process.execPath, [
    vitestBin,
    command,
    `--maxWorkers=${maxWorkers}`,
    '--reporter=dot',
    ...(resultFile ? ['--reporter=json', `--outputFile.json=${resultFile}`] : []),
    '--silent=passed-only',
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

function resolveTargetedInvocation(args) {
  if (args[0] === 'related') return { command: 'related', args: args.slice(1) };
  const relatedIndex = args.indexOf('--related');
  if (relatedIndex < 0) return { command: 'run', args };
  return {
    command: 'related',
    args: [...args.slice(0, relatedIndex), ...args.slice(relatedIndex + 1)],
  };
}

function validateExplicitTestFiles(args) {
  const missingFiles = explicitTestFileArgs(args)
    .filter(file => !existsSync(resolve(process.cwd(), file)));
  if (missingFiles.length === 0) return;

  console.error('[unit] Explicit test file(s) not found:');
  for (const file of missingFiles) console.error(`  - ${file}`);
  process.exit(2);
}

function explicitTestFileArgs(args) {
  const files = [];
  const optionsWithValue = new Set([
    '-t',
    '--testNamePattern',
    '--dir',
    '--root',
    '--config',
    '--environment',
    '--maxWorkers',
    '--minWorkers',
    '--pool',
    '--sequence',
    '--shard',
    '--reporter',
    '--outputFile',
  ]);

  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === '--') {
      files.push(...args.slice(index + 1).filter(isExplicitTestFile));
      break;
    }
    if (optionsWithValue.has(arg)) {
      index += 1;
      continue;
    }
    if (arg.startsWith('-')) continue;
    if (isExplicitTestFile(arg)) files.push(arg);
  }
  return files;
}

function isExplicitTestFile(value) {
  return /\.(?:test|spec)\.[cm]?[jt]sx?$/.test(value) && !/[*?[\]{}]/.test(value);
}

function readExecutedTargetTestCount(resultFile) {
  if (!existsSync(resultFile)) return 0;
  try {
    const report = JSON.parse(readFileSync(resultFile, 'utf8'));
    return Number(report.numPassedTests ?? 0) + Number(report.numFailedTests ?? 0);
  } catch (error) {
    console.error(`[unit] Could not read targeted Vitest results: ${error.message}`);
    return 0;
  }
}

function removeIfPresent(path) {
  if (existsSync(path)) unlinkSync(path);
}

function exitForResult(result, label, runStartedAt) {
  if (result.error) {
    console.error(`[unit] ${label} failed to start: ${result.error.message}`);
    process.exit(1);
  }
  if (result.status !== 0) {
    console.error(`[unit] ${label} failed after ${formatDuration(runStartedAt)}.`);
    process.exit(result.status ?? 1);
  }
}

function readPositiveInteger(name, fallback) {
  const raw = process.env[name];
  if (raw == null || raw === '') return fallback;

  const value = Number(raw);
  if (!Number.isSafeInteger(value) || value <= 0) {
    console.error(`[unit] ${name} must be a positive integer; received "${raw}".`);
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
