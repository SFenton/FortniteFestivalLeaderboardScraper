/**
 * Loading fallback shown while lazy-loaded route chunks are being fetched.
 * Displays the shared arc spinner centered in the viewport.
 */
import ArcSpinner from './ArcSpinner';
import { pageCss } from '../../pages/Page';

export default function SuspenseFallback() {
  return (
    <div style={pageCss.spinnerOverlay}>
      <ArcSpinner />
    </div>
  );
}
