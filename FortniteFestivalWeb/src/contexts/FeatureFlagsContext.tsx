import { createContext, useContext, useMemo, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { FeatureFlagsResponse } from '@festival/core/api/serverTypes';
import { api } from '../api/client';
import { queryKeys } from '../api/queryKeys';

/* ── Types ── */

export type FeatureFlags = {
  compete: boolean;
  leaderboards: boolean;
  difficulty: boolean;
  playerBands: boolean;
  experimentalRanks: boolean;
  appManual: boolean;
};

type FeatureFlagsContextValue = {
  flags: FeatureFlags;
  resolved: boolean;
};

const DEFAULT_FLAGS: FeatureFlags = {
  compete: true,
  leaderboards: true,
  difficulty: true,
  playerBands: true,
  experimentalRanks: true,
  appManual: false,
};

function mergeFeatureFlags(response: FeatureFlagsResponse | undefined): FeatureFlags {
  return {
    ...DEFAULT_FLAGS,
    ...response,
    appManual: response?.appManual === true,
  };
}

/* ── Context ── */

const FeatureFlagsContext = createContext<FeatureFlagsContextValue | null>(null);

/* ── Provider ── */

export function FeatureFlagsProvider({ children }: { children: ReactNode }) {
  const query = useQuery({
    queryKey: queryKeys.features(),
    queryFn: ({ signal }) => api.getFeatures({ signal }),
    retry: false,
    staleTime: 5 * 60 * 1000,
  });
  const value = useMemo<FeatureFlagsContextValue>(() => ({
    flags: mergeFeatureFlags(query.data),
    resolved: query.status !== 'pending',
  }), [query.data, query.status]);

  return (
    <FeatureFlagsContext.Provider value={value}>
      {children}
    </FeatureFlagsContext.Provider>
  );
}

/* ── Hook ── */

export function useFeatureFlags(): FeatureFlags {
  const ctx = useContext(FeatureFlagsContext);
  if (!ctx) {
    throw new Error('useFeatureFlags must be used within a FeatureFlagsProvider');
  }
  return ctx.flags;
}

export function useFeatureFlagsState(): FeatureFlagsContextValue {
  const ctx = useContext(FeatureFlagsContext);
  if (!ctx) {
    throw new Error('useFeatureFlagsState must be used within a FeatureFlagsProvider');
  }
  return ctx;
}
