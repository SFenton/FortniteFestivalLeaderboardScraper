import { describe, it, expect, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import React, { type ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useAvailableSeasons } from '../../../src/hooks/data/useAvailableSeasons';
import { FestivalProvider } from '../../../src/contexts/FestivalContext';

vi.mock('../../../src/api/client', () => ({
  api: {
    getSongs: vi.fn().mockResolvedValue({ songs: [], currentSeason: 5 }),
  },
}));

function wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return React.createElement(QueryClientProvider, { client: qc },
    React.createElement(FestivalProvider, null, children)
  );
}

describe('useAvailableSeasons', () => {
  it('returns array 1..currentSeason', async () => {
    const { result, rerender } = renderHook(() => useAvailableSeasons(), { wrapper });
    // Wait for the FestivalProvider to load songs / currentSeason
    await vi.waitFor(() => {
      rerender();
      expect(result.current.length).toBeGreaterThan(0);
    });
    expect(result.current).toEqual([1, 2, 3, 4, 5]);
  });
});
