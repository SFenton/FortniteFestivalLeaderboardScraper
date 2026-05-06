import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn());
  localStorage.clear();
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
  it('returns all flags ON without fetching', () => {
    const wrapper = makeWrapper();
    const { result } = renderHook(() => useFeatureFlags(), { wrapper });

    expect(result.current.compete).toBe(true);
    expect(result.current.leaderboards).toBe(true);
    expect(result.current.difficulty).toBe(true);
    expect(result.current.playerBands).toBe(true);
    expect(result.current.experimentalRanks).toBe(true);
    expect(fetch).not.toHaveBeenCalled();
  });

  it('ignores legacy local overrides', () => {
    localStorage.setItem('fst:featureFlagOverrides', JSON.stringify({ playerBands: false, experimentalRanks: false }));

    const wrapper = makeWrapper();
    const { result } = renderHook(() => useFeatureFlags(), { wrapper });

    expect(result.current.playerBands).toBe(true);
    expect(result.current.experimentalRanks).toBe(true);
  });

  it('throws when used outside provider', () => {
    expect(() => {
      renderHook(() => useFeatureFlags());
    }).toThrow('useFeatureFlags must be used within a FeatureFlagsProvider');
  });
});
