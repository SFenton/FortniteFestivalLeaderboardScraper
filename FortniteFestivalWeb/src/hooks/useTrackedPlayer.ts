import { useState, useCallback, useEffect } from 'react';

const STORAGE_KEY = 'fst:trackedPlayer';
const SYNC_EVENT = 'fst:trackedPlayerChanged';

export type TrackedPlayer = {
  accountId: string;
  displayName: string;
};

function loadPlayer(): TrackedPlayer | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (parsed?.accountId && parsed?.displayName) return parsed;
    return null;
  } catch {
    return null;
  }
}

export function useTrackedPlayer() {
  const [player, setPlayerState] = useState<TrackedPlayer | null>(loadPlayer);

  const setPlayer = useCallback((p: TrackedPlayer) => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(p));
    setPlayerState(p);
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
      if (e.key === STORAGE_KEY) sync();
    });
    return () => {
      window.removeEventListener(SYNC_EVENT, sync);
      window.removeEventListener('storage', sync);
    };
  }, []);

  return { player, setPlayer, clearPlayer };
}
