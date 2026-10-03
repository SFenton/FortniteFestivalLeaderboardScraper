import type { ReactNode } from 'react';
import { useLocation } from 'react-router-dom';
import { getRoutePageOwner } from '../../routes';
import ErrorBoundary from './ErrorBoundary';
import RouteErrorFallback from './RouteErrorFallback';

export default function RouteBoundary({ children }: { children: ReactNode }) {
  const { pathname } = useLocation();
  return (
    <ErrorBoundary key={getRoutePageOwner(pathname)} fallback={<RouteErrorFallback />}>
      {children}
    </ErrorBoundary>
  );
}
