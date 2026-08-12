import { lazy, StrictMode, Suspense } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import App from './App';
import { queryClient } from './api/queryClient';
import BackendAvailabilityGate from './components/maintenance/BackendAvailabilityGate';
import PublicationBoundary from './contexts/PublicationBoundary';
import { applyScrollFadeTestMode } from './diagnostics/scrollFadeTestMode';
import { installStaleChunkRecovery } from './utils/staleChunkRecovery';
import { migrateDirectPathToHashRoute } from './utils/directRouteMigration';
import './i18n';
import './index.css';

const PwaIconCapture = lazy(() => import('./components/icons/PwaIconCapture'));
const ModalAccessibilityFixture = lazy(() => import('./diagnostics/ModalAccessibilityFixture'));

migrateDirectPathToHashRoute();
applyScrollFadeTestMode();
installStaleChunkRecovery();

const searchParams = new URLSearchParams(window.location.search);
const Root = searchParams.has('pwaIconCapture')
  ? PwaIconCapture
  : searchParams.has('modalA11yFixture')
    ? ModalAccessibilityFixture
    : App;
const showReactQueryDevtools = import.meta.env.DEV && import.meta.env.MODE !== 'e2e';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <PublicationBoundary>
        <BackendAvailabilityGate>
          <Suspense fallback={null}>
            <Root />
          </Suspense>
        </BackendAvailabilityGate>
      </PublicationBoundary>
      {showReactQueryDevtools && <ReactQueryDevtools initialIsOpen={false} />}
    </QueryClientProvider>
  </StrictMode>,
);
