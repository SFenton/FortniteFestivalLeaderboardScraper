import {
  createContext,
  useContext,
  useCallback,
  useMemo,
  type ReactNode,
} from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { ServerSong as Song, SongsResponse } from '@festival/core/api/serverTypes';
import { api } from '../api/client';
import { queryKeys } from '../api/queryKeys';

const SONGS_CACHE_KEY = 'fst_songs_cache';
/** Must match SONGS_CACHE_VERSION in api/client.ts */
const SONGS_CACHE_VERSION = 2;

function getCachedSongs(): SongsResponse | undefined {
  try {
    const raw = localStorage.getItem(SONGS_CACHE_KEY);
    if (!raw) return undefined;
    const parsed = JSON.parse(raw) as { data: SongsResponse; v?: number };
    // Reject stale cache versions (shape may lack new fields like maxScores)
    if ((parsed.v ?? 0) < SONGS_CACHE_VERSION) return undefined;
    return parsed.data;
  } catch {
    return undefined;
  }
}

type FestivalState = {
  songs: Song[];
  currentSeason: number;
  isLoading: boolean;
  error: string | null;
};

type FestivalActions = {
  refresh: () => Promise<void>;
};

type FestivalContextValue = {
  state: FestivalState;
  actions: FestivalActions;
};

export const FestivalContext = createContext<FestivalContextValue | null>(null);

export function FestivalProvider({ children }: { children: ReactNode }) {
  const qc = useQueryClient();
  const cachedResponse = useMemo(getCachedSongs, []);

  const { data, isLoading, error } = useQuery({
    queryKey: queryKeys.songs(),
    queryFn: () => api.getSongs(),
    placeholderData: getCachedSongs,  // instant render from localStorage; always refetches in background
    staleTime: 5 * 60 * 1000,        // 5 min — revalidation is cheap (304 via ETag)
  });

  const refresh = useCallback(async () => {
    await qc.invalidateQueries({ queryKey: queryKeys.songs() });
  }, [qc]);

  const value = useMemo<FestivalContextValue>(() => ({
    state: {
      songs: data?.songs ?? cachedResponse?.songs ?? [],
      currentSeason: data?.currentSeason ?? cachedResponse?.currentSeason ?? 0,
      isLoading,
      error: error ? (error instanceof Error ? error.message : 'Failed to load songs') : null,
    },
    actions: { refresh },
  }), [data, cachedResponse, isLoading, error, refresh]);

  return (
    <FestivalContext.Provider value={value}>
      {children}
    </FestivalContext.Provider>
  );
}

export function useFestival(): FestivalContextValue {
  const ctx = useContext(FestivalContext);
  if (!ctx) {
    throw new Error('useFestival must be used within a FestivalProvider');
  }
  return ctx;
}
