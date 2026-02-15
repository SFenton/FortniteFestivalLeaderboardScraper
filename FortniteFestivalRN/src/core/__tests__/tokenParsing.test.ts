import {parseExchangeCodeToken, parseTokenVerify} from '../auth/tokenParsing';

describe('tokenParsing', () => {
  test('parseExchangeCodeToken returns null for non-json/errorCode/missing fields', () => {
    expect(parseExchangeCodeToken(null)).toBeNull();
    expect(parseExchangeCodeToken('x')).toBeNull();
    expect(parseExchangeCodeToken('{"errorCode":"E"}')).toBeNull();
    expect(parseExchangeCodeToken('{"access_token":"t"}')).toBeNull();
    expect(parseExchangeCodeToken('{"account_id":"a"}')).toBeNull();
    expect(parseExchangeCodeToken('{bad')).toBeNull();
  });

  test('parseExchangeCodeToken returns token for valid shape', () => {
    const t = parseExchangeCodeToken('{"access_token":"t","account_id":"a"}');
    expect(t?.access_token).toBe('t');
    expect(t?.account_id).toBe('a');
  });

  test('parseTokenVerify extracts account_id and displayName', () => {
    expect(parseTokenVerify('{"account_id":"acc","displayName":"name"}')).toEqual({
      accountId: 'acc',
      displayName: 'name',
    });
  });

  test('parseTokenVerify returns undefined fields for valid json with missing keys', () => {
    expect(parseTokenVerify('{}')).toEqual({accountId: undefined, displayName: undefined});
  });

  test('parseTokenVerify ignores non-string fields', () => {
    expect(parseTokenVerify('{"account_id":123,"displayName":{}}')).toEqual({
      accountId: undefined,
      displayName: undefined,
    });
  });

  test('parseTokenVerify returns empty on invalid', () => {
    expect(parseTokenVerify(null)).toEqual({});
    expect(parseTokenVerify('x')).toEqual({});
    expect(parseTokenVerify('{bad')).toEqual({});
  });
});
