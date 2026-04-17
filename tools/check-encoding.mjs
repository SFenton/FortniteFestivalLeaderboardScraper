#!/usr/bin/env node
/**
 * Scan for mojibake (double-encoded UTF-8) byte signatures in source files.
 *
 * Detects the byte sequence `C3 A2 E2 82 AC` which is the signature of a UTF-8
 * character (e.g. `…` `–` `—` `'` `"` `"`) that was decoded as Windows-1252
 * and re-encoded as UTF-8 — a classic encoding corruption.
 *
 * Exits 0 on clean, 1 if any hits found.
 *
 * Usage:  node tools/check-encoding.mjs
 */
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const ROOTS = ['FortniteFestivalWeb/src', 'FortniteFestivalWeb/__test__'].map(p => join(REPO_ROOT, p));
const EXTS = new Set(['.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs', '.json', '.css', '.md', '.html']);
const IGNORE_DIRS = new Set(['node_modules', 'dist', 'build', '.next', 'coverage']);

// Mojibake signatures:
//   C3 A2 E2 82 AC = double-encoded `…` `–` `—` `'` `"` `"` (UTF-8 read as Win1252 then re-UTF-8'd)
//   C3 83 C2 ..    = double-encoded Latin accented letter (e.g. `é` → `Ã©`)
//   C3 82 C2 ..    = double-encoded Latin-1 Supplement letter
const SIGNATURES = [
  { name: 'punctuation (…–—\u2018\u2019\u201C\u201D)', bytes: [0xc3, 0xa2, 0xe2, 0x82, 0xac] },
];

function* walk(dir) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const s = statSync(full);
    if (s.isDirectory()) {
      if (IGNORE_DIRS.has(entry)) continue;
      yield* walk(full);
    } else if (s.isFile()) {
      const dot = entry.lastIndexOf('.');
      if (dot > 0 && EXTS.has(entry.slice(dot))) yield full;
    }
  }
}

function matchesAt(buf, offset, pattern) {
  for (let i = 0; i < pattern.length; i++) {
    if (buf[offset + i] !== pattern[i]) return false;
  }
  return true;
}

function findHits(buf) {
  const hits = [];
  for (const sig of SIGNATURES) {
    for (let i = 0; i <= buf.length - sig.bytes.length; i++) {
      if (matchesAt(buf, i, sig.bytes)) {
        hits.push({ offset: i, signature: sig.name });
        break; // one hit per signature per file is enough to report
      }
    }
  }
  return hits;
}

let failed = 0;
for (const root of ROOTS) {
  try {
    statSync(root);
  } catch {
    continue;
  }
  for (const file of walk(root)) {
    const buf = readFileSync(file);
    const hits = findHits(buf);
    if (hits.length) {
      failed++;
      const rel = relative(REPO_ROOT, file).split(sep).join('/');
      for (const h of hits) {
        console.error(`MOJIBAKE  ${rel}  (byte ${h.offset}, ${h.signature})`);
      }
    }
  }
}

if (failed) {
  console.error(`\n${failed} file(s) contain double-encoded UTF-8 mojibake.`);
  console.error('Repair with the transform: UTF8.GetString(Win1252.GetBytes(UTF8.GetString(bytes))).');
  process.exit(1);
}
console.log('check-encoding: no mojibake signatures found.');
