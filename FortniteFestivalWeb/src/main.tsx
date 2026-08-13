import { lazy, StrictMode, Suspense } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import App from './App';
import ErrorBoundary from './components/page/ErrorBoundary';
import { queryClient } from './api/queryClient';
import BackendAvailabilityGate from './components/maintenance/BackendAvailabilityGate';
import PublicationBoundary from './contexts/PublicationBoundary';
import {
  DISABLE_SCROLL_FADE_QUERY_PARAM,
  DISABLE_SCROLL_FADE_STORAGE_KEY,
} from './diagnostics/scrollFadeTestModeBridge';
import { installStaleChunkRecovery } from './utils/staleChunkRecovery';
import { migrateDirectPathToHashRoute } from './utils/directRouteMigration';
import i18n from './i18n';
import './index.css';

const PwaIconCapture = lazy(() => import('./components/icons/PwaIconCapture'));
const ModalAccessibilityFixture = lazy(() => import('./diagnostics/ModalAccessibilityFixture'));

async function bootstrap() {
  migrateDirectPathToHashRoute();
  installStaleChunkRecovery();

  const searchParams = new URLSearchParams(window.location.search);
  const DiagnosticRoot = searchParams.has('pwaIconCapture')
    ? PwaIconCapture
    : searchParams.has('modalA11yFixture')
      ? ModalAccessibilityFixture
      : null;
  if (DiagnosticRoot) {
    createRoot(document.getElementById('root')!).render(
      <StrictMode>
        <ErrorBoundary>
          <Suspense fallback={null}>
            <DiagnosticRoot />
          </Suspense>
        </ErrorBoundary>
      </StrictMode>,
    );
    return;
  }

  if (shouldLoadScrollFadeTestMode()) {
    try {
      const { applyScrollFadeTestMode } = await import('./diagnostics/scrollFadeTestMode');
      applyScrollFadeTestMode();
    } catch (error) {
      console.error('Unable to load scroll-fade diagnostic mode.', error);
    }
  }

  const showReactQueryDevtools = import.meta.env.DEV && import.meta.env.MODE !== 'e2e';

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <PublicationBoundary>
          <BackendAvailabilityGate>
            <Suspense fallback={null}>
              <App />
            </Suspense>
          </BackendAvailabilityGate>
        </PublicationBoundary>
        {showReactQueryDevtools && <ReactQueryDevtools initialIsOpen={false} />}
      </QueryClientProvider>
    </StrictMode>,
  );
}

function shouldLoadScrollFadeTestMode(): boolean {
  const searchParams = new URLSearchParams(window.location.search);
  if (searchParams.has(DISABLE_SCROLL_FADE_QUERY_PARAM)) return true;

  const queryStart = window.location.hash.indexOf('?');
  if (queryStart >= 0) {
    const hashParams = new URLSearchParams(window.location.hash.slice(queryStart + 1));
    if (hashParams.has(DISABLE_SCROLL_FADE_QUERY_PARAM)) return true;
  }

  try {
    return window.localStorage.getItem(DISABLE_SCROLL_FADE_STORAGE_KEY) != null;
  } catch {
    return false;
  }
}

function renderBootstrapFailure(error: unknown): void {
  console.error('Unable to bootstrap the application.', error);
  const rootElement = document.getElementById('root');
  if (!rootElement) return;
  createRoot(rootElement).render(
    <StrictMode>
      <div role="alert">
        <p>{i18n.t('error.unexpectedCrash')}</p>
        <button type="button" onClick={() => window.location.reload()}>
          {i18n.t('common.reload')}
        </button>
      </div>
    </StrictMode>,
  );
}

void bootstrap().catch(renderBootstrapFailure);
