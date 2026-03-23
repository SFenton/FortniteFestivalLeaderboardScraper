import { Component, type ReactNode } from 'react';
import css from './ErrorBoundary.module.css';

interface Props {
  children: ReactNode;
  fallback?: ReactNode;
}

interface State {
  hasError: boolean;
}

/**
 * Root-level error boundary that catches unhandled render errors
 * and displays a recovery UI instead of a white screen.
 */
export default class ErrorBoundary extends Component<Props, State> {
  state: State = { hasError: false };

  static getDerivedStateFromError(): State {
    return { hasError: true };
  }

  /* v8 ignore start */
  private handleReload = () => {
    window.location.reload();
  /* v8 ignore stop */
  };

  render() {
    if (this.state.hasError) {
      if (this.props.fallback) return this.props.fallback;
      return (
        <div className={css.container}>
          <h1 className={css.title}>Something went wrong</h1>
          <p className={css.message}>An unexpected error occurred. Try reloading the page.</p>
          <button onClick={this.handleReload} className={css.reloadBtn}>
            Reload
          </button>
        </div>
      );
    }
    return this.props.children;
  }
}
