import {
  __resetForTests,
  buildSummaryLine,
  computeCorrelationId,
  extractError,
  formatHttpError,
} from '../httpErrorHelper';

describe('httpErrorHelper', () => {
  beforeEach(() => {
    __resetForTests();
  });

  test('buildSummaryLine returns <none> when empty', () => {
    expect(buildSummaryLine()).toBe('ErrorCodeSummary: <none>');
  });

  test('extractError reads errorCode/errorMessage', () => {
    const e = extractError('{"errorCode":"errors.com.epicgames.test","errorMessage":"nope"}');
    expect(e.errorCode).toBe('errors.com.epicgames.test');
    expect(e.errorMessage).toBe('nope');
  });

  test('extractError returns undefined fields when not present', () => {
    const e = extractError('{}');
    expect(e).toEqual({errorCode: undefined, errorMessage: undefined});
  });

  test('extractError falls back to message field', () => {
    const e = extractError('{"errorCode":"E","message":"fallback"}');
    expect(e.errorCode).toBe('E');
    expect(e.errorMessage).toBe('fallback');
  });

  test('extractError tolerates non-json and returns empty', () => {
    expect(extractError('not json')).toEqual({});
  });

  test('extractError handles null/empty inputs', () => {
    expect(extractError(null)).toEqual({});
    expect(extractError('')).toEqual({});
    expect(extractError('   ')).toEqual({});
  });

  test('extractError handles invalid json object', () => {
    expect(extractError('{invalid')).toEqual({});
  });

  test('formatHttpError includes op/status/code and snippet', () => {
    const s = formatHttpError({
      op: 'FetchSongs',
      status: 500,
      statusText: 'Server Error',
      body: '{"errorCode":"X","errorMessage":"Y"}',
      errorCode: 'X',
      errorMessage: 'Y',
    });
    expect(s).toContain('[FetchSongs] HTTP 500 (Server Error)');
    expect(s).toContain('errorCode=X');
    expect(s).toContain('bodySnippet=');
  });

  test('formatHttpError handles missing optional fields', () => {
    const s = formatHttpError({op: 'Op', status: 404});
    expect(s).toContain('[Op] HTTP 404');
    expect(s).toContain('errorCode=<none>');
  });

  test('formatHttpError omits msg when errorMessage is blank', () => {
    const s = formatHttpError({op: 'Op', status: 400, errorMessage: '   ', body: 'x'});
    expect(s).not.toContain('msg="');
  });

  test('formatHttpError truncates long body and message', () => {
    const longBody = 'a'.repeat(500);
    const longMsg = 'b'.repeat(500);
    const s = formatHttpError({op: 'Op', status: 500, body: longBody, errorMessage: longMsg});
    expect(s).toContain('bodySnippet=');
    expect(s).toContain('msg="');
    expect(s.length).toBeLessThan(1200);
  });

  test('computeCorrelationId is stable for same input', () => {
    const err = new Error('boom');
    const a = computeCorrelationId(err);
    const b = computeCorrelationId(err);
    expect(a).toBe(b);
    expect(a).toMatch(/^[0-9A-F]{8}$/);
  });

  test('computeCorrelationId supports non-error values', () => {
    expect(computeCorrelationId('x')).toMatch(/^[0-9A-F]{8}$/);
  });

  test('computeCorrelationId tolerates Error objects with missing stack', () => {
    const err = new Error('boom');
    // Some environments omit stack; ensure our nullish fallback is exercised.
    (err as any).stack = undefined;
    expect(computeCorrelationId(err)).toMatch(/^[0-9A-F]{8}$/);
  });

  test('computeCorrelationId handles undefined', () => {
    expect(computeCorrelationId(undefined)).toMatch(/^[0-9A-F]{8}$/);
  });

  test('buildSummaryLine returns a string', () => {
    // extractError increments counts
    extractError('{"errorCode":"A","errorMessage":"x"}');
    extractError('{"errorCode":"A","errorMessage":"y"}');
    extractError('{"errorCode":"B","errorMessage":"z"}');
    const line = buildSummaryLine();
    expect(line).toContain('ErrorCodeSummary:');
    expect(line).toContain('A=2');
    expect(line).toContain('B=1');
  });
});
