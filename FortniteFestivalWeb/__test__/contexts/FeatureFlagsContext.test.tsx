import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';

// Dynamic flag values that individual tests can override
const flagValues = vi.hoisted(() => ({
  rivals: true, compete: true, leaderboards: true, firstRun: true, playerBands: true,
}));

// Track whether fetch should fail
const fetchShouldFail = vi.hoisted(() => ({ value: false }));

beforeEach(() => {
  flagValues.rivals = true;
  flagValues.compete = true;
  flagValues.leaderboards = true;
  flagValues.firstRun = true;
  flagValues.playerBands = true;
  fetchShouldFail.value = false;

  vi.stubGlobal('fetch', vi.fn(() => {
    if (fetchShouldFail.value) {
      return Promise.resolve({ ok: false, status: 500, statusText: 'Internal Server Error' });
    }
    return Promise.resolve({
      ok: true,
      json: () => Promise.resolve({ ...flagValues }),
    });
  }));
});

afterEach(() => {
  vi.restoreAllMocks();
});

import { FeatureFlagsProvider, useFeatureFlags } from '../../src/contexts/FeatureFlagsContext';

function makeWrapper() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={qc}>
        <FeatureFlagsProvider>{children}</FeatureFlagsProvider>
      </QueryClientProvider>
    );
  };
}

describe('FeatureFlagsContext', () => {
  describe('dev mode (default in vitest)', () => {
    it('returns all flags ON without fetching', () => {
      const wrapper = makeWrapper();
      const { result } = renderHook(() => useFeatureFlags(), { wrapper });

      expect(result.current.rivals).toBe(true);
      expect(result.current.compete).toBe(true);
      expect(result.current.leaderboards).toBe(true);
      expect(result.current.firstRun).toBe(true);
      expect(result.current.playerBands).toBe(true);
      expect(fetch).not.toHaveBeenCalled();
    });
  });

  describe('prod mode', () => {
    beforeEach(() => {
      (import.meta.env as Record<string, unknown>).DEV = false;
    });
    afterEach(() => {
      (import.meta.env as Record<string, unknown>).DEV = true;
    });

    it('fetches flags from /api/features', async () => {
      const wrapper = makeWrapper();
      const { result } = renderHook(() => useFeatureFlags(), { wrapper });

      await waitFor(() => {
        expect(result.current.rivals).toBe(true);
      });

      expect(result.current.compete).toBe(true);
      expect(result.current.leaderboards).toBe(true);
      expect(result.current.firstRun).toBe(true);
      expect(result.current.playerBands).toBe(true);
      expect(fetch).toHaveBeenCalledWith('/api/features');
    });

    it('returns partial flags when server sends partial response', async () => {
      flagValues.leaderboards = false;

      const wrapper = makeWrapper();
      const { result } = renderHook(() => useFeatureFlags(), { wrapper });

      await waitFor(() => {
        expect(result.current.rivals).toBe(true);
      });

      expect(result.current.compete).toBe(true);
      expect(result.current.leaderboards).toBe(false);
      expect(result.current.playerBands).toBe(true);
    });

    it('returns all flags OFF when fetch fails', async () => {
      fetchShouldFail.value = true;

      const wrapper = makeWrapper();
      const { result } = renderHook(() => useFeatureFlags(), { wrapper });

      await waitFor(() => {
        expect(fetch).toHaveBeenCalled();
      });

      expect(result.current.rivals).toBe(false);
      expect(result.current.compete).toBe(false);
      expect(result.current.leaderboards).toBe(false);
      expect(result.current.firstRun).toBe(false);
      expect(result.current.playerBands).toBe(false);
    });
  });

  it('throws when used outside provider', () => {
    expect(() => {
      renderHook(() => useFeatureFlags());
    }).toThrow('useFeatureFlags must be used within a FeatureFlagsProvider');
  });
});
