import { Component, type ReactNode, type CSSProperties } from 'react';
import i18next from 'i18next';
import {
  Colors, Font, Gap, Radius, Cursor, CssValue,
  flexColumn, padding,
} from '@festival/theme';

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
 *
 * Uses i18next.t() directly (not the hook) since this is a class component.
 * If i18n itself has crashed, the keys render as-is — still legible.
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
        <div style={styles.container}>
          <h1 style={styles.title}>{i18next.t('common.error')}</h1>
          <p style={styles.message}>{i18next.t('error.unexpectedCrash')}</p>
          <button onClick={this.handleReload} style={styles.reloadBtn}>
            {i18next.t('common.reload')}
          </button>
        </div>
      );
    }
    return this.props.children;
  }
}

const styles: Record<string, CSSProperties> = {
  container: {
    ...flexColumn,
    alignItems: 'center',
    justifyContent: 'center',
    height: CssValue.viewportFull,
    color: Colors.textPrimary,
    textAlign: 'center',
    padding: Gap.section,
    gap: Gap.xl,
  },
  title: {
    fontSize: Font['2xl'],
    margin: Gap.none,
  },
  message: {
    fontSize: Font.md,
    color: Colors.textSecondary,
    margin: Gap.none,
  },
  reloadBtn: {
    padding: padding(Gap.lg, Gap.section),
    borderRadius: Radius.xs,
    border: CssValue.none,
    background: Colors.accentPurple,
    color: Colors.textPrimary,
    fontSize: Font.md,
    cursor: Cursor.pointer,
  },
};
