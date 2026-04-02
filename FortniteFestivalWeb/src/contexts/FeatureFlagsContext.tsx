import { createContext, useContext, useMemo, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';

/* ── Types ── */

export type FeatureFlags = {
  shop: boolean;
  rivals: boolean;
  compete: boolean;
  leaderboards: boolean;
  firstRun: boolean;
  difficulty: boolean;
};

const ALL_ON: FeatureFlags = { shop: true, rivals: true, compete: true, leaderboards: true, firstRun: true, difficulty: true };
const ALL_OFF: FeatureFlags = { shop: false, rivals: false, compete: false, leaderboards: false, firstRun: false, difficulty: false };

/* ── Context ── */

const FeatureFlagsContext = createContext<FeatureFlags | null>(null);

/* ── Fetcher ── */

async function fetchFeatureFlags(): Promise<FeatureFlags> {
  const res = await fetch('/api/features');
  if (!res.ok) throw new Error(`features ${res.status}`);
  return res.json() as Promise<FeatureFlags>;
}

/* ── Provider ── */

export function FeatureFlagsProvider({ children }: { children: ReactNode }) {
  const isDev = import.meta.env.DEV;

  const { data } = useQuery<FeatureFlags>({
    queryKey: ['features'],
    queryFn: fetchFeatureFlags,
    staleTime: 30 * 60 * 1000,
    gcTime: 60 * 60 * 1000,
    retry: 1,
    refetchOnWindowFocus: false,
    enabled: !isDev,
  });

  const flags = useMemo<FeatureFlags>(() => {
    if (isDev) {
      try {
        const raw = localStorage.getItem('fst:featureFlagOverrides');
        if (raw) return { ...ALL_ON, ...JSON.parse(raw) as Partial<FeatureFlags> };
      } catch { /* ignore malformed JSON */ }
      return ALL_ON;
    }
    return data ?? ALL_OFF;
  }, [isDev, data]);

  return (
    <FeatureFlagsContext.Provider value={flags}>
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
  return ctx;
}
