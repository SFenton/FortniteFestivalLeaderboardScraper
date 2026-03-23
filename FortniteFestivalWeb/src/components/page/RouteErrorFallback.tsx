/**
 * Error fallback shown when a lazy-loaded route crashes.
 * Provides a "Go to Songs" link and a "Reload" button.
 */
import css from './RouteErrorFallback.module.css';

export default function RouteErrorFallback() {
  return (
    <div className={css.container}>
      <h2 className={css.title}>Something went wrong</h2>
      <p className={css.message}>
        An error occurred loading this page. Try going back or reloading.
      </p>
      <div className={css.actions}>
        <a href="#/songs" className={css.linkBtn}>
          Go to Songs
        </a>
        <button onClick={() => window.location.reload()} className={css.reloadBtn}>
          Reload
        </button>
      </div>
    </div>
  );
}
