import {FstAuthClient, FstAuthError} from '../fstAuthClient';

// ── mock global fetch ──
const mockFetch = jest.fn() as jest.Mock;
(global as any).fetch = mockFetch;

afterEach(() => mockFetch.mockReset());

describe('FstAuthClient', () => {
  const client = new FstAuthClient('https://example.com/');

  /* ── login ── */

  test('login sends POST and returns parsed JSON on success', async () => {
    const body = {accessToken: 'at', refreshToken: 'rt', expiresIn: 3600, accountId: '1', displayName: 'User', friends: []};
    mockFetch.mockResolvedValueOnce({ok: true, json: () => Promise.resolve(body)});

    const result = await client.login('User', 'dev1', 'windows');

    expect(mockFetch).toHaveBeenCalledWith('https://example.com/api/auth/login', expect.objectContaining({
      method: 'POST',
      headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({username: 'User', deviceId: 'dev1', platform: 'windows'}),
    }));
    expect(result).toEqual(body);
  });

  test('login throws FstAuthError on non-ok response', async () => {
    mockFetch.mockResolvedValue({ok: false, status: 401, text: () => Promise.resolve('Unauthorized')});

    try {
      await client.login('u', 'd', 'ios');
      fail('Expected FstAuthError');
    } catch (e: any) {
      expect(e).toBeInstanceOf(FstAuthError);
      expect(e.endpoint).toBe('login');
      expect(e.statusCode).toBe(401);
      expect(e.responseBody).toBe('Unauthorized');
    }
  });

  test('login handles text() rejection gracefully', async () => {
    mockFetch.mockResolvedValue({ok: false, status: 500, text: () => Promise.reject(new Error('body fail'))});

    await expect(client.login('u', 'd', 'android')).rejects.toThrow(FstAuthError);
  });

  /* ── refresh ── */

  test('refresh sends POST and returns parsed JSON on success', async () => {
    const body = {accessToken: 'at2', refreshToken: 'rt2', expiresIn: 7200};
    mockFetch.mockResolvedValueOnce({ok: true, json: () => Promise.resolve(body)});

    const result = await client.refresh('rt1');
    expect(result).toEqual(body);
    expect(mockFetch).toHaveBeenCalledWith('https://example.com/api/auth/refresh', expect.objectContaining({
      body: JSON.stringify({refreshToken: 'rt1'}),
    }));
  });

  test('refresh throws FstAuthError on non-ok response', async () => {
    mockFetch.mockResolvedValueOnce({ok: false, status: 403, text: () => Promise.resolve('Forbidden')});

    await expect(client.refresh('bad')).rejects.toThrow(FstAuthError);
  });

  /* ── logout ── */

  test('logout sends POST and completes silently on success', async () => {
    mockFetch.mockResolvedValueOnce({ok: true});

    await expect(client.logout('rt')).resolves.toBeUndefined();
    expect(mockFetch).toHaveBeenCalledWith('https://example.com/api/auth/logout', expect.objectContaining({
      body: JSON.stringify({refreshToken: 'rt'}),
    }));
  });

  test('logout warns but does not throw on non-ok response', async () => {
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});
    mockFetch.mockResolvedValueOnce({ok: false, status: 500, text: () => Promise.resolve('err')});

    await expect(client.logout('rt')).resolves.toBeUndefined();
    expect(warnSpy).toHaveBeenCalledWith(expect.stringContaining('logout returned 500'), expect.anything());
    warnSpy.mockRestore();
  });

  test('logout handles text() rejection in warning', async () => {
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});
    mockFetch.mockResolvedValueOnce({ok: false, status: 503, text: () => Promise.reject(new Error('fail'))});

    await expect(client.logout('rt')).resolves.toBeUndefined();
    expect(warnSpy).toHaveBeenCalled();
    warnSpy.mockRestore();
  });
});

/* ── FstAuthError ── */

describe('FstAuthError', () => {
  test('stores endpoint, statusCode, responseBody', () => {
    const err = new FstAuthError('login', 500, 'Internal');
    expect(err.name).toBe('FstAuthError');
    expect(err.message).toBe('FST auth error on login: HTTP 500');
    expect(err.endpoint).toBe('login');
    expect(err.statusCode).toBe(500);
    expect(err.responseBody).toBe('Internal');
    expect(err).toBeInstanceOf(Error);
  });
});
