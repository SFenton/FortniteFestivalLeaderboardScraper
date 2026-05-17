import { chromium } from '@playwright/test';
import { createServer } from 'vite';
import { copyFile, mkdir, readFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { basename, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(scriptDir, '..');
const repoRoot = resolve(webRoot, '..');
const webIconDir = resolve(webRoot, 'public/icons');
const serviceIconDir = resolve(repoRoot, 'FSTService/wwwroot/icons');
const viteConfig = resolve(webRoot, 'vite.config.ts');
const chromiumFallback = '/home/sfenton/.cache/ms-playwright/chromium-1208/chrome-linux64/chrome';

const outputs = [
  { size: 192, fileName: 'fst-icon-192.png' },
  { size: 512, fileName: 'fst-icon-512.png' },
  { size: 512, fileName: 'fst-icon-maskable-512.png' },
];

function readPngDimensions(buffer) {
  if (buffer.toString('ascii', 1, 4) !== 'PNG') {
    throw new Error('Generated file is not a PNG.');
  }

  return {
    width: buffer.readUInt32BE(16),
    height: buffer.readUInt32BE(20),
  };
}

async function createViteServer() {
  const server = await createServer({
    configFile: viteConfig,
    root: webRoot,
    server: {
      host: '127.0.0.1',
      port: 0,
      strictPort: false,
      watch: null,
    },
    clearScreen: false,
    logLevel: 'error',
  });

  await server.listen();
  const localUrl = server.resolvedUrls?.local?.[0];
  if (!localUrl) {
    throw new Error('Vite did not expose a local URL for icon generation.');
  }

  return { server, baseUrl: localUrl };
}

async function launchBrowser() {
  const launchOptions = {
    headless: true,
    args: ['--no-sandbox', '--disable-setuid-sandbox', '--disable-gpu', '--disable-dev-shm-usage'],
  };

  if (process.env.PWA_ICON_CHROMIUM) {
    return chromium.launch({ ...launchOptions, executablePath: process.env.PWA_ICON_CHROMIUM });
  }

  if (existsSync(chromiumFallback)) {
    return chromium.launch({ ...launchOptions, executablePath: chromiumFallback });
  }

  return chromium.launch(launchOptions);
}

async function captureIcon(browser, baseUrl, output) {
  const page = await browser.newPage({ viewport: { width: output.size, height: output.size }, deviceScaleFactor: 1 });
  const outputPath = resolve(webIconDir, output.fileName);

  try {
    await page.goto(`${baseUrl}?pwaIconCapture=1&pwaIconSize=${output.size}`, { waitUntil: 'networkidle' });
    const icon = page.getByTestId('pwa-icon-capture');
    await icon.waitFor({ state: 'visible', timeout: 10_000 });
    await mkdir(dirname(outputPath), { recursive: true });
    await icon.screenshot({ path: outputPath, type: 'png' });
  } finally {
    await page.close();
  }

  const dimensions = readPngDimensions(await readFile(outputPath));
  if (dimensions.width !== output.size || dimensions.height !== output.size) {
    throw new Error(`${output.fileName} generated at ${dimensions.width}x${dimensions.height}, expected ${output.size}x${output.size}.`);
  }

  return outputPath;
}

async function mirrorAssets() {
  await mkdir(serviceIconDir, { recursive: true });
  await copyFile(resolve(webIconDir, 'fst-icon.svg'), resolve(serviceIconDir, 'fst-icon.svg'));

  for (const output of outputs) {
    const sourcePath = resolve(webIconDir, output.fileName);
    await copyFile(sourcePath, resolve(serviceIconDir, basename(sourcePath)));
  }
}

let viteServer;
let browser;

try {
  const vite = await createViteServer();
  viteServer = vite.server;
  browser = await launchBrowser();

  for (const output of outputs) {
    await captureIcon(browser, vite.baseUrl, output);
  }

  await mirrorAssets();
} finally {
  await browser?.close();
  await viteServer?.close();
}