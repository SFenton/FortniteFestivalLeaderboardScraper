import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import App from './App';
import { queryClient } from './api/queryClient';
import PwaIconCapture from './components/icons/PwaIconCapture';
import BackendAvailabilityGate from './components/maintenance/BackendAvailabilityGate';
import ModalAccessibilityFixture from './diagnostics/ModalAccessibilityFixture';
import { applyScrollFadeTestMode } from './diagnostics/scrollFadeTestMode';
import { installStaleChunkRecovery } from './utils/staleChunkRecovery';
import './i18n';
import './index.css';

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
      <BackendAvailabilityGate>
        <Root />
      </BackendAvailabilityGate>
      {showReactQueryDevtools && <ReactQueryDevtools initialIsOpen={false} />}
    </QueryClientProvider>
  </StrictMode>,
);
