import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { FestivalProvider, useFestival } from '../../src/contexts/FestivalContext';

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
