import { createContext, useContext, useMemo, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';

/* ── Types ── */

export type FeatureFlags = {
  shop: boolean;
  rivals: boolean;
  compete: boolean;
  leaderboards: boolean;
  firstRun: boolean;
};

const ALL_ON: FeatureFlags = { shop: true, rivals: true, compete: true, leaderboards: true, firstRun: true };
const ALL_OFF: FeatureFlags = { shop: false, rivals: false, compete: false, leaderboards: false, firstRun: false };

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
  const { data } = useQuery<FeatureFlags>({
    queryKey: ['features'],
    queryFn: fetchFeatureFlags,
    staleTime: 30 * 60 * 1000,
    gcTime: 60 * 60 * 1000,
    retry: 1,
    refetchOnWindowFocus: false,
  });

  const flags = useMemo<FeatureFlags>(() => {
    if (data) return data;
    // Dev fallback: all features ON so developers don't need a running backend
    return import.meta.env.DEV ? ALL_ON : ALL_OFF;
  }, [data]);

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
