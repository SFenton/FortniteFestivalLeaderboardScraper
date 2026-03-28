import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';

// Dynamic flag values that individual tests can override
const flagValues = vi.hoisted(() => ({
  shop: true, rivals: true, compete: true, leaderboards: true, firstRun: true,
}));

// Track whether fetch should fail
const fetchShouldFail = vi.hoisted(() => ({ value: false }));

beforeEach(() => {
  flagValues.shop = true;
  flagValues.rivals = true;
  flagValues.compete = true;
  flagValues.leaderboards = true;
  flagValues.firstRun = true;
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
  it('fetches flags from /api/features', async () => {
    const wrapper = makeWrapper();
    const { result } = renderHook(() => useFeatureFlags(), { wrapper });

    await waitFor(() => {
      expect(result.current.shop).toBe(true);
    });

    expect(result.current.rivals).toBe(true);
    expect(result.current.compete).toBe(true);
    expect(result.current.leaderboards).toBe(true);
    expect(result.current.firstRun).toBe(true);
    expect(fetch).toHaveBeenCalledWith('/api/features');
  });

  it('returns partial flags when server sends partial response', async () => {
    flagValues.shop = false;
    flagValues.leaderboards = false;

    const wrapper = makeWrapper();
    const { result } = renderHook(() => useFeatureFlags(), { wrapper });

    await waitFor(() => {
      expect(result.current.shop).toBe(false);
    });

    expect(result.current.rivals).toBe(true);
    expect(result.current.compete).toBe(true);
    expect(result.current.leaderboards).toBe(false);
  });

  it('returns dev defaults (all ON) when fetch fails in dev mode', async () => {
    fetchShouldFail.value = true;

    const wrapper = makeWrapper();
    const { result } = renderHook(() => useFeatureFlags(), { wrapper });

    // In vitest (dev mode), import.meta.env.DEV is true
    // So the fallback should be all ON
    await waitFor(() => {
      expect(fetch).toHaveBeenCalled();
    });

    expect(result.current.shop).toBe(true);
    expect(result.current.rivals).toBe(true);
    expect(result.current.compete).toBe(true);
    expect(result.current.leaderboards).toBe(true);
    expect(result.current.firstRun).toBe(true);
  });

  it('throws when used outside provider', () => {
    expect(() => {
      renderHook(() => useFeatureFlags());
    }).toThrow('useFeatureFlags must be used within a FeatureFlagsProvider');
  });
});
