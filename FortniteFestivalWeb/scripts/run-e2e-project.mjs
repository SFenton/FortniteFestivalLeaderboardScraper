import { spawn, spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
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

  for (let shard = 1; shard <= shardCount; shard += 1) {
    const shardStartedAt = performance.now();
    console.log(`\n[e2e] Running ${project} shard ${shard}/${shardCount}`);
    const result = spawnSync(process.execPath, [
      playwrightBin,
      'test',
      `--project=${project}`,
      '--workers=1',
      `--shard=${shard}/${shardCount}`,
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

    if (result.error) {
      console.error(`[e2e] Shard ${shard}/${shardCount} failed to start: ${result.error.message}`);
      exitCode = 1;
      break;
    }
    if (result.status !== 0) {
      console.error(`[e2e] ${project} shard ${shard}/${shardCount} failed after ${duration(shardStartedAt)}.`);
      exitCode = result.status ?? 1;
      break;
    }

    console.log(`[e2e] ${project} shard ${shard}/${shardCount} passed in ${duration(shardStartedAt)}.`);
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
