import { describe, expect, it, vi } from 'vitest';
import { recoverFromStaleChunk } from '../../src/utils/staleChunkRecovery';

function createStorage() {
  const values = new Map<string, string>();
  return {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => values.set(key, value),
    removeItem: (key: string) => values.delete(key),
  };
}

describe('stale chunk recovery', () => {
  it('reloads once when Vite reports a missing lazy chunk', () => {
    const event = new Event('vite:preloadError', { cancelable: true });
    const reload = vi.fn();

    const recovered = recoverFromStaleChunk(event, {
      storage: createStorage(),
      reload,
      now: () => 1_000,
    });

    expect(recovered).toBe(true);
    expect(event.defaultPrevented).toBe(true);
    expect(reload).toHaveBeenCalledOnce();
  });

  it('does not create a reload loop within the guard window', () => {
    const storage = createStorage();
    const reload = vi.fn();
    const firstEvent = new Event('vite:preloadError', { cancelable: true });
    const secondEvent = new Event('vite:preloadError', { cancelable: true });

    expect(recoverFromStaleChunk(firstEvent, { storage, reload, now: () => 1_000 })).toBe(true);
    expect(recoverFromStaleChunk(secondEvent, { storage, reload, now: () => 2_000 })).toBe(false);

    expect(secondEvent.defaultPrevented).toBe(false);
    expect(reload).toHaveBeenCalledOnce();
  });

  it('allows a later deployment recovery after the guard window', () => {
    const storage = createStorage();
    const reload = vi.fn();

    expect(recoverFromStaleChunk(
      new Event('vite:preloadError', { cancelable: true }),
      { storage, reload, now: () => 1_000 },
    )).toBe(true);
    expect(recoverFromStaleChunk(
      new Event('vite:preloadError', { cancelable: true }),
      { storage, reload, now: () => 62_000 },
    )).toBe(true);

    expect(reload).toHaveBeenCalledTimes(2);
  });
});
