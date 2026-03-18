/**
 * Loading fallback shown while lazy-loaded route chunks are being fetched.
 * Displays the shared arc spinner centered in the viewport.
 */
import ArcSpinner from './ArcSpinner';
import css from '../../pages/Page.module.css';

export default function SuspenseFallback() {
  return (
    <div className={css.spinnerOverlay}>
      <ArcSpinner />
    </div>
  );
}
