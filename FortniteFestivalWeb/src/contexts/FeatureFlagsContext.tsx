import { createContext, useContext, useMemo, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import { queryKeys } from '../api/queryKeys';

export type FeatureFlags = {
  appManual: boolean;
};

type FeatureFlagsContextValue = {
  flags: FeatureFlags;
  resolved: boolean;
};

const DEFAULT_FLAGS: FeatureFlags = {
  appManual: false,
};

const FeatureFlagsContext = createContext<FeatureFlagsContextValue | null>(null);

export function FeatureFlagsProvider({ children }: { children: ReactNode }) {
  const query = useQuery({
    queryKey: queryKeys.features(),
    queryFn: ({ signal }) => api.getFeatures({ signal }),
    retry: false,
    staleTime: 5 * 60 * 1000,
  });
  const value = useMemo<FeatureFlagsContextValue>(() => ({
    flags: {
      ...DEFAULT_FLAGS,
      appManual: query.data?.appManual === true,
    },
    resolved: query.status !== 'pending',
  }), [query.data, query.status]);

  return (
    <FeatureFlagsContext.Provider value={value}>
      {children}
    </FeatureFlagsContext.Provider>
  );
}

export function useFeatureFlags(): FeatureFlags {
  const context = useContext(FeatureFlagsContext);
  if (!context) {
    throw new Error('useFeatureFlags must be used within a FeatureFlagsProvider');
  }
  return context.flags;
}

export function useFeatureFlagsState(): FeatureFlagsContextValue {
  const context = useContext(FeatureFlagsContext);
  if (!context) {
    throw new Error('useFeatureFlagsState must be used within a FeatureFlagsProvider');
  }
  return context;
}
