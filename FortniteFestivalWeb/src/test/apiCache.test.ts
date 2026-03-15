import { describe, it, expect, vi, beforeEach } from 'vitest';
import { cachedFetch, clearApiCache, isCached } from '../api/cache';

describe('cachedFetch', () => {
  beforeEach(() => {
    clearApiCache();
    vi.useRealTimers();
  });

  it('calls fetcher on first request', async () => {
    const fetcher = vi.fn().mockResolvedValue({ data: 'hello' });
    const result = await cachedFetch('key1', fetcher);
    expect(result).toEqual({ data: 'hello' });
    expect(fetcher).toHaveBeenCalledTimes(1);
  });

  it('returns cached data on subsequent calls', async () => {
    const fetcher = vi.fn().mockResolvedValue({ data: 'hello' });
    await cachedFetch('key2', fetcher);
    const result = await cachedFetch('key2', fetcher);
    expect(result).toEqual({ data: 'hello' });
    expect(fetcher).toHaveBeenCalledTimes(1);
  });

  it('re-fetches after TTL expires', async () => {
    vi.useFakeTimers();
    const fetcher = vi.fn()
      .mockResolvedValueOnce('first')
      .mockResolvedValueOnce('second');

    await cachedFetch('key3', fetcher, 100);
    expect(fetcher).toHaveBeenCalledTimes(1);

    vi.advanceTimersByTime(150);
    const result = await cachedFetch('key3', fetcher, 100);
    expect(result).toBe('second');
    expect(fetcher).toHaveBeenCalledTimes(2);
  });

  it('clearApiCache removes specific key', async () => {
    const fetcher = vi.fn().mockResolvedValue('data');
    await cachedFetch('a', fetcher);
    await cachedFetch('b', fetcher);

    clearApiCache('a');
    expect(isCached('a')).toBe(false);
    expect(isCached('b')).toBe(true);
  });

  it('clearApiCache with no args clears all', async () => {
    const fetcher = vi.fn().mockResolvedValue('data');
    await cachedFetch('x', fetcher);
    await cachedFetch('y', fetcher);

    clearApiCache();
    expect(isCached('x')).toBe(false);
    expect(isCached('y')).toBe(false);
  });

  it('isCached returns false for missing keys', () => {
    expect(isCached('nonexistent')).toBe(false);
  });
});
