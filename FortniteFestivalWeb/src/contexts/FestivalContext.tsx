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
import { readSongsCache } from '../api/songsCache';

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
  const cachedResponse = useMemo(() => readSongsCache()?.data, []);

  const { data, isLoading, error } = useQuery<SongsResponse>({
    queryKey: queryKeys.songs(),
    queryFn: ({ signal }) => api.getSongs({ signal }),
    placeholderData: cachedResponse,
    staleTime: 5 * 60 * 1000,        // 5 min — revalidation is cheap (304 via ETag)
    gcTime: 10 * 60 * 1000,
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
