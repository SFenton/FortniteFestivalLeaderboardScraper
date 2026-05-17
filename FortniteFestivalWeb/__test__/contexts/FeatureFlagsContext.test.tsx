import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn());
  localStorage.clear();
});

afterEach(() => {
  vi.restoreAllMocks();
});

import { FeatureFlagsProvider, useFeatureFlags, useFeatureFlagsState } from '../../src/contexts/FeatureFlagsContext';

function mockFeatureResponse(data: Record<string, unknown>) {
  (fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
    ok: true,
    status: 200,
    json: () => Promise.resolve(data),
    headers: new Headers(),
  });
}

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
  it('keeps existing flags on and App Manual off while loading', () => {
    (fetch as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(() => {}));
    const wrapper = makeWrapper();
    const { result } = renderHook(() => useFeatureFlags(), { wrapper });

    expect(result.current.compete).toBe(true);
    expect(result.current.leaderboards).toBe(true);
    expect(result.current.difficulty).toBe(true);
    expect(result.current.playerBands).toBe(true);
    expect(result.current.experimentalRanks).toBe(true);
    expect(result.current.appManual).toBe(false);
  });

  it('merges feature flags from the service', async () => {
    mockFeatureResponse({ compete: true, leaderboards: false, difficulty: false, playerBands: true, experimentalRanks: false, appManual: true });
    const wrapper = makeWrapper();
    const { result } = renderHook(() => useFeatureFlagsState(), { wrapper });

    await waitFor(() => expect(result.current.resolved).toBe(true));

    expect(result.current.flags.leaderboards).toBe(false);
    expect(result.current.flags.appManual).toBe(true);
    expect(fetch).toHaveBeenCalledWith('/api/features', { headers: {}, signal: expect.any(AbortSignal) });
  });

  it('keeps App Manual disabled when the service omits the flag', async () => {
    mockFeatureResponse({ compete: true, leaderboards: true, difficulty: true, playerBands: true, experimentalRanks: true });
    const wrapper = makeWrapper();
    const { result } = renderHook(() => useFeatureFlagsState(), { wrapper });

    await waitFor(() => expect(result.current.resolved).toBe(true));

    expect(result.current.flags.appManual).toBe(false);
  });

  it('fails closed for App Manual when feature fetch fails', async () => {
    (fetch as ReturnType<typeof vi.fn>).mockResolvedValue({ ok: false, status: 500, statusText: 'Internal Server Error' });
    const wrapper = makeWrapper();
    const { result } = renderHook(() => useFeatureFlagsState(), { wrapper });

    await waitFor(() => expect(result.current.resolved).toBe(true));

    expect(result.current.flags.appManual).toBe(false);
  });

  it('ignores legacy local overrides', () => {
    (fetch as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(() => {}));
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
