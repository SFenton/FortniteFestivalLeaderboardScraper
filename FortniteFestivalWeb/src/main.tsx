import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import PwaIconCapture from './components/icons/PwaIconCapture';
import BackendAvailabilityGate from './components/maintenance/BackendAvailabilityGate';
import { applyScrollFadeTestMode } from './diagnostics/scrollFadeTestMode';
import './i18n';
import './index.css';

applyScrollFadeTestMode();

const searchParams = new URLSearchParams(window.location.search);
const Root = searchParams.has('pwaIconCapture') ? PwaIconCapture : App;

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BackendAvailabilityGate>
      <Root />
    </BackendAvailabilityGate>
  </StrictMode>,
);
