import { spawn, spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { readdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { performance } from 'node:perf_hooks';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const playwrightPackage = require.resolve('@playwright/test/package.json');
const playwrightBin = resolve(
  dirname(playwrightPackage),
  require(playwrightPackage).bin.playwright,
);
const vitePackage = require.resolve('vite/package.json');
const viteBin = resolve(dirname(vitePackage), require(vitePackage).bin.vite);
const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const project = process.argv[2];
const shardCount = readPositiveInteger('E2E_SHARDS', 8);
const port = readPositiveInteger('PLAYWRIGHT_PORT', 4173);
const isolatedTestFiles = [
  'e2e/remote-data-ownership.spec.ts',
  'e2e/secondary-controls-lazy.spec.ts',
];
const mainTestFiles = listTestFiles(resolve(webRoot, 'e2e'))
  .filter(file => !isolatedTestFiles.includes(file));
const startedAt = performance.now();

if (project !== 'desktop' && project !== 'mobile') {
  console.error(`[e2e] Project must be "desktop" or "mobile"; received "${project ?? ''}".`);
  process.exit(2);
}

const server = spawn(process.execPath, [
  viteBin,
  '--mode',
  'e2e',
  '--port',
  String(port),
  '--strictPort',
], {
  cwd: webRoot,
  env: process.env,
  stdio: ['ignore', 'inherit', 'inherit'],
});

let exitCode = 0;
try {
  await waitForServer(server, port);

  const isolatedStartedAt = performance.now();
  console.log(`\n[e2e] Running ${project} stateful isolation files`);
  const isolatedResult = runPlaywright(isolatedTestFiles);
  if (!handleResult(isolatedResult, `${project} stateful isolation files`, isolatedStartedAt)) {
    exitCode = isolatedResult.status ?? 1;
  }

  for (let shard = 1; exitCode === 0 && shard <= shardCount; shard += 1) {
    const shardStartedAt = performance.now();
    console.log(`\n[e2e] Running ${project} shard ${shard}/${shardCount}`);
    const result = runPlaywright([
      ...mainTestFiles,
      `--shard=${shard}/${shardCount}`,
    ]);
    if (!handleResult(result, `${project} shard ${shard}/${shardCount}`, shardStartedAt)) {
      exitCode = result.status ?? 1;
      continue;
    }
  }
} catch (error) {
  console.error(`[e2e] ${error instanceof Error ? error.message : String(error)}`);
  exitCode = 1;
} finally {
  await stopServer(server);
}

if (exitCode !== 0) {
  process.exit(exitCode);
}

console.log(`\n[e2e] All ${shardCount} ${project} shards passed in ${duration(startedAt)}.`);

function readPositiveInteger(name, fallback) {
  const raw = process.env[name];
  if (raw == null || raw === '') return fallback;

  const value = Number(raw);
  if (!Number.isSafeInteger(value) || value <= 0) {
    console.error(`[e2e] ${name} must be a positive integer; received "${raw}".`);
    process.exit(2);
  }
  return value;
}

function runPlaywright(testArgs) {
  return spawnSync(process.execPath, [
    playwrightBin,
    'test',
    ...testArgs,
    `--project=${project}`,
    '--workers=1',
  ], {
    cwd: webRoot,
    env: {
      ...process.env,
      CI: process.env.CI ?? '1',
      PLAYWRIGHT_PORT: String(port),
      PLAYWRIGHT_REUSE_SERVER: '1',
    },
    stdio: 'inherit',
  });
}

function handleResult(result, label, runStartedAt) {
  if (result.error) {
    console.error(`[e2e] ${label} failed to start: ${result.error.message}`);
    return false;
  }
  if (result.status !== 0) {
    console.error(`[e2e] ${label} failed after ${duration(runStartedAt)}.`);
    return false;
  }
  console.log(`[e2e] ${label} passed in ${duration(runStartedAt)}.`);
  return true;
}

function listTestFiles(directory, relativeDirectory = 'e2e') {
  return readdirSync(directory, { withFileTypes: true })
    .flatMap(entry => {
      const relativePath = `${relativeDirectory}/${entry.name}`;
      if (entry.isDirectory()) {
        return listTestFiles(resolve(directory, entry.name), relativePath);
      }
      return entry.isFile() && entry.name.endsWith('.spec.ts')
        ? [relativePath]
        : [];
    })
    .sort();
}

async function waitForServer(child, serverPort) {
  const deadline = Date.now() + 60_000;
  const url = `http://127.0.0.1:${serverPort}/e2e/fixtures/reset.html`;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) {
      throw new Error(`Vite exited before becoming ready with code ${child.exitCode}.`);
    }
    try {
      const response = await fetch(url);
      if (response.ok) return;
    } catch {
      // Retry until the startup deadline.
    }
    await delay(250);
  }
  throw new Error(`Vite did not become ready at ${url} within 60 seconds.`);
}

async function stopServer(child) {
  if (child.exitCode !== null) return;
  child.kill('SIGTERM');
  const exited = await Promise.race([
    new Promise(resolveExit => child.once('exit', () => resolveExit(true))),
    delay(5_000).then(() => false),
  ]);
  if (!exited && child.exitCode === null) {
    child.kill('SIGKILL');
    await new Promise(resolveExit => child.once('exit', resolveExit));
  }
}

function delay(milliseconds) {
  return new Promise(resolveDelay => setTimeout(resolveDelay, milliseconds));
}

function duration(startedAt) {
  return `${((performance.now() - startedAt) / 1000).toFixed(1)}s`;
}
