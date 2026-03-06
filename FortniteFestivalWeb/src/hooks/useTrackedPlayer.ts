import { useState, useCallback } from 'react';

const STORAGE_KEY = 'fst:trackedPlayer';

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
  }, []);

  const clearPlayer = useCallback(() => {
    localStorage.removeItem(STORAGE_KEY);
    setPlayerState(null);
  }, []);

  return { player, setPlayer, clearPlayer };
}
