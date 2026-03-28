import { Navigate } from 'react-router-dom';
import { useFeatureFlags, type FeatureFlags } from '../../contexts/FeatureFlagsContext';
import { Routes } from '../../routes';
import type { ReactNode } from 'react';

type FlagKey = keyof FeatureFlags;

/**
 * Renders children only when the given feature flag is enabled.
 * Redirects to the Songs page otherwise.
 */
export default function FeatureGate({ flag, children }: { flag: FlagKey; children: ReactNode }) {
  const flags = useFeatureFlags();
  if (!flags[flag]) return <Navigate to={Routes.songs} replace />;
  return <>{children}</>;
}
