import { copyFileSync, mkdirSync, statSync } from 'node:fs';
import { dirname, isAbsolute, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const outputRoot = resolveOutputRoot();
const aliases = [
  ['icons/fst-icon-512.png', 'icons/fst-icon-maskable-512.png'],
  ['manual/screenshots/song-detail-overview-mobile.png', 'manual/screenshots/song-detail-cards-mobile.png'],
  ['manual/screenshots/song-detail-overview-compact.png', 'manual/screenshots/song-detail-cards-compact.png'],
  ['manual/screenshots/song-detail-overview-wide.png', 'manual/screenshots/song-detail-cards-wide.png'],
];

for (const [sourceRelative, aliasRelative] of aliases) {
  const source = resolve(outputRoot, sourceRelative);
  const alias = resolve(outputRoot, aliasRelative);
  statSync(source);
  mkdirSync(dirname(alias), { recursive: true });
  copyFileSync(source, alias);
}

console.log(`[embedded] Wrote ${aliases.length} compatibility aliases to ${outputRoot}.`);

function resolveOutputRoot() {
  const configured = process.env.FST_WEB_OUT_DIR;
  if (!configured) return resolve(webRoot, '../FSTService/wwwroot');
  return isAbsolute(configured) ? configured : resolve(webRoot, configured);
}
