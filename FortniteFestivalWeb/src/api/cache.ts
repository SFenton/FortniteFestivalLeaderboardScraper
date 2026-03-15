/**
 * Lightweight API response cache with TTL.
 * Wraps fetch calls to avoid duplicate requests within a time window.
 */
const cache = new Map<string, { data: unknown; ts: number }>();

const DEFAULT_TTL = 5 * 60 * 1000; // 5 minutes

/**
 * Return cached data if fresh, otherwise call `fetcher` and cache the result.
 *
 * @param key     Cache key (typically the API path)
 * @param fetcher Async function that produces the data
 * @param ttl     Time-to-live in milliseconds (default 5 min)
 */
export async function cachedFetch<T>(
  key: string,
  fetcher: () => Promise<T>,
  ttl = DEFAULT_TTL,
): Promise<T> {
  const cached = cache.get(key);
  if (cached && Date.now() - cached.ts < ttl) {
    return cached.data as T;
  }
  const data = await fetcher();
  cache.set(key, { data, ts: Date.now() });
  return data;
}

/** Clear one or all cached entries. */
export function clearApiCache(key?: string): void {
  if (key) cache.delete(key);
  else cache.clear();
}

/** Check if a key is cached and fresh. */
export function isCached(key: string, ttl = DEFAULT_TTL): boolean {
  const cached = cache.get(key);
  return !!cached && Date.now() - cached.ts < ttl;
}
