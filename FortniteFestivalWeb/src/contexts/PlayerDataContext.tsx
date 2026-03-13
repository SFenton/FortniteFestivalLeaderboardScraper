import {
  createContext,
  useContext,
  useState,
  useEffect,
  useCallback,
  useRef,
  useMemo,
  type ReactNode,
} from 'react';
import { api } from '../api/client';
import { useSyncStatus, type SyncPhase } from '../hooks/useSyncStatus';
import type { PlayerResponse } from '../models';

type PlayerDataContextValue = {
  playerData: PlayerResponse | null;
  playerLoading: boolean;
  playerError: string | null;
  refreshPlayer: () => Promise<void>;
  isSyncing: boolean;
  syncPhase: SyncPhase;
  backfillProgress: number;
  historyProgress: number;
};

const PlayerDataContext = createContext<PlayerDataContextValue | null>(null);

export function PlayerDataProvider({
  accountId,
  children,
}: {
  accountId: string | undefined;
  children: ReactNode;
}) {
  const [data, setData] = useState<PlayerResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const cachedIdRef = useRef<string | undefined>(undefined);
  const hasDataRef = useRef(false);

  const { isSyncing, phase, backfillProgress, historyProgress, justCompleted, clearCompleted } =
    useSyncStatus(accountId);

  const fetchPlayer = useCallback(async (id: string, isRefresh: boolean) => {
    if (!isRefresh) setLoading(true);
    setError(null);
    try {
      const res = await api.getPlayer(id);
      setData(res);
      hasDataRef.current = true;
    } catch (e) {
      if (!isRefresh) {
        setError(e instanceof Error ? e.message : 'Failed to load player');
      }
    } finally {
      setLoading(false);
    }
  }, []);

  // Fetch when accountId changes
  useEffect(() => {
    if (!accountId) {
      setData(null);
      setLoading(false);
      setError(null);
      hasDataRef.current = false;
      cachedIdRef.current = undefined;
      return;
    }
    if (accountId !== cachedIdRef.current) {
      hasDataRef.current = false;
      cachedIdRef.current = accountId;
      void fetchPlayer(accountId, false);
    }
  }, [accountId, fetchPlayer]);

  // Auto-reload when sync completes
  useEffect(() => {
    if (justCompleted && accountId) {
      clearCompleted();
      void fetchPlayer(accountId, hasDataRef.current);
    }
  }, [justCompleted, clearCompleted, accountId, fetchPlayer]);

  // Exposed refresh — silently reloads without clearing existing data
  const refreshPlayer = useCallback(async () => {
    if (accountId) {
      await fetchPlayer(accountId, hasDataRef.current);
    }
  }, [accountId, fetchPlayer]);

  const value = useMemo<PlayerDataContextValue>(() => ({
    playerData: data,
    playerLoading: loading,
    playerError: error,
    refreshPlayer,
    isSyncing,
    syncPhase: phase,
    backfillProgress,
    historyProgress,
  }), [data, loading, error, refreshPlayer, isSyncing, phase, backfillProgress, historyProgress]);

  return (
    <PlayerDataContext.Provider value={value}>
      {children}
    </PlayerDataContext.Provider>
  );
}

export function usePlayerData(): PlayerDataContextValue {
  const ctx = useContext(PlayerDataContext);
  if (!ctx) {
    throw new Error('usePlayerData must be used within a PlayerDataProvider');
  }
  return ctx;
}
