#!/usr/bin/env node
import { createRequire } from 'node:module';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import ts from 'typescript';

const webRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const sourceRoot = path.join(webRoot, 'src');
const require = createRequire(import.meta.url);
const parserEntry = require.resolve('ast-v8-to-istanbul');
const parserRequire = createRequire(parserEntry);
const jsTokens = (await import(pathToFileURL(parserRequire.resolve('js-tokens')).href)).default;
const expectedParserVersion = '0.3.12';
const maxRangeLines = 50;
const allowedNextFingerprints = new Map([
  ['src/pages/suggestions/firstRun/demo/InstrumentFilterDemo.tsx', new Set([
    'v8 ignore next -- instrument always set from instruments[0]=>instrument ? getToggles(instrument) : [],',
  ])],
  ['src/pages/suggestions/firstRun/demo/CategoryCardDemo.tsx', new Set([
    "v8 ignore next -- songMeta only called when timer rotates to this template=>songMeta: (i) => ({ percentileDisplay: `Top ${3 + i}%`, instrumentKey: 'bass' as const }),",
    "v8 ignore next -- songMeta only called when timer rotates to this template=>songMeta: (i) => ({ instrumentKey: (['guitar', 'bass', 'drums', 'vocals'] as const)[i % 4] }),",
    "v8 ignore next -- songMeta only called when timer rotates to this template=>songMeta: () => ({ instrumentKey: 'drums' as const }),",
  ])],
]);
const directivePattern = /(?:istanbul|[cv]8|node:coverage)\s+ignore\s+(start|stop|next|if|else|file)(?:\s+(\d+))?/gi;
const parserDirectivePattern = /^\s*(?:istanbul|[cv]8|node:coverage)\s+ignore\s+(if|else|next|file)(?=\W|$)/i;

export function analyzeSource(filePath, source, options = {}) {
  const relativePath = normalizePath(options.relativePath ?? filePath);
  const maximumRangeLines = options.maxRangeLines ?? maxRangeLines;
  const allowedFingerprints = options.allowedNextFingerprints ?? new Set();
  const comments = commentSpans(filePath, source);
  const realComments = comments.filter(comment => !comment.jsxText);
  const errors = [];
  const ranges = [];
  const nextDirectives = [];
  let openRange = null;
  let offset = 0;
  const lines = source.split('\n');

  for (let index = 0; index < lines.length; index += 1) {
    const lineNumber = index + 1;
    const line = lines[index]?.replace(/\r$/, '') ?? '';
    const matches = [...line.matchAll(directivePattern)];
    if (matches.length > 1) {
      errors.push(`${relativePath}:${lineNumber}: multiple coverage directives on one line`);
    }

    for (const match of matches) {
      const markerOffset = offset + (match.index ?? 0);
      if (!realComments.some(span => markerOffset >= span.start && markerOffset < span.end)) {
        errors.push(`${relativePath}:${lineNumber}: coverage directive appears outside a comment`);
      }

      const type = match[1];
      if (type !== 'start' && type !== 'stop') continue;
      if (type === 'start') {
        if (openRange) {
          errors.push(`${relativePath}:${lineNumber}: nested ignore start; open range began at line ${openRange}`);
        } else {
          openRange = lineNumber;
        }
        continue;
      }

      if (!openRange) {
        errors.push(`${relativePath}:${lineNumber}: ignore stop has no matching start`);
        continue;
      }

      const length = lineNumber - openRange + 1;
      ranges.push({ start: openRange, stop: lineNumber, length });
      if (length > maximumRangeLines) {
        errors.push(`${relativePath}:${openRange}-${lineNumber}: ignore range spans ${length} lines; maximum is ${maximumRangeLines}`);
      }
      openRange = null;
    }
    offset += (lines[index]?.length ?? 0) + 1;
  }

  if (openRange) {
    errors.push(`${relativePath}:${openRange}: ignore range reaches EOF without a stop`);
  }

  for (const comment of comments) {
    const body = normalizeComment(comment.value);
    const directive = body.match(parserDirectivePattern)?.[1]?.toLowerCase();
    if (!directive) continue;
    const lineNumber = lineNumberAt(source, comment.start);
    if (comment.jsxText) {
      errors.push(`${relativePath}:${lineNumber}: coverage directive appears in JSX text`);
      continue;
    }
    if (directive === 'file' || directive === 'if' || directive === 'else') {
      errors.push(`${relativePath}:${lineNumber}: "${directive}" coverage directives are not permitted`);
      continue;
    }
    if (/\bignore\s+next\s+\d+/i.test(body)) {
      errors.push(`${relativePath}:${lineNumber}: counted ignore-next directives are unsupported`);
    }
    const fingerprint = `${normalizeWhitespace(body)}=>${nextCodeLine(source, comment.end)}`;
    nextDirectives.push({ line: lineNumber, fingerprint });
    if (!allowedFingerprints.has(fingerprint)) {
      errors.push(`${relativePath}:${lineNumber}: unverified ignore-next target "${fingerprint}"`);
    }
  }
  const fingerprintCounts = new Map();
  for (const directive of nextDirectives) {
    fingerprintCounts.set(directive.fingerprint, (fingerprintCounts.get(directive.fingerprint) ?? 0) + 1);
  }
  for (const [fingerprint, count] of fingerprintCounts) {
    if (count > 1) {
      errors.push(`${relativePath}: duplicate ignore-next fingerprint "${fingerprint}" appears ${count} times`);
    }
  }
  for (const fingerprint of allowedFingerprints) {
    if (!nextDirectives.some(directive => directive.fingerprint === fingerprint)) {
      errors.push(`${relativePath}: stale ignore-next fingerprint "${fingerprint}"`);
    }
  }

  return { errors, ranges, nextDirectives };
}

export function checkProject(root = sourceRoot) {
  const parserVersion = JSON.parse(
    readFileSync(path.resolve(path.dirname(parserEntry), '../package.json'), 'utf8'),
  ).version;
  const errors = [];
  if (parserVersion !== expectedParserVersion) {
    errors.push(`ast-v8-to-istanbul version ${parserVersion} requires coverage-ignore policy revalidation; expected ${expectedParserVersion}`);
  }

  let rangeCount = 0;
  let ignoredLineSlots = 0;
  let nextCount = 0;
  const files = listFiles(root).filter(file => /\.[cm]?[jt]sx?$/.test(file));
  const seenAllowlistPaths = new Set();
  for (const file of files) {
    const relativePath = normalizePath(path.relative(webRoot, file));
    const allowedFingerprints = allowedNextFingerprints.get(relativePath) ?? new Set();
    if (allowedNextFingerprints.has(relativePath)) seenAllowlistPaths.add(relativePath);
    const result = analyzeSource(file, readFileSync(file, 'utf8'), {
      relativePath,
      maxRangeLines,
      allowedNextFingerprints: allowedFingerprints,
    });
    errors.push(...result.errors);
    rangeCount += result.ranges.length;
    ignoredLineSlots += result.ranges.reduce((total, range) => total + range.length, 0);
    nextCount += result.nextDirectives.length;
  }

  for (const allowlistedPath of allowedNextFingerprints.keys()) {
    if (!seenAllowlistPaths.has(allowlistedPath)) {
      errors.push(`${allowlistedPath}: stale ignore-next allowlist entry`);
    }
  }

  return {
    errors,
    summary: {
      parserVersion,
      files: files.length,
      rangeCount,
      ignoredLineSlots,
      nextCount,
      maxRangeLines,
    },
  };
}

function commentSpans(filePath, source) {
  const jsxTextSpans = [];
  if (filePath.endsWith('x')) {
    const sourceFile = ts.createSourceFile(filePath, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
    const visit = node => {
      if (ts.isJsxText(node)) jsxTextSpans.push({ start: node.pos, end: node.end });
      ts.forEachChild(node, visit);
    };
    visit(sourceFile);
  }

  const spans = [];
  let offset = 0;
  for (const token of jsTokens(source)) {
    if (token.type === 'SingleLineComment' || token.type === 'MultiLineComment') {
      const end = offset + token.value.length;
      spans.push({
        start: offset,
        end,
        value: token.value,
        jsxText: jsxTextSpans.some(span => offset < span.end && end > span.start),
      });
    }
    offset += token.value.length;
  }
  return spans;
}

function normalizeComment(value) {
  return value
    .replace(/^\/\*\*/, '')
    .replace(/^\/\*/, '')
    .replace(/\*\*\/$/, '')
    .replace(/\*\/$/, '')
    .replace(/^\/\//, '')
    .trim();
}

function normalizeWhitespace(value) {
  return value.replace(/\s+/g, ' ').trim();
}

function lineNumberAt(source, offset) {
  return source.slice(0, offset).split('\n').length;
}

function nextCodeLine(source, offset) {
  for (const line of source.slice(offset).split('\n')) {
    const normalized = normalizeWhitespace(line);
    if (normalized) return normalized;
  }
  return '<eof>';
}

function listFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) return listFiles(fullPath);
    return statSync(fullPath).isFile() ? [fullPath] : [];
  });
}

function normalizePath(value) {
  return value.replaceAll('\\', '/');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const result = checkProject();
  console.log(JSON.stringify(result.summary, null, 2));
  if (result.errors.length > 0) {
    for (const error of result.errors) console.error(`[coverage-ignore] ${error}`);
    process.exitCode = 1;
  } else {
    console.log('[coverage-ignore] Coverage directives are bounded and parser-safe.');
  }
}
