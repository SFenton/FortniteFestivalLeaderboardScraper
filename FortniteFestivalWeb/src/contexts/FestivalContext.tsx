import {
  createContext,
  useContext,
  useCallback,
  useMemo,
  type ReactNode,
} from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { IncomingSongsResponse, ServerSong as Song, SongsResponse } from '@festival/core/api';
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

const EMPTY_SONGS: Song[] = [];

/**
 * Appends ingested-but-unpublished songs to the published catalog. Published
 * entries always win, so a song's metadata never changes before publication.
 */
export function mergeIncomingSongs(published: Song[], incoming: Song[] | undefined): Song[] {
  if (!incoming || incoming.length === 0) return published;
  const publishedIds = new Set(published.map(song => song.songId));
  const additions = incoming
    .filter(song => !publishedIds.has(song.songId))
    .map(song => ({ ...song, awaitingPublication: true }));
  return additions.length === 0 ? published : [...published, ...additions];
}

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

  // Songs ingested after the current publication. Failures never block the
  // published catalog; they only defer showing the new songs.
  const { data: incoming } = useQuery<IncomingSongsResponse>({
    queryKey: queryKeys.incomingSongs(),
    queryFn: ({ signal }) => api.getIncomingSongs({ signal }),
    staleTime: 60 * 1000,
    refetchInterval: 5 * 60 * 1000,
    retry: 1,
  });

  const refresh = useCallback(async () => {
    await Promise.all([
      qc.invalidateQueries({ queryKey: queryKeys.songs() }),
      qc.invalidateQueries({ queryKey: queryKeys.incomingSongs() }),
    ]);
  }, [qc]);

  const publishedSongs = data?.songs ?? cachedResponse?.songs;
  const songs = useMemo(
    () => mergeIncomingSongs(publishedSongs ?? EMPTY_SONGS, incoming?.songs),
    [publishedSongs, incoming?.songs],
  );

  const value = useMemo<FestivalContextValue>(() => ({
    state: {
      songs,
      currentSeason: data?.currentSeason ?? cachedResponse?.currentSeason ?? 0,
      isLoading,
      error: error ? (error instanceof Error ? error.message : 'Failed to load songs') : null,
    },
    actions: { refresh },
  }), [songs, data, cachedResponse, isLoading, error, refresh]);

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
