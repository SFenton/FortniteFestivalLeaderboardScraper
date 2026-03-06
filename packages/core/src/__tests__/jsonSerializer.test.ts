import {loadOrDefault, parseJson, savePretty} from '../io/jsonSerializer';

describe('jsonSerializer', () => {
  test('loadOrDefault returns default on null/invalid', () => {
    expect(loadOrDefault(null, () => ({a: 1}))).toEqual({a: 1});
    expect(loadOrDefault('{bad', () => ({a: 1}))).toEqual({a: 1});
    expect(loadOrDefault('null', () => ({a: 1}))).toEqual({a: 1});
  });

  test('loadOrDefault returns parsed object', () => {
    expect(loadOrDefault('{"a":2}', () => ({a: 1}))).toEqual({a: 2});
  });

  test('savePretty stringifies', () => {
    const s = savePretty({a: 1});
    expect(s).toContain('"a"');
  });

  test('savePretty returns empty on circular input', () => {
    const o: any = {a: 1};
    o.self = o;
    expect(savePretty(o)).toBe('');
  });

  test('parseJson parses valid JSON', () => {
    expect(parseJson('{"a":1}')).toEqual({a: 1});
  });

  test('parseJson throws on invalid JSON', () => {
    expect(() => parseJson('{bad')).toThrow();
  });
});
