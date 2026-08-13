import assert from 'node:assert/strict';
import test from 'node:test';
import { analyzeSource } from '../FortniteFestivalWeb/scripts/check-coverage-ignores.mjs';

function errors(source, options = {}) {
  return analyzeSource('fixture.tsx', source, { relativePath: 'fixture.tsx', ...options }).errors;
}

test('accepts one bounded comment range', () => {
  assert.deepEqual(errors('/* v8 ignore start */\nconst value = 1;\n/* v8 ignore stop */'), []);
});

test('rejects an unclosed range', () => {
  assert.ok(errors('/* v8 ignore start */\nconst value = 1;').some(error => error.includes('reaches EOF')));
});

test('rejects nested starts and orphan stops', () => {
  const result = errors('/* v8 ignore stop */\n/* v8 ignore start */\n/* v8 ignore start */\n/* v8 ignore stop */');
  assert.ok(result.some(error => error.includes('no matching start')));
  assert.ok(result.some(error => error.includes('nested ignore start')));
});

test('rejects range expansion beyond the maximum', () => {
  const source = ['/* v8 ignore start */', ...Array.from({ length: 50 }, () => 'work();'), '/* v8 ignore stop */'].join('\n');
  assert.ok(errors(source).some(error => error.includes('maximum is 50')));
});

test('rejects directives in strings and multiple markers on one line', () => {
  const result = errors('const text = "v8 ignore start";\n/* v8 ignore start */ /* v8 ignore stop */');
  assert.ok(result.some(error => error.includes('outside a comment')));
  assert.ok(result.some(error => error.includes('multiple coverage directives')));
});

test('rejects unsupported and unverified ignore-next directives', () => {
  assert.ok(errors('/* v8 ignore next 6 */\nrun();').some(error => error.includes('unsupported')));
  assert.ok(errors('/* v8 ignore next */\nrun();').some(error => error.includes('unverified')));
});

test('accepts only exact directive-to-target fingerprints and rejects stale allowlists', () => {
  const fingerprint = 'v8 ignore next -- verified=>run();';
  assert.deepEqual(errors('/* v8 ignore next -- verified */\nrun();', {
    allowedNextFingerprints: new Set([fingerprint]),
  }), []);
  assert.ok(errors('/* v8 ignore next -- verified */\nother();', {
    allowedNextFingerprints: new Set([fingerprint]),
  }).some(error => error.includes('unverified')));
  assert.ok(errors('run();', {
    allowedNextFingerprints: new Set([fingerprint]),
  }).some(error => error.includes('stale')));
});

test('rejects parser-supported file and branch directives including multiline next', () => {
  const result = errors('/* v8 ignore file */\n/* v8 ignore if */\n/* v8 ignore else */\n/* v8 ignore\n next */\nrun();');
  assert.ok(result.some(error => error.includes('"file"')));
  assert.ok(result.some(error => error.includes('"if"')));
  assert.ok(result.some(error => error.includes('"else"')));
  assert.ok(result.some(error => error.includes('unverified')));
});

test('rejects JSX text that resembles a coverage comment', () => {
  const blockResult = errors('const node = <div>/* v8 ignore start */</div>;\n/* v8 ignore stop */');
  assert.ok(blockResult.some(error => error.includes('outside a comment')));
  const lineResult = errors('const node = <div>// v8 ignore start</div>;\n/* v8 ignore stop */');
  assert.ok(lineResult.some(error => error.includes('outside a comment')));
  const multilineResult = errors('const node = <div>/* v8 ignore\n file */</div>;');
  assert.ok(multilineResult.some(error => error.includes('coverage directive appears in JSX text')));
});

test('rejects duplicate verified ignore-next fingerprints', () => {
  const fingerprint = 'v8 ignore next -- verified=>run();';
  const result = errors(
    '/* v8 ignore next -- verified */\nrun();\n/* v8 ignore next -- verified */\nrun();',
    { allowedNextFingerprints: new Set([fingerprint]) },
  );
  assert.ok(result.some(error => error.includes('duplicate ignore-next fingerprint')));
});
