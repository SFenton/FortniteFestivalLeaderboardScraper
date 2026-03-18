import { Component, type ReactNode } from 'react';

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
        <div style={{
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          justifyContent: 'center',
          height: '100vh',
          color: '#fff',
          textAlign: 'center',
          padding: 24,
          gap: 16,
        }}>
          <h1 style={{ fontSize: 24, margin: 0 }}>Something went wrong</h1>
          <p style={{ fontSize: 14, opacity: 0.7, margin: 0 }}>An unexpected error occurred. Try reloading the page.</p>
          <button
            onClick={this.handleReload}
            style={{
              padding: '10px 24px',
              borderRadius: 8,
              border: 'none',
              background: '#7c3aed',
              color: '#fff',
              fontSize: 14,
              cursor: 'pointer',
            }}
          >
            Reload
          </button>
        </div>
      );
    }
    return this.props.children;
  }
}
