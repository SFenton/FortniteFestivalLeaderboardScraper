import { useState, useCallback, useEffect, useMemo } from 'react';

const STORAGE_KEY = 'fst:trackedPlayer';
const SYNC_EVENT = 'fst:trackedPlayerChanged';

export type TrackedPlayer = {
  accountId: string;
  displayName: string;
};

const UNKNOWN_USER = 'Unknown User';

function loadPlayer(): TrackedPlayer | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (parsed?.accountId) {
      return { accountId: parsed.accountId, displayName: parsed.displayName || UNKNOWN_USER };
    }
    /* v8 ignore start */
    return null;
    /* v8 ignore stop */
  } catch {
    return null;
  }
}

export function useTrackedPlayer() {
  const [player, setPlayerState] = useState<TrackedPlayer | null>(loadPlayer);

  const setPlayer = useCallback((p: TrackedPlayer) => {
    const normalized = { ...p, displayName: p.displayName || UNKNOWN_USER };
    localStorage.setItem(STORAGE_KEY, JSON.stringify(normalized));
    setPlayerState(normalized);
    window.dispatchEvent(new Event(SYNC_EVENT));
  }, []);

  const clearPlayer = useCallback(() => {
    localStorage.removeItem(STORAGE_KEY);
    setPlayerState(null);
    window.dispatchEvent(new Event(SYNC_EVENT));
  }, []);

  useEffect(() => {
    const sync = () => setPlayerState(loadPlayer());
    window.addEventListener(SYNC_EVENT, sync);
    window.addEventListener('storage', (e) => {
      /* v8 ignore start */
      if (e.key === STORAGE_KEY) sync();
      /* v8 ignore stop */
    });
    return () => {
      window.removeEventListener(SYNC_EVENT, sync);
      window.removeEventListener('storage', sync);
    };
  }, []);

  return useMemo(() => ({ player, setPlayer, clearPlayer }), [player, setPlayer, clearPlayer]);
}
