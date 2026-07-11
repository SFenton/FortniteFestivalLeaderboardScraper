import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
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

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BackendAvailabilityGate>
      <Root />
    </BackendAvailabilityGate>
  </StrictMode>,
);
