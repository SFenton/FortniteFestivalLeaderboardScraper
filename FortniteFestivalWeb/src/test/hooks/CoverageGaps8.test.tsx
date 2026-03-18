import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, renderHook, waitFor } from '@testing-library/react';
import React from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/* ---------- AlbumArt ---------- */
import AlbumArt from '../../components/songs/metadata/AlbumArt';

describe('AlbumArt branch coverage', () => {
  it('renders with priority — eager + high fetchPriority', () => {
    const { container } = render(<AlbumArt src="test.jpg" size={44} priority />);
    const img = container.querySelector('img')!;
    expect(img.getAttribute('loading')).toBe('eager');
    expect(img.getAttribute('fetchpriority')).toBe('high');
  });
});

/* ---------- FestivalContext non-Error rejection ---------- */
vi.mock('../../api/client', () => ({
  api: {
    getSongs: vi.fn(),
    getPlayerData: vi.fn().mockResolvedValue(null),
  },
}));

import { api } from '../../api/client';
import { FestivalProvider, useFestival } from '../../contexts/FestivalContext';
const mockGetSongs = api.getSongs as ReturnType<typeof vi.fn>;

describe('FestivalContext branch coverage', () => {
  beforeEach(() => vi.clearAllMocks());

  it('shows fallback message for non-Error rejection', async () => {
    mockGetSongs.mockRejectedValue('string-error');
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
    const wrapper = ({ children }: { children: React.ReactNode }) => (
      <QueryClientProvider client={qc}>
        <FestivalProvider>{children}</FestivalProvider>
      </QueryClientProvider>
    );
    const { result } = renderHook(() => useFestival(), { wrapper });
    await waitFor(() => expect(result.current.state.isLoading).toBe(false));
    expect(result.current.state.error).toBe('Failed to load songs');
  });
});

/* ---------- useTrackedPlayer: no accountId branch ---------- */
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';

describe('useTrackedPlayer branch coverage', () => {
  beforeEach(() => localStorage.clear());

  it('returns null when stored object has no accountId', () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ foo: 'bar' }));
    const { result } = renderHook(() => useTrackedPlayer());
    expect(result.current.player).toBeNull();
  });

  it('returns null when stored value is JSON null', () => {
    localStorage.setItem('fst:trackedPlayer', 'null');
    const { result } = renderHook(() => useTrackedPlayer());
    expect(result.current.player).toBeNull();
  });
});

/* ---------- useVersions: defined globals ---------- */
describe('useVersions branch coverage', () => {
  it('uses defined __APP_VERSION__ when available', async () => {
    // The module is evaluated at import time with fallback.
    // To test the defined branch, we reset modules and define globals.
    vi.resetModules();
    (globalThis as any).__APP_VERSION__ = '2.5.0';
    (globalThis as any).__CORE_VERSION__ = '1.3.0';
    try {
      const mod = await import('../../hooks/data/useVersions');
      expect(mod.APP_VERSION).toBe('2.5.0');
      expect(mod.CORE_VERSION).toBe('1.3.0');
    } finally {
      delete (globalThis as any).__APP_VERSION__;
      delete (globalThis as any).__CORE_VERSION__;
    }
  });
});
