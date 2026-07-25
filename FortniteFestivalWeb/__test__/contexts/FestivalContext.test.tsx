import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { FestivalProvider, useFestival } from '../../src/contexts/FestivalContext';
import {
  clearSongsCache,
  PUBLIC_CATALOG_CACHE_SCOPE,
  SONGS_CACHE_KEY,
  SONGS_CACHE_VERSION,
} from '../../src/api/songsCache';

vi.mock('../../src/api/client', () => ({
  api: {
    getSongs: vi.fn(),
  },
}));

import { api } from '../../src/api/client';
const mockGetSongs = api.getSongs as ReturnType<typeof vi.fn>;

let testQc: QueryClient;

function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={testQc}><FestivalProvider>{children}</FestivalProvider></QueryClientProvider>;
}

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  clearSongsCache();
  testQc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
});

describe('FestivalContext', () => {
  it('starts in loading state', () => {
    mockGetSongs.mockReturnValue(new Promise(() => {})); // never resolves
    const { result } = renderHook(() => useFestival(), { wrapper });
    expect(result.current.state.isLoading).toBe(true);
    expect(result.current.state.songs).toEqual([]);
  });

  it('loads songs on mount', async () => {
    mockGetSongs.mockResolvedValue({ songs: [{ songId: 's1', title: 'Song 1', artist: 'A' }], currentSeason: 5 });
    const { result } = renderHook(() => useFestival(), { wrapper });
    await waitFor(() => expect(result.current.state.isLoading).toBe(false));
    expect(result.current.state.songs).toHaveLength(1);
    expect(result.current.state.currentSeason).toBe(5);
    expect(result.current.state.error).toBeNull();
  });

  it('sets error on fetch failure', async () => {
    mockGetSongs.mockRejectedValue(new Error('Network fail'));
    const { result } = renderHook(() => useFestival(), { wrapper });
    await waitFor(() => expect(result.current.state.isLoading).toBe(false));
    expect(result.current.state.error).toBe('Network fail');
    expect(result.current.state.songs).toEqual([]);
  });

  it('parses one validated placeholder and preserves it when the refetch fails', async () => {
    const cached = {
      count: 1,
      currentSeason: 4,
      songs: [{ songId: 'cached', title: 'Cached', artist: 'Artist' }],
    };
    localStorage.setItem(SONGS_CACHE_KEY, JSON.stringify({
      version: SONGS_CACHE_VERSION,
      scope: PUBLIC_CATALOG_CACHE_SCOPE,
      data: cached,
      etag: '"cached"',
    }));
    const parse = vi.spyOn(JSON, 'parse');
    mockGetSongs.mockRejectedValue(new Error('Offline'));

    const { result } = renderHook(() => useFestival(), { wrapper });

    expect(result.current.state.songs).toEqual(cached.songs);
    expect(result.current.state.currentSeason).toBe(4);
    await waitFor(() => expect(result.current.state.error).toBe('Offline'));
    expect(result.current.state.songs).toEqual(cached.songs);
    expect(parse).toHaveBeenCalledTimes(1);
  });

  it('keeps local placeholder and network normalization in parity', async () => {
    const response = {
      count: 1,
      currentSeason: 6,
      songs: [{ songId: 'same', title: 'Same Song', artist: 'Artist' }],
    };
    localStorage.setItem(SONGS_CACHE_KEY, JSON.stringify({
      version: SONGS_CACHE_VERSION,
      scope: PUBLIC_CATALOG_CACHE_SCOPE,
      data: response,
      etag: '"same"',
    }));
    mockGetSongs.mockResolvedValue(response);

    const { result } = renderHook(() => useFestival(), { wrapper });

    expect(result.current.state.songs).toEqual(response.songs);
    await waitFor(() => expect(mockGetSongs).toHaveBeenCalledTimes(1));
    expect(result.current.state.songs).toEqual(response.songs);
    expect(result.current.state.currentSeason).toBe(response.currentSeason);
  });

  it('refresh reloads data', async () => {
    mockGetSongs.mockResolvedValueOnce({ songs: [], currentSeason: 1 });
    const { result } = renderHook(() => useFestival(), { wrapper });
    await waitFor(() => expect(result.current.state.isLoading).toBe(false));

    mockGetSongs.mockResolvedValueOnce({ songs: [{ songId: 's2', title: 'X', artist: 'Y' }], currentSeason: 2 });
    await act(async () => { await result.current.actions.refresh(); });
    await waitFor(() => expect(result.current.state.songs).toHaveLength(1));
    expect(result.current.state.currentSeason).toBe(2);
  });

  it('throws when used outside provider', () => {
    expect(() => renderHook(() => useFestival())).toThrow('useFestival must be used within a FestivalProvider');
  });

  it('shows fallback message for non-Error rejection', async () => {
    mockGetSongs.mockRejectedValue('string-error');
    const { result } = renderHook(() => useFestival(), { wrapper });
    await waitFor(() => expect(result.current.state.isLoading).toBe(false));
    expect(result.current.state.error).toBe('Failed to load songs');
  });
});
