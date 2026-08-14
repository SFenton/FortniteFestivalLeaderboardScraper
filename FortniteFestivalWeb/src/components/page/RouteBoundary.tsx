import type { ReactNode } from 'react';
import { useLocation } from 'react-router-dom';
import ErrorBoundary from './ErrorBoundary';
import RouteErrorFallback from './RouteErrorFallback';

export default function RouteBoundary({ children }: { children: ReactNode }) {
  const { pathname } = useLocation();
  return (
    <ErrorBoundary key={pathname} fallback={<RouteErrorFallback />}>
      {children}
    </ErrorBoundary>
  );
}
