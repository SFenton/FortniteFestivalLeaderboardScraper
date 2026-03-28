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

function getCachedSongs(): SongsResponse | undefined {
  try {
    const raw = localStorage.getItem(SONGS_CACHE_KEY);
    if (!raw) return undefined;
    return (JSON.parse(raw) as { data: SongsResponse }).data;
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

  const { data, isLoading, error } = useQuery({
    queryKey: queryKeys.songs(),
    queryFn: () => api.getSongs(),
    initialData: getCachedSongs,   // instant render from localStorage while ETag revalidation runs
    staleTime: 5 * 60 * 1000,     // 5 min — revalidation is cheap (304 via ETag)
  });

  const refresh = useCallback(async () => {
    await qc.invalidateQueries({ queryKey: queryKeys.songs() });
  }, [qc]);

  const value = useMemo<FestivalContextValue>(() => ({
    state: {
      songs: data?.songs ?? [],
      currentSeason: data?.currentSeason ?? 0,
      isLoading,
      error: error ? (error instanceof Error ? error.message : 'Failed to load songs') : null,
    },
    actions: { refresh },
  }), [data, isLoading, error, refresh]);

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
