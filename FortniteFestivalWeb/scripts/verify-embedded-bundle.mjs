import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = resolve(webRoot, '..');
const outputRoot = resolve(repoRoot, 'FSTService/wwwroot');
const retiredReferences = ['/api/features', 'FeatureFlagsContext'];
const aliases = [
  ['icons/fst-icon-512.png', 'icons/fst-icon-maskable-512.png'],
  ['manual/screenshots/song-detail-overview-mobile.png', 'manual/screenshots/song-detail-cards-mobile.png'],
  ['manual/screenshots/song-detail-overview-compact.png', 'manual/screenshots/song-detail-cards-compact.png'],
  ['manual/screenshots/song-detail-overview-wide.png', 'manual/screenshots/song-detail-cards-wide.png'],
];

verifyIndexAssets();
verifyCompatibilityAliases();
verifyRetiredReferences();
verifyGitClean();

console.log('[embedded] Committed bundle is current, self-contained, and compatibility-safe.');

function verifyIndexAssets() {
  const indexPath = resolve(outputRoot, 'index.html');
  const html = readFileSync(indexPath, 'utf8');
  const references = [...html.matchAll(/(?:src|href)="(\/assets\/[^"]+)"/g)]
    .map(match => match[1]);
  if (references.length === 0) fail('index.html does not reference any built assets.');
  for (const reference of references) {
    if (!existsSync(resolve(outputRoot, reference.slice(1)))) {
      fail(`index.html references missing asset ${reference}.`);
    }
  }
}

function verifyCompatibilityAliases() {
  for (const [sourceRelative, aliasRelative] of aliases) {
    const source = readFileSync(resolve(outputRoot, sourceRelative));
    const alias = readFileSync(resolve(outputRoot, aliasRelative));
    if (!source.equals(alias)) fail(`${aliasRelative} does not match ${sourceRelative}.`);
  }
}

function verifyRetiredReferences() {
  const matches = [];
  for (const file of listFiles(outputRoot)) {
    if (!/\.(?:html|js|json|map)$/.test(file)) continue;
    const contents = readFileSync(file, 'utf8');
    for (const reference of retiredReferences) {
      if (contents.includes(reference)) matches.push(`${file}: ${reference}`);
    }
  }
  if (matches.length > 0) fail(`retired references remain:\n${matches.join('\n')}`);
}

function verifyGitClean() {
  if (process.env.FST_EMBEDDED_COMPARE_INDEX_ONLY === '1') {
    const unstaged = execFileSync(
      'git',
      ['diff', '--name-status', '--', 'FSTService/wwwroot'],
      { cwd: repoRoot, encoding: 'utf8' },
    ).trim();
    const untracked = execFileSync(
      'git',
      ['ls-files', '--others', '--exclude-standard', '--', 'FSTService/wwwroot'],
      { cwd: repoRoot, encoding: 'utf8' },
    ).trim();
    const differences = [unstaged, untracked].filter(Boolean).join('\n');
    if (differences) fail(`FSTService/wwwroot differs from the prepared index:\n${differences}`);
    return;
  }

  const status = execFileSync(
    'git',
    ['status', '--porcelain=v1', '--untracked-files=all', '--', 'FSTService/wwwroot'],
    { cwd: repoRoot, encoding: 'utf8' },
  ).trim();
  if (status) fail(`FSTService/wwwroot differs from the checked-out commit:\n${status}`);
}

function listFiles(root) {
  const output = execFileSync('find', [root, '-type', 'f', '-print'], { encoding: 'utf8' });
  return output.trim() ? output.trim().split('\n') : [];
}

function fail(message) {
  console.error(`[embedded] ${message}`);
  process.exit(1);
}
