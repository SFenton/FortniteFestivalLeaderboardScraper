import { createContext, useContext, type ReactNode } from 'react';

/* ── Types ── */

export type FeatureFlags = {
  compete: boolean;
  leaderboards: boolean;
  difficulty: boolean;
  playerBands: boolean;
  experimentalRanks: boolean;
};

const ALL_ON: FeatureFlags = { compete: true, leaderboards: true, difficulty: true, playerBands: true, experimentalRanks: true };

/* ── Context ── */

const FeatureFlagsContext = createContext<FeatureFlags | null>(null);

/* ── Provider ── */

export function FeatureFlagsProvider({ children }: { children: ReactNode }) {
  return (
    <FeatureFlagsContext.Provider value={ALL_ON}>
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
